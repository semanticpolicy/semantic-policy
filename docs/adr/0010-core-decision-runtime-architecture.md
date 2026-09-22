# 0010. The core runtime fixes six boundaries that the provider, integration and evaluation packages inherit

**Status:** Accepted
**Date:** 2026-09-22

## Context

`SemanticPolicy.Core` now exists: the protocol v0 mirror, rule and policy definitions with their
builders and validation, the evaluation of a policy over provider results, a live evaluator that
drives providers, telemetry, and dependency-injection registration.
[0001](0001-semantic-decision-runtime-boundary.md) through
[0009](0009-normalization-layer-deferred-to-calibration.md) were written before that code and say
what the library is. This record says what the code, now that it exists, fixes for the packages
built on top of it: provider adapters, the contract test suite, the agent-framework integration and
the evaluation CLI.

Six questions came up while the runtime was built, and each answer constrains code outside `Core`:

1. What may a provider adapter depend on, and what must it never see?
2. Can the evaluation logic run offline, on recorded results, without a second implementation?
3. Where do thresholds and margin gates live: on the rule, or on the binding of a rule to a provider?
4. What happens to a provider call that is still running when the policy's time budget expires?
5. When a provider cannot read structured context, who flattens it, and how?
6. Can an application running a policy in Shadow mode enforce its verdict by accident?

## Decision

1. **A provider adapter depends on the protocol and the provider abstraction, and on nothing else in
   `Core`.** It compiles against the `SemanticPolicy.Protocol` and `SemanticPolicy.Providers`
   namespaces — `DecisionRequest`, `ProviderResult`, `IDecisionProvider`, `ProviderCapabilities` —
   and does not reference the policy definitions, the builders or the evaluation engine. It emits no
   telemetry of its own, neither activities nor metric instruments: all telemetry comes from the
   evaluator, the one place that knows the policy, the rule and the outcome together.
2. **Evaluation is a pure step function.** `PolicyEvaluation.Evaluate(policy, attempts)` takes a policy
   and the provider results collected so far, and returns either a terminal `PolicyVerdict` or the
   next set of attempts it needs, keyed by rule and binding. It performs no I/O. The live
   `PolicyEvaluator` calls it in a loop and dispatches each requested attempt to a provider; an
   offline tool calls the same function on recorded results and gets the same verdicts without a
   provider.
3. **Operating points belong to the binding, not the rule.** A rule declares meaning: the question,
   the answers, and the verdict each answer maps to. Every number — the thresholds and the margin
   gate — lives in the `RuleOperatingPoint` of a `ProviderBinding`, because the same rule reaches its
   operating point at a different value on a different provider
   ([0005](0005-evaluation-and-threshold-ownership.md)). A rule carries no default threshold.
4. **The time budget is cooperative, and every attempt is awaited.** The budget is enforced through
   a linked `CancellationToken` handed to each provider call. When it expires, the evaluator still
   awaits every attempt it dispatched; it never abandons a running call. An attempt that had not
   answered is recorded as a synthesized `Failure(Timeout)` result whose metadata names the provider
   and no model, and it goes through the policy's declared failure behaviour
   ([0007](0007-per-policy-failure-behaviour.md)).
5. **Context is flattened by one algorithm.** An adapter whose provider does not read structured
   context declares so in its capabilities and renders an object or array context to text with
   `SemanticContext.ToCanonicalText`, which is deterministic and shared by every adapter. A
   comparison between two providers therefore compares models, not two adapters' ideas of formatting.
6. **Shadow cannot enforce.** `PolicyVerdict` carries both `Effective` and `Evaluated`. In Shadow
   mode `Effective` is always `Allow` while `Evaluated` records what the policy concluded, so code
   that reads the effective verdict cannot act on a Shadow policy's `Deny`.

## Consequences

- A provider adapter is small: take a `DecisionRequest`, return a `ProviderResult`, observe the
  cancellation token, declare capabilities. It never manages thresholds, chains or spans, and the
  contract test suite can hold every adapter to the same behaviour, including honouring cancellation.
- Evaluation tooling replays recorded results through the function the runtime uses, so a threshold
  sweep or a provider comparison cannot drift from what the runtime would have decided.
- Every binding declares its operating point explicitly. There is no "reasonable default" to reach
  for, and a policy with an unmeasured number in it looks like one.
- A slow provider cannot be cut off: a budget expiry waits for the call to observe cancellation. An
  adapter that ignores the token holds the evaluation open until its own transport times out, which
  is what the contract suite is there to catch.
- Telemetry is uniform across providers because no adapter adds its own, at the cost that an adapter
  cannot record transport-level detail as spans of its own.
- Canonical flattening is a fixed format. An adapter for a text-only provider uses it instead of a
  rendering of its own, so a provider that would do better with a different layout of structured
  context does not get one.

## Alternatives considered

- **One namespace for the protocol and the policy definitions.** Lost because an adapter author would
  pull the policy builders and the evaluation engine into every provider package, and the boundary
  of [0001](0001-semantic-decision-runtime-boundary.md) — a provider never sees a threshold — would
  hold only by convention.
- **Evaluate rules sequentially in the live evaluator.** Lost because a policy with several rules
  would take the sum of its providers' latencies instead of the slowest one in each round.
- **Cut off provider calls when the budget expires.** Lost because an abandoned task leaks its socket
  and its exception; the cost of waiting for cancellation to be observed is bounded by the adapter's
  own transport timeout.
- **Thresholds on the rule, with a per-provider override.** Lost by
  [0005](0005-evaluation-and-threshold-ownership.md): the value is a property of the provider and
  the dataset, and a default on the rule is a number nobody measured.
- **A single verdict in Shadow, with the mode as a flag beside it.** Lost because every reader of the
  verdict would have to check the flag, and the one who forgets enforces a Shadow policy.
