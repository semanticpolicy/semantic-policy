namespace SemanticPolicy;

/// <summary>
/// One step of a Score rule's map: the verdict reached once the provider's level is this one or higher.
/// </summary>
/// <param name="Level">A level of the rule, from which the verdict applies.</param>
/// <param name="Verdict">Warn, Escalate or Deny.</param>
public sealed record ScoreRung(string Level, Verdict Verdict)
{
    /// <summary>A level of the rule, from which the verdict applies.</summary>
    public string Level { get; init; } = Level ?? throw new ArgumentNullException(nameof(Level));
}
