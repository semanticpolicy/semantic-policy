# SemanticPolicy.FluentValidation

Semantic rules for [FluentValidation](https://docs.fluentvalidation.net/). `.Semantic(...)` asks a
[SemanticPolicy](https://github.com/semanticpolicy/semantic-policy) policy about a property, such as
whether a support ticket is about support at all, and reports a flagged verdict as a validation
failure, next to the rules you already write.

```sh
dotnet add package SemanticPolicy.FluentValidation --prerelease
```

## Register a policy and the evaluator

A policy asks a provider a question and turns the evidence in its answer into a verdict. Register the
provider, the policy and the evaluator once, at startup. This one asks TypeSafe Jev through OpenRouter,
from the `SemanticPolicy.Providers.TypeSafe` package, which reads its key from `OPENROUTER_API_KEY`:

```csharp
using SemanticPolicy;
using SemanticPolicy.Providers.TypeSafe;

builder.Services.AddSemanticPolicy()
    .AddTypeSafeJev("jev", options => options.Route = TypeSafeJevRoute.OpenRouter)
    .AddPolicy(Policy.Define("off-topic")
        .Enforce()
        .Rule(Policy.Rule("not-support")
            .Boolean("Is this text about something other than a request for support?")
            .WhenTrue(Verdict.Warn, Verdict.Escalate, Verdict.Deny))
        .Using("jev", binding => binding
            .WarnAboveProbability(0.5)
            .EscalateAboveProbability(0.7)
            .DenyAboveProbability(0.9))
        .OnFailure(FailureBehavior.Escalate)
        .Build());
```

The thresholds are illustrative. Choose yours from measured precision and recall on your own data.

## Add semantic rules to a validator

The validator takes the evaluator in its constructor. A rule on one text property sends that text; a
rule with a context delegate sends the parts the delegate builds, in its order, whatever the property's
type:

```csharp
using FluentValidation;
using SemanticPolicy;

public sealed record SupportTicket(string Category, string Description, string? Notes);

public sealed class SupportTicketValidator : AbstractValidator<SupportTicket>
{
    public SupportTicketValidator(IPolicyEvaluator evaluator)
    {
        RuleFor(ticket => ticket.Notes).Semantic(evaluator, "off-topic");

        RuleFor(ticket => ticket.Description)
            .NotEmpty()
            .Semantic(evaluator, "off-topic", ticket => new SemanticContext(
            [
                ContextPart.Text("category", ticket.Category),
                ContextPart.Text("description", ticket.Description),
            ]));
    }
}
```

A one-field rule passes a null, empty or whitespace value without asking the provider. Register the
validator like any other, for example with
`builder.Services.AddSingleton<IValidator<SupportTicket>, SupportTicketValidator>()`.

## Validate asynchronously

A semantic rule asks a provider over the network, so validate with `ValidateAsync`:

```csharp
ValidationResult result = await validator.ValidateAsync(ticket, cancellationToken);
```

`Validate` on a validator that holds a semantic rule throws `AsyncValidatorInvokedSynchronouslyException`.
The token reaches the provider, and cancelling it throws `OperationCanceledException`. A provider that
fails or runs out of time throws nothing: the policy's `OnFailure` turns that into a verdict, which the
rule reads like any other.

## The default mapping

The rule reads `PolicyVerdict.Effective`:

| Verdict | Failure |
|---|---|
| `Deny` | one failure, `Severity.Error` |
| `Escalate` | one failure, `Severity.Warning` |
| `Warn`, `Abstain`, `Allow` | none |

A policy in Shadow mode never fails the validation, because its effective verdict is always `Allow`.
FluentValidation counts a failure of every severity against `IsValid`, so read `Severity` to tell a
warning from an error.

## Replace the mapping with `severity:`

Pass `severity:` by name. It receives the whole `PolicyVerdict`; the severity it returns is the
failure's, and null means no failure. To watch a policy's `Deny` verdicts before you enforce it, define
it with `.Shadow()` in place of `.Enforce()` and report them as `Info`. In Shadow the effective verdict
is always `Allow`, so the delegate reads `Evaluated`, the verdict the policy reached:

```csharp
RuleFor(ticket => ticket.Notes).Semantic(
    evaluator,
    "off-topic",
    severity: verdict => verdict.Evaluated == Verdict.Deny ? Severity.Info : null);
```

An `Info` failure still counts against `IsValid`, so filter on `Severity` where only errors should stop
the request.

## What a failure carries

- `CustomState` is the `PolicyVerdict` the evaluator returned, with the result of every rule behind it.
- `ErrorCode` is `SemanticPolicyValidator`.
- The message is `'{PropertyName}' was flagged by semantic policy '{PolicyId}'.`

`.WithMessage()` can use two placeholders the rule adds: `{PolicyId}`, and `{Verdict}`, the name of
`PolicyVerdict.Evaluated`. Both also appear in the failure's `FormattedMessagePlaceholderValues`.

```csharp
RuleFor(ticket => ticket.Notes)
    .Semantic(evaluator, "off-topic")
    .WithMessage("Policy {PolicyId} returned {Verdict} for '{PropertyName}'.");
```

`.WithMessage()`, `.WithSeverity()`, `.WithState()` and `.WithErrorCode()` replace what they name.
Whether the rule fails is still decided by the mapping, or by your `severity:` delegate.

## Test the validator once

A policy id is looked up when the rule runs, not when the validator is built. An id that no policy is
registered under throws the evaluator's `ArgumentException` from the first `ValidateAsync`, so a test
that builds your container and runs the validator once catches a misspelt id before a user does.

The overloads that take a `Policy` in place of an id need no registration. The evaluator checks that
policy against the registered providers on every call, and throws `PolicyConfigurationException` when
one it names is not registered or does not declare what the policy relies on.

## A signal, not a security boundary

A verdict is a probabilistic signal about content. It can be wrong in either direction on an input
nobody anticipated. A semantic rule is not an authorization check, so keep yours. A flagged value is not
proof of anything, and an allowed one is not proof of safety: what happens next is your application's
decision. The provider sends the text it evaluates to the endpoint it is configured with, so choosing a
provider is a data-residency decision too.
[SECURITY.md](https://github.com/semanticpolicy/semantic-policy/blob/main/SECURITY.md) says what this
means for what you build on it.
