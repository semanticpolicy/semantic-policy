# 0022. The guard types live in Core, and the chat-client guards wrap the function-invoking loop

**Status:** Accepted
**Date:** 2026-10-02

## Context

[0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) put the guard layer's
subjects, outcomes and handlers in `SemanticPolicy.AgentFramework`, framework-neutral, and said they
"can move to another assembly without a source change". The guard that evaluates one policy over one
subject, the intervention point and the default contexts stayed internal to that package. It turned
down hooking in at the Microsoft.Extensions.AI layer for the first integration, and kept that open as
a separate integration on the same subjects and outcomes.

An application that calls its model through Microsoft.Extensions.AI without the Agent Framework has a
function-calling loop, `FunctionInvokingChatClient`, and no place to check a tool call. Writing that
integration, `SemanticPolicy.Extensions.AI`, raised six questions, and each answer holds for the
integrations after it:

1. Where do the shared types live, so that one integration does not pull in another?
2. What may an integration nobody has written yet, or an application, use to evaluate on its own?
3. How do two packages share the internal machinery when one of them references the other?
4. Where does a guard hook into a chat-client pipeline, and in what order do several run?
5. Which points and which outcomes does a plain chat client get?
6. How do the Agent Framework and Extensions.AI packages relate?

## Decision

1. **The guard layer's public types live in Core.** `ConversationMessage`, `ModelInput`, `ToolCall`,
   `ToolResult`, `PreModelOutcome`, `PreToolOutcome`, `PostToolOutcome` with their `…Kind` enums, and
   the handlers `PreModelHandler`, `PreToolHandler` and `PostToolHandler` move from
   `SemanticPolicy.AgentFramework` to `SemanticPolicy.Core`, unchanged and still in namespace
   `SemanticPolicy`. Core gains no dependency. This narrows
   [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) § 2, which placed them in
   the agent integration's assembly. The Agent Framework assembly drops them without type forwards:
   the move is source-compatible, and an assembly compiled against an earlier version that uses one of
   them is rebuilt.

2. **The public road to a verdict is the default context; the machinery stays internal.** `ModelInput`,
   `ToolCall` and `ToolResult` each gain `ToSemanticContext()`: the default context of
   [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) § 3, under the subject's
   correlation id. Code that wants a verdict without an integration builds the subject, calls
   `ToSemanticContext()` and calls `IPolicyEvaluator` itself. The guard, the intervention point and the
   code that applies an outcome to a framework's call stay internal.

3. **The machinery is linked source, compiled into each integration.** It lives in
   `src/Shared/Guards/`, `internal`, in namespace `SemanticPolicy.Guards`, and each project links its
   files one at a time, as the HTTP providers link their transport
   ([0017](0017-protocol-v0-servers-over-one-http-binding.md)). There is no `InternalsVisibleTo`
   between two packages. The Agent Framework assembly references the Extensions.AI assembly and both
   compile the same internal types, so no compilation may see both sets of internals: each test
   project is granted the internals of its own package only. A CS0436 warning, a source type
   conflicting with an imported one, is the symptom; it is fixed, never suppressed.

4. **The chat-client guards hang on `ChatClientBuilder` and wrap the loop's invoker at `Build`.**
   `UseSemanticPolicyBeforeTool` and `UseSemanticPolicyAfterTool` extend `ChatClientBuilder`, in
   namespace `Microsoft.Extensions.AI`, and are written before `UseFunctionInvocation()`. At `Build`
   each finds the `FunctionInvokingChatClient` below it through `GetService`, wraps its current
   `FunctionInvoker`, or the function's own invocation when none is set, and returns the client it was
   given.
   - The guards nest in the order written, outside an invoker the application sets in
     `UseFunctionInvocation(configure: …)`. Before-tool handlers run first to last. After-tool handlers
     run last to first, each checking what the stages inside it returned, a replacement included.
   - `Build` fails when a guard finds no function-invoking client below it. It also fails when a
     builder over a client the application constructed is built a second time, because wrapping that
     one client again would run every guard twice.
   - A policy is passed by id, resolved from the container given to `Build`, or together with its
     evaluator. The by-id road fails at `Build` as
     [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) § 5 requires.
   - `ToToolCall()` on `FunctionInvocationContext` maps a proposed call, the same way in both packages.

5. **A plain chat client gets the two tool points and 0012's outcomes, and no approval outcome.** There
   is no pre-model point: an `IChatClient` is stateless and receives the whole history on every call,
   so "the input" has no natural definition. The outcome sets are
   [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md)'s, unchanged. The loop
   decides approval from the function's type before any invoker runs, so a guard cannot ask for one;
   an application that turns `Escalate` into a person's decision does so through decision 2. As in
   0012 § 1, § 4 and § 6, the guards read no mode and await the verdict in every mode, declare no
   telemetry of their own, and check every call.

6. **The Agent Framework package stands on Extensions.AI.** It references
   `SemanticPolicy.Extensions.AI`, maps its function-calling middleware's calls through `ToToolCall()`
   and applies outcomes through the shared source. It keeps the pre-model point and the
   `AIAgentBuilder` methods, and its public API does not change. The tool points have one
   implementation.

## Consequences

- The next integration, such as a tool gateway or another agent framework, maps its calls onto Core's
  subjects and reaches the evaluator without referencing either integration package. An application's
  handlers move to it unchanged.
- Core's public API carries agent vocabulary: a model input, a tool call, a tool result. A reader who
  never builds an agent sees them beside policies and verdicts.
- An assembly compiled against `0.1.0-alpha.2` that uses a moved type fails to load against a later
  Agent Framework package until it is rebuilt. Source compiles unchanged.
- The guard source is compiled into two assemblies, one of which references the other. Adding
  `InternalsVisibleTo` between them, or granting one test project both packages' internals, breaks the
  build. A change to the shared source is tested by both packages' test projects.
- Neither package calls the other's internals, so upgrading Extensions.AI alone cannot break the Agent
  Framework package at run time through a changed internal.
- The chat-client guards need Microsoft.Extensions.AI's own loop. An application with a tool loop of
  its own builds a `ToolCall` and calls the evaluator itself.
- An invoker the application assigns after `Build` replaces the guards without an error. The package
  documentation says to set it in `configure`; the library cannot detect the assignment.
- A call to a function that needs approval reaches no guard until the approval comes back, and when
  one call in a response needs approval, every call in it waits.
- Every tool call costs one call to the decision model, as in
  [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md).

## Alternatives considered

- **A `SemanticPolicy.Guards` package for the shared types.** Lost because it is one more package to
  version and publish for about fifteen types.
- **The shared types inside `SemanticPolicy.Extensions.AI`.** Lost because an integration that is not
  built on Microsoft.Extensions.AI would pull in all of it.
- **Type forwards from the Agent Framework assembly.** Lost because they serve only an assembly
  upgraded without a rebuild, and an upgrade of a prerelease through NuGet recompiles.
- **Public per-point guard classes, or the generic guard made public.** Lost because they freeze a
  larger surface and add only the correlation id and the policy lookup.
- **`InternalsVisibleTo` from Extensions.AI to the Agent Framework package.** Lost because upgrading
  Extensions.AI alone could then break the Agent Framework package at run time on a changed internal,
  unless one pinned the other's exact version.
- **A public building block in Extensions.AI, such as `BeforeToolAsync(context, next, …)`.** Lost
  because it is the public machinery decision 2 declines.
- **A namespace per assembly for the shared files, or `#pragma warning disable CS0436`.** Lost because
  the first gives one source two names, and the second hides which copy a test exercises.
- **Extension methods on `FunctionInvokingChatClient`, called inside
  `UseFunctionInvocation(configure: …)`.** Lost because `configure` gets no container, so a policy id
  could not be checked at build; the last call would run first; and an invoker assigned after it would
  discard it without an error.
- **A `FunctionInvokingChatClient` subclass that overrides `InvokeFunctionAsync`.** Lost because it
  collides with an application's own subclass or invoker.
- **A `DelegatingChatClient` below the loop that reads the function calls in the model's response and
  the results in the next request.** It does not depend on the loop and leads towards native
  approval. Lost for now because it rewrites messages and needs a spike first.
- **Resolving the policy id on the first call, from `FunctionInvocationContext.Arguments.Services`.**
  Lost because a configuration mistake would reach the model as "Error: Function failed." and throw
  only after `MaximumConsecutiveErrorsPerRequest` failures in a row, against
  [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) § 5.
- **An explicit evaluator only.** Lost because it would differ from the Agent Framework adapter and
  from Core's registration by id.
- **A pre-model point over the user messages since the last assistant reply.** Lost because nobody
  has asked for it, and the definition is a guess about what an application means by "the input".
- **A `RequireApproval` outcome, with a client below the loop that rewrites the call into an approval
  request.** Lost because it is uncertain work for a need nobody has raised.
- **A fire-and-forget observer for Shadow, or not awaiting the verdict in Shadow.** Lost because the
  first is a second path to test, and the second makes the integration read the mode, against
  [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) § 1.
- **The Agent Framework package on Core alone, with its own mapping and middleware.** Lost because it
  is the same semantics in two implementations.
- **The names `SemanticPolicy.MicrosoftExtensionsAI` and `SemanticPolicy.AI`.** Lost because the first
  is awkward and the second reads as an AI layer of the library's own.
