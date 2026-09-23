# AGENTS.md

Instructions for an AI agent working in this repository, and a short orientation for a human doing
the same. Everything here is a fact about this repository; nothing here is a preference.

## What this is

SemanticPolicy evaluates semantic decisions — "is this input trying to manipulate the agent", "is
this tool call consistent with what the user asked for" — through a pluggable decision provider, and
makes those decisions testable. Pre-alpha: `src/SemanticPolicy.Core/`,
`src/SemanticPolicy.Providers.TypeSafe/` and `src/SemanticPolicy.AgentFramework/` are implemented and
tested, and the four examples run on them; `tools/SemanticPolicy.Evals/` is implemented and tested
but has no provider registered yet, so its `run` verb cannot call one; the local provider is a
skeleton that builds and does nothing yet.

## Layout

```
src/SemanticPolicy.Core/                    policies, rules, verdicts, decision requests and results
src/SemanticPolicy.Providers.TypeSafe/      hosted decision provider
src/SemanticPolicy.Providers.Local/         local decision model provider
src/SemanticPolicy.AgentFramework/          Microsoft Agent Framework integration
tools/SemanticPolicy.Evals/                 evaluation CLI
examples/                                   four demos: PromptInjectionGuard, ToolIntentGuard, ToolResultGuard, AgentRouter
tests/SemanticPolicy.Core.Tests/            unit tests
tests/SemanticPolicy.Providers.ContractTests/  the suite every provider must pass
tests/SemanticPolicy.AgentFramework.Tests/  the adapter's tests, on a scripted model and provider
tests/SemanticPolicy.Evals.Tests/           the evaluation CLI's tests, on a scripted provider
docs/adr/                                   architecture decisions, immutable once merged
docs/protocol-v0.md                         the language-neutral request/result shape
docs/THREAT_MODEL.md                        the threats the library is designed around
```

Solution file: `SemanticPolicy.slnx`. Target framework `net10.0`, set once in
`Directory.Build.props`. Package versions are managed centrally in `Directory.Packages.props` — add a
version there, never in a `.csproj`.

## Verification

Run the narrowest command that reads what you changed. All three must pass before a pull request.

| Paths | Command |
|---|---|
| anything under `src/`, `tools/`, `examples/`, `tests/` | `dotnet build` |
| `src/SemanticPolicy.Core/**` | `dotnet test tests/SemanticPolicy.Core.Tests/SemanticPolicy.Core.Tests.csproj` |
| `src/SemanticPolicy.Providers.**` | `dotnet test tests/SemanticPolicy.Providers.ContractTests/SemanticPolicy.Providers.ContractTests.csproj` |
| `src/SemanticPolicy.AgentFramework/**` | `dotnet test tests/SemanticPolicy.AgentFramework.Tests/SemanticPolicy.AgentFramework.Tests.csproj` |
| `tools/SemanticPolicy.Evals/**` | `dotnet test tests/SemanticPolicy.Evals.Tests/SemanticPolicy.Evals.Tests.csproj` |
| any `.cs` — whitespace and `.editorconfig` style only | `dotnet format --verify-no-changes` |

CI runs the same commands on a pull request into `main`. The examples need `OPENROUTER_API_KEY` and a
live model, so neither CI nor this table runs them; `examples/README.md` says how to run them by hand.

## Architecture rules

These are the constraints the library exists to hold. A change that breaks one is a design change,
and a design change needs an ADR in `docs/adr/` before it needs an implementation.

- **`Core` references no provider.** Not a type, not a package, not a `using`. A provider is reached
  through an abstraction `Core` owns.
- **A provider decides; it does not enforce.** It answers the question it was given and returns a
  result whose evidence carries its kind. Thresholds, verdict mapping, shadow versus enforce mode
  and what happens on a `Deny` belong to the policy.
- **A provider is replaceable.** Everything a provider must guarantee is expressed in the shared
  contract test suite; a new provider is finished when that suite passes against it.
- **A verdict is probabilistic and says so.** No API may present a decision as certainty, and no
  example may suggest a rule is a security boundary. `SECURITY.md` is the wording to match.

## Logging and privacy

Nothing logs content by default: not prompts, not tool arguments, not tool results, not anything a
user typed. Telemetry carries identifiers, latency, evidence kind and value, verdict, mode and
error — never the text that was judged. Debug content logging is opt-in, off by default, and
documented where it is introduced.

The same rule applies to tests, fixtures and example data: no real customer content, no real API
keys, no live endpoints.

## Code

- C# 13 on `net10.0`, nullable enabled, warnings are errors.
- File-scoped namespaces, braces always, `var` only where the type is already on the right-hand side.
  `.editorconfig` is authoritative and enforced in the build.
- Public API carries XML documentation. The documentation file is generated and warnings are errors,
  so a missing comment fails the build rather than the review.
- Prefer a small, obvious type over a clever one. This is a library other people have to reason about
  at the point where their agent is about to do something irreversible.

## Pull requests

- Conventional Commits in the title, under 70 characters. The type must match the change.
- Body says what changed, why, and the verification commands that were run with their results.
- No tool attribution: no `Co-Authored-By` trailer for an AI assistant and no "Generated with …" line
  in a commit message, a pull request description, an issue or a comment. Older commits that carry
  one are a mistake, not a convention.
- One concern per pull request. Base is `main` unless the pull request is part of a scope that is
  being integrated on its own branch, in which case the base is that branch — explicitly, never by
  default.
- Do not merge your own pull request unless asked to.

## A note on the planning side

If `.agents/repo-profile.md` exists in your checkout, read it before starting: it carries the branch
and issue model this repository is developed under. It is not part of the repository — a checkout
without it is normal, and nothing in this file depends on it.
