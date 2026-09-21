# 0009. The normalization layer has no type of its own until a calibration layer produces it

**Status:** Accepted
**Date:** 2026-09-21

## Context

[0004](0004-decision-policy-enforcement-separation.md) names four layers a decision flows through
and gives each its own type: `ProviderResult`, `SemanticDecision`, `PolicyEvaluation` with its
`PolicyVerdict`, and application enforcement outside the library. The second layer was described as
the normalized meaning of a provider result — the answer in the vocabulary of the decision type, and
the evidence in a form a policy can threshold — carrying a calibrated probability only when one
genuinely exists.

That record was written before the provider contract was frozen. [Protocol v0](../protocol-v0.md),
frozen with [0002](0002-provider-contract-and-capabilities.md) after two providers of different
kinds passed it, already delivers what the second layer was meant to produce: the value is in the
vocabulary of the decision type (`true` or `false`, an option key, a level with its index), every
numeric evidence carries its kind ([0003](0003-evidence-semantics.md)), and a provider that lacks a
kind omits it rather than fabricating it. Normalization happens at the contract boundary, inside the
adapter, and a result crosses into `Core` already normalized.

The one transformation 0004 reserved for the second layer that the contract does not perform is
calibration: turning `score`, `logit` or `margin` evidence into `probability` evidence from held-out
data. [0003](0003-evidence-semantics.md) and [0005](0005-evaluation-and-threshold-ownership.md)
place that layer after the first release and make it optional per policy.

The question was whether to ship a `SemanticDecision` type now, mapping a `ProviderResult` onto
itself so that the code matches the record, or to say that the layer has nothing to do yet.

## Decision

**Policy evaluation reads `ProviderResult` directly. There is no `SemanticDecision` type until a
calibration layer exists to produce one.**

- The layers of 0004 stand as a description of responsibilities: what the provider returned is kept
  apart from what the policy concluded, thresholds and modes never reach a provider, and a stored
  result is re-evaluated against a different policy without a provider call. What changes is one
  sentence: the second layer does not have its own type while its only content would be a copy of
  the first.
- Evaluation performs small arithmetic on declared evidence before it thresholds: the
  runtime-computed margin that 0003 sanctions, and the complement of a Boolean probability that a
  provider sent under one key. This is evaluation reading evidence, not a normalization layer; it
  never changes a kind and never produces a probability from a score.
- When a calibration layer lands, its output — the same result with `probability` evidence added per
  provider from held-out data — is the normalized decision 0004 described. It gets its type then,
  defined by what calibration actually produces rather than by a placeholder written before it
  existed.

0004 remains accepted. This record narrows it and says when the narrowing ends.

## Consequences

- One type fewer in the public surface: a caller who wants the verdict sees `ProviderResult` inside
  `PolicyVerdict` and nothing between them. Explainability and replay read the provider result,
  which is the unit of record 0004 already made it.
- The evaluation function takes provider results as its input, so an offline evaluation run feeds
  stored results into the same function the runtime uses. No mapping step sits in the way that a
  test would have to prove is the identity.
- Adding a calibration layer later is an addition, not a refactor of a type every caller already
  holds: the layer produces results, evaluation reads results, and a policy that opts into
  calibration sees `probability` evidence where it saw `score` before.
- A reader of 0004 finds a type name the code does not contain. This record is the answer, and the
  text of 0004 is unchanged so that the history of what was believed stays readable.

This forecloses a `SemanticDecision` type that exists to satisfy a diagram, and it forecloses folding
calibration into evaluation when calibration arrives: calibration produces evidence, evaluation
consumes it.

## Alternatives considered

- **Ship a `SemanticDecision` record now, produced by an identity `Normalize()`.** Lost because it
  is public API to document, version and maintain with nothing behind it, its only test would assert
  that a copy equals its original, and its shape would be guessed before calibration exists to
  define it.
- **Treat 0004 as describing logical layers, and say nothing.** Lost because 0004 says each layer
  has its own type, and a contradiction between an accepted record and the code, written down
  nowhere, is the thing this directory exists to prevent. It would come back as a review question
  every time a new contributor read 0004.
- **Supersede 0004 with a three-layer record.** Lost because three of its four layers are unchanged
  and the fourth is deferred, not removed. A superseding record would restate what still holds in
  order to change one sentence.
- **Perform calibration inside evaluation from the start, so that the layer has a job.** Lost by
  [0003](0003-evidence-semantics.md) and [0005](0005-evaluation-and-threshold-ownership.md):
  calibration needs held-out labelled data per provider, which the first release measures and does
  not yet apply.
