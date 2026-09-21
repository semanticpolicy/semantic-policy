using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evaluation;

/// <summary>
/// What one rule concluded and how. The verdict is a semantic signal about the context, not an
/// authorization; the rest of the record is what makes it explainable — which binding's answer
/// decided it, which rung of the rule's vocabulary was crossed, which evidence was read — or says
/// that no answer decided it at all. Every attempt made for the rule is kept, in chain order.
/// </summary>
/// <param name="RuleId">The rule.</param>
/// <param name="Verdict">What the rule concluded.</param>
/// <param name="Source">Whether a provider's answer decided the rule, or the policy's own declaration did.</param>
/// <param name="DecidingBinding">
/// The position of the binding whose answer decided the rule; <see langword="null"/> when the verdict
/// came from the failure behaviour or from an exhausted gate.
/// </param>
/// <param name="RungCrossed">
/// The ladder or Score rung the verdict is; <see langword="null"/> when no rung was reached, or when
/// the rule is a Choice rule or no answer decided it.
/// </param>
/// <param name="EvidenceKind">
/// The kind of evidence the ladder was read on, for a Boolean rule an answer decided.
/// </param>
/// <param name="EvidenceValue">
/// The flagged answer's evidence of that kind, after a one-sided probability was completed, for a
/// Boolean rule an answer decided.
/// </param>
/// <param name="Attempts">Every attempt made for the rule, in chain order.</param>
public sealed record RuleVerdict(
    string RuleId,
    Verdict Verdict,
    VerdictSource Source,
    int? DecidingBinding,
    Verdict? RungCrossed,
    EvidenceKind? EvidenceKind,
    double? EvidenceValue,
    IReadOnlyList<Attempt> Attempts);
