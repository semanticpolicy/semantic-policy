# 0019. A calibration is data on a Boolean operating point, and it produces evidence the ladder reads

**Status:** Accepted
**Date:** 2026-09-29

## Context

[0003](0003-evidence-semantics.md) gave a calibration layer a defined place: it turns `Score`,
`Logit` or `Margin` evidence into `Probability` evidence.
[0005](0005-evaluation-and-threshold-ownership.md) said calibration is measured on the evaluation
side, applied on the runtime side, and optional per policy.
[0009](0009-normalization-layer-deferred-to-calibration.md) left the normalized decision of
[0004](0004-decision-policy-enforcement-separation.md) without a type until a calibration layer
produces one, defined by what calibration actually produces, and foreclosed folding calibration into
evaluation: calibration produces evidence, evaluation consumes it.

`SemanticPolicy.Core` now applies a calibration. The questions were where a calibration lives, which
evidence it reads, what the thresholds and the margin gate read once it is there, what the trace
keeps, and what 0009's promised type turns out to be. Fitting a calibration is the evaluation tool's
job and outside this record.

## Decision

**A calibration is data on a Boolean rule's operating point. Core's step function applies it before
the thresholds: it produces `Probability` evidence, and the ladder reads that evidence as it reads a
provider's.**

1. **Platt scaling only, on a Boolean rule only.** `RuleOperatingPoint.Calibration` holds an
   `EvidenceCalibration`: the method, the source kind, the input transform, `Slope`, `Intercept` and
   an optional provenance. The map is p = σ(`Slope` · x + `Intercept`). Only the Boolean ladder reads
   a probability today, so a calibration on a Choice or Score rule's point is a configuration error.
   The calibration names its method, in the type and in the JSON, so a second method is an addition
   rather than a new shape.
2. **One calibration per rule and binding, set through `ForRule`.** A calibration is fitted on one
   rule's labels, so the binding-level shorthand, which expands to every Boolean rule, does not
   calibrate.
3. **The source kind is `Score`, `Logit` or `Probability`, never `Margin`.** A margin carries no
   distribution to calibrate. `Probability` is a source so that a provider whose probability proves
   miscalibrated on a user's data can be recalibrated. The provider has to declare the source kind,
   not `Probability`. A success without evidence of the source kind is `Malformed`, and a native
   `Probability` on the same result does not replace the calibrated value.
4. **The transform is written in the policy, never guessed.** `LogOdds` reads ln(v / (1 − v)) of a
   value held to [ε, 1 − ε], for a source in [0, 1]. `Identity` reads the value itself, for a logit or
   an unbounded score, and it is the only transform a `Logit` source takes.
   `EvidenceCalibration.Epsilon` is 1e-6 and part of the transform's definition: every calibration
   fitted under it depends on it, so it does not change.
5. **`Slope` is a finite number above zero, and `Intercept` is finite.** A strictly increasing map
   turns a threshold on the probability into one on the value that flags the same rows, and gives
   `Invert` an answer. Platt and scikit-learn write P = 1 / (1 + exp(A · f + B)), so their pair enters
   as `Slope` = −A and `Intercept` = −B, and a pair copied without flipping the signs is refused by its
   slope.
6. **The thresholds read the calibrated probability; the gate reads the provider's margin.** At a
   calibrated point every threshold is on `Probability`, and a gate is on the source kind and reads its
   margin as the provider returned it. A gate on the calibrated margin waits until a recorded cascade
   shows that a band around a calibrated 0.5 sends fewer rows onward than the best raw-margin gate, at
   the same verdict quality.
7. **The trace keeps the result untouched and adds the calibrated evidence beside it.**
   `Attempt.CalibratedEvidence` is `Probability` evidence — the calibrated value under the flagged
   answer's key, its complement under the other, no scale — on every attempt whose success was read at
   a calibrated point, one a gate moved past included, and on no other attempt.
   `RuleVerdict.EvidenceKind` and `EvidenceValue` are what the thresholds read. An uncalibrated
   policy's trace, JSON and spans do not change.
8. **A model mismatch is marked, never a failure.** `CalibrationProvenance.Model` names the model the
   calibration was fitted on. When a result names a different model, compared ordinally, the attempt
   carries `CalibrationModelMismatch` and the verdict is the one it would be without the mark. When
   either side names no model, nothing is compared. Core reads nothing else from the provenance.
9. **Telemetry says that a calibration was involved, and nothing more.** At a calibrated point,
   `semanticpolicy.evidence.kind` and `semanticpolicy.evidence.value` carry the calibrated
   probability, the number the ladder was read against. `semanticpolicy.calibration.method` is on
   every attempt span that carries calibrated evidence, and `semanticpolicy.calibration.model_mismatch`
   is `true` on a marked attempt's span and absent otherwise. No instrument or metric dimension
   changes, and no tag carries content ([0008](0008-telemetry-and-content-logging.md)).
10. **Core applies a calibration; the evaluation tool fits one.** The arithmetic is public on
    `EvidenceCalibration` and nowhere else: `Epsilon`, `Input`, `Apply` and `Invert`. A fitter
    computes x with `Input`, so a fit and the runtime read a value alike and the map is never written
    twice. Nothing goes into `EvidenceMath`, which never reinterprets evidence, and Core gains no
    numerics package.

This keeps 0009's reading: the calibration produces evidence, and the ladder consumes it. The
normalized decision 0004 described is the untouched `ProviderResult` plus the `Evidence` the
calibration adds, both on the `Attempt`. That needs no type of its own, so there is still no
`SemanticDecision`.

## Consequences

- Everything that evaluates through Core's step function — the runtime and the evaluation tool's
  replay — computes the same calibrated number, because the calibration travels with the policy. A
  recording keeps the provider's own numbers, so a calibration can be fitted again from it.
- A calibrated probability is an estimate fitted on labelled data. Like the evidence it comes from, it
  can be wrong on inputs unlike that data, and a model mismatch mark is a warning, not a guard.
- Calibrating a point does not move a cascade's routing, because the gate still reads the provider's
  margin. A router that wants a band around a calibrated 0.5 cannot express it yet.
- `RuleOperatingPoint` and `Attempt` gained trailing optional parameters. A constructor call compiles
  as before, a positional deconstruction needs a place for each new member, and an assembly built
  against an earlier release that constructs or deconstructs either record must be rebuilt.
- `Math.Exp` and `Math.Log` make no cross-platform promise of an exact result, so a calibrated number
  can differ by an ulp between machines. Tests compare calibrated numbers with a tolerance, and a
  threshold placed exactly on a calibrated value's image can flag a row on one machine and not on
  another.
- Two passages written before calibration existed read differently now: 0003's list of sources, which
  names `Margin` and not `Probability`, and the "Calibration" bullet of
  [protocol v0](../protocol-v0.md), which names `margin` as a source and says the layer produces a
  result in the same shape. The layer adds evidence beside a result and produces no result of its
  own. Where they disagree with this record, this record holds.

## Alternatives considered

- **A provider decorator that adds a `Probability` entry to the result.** Lost because the map would
  have to be keyed by question, recordings would store calibrated numbers that nothing could be fitted
  on again, and calibration would stop being optional per policy.
- **A registry of calibrations in dependency injection, keyed by policy, rule and provider.** Lost
  because the operating point would live in two places, and a replay would have to rebuild the
  registry.
- **A calibration on the binding, applied to every Boolean rule.** Lost because it would apply one
  question's map to the others.
- **A `SemanticDecision` record holding the result, the calibrated evidence and the calibration.** It
  takes 0004's layer literally. Lost because it is public API for what one `Evidence` beside the
  result already says.
- **A copy of the result with an extra `Probability` entry.** Simplest to read. Lost because the trace
  would rewrite `Result`, and native and calibrated probabilities would mix.
- **Temperature scaling beside Platt, for Choice and Score rules.** Lost because no Choice or Score
  threshold reads a calibrated probability yet, and the measurements made so far showed no gain from
  it. It comes back with the thresholds that would read it.
- **Always the raw value as the input, with no transform.** Platt's original form fits any scale.
  Lost because it reshapes a probability that is already calibrated, where the log-odds form contains
  the identity and barely moves it.
- **Always the log-odds, with no transform field.** Lost because a score outside [0, 1] could not be
  calibrated at all.
- **A smaller ε, or one matched to a provider's precision.** Lost because a provider's rounded 0 or 1
  would become an outlier that pulls a fit, or the bound would be tied to one provider, and ε never
  changes once policies carry it.
- **Parameters named `A` and `B`.** Lost because Platt and scikit-learn use those letters with the
  opposite sign, so a pair copied from either would invert the map.
- **Letting the gate's kind choose the margin: raw for the source kind, calibrated for
  `Probability`.** Lost because a recalibrated `Probability` source's gate would always read the
  calibrated margin, and a gate copied from the uncalibrated point would change the cascade.
- **A flag on `MarginGate` choosing the margin.** Lost because it adds public API and JSON before
  anyone needs them.
- **A model mismatch as `Malformed`.** Safer. Lost because a vendor's model upgrade would switch the
  binding off, and a server's reported model name is not reliable enough to fail on.
- **A fitting API in Core.** Users could fit in their own pipeline without the tool. Lost because it
  is more versioned public API, and an optimiser inside a runtime library.
- **Exact equality with numbers captured on one machine, or a managed `Exp` and `Log` in Core.** Lost
  because the first breaks on another C runtime, and the second puts a numerics library into a policy
  runtime for a difference no verdict has shown.
