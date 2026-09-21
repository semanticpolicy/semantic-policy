namespace SemanticPolicy.Protocol;

/// <summary>
/// What happened to a provider call. A failure or an abstention is carried as such until the policy's
/// declared failure behaviour maps it to a verdict; it is never read as an answer.
/// </summary>
/// <param name="Status">Whether the call produced an answer, was declined, or failed.</param>
/// <param name="Kind">On a failure, why; otherwise absent.</param>
/// <param name="Message">A note for the log. It is never content from the context.</param>
public sealed record ProviderOutcome(OutcomeStatus Status, FailureKind? Kind = null, string? Message = null)
{
    /// <summary>The call produced an answer.</summary>
    public static ProviderOutcome Success { get; } = new(OutcomeStatus.Success);

    /// <summary>The provider declined this input.</summary>
    /// <param name="message">A note for the log, or <see langword="null"/> when the provider gave none.</param>
    public static ProviderOutcome Abstain(string? message) => new(OutcomeStatus.Abstain, Message: message);

    /// <summary>The call failed.</summary>
    /// <param name="kind">Why.</param>
    /// <param name="message">A note for the log. It is never content from the context.</param>
    public static ProviderOutcome Failure(FailureKind kind, string message) =>
        new(OutcomeStatus.Failure, kind, message);
}
