# 0005. Thresholds belong to a policy on a provider on a dataset, and are chosen from measured operating points

**Status:** Accepted
**Date:** 2026-09-20

## Context

Every example of the library shows a threshold: `DenyAbove(0.90)`. The number is the most visible
part of a policy and the least defensible. There is no universal `0.90 = deny`. The right threshold
depends on the policy's question, on which provider answers it and on what scale
([0003](0003-evidence-semantics.md)), on the data it will see, and on what a mistake costs in each
direction — a missed injection and a blocked legitimate request are not symmetric, and the
asymmetry differs per deployment.

A single "best" threshold chosen by one metric is also the easiest thing to overfit. Optimizing F1
on the same data the threshold is reported on gives a number that looks authoritative and does not
survive production; a model that abstains on every hard case scores well on the rest.

The evaluation CLI in `tools/SemanticPolicy.Evals` exists so that a rule is measured like any
classifier. The question was what it owns, what it must never hide, and what it must refuse to do.
Which metrics it prints is product scope and is not decided here.

## Decision

**A threshold is a property of the triple (policy, provider, dataset).** The library ships no
default threshold that the documentation calls recommended. A rule without a dataset and a measured
operating point is a rule with an untested number in it, and the documentation says so.

**Thresholds are chosen against a stated constraint**, not a single score. The evaluation tooling
sweeps thresholds and recommends an operating point that satisfies a constraint the user names —
minimum recall, maximum false-positive rate, minimum precision, or minimum expected cost from
explicit error costs — and shows the trade-off curve behind it. It does not pick a "best" threshold
by F1 or accuracy on its own, and it says which data split the recommendation was made on.

**Warn and deny thresholds are chosen independently**, each against its own constraint.

**Evaluation never hides an error direction, a failure or an abstention.** Both error rates are
reported, always. Provider failures and abstentions
([0006](0006-failure-and-abstention-model.md)) are counted separately from classification errors,
so a provider that abstains on the hard cases does not win on precision. Calibration metrics are
computed only where the evidence kind is `Probability` and are reported as not applicable
otherwise.

**A held-out split is held out.** Data used to choose a threshold is not the data the threshold is
reported on. A dataset may label a record `ambiguous` or `abstain` rather than force a truth that
is not there, and public adversarial benchmarks are evaluation data, never tuning data.

**Calibration is measured by the evaluation side, applied on the runtime side, and optional per
policy.** The first release measures calibration and modifies no provider's output. A later
calibration layer fits per provider on held-out data and turns non-probability evidence into
`Probability` evidence before the threshold is applied. A policy whose evidence is already a
calibrated probability, or whose use is ordinal, does not need it.

**Evaluation is a tool, not a runtime dependency.** `tools/SemanticPolicy.Evals` depends on `Core`;
`Core` does not depend on it, and a policy runs without it.

## Consequences

- Moving a policy from Shadow to Enforce has evidence behind it: a dataset, a sweep, an operating
  point and the constraint it was chosen for. A reviewer can ask for that and it exists.
- Provider comparison is honest, because failures and abstentions are in the table rather than in
  a footnote.
- An example that shows a threshold says the number is illustrative and points at the evaluation
  tooling; it does not present it as a recommendation. The quickstart stays short; the claim it
  makes about the number is the only thing that changes.
- A threshold tuned on one provider is not portable to another without re-running the sweep. This
  is a cost and it is the truth.
- Calibration metrics on a small dataset are noisy; the tooling reports the sample size next to
  them rather than hiding it.

This forecloses shipping a recommended default threshold, and it forecloses a mode of the tooling
that returns one threshold with no constraint named.

## Alternatives considered

- **Ship sensible default thresholds per example rule.** Lost because a default that looked
  reasonable on the author's data is exactly the number nobody re-measures. The cost of one
  untested `0.90` in a security policy is higher than the cost of making every user run a sweep.
- **Let the tooling pick the best threshold by F1.** Lost because a single metric is gamed by
  construction: maximizing F1 can hide a recall collapse, and choosing on the reported split
  overstates every number. Constraint-driven selection makes the trade-off the user's decision.
- **Defer the evaluation tooling until after the first release.** Lost because a rule that cannot
  be measured cannot be moved from Shadow to Enforce with evidence, and the claim that rules are
  testable would be a claim about a future release.
- **Automatic calibration in the runtime from the start.** Lost because it needs held-out labelled
  data per provider, and calibrating on the data used to choose the threshold produces confident
  nonsense. The measurement comes first; the correction comes when there is data to fit it on.
