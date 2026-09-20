# 0007. There is no global fail-open or fail-closed; each policy declares its failure behaviour, and Shadow and Enforce both ship

**Status:** Accepted
**Date:** 2026-09-20

## Context

The threat model asked for fail-closed: if a security check times out, the safest thing is to
block. It is right for a prompt-injection rule on a tool that can send email. It is wrong for a rule
that tags a support ticket with a department, where a hung provider would take the ticket queue
down, and it is meaningless for a rule that only records a score for later review. The same runtime
runs all three ([0001](0001-semantic-decision-runtime-boundary.md)), so a library-wide default is
wrong for most of the policies it would apply to.

The threat model also recommended that the public alpha be shadow-only: evaluate, record, never
block. The recommendation is sound about deployment and was not adopted about the runtime, because
a mode that does not exist in the alpha cannot be tested by the contract suite in the alpha, and
turning it on later would be an upgrade rather than a configuration change.

## Decision

**Every policy declares what happens when the provider does not decide.** The declaration is part
of the policy definition, it is explicit, and there is no library-wide default to fall back on. A
policy without a failure behaviour is incomplete and the API says so; it does not quietly pick
one.

The minimum set of behaviours:

- **Allow** — proceed as if the check had passed; the failure is recorded;
- **Deny** — stop; the failure is recorded as the reason;
- **Fallback** — evaluate again on a named fallback provider, then apply the fallback's own
  outcome to this same declaration;
- **Escalate** — hand the decision to something outside the runtime: a human, a stronger model, a
  separate queue.

The declaration covers provider failure and provider abstention
([0006](0006-failure-and-abstention-model.md)); a policy may declare them separately.

**Both modes ship in the alpha:**

- **Shadow** — the policy is evaluated in full, the verdict and everything behind it are recorded,
  and the runtime returns pass-through. Telemetry marks the verdict as shadow;
- **Enforce** — the verdict is returned as the policy's decision.

The mode is a property of the policy evaluation, not of the provider, and switching it is a
configuration change with no code change.

**Guidance, not mechanism:** a new probabilistic policy on a security-sensitive path starts in
Shadow, and moves to Enforce when a dataset evaluation and production observations justify the
threshold ([0005](0005-evaluation-and-threshold-ownership.md)). Every example in the repository
shows Shadow first. Security-sensitive policies are expected to declare **Deny** or **Escalate**;
business-semantic policies may declare **Allow** or **Fallback**; a policy whose only purpose is
to observe should never block, and its examples show **Allow**.

Whether the public API also needs a named risk class — security-critical, business,
observability-only — from which a failure behaviour could be derived, or whether an explicit
behaviour is enough, is open. This record decides the explicit behaviour; a risk class, if it
comes, is sugar over it and not a replacement.

## Consequences

- A hung provider affects exactly the policies that chose to be affected. A deploy can carry a
  fail-closed injection guard and a fail-open router side by side, and each is right.
- Reviewing a policy means reading one declaration to know what an outage does to it. There is no
  need to know the library's default, because there is none.
- Policy authors must make a decision they might have preferred to avoid, at the moment they write
  the policy. This is the intended friction.
- Shadow and Enforce are both covered by the contract suite from the first provider, so no provider
  is "shadow-only tested".
- The examples are longer: each shows a mode and a failure behaviour, and the security examples
  show Shadow before Enforce.

This forecloses a `SemanticPolicyOptions.FailClosed` global, and it forecloses an alpha in which
Enforce is not a supported mode.

## Alternatives considered

- **Global fail-closed.** Lost because it breaks every policy that is not a security check, and
  because one provider outage becomes an application outage for all of them at once. The threat
  model's own recommendation was per-category, not global.
- **Global fail-open.** Lost because it silently disables every security policy during an outage,
  which is the window an attacker would choose.
- **Fail-closed by default, opt out per policy.** Lost because a default is still a default: the
  policies that forget to opt out are the business rules that should never have been closed, and the
  policies that opt out carelessly are the security rules that should never have been open. Making
  the declaration mandatory removes the class of policy that inherited its behaviour.
- **Shadow-only alpha, Enforce in a later release.** Lost because it makes the first enforcement an
  upgrade rather than a switch, leaves Enforce untested by the alpha's contract suite, and makes the
  alpha's claim that policies are shippable gradually a claim about a future version.
- **Derive the behaviour from a risk class.** Not lost — deferred. It cannot replace the explicit
  declaration because a class hides the choice it makes; it may be added as a shorthand once the
  explicit form has been used enough to know which shorthands are wanted.
