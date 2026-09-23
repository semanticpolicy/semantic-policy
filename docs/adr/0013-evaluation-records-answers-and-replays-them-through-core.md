# 0013. The evaluation tool records provider answers once and replays them through Core

**Status:** Accepted
**Date:** 2026-09-23

## Context

`tools/SemanticPolicy.Evals` measures a rule on labelled examples. `run` sends each example to the
providers a policy binds and records their answers; `report`, `sweep` and `compare` read the
recording and call no provider.

[0005](0005-evaluation-and-threshold-ownership.md) says what the tool must do: choose a threshold on
data against a stated constraint, show both error directions, failures and abstentions, and keep a
held-out split held out. [0010](0010-core-decision-runtime-architecture.md) says an offline tool
replays recorded results through the runtime's own step function. Neither says what a recording
holds, which changes a replay accepts, what a dataset row means, or what the tool needs from Core and
from a provider. Building the tool raised six questions, and each answer binds code outside it:

1. How does a sweep try a threshold without reading evidence itself?
2. What does a run ask, and what does it keep?
3. What may differ between the recorded policy and the replayed one?
4. What does a dataset row hold, and which part of it is the truth?
5. How is a row counted when no threshold decided it?
6. How does a provider reach the tool, and how are its latency and cost read?

## Decision

1. **The tool never reads evidence; it replays policy variants.** Each candidate threshold or gate is
   a copy of the policy with that one number changed, and every row is replayed through
   `PolicyEvaluation.Evaluate`. The complement of a one-sided probability, `Malformed` evidence, the
   margin gate and the failure behaviour mean in the tool what they mean at run time. A point on one
   rung's curve is a variant whose ladder holds that rung alone: `Policy.Validate` requires thresholds
   to increase with severity, so the rungs cannot share one candidate. A replay keeps only the rule
   being measured, because the step function returns no verdict until every rule it is given has its
   attempts.

2. **`run` asks every binding, and the recording keeps answers, not content.** Every row, rule and
   binding is one attempt, including those a cascade would have skipped, so a replay can move a gate
   or reorder the bindings. `Policy.Budget` is ignored, and the run says so. A per-attempt timeout
   becomes `Failure(Timeout)`, as an expired budget does in the live evaluator. The recording,
   `semanticpolicy/evals-recording/v0`, is JSON Lines:
   - a header with the full policy, each dataset's path, split and SHA-256 digest, each provider's
     registration name and model, and the tool version;
   - one line per row with its `id` and each attempt's `ProviderResult` as Core serializes it, keyed
     by rule id and then provider name.

   It holds no input, no label and no raw payload — Core never serializes `ProviderResult.Raw` — so a
   recording can be committed ([0008](0008-telemetry-and-content-logging.md)). A replay refuses a
   dataset whose digest differs unless `--force` is given.

3. **A replay accepts the same question with different numbers.** The rule must match the recorded
   one in everything the provider was shown:
   - every rule: id, type and question;
   - Boolean: criteria, flagged answer and ladder;
   - Choice: options, with key, description and verdict;
   - Score: levels and rungs.

   Thresholds, gates, the order and membership of bindings, `OnFailure`, `Mode` and `Budget` may
   differ. An attempt the replay needs and the recording lacks is an error, never a synthesized
   failure. An error names the rule and the field, never the question's text.

4. **A dataset row carries the rule's answer, and its input is a context.** A row is one JSON object
   per line with `id`, `input`, `label` and optional `metadata`. The label is an answer in the rule's
   vocabulary — `true` or `false`, an option key, a level name — or `ambiguous` or `abstain`. It is
   never a verdict, because a verdict depends on the operating point the sweep is choosing. Only
   `label` is truth; `metadata` is provenance and what `--where` filters on. A string `input` becomes
   `SemanticContext.FromText`. An object becomes one context part per property, in order: text for a
   string, JSON otherwise, the shape `SemanticContext.ToJson` produces. The policy under test is a
   `Policy` serialized with `SemanticPolicyJson`; the tool has no rule format of its own.

5. **A row is counted by the rule's verdict and where it came from.** Metrics read
   `RuleVerdict.Verdict`, never the policy's `Effective` verdict, so Shadow and Enforce give the same
   numbers. A row labelled `ambiguous` or `abstain` goes to its own bucket, whatever its verdict.
   Otherwise `RuleVerdict.Source` sorts it: `Threshold`, `OptionMap` and `LevelMap` enter the
   confusion matrix, `FailureBehavior` counts as a provider failure and `UncertaintyExhausted` as an
   abstention. A Boolean rule gets one binary matrix per ladder rung, "verdict at or above this rung"
   against "label is the flagged answer", so `Deny` has a precision of its own.

6. **A provider reaches the tool through its DI registration, and the tool reads its latency and cost
   without knowing it.** The tool builds a container with `AddSemanticPolicy()`, lets its entry point
   add providers to the builder, and resolves the `ProviderRegistration`s. A binding's provider id is
   a registration name. A policy that names an unregistered provider fails before the first call and
   lists the registered names. The tool has no provider configuration of its own: an adapter's
   settings and secrets come from wherever the adapter documents them. Latency is the p50 and p95 of
   `ProviderMetadata.LatencyMs` over every attempt. Usage is every top-level numeric property of
   `ProviderMetadata.Usage`, summed under the provider's own name for it and not interpreted.

## Consequences

- Core's JSON shapes are now kept on disk. A change to how `Policy` or `ProviderResult` serializes
  reaches recordings people have committed, and the tool then needs a new format version or a reader
  for the old one.
- A new `VerdictSource` value needs a bucket in the tool; until it has one, counting a row with it
  fails.
- A field added to a rule definition has to be sorted into the question or the numbers. The replay
  compares the question field by field, so a field it does not know about is allowed to differ.
- One recording answers every threshold, gate and binding order, with no provider and no key. The
  price is paid once, and it is higher than production's: rows × rules × bindings calls, where a
  cascade usually stops at its first binding.
- A dataset stays valid when a policy's numbers change, and it cannot carry an expected verdict.
- The digest is over the file's bytes, so a line-ending conversion at checkout changes it.
- An adapter gets cost columns by reporting numeric top-level fields under `usage`; a nested or string
  field shows no number. Latency is whatever the adapter reports in `LatencyMs`.
- A provider package is usable by the tool only once its registration is added to the tool's entry
  point.

## Alternatives considered

- **Sweep on the evidence values directly.** Lost because the tool would be a second reader of
  evidence that must agree with Core forever: complement, `Malformed`, gate and fallback included.
- **Set every rung to the candidate in one variant, or nudge the other rungs by an epsilon.** Lost
  because `Policy.Validate` rejects the first, and an epsilon is invalid at 1.0 on a probability and
  meaningless on a score scale.
- **Record through the lazy evaluator and honour `Budget`.** Lost because a cascade recorded lazily
  cannot be replayed under another gate, and a recorded `Timeout` would mean something other than it
  means in production.
- **Copy `input` and `label` into the recording.** Lost because the recording would carry content.
- **Key attempts by Core's `AttemptKey`.** Lost because it holds a binding index, so a replay that
  reorders the bindings could not find its attempts.
- **Let a replay change thresholds and gates only.** Lost because comparing `Deny` with a fallback on
  the same answers would need a new live run.
- **Replay any policy, with a warning when the question changed.** Lost because the numbers would
  mean nothing, behind a warning nobody reads.
- **Label rows with verdicts, or with an answer and an `expected_verdict`.** Lost because a verdict
  label writes a threshold into the data, and two truths per row give two sets of metrics.
- **String-only `input`.** Lost because a multi-part guard would be flattened in the dataset
  differently from the runtime. An object for every row lost because a single text would be wrapped
  for nothing.
- **Rule and provider as command-line flags, or an evaluation spec of the tool's own.** Lost because a
  ladder, criteria and gates do not fit in flags, and a second rule format would have to be translated
  into a `Policy` before every replay.
- **Count a failure-behaviour `Deny` as a prediction.** Lost because an outage would look like recall.
- **Drop ambiguous rows.** Lost because that is how a hard dataset is made to look easy.
- **Report the top rung only, or one flagged-versus-not matrix.** Lost because the first loses Warn
  and the second loses the precision of Deny.
- **A `providers.json` mapped to adapters, or adapters loaded by reflection.** Lost because the first
  is a second configuration format per adapter, and the second makes configuration, disposal and load
  errors the tool's problem.
- **Map each provider's usage fields in the tool.** Lost because the tool must work with any
  `IDecisionProvider`.
