# 0021. A validation rule reports a flagged verdict as a failure by default

**Status:** Accepted
**Date:** 2026-09-30

## Context

`SemanticPolicy.FluentValidation` adds `.Semantic(...)` to FluentValidation's rule builder. A rule
asks a policy about a property and reports a flagged verdict as a validation failure, next to the
rules a validator already holds. It is the first integration that is not an agent framework.

[0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) fixed what an agent
integration does with a verdict: it never decides, and it has no default handler. A validation rule
sits somewhere else. It returns failures; the application reads the `ValidationResult` and decides
what a failure means. Writing the adapter raised seven questions, and each answer holds for the next
integration of this kind:

1. Does the library extend a validation framework or bring a rule model of its own?
2. Can a rule run synchronously?
3. What does a rule send to the policy by default?
4. Which verdicts fail a validation, and who can change that?
5. What does a failure carry, and in what words?
6. When does a configuration error show up, and what happens to an exception?
7. What does the package depend on, and where does classification go?

## Decision

1. **The package adapts FluentValidation and adds no rule model of its own.** `.Semantic(...)`
   extends `IRuleBuilder<T, TProperty>`. The extensions live in namespace `FluentValidation`, the
   namespace of the type they extend, as the agent integration's do
   ([0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) § 2), and the package is
   named after the framework it adapts. The public surface is four overloads of one method: a policy
   id or a `Policy`, each with or without a context delegate, each with an optional `severity:`. The
   validator behind them and its default mapping are internal. A rule names a policy defined with
   Core's builder and never defines one in place.

2. **The rule is asynchronous only.** `ValidateAsync` runs it; `Validate` throws FluentValidation's
   own `AsyncValidatorInvokedSynchronouslyException`. No path blocks on the evaluator. The
   validation's cancellation token reaches `EvaluateAsync`.

3. **By default a rule sends the property's text as one part named `text`, and a blank value passes
   without a call.** On a `string` property the context is `SemanticContext.FromText(value)`, whatever
   the property is called. A null, empty or whitespace value passes without asking the provider, as
   FluentValidation's own validators leave emptiness to `NotEmpty`. An overload takes
   `Func<T, SemanticContext>` for several fields or a property that is not a string. Its parts reach
   the policy in its order, and it skips nothing: a field it reads can be null, so the rules that
   catch that go before it, under `CascadeMode.Stop`.

4. **By default the effective verdict maps to a failure.** `Deny` fails the property at
   `Severity.Error` and `Escalate` at `Severity.Warning`; `Warn`, `Abstain` and `Allow` produce no
   failure. The mapping reads `PolicyVerdict.Effective`, so a policy in Shadow never fails a
   validation. A `Func<PolicyVerdict, Severity?>` passed as `severity:` replaces the mapping. It sees
   the whole verdict, `Evaluated` and the rules included, and null means no failure.

   This is not the default [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md)
   refuses. An agent integration applies the outcome itself, so a default handler would stop a run or
   refuse a tool call: an enforcement choice that
   [0001](0001-semantic-decision-runtime-boundary.md) § 3 leaves to the application. A validation rule
   only reports. The application decides whether a failure rejects the request, goes to a person or is
   only logged, so a default mapping enforces nothing. An integration that acts on a verdict takes a
   handler from the application; one that only reports may map `Effective` by default.

5. **A failure says the value was flagged, and carries the verdict.** The default message is
   `'{PropertyName}' was flagged by semantic policy '{PolicyId}'.` It never says "invalid", "blocked"
   or anything else that presents the verdict as certain
   ([0001](0001-semantic-decision-runtime-boundary.md)). The failure's `CustomState` is the
   `PolicyVerdict`. Its `ErrorCode` is `SemanticPolicyValidator`, the validator's name, as
   FluentValidation names its own; the code is fixed and stays stable once released. The rule adds two
   placeholders: `{PolicyId}`, and `{Verdict}`, the name of `Evaluated`, so a Shadow report names the
   verdict the policy reached. `.WithMessage()`, `.WithSeverity()`, `.WithState()` and
   `.WithErrorCode()` replace what they name; whether a failure exists stays the mapping's.

6. **Configuration errors show up at the first `ValidateAsync`, and exceptions pass through.** An id
   no registered policy carries throws the evaluator's `ArgumentException` when the rule first runs.
   The `Policy` overloads throw `PolicyConfigurationException` for a provider the evaluator lacks.
   Neither is caught or turned into a validation failure, and a cancelled validation throws
   `OperationCanceledException`. A provider that fails or times out throws nothing: the policy's
   `OnFailure` turns that into a verdict, which the rule maps like any other. Core gains no policy
   lookup for this.

7. **The package depends on FluentValidation and Core only, and classification is not its job.** It
   has no reference to ASP.NET Core and no endpoint filter; the application calls `ValidateAsync` and
   turns the result into its answer. It declares no `ActivitySource` and no `Meter`: spans and
   metrics are the evaluator's ([0008](0008-telemetry-and-content-logging.md),
   [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) § 4). A validator answers
   valid or not and never returns a label, so choosing one of several labels outside an agent is
   `EvaluateAsync` with a Choice rule, documented in `docs/classification.md`, with no API of its own.

## Consequences

- An existing validator takes a semantic rule in one line, and its author learns no second library.
  The price is FluentValidation's model: one error code per rule, every severity counted against
  `IsValid`, and asynchronous rules only through `ValidateAsync`.
- The application still decides, and it has to read `Severity` to do so: an `Escalate` fails
  `IsValid` as a `Deny` does. An endpoint that answers every invalid result with a 400 rejects a
  ticket a person should have checked. The example answers 400 only when a failure is an `Error`;
  the library cannot make an application do the same.
- Two integrations now differ: an agent integration has no default and a validation rule has one. A
  third chooses by the test at the end of decision 4.
- A misspelt policy id surfaces on the first validation, not at startup. A test that runs the
  validator once catches it; an application without one finds it in production. Whoever wants the
  check at construction passes the `Policy`.
- The part name `text` is a key in the context's JSON form, so labelled examples recorded for a
  one-field rule stay valid when the property is renamed. A context delegate's part names are the
  application's, with [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md)'s
  consequence: renaming one makes recorded data stale.
- The error code and the default message are public contract. Changing either breaks applications
  that match on them.
- An ASP.NET Core application writes a few lines to turn the result into a response. An endpoint
  filter, if one is ever asked for, is a separate package.
- A classification outside agents is written on Core by hand, once per application.

## Alternatives considered

- **A rule builder of the library's own.** Lost because it is a second validation framework to write,
  and its users would have to switch to it.
- **A rule model of the library's own, with a FluentValidation adapter on top.** Lost because it is
  the most work, for a generality nothing asks for yet.
- **A DataAnnotations attribute.** Lost because `ValidationAttribute.IsValid` is synchronous and the
  evaluator is not.
- **A package name such as `SemanticPolicy.Rules`.** Lost because it hides the framework people search
  for.
- **A synchronous fallback that blocks on the evaluator.** Lost because it holds a thread-pool thread
  for a model call and invites deadlocks, the sync-over-async that FluentValidation itself throws on.
- **An inline question, `.Semantic("Is this a refund request?", …)`, building a one-rule policy in
  place.** Lost because [0007](0007-per-policy-failure-behaviour.md) leaves a policy no defaults: the
  call would take a provider, thresholds, a mode and a failure behaviour, and end up longer than a
  separate definition. A policy has one way to be defined, Core's.
- **A part named after the property.** Lost because renaming the property would make recorded data
  stale.
- **A context delegate on every rule.** Lost because the commonest case, one text property, would be
  the longest to write.
- **Asking the provider about a blank value.** Lost because it is a paid call about nothing, and a null
  text throws.
- **Failing a blank value.** Lost because it duplicates `NotEmpty`.
- **A mandatory mapping delegate, as
  [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) requires a handler.** Lost
  because the rule only reports, a FluentValidation rule is expected to fail on its own, and every
  rule would repeat the same lines.
- **Only `Deny` fails, with no override.** Lost because an `Escalate` would disappear silently.
- **A per-verdict table such as `.Map(Verdict.Warn, Severity.Warning)`.** Lost because it cannot see
  `Evaluated` or the rules, so a Shadow `Deny` could not be reported.
- **A mandatory message.** Lost because every built-in FluentValidation validator has a default one,
  and the commonest rule would no longer fit on one line.
- **The verdict's name as the error code, with failures added through `AddFailure`.** Lost because
  FluentValidation builds one code per rule, and `AddFailure` silently ignores `.WithMessage()` and
  every other `.With…()`.
- **`ValidatorOptions.Global.OnFailureCreated`.** Lost because it is global state of the host
  application, with one hook per application.
- **`{Verdict}` as the effective verdict.** Lost because it is always `Allow` in Shadow, so a Shadow
  report would name the wrong verdict.
- **A policy lookup on `IPolicyEvaluator`, so the id is checked when the validator is built.** Lost
  because it is a Core change for one integration, and a validator has no container to check against.
- **An evaluator exception turned into a validation failure.** Lost because an outage would read as a
  verdict about what the user typed.
- **An endpoint filter in this package.** Lost because every console or worker application would pull
  in ASP.NET Core, and FluentValidation's automatic validation cannot run asynchronous rules anyway.
- **A validator over a Choice rule that checks the user's selection against the model's.** Lost
  because it needs a mapping other than decision 4's, and more public API.
