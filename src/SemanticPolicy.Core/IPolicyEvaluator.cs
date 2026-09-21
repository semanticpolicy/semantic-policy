using SemanticPolicy.Evaluation;

namespace SemanticPolicy;

/// <summary>
/// The runtime: asks the registered providers a policy's questions about a context and returns what
/// the policy concluded. The verdict is a semantic signal, not an authorization — acting on it is the
/// application's code. The evaluator itself decides nothing: which attempts to make and what an
/// answer means is <see cref="PolicyEvaluation.Evaluate"/>'s; the evaluator builds the requests,
/// calls the providers under the policy's budget, and hands the results back to it.
/// </summary>
public interface IPolicyEvaluator
{
    /// <summary>
    /// Evaluates a policy passed as a value. The policy is validated and checked against the registered
    /// providers' capabilities before any provider is called.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="context">The thing the policy is asked about.</param>
    /// <param name="cancellationToken">
    /// The caller's token. Cancelling it ends the evaluation with an <see cref="OperationCanceledException"/>
    /// and no verdict; the policy's own budget never does — its expiry is a provider failure.
    /// </param>
    /// <returns>What the policy concluded, with every attempt behind it.</returns>
    /// <exception cref="PolicyConfigurationException">
    /// The policy cannot be evaluated as written, or names a provider that is not registered or does not
    /// declare a decision type or evidence kind the policy relies on.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    Task<PolicyVerdict> EvaluateAsync(
        Policy policy,
        SemanticContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates a registered policy. It was validated and checked against the registered providers when
    /// the evaluator was constructed.
    /// </summary>
    /// <param name="policyId">The id the policy was registered under.</param>
    /// <param name="context">The thing the policy is asked about.</param>
    /// <param name="cancellationToken">
    /// The caller's token. Cancelling it ends the evaluation with an <see cref="OperationCanceledException"/>
    /// and no verdict; the policy's own budget never does — its expiry is a provider failure.
    /// </param>
    /// <returns>What the policy concluded, with every attempt behind it.</returns>
    /// <exception cref="ArgumentException">No policy is registered under the id.</exception>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    Task<PolicyVerdict> EvaluateAsync(
        string policyId,
        SemanticContext context,
        CancellationToken cancellationToken = default);
}
