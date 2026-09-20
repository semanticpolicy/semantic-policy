# 0004. Provider result, semantic decision, policy evaluation and verdict are separate layers

**Status:** Accepted
**Date:** 2026-09-20

## Context

The first sketch of the library had one result type: the provider returned it, the policy read a
number out of it, and the application switched on a verdict in it. One type is convenient until
three questions arrive at once — where does calibration happen, where does a threshold live, and
what does a timeout turn into — and each has a different answer depending on which part of the
type is being asked.

[0001](0001-semantic-decision-runtime-boundary.md) draws the boundaries between provider, policy
and application. This record names the layers those boundaries produce and what each one is allowed
to know.

## Decision

A decision flows through four layers. Each has its own type, and a layer only reads from the one
before it.

```text
ProviderResult
      ↓
SemanticDecision
      ↓
PolicyEvaluation  →  PolicyVerdict
      ↓
Application enforcement (outside the library)
```

**ProviderResult** is what the provider actually returned, untouched: the outcome (value, abstain
or failure), the evidence with its kind, provider and model identifiers, latency, and the raw output
when the provider supplies it. It knows nothing about thresholds or modes. It is the unit of record
for evaluation: a stored `ProviderResult` can be re-evaluated against a different policy without
asking the provider again.

**SemanticDecision** is the normalized meaning of that result: the answer in the vocabulary of the
decision type (a Boolean, a chosen option, a graded level), and the evidence in a form a policy can
threshold. It carries a calibrated probability only when one genuinely exists — the provider yielded
it, or a calibration layer computed it ([0003](0003-evidence-semantics.md)). It never invents one.

**PolicyEvaluation** applies the policy to the decision: the thresholds, the mode (Shadow or
Enforce), the declared failure behaviour, fallback to another provider, and the mapping from the
thresholded decision to an action. Its output is a **PolicyVerdict** — Allow, Warn, Deny, Escalate,
or Abstain when the policy could not decide — together with what produced it: which rule, which
threshold, which evidence, which provider, which mode, whether a fallback ran, and, in Shadow, what
the verdict would have been. When a policy has two thresholds (warn and deny), the verdict carries
both and says which one was crossed. Telemetry is emitted from this layer.

**Application enforcement** is not a layer of the library. The verdict is returned; the application
decides what a `Deny` means for its user, its tool and its audit trail.

## Consequences

- Ownership is legible from the types. A change to thresholding touches `PolicyEvaluation`; a
  change to what a provider returns touches `ProviderResult`; neither reaches the other.
- Evaluation replays. Because a `ProviderResult` is complete on its own, a dataset run can store
  every provider response once and sweep thresholds, compare policies and compute calibration
  metrics offline, without a second round of provider calls.
- Shadow mode is a property of evaluation, not of the provider or the decision, so a provider does
  not know whether it is being enforced and cannot behave differently when it is.
- There are more types than a single-result design would have, and a caller who only wants the
  verdict sees the layers underneath it. The fluent API can hide the layers; the types cannot
  collapse them.
- Every verdict is traceable to its evidence and its threshold, which is what makes a `Deny`
  explainable in a review and reproducible in a test.

This forecloses a provider adapter that applies thresholds "for speed", and a decision type that
carries a verdict.

## Alternatives considered

- **One `SemanticDecisionResult` that holds value, number, threshold state and verdict.** Lost
  because the same object would be produced by the provider and consumed by the application, so
  every field would need to be nullable depending on who filled it, and nothing in the type would
  say whether a `Deny` came from a threshold or from a timeout.
- **Provider returns the verdict.** Lost by [0001](0001-semantic-decision-runtime-boundary.md).
- **Policy evaluation inside the provider adapter, to save a hop.** Lost because the policy would
  then be re-implemented per provider, the contract suite could not test it once, and a provider
  swap would change enforcement behaviour.
- **Two layers: result and verdict, with normalization folded into evaluation.** Lost because
  calibration and normalization are provider-specific while thresholding is policy-specific;
  folding them together puts provider knowledge into the policy layer, which
  [0002](0002-provider-contract-and-capabilities.md) keeps out.
