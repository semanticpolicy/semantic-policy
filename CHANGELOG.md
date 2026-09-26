# Changelog

Notable changes to the packages `SemanticPolicy.Core`, `SemanticPolicy.Providers.SystemOne` (from
0.1.0-alpha.2), `SemanticPolicy.Providers.TypeSafe` and `SemanticPolicy.AgentFramework`, which share
one version. Versions follow [Semantic Versioning](https://semver.org/); while the major version is
0, any release can change the API.

## 0.1.0-alpha.2

The System One provider, for a decision model you run yourself.

- **`SemanticPolicy.Providers.SystemOne`.** New. Any server that answers TypeSafe's System One API at
  `/v1/systemone`, registered with `AddSystemOne`. It reports the server's numbers as a score unless
  you declare them a probability, refuses plain `http` to anything but a loopback host unless you
  allow it, and can refuse a context longer than a limit you set.
- **`SemanticPolicy.Providers.TypeSafe`.** Built on the System One provider, which it now brings
  along at exactly its own version.

In the repository, not in a package: the evaluation CLI gains a `local` binding for a Von server
beside `jev`, and its recordings of the smoke and router sets are answered by both. It builds a
provider only when a policy binds it, and a curve replays at most 101 candidates.
[Local setup](README.md#local-setup) starts Von, and [docs/local-models.md](docs/local-models.md)
has what a probe measured on it and on two other local servers.

## 0.1.0-alpha.1 - 2026-09-24

The first release: prerelease packages on NuGet, for .NET 10.

- **`SemanticPolicy.Core`.** Policies of Boolean, Choice and Score rules, each bound to one or more
  providers with thresholds on the evidence they return. `IPolicyEvaluator` turns an input into one
  of five verdicts: Allow, Warn, Abstain, Escalate or Deny. A policy runs in Shadow or Enforce and
  declares what happens when a provider fails; a margin gate moves a close call to the next binding
  and ends in Abstain after the last. Also the provider contract of
  [protocol v0](docs/protocol-v0.md), telemetry that carries no content, and `AddSemanticPolicy()`
  for dependency injection.
- **`SemanticPolicy.Providers.TypeSafe`.** The TypeSafe Jev decision model, at TypeSafe's own
  endpoint or through OpenRouter, registered with `AddTypeSafeJev`.
- **`SemanticPolicy.AgentFramework`.** `UseSemanticPolicyBeforeModel`, `UseSemanticPolicyBeforeTool`
  and `UseSemanticPolicyAfterTool` on `AIAgentBuilder`: each evaluates a policy and hands the verdict
  to your handler.

The repository also holds the evaluation CLI, `tools/SemanticPolicy.Evals`, and four examples;
neither is published as a package.
