# 0010. Core decision runtime architecture, step function and provider boundaries

**Status:** Accepted
**Date:** 2026-09-22

## Context

Following [0001](0001-semantic-decision-runtime-boundary.md) through [0009](0009-normalization-layer-deferred-to-calibration.md),
the `core` scope implemented the foundational runtime for `SemanticPolicy.Core`: the protocol mirror,
rule definitions, fluent builders, the cascade step function, live evaluation, telemetry, and dependency
injection registration.

As the core library is completed, specific architectural constraints must bind future development in adjacent
scopes — namely decision providers (`SemanticPolicy.Providers.*`), contract tests, agent framework integrations
(`SemanticPolicy.AgentFramework`), and offline evaluation harnesses (`SemanticPolicy.Evals`).

The fundamental architectural requirements were:
1. Preserving strict provider decoupling: ensuring decision providers depend only on the wire protocol
   and capability contracts, without leaking policy definition DSLs or runtime evaluation logic.
2. Providing a pure, deterministic evaluation core that functions identically online in a live service and
   offline in an evals CLI without network I/O or mocking.
3. Defining clear ownership of thresholds and margin gates per provider binding rather than on the rule.
4. Handling concurrency, evaluation budgets, and timeouts cooperatively without leaking unobserved tasks.
5. Providing unambiguous, safe verdict semantics so applications running in shadow mode cannot accidentally enforce.

## Decision

**The core runtime establishes the following boundaries across providers, evaluation, and telemetry:**

1. **Provider package isolation**: Provider adapters compile against `SemanticPolicy.Protocol` and
   `SemanticPolicy.Providers`. They do not reference `SemanticPolicy` (the policy DSL) or the evaluation engine.
   Adapters do not emit OpenTelemetry activities or metric instruments; all telemetry is emitted centrally by
   `PolicyEvaluator` (CORE-01, CORE-20, P-01).
2. **Pure evaluation step function**: The cascade and aggregation engine is implemented as a pure function
   `PolicyEvaluation.Evaluate(Policy policy, IReadOnlyDictionary<AttemptKey, ProviderResult> attempts) -> EvaluationStep`
   (CORE-14, CORE-23). An `EvaluationStep` returns either a terminal `PolicyVerdict` or the next set of required
   attempts across rules and bindings. `PolicyEvaluator` uses this to drive lazy execution over network providers,
   while `SemanticPolicy.Evals` uses the exact same function to evaluate recorded traces offline without running
   network requests.
3. **Operating points are per rule per provider binding**: Rules define semantic meaning and verdict vocabularies
   (Boolean ladders, Choice option mappings, Score rungs) without numbers. Thresholds and margin gates live exclusively
   in the `RuleOperatingPoint` of a `ProviderBinding` (CORE-05, CORE-06, P-02).
4. **Cooperative evaluation budgets and observed completion**: A policy time budget is enforced cooperatively
   via a linked `CancellationToken` (P-17). The evaluator always awaits all dispatched attempts before returning;
   it never abandons running provider calls to avoid orphaned tasks and unobserved exceptions. Budget expiry surfaces
   as a synthesized `Failure(Timeout)` result with null model metadata.
5. **Canonical context flattening**: For providers declaring `structuredContext: false`, context parts are
   flattened into plain text exclusively via the deterministic algorithm in `SemanticContext.ToCanonicalText(JsonElement)`
   (CORE-18, P-05). Provider benchmarks in evals are guaranteed to evaluate model differences rather than divergent
   adapter-specific formatting.
6. **Effective vs. Evaluated verdicts in Shadow mode**: `PolicyVerdict` provides both `Effective` and `Evaluated`
   verdicts (CORE-12). In `Shadow` mode, `Effective` is unconditionally `Verdict.Allow`, ensuring consuming
   applications reading the effective verdict cannot inadvertently enforce a denial.

## Consequences

- Provider implementations remain minimal and focused: they take a `DecisionRequest` and return a `ProviderResult`,
  implementing `IDecisionProvider`. They do not manage thresholds, chains, or spans.
- Offline evaluations and replay tools (`tools/SemanticPolicy.Evals`) share the exact production evaluation semantics
  without duplicating cascade or threshold logic.
- Benchmarking across providers is consistent due to canonical context formatting.
- Providers are required to faithfully observe the `CancellationToken` passed to `DecideAsync`; the contract test
  suite in `providers` verifies this guarantee.
- Rule definitions cannot specify default thresholds; every provider binding must explicitly configure its operating
  points.

## Alternatives considered

- **Coupling `IDecisionProvider` with Policy DSL in a single root namespace.** Rejected because provider authors
  should not pull in policy definition types or builders.
- **Sequential rule evaluation in `PolicyEvaluator`.** Rejected because multi-rule policies would incur latency
  equal to the sum of all provider calls rather than the slowest call in the round.
- **Immediate deadline cutoff that abandons provider tasks on budget expiry.** Rejected because unobserved background
  tasks leak sockets, compute, and unhandled exceptions.
- **Thresholds authored on the `Rule` itself.** Rejected by [0005](0005-evaluation-and-threshold-ownership.md):
  thresholds are specific to a provider and its calibration.

---

*The following execution-scaffolding decisions from `.agents/tasks/archive/core/decisions.md` were not promoted as they
governed internal task sequencing and implementation details of scope `core` only:*
- `CORE-01`: initial scope boundaries for Core task decomposition.
- `CORE-19`, `P-06`, `P-07`: `System.Text.Json` converter implementation details for `DecisionValue` and `JsonElement`.
- `P-10`: internal exception mapping between `ArgumentException` and `PolicyConfigurationException`.
- `P-11`: test stack package version pinning policy during development.
- `P-12`: specific task file touch list.
- `P-15`, `P-16`: enum numeric ordering and polymorphic rule type discriminator JSON property attributes.
