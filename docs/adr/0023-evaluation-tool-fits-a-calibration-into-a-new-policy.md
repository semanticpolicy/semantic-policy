# 0023. The evaluation tool fits a calibration on tune and writes it into a new policy

**Status:** Accepted
**Date:** 2026-10-02

## Context

[0019](0019-calibration-is-data-on-a-boolean-operating-point.md) made a calibration data on a
Boolean operating point, applied by Core, and left fitting one to the evaluation tool.
[0005](0005-evaluation-and-threshold-ownership.md) said a calibration layer fits per provider on
held-out data, that data used to choose a threshold is not the data it is reported on, and that
calibrating on the data used to choose the threshold "produces confident nonsense".

The evaluation tool now has a `calibrate` verb, and `report`, `sweep` and `compare` read the policy it
writes. Writing them raised nine questions, and each answer holds for whatever fits or reads a
calibration next:

1. Which rows is a calibration fitted on, and which is it reported on?
2. How few rows are too few?
3. Which of the tune rows count?
4. Which input transform does the tool write?
5. How does a fit reach a policy, and what happens to its thresholds and gate?
6. What do "before" and "after" measure?
7. What does the written file record, and when is it the same file?
8. How do the other verbs and the JSON result read a calibrated point?
9. What becomes of a provider that declares a probability of its own?

## Decision

1. **A calibration is fitted on tune and reported on test.** It is fitted on the same tune rows the
   thresholds are chosen on, and every number `calibrate` prints is measured on test. This clarifies
   [0005](0005-evaluation-and-threshold-ownership.md): the held-out split is what the numbers are
   reported on. Platt scaling is strictly increasing, so a threshold chosen under a recall,
   false-positive-rate or precision bound flags the same rows on raw and on calibrated evidence, and
   fitting both on tune does not bias the threshold. What would be confident nonsense is a
   calibration reported on the rows it was fitted on, where it always looks better than it is.
   Without a split, `calibrate` fits and reports on the same rows and says so in the words `sweep`
   prints.

2. **A fit needs at least ten flagged and ten other rows, and uses smoothed targets.** Below either
   count `calibrate` exits with code 1, names both counts and writes nothing; there is no override.
   The counts are always in the output. Platt's targets are smoothed, (N₊ + 1) / (N₊ + 2) for a
   flagged row and 1 / (N₋ + 2) for the others, so tune rows that separate perfectly still give a
   finite slope.

3. **The fitting rows are every tune row the binding read.** A tune row counts when its label is an
   answer, not `ambiguous` or `abstain`, and the recorded attempt at the binding is a success that
   carries evidence of the source kind, whatever the rest of the chain did with it. The flagged value
   is read after `EvidenceMath.WithBooleanComplement`. A row the binding failed on does not count.

4. **The tool chooses the transform and writes it.** It writes `LogOdds` for a `Probability` source,
   and for a `Score` source when every fitting value and every threshold of the input point lies in
   [0, 1]; on a re-fit, each threshold is read as its image under the old calibration's `Invert`. It
   writes `Identity` for a `Logit` source, and for a `Score` with any fitting value or threshold
   outside [0, 1]: a threshold above 1 says the scale goes above 1, and under `LogOdds` it would
   become `Apply(1 − ε)` and flag rows it did not flag before.

5. **`calibrate` writes a new policy in which no recorded row changes its verdict.**
   - It reads a policy, a dataset and a recording, and calls no provider. `--provider` names the
     binding, and is required when the policy has more than one. `--out-policy` names a new file; it
     may be neither a file `calibrate` reads nor another of its outputs.
   - The file is the input policy whole, with only the calibrated operating point changed: the
     calibration, and each threshold moved through the map to the probability of the value it stood
     at. The gate is copied unchanged, because it reads the provider's own margin
     ([0019](0019-calibration-is-data-on-a-boolean-operating-point.md) § 6).
   - On a point that already carries a calibration, the new one replaces it, and each threshold goes
     back through the old map's `Invert` before the new map's `Apply`.
   - The binding is replayed through Core alone before and after, and a fit that would change a
     recorded row's verdict is refused, naming the rows. A threshold the map cannot move, such as one
     inside the log-odds clamp near 0 or 1, is the user's to move first.
   - Every number the map produces goes through Core's `EvidenceCalibration`
     ([0019](0019-calibration-is-data-on-a-boolean-operating-point.md) § 10).

6. **Before and after are measured on the binding alone, without its gate, by replay.** A variant of
   the policy narrowed to the one binding, with its gate removed, is replayed through Core on the test
   rows, and the calibration metrics are read from that replay. "After" is the written point. "Before"
   is the input point as it stands, and applies only when it already reads a probability: a
   `Probability` source, or a calibration being replaced. Otherwise it is reported as not applicable.

7. **The written file records what it was fitted on, and the same inputs write the same bytes.**
   - The provenance holds the model most fitting rows name, ties broken ordinally; the output counts
     the fitting rows that name another, and a result that names no model is not counted.
   - It holds `sha256:` and the hex digest of the file the fitting rows came from, the tune split's
     name when the split comes from the dataset's metadata (null otherwise), and the flagged and
     other row counts. It holds no timestamp.
   - The file is written with the library's policy JSON options, indented, with `\n` newlines, as
     UTF-8 without a byte-order mark, ending in a newline. The same inputs on the same machine write
     the same bytes. On another operating system the slope, the intercept and the moved thresholds
     may differ in their last digits ([0019](0019-calibration-is-data-on-a-boolean-operating-point.md)
     names why), so a committed file is compared with a run elsewhere parsed, with a tolerance.

8. **The other verbs read a calibrated point through Core, and the result format grows by optional
   members.**
   - At a calibrated point, `sweep`'s threshold candidates are `EvidenceCalibration.Apply` of the
     flagged source-kind value of every recorded result, merged with the 0.05 grid. Its gate
     candidates read the calibration's source kind.
   - `report` counts the rows with an attempt marked `CalibrationModelMismatch`, as
     `calibrationModelMismatchRows`, left out when no binding carries a calibration for the rule.
   - `calibrate --out` writes the `semanticpolicy/evals-result/v0` envelope with a `calibrate`
     section.
   - Both are optional members, so the format stays `v0`
     ([0020](0020-evaluation-tool-retries-resumes-and-gates-a-build.md)).

9. **A declared probability and a fitted calibration are two roads, and both stay.** A System One
   binding can still declare `probability` evidence. The documentation says that is a claim nothing
   checks, and points at `calibrate` for a measured fit, which can also recalibrate a `Probability`
   source ([0019](0019-calibration-is-data-on-a-boolean-operating-point.md) § 3).

## Consequences

- A calibration is fitted from a recording, with no provider call and no key. After a model upgrade
  it is fitted again from a new recording, and until then the mismatch count says how many rows the
  new model answered.
- A dataset with fewer than ten flagged or ten other answered tune rows cannot be calibrated at all.
  The way out is labelling more rows, not a flag.
- Calibrating does not change which rows a binding ranks first, so it does not improve
  discrimination: a provider that cannot tell flagged rows from the others still cannot.
- A threshold at 1.0, or one a re-fit takes above the old map's top, makes `calibrate` refuse the
  fit. The user moves the threshold to a value the provider's evidence reaches first.
- Without a split, the numbers after calibration are optimistic, and the output says so; it does not
  refuse.
- "Before" and "after" describe the binding alone. A cascade's own report still shows how the whole
  chain decided, and the two can differ.
- A calibrated file is reproducible on one machine only. Tests and CI compare it parsed, with a
  tolerance, never byte for byte.
- A run resumed across a vendor's upgrade can still be calibrated, under the model most of its rows
  name.
- A reader of the JSON result that ignores unknown members keeps working, and one that wants the new
  numbers reads two optional members.

## Alternatives considered

- **A third split for calibration, [0005](0005-evaluation-and-threshold-ownership.md) to the
  letter.** Lost because on the shipped smoke set it leaves about 33, 27 and 40 rows, and every number
  becomes much noisier.
- **Cross-fitting on tune, with out-of-fold calibrated values.** The cleanest statistically. Lost
  because it is the most code and the hardest to explain.
- **Refusing to fit without a split.** Lost because it is stricter than `sweep`; the tool fits and
  says the numbers are optimistic instead.
- **No minimum, with the counts in the report, or a warning below one.** Lost because a calibration
  fitted on five rows would land in a policy looking as trustworthy as any other.
- **Fitting only on the rows the chain classified.** Lost because a gate that sends a row on, or a
  later binding that decides it, would drop the band nearest 0.5, where calibration matters most.
- **`LogOdds` for any `Score` whose fitting values lie in [0, 1], whatever its thresholds.** Lost
  because a threshold above 1 would move to `Apply(1 − ε)` and flag rows it did not flag before.
- **`sweep --calibrate`, fitting and sweeping in one step, with the user editing the policy by hand;
  or printing the parameters only.** Lost because no verb wrote a policy, and a fit copied by hand is
  a fit copied wrong.
- **Writing the smallest calibrated gate that covers the rows the copied gate sends onward.** Lost
  because that gate also sends others: on the shipped smoke set, a whole further band of scores goes
  to the next binding, at more calls and more cost.
- **Requiring `--gate` and sweeping the calibrated margin.** Lost because a gate on the calibrated
  margin waits for evidence ([0019](0019-calibration-is-data-on-a-boolean-operating-point.md) § 6).
- **Dropping the gate and saying so.** Lost because rows the gate sent onward would be decided by the
  calibrated binding instead.
- **Overwriting `--policy` in place.** Lost because the original is what the comparison is made
  against.
- **Writing only the calibrated binding.** Lost because a policy file must load on its own.
- **Measuring before and after through the whole chain, gate included.** Lost because the gated band
  would drop out and rows another binding decided would mix in.
- **Computing the metrics from the fitted map directly.** Lost because it is a second path beside the
  replay through Core.
- **On a re-fit, "before" as not applicable for a `Score` or `Logit` source.** Lost because it hides
  the comparison a re-fit is run for.
- **Refusing to fit when the fitting rows name several models.** Lost because a run resumed across a
  vendor's upgrade could never be calibrated.
- **Recording no model.** Lost because it would switch the mismatch mark off without a word.
- **The bare hex digest, as the recording header keeps it.** Lost because the value would not say
  which algorithm made it.
- **A timestamp in the provenance.** Lost because the same inputs would no longer write the same
  file.
- **Sweep candidates read off a replay's calibrated evidence.** Core's numbers too. Lost because it
  costs a full replay per candidate set where one call per result does.
- **The mismatch count as a note only.** Lost because a build could not read the number.
- **A new result format version.** Lost because the change is additive.
- **Text output only from `calibrate`.** Lost because every other verb's numbers are readable by a
  build, and the tests read `--out`.
- **`[Obsolete]` on System One's `probability` declaration.** It would leave a local server one road to
  `Probability`. Lost because it is an API change, and a server may really be calibrated.
