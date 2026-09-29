using System.Text.Json.Serialization;
using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// One rung of a Boolean ladder made concrete on one provider: the verdict is reached when the flagged
/// answer's evidence of this kind, or at a calibrated operating point its calibrated probability, is at or
/// above the value. The number belongs to the triple of policy, provider and the dataset it was measured
/// on; it is not portable to another provider without a new measurement.
/// </summary>
/// <param name="Verdict">The ladder rung this threshold reaches.</param>
/// <param name="Kind">
/// The evidence the value is compared against, the same for every threshold of an operating point. At a
/// calibrated operating point it is <see cref="EvidenceKind.Probability"/> and the value is compared against
/// the calibrated probability, while the gate stays on the calibration's source kind.
/// </param>
/// <param name="AtOrAbove">
/// The value, inclusive: a finite number, in [0, 1] for <see cref="EvidenceKind.Probability"/>.
/// </param>
public sealed record Threshold(
    [property: JsonRequired] Verdict Verdict,
    [property: JsonRequired] EvidenceKind Kind,
    [property: JsonRequired] double AtOrAbove);
