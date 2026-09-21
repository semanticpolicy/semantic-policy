using System.Diagnostics;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy;

/// <summary>
/// The live path of <see cref="IPolicyEvaluator"/>: the loop that drives <see cref="PolicyEvaluation.Evaluate"/>
/// against real providers. Each round it builds one request per attempt the step function requires,
/// dispatches them all at once, adds the results and asks again, until there is a verdict. It holds the
/// providers by registration name and the policies by id, both fixed at construction, and it is safe
/// to share: one evaluation carries no state past its own call.
/// </summary>
public sealed class PolicyEvaluator : IPolicyEvaluator
{
    private readonly Dictionary<string, IDecisionProvider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Policy> _policies = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the evaluator over the providers and the policies it will serve. Every registered policy
    /// is validated here, and checked against the providers it names, so a policy that can never work
    /// fails at start-up rather than on its first request.
    /// </summary>
    /// <param name="providers">The providers, each under the name a policy binds to.</param>
    /// <param name="policies">The policies reachable by id.</param>
    /// <exception cref="PolicyConfigurationException">
    /// Two providers are registered under one name; two policies share an id; a policy cannot be
    /// evaluated as written, or names a provider that is not registered or does not declare a decision
    /// type or evidence kind the policy relies on.
    /// </exception>
    public PolicyEvaluator(IEnumerable<ProviderRegistration> providers, IEnumerable<Policy> policies)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(policies);
        foreach (ProviderRegistration registration in providers)
        {
            if (registration is null)
            {
                throw new ArgumentException("A provider registration is null.", nameof(providers));
            }

            if (string.IsNullOrWhiteSpace(registration.Name))
            {
                throw new PolicyConfigurationException("A provider is registered under an empty name.", policyId: null);
            }

            if (!_providers.TryAdd(registration.Name, registration.Provider))
            {
                throw new PolicyConfigurationException(
                    $"Provider '{registration.Name}' is registered twice.",
                    policyId: null,
                    providerId: registration.Name);
            }
        }

        foreach (Policy policy in policies)
        {
            if (policy is null)
            {
                throw new ArgumentException("A policy is null.", nameof(policies));
            }

            Check(policy);
            if (!_policies.TryAdd(policy.Id, policy))
            {
                throw new PolicyConfigurationException($"Policy '{policy.Id}' is registered twice.", policy.Id);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<PolicyVerdict> EvaluateAsync(
        Policy policy,
        SemanticContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(context);
        Check(policy);
        return await RunAsync(policy, context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<PolicyVerdict> EvaluateAsync(
        string policyId,
        SemanticContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policyId);
        ArgumentNullException.ThrowIfNull(context);
        if (!_policies.TryGetValue(policyId, out Policy? policy))
        {
            throw new ArgumentException($"No policy is registered under '{policyId}'.", nameof(policyId));
        }

        return await RunAsync(policy, context, cancellationToken).ConfigureAwait(false);
    }

    // What Validate() cannot see, because it reads the policy alone: whether every provider the chain
    // names is registered, answers every rule's decision type, and produces the evidence kind each
    // threshold and gate reads. Capabilities are in-process, so this costs no call.
    private void Check(Policy policy)
    {
        policy.Validate();
        foreach (ProviderBinding binding in policy.Bindings)
        {
            string providerId = binding.ProviderId;
            if (!_providers.TryGetValue(providerId, out IDecisionProvider? provider))
            {
                throw PolicyConfigurationException.For(
                    policy.Id,
                    "the provider is not registered.",
                    providerId: providerId);
            }

            ProviderCapabilities capabilities = provider.Capabilities;
            foreach (Rule rule in policy.Rules)
            {
                if (!capabilities.Types.Contains(rule.Type))
                {
                    throw PolicyConfigurationException.For(
                        policy.Id,
                        $"the provider does not answer {rule.Type} questions.",
                        rule.Id,
                        providerId);
                }
            }

            foreach (RuleOperatingPoint point in binding.OperatingPoints)
            {
                foreach (Threshold threshold in point.Thresholds)
                {
                    if (!capabilities.Evidence.Contains(threshold.Kind))
                    {
                        throw PolicyConfigurationException.For(
                            policy.Id,
                            $"the provider does not produce {threshold.Kind} evidence, which a threshold reads.",
                            point.RuleId,
                            providerId);
                    }
                }

                if (point.Gate is { } gate && !capabilities.Evidence.Contains(gate.Kind))
                {
                    throw PolicyConfigurationException.For(
                        policy.Id,
                        $"the provider does not produce {gate.Kind} evidence, which the margin gate reads.",
                        point.RuleId,
                        providerId);
                }
            }
        }
    }

    private async Task<PolicyVerdict> RunAsync(Policy policy, SemanticContext context, CancellationToken callerToken)
    {
        using CancellationTokenSource? budget = StartBudget(policy, callerToken);
        CancellationToken budgetToken = budget?.Token ?? callerToken;

        Dictionary<AttemptKey, ProviderResult> attempts = [];
        EvaluationStep step = PolicyEvaluation.Evaluate(policy, attempts);
        while (!step.IsComplete)
        {
            callerToken.ThrowIfCancellationRequested();
            (AttemptKey Key, Task<ProviderResult> Result)[] round =
                Dispatch(policy, context, step.Required, budgetToken, callerToken);

            // WhenAll completes only once every attempt has, so a provider that faults or is cancelled never
            // leaves a sibling's call unobserved; the first exception then surfaces as itself.
            await Task.WhenAll(round.Select(attempt => attempt.Result)).ConfigureAwait(false);
            foreach ((AttemptKey key, Task<ProviderResult> result) in round)
            {
                attempts[key] = result.Result;
            }

            step = PolicyEvaluation.Evaluate(policy, attempts);
        }

        return step.Verdict!;
    }

    // The budget is a token linked to the caller's, cancelled after the policy's span, and that token is
    // its only authority: a provider is given it and expected to honour it. Without a budget there is no
    // second token and nothing times out.
    private static CancellationTokenSource? StartBudget(Policy policy, CancellationToken callerToken)
    {
        if (policy.Budget is not { } span)
        {
            return null;
        }

        CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        budget.CancelAfter(span);
        return budget;
    }

    // Every request of the round is built and checked before the first provider is called, so a request
    // the protocol rejects stops the round with no provider asked, not one provider asked and one not.
    private (AttemptKey Key, Task<ProviderResult> Result)[] Dispatch(
        Policy policy,
        SemanticContext context,
        IReadOnlyList<AttemptKey> required,
        CancellationToken budgetToken,
        CancellationToken callerToken)
    {
        var prepared = new (Rule Rule, ProviderBinding Binding, DecisionRequest Request)[required.Count];
        for (int i = 0; i < required.Count; i++)
        {
            AttemptKey key = required[i];
            Rule rule = policy.Rules.First(
                candidate => string.Equals(candidate.Id, key.RuleId, StringComparison.Ordinal));
            ProviderBinding binding = policy.Bindings[key.BindingIndex];
            DecisionRequest request = rule.CreateRequest(context);
            request.EnsureValid();
            prepared[i] = (rule, binding, request);
        }

        var round = new (AttemptKey Key, Task<ProviderResult> Result)[required.Count];
        for (int i = 0; i < required.Count; i++)
        {
            (Rule rule, ProviderBinding binding, DecisionRequest request) = prepared[i];
            IDecisionProvider provider = _providers[binding.ProviderId];
            round[i] = (required[i], AttemptAsync(provider, rule, binding, request, budgetToken, callerToken));
        }

        return round;
    }

    private static async Task<ProviderResult> AttemptAsync(
        IDecisionProvider provider,
        Rule rule,
        ProviderBinding binding,
        DecisionRequest request,
        CancellationToken budgetToken,
        CancellationToken callerToken)
    {
        // An attempt the step requires once the budget has already expired — a fallback binding after the
        // primary timed out — is not made at all: the provider would only be handed a cancelled token.
        if (BudgetExpired(budgetToken, callerToken))
        {
            return Expired(
                rule.Type,
                binding.ProviderId,
                latencyMs: 0,
                "the budget expired before the attempt was made.");
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            return await provider.DecideAsync(request, budgetToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (BudgetExpired(budgetToken, callerToken))
        {
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return Expired(rule.Type, binding.ProviderId, elapsed, "the budget expired before the provider answered.");
        }
    }

    // Expiry is the budget's token cancelled while the caller's is not; the linked token cannot tell the
    // two apart on its own. The filter is deliberately this narrow: an OperationCanceledException an
    // adapter lets escape with neither token cancelled — HttpClient's own timeout surfaces as one — is a
    // programming error that propagates, never a third way to synthesize a Timeout.
    private static bool BudgetExpired(CancellationToken budgetToken, CancellationToken callerToken) =>
        budgetToken.IsCancellationRequested && !callerToken.IsCancellationRequested;

    // The model is null because no adapter answered; the provider id is the binding's, which is the
    // registration name, so the trace reads the same as for an answered attempt.
    private static ProviderResult Expired(DecisionType type, string providerId, double latencyMs, string message) =>
        ProviderResult.Failed(
            type,
            FailureKind.Timeout,
            message,
            new ProviderMetadata(providerId, Model: null, latencyMs));
}
