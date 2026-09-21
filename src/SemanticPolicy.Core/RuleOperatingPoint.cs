namespace SemanticPolicy;

/// <summary>
/// The numbers for one rule on one provider: a threshold per ladder rung for a Boolean rule, none for a
/// Choice or Score rule, and an optional margin gate for any. Every threshold and the gate read one
/// evidence kind.
/// </summary>
/// <param name="RuleId">The rule this operating point is for.</param>
/// <param name="Thresholds">
/// For a Boolean rule, exactly one threshold per rung of its ladder, increasing with severity; empty
/// for a Choice or Score rule.
/// </param>
/// <param name="Gate">The margin gate, or <see langword="null"/> for no gate.</param>
public sealed record RuleOperatingPoint(string RuleId, IReadOnlyList<Threshold> Thresholds, MarginGate? Gate = null)
{
    /// <summary>The rule this operating point is for.</summary>
    public string RuleId { get; init; } = RuleId ?? throw new ArgumentNullException(nameof(RuleId));

    /// <summary>
    /// For a Boolean rule, exactly one threshold per rung of its ladder, increasing with severity; empty
    /// for a Choice or Score rule.
    /// </summary>
    public IReadOnlyList<Threshold> Thresholds { get; init; } =
        Thresholds ?? throw new ArgumentNullException(nameof(Thresholds));
}
