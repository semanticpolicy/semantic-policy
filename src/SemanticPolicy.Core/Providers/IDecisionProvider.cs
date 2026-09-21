using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers;

/// <summary>
/// An in-process adapter for one decision provider. It answers the question it is given and returns
/// what it observed; thresholds, verdicts, and what happens on a failure belong to the policy and
/// never reach it.
/// </summary>
public interface IDecisionProvider
{
    /// <summary>The adapter's id: the name a policy binds to, unless it was registered under another.</summary>
    string Id { get; }

    /// <summary>What the provider declares it can do.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Asks the provider. A provider failure comes back as a <see cref="OutcomeStatus.Failure"/> outcome
    /// and is never thrown; exceptions are for programming errors and for cancellation.
    /// </summary>
    /// <param name="request">A request that passed <see cref="DecisionRequest.EnsureValid"/>.</param>
    /// <param name="cancellationToken">
    /// The caller's token. Cancellation surfaces as an <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>The provider's estimate, or the outcome that stood in for one.</returns>
    Task<ProviderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default);
}
