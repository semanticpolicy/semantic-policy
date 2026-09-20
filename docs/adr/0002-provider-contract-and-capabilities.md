# 0002. The provider contract is a small mandatory core plus declared capabilities, frozen only after two providers pass it

**Status:** Accepted
**Date:** 2026-09-20

## Context

`Core` references no provider, so everything a provider must guarantee has to be expressed in one
abstraction that `Core` owns and one contract test suite that every provider passes. The question
was what that abstraction has to carry, and what it must not pretend to carry.

The two providers planned for the alpha are deliberately different in kind. The hosted decision
model behind `SemanticPolicy.Providers.TypeSafe` takes a typed question — yes/no, choice among
options, graded score — and returns a typed answer with a probability distribution over the options
and a confidence derived from it, which the vendor states is calibrated. The local model behind
`SemanticPolicy.Providers.Local` is a zero-shot classifier: it takes candidate labels and returns a
score per label, on a scale that is not a calibrated probability and with no separate notion of
confidence. A model used as a fallback judge may return a label and nothing numeric at all. The
same request has to go through each of them and come back in a form a policy can evaluate.

A contract that gives every provider a `Confidence` field of type `double` would compile against all
three and mean something different in each. Research into the providers named that as the single
biggest risk to the provider-agnostic hypothesis: not that a provider cannot answer, but that two
providers answer with numbers that look comparable and are not.

## Decision

A provider implements one interface owned by `Core`. It receives a decision request — the decision
type (Boolean, Choice, Score), the question, the context to judge, and the choices where the type
needs them — and returns a provider result.

**Mandatory in every result:**

- the decision type, echoed;
- an outcome: a value, or an explicit non-value — the provider abstained or failed. A missing value
  is never represented by a default such as `false`, `0` or an empty label;
- a status that says which of those it is, with a failure kind when it is a failure (timeout,
  unavailable, malformed response, input rejected, not authorized, unknown);
- provider metadata: provider identifier, model or version identifier, observed latency.

**Optional, and declared rather than inferred:**

- per-option evidence of a declared kind — a calibrated probability, a provider-scaled score, a
  logit ([0003](0003-evidence-semantics.md)). A probability over every option that sums to one is
  a distribution; that is a property of the evidence, not a capability of its own;
- the provider's raw output, for evaluation and debugging.

A provider declares which decision types and which of these evidence kinds it supports. A policy or
an evaluation run can ask before it relies on one. An absent capability is absent — not zero, not
`false`, not an empty distribution.

**There is no universal `confidence` with shared semantics, and no provider reports one.** Every
numeric evidence carries its kind, and the runtime treats the kind as part of the value
([0003](0003-evidence-semantics.md)). Where a vendor computes a confidence from its own
distribution, it is a projection of evidence the result already carries and stays in provider
metadata. The margin between the top option and the runner-up is arithmetic the runtime performs on
declared evidence when a policy asks for it.
The failure and abstention states are a separate axis from the decision value
([0006](0006-failure-and-abstention-model.md)).

**Provider-specific fields live in metadata.** Nothing vendor-shaped — a field name from one API, a
wrapper object, a vendor's label for a decision type — appears in the shared fields. Raw output is
kept in the result for evaluation and is excluded from telemetry by default
([0008](0008-telemetry-and-content-logging.md)).

**The contract was frozen only after two providers of different kinds passed it.** The same
request went through a hosted decision model and a local zero-shot classifier; both returned
normalized results for Boolean, Choice and Score, with raw outputs, latency, the meaning of each
provider's numbers and seventeen failure cases examined side by side. A contract that only one kind
of provider has passed is a wrapper around that provider; this one is not.

The .NET types are the first runtime, not the protocol. The request and result are documented as a
language-neutral JSON shape in [`docs/protocol-v0.md`](../protocol-v0.md), frozen with this
decision. A change to the shape is a new protocol version and a new record.

## Consequences

- A new provider is finished when the contract suite passes against it, and the suite can test
  capability declarations directly: a provider that claims a probability distribution must produce
  one that sums to one; a provider that does not claim one must not fabricate it.
- Evaluation can compare providers on what each actually returns, and can decide per provider
  whether a calibration metric is meaningful.
- Policy authors have to handle the case where the evidence they wanted is not there. A threshold on
  a probability cannot be applied to a provider that returns only a score, and the runtime says so
  instead of coercing.
- The result type is larger than a value and a number. That is the cost of not lying about what the
  number is.
- `Core` validates a request before any provider sees it: at least two options, two to ten levels,
  a non-empty question, a context. Providers differ in what they accept — one answers a single-level
  score with a degenerate distribution — so an unvalidated request can yield a `Success` that means
  nothing. An invalid request is an argument error, not a provider outcome.
- A provider that reads text flattens a structured context to text and declares that it does. A
  policy that depends on structure being seen as structure checks the declaration.

This forecloses a `double Confidence` on the shared result, and it forecloses freezing the contract
from a single provider's response schema.

## Alternatives considered

- **One `Confidence` in [0, 1] on every result, provider fills it as best it can.** Lost because the
  same threshold would mean a calibrated probability on one provider and a sigmoid score on another,
  and nothing in the type would say which. This is the failure the provider research called the
  abstraction's biggest risk.
- **The lowest common denominator: a Boolean answer, no numbers.** Lost because it conveys nothing a
  policy can threshold, nothing an evaluation can sweep, and nothing that distinguishes a confident
  answer from a guess. A contract that minimal is provider-agnostic and useless.
- **One interface per provider, and the policy picks.** Lost because the policy would then depend on
  the provider it was written against, which is the coupling the library exists to remove.
- **Freeze the contract from the hosted provider's schema and adapt the local model to it.** Lost
  because the second provider would be a translation layer over the first provider's vocabulary, and
  the claim that the application does not depend on one provider would be untested.
- **Throw on provider failure instead of returning a status.** Lost — see
  [0006](0006-failure-and-abstention-model.md).
