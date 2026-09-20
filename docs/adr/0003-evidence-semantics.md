# 0003. A raw provider score is not a probability, and every numeric evidence carries its kind

**Status:** Accepted
**Date:** 2026-09-20

## Context

A policy wants to write `DenyAbove(0.90)`. Providers return numbers between 0 and 1. The question
was whether those two facts join.

They do not. The hosted decision model returns a probability distribution over the options and a
confidence derived from it, trained to be calibrated: when it says 0.90, about nine in ten such
answers are right. A local zero-shot classifier returns a score per label from a sigmoid or a
softmax, which is monotonic — a higher score is more likely the label — but nothing more. A logit
is unbounded. A margin between the top two options is a third thing. A judge model prompted for a
number returns whatever it was prompted for. Each is a legitimate output of a legitimate provider,
each fits in a `double`, and each means something different when compared with 0.90.

Two things make this worse than an inconvenience. Deep classifiers are systematically
overconfident, so a raw score read as a probability overstates certainty in the direction that
lets attacks through. And an attacker who controls the input can push a score across a fixed
threshold; a threshold on a number whose meaning is not defined cannot be reasoned about, only
tuned.

## Decision

**The runtime does not interpret a provider's number as a calibrated probability unless the number
says it is one.**

Every numeric evidence carries a kind. The kinds, by meaning — the type and member names are
decided with the public API:

- **Probability** — in [0, 1], and the provider or a calibration layer claims it is calibrated;
- **Score** — monotonic and provider-scaled; ordering is meaningful, the value is not a probability;
- **Logit** — an unbounded log-odds value;
- **Margin** — the gap between the top option and the runner-up, on the provider's scale;
- **Unknown** — a number the provider returned whose meaning it does not define;
- **None** — the provider returned no numeric evidence.

Rules that follow:

- A bare number is not a valid result. `0.91` without its kind does not satisfy the provider
  contract ([0002](0002-provider-contract-and-capabilities.md)).
- A policy threshold is declared against a kind. Applying a probability threshold to evidence of a
  different kind is a configuration error the runtime reports, not a silent coercion.
- A calibrated probability appears in a decision only when the provider yields one or a calibration
  layer computed one from other evidence. The runtime never manufactures one by renaming a score.
- A `Score` decision type — a graded answer — is distinct from `Score` evidence. The graded answer
  is an ordered set of levels declared in the request; the value is the top level and the evidence
  is per level, of whatever kind the provider supports. A continuous scalar such as the expected
  level is derived from that evidence and is not part of the result.
- `Margin` may be computed by the runtime from any per-option evidence when a policy asks for it.
  That is arithmetic on declared evidence, unlike renaming a score to a probability, and the result
  is `Margin` on the provider's scale, not a probability.
- Evaluation computes calibration metrics only on `Probability` evidence. For other kinds it reports
  threshold-free discrimination and per-provider operating points instead
  ([0005](0005-evaluation-and-threshold-ownership.md)).

## Consequences

- Cross-provider threshold comparison — "0.9 on provider A versus 0.9 on provider B" — is only
  meaningful after calibration, and the types make that visible at the point of comparison rather
  than in an incident.
- A calibration layer has a defined place: it turns `Score`, `Logit` or `Margin` evidence into
  `Probability` evidence, per provider, from held-out data. It is optional per policy and is not in
  the alpha.
- The policy API is more explicit than `DenyAbove(0.90)`. The fluent surface can keep that shape for
  probability evidence, but it has to name what it thresholds, and the documentation has to say why.
- Provider adapters carry the burden of honesty: a provider that outputs a sigmoid score declares
  `Score`, not `Probability`, even if `Probability` would look better in a comparison table.
- Abstention and low evidence stay distinguishable from a low probability
  ([0006](0006-failure-and-abstention-model.md)).

This forecloses any shared field whose meaning depends on which provider filled it.

## Alternatives considered

- **Normalize everything into [0, 1] and call it confidence.** Lost because normalization does not
  change what the number means; it only hides that the meaning differs. The threshold fooled in
  production would be fooled with a cleaner-looking API.
- **Per-provider thresholds, no kinds.** Lost because it addresses comparability and not meaning. A
  threshold per provider still does not tell evaluation whether an expected-calibration-error is
  meaningful, or tell a policy author whether "0.8 means safe" is a statement about the world or
  about one model's output scale.
- **Calibrate every provider inside the runtime so all evidence is a probability.** Lost for the
  alpha because calibration needs held-out labelled data per provider and per policy, which the
  runtime does not have and should not require. It is the direction of a later calibration layer,
  not a substitute for typed evidence.
- **Expose only the provider's raw output and let the policy interpret it.** Lost because it moves
  the provider's semantics into every policy, which is the coupling
  [0002](0002-provider-contract-and-capabilities.md) removes.
