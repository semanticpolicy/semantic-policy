# 0012. An agent integration evaluates a policy and the application's handler acts on the verdict

**Status:** Accepted
**Date:** 2026-09-23

## Context

`SemanticPolicy.AgentFramework` checks a policy at three points of a Microsoft Agent Framework agent:
before the model is called, before a tool call runs, and after a tool returns. It hooks in through
`AIAgentBuilder`, with agent-run middleware for the first point and function-calling middleware for
the other two.

It is the first integration, not the only one. Another agent framework, or a component that only
wants a verdict and runs nothing, needs the same three checks.
[0001](0001-semantic-decision-runtime-boundary.md) says enforcement is the application's code, and
[0007](0007-per-policy-failure-behaviour.md) makes every policy declare what happens on failure.
Neither says what an integration may do with a verdict once it has one. Writing the adapter raised
seven questions, and each answer holds for the integrations after it:

1. Who acts on a verdict: the integration or the application?
2. What does the application return, and what does it see?
3. What does the decision model read by default?
4. Does the integration add telemetry of its own?
5. When does a configuration error show up?
6. Can some tool calls skip the check?
7. What may an example's tools do?

## Decision

1. **The integration never decides.** Every `Use…` method takes a handler from the application, and
   there is no default. The handler gets the subject and the `PolicyVerdict` and returns an outcome.
   The adapter applies that outcome in every mode: it reads neither `Effective` nor `Evaluated`, and
   it never throws because of a verdict. A handler that returns `Stop` in Shadow has chosen to stop.
   The documented pattern is to act on `Effective` and report `Evaluated`.

2. **Subjects, outcomes and handlers are public and know nothing of the framework; the machinery is
   internal.**
   - Subjects: `ModelInput`, `ToolCall`, `ToolResult` and `ConversationMessage`.
   - Outcomes, a closed set per point, built with factories. Before the model: `Proceed` or
     `Stop(message)`. Before a tool: `Proceed`, `Refuse(message)`, where the message becomes the
     tool's result and the loop goes on, or `Stop(message)`, where the loop ends. After a tool:
     `Proceed`, `Replace(result)` or `Stop(message)`.
   - Handlers: `PreModelHandler`, `PreToolHandler` and `PostToolHandler`, asynchronous, each returning
     a `ValueTask` of its outcome.

   These live in namespace `SemanticPolicy` and reference no Agent Framework type. A handler written
   for one integration works with the next one, and the types can move to another assembly without a
   source change. The intervention point, the default contexts, the guard that evaluates one policy
   over one subject and the middleware are internal to `SemanticPolicy.AgentFramework`. The extension
   methods live in the namespace of the type they extend, `Microsoft.Agents.AI`, as Core's
   `AddSemanticPolicy` lives in `Microsoft.Extensions.DependencyInjection`.

3. **Default contexts are small and use fixed part names.**
   - Before the model: `input`.
   - Before a tool: `user_request`, `tool` (JSON with the name and the description) and `arguments`
     (JSON).
   - After a tool: `user_request`, `tool` and `result` (text when the tool returned a string, JSON
     otherwise).

   `user_request` is every user-role message of the operation, in order, joined into one text. The
   trust line is the role: the user on one side, the assistant and the tools on the other. What a user
   wants builds up over several turns, so one message is not enough. No default carries the whole
   conversation. Every `Use…` method takes a delegate that builds the context instead.

4. **The integration has no telemetry of its own.** The adapter declares no `ActivitySource` and no
   `Meter`; spans and metrics come from the evaluator ([0008](0008-telemetry-and-content-logging.md),
   [0010](0010-core-decision-runtime-architecture.md)). It sets the context's `CorrelationId` to the
   framework's call id at the two tool points, and to an id generated per run before the model, so a
   verdict can be matched to a tool call in the application's traces.

5. **Configuration errors show up at `Build(services)`; evaluator exceptions pass through.** A guard
   registered by policy id resolves `IPolicyEvaluator` and looks the policy up when the agent is
   built. A container with no evaluator throws `InvalidOperationException`; an id no registered
   policy carries throws `ArgumentException` naming it. The adapter does not catch exceptions the
   evaluator throws during a run. Inside the function-calling loop the framework turns them into a
   function error the model sees, not the developer, which is why the check happens at build time.

6. **Every tool call is checked.** There is no tool filter and no list of tools that always pass. An
   application that wants to skip a harmless tool does so in its handler, or narrows the context
   through the delegate.

7. **The tools in `examples/` are in-memory stubs.** Each returns canned data or prints what it would
   have done. None touches the disk, a shell or the network.

## Consequences

- A second integration maps its framework's types onto the same subjects and outcomes and reuses the
  internal guard. The application's handlers move to it unchanged.
- The application writes a handler for every point it uses, even one that only returns `Proceed`.
  That is deliberate friction, like the mandatory `OnFailure` of
  [0007](0007-per-policy-failure-behaviour.md), and it costs a few lines in every program.
- The library never words a refusal. Two applications may refuse the same tool call with different
  messages, and the library cannot make them consistent.
- The part names are keys in the context's JSON form (`SemanticContext.ToJson()`), so labelled
  examples recorded for evaluation carry them. Renaming a part makes recorded data stale, and adding
  a part to a default changes what the decision model reads, so thresholds measured before it no
  longer apply.
- Every tool call costs one call to the decision model, in latency and in money. An application with
  many harmless tool calls pays for each of them until its handler or its context delegate narrows
  them.
- A configuration error is caught before the first run. A provider that fails during a run follows
  the policy's `OnFailure`. Anything else the evaluator throws inside a tool call reaches the model as
  a function error.
- An example cannot be pasted into a real agent as it is: every tool has to be written. That is the
  intent.

## Alternatives considered

- **Hook in at the `Microsoft.Extensions.AI` layer instead, with a `DelegatingChatClient` and a
  `FunctionInvokingChatClient` subclass.** Lost because it is a different package and a heavier
  integration to maintain. It stays possible as a separate integration on the same subjects and
  outcomes.
- **A default handler the application can override.** Lost because the library would then ship an
  enforcement choice, which [0001](0001-semantic-decision-runtime-boundary.md) leaves to the
  application.
- **Observe only: the adapter records the verdict and never acts.** Lost because Enforce would then
  do nothing.
- **Give the handler the framework's own context (`FunctionInvocationContext`, `AgentSession`).**
  Lost because the handler would no longer move to another integration.
- **A `bool` from the handler.** Lost because the adapter would then write the refusal text, and
  "proceed" could not be told apart from "replace the result".
- **The adapter forces `Proceed` in Shadow.** Lost because the adapter would then be deciding, and a
  handler could not treat a Shadow verdict its own way.
- **Public typed guard classes (`ToolCallGuard` and so on) from the start.** Lost because they fix a
  larger public surface before a second integration shows what it needs.
- **Three `Use…` methods with no neutral layer under them.** Lost because the second integration
  would rewrite contexts and outcomes from scratch.
- **Synchronous handlers.** Lost because a handler that asks a person or a store would block the
  function-calling loop, and adding asynchronous overloads later doubles the surface.
- **Only the last user message as `user_request`.** Lost because "delete the branch" followed by
  "the old one" keeps only the second half.
- **An application-supplied selector for `user_request`.** Lost because there would be no default,
  and every program would repeat the same code.
- **The whole conversation in the default context.** Lost because irrelevant context lowers the
  decision model's accuracy.
- **An activity per intervention point, tagged with the tool name.** Lost because it would add a
  telemetry dimension through an integration instead of through
  [0008](0008-telemetry-and-content-logging.md).
- **Check the policy id at the `Use…` call.** Lost because the container does not exist yet.
- **Catch evaluator exceptions inside the loop and rethrow them after the run.** Lost because it is a
  second error path for a mistake the build-time check already catches.
- **An optional predicate, or a list of tools that skip the check.** Lost because it is a second path
  before anyone asked for one, and a list of tools that always pass reads like an authorization list,
  which `SECURITY.md` says a rule is not.
- **Real tools in the examples, such as a `read_file` on the working directory.** Lost because a
  reader copies the example and runs it on their own disk, with an injection in the data.
