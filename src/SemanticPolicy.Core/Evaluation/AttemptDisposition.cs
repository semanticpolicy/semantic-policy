namespace SemanticPolicy.Evaluation;

/// <summary>
/// What evaluation did with one attempt: whether it ended the rule, and if not, what sent the rule
/// on to the next binding. Read with the attempt's position in the rule's trace, it says how the
/// chain was walked.
/// </summary>
public enum AttemptDisposition
{
    /// <summary>The provider's answer decided the rule.</summary>
    Decided,

    /// <summary>The evidence fell under the margin gate and a later binding was tried.</summary>
    MovedOnByGate,

    /// <summary>
    /// The provider failed, abstained or broke the contract, and the fallback moved the rule to a later
    /// binding.
    /// </summary>
    MovedOnByFailure,

    /// <summary>The evidence fell under the margin gate on the last binding; the rule abstained.</summary>
    ExhaustedByGate,

    /// <summary>
    /// The provider failed, abstained or broke the contract, and the failure behaviour ended the rule —
    /// with its own verdict, or with the fallback's terminal verdict once no binding was left.
    /// </summary>
    TerminatedByFailure,
}
