# Threat model

What SemanticPolicy is designed to help with, what it does about each threat, what stays with the
application, and which claims about it are defensible. `SECURITY.md` is the short version and the
wording to match; the records in `docs/adr/` carry the reasoning behind each mechanism named here.

The document is written for the person deciding where to put a rule in an agent loop and what to do
with its verdict. It rests on the library's one premise: a semantic decision is a **signal**, not an
authorization ([ADR 0001](adr/0001-semantic-decision-runtime-boundary.md)). Every threat below is
mitigated by the application, with the library supplying one input to that mitigation.

## Trust boundaries

**Trusted:** the application's own code, its policy definitions and configuration, the developer's
system instructions, and tool definitions the developer has vetted.

**Untrusted:** everything the agent reads at run time — user input, uploaded files, retrieved
documents, the tool calls a model proposes and their arguments, what a tool returns, and the response
of any remote service, including a decision provider.

**Engines, not authorities:** the agent's language model and the decision provider behind a rule.
Both are trusted to run; neither output is authoritative. A verdict is an input to a decision the
application owns, exactly as a model's proposed tool call is.

**The security boundary is the application's enforcement:** authorization, tool execution,
sandboxing, and the credentials it holds. SemanticPolicy runs inside that boundary and never moves
it. Nothing in the library grants, denies or revokes access, and no verdict is an access decision.

A rule sits at one of three points in an agent loop, and the same runtime serves a fourth kind of
question that has nothing to do with safety:

| Point | What is judged | Typical question |
|---|---|---|
| before the model sees input | user input, a document, a retrieved passage | does this content carry instructions aimed at the agent? |
| before a tool runs | the tool call the model proposed | is this call consistent with what the user asked for? |
| after a tool returns | the tool's result | does this result carry instructions aimed at the agent? |
| routing | any of the above | which specialist should handle this request? |

## Threats

Each entry says what the threat is, what a rule can contribute, what the application still has to
do, and what remains. "Flags" means a verdict of Warn or Deny that the application reads; the library
never acts on a verdict itself.

### Direct prompt injection

Instructions inside the user's own input that try to override the agent's system instructions.
**A rule can** flag content that reads as an attempt to manipulate an agent. **The application
still** keeps system instructions and user content in separate channels, constrains the format of
inputs, and requires a person for irreversible actions. **Residual:** a classifier misses subtle
injections and flags some legitimate requests; both rates are measured, never assumed.

### Indirect prompt injection

Instructions hidden in content the agent reads on the user's behalf: files, web pages, retrieved
passages, email. **A rule can** flag a passage that carries instructions before it reaches the model,
and flag a response that appears to have followed one. **The application still** treats retrieved
content as evidence and never as an instruction, and isolates the tools a retrieved document can
reach. **Residual:** an instruction encoded, split across passages or phrased as innocuous prose can
pass a classifier.

### Tool description poisoning

A tool's metadata — its description, its parameter documentation — carries instructions that shape
the agent's plan in every session that loads the tool. **A rule can** flag a proposed call that does
not follow from the user's request, whichever description led to it. **The application still** vets
tool descriptions at deployment, pins them, and inventories what is loaded. **Residual:** a poisoned
description that leads to plausible calls is not visible in the calls.

### Tool result poisoning

A tool returns a result with instructions in it: a field in a JSON document, a comment in a page, a
line in a log. **A rule can** flag a result that carries instructions before the model reads it.
**The application still** validates results against a schema, strips what the schema does not name,
and never lets a tool's result authorize the next call. **Residual:** the same evasions as indirect
injection.

### Actions beyond the user's intent

The confused deputy: the agent, holding its own privileges, is steered into using them for someone
else — deleting, sending, paying, exporting — through a chain of individually plausible steps.
**A rule can** flag a proposed action that is inconsistent with, or far more powerful than, what the
user asked for. **The application still** authorizes every action against the caller's own
permissions, issues narrow short-lived credentials per task, and asks a person before an
irreversible step. **Residual:** an ambiguous request ("clean up my inbox") makes the intended scope
genuinely unclear, and a rule is as uncertain as the request.

### Data exfiltration

Sensitive content leaves through a model output or a tool call: a summary that includes secrets, a
request that sends a document somewhere. **A rule can** flag content or calls that look like a data
dump. **The application still** runs its own output handling, limits what a tool can reach, and keeps
the most sensitive data away from the agent entirely. **Residual:** phrasing evades a classifier, and
leakage by accident looks like ordinary work.

### Evasion and threshold manipulation

An attacker who controls the input can shape the number a provider returns: rephrase until the score
drops under a threshold, or push a benign request over one so that a real user is blocked.
**The library** treats every number as evidence of a declared kind and never as a certainty
([ADR 0003](adr/0003-evidence-semantics.md)); a margin gate lets a policy abstain when the evidence is
too close to call; a threshold is chosen from measured operating points on the deployment's own data
([ADR 0005](adr/0005-evaluation-and-threshold-ownership.md)). **The application still** makes no
decision on a number alone and keeps its deterministic checks — schema, allow-list, authorization —
independent of any threshold. **Residual:** no classifier is robust to an adversary with unlimited
attempts; evaluation on adversarial data says how far it bends, not that it will not.

### Wrong verdicts in either direction

A false negative lets an attack through. A false positive blocks a legitimate request, and enough of
them teach the operator to switch the rule off. Both are threats. **The library** ships no recommended
threshold, reports both error rates whenever a rule is evaluated, requires every policy to name its
mode, and has every example start in Shadow, where the verdict is recorded and the application's
behaviour is unchanged ([ADR 0007](adr/0007-per-policy-failure-behaviour.md)). **The application
still** owns the asymmetry: what a missed injection costs against what a blocked request costs, per
rule. **Residual:** a dataset is never the production distribution.

### A compromised or silently changed provider

A hosted provider is breached, its model is replaced, or it starts returning different answers to
the same question. **The library** records the provider and model identifier on every result and
keeps the raw response for replay, so a change in behaviour is visible on a stored dataset; it treats
a provider's answer as untrusted input and validates it against the contract; and a policy can fall
back to a second provider of a different kind. **The application still** chooses providers with
retention and audit terms it accepts, and re-runs its evaluation set when a model version changes.
**Residual:** a provider that answers plausibly and wrongly is not detectable from one result.

### Provider outage and latency

A remote provider is slow or unreachable. **The library** keeps the outcome of the call apart from
the verdict of the policy: a timeout is neither `false` nor Allow nor Deny
([ADR 0006](adr/0006-failure-and-abstention-model.md)). Every policy declares what happens when the
provider does not decide — Allow, Deny, Escalate, or fall back to the next provider — and there is no
library-wide default to fall into. A policy can carry a time budget for the whole chain; expiry is
recorded as a timeout and goes through the same declaration. **The application still** picks the
behaviour per rule: Deny or Escalate on a security-sensitive path, Allow where the rule only
observes. **Residual:** a fail-closed rule on a flaky provider is an availability incident, and a
fail-open one is a window.

### Malformed provider responses

A provider returns something that does not match the contract, by fault or on purpose. **The
library** validates every result and turns a broken one into a `Malformed` failure that goes through
the policy's declared failure behaviour; it never partially trusts a response, and it never raises the
response body into an exception message or a log line. **Residual:** a provider that sends garbage at
volume is a denial of service against the rule, which the failure behaviour turns into whatever the
policy declared.

### Leakage through telemetry and logs

The content a rule judges is exactly the content an application least wants in a log store.
**The library** emits metadata only: policy, rule and provider identifiers, decision type, outcome and
failure kind, evidence kind and value, verdict, mode, latency. It never emits prompts, tool
arguments, tool results or a provider's raw output, and the raw output does not serialize when a
verdict is logged ([ADR 0008](adr/0008-telemetry-and-content-logging.md)). Content logging, if it is
ever added, is an opt-in with a name a reviewer can search for, not a log level. **The application
still** applies the same rule to its own logging around the call. **Residual:** an application that
logs the request it built is outside the library's reach.

### Sensitive data sent to a remote provider

A hosted provider receives the content it judges. **The library** makes the choice visible: which
provider runs which rule is a policy setting, so a rule can be bound to a provider that runs where the
content is allowed to go. **The application still** classifies its data, chooses a provider whose
retention terms fit, and keeps secrets out of the content it sends. **Residual:** the content of a
prompt is partly inferable from a well-chosen question even when the prompt itself is not sent.

### Configuration tampering

A threshold moved, a mode flipped from Enforce to Shadow, a rule removed. **The library** keeps a
policy as an immutable value that is validated when it is built and that serializes, so it can live
in source control and be diffed. **The application still** protects its configuration as it protects
its code, and alerts on change. **Residual:** whoever can deploy can change the policy.

## Out of scope

- **The models.** The library ships no model and trains none. Poisoning of a provider's training
  data is the provider's threat model; the library's answer is provider choice, contract validation
  and replaceability.
- **Caching and replay.** The library keeps no cache of decisions, so a replayed input is judged
  again. A cache added by the application inherits the application's replay risks.
- **Authentication, authorization, sandboxing, orchestration.** The library integrates with the
  systems that do these and does none of them.

## Invariants the library holds

- **A verdict is a signal, not an authorization.** No API grants or denies access.
- **A provider decides; it does not enforce.** Thresholds, modes and failure behaviour never reach a
  provider, and a provider never learns what a `Deny` means.
- **Every number carries its kind.** A score is not a probability, and the runtime never renames one.
- **Outcome and verdict are two axes.** A failure is carried as a failure until the policy's own
  declaration maps it; it is never `false`.
- **Every policy declares its failure behaviour.** There is no global fail-open or fail-closed.
- **Shadow cannot enforce by accident.** In Shadow the effective verdict is Allow while the evaluated
  verdict is recorded beside it.
- **Telemetry carries no content, and the raw response never serializes.**
- **The runtime does not choose thresholds.** They are measured on the deployment's data.

## Defaults for a deployment

| Policy kind | Failure behaviour | Mode to start in |
|---|---|---|
| security-sensitive: injection, tool intent, a tool result before a privileged action | Deny, or Escalate to a person | Shadow; Enforce once a dataset and production observation justify the threshold |
| business-semantic: routing, tagging, tone | Allow, or Fallback to a cheaper provider | Shadow or Enforce, by the cost of a wrong answer |
| observation only | Allow | Shadow |

In every kind, a low-evidence answer is the policy's Abstain, which outranks Warn and is never read
as Allow.

## Defence in depth: what stays with the application

- Authorization on every action, evaluated against the caller's permissions and independent of any
  verdict.
- Narrow, short-lived credentials per task; the agent never holds a broad token, and secrets never
  appear in the content a rule judges.
- An allow-list of tools with vetted, pinned descriptions.
- Sandboxed tool execution with the least network and filesystem reach the tool needs.
- Schema validation of tool arguments and results before and after a semantic rule.
- A person in the loop for anything irreversible, whatever the verdict.
- A policy engine, where one exists, that receives a verdict as an attribute and owns the decision.

## Language for claims

Safe: *helps detect*, *flags*, *provides a signal*, *evaluates alignment with user intent*, *records
what it would have decided*. Not safe: *prevents*, *blocks all*, *guarantees*, *ensures compliance*,
*authorizes*. "Flags tool calls that seem inconsistent with the user's request" is a claim this
library can stand behind; "blocks bad tool calls" is not, and no example in this repository makes it.

## Testing against this model

A deployment tests the whole chain, not the rule: a flagged input must also be stopped by the
application's own enforcement, and an evaluation run measures the rule alone. What to hold a rule
to, on the deployment's own data and on public adversarial sets used as evaluation data only:

- both error rates, per rule and per provider, at the operating point actually deployed;
- failure and abstention rates, reported separately from classification errors;
- behaviour on a timed-out, unavailable and malformed provider, per declared failure behaviour;
- a Shadow run compared with an Enforce run on the same data, to see what enforcement would block;
- a check that no test input — including synthetic personal data — appears in any emitted
  telemetry.
