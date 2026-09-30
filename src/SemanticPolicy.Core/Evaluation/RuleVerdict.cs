using System.Text.Json.Serialization;
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
/// the rule is a Choice rule, whose pick is <see cref="ChosenOption"/>, or no answer decided it.
/// </param>
/// <param name="EvidenceKind">
/// The kind of evidence the ladder was read on, for a Boolean rule an answer decided. At a calibrated
/// operating point it is Probability, the kind the calibration produces, whatever kind the provider returned.
/// </param>
/// <param name="EvidenceValue">
/// The flagged answer's evidence of that kind, after a one-sided probability was completed, for a
/// Boolean rule an answer decided. At a calibrated operating point it is the calibrated probability, an
/// estimate fitted on labelled data, and the provider's own value stays in the deciding attempt's result.
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
    IReadOnlyList<Attempt> Attempts)
{
    // Both members below read the attempts, which the serialized verdict already carries, so writing them
    // would repeat an attempt's result in every stored verdict.

    /// <summary>
    /// The attempt at <see cref="DecidingBinding"/>, whose result holds the answer that decided the rule
    /// and the evidence the provider returned with it; <see langword="null"/> when no answer decided it.
    /// </summary>
    [JsonIgnore]
    public Attempt? DecidingAttempt =>
        DecidingBinding is { } index ? Attempts.FirstOrDefault(attempt => attempt.BindingIndex == index) : null;

    /// <summary>
    /// The option the deciding answer picked, for a Choice rule an answer decided; <see langword="null"/>
    /// for a Boolean or Score rule, and when no answer decided the rule — the gate abstained, or the
    /// failure behaviour set the verdict. It is the provider's pick, not a measure of how right it is: the
    /// deciding attempt's evidence carries the provider's number for every option.
    /// </summary>
    [JsonIgnore]
    public string? ChosenOption => (DecidingAttempt?.Result.Value as ChoiceValue)?.Option;
}
