# Changelog

Notable changes to the three packages, `SemanticPolicy.Core`, `SemanticPolicy.Providers.TypeSafe`
and `SemanticPolicy.AgentFramework`, which share one version. Versions follow
[Semantic Versioning](https://semver.org/); while the major version is 0, any release can change the
API.

## 0.1.0-alpha.1

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
