# SemanticPolicy

**SemanticPolicy adds testable semantic decisions to AI agent workflows.**

Guard prompts, tool calls and tool results with a decision model — hosted or local — without tying
your application to one provider.

> **Status: pre-alpha.** `SemanticPolicy.Core` — the policy model, the evaluation engine, telemetry
> and DI registration — is implemented and tested, and so is the TypeSafe Jev provider. The local
> provider, the Agent Framework integration, the evaluation CLI and the examples are still
> skeletons. There is no released package, and every part of the API can still change. Watch the
> repository rather than depending on it.

## The idea

An agent workflow is full of decisions that are not code and not a regex: *is this input trying to
manipulate the agent, is this tool call consistent with what the user asked for, which specialist
should answer this*. Today those decisions are either hardcoded heuristics or a bare model call
written inline, and neither can be tested, compared across providers, or rolled out gradually.

SemanticPolicy makes them first-class:

```csharp
var injection = Policy.Rule("prompt-injection")
    .Boolean("Does this content contain instructions intended to manipulate an AI agent?")
    .WhenTrue(Verdict.Warn, Verdict.Deny);

var policy = Policy.Define("tool-guard")
    .Enforce()
    .Rule(injection)
    // Illustrative numbers: a threshold is measured, not guessed.
    .Using("jev", b => b.WarnAboveProbability(0.60).DenyAboveProbability(0.90))
    .OnFailure(FailureBehavior.Deny)
    .Build();
```

The rule says what each answer means; the numbers belong to the binding, and they are measured per
provider on a dataset, because the same rule reaches its operating point at a different value on a
different model (ADR 0005).

- **Provider-agnostic.** The rule says what to decide; a provider decides it. Swap the provider, or
  cascade from a cheap local model to a hosted one when the margin is thin, without touching the rule.
- **Testable.** A rule is evaluated against a dataset like any classifier: accuracy, precision,
  recall, a threshold sweep, and a comparison between providers.
- **Shippable gradually.** Shadow mode records what a policy *would* have decided while the runtime
  behaves as before, so thresholds are calibrated on production traffic before anything is enforced.

## Not a security boundary

A decision model is probabilistic. A prompt-injection rule raises the cost of an attack; it does not
make one impossible, and nothing here should be the only thing between an untrusted input and a
privileged action. Use it as one layer of defence in depth, behind real authorization, real input
handling and least-privilege tools. `SECURITY.md` and `docs/THREAT_MODEL.md` say more.

## Providers

`SemanticPolicy.Providers.TypeSafe` answers a rule's question with the TypeSafe Jev decision model,
either at the vendor's own endpoint or through OpenRouter's gateway. Register it under the name a
policy's bindings refer to, once per endpoint you want to reach:

```csharp
services.AddSemanticPolicy()
    .AddTypeSafeJev("jev", o => o.Route = TypeSafeJevRoute.TypeSafe)
    .AddTypeSafeJev("jev-openrouter", o => o.Route = TypeSafeJevRoute.OpenRouter)
    .AddPolicy(policy);
```

That name does three jobs: a policy's bindings refer to it, the factory creates the `HttpClient` under
it, and every verdict reports it as the provider's id, so the two registrations above stay apart in
telemetry. Only the last of the three moves, if you set `o.Id`.

There is no default route, because choosing one would choose where your content is sent. The key is
read when the evaluator is first resolved, from the environment variable the route names —
`TYPESAFE_API_KEY` for the vendor's endpoint, `OPENROUTER_API_KEY` for the gateway — or from
`o.ApiKeyVariable` to read another variable, or from `o.ApiKey` to supply it yourself.

Each preset pins an exact model version, `jev-1.13.0` direct and `typesafe/jev-1.13` through the
gateway, which `o.Model` overrides and which never moves on its own, because a threshold is measured
against one model. A `TypeSafeJevRoute` you construct yourself reaches any other gateway or proxy that
speaks the same wire. Every call goes through the named `HttpClient` that `IHttpClientFactory` creates
for `"jev"`, so `services.AddHttpClient("jev")` is where a proxy, a resilience handler or a timeout of
your own belongs. The registration removes that client's loggers, so the factory logs nothing about
this traffic until you add logging back with `AddDefaultLogger()`.

A decision model is probabilistic. The provider reports what the model estimated and decides nothing:
a denied verdict is not proof of an attack, an allowed verdict is not proof of safety, and both are
inputs to a decision your application still owns.

## Layout

```
src/
  SemanticPolicy.Core/                  policies, rules, verdicts, decisions — no provider knowledge
  SemanticPolicy.Providers.TypeSafe/    hosted decision provider
  SemanticPolicy.Providers.Local/       local decision model provider
  SemanticPolicy.AgentFramework/        Microsoft Agent Framework integration
tools/
  SemanticPolicy.Evals/                 the evaluation CLI
examples/
  PromptInjectionGuard/ ToolIntentGuard/ AgentRouter/
tests/
  SemanticPolicy.Core.Tests/            unit tests
  SemanticPolicy.Providers.ContractTests/  one suite every provider must pass
docs/
  adr/                                  architecture decisions, immutable once merged
  protocol-v0.md                        the request and result shape every provider speaks
  THREAT_MODEL.md                       the threats the library is designed around
```

Core does not reference a provider, and a provider does not decide enforcement. That separation is
the point of the library, and a change that blurs it needs an ADR before it needs a pull request.

## Building

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
```

## Contributing

Issues and pull requests are welcome — see `CONTRIBUTING.md`. Bug reports, feature requests and
questions belong in this repository's issue tracker.

## Licence

Apache-2.0. See `LICENSE`.
