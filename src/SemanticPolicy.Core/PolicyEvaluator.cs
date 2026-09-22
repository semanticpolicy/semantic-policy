using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;
using SemanticPolicy.Telemetry;
using static SemanticPolicy.Telemetry.SemanticPolicyTelemetry;

namespace SemanticPolicy;

/// <summary>
/// The live path of <see cref="IPolicyEvaluator"/>: the loop that drives <see cref="PolicyEvaluation.Evaluate"/>
/// against real providers. Each round it builds one request per attempt the step function requires,
/// dispatches them all at once, adds the results and asks again, until there is a verdict. It holds the
/// providers by registration name and the policies by id, both fixed at construction, and it is safe
/// to share: one evaluation carries no state past its own call. It is also where every span and every
/// measurement comes from — an activity per evaluation with a child per attempt, under
/// <see cref="SemanticPolicyTelemetry.ActivitySourceName"/> and <see cref="SemanticPolicyTelemetry.MeterName"/> —
/// tagged with identifiers, numbers and the policy's vocabulary and never with the content it judged.
/// </summary>
public sealed class PolicyEvaluator : IPolicyEvaluator
{
    // Adapters create no spans of their own: every span comes from here, so the field list is written
    // once. Static, so a second evaluator — or a second container — adds no second instrument.
    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);
    private static readonly Counter<long> _evaluations = _meter.CreateCounter<long>(
        EvaluationsInstrument,
        unit: "{evaluation}",
        description: "Evaluations that reached a verdict.");
    private static readonly Counter<long> _attempts = _meter.CreateCounter<long>(
        AttemptsInstrument,
        unit: "{attempt}",
        description: "Provider attempts, as evaluation acted on them.");
    private static readonly Histogram<double> _duration = _meter.CreateHistogram<double>(
        EvaluationDurationInstrument,
        unit: "s",
        description: "The duration of an evaluation.");

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
        long started = Stopwatch.GetTimestamp();
        using Activity? evaluation = _activitySource.StartActivity(EvaluateActivity);
        if (evaluation is not null)
        {
            evaluation.SetTag(PolicyIdTag, policy.Id);
            evaluation.SetTag(PolicyModeTag, Name(policy.Mode));
            if (context.CorrelationId is { } correlationId)
            {
                evaluation.SetTag(CorrelationIdTag, correlationId);
            }
        }

        // An attempt's span stays open until the verdict exists, because what the attempt did to the
        // chain — the margin, the rung, whether and where the chain moved on — is only known then. Its end
        // time was set when the provider answered, so its duration is still the call's; if the evaluation
        // throws instead, the spans are stopped as they are.
        Dictionary<AttemptKey, Activity> spans = [];
        try
        {
            using CancellationTokenSource? budget = StartBudget(policy, callerToken);
            CancellationToken budgetToken = budget?.Token ?? callerToken;

            Dictionary<AttemptKey, ProviderResult> attempts = [];
            EvaluationStep step = PolicyEvaluation.Evaluate(policy, attempts);
            while (!step.IsComplete)
            {
                callerToken.ThrowIfCancellationRequested();
                (AttemptKey Key, Task<Answered> Answer)[] round =
                    Dispatch(policy, context, step.Required, budgetToken, callerToken);

                // WhenAll completes only once every attempt has, so a provider that faults or is cancelled never
                // leaves a sibling's call unobserved; the first exception then surfaces as itself. A sibling that
                // answered before the failure has a span nobody else will stop: it joins the others, so that
                // the finally below stops it, before the exception goes on.
                try
                {
                    await Task.WhenAll(round.Select(attempt => attempt.Answer)).ConfigureAwait(false);
                }
                catch
                {
                    foreach ((AttemptKey key, Task<Answered> answered) in round)
                    {
                        if (answered.IsCompletedSuccessfully && answered.Result.Span is { } span)
                        {
                            spans[key] = span;
                        }
                    }

                    throw;
                }

                foreach ((AttemptKey key, Task<Answered> answered) in round)
                {
                    (ProviderResult result, Activity? span) = answered.Result;
                    attempts[key] = result;
                    if (span is not null)
                    {
                        spans[key] = span;
                    }
                }

                step = PolicyEvaluation.Evaluate(policy, attempts);
            }

            PolicyVerdict verdict = step.Verdict!;
            Report(policy, verdict, spans, evaluation, Stopwatch.GetElapsedTime(started));
            return verdict;
        }
        finally
        {
            foreach (Activity span in spans.Values)
            {
                span.Dispose();
            }
        }
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
    private (AttemptKey Key, Task<Answered> Answer)[] Dispatch(
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

        var round = new (AttemptKey Key, Task<Answered> Answer)[required.Count];
        for (int i = 0; i < required.Count; i++)
        {
            (Rule rule, ProviderBinding binding, DecisionRequest request) = prepared[i];
            IDecisionProvider provider = _providers[binding.ProviderId];
            round[i] = (required[i], AttemptAsync(provider, rule, binding, request, budgetToken, callerToken));
        }

        return round;
    }

    // The span is the provider call: it is the ambient activity the adapter sees, so an adapter that
    // traces its own I/O nests under it. What it carries here is known before the call; what the attempt
    // did to the chain is tagged by the caller from the trace, once there is one.
    private static async Task<Answered> AttemptAsync(
        IDecisionProvider provider,
        Rule rule,
        ProviderBinding binding,
        DecisionRequest request,
        CancellationToken budgetToken,
        CancellationToken callerToken)
    {
        Activity? span = _activitySource.StartActivity(AttemptActivity);
        if (span is not null)
        {
            span.SetTag(RuleIdTag, rule.Id);
            span.SetTag(ProviderIdTag, binding.ProviderId);
            span.SetTag(DecisionTypeTag, Name(rule.Type));
        }

        try
        {
            ProviderResult result = await CallAsync(provider, rule, binding, request, budgetToken, callerToken)
                .ConfigureAwait(false);
            span?.SetEndTime(DateTime.UtcNow);
            return new Answered(result, span);
        }
        catch
        {
            span?.Dispose();
            throw;
        }
    }

    private static async Task<ProviderResult> CallAsync(
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

    // Everything that says what an attempt did to the chain is read off the trace, never off the request
    // or the raw response, so the tags cannot carry content the trace does not. Each attempt's span is
    // tagged and stopped here, before the parent, which is the moment an exporter reads it.
    private static void Report(
        Policy policy,
        PolicyVerdict verdict,
        Dictionary<AttemptKey, Activity> spans,
        Activity? evaluation,
        TimeSpan elapsed)
    {
        bool measuring = _evaluations.Enabled || _attempts.Enabled || _duration.Enabled;
        if (evaluation is null && spans.Count == 0 && !measuring)
        {
            return;
        }

        if (evaluation is not null)
        {
            evaluation.SetTag(EffectiveVerdictTag, Name(verdict.Effective));
            evaluation.SetTag(EvaluatedVerdictTag, Name(verdict.Evaluated));
        }

        foreach (RuleVerdict rule in verdict.Rules)
        {
            Rule definition = policy.Rules.First(
                candidate => string.Equals(candidate.Id, rule.RuleId, StringComparison.Ordinal));
            foreach (Attempt attempt in rule.Attempts)
            {
                if (spans.TryGetValue(new AttemptKey(rule.RuleId, attempt.BindingIndex), out Activity? span))
                {
                    Tag(span, policy, rule, attempt);
                    span.Dispose();
                }

                if (_attempts.Enabled)
                {
                    _attempts.Add(1, AttemptTags(definition, attempt));
                }
            }
        }

        if (_evaluations.Enabled)
        {
            _evaluations.Add(
                1,
                new KeyValuePair<string, object?>(PolicyIdTag, policy.Id),
                new KeyValuePair<string, object?>(PolicyModeTag, Name(policy.Mode)),
                new KeyValuePair<string, object?>(EvaluatedVerdictTag, Name(verdict.Evaluated)));
        }

        if (_duration.Enabled)
        {
            _duration.Record(
                elapsed.TotalSeconds,
                new KeyValuePair<string, object?>(PolicyIdTag, policy.Id),
                new KeyValuePair<string, object?>(PolicyModeTag, Name(policy.Mode)));
        }
    }

    private static void Tag(Activity span, Policy policy, RuleVerdict rule, Attempt attempt)
    {
        if (attempt.Result.Provider.Model is { } model)
        {
            span.SetTag(ProviderModelTag, model);
        }

        ProviderOutcome outcome = attempt.EffectiveOutcome;
        span.SetTag(OutcomeStatusTag, Name(outcome.Status));
        if (outcome.Kind is { } kind)
        {
            span.SetTag(OutcomeFailureKindTag, Name(kind));
        }

        // The reading behind a decided rule belongs to the attempt that decided it; the margin belongs to
        // every attempt a gate was applied to, whether or not it passed.
        if (attempt.BindingIndex == rule.DecidingBinding)
        {
            if (rule.EvidenceKind is { } evidenceKind)
            {
                span.SetTag(EvidenceKindTag, Name(evidenceKind));
            }

            if (rule.EvidenceValue is { } evidenceValue)
            {
                span.SetTag(EvidenceValueTag, evidenceValue);
            }

            if (rule.RungCrossed is { } rung)
            {
                span.SetTag(ThresholdCrossedTag, Name(rung));
            }
        }

        if (attempt.Margin is { } margin)
        {
            span.SetTag(MarginTag, margin);
        }

        string? movedBy = attempt.Disposition switch
        {
            AttemptDisposition.MovedOnByGate => "gate",
            AttemptDisposition.MovedOnByFailure => "failure",
            _ => null,
        };
        if (movedBy is not null)
        {
            span.SetTag(ChainMovedByTag, movedBy);
            span.SetTag(FallbackToTag, policy.Bindings[attempt.BindingIndex + 1].ProviderId);
        }
    }

    private static TagList AttemptTags(Rule rule, Attempt attempt)
    {
        TagList tags = new()
        {
            { ProviderIdTag, attempt.ProviderId },
            { DecisionTypeTag, Name(rule.Type) },
            { OutcomeStatusTag, Name(attempt.EffectiveOutcome.Status) },
        };
        if (attempt.EffectiveOutcome.Kind is { } kind)
        {
            tags.Add(OutcomeFailureKindTag, Name(kind));
        }

        return tags;
    }

    // A tag value from the policy's vocabulary is spelled as the protocol writes it on the wire —
    // camelCase — so a stored result and a dashboard read alike. Only called once a listener exists.
    private static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    // What one attempt came back with: the provider's result and the span that timed the call, still
    // open so the trace can tag it, or null when nothing listens.
    private readonly record struct Answered(ProviderResult Result, Activity? Span);
}
