# SemanticPolicy.Extensions.AI

Two extension methods on `ChatClientBuilder` put a
[SemanticPolicy](https://github.com/semanticpolicy/semantic-policy) policy around the
function-calling loop of any `IChatClient` built with Microsoft.Extensions.AI, and hand the verdict to
your code:

```csharp
builder.UseSemanticPolicyBeforeTool(policyId, handler); // a call the model proposed, before the tool runs
builder.UseSemanticPolicyAfterTool(policyId, handler);  // what the tool returned, before the model sees it
```

Each one evaluates a policy and calls your handler with the verdict. The guard applies what the
handler returns and decides nothing of its own.

```sh
dotnet add package SemanticPolicy.Extensions.AI --prerelease
```

## Quick start

A policy that reads every tool call, TypeSafe Jev through OpenRouter to answer it, and a handler that
refuses a call the policy denies:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Providers.TypeSafe;

Policy policy = Policy.Define("tool-intent")
    .Shadow() // record what the policy would do; switch to .Enforce() once its numbers are measured
    .Rule(Policy.Rule("intent")
        .Boolean("Is this tool call consistent with what the user asked for?")
        .WhenFalse(Verdict.Escalate, Verdict.Deny))
    .Using("jev", b => b.EscalateAboveProbability(0.60).DenyAboveProbability(0.90)) // illustrative numbers
    .OnFailure(FailureBehavior.Deny)
    .Build();

ServiceCollection services = new();
services.AddSemanticPolicy()
    .AddTypeSafeJev("jev", o => o.Route = TypeSafeJevRoute.OpenRouter) // reads OPENROUTER_API_KEY
    .AddPolicy(policy);
using ServiceProvider serviceProvider = services.BuildServiceProvider();

IChatClient guarded = new ChatClientBuilder(chatClient) // chatClient: the IChatClient your model package gives you
    .UseSemanticPolicyBeforeTool("tool-intent", OnToolCall)
    .UseFunctionInvocation()
    .Build(serviceProvider);

static ValueTask<PreToolOutcome> OnToolCall(ToolCall call, PolicyVerdict verdict, CancellationToken cancellationToken)
{
    // In Shadow, Effective is always Allow; Evaluated says what enforcing would have done.
    Console.WriteLine($"{call.Name}: evaluated {verdict.Evaluated}, effective {verdict.Effective}");
    return ValueTask.FromResult(verdict.Effective == Verdict.Deny
        ? PreToolOutcome.Refuse(
            $"The {verdict.PolicyId} policy did not read that call as part of the request, so it was not run.")
        : PreToolOutcome.Proceed);
}
```

Requests then go through `guarded` with the tools in `ChatOptions.Tools`, as they would through any
function-invoking client.

## Not a security boundary

A verdict is probabilistic: a rule here **helps detect** a prompt injection or a tool call that does
not match the request, and **flags** it. A denied verdict is not proof of an attack, and an allowed one
is not proof of safety. Keep a guard as one layer among authorization, least-privilege tools and a
person in the loop for anything irreversible —
[`SECURITY.md`](https://github.com/semanticpolicy/semantic-policy/blob/main/SECURITY.md) and
[`docs/THREAT_MODEL.md`](https://github.com/semanticpolicy/semantic-policy/blob/main/docs/THREAT_MODEL.md)
say more.

## Where the calls go

Write every `UseSemanticPolicy…` call **before** `UseFunctionInvocation()` on the same builder. When
the pipeline is built, each guard finds the `FunctionInvokingChatClient` below it and wraps the
function that client calls to run a tool. A guard with no function-invoking client below it — written
after `UseFunctionInvocation()`, or on a pipeline without one — fails at `Build` with an
`InvalidOperationException`, rather than leaving every call unchecked.

The guards run in the order their calls are written:

```csharp
IChatClient guarded = new ChatClientBuilder(chatClient)
    .UseSemanticPolicyBeforeTool("tool-intent", OnToolCall)   // 1. checks the call
    .UseSemanticPolicyBeforeTool("tool-scope", OnScope)       // 2. checks it next, if 1 proceeded
    .UseSemanticPolicyAfterTool("tool-result", OnToolResult)  // 4. checks what 3 returned
    .UseFunctionInvocation(configure: loop =>
        loop.FunctionInvoker = (context, cancellationToken) =>         // 3. your own invoker, if you have one
            context.Function.InvokeAsync(context.Arguments, cancellationToken))
    .Build(serviceProvider);
```

A before-tool guard that refuses or stops ends the chain there: the guards after it, your invoker and
the tool never run, and its message is the call's result.

### Your own invoker

All the guards run outside `FunctionInvokingChatClient.FunctionInvoker`. If your application sets
one, set it in `UseFunctionInvocation(configure: …)`, as above: the before-tool guards then run ahead
of it, and the after-tool guards check what it returned. Without one, the tool's own function runs.

**Never assign `FunctionInvoker` after `Build`.** The guards wrap the invoker that is there when the
pipeline is built; one assigned afterwards replaces them, and every call then runs unchecked, with no
error.

### A client you built by hand

A `FunctionInvokingChatClient` you construct yourself, with its options and your invoker already set,
goes into `new ChatClientBuilder(client)` with the guards on that builder, and the builder is built
**once**:

```csharp
FunctionInvokingChatClient loop = new(chatClient) { AllowConcurrentInvocation = true };
IChatClient guarded = new ChatClientBuilder(loop)
    .UseSemanticPolicyBeforeTool("tool-intent", OnToolCall)
    .Build(serviceProvider);
```

The builder holds that one instance, so every `Build` wraps its invoker again, and a second one would
put every guard in the chain twice.

## The handler

A handler takes what its point checked — a `ToolCall` before the tool, a `ToolResult` after it — with
the verdict and the request's cancellation token, and returns one of that point's fixed set of
[outcomes](#outcomes). None of these types comes from Microsoft.Extensions.AI, so a handler is plain
code you can unit-test without a chat client, and the same handler works on the Microsoft Agent
Framework integration.

Two properties of the verdict, and the difference matters:

- **`Effective`** is what the policy's mode makes of it — always `Allow` in Shadow, the evaluated
  verdict in Enforce. Act on this one.
- **`Evaluated`** is what the policy concluded in either mode. Report this one, and a Shadow
  deployment tells you what enforcement would have done before you turn it on.

The guard never reads either. A handler that returns `Stop` in Shadow stops the loop, because the
handler decided so — there is no mode in which the library overrides you.

**`ToolResult.Value` is what the function-invoking loop received**, not what your method wrote in its
`return` statement. For a function built with `AIFunctionFactory` that is a `JsonElement` — a
string-valued element for a method returning `string`, an object element with camel-cased property
names for one returning an object.

## Pass a policy id, or the policy itself

**A policy id**, as in the quick start: the evaluator and the policy come from the container passed
to `Build(serviceProvider)`. A container without an `IPolicyEvaluator` — or `Build()` with no
container at all — fails there with an `InvalidOperationException`, and an id no registered policy
carries with an `ArgumentException`, rather than on the first call.

**The policy itself**, with an evaluator in hand and no container at all:

```csharp
using SemanticPolicy.Providers; // ProviderRegistration and IDecisionProvider, beside the quick start's

// decisionProvider: any IDecisionProvider, such as new TypeSafeJevProvider(httpClient, options).
IPolicyEvaluator evaluator = new PolicyEvaluator(
    [new ProviderRegistration("jev", decisionProvider)],
    [policy]);

IChatClient guarded = new ChatClientBuilder(chatClient)
    .UseSemanticPolicyBeforeTool(policy, evaluator, OnToolCall)
    .UseFunctionInvocation()
    .Build();
```

## What the policy is asked

Each point builds a context of named parts — only what the point is about, because a provider's
accuracy drops with context that has nothing to do with the question.

| Point | Subject | Default parts |
|---|---|---|
| before a tool | `ToolCall` | `user_request`, `tool` (name and description), `arguments` |
| after a tool | `ToolResult` | `user_request`, `tool`, `result` |

The call is read from the loop's `FunctionInvocationContext` with `ToToolCall()`: the function's name
and description, the arguments as JSON, every message of the conversation as its role and its text,
and the function call's id. `user_request` is every **user-role** message, in order, joined into one
part; an assistant or tool message never counts, whatever it says.

Both methods take an optional delegate that builds the context instead:

```csharp
builder.UseSemanticPolicyBeforeTool(
    "tool-intent",
    OnToolCall,
    call => new SemanticContext([ContextPart.Text("arguments", call.Arguments.ToString())]));
```

A context your delegate returns without a correlation id gets the call's.

## Outcomes

| Point | Outcome | What the loop does |
|---|---|---|
| before a tool | `PreToolOutcome.Proceed` | the next guard, your invoker or the tool runs |
| | `PreToolOutcome.Refuse(message)` | the tool does not run; the message becomes the call's result and the model carries on, free to try something else |
| | `PreToolOutcome.Stop(message)` | the tool does not run; the message becomes the call's result and the loop ends after it |
| after a tool | `PostToolOutcome.Proceed` | the model sees what the tool returned |
| | `PostToolOutcome.Replace(result)` | the model sees the replacement instead, and the loop carries on |
| | `PostToolOutcome.Stop(message)` | the model sees the message as the result and the loop ends after it |

There is no `Transform`: an outcome cannot rewrite a call's arguments. When the loop ends on a
`Stop`, the response carries the message as the call's result and no further model turn.

## Every call is checked, in every mode

There is no tool filter or allow-list: every call the loop invokes reaches the policy, including
several proposed in one response, and each gets the outcome of its own verdict — also when
`AllowConcurrentInvocation` runs them in parallel. To skip a harmless tool, say so in your handler or
narrow the question with the context delegate.

The guard awaits the verdict before the loop moves on, **in Shadow as in Enforce**: it never reads the
policy's mode, so a Shadow policy costs the same latency as an enforced one. Each evaluation calls the
decision provider once for every rule and binding it tries; a policy's `Budget`, when set, caps how
long that may take, and its `OnFailure` says what running out means.

An application that wants a Shadow policy off the critical path does not use a guard for it. It reads
the call itself, inside its own invoker or wherever it sees the invocation, and evaluates without
waiting:

```csharp
loop.FunctionInvoker = (context, cancellationToken) =>
{
    ToolCall call = context.ToToolCall();
    Task<PolicyVerdict> verdict = evaluator.EvaluateAsync("tool-intent", call.ToSemanticContext());
    _ = RecordWhenDone(verdict); // your code: report verdict.Evaluated, and observe a failure
    return context.Function.InvokeAsync(context.Arguments, cancellationToken);
};
```

`ToToolCall()`, `ToSemanticContext()` and `IPolicyEvaluator` are the same pieces a guard is built
from, so the policy is asked exactly what a guard would have asked it.

## Functions that need approval

The loop decides approval before any guard runs. A call to an `ApprovalRequiredAIFunction` becomes a
`ToolApprovalRequestContent` in the response and is not invoked, so no guard sees it until the
approval comes back and the loop invokes it; and when one call in a response needs approval, every
call in that response waits with it. A guard cannot ask for an approval either: an application that
turns `Escalate` into a person's decision does so in its own code, around `ToToolCall()` and the
evaluator.

## Telemetry

The package declares no `ActivitySource`, no `Meter` and no logger, and logs nothing at all — not a
prompt, not a tool argument, not a result. The one trace is the evaluator's `semanticpolicy.evaluate`
activity, and the function call's id is the correlation id on the context, so the verdict ties back
to the call it judged. The id is never derived from the content.

## When the evaluator throws

The guard catches nothing, and the function-invoking loop's own handling applies. The loop turns the
exception into the call's result — "Error: Function failed." unless `IncludeDetailedErrors` is set —
and the model reads it and answers from there; the loop throws only after
`MaximumConsecutiveErrorsPerRequest` such failures in a row. A before-tool guard that throws leaves
the tool unrun. A misconfigured policy is therefore reported to the model, not to you — which is why a
guard given a policy id checks it at `Build(serviceProvider)` instead.
