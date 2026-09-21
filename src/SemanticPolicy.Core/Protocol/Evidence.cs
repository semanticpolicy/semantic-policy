namespace SemanticPolicy.Protocol;

/// <summary>
/// The numbers behind an answer, each with the kind that says what they mean. A bare number is not
/// evidence, and a threshold is only ever compared with evidence of the kind it was written for.
/// </summary>
/// <param name="Kind">What the values mean.</param>
/// <param name="Values">
/// One number per option, per level, or per <c>true</c> / <c>false</c> for a Boolean question.
/// </param>
/// <param name="Scale">
/// The provider's name for its scale, such as <c>calibrated</c> or <c>sigmoid</c>; absent when the
/// provider names none.
/// </param>
public sealed record Evidence(EvidenceKind Kind, IReadOnlyDictionary<string, double> Values, string? Scale = null);
