using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// The uncertainty gate on one rule for one provider: when the margin between the top answer and the
/// runner-up, on this evidence kind, is below the value, the attempt moves to the next binding — or
/// yields Abstain when there is none. A flat distribution is the local model's failure mode; a margin
/// sees it where a top probability does not.
/// </summary>
/// <param name="Kind">The evidence the margin is computed on; the operating point's one kind.</param>
/// <param name="Below">The margin under which the attempt does not decide; greater than zero.</param>
public sealed record MarginGate(EvidenceKind Kind, double Below);
