namespace SemanticPolicy;

/// <summary>
/// The numbers for one rule on one provider: a threshold per ladder rung for a Boolean rule, none for a
/// Choice or Score rule, an optional margin gate for any, and an optional calibration for a Boolean rule.
/// Without a calibration, every threshold and the gate read one evidence kind. With one, the thresholds read
/// the calibrated probability, so they are all <see cref="Protocol.EvidenceKind.Probability"/>, and the gate
/// reads the margin of the calibration's source kind as the provider returned it.
/// </summary>
/// <param name="RuleId">The rule this operating point is for.</param>
/// <param name="Thresholds">
/// For a Boolean rule, exactly one threshold per rung of its ladder, increasing with severity; empty
/// for a Choice or Score rule.
/// </param>
/// <param name="Gate">The margin gate, or <see langword="null"/> for no gate.</param>
/// <param name="Calibration">
/// The map from the provider's evidence to the probability the thresholds read, or <see langword="null"/> for
/// none, when the thresholds read the provider's evidence as it is. Only a Boolean rule's operating point may
/// carry one.
/// </param>
public sealed record RuleOperatingPoint(
    string RuleId,
    IReadOnlyList<Threshold> Thresholds,
    MarginGate? Gate = null,
    EvidenceCalibration? Calibration = null)
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
