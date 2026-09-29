using System.Text.Json.Serialization;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evaluation;

/// <summary>
/// One provider call in a rule's trace: the provider's result as it was returned, what evaluation
/// made of it, and what happened next. The result is never rewritten — a success that broke the
/// contract keeps its result and gets a synthesized <see cref="EffectiveOutcome"/> beside it, and a
/// success read at a calibrated operating point keeps its result and gets the <see cref="CalibratedEvidence"/>
/// the thresholds read beside it — so a stored trace replays against another policy exactly as the provider
/// answered.
/// </summary>
/// <param name="BindingIndex">The binding's position in the policy's chain, zero-based.</param>
/// <param name="ProviderId">The binding's provider.</param>
/// <param name="Result">The provider's result, untouched.</param>
/// <param name="EffectiveOutcome">
/// The outcome evaluation acted on: the result's own, or <c>Failure(Malformed)</c> when a success did
/// not honour the contract.
/// </param>
/// <param name="Margin">
/// The gap between the top answer and the runner-up on the gate's evidence kind, when a gate applied
/// to this attempt; otherwise <see langword="null"/>. At a calibrated operating point it is the provider's
/// own margin, before calibration.
/// </param>
/// <param name="Disposition">Whether the attempt decided the rule, and if not, what moved the chain.</param>
/// <param name="CalibratedEvidence">
/// On a success read at a calibrated operating point — whether it decided the rule or a gate moved the chain
/// past it — the probability evidence the calibration made of the provider's: the calibrated value under the
/// flagged answer's key and its complement under the other, with no scale. It is an estimate fitted on
/// labelled data and can be wrong on inputs unlike that data. <see langword="null"/> on every other attempt.
/// </param>
/// <param name="CalibrationModelMismatch">
/// Whether the result names a model other than the one the calibration records it was fitted on, compared
/// ordinally. Only an attempt with <see cref="CalibratedEvidence"/> is marked, and only when both models are
/// named. The mark changes nothing about the verdict; it says the calibration may not fit this model.
/// </param>
public sealed record Attempt(
    int BindingIndex,
    string ProviderId,
    ProviderResult Result,
    ProviderOutcome EffectiveOutcome,
    double? Margin,
    AttemptDisposition Disposition,
    Evidence? CalibratedEvidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool CalibrationModelMismatch = false);
