![](https://raw.githubusercontent.com/semanticpolicy/semantic-policy/main/assets/icon.png)

# SemanticPolicy
[![NuGet][nuget-badge]][nuget] [![.NET 10][dotnet-badge]][dotnet] [![Licence: Apache-2.0][licence-badge]][licence]

**SemanticPolicy adds testable semantic decisions to .NET applications.**

Write a decision that no `if` or regex can make as a rule, let a decision model answer it, and
measure it on labelled examples, without tying your application to one provider. Use it in business
logic, around a model call, or inside an AI agent's loop.

> **Status: alpha.** `0.1.0-alpha.1` is the first release: the core library (policies, the evaluation
> engine, telemetry, DI registration), the TypeSafe Jev provider and the Microsoft Agent Framework
> integration, as prerelease packages on NuGet. The evaluation CLI runs from this repository; it and
> the four [examples][examples] call Jev through OpenRouter. Not yet: the local provider. Every part
> of the API can still change between alpha releases.

## The idea

Applications are full of decisions that are not code and not a regex: *is this ticket a refund
request, which specialist should answer this, is this input trying to manipulate an agent, is this
tool call consistent with what the user asked for*. Today those decisions are either hardcoded
heuristics or a bare model call written inline, and neither can be tested, compared across providers,
or rolled out gradually.

SemanticPolicy writes each one down as a rule: a question for a decision model, and what each answer
means.

```csharp
var injection = Policy.Rule("prompt-injection")
    .Boolean("Does this content contain instructions intended to manipulate an AI agent?")
    .WhenTrue(Verdict.Warn, Verdict.Deny);

var policy = Policy.Define("tool-guard")
    .Enforce()
    .Rule(injection)
    // Illustrative numbers.
    .Using("jev", b => b.WarnAboveProbability(0.60).DenyAboveProbability(0.90))
    .OnFailure(FailureBehavior.Deny)
    .Build();
```

A *binding*, the `.Using("jev", …)` line, names the provider that answers and holds the numbers for
it. The provider answers the question with a probability of *yes*, and
`WhenTrue(Verdict.Warn, Verdict.Deny)` turns it into a verdict in two steps: `Warn` at 0.60 or
above, `Deny` at 0.90 or above, `Allow` below 0.60. The numbers are illustrative: the same rule
needs different ones on a different model, so measure them per provider on labelled examples
([ADR 0005][adr-0005]).

`Allow`, `Warn` and `Deny` are three of the five verdicts. From least to most severe: `Allow`,
nothing found; `Warn`, worth knowing about; `Abstain`, could not decide; `Escalate`, for a person or
another system to decide; and `Deny`. No rule names `Abstain`: a rule ends there when a binding's
margin gate finds the answer too close to call and no binding is left to ask. A policy's verdict is
the most severe of its rules'. *Evidence* is what a provider returns with its answer, such as a
probability, or a score on the provider's own scale. A binding's thresholds and margin gate read it.

- **Provider-agnostic.** The rule says what to decide; a provider decides it. Swap the provider
  without touching the rule, or bind several in order and move on to the next when the *margin*, the
  gap between a provider's two likeliest answers, is too thin to call.
- **Testable.** The evaluation CLI measures a rule on labelled examples like any classifier:
  precision, recall, a threshold sweep and a comparison between providers. It registers one
  provider, Jev, so for now a comparison measures a single binding.
- **Shippable gradually.** Shadow mode records what a policy *would* have decided while the runtime
  behaves as before, so thresholds are checked against production traffic before anything is
  enforced.

## Not a security boundary

A decision model is probabilistic. A prompt-injection rule raises the cost of an attack; it does not
make one impossible, and nothing here should be the only thing between an untrusted input and a
privileged action. Use it as one layer of defence in depth, behind real authorization, real input
handling and least-privilege tools. [`SECURITY.md`][security] and
[`docs/THREAT_MODEL.md`][threat-model] say more.

## Install

The packages are prereleases, so `dotnet add package` needs `--prerelease`. All three target .NET 10.

```bash
dotnet add package SemanticPolicy.Core --prerelease                # policies, rules and the evaluator
dotnet add package SemanticPolicy.Providers.TypeSafe --prerelease  # the TypeSafe Jev provider
dotnet add package SemanticPolicy.AgentFramework --prerelease      # for a Microsoft Agent Framework agent
```

The provider and the Agent Framework package each depend on `SemanticPolicy.Core`, so either one
brings it along. The evaluation CLI is not a package yet: run it from a clone, as [Evals][evals]
shows.

The snippets on this page assume these `using` directives:

```csharp
using Microsoft.Agents.AI;                      // AIAgentBuilder and UseSemanticPolicyAfterTool
using Microsoft.Extensions.DependencyInjection; // ServiceCollection and AddSemanticPolicy
using SemanticPolicy;                           // Policy, Verdict, the evaluator and handler types
using SemanticPolicy.Evaluation;                // PolicyVerdict
using SemanticPolicy.Providers.TypeSafe;        // TypeSafeJevRoute
```

## Providers

`SemanticPolicy.Providers.TypeSafe` answers a rule's question with the TypeSafe Jev decision model,
at the vendor's own endpoint or through OpenRouter's gateway. It answers boolean, choice and score
rules, and returns a probability for the policy to threshold.

```csharp
ServiceCollection services = new(); // or the host's builder.Services
services.AddSemanticPolicy()
    .AddTypeSafeJev("jev", o => o.Route = TypeSafeJevRoute.OpenRouter) // reads OPENROUTER_API_KEY
    .AddPolicy(policy);
```

`TypeSafeJevRoute.TypeSafe` goes to the vendor's own endpoint instead, and reads `TYPESAFE_API_KEY`.
There is no default route, because choosing one would choose where your content is sent.

The name you register under does three jobs: a policy's bindings refer to it (`.Using("jev", …)`),
the factory creates the `HttpClient` under it, and every verdict reports it as the provider's id. Set
`o.Id` to report a different id; the other two jobs keep the name.

| Option | |
|---|---|
| `o.Route` | Required. `TypeSafeJevRoute.TypeSafe` or `TypeSafeJevRoute.OpenRouter`; construct your own to reach another gateway or proxy with the same request and response shape. |
| `o.ApiKey` | The key. Leave it unset and it is read from the environment variable the route names — `TYPESAFE_API_KEY` or `OPENROUTER_API_KEY` — or from the one `o.ApiKeyVariable` names. Read once, when the evaluator is first resolved. |
| `o.Model` | Overrides the model the route pins. A route pins a named version rather than a floating alias, because a threshold is measured against one model: `jev-1.13.0` direct, `typesafe/jev-1.13` through the gateway. |
| `o.Timeout` | How long one call may take, ten seconds by default. Past it the provider reports a timeout, and the policy's `OnFailure` decides what that means. |

Every call goes through the named `HttpClient`, so `services.AddHttpClient("jev")` is where a proxy
or a resilience handler of your own belongs. Leave that client's own `Timeout` infinite and use
`o.Timeout` instead: the provider keeps its own timer, and a shorter client timeout surfaces as a
cancellation the evaluator reads as a bug. The registration also strips that client's loggers, so no
log line can print the `Authorization` header; `AddDefaultLogger()` puts the factory's logging back
under your own redaction.

The provider reports what the model estimated and decides nothing; the policy decides what a
probability means. Ask it about a piece of text through the evaluator the registration adds:

```csharp
using ServiceProvider serviceProvider = services.BuildServiceProvider();
IPolicyEvaluator evaluator = serviceProvider.GetRequiredService<IPolicyEvaluator>();
PolicyVerdict verdict = await evaluator.EvaluateAsync("tool-guard", SemanticContext.FromText(input));
```

`verdict.Effective` is the verdict to act on, and acting on it is your application's job, not the
library's.

### Any System One server

`SemanticPolicy.Providers.SystemOne` sends a rule's question to any server that answers the System
One wire at `/v1/systemone`, such as a decision model you run yourself. It answers boolean, choice
and score rules, and returns the server's numbers for the policy to threshold.

```csharp
services.AddSemanticPolicy()
    .AddSystemOne("local", o =>
    {
        o.BaseUrl = new Uri("http://127.0.0.1:8000");
        o.Model = "von-1.2.0"; // the name your server expects
    })
    .AddPolicy(policy);
```

The registration name does the same three jobs as for TypeSafe, and the named `HttpClient` gets the
same treatment: its loggers stripped, its own timeout infinite, `o.Timeout` in charge.

| Option | |
|---|---|
| `o.BaseUrl` | Required. `https`, or plain `http` to a loopback host (`localhost`, `127.0.0.1`, `[::1]`). `http` to any other host needs `o.AllowInsecureHttp`. |
| `o.Model` | Required, with no default. Some servers pick a checkpoint by this name and others ignore it, so only you know what yours expects. A verdict reports the model the server names in its answer, or this one when it names none. |
| `o.Evidence` | `EvidenceKind.Score` by default; `EvidenceKind.Probability` only as a claim you make, below. No other kind. |
| `o.ApiKey` | Optional. Sent as a Bearer token when set. Leave it unset and the key is read from the environment variable `o.ApiKeyVariable` names, once, when the evaluator is first resolved; with neither, or with that variable unset, no `Authorization` header is sent. |
| `o.AllowInsecureHttp` | `false` by default. |
| `o.MaxContextLength` | Off by default. A context longer than this many characters is not sent, below. |
| `o.Path` | `/v1/systemone` by default. |
| `o.Timeout` | How long one call may take, ten seconds by default. Past it the provider reports a timeout, and the policy's `OnFailure` decides what that means. |

**Why a score, not a probability.** A server's number between 0 and 1 orders its answers, but
nothing says that 0.8 is right four times in five. So the provider reports it as a score on the
`systemone` scale, and a policy thresholds it with `WarnAboveScore`, `DenyAboveScore` and
`WhenScoreMarginBelow`. A boolean answer carries both ends, `true` and `false`, and the margin is the
distance between them. A policy written with probability thresholds fails when the evaluator is
resolved; it never reads a score as a probability ([ADR 0003][adr-0003]). Setting
`o.Evidence = EvidenceKind.Probability` (from `SemanticPolicy.Protocol`) reports the same numbers on
the `calibrated` scale. That is your claim that the server is calibrated, not the provider's: it
checks nothing, so measure calibration on your own data before you make it.

**Plain `http`.** Off loopback it sends your content, and your key if there is one, across the
network in clear text. `o.AllowInsecureHttp = true` says in code that someone decided that, for a
sidecar on a private network for example. Any scheme other than `http` or `https` is refused.

**Long contexts.** Some servers cut a long input without saying so, and an instruction past the cut
then scores like the text before it. With `o.MaxContextLength` set, a context whose canonical text is
longer is not sent: the provider reports a rejected input, and the policy's `OnFailure` decides.

## Guarding an agent

`SemanticPolicy.AgentFramework` asks a policy at three points of a Microsoft Agent Framework agent's
loop — before the model reads the input, before a tool the model chose runs, and after the tool
returns — and hands the verdict to a handler you write. The handler decides what happens; the library
does not. The agent comes from Agent Framework, and its chat model from a package you add yourself:
the examples use `Microsoft.Agents.AI.OpenAI`, pointed at OpenRouter.

```csharp
// agent: any AIAgent, such as chatClient.AsAIAgent(...); serviceProvider: the container built above.
AIAgent guarded = new AIAgentBuilder(agent)
    .UseSemanticPolicyAfterTool("tool-guard", OnToolResult)
    .Build(serviceProvider);

static ValueTask<PostToolOutcome> OnToolResult(
    ToolResult result, PolicyVerdict verdict, CancellationToken cancellationToken) =>
    ValueTask.FromResult(verdict.Effective == Verdict.Deny
        ? PostToolOutcome.Replace(
            $"The {verdict.PolicyId} policy did not pass this result on. Answer from what you already have.")
        : PostToolOutcome.Proceed);
```

`Effective` is always `Allow` while a policy runs in Shadow mode, and the evaluated verdict once it
enforces. [The adapter's README][adapter-readme] covers the three
points, what each one asks the policy, and every outcome a handler can return.

## Examples

Four programs, each a single `dotnet run` on TypeSafe Jev through OpenRouter. Set
`OPENROUTER_API_KEY` — one key covers both the chat model and the decision model — then:

```bash
dotnet run --project examples/PromptInjectionGuard   # an instruction planted in the user's input
dotnet run --project examples/ToolIntentGuard        # a tool call that does not match the request
dotnet run --project examples/ToolResultGuard        # an instruction planted in a tool's result
dotnet run --project examples/AgentRouter            # the same runtime routing support requests
```

Each security example runs twice, in Shadow and then in Enforce, and prints what the policy concluded
and what the application did about it. [examples/README.md][examples] says what each one
shows, what five live runs of it returned, where the rules get it wrong, and how long a check takes.

## Evals

`tools/SemanticPolicy.Evals` tells you how well a rule works on examples you labelled yourself: how
often it flags safe inputs, how often it misses bad ones, and which thresholds meet a goal such as
"deny must be right 95% of the time". `run` asks the providers once and saves their answers;
`report`, `sweep` and `compare` replay them without calling anything. The numbers hold for that
dataset only.

```bash
dotnet run --project tools/SemanticPolicy.Evals -- run --policy policy.json --dataset dataset.jsonl --record run.recording.jsonl
dotnet run --project tools/SemanticPolicy.Evals -- sweep --policy policy.json --dataset dataset.jsonl --recording run.recording.jsonl --deny min-precision=0.95
```

`run` calls Jev through OpenRouter, so it needs `OPENROUTER_API_KEY` and sends every dataset input
to that third party. A recorded run of the smoke set ships with the tool, so the other three commands
work on a fresh clone without a key. [Its README][evals-readme] explains the dataset format, the four
commands and how to read their output.

## Layout

```
src/
  SemanticPolicy.Core/                  policies, rules, verdicts, decisions — no provider knowledge
  SemanticPolicy.Providers.TypeSafe/    hosted decision provider — TypeSafe Jev
  SemanticPolicy.Providers.Local/       local decision model provider — skeleton
  SemanticPolicy.AgentFramework/        Microsoft Agent Framework integration
tools/
  SemanticPolicy.Evals/                 the evaluation CLI — runs on TypeSafe Jev
examples/
  PromptInjectionGuard/ ToolIntentGuard/ ToolResultGuard/ AgentRouter/
tests/
  SemanticPolicy.Core.Tests/            unit tests
  SemanticPolicy.Providers.ContractTests/  one suite every provider must pass
  SemanticPolicy.AgentFramework.Tests/  the adapter's tests, no key needed
  SemanticPolicy.Evals.Tests/           the evaluation CLI's tests, no key needed
docs/
  adr/                                  architecture decisions, immutable once merged
  protocol-v0.md                        the request and result shape every provider speaks
  THREAT_MODEL.md                       the threats the library is designed around
```

Core does not reference a provider, and a provider does not decide enforcement. That separation is
the point of the library, and a change that blurs it needs an ADR before it needs a pull request.

## Building

Requires the .NET 10 SDK, 10.0.300 or later (see `global.json`).

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
```

## Contributing

Issues and pull requests are welcome — see [`CONTRIBUTING.md`][contributing]. Bug reports, feature
requests and questions belong in this repository's [issue tracker][issues].

## Licence

Apache-2.0. See [`LICENSE`][licence].

[examples]: https://github.com/semanticpolicy/semantic-policy/blob/main/examples/README.md
[adr-0003]: https://github.com/semanticpolicy/semantic-policy/blob/main/docs/adr/0003-evidence-semantics.md
[adr-0005]: https://github.com/semanticpolicy/semantic-policy/blob/main/docs/adr/0005-evaluation-and-threshold-ownership.md
[security]: https://github.com/semanticpolicy/semantic-policy/blob/main/SECURITY.md
[threat-model]: https://github.com/semanticpolicy/semantic-policy/blob/main/docs/THREAT_MODEL.md
[evals]: https://github.com/semanticpolicy/semantic-policy#evals
[adapter-readme]: https://github.com/semanticpolicy/semantic-policy/blob/main/src/SemanticPolicy.AgentFramework/README.md
[evals-readme]: https://github.com/semanticpolicy/semantic-policy/blob/main/tools/SemanticPolicy.Evals/README.md
[contributing]: https://github.com/semanticpolicy/semantic-policy/blob/main/CONTRIBUTING.md
[issues]: https://github.com/semanticpolicy/semantic-policy/issues
[licence]: https://github.com/semanticpolicy/semantic-policy/blob/main/LICENSE
[licence-badge]: https://img.shields.io/badge/licence-Apache--2.0-blue
[nuget]: https://www.nuget.org/packages/SemanticPolicy.Core
[nuget-badge]: https://img.shields.io/nuget/vpre/SemanticPolicy.Core?label=NuGet
[dotnet]: https://dotnet.microsoft.com/download/dotnet/10.0
[dotnet-badge]: https://img.shields.io/badge/.NET-10-512BD4
