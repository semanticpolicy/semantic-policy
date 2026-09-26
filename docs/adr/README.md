# Architecture decision records

One record per decision that constrains code outside the change that made it. If someone working on
an unrelated part of the library six months from now would make a worse choice for not knowing it, it
belongs here.

## Records

- [0001](0001-semantic-decision-runtime-boundary.md) — A semantic decision is a signal, not an
  authorization, and enforcement stays in the application.
- [0002](0002-provider-contract-and-capabilities.md) — The provider contract is a small mandatory
  core plus declared capabilities, frozen only after two providers pass it.
- [0003](0003-evidence-semantics.md) — A raw provider score is not a probability, and every numeric
  evidence carries its kind. [0011](0011-absence-of-evidence-is-an-empty-list.md) withdraws its
  `None` kind.
- [0004](0004-decision-policy-enforcement-separation.md) — Provider result, semantic decision,
  policy evaluation and verdict are separate layers.
  [0009](0009-normalization-layer-deferred-to-calibration.md) narrows it: there is no
  `SemanticDecision` type until a calibration layer produces one.
- [0005](0005-evaluation-and-threshold-ownership.md) — Thresholds belong to a policy on a provider
  on a dataset, and are chosen from measured operating points.
- [0006](0006-failure-and-abstention-model.md) — Provider outcome and policy verdict are two axes; a
  timeout is neither `false` nor `Deny`.
- [0007](0007-per-policy-failure-behaviour.md) — There is no global fail-open or fail-closed; each
  policy declares its failure behaviour, and Shadow and Enforce both ship.
- [0008](0008-telemetry-and-content-logging.md) — Telemetry carries metadata only; logging the
  judged content is an explicit opt-in.
- [0009](0009-normalization-layer-deferred-to-calibration.md) — The normalization layer has no type
  of its own until a calibration layer produces it.
- [0010](0010-core-decision-runtime-architecture.md) — The core runtime fixes six boundaries that
  the provider, integration and evaluation packages inherit.
- [0011](0011-absence-of-evidence-is-an-empty-list.md) — Absence of evidence is an empty evidence
  list, not a `None` kind.
- [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) — An agent integration
  evaluates a policy and the application's handler acts on the verdict.
- [0013](0013-evaluation-records-answers-and-replays-them-through-core.md) — The evaluation tool
  records provider answers once and replays them through Core.
- [0014](0014-evaluation-tool-records-live-once-and-ci-replays.md) — The evaluation tool calls a
  provider once, commits what it recorded, and CI only replays it.
- [0015](0015-self-hosted-models-through-one-system-one-client.md) — Self-hosted decision models
  are reached through one System One client, and TypeSafe's Jev is a preset on it.

## Format

`NNNN-short-slug.md`, four digits, numbered in the order they are accepted. A record is **immutable
once merged**: a decision that no longer holds is superseded by a new record that says so, not edited
in place. The history of what was believed and when is most of the value.

```markdown
# NNNN. Title in one line

**Status:** Accepted | Superseded by [NNNN](NNNN-slug.md)
**Date:** YYYY-MM-DD

## Context

What made this a question. The constraints that were real at the time — not a summary of the final
answer.

## Decision

What was decided, in the present tense. "Core does not reference any provider package."

## Consequences

What this makes easy, what it makes hard, and what it forecloses. Include the costs; a record with
only benefits was written to justify rather than to record.

## Alternatives considered

Each with the reason it lost. An alternative with no stated reason reads as one nobody thought about,
and it comes back every six months.
```

## What does not go here

Anything commercial: pricing, competitors, what gets built next quarter, who a feature is aimed at.
This directory is read by contributors deciding how to write code, and a record they cannot act on is
noise at best.
