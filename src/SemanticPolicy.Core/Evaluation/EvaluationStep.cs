namespace SemanticPolicy.Evaluation;

/// <summary>
/// One turn of the step function: either the policy's verdict, or the attempts still needed before
/// there can be one. The live evaluator loops — gather what is required, call again — and an offline
/// run that holds a complete set of results gets the verdict at the first call.
/// </summary>
/// <param name="Verdict">The verdict, once every rule has one; otherwise <see langword="null"/>.</param>
/// <param name="Required">
/// The attempts still missing, one per undecided rule in policy order — never one already held.
/// Empty once there is a verdict.
/// </param>
public sealed record EvaluationStep(PolicyVerdict? Verdict, IReadOnlyList<AttemptKey> Required)
{
    /// <summary>Whether the step carries a verdict.</summary>
    public bool IsComplete => Verdict is not null;
}
