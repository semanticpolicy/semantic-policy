# 0006. Provider outcome and policy verdict are two axes; a timeout is neither `false` nor `Deny`

**Status:** Accepted
**Date:** 2026-09-20

## Context

A provider call can end six ways that all look like "not yes" from a distance: the model said no;
the model declined to answer; the model answered with evidence too weak to threshold; the call timed
out; the endpoint was unreachable; the response did not parse. Collapsing them into one value is the
natural thing to do in a Boolean API, and it is wrong in both directions. A timeout read as `false`
silently allows on a "deny if true" rule and silently denies on an "allow if true" rule, and the
policy that was supposed to decide what happens on failure never sees a failure.

The provider research and the threat model both named this: a failed or low-evidence check must not
be interpreted as "allow", and a provider error is a technical fact about the call, not a semantic
fact about the input.

## Decision

The result of a provider call and the result of a policy are **two separate axes**, each with its
own type, and neither value is ever expressed as a value of the other.

**Provider outcome** — what happened to the call:

- **Success** — the provider returned a value, with whatever evidence it supports;
- **Abstain** — the provider declined to answer this input. Evidence may still be present;
- **Failure**, with a kind — **Timeout**, **Unavailable**, **Malformed** (the response did not
  match the contract), **RejectedInput** (the provider refused the request as invalid), **Unauthorized**,
  **Unknown**.

**Policy verdict** — what the policy concluded:

- **Allow**, **Warn**, **Deny**, **Escalate** — the policy decided;
- **Abstain** — the policy could not decide from what it had, and says so rather than picking a
  side.

Rules that follow:

- `Timeout != false`. `Timeout != Allow`. `Timeout != Deny`. A failure is carried as a failure
  through `ProviderResult` and `SemanticDecision` ([0004](0004-decision-policy-enforcement-separation.md))
  until the policy's declared failure behaviour maps it to a verdict
  ([0007](0007-per-policy-failure-behaviour.md)).
- **Low evidence is a Success.** A value with evidence below every threshold is a successful call
  whose answer the policy treats as insufficient; it is not an abstention and not a failure. The
  distinction is the policy's to make, and it is made in `PolicyEvaluation`.
- **A negative decision is a Success.** "No, this is not an injection" is a value, not the absence
  of one.
- **Malformed is a failure, not a low-confidence success.** A response that does not match the
  contract is not partially trusted.
- **Provider failures are not exceptions.** A provider adapter returns a `Failure` outcome; it does
  not throw past the policy. Exceptions are for programming errors and cancellation, not for the
  provider being slow.
- Telemetry records both axes on every evaluation
  ([0008](0008-telemetry-and-content-logging.md)), and evaluation counts abstentions and failures
  separately from classification errors ([0005](0005-evaluation-and-threshold-ownership.md)).

## Consequences

- A policy can say "on timeout, fall back to the local provider; on abstain, escalate; on a
  negative decision, allow" and each clause is about a different thing. Without the two axes the
  first and third clauses would be about the same value.
- A dashboard can tell an outage from an attack. Failure rate and deny rate are different numbers.
- Provider adapters have more to do on the error path: every transport, parsing and status-code
  case has to land on a named failure kind. The contract suite tests every kind, so a provider that
  maps everything to `Unknown` fails the suite.
- Callers who wanted a `bool` get a verdict and an outcome. The fluent API can offer a convenience
  over the verdict; it cannot offer one that hides the outcome.
- Every example that shows a `Deny` also has to show what the application does on `Failure` and on
  `Abstain`, because the library will not choose for it.
- Neither provider in the alpha abstains on its own — a hosted decision model answers an empty
  context with a probability, and a classifier always scores. `Abstain` stays in the provider
  outcome for providers that do; the abstention that matters in practice is the policy's, derived
  from evidence in `PolicyEvaluation`.

This forecloses `Task<bool> EvaluateAsync(...)` as a public surface, and it forecloses a provider
that answers `false` when it did not run.

## Alternatives considered

- **Throw on provider failure.** Lost because an exception in an agent loop is neither an allow nor
  a deny; it is whatever the nearest `catch` does, which is usually "log and continue", which is a
  silent allow. The policy's declared failure behaviour is bypassed by the language.
- **Map failure to `Deny`.** Lost because it is a global fail-closed, which
  [0007](0007-per-policy-failure-behaviour.md) rejects, and because it hides outages inside the deny
  rate.
- **Map failure to low confidence and let the threshold sort it out.** Lost because a threshold is
  about the input and a failure is about the call. A policy tuned for recall on real answers would
  treat every outage as a positive.
- **One enum with all the states in it — `Allow, Deny, Timeout, Abstain, ...`.** Lost because it
  makes "the provider timed out and the policy fell back and the fallback allowed" unrepresentable,
  and that is the case the model has to represent.
