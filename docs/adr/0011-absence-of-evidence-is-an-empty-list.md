# 0011. Absence of evidence is an empty evidence list, not a `None` kind

**Status:** Accepted
**Date:** 2026-09-22

## Context

[0003](0003-evidence-semantics.md) lists the kinds a numeric evidence can carry and names six:
`Probability`, `Score`, `Logit`, `Margin`, `Unknown` and `None`, the last defined as "the provider
returned no numeric evidence". [Protocol v0](../protocol-v0.md), frozen with
[0002](0002-provider-contract-and-capabilities.md) after two providers of different kinds passed it,
defines `evidence` as zero or more entries, each with a kind, and names five kinds. It has no `none`,
because its one rule for anything a provider cannot fill is that the field is absent — never `null`,
`0`, `false`, `""`, and never a placeholder entry. `EvidenceKind` in `SemanticPolicy.Core` mirrors the
protocol and has the same five members.

So the record and the contract disagree on one member, and a contributor who reads 0003 first
writes a sixth enum member, a converter case for it and a test that a provider "returns `None`", all
of which the contract then rejects. A record is immutable once merged, so the correction is a new
record rather than an edit.

## Decision

**A provider that has no numeric evidence returns an empty evidence list. There is no `None`
evidence kind, on the wire or in `Core`.**

- The five kinds of protocol v0 are the kinds. `Unknown` remains the kind of a number the provider
  returned without defining its meaning; it is not the kind of a number that was not returned.
- A capability set that declares no evidence kinds is how an adapter says its provider never
  produces numeric evidence. That is a legitimate provider — a judge that returns a label — and the
  evaluator checks every binding against the provider's declared capabilities before any call, so a
  threshold on a kind the provider does not declare is a configuration error, not a runtime surprise.
- Evaluation reads absence the way the contract writes it: a stored result whose evidence list is
  empty, or missing altogether, carries no evidence of any kind. A rule whose operating point reads a
  kind the result does not carry treats that result as a contract break — a `Malformed` failure that
  goes through the policy's declared failure behaviour
  ([0006](0006-failure-and-abstention-model.md), [0007](0007-per-policy-failure-behaviour.md)) — and
  never as a value with zero evidence.

Everything else in 0003 stands: a bare number is not evidence, a threshold is declared against a
kind, a score is never renamed to a probability, and `Margin` may be computed from declared
per-option evidence when a policy asks for it. This record withdraws one line of that list and
changes nothing else.

## Consequences

- One kind fewer to handle: no converter case, no capability entry, no threshold that could be
  declared against "nothing".
- The absence of evidence is not a value a provider can be asked to produce, so it cannot be produced
  wrongly. An adapter that has nothing to say says nothing, which the contract already requires of
  every other optional field.
- A provider without numeric evidence still answers Boolean, Choice and Score questions by value. A
  Boolean rule always thresholds evidence, so it cannot be bound to such a provider; a Choice or
  Score rule reads evidence only through its margin gate and can be bound to one without a gate.
- A reader of 0003 has to know this record exists. The directory has no index, so the pairing is
  carried by this record's title and by the protocol document, which was already right.

## Alternatives considered

- **Add `none` to the protocol and `None` to `Core`, matching 0003.** Lost because a kind with no
  values is a placeholder entry, which the protocol's "absent means absent" rule forbids for every
  other field, and because it would give an adapter a way to fill a required-looking field with
  nothing.
- **Edit 0003 in place.** Lost because a record is immutable once merged; the history of what was
  believed and when is most of the value of the directory.
- **Leave the disagreement and rely on the code.** Lost because the code is read after the records,
  by exactly the contributor the records exist for.
