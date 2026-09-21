using System.Text.Json.Serialization;
using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// One rung of a Boolean ladder made concrete on one provider: the verdict is reached when the flagged
/// answer's evidence of this kind is at or above the value. The number belongs to the triple of policy,
/// provider and the dataset it was measured on; it is not portable to another provider without a new
/// measurement.
/// </summary>
/// <param name="Verdict">The ladder rung this threshold reaches.</param>
/// <param name="Kind">The evidence the value is compared against; one kind per operating point.</param>
/// <param name="AtOrAbove">
/// The value, inclusive: a finite number, in [0, 1] for <see cref="EvidenceKind.Probability"/>.
/// </param>
public sealed record Threshold(
    [property: JsonRequired] Verdict Verdict,
    [property: JsonRequired] EvidenceKind Kind,
    [property: JsonRequired] double AtOrAbove);
