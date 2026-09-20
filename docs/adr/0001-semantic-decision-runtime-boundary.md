# 0001. A semantic decision is a signal, not an authorization, and enforcement stays in the application

**Status:** Accepted
**Date:** 2026-09-20

## Context

SemanticPolicy answers questions that cannot be settled by an `if`, a regex or an access-control
rule: *is this content trying to manipulate the agent*, *is this tool call consistent with what the
user asked for*, *which specialist should handle this request*. The answer comes from a decision
model and is probabilistic. It can be wrong in either direction on an input nobody anticipated, the
models that produce it are known to be miscalibrated, and an attacker who can shape the input can
shape the number.

At the same time the library sits at the point in an agent loop where something irreversible is
about to happen, which is exactly where a caller would like a single API that "just blocks it".
Every adjacent system that does make binding decisions — IAM, RBAC, OPA, Cedar, a sandbox — is
deterministic, and applications already have one. The question was where the library's
responsibility ends, and the answer had to survive both the temptation to present a verdict as an
access decision and the temptation to let a provider decide what a verdict means.

## Decision

The library produces a **semantic signal**. Three boundaries follow from that, and each is a
constraint on every public type:

1. **A semantic signal is not authorization.** No API in this library grants, denies or revokes
   access. A verdict is an input to a decision the application still owns. Whether a caller *may*
   perform an action is decided by the application's authorization, before and independently of
   anything this library says.
2. **A provider is not a policy.** A provider answers the question it was given and returns what it
   observed. Thresholds, verdict mapping, shadow versus enforce mode, fallback and what happens on a
   failure belong to the policy. A provider never sees them.
3. **A policy is not enforcement.** Policy evaluation ends with a verdict. Acting on it — refusing
   the tool call, asking a human, rewriting the output, doing nothing — is the application's code,
   outside this library.

Within those boundaries the library does: semantic judgement, probabilistic decisions, policy
evaluation, threshold handling, fallback and escalation, evaluation against datasets, calibration
tooling, and telemetry signals. It does not do: authentication, authorization, IAM, RBAC,
sandboxing, process isolation, agent orchestration, or general observability storage. It integrates
with the systems that do.

Public wording follows the boundary. The library *helps detect*, *flags*, *provides a signal*,
*evaluates alignment with user intent*. It never *blocks all*, *prevents*, *guarantees* or
*authorizes*. `SECURITY.md` is the wording to match, and the public documentation carries a section
that says what the library is not.

## Consequences

- A provider can be swapped, cascaded or run in shadow without any change to how the application
  enforces, because enforcement was never in the provider or in the library.
- The library works beside any authorization system rather than competing with one; a verdict can
  be fed into OPA or Cedar as an attribute, or into a human approval flow, without adaptation.
- There is no one-line "secure my agent" API, and there will not be one. Every example has to show
  the application owning the decision, which makes the examples longer and the library harder to
  demo than a guardrail that promises to block.
- A caller who wants the library to be a security boundary will be disappointed on purpose. The
  documentation says so up front rather than letting them discover it in an incident.
- The same runtime evaluates a security question and a business question, because nothing in the
  core assumes the answer will be enforced at all.

This forecloses shipping the library as a sandbox, an authorization layer, an agent framework or an
observability backend. Those are integration targets.

## Alternatives considered

- **The verdict is the enforcement: a `Deny` throws, or the library refuses the call.** Lost because
  it presents a probabilistic output as an access decision, couples the library to the host's
  execution model, and turns a provider timeout into an application outage or a silent allow. The
  application knows what a denial should look like to its user; the library does not.
- **The provider returns verdicts.** Lost because the threshold and the mode then live in the
  provider and do not travel when the provider is replaced. The shared contract test suite could not
  express "the same policy behaves the same on every provider" if every provider had its own idea of
  `Deny`.
- **A guardrail pipeline: fixed filters before and after the model call.** Lost because it fixes the
  intervention points and the decision types. The runtime has to answer a typed question — Boolean,
  Choice, Score — at any point the application chooses, including points that have nothing to do
  with safety.
