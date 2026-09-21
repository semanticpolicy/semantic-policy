namespace SemanticPolicy;

/// <summary>
/// A policy's declaration of what happens when a provider does not decide. It is mandatory on every
/// policy; a <see cref="FailureAction.Fallback"/> also names the verdict for an exhausted chain, and
/// the other actions name nothing more.
/// </summary>
/// <param name="Action">What the policy does on a failure or an abstention.</param>
/// <param name="Then">
/// For <see cref="FailureAction.Fallback"/>, the verdict when no binding is left: Allow, Deny or Escalate.
/// Absent for every other action.
/// </param>
public sealed record FailureBehavior(FailureAction Action, Verdict? Then = null)
{
    /// <summary>Proceed as if the check had passed; the failure is recorded.</summary>
    public static FailureBehavior Allow { get; } = new(FailureAction.Allow);

    /// <summary>Conclude Deny; the failure is recorded as the reason.</summary>
    public static FailureBehavior Deny { get; } = new(FailureAction.Deny);

    /// <summary>Hand the decision to something outside the runtime.</summary>
    public static FailureBehavior Escalate { get; } = new(FailureAction.Escalate);

    /// <summary>
    /// Evaluate the failed rules on the next binding, and conclude <paramref name="then"/> once no binding
    /// is left.
    /// </summary>
    /// <param name="then">The verdict for an exhausted chain: Allow, Deny or Escalate.</param>
    public static FailureBehavior Fallback(Verdict then) => new(FailureAction.Fallback, then);
}
