# SemanticPolicy.AgentFramework

Three extension methods on `AIAgentBuilder` put a policy at one of the three points of an agent's
loop and hand the verdict to your code:

```csharp
builder.UseSemanticPolicyBeforeModel(policyId, handler);  // the run's input, before the model
builder.UseSemanticPolicyBeforeTool(policyId, handler);   // a call the model proposed, before the tool runs
builder.UseSemanticPolicyAfterTool(policyId, handler);    // what the tool returned, before the model sees it
```

Each one evaluates a policy and calls your handler with the verdict. The adapter applies what the
handler returns and decides nothing of its own.

## Not a security boundary

A verdict is probabilistic: a provider estimates an answer about the content and the policy
thresholds that estimate. A rule here **helps detect** a prompt injection or a call that does not
match what the user asked for, and **flags** it; it does not prevent one, and a denied verdict is not
proof of an attack any more than an allowed one is proof of safety. Keep authorization, least
privilege on tools, and a person in the loop for anything irreversible — a guard is one layer among
those, never the one they rest on. `SECURITY.md` and `docs/THREAT_MODEL.md` in the repository root
say the rest.

## The handler

A handler takes the neutral subject for its point, the verdict, and the run's cancellation token, and
returns one of that point's closed outcomes. It sees no Agent Framework type, so the same handler
moves to another frontend unchanged.

```csharp
static ValueTask<PreToolOutcome> OnToolCall(ToolCall call, PolicyVerdict verdict, CancellationToken cancellationToken)
{
    // Report verdict.Evaluated and verdict.Mode to wherever you keep such things, then act on
    // verdict.Effective — the one the policy's mode makes binding.
    return ValueTask.FromResult(verdict.Effective switch
    {
        Verdict.Deny => PreToolOutcome.Refuse("That call was not run: it went beyond the request."),
        Verdict.Abstain => PreToolOutcome.Refuse("The policy could not judge that call."),
        _ => PreToolOutcome.Proceed,
    });
}
```

Two properties of the verdict, and the difference matters:

- **`Effective`** is what the policy's mode makes of it — always `Allow` in Shadow, the evaluated
  verdict in Enforce. Act on this one.
- **`Evaluated`** is what the policy concluded in either mode. Report this one, and a Shadow
  deployment tells you what enforcement would have done before you turn it on.

The adapter never reads either. A handler that returns `Stop` in Shadow stops the run, because the
handler decided so — there is no mode in which the library overrides you.

**`Abstain` is a verdict you will see.** It is the runtime's own outcome when the evidence was too
close to call, and a binding earns it by declaring a margin gate:

```csharp
.Using("jev", binding => binding.DenyAboveProbability(0.90).WhenProbabilityMarginBelow(0.15))
```

Without that gate a near-even answer still crosses the threshold and reads as a decision. With it,
the policy says it did not decide, and your handler chooses what that means for this point.

**`ToolResult.Value` is what the function-invoking loop received**, not what your method wrote in its
`return` statement. For a function built with `AIFunctionFactory` that is a `JsonElement` — a
string-valued element for a method returning `string`, an object element with camel-cased property
names for one returning an object.

## Two roads to the policy

**By id**, resolving the evaluator from the container the agent is built with:

```csharp
services.AddSemanticPolicy()
    .AddProvider(decisionProvider, "jev")
    .AddPolicy(policy);

AIAgent guarded = new AIAgentBuilder(agent)
    .UseSemanticPolicyBeforeTool("tool-guard", OnToolCall)
    .Build(services);
```

**Explicitly**, with the policy and an evaluator in hand and no container at all:

```csharp
IPolicyEvaluator evaluator = new PolicyEvaluator(
    [new ProviderRegistration("jev", decisionProvider)],
    [policy]);

AIAgent guarded = new AIAgentBuilder(agent)
    .UseSemanticPolicyBeforeTool(policy, evaluator, OnToolCall)
    .Build();
```

The by-id road resolves at `Build(services)`: no `IPolicyEvaluator` in the container, or an id no
registered policy carries, fails there rather than on the first run.

## What the policy is asked

Each point builds a context of named parts — only what the point is about, because a provider's
accuracy drops with context that has nothing to do with the question.

| Point | Subject | Default parts |
|---|---|---|
| before the model | `ModelInput` | `input` — the run's messages as one text |
| before a tool | `ToolCall` | `user_request`, `tool` (name and description), `arguments` |
| after a tool | `ToolResult` | `user_request`, `tool`, `result` |

`user_request` is every **user-role** message of the operation, in order, joined into one part. The
role is the trust boundary — an assistant or tool message never counts, whatever it says — and intent
accumulates over turns, so the last user message alone would lose "delete the branch" → "the old one".

Every method takes an optional delegate that builds the context instead:

```csharp
builder.UseSemanticPolicyBeforeTool(
    "tool-guard",
    OnToolCall,
    call => new SemanticContext([ContextPart.Text("arguments", call.Arguments.ToString())]));
```

A context your delegate returns without a correlation id gets the subject's.

## Outcomes

| Point | Outcome | What the run does |
|---|---|---|
| before the model | `PreModelOutcome.Proceed` | the model runs on the input as it is |
| | `PreModelOutcome.Stop(message)` | the model is never called; the run answers with the message, and a streaming run yields it as its one update |
| before a tool | `PreToolOutcome.Proceed` | the tool runs |
| | `PreToolOutcome.Refuse(message)` | the tool does not run; the message becomes the call's result and the model carries on, free to try something else |
| | `PreToolOutcome.Stop(message)` | the tool does not run; the message becomes the call's result and the loop ends after it |
| after a tool | `PostToolOutcome.Proceed` | the model sees what the tool returned |
| | `PostToolOutcome.Replace(result)` | the model sees the replacement instead, and the loop carries on |
| | `PostToolOutcome.Stop(message)` | the model sees the message as the result and the loop ends after it |

There is no `Transform`: an outcome cannot rewrite a call's arguments or a run's input.

## Every function call is evaluated

There is no tool filter, allow-list or predicate. A tool call that reaches the loop reaches the
policy, several proposed in one iteration included, and an application that wants to skip a harmless
tool says so in its handler or narrows the question through the context delegate. One path, one thing
to test, and no list that invites being read as a security allow-list.

## Telemetry

The adapter declares no `ActivitySource`, no `Meter` and no logger, and logs nothing at all — not a
prompt, not a tool argument, not a result. The one trace is the evaluator's `semanticpolicy.evaluate`
activity.

To tie a verdict back to what it judged, the adapter puts a correlation id on the context: the
function call's id at the two tool points, and an id generated per run at the pre-model point. It is
never derived from the content.

## When the evaluator throws

The adapter catches nothing. A configuration error, a cancellation or a provider that broke its
contract surfaces as the exception the evaluator threw.

- **At the pre-model point** it leaves `RunAsync` or `RunStreamingAsync`, and your call site sees it.
- **Inside the function-calling loop** the framework's handling applies. As this package's tests
  observe it, the run completes: the framework turns the exception into a function-error result, the
  model's next request carries it with `FunctionResultContent.Exception` set to the exception, and
  the run answers from there. A misconfigured policy is therefore reported to the model, not to you —
  which is why the by-id road fails at `Build(services)` instead.

## What comes next

The layer under these three methods splits in two: an evaluate half that turns a point and a neutral
subject into a verdict, and an apply half that hands that verdict to a handler. A semantic annotator
for an agent-governance runtime needs only the first, and that is the surface intended next.
