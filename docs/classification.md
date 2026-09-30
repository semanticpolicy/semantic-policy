# Classification

A Choice rule asks a decision model to pick one of several options, so a policy that holds one is a
classifier: which team should answer a ticket, which queue a document belongs in. It needs no agent
and no package beyond Core and a provider. This page routes a support ticket to one of four teams
with `IPolicyEvaluator` alone. It is the same Core the agent integrations use, and
[`examples/AgentRouter`](../examples/AgentRouter/Program.cs) runs the same policy in a program, where
the chosen team's agent answers the customer.

A label from a decision model is a probabilistic reading of the text, not a fact about it. It can be
wrong in either direction on an input nobody anticipated, and a routing rule is not a security
boundary ([`SECURITY.md`](../SECURITY.md)).

## The policy

The rule names each option with a key and a description, and the model picks among the
descriptions. This one asks TypeSafe Jev through OpenRouter, which reads its key from
`OPENROUTER_API_KEY`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.TypeSafe;

// Illustrative, not measured: when the two likeliest teams are closer than this, the rule abstains.
const double MarginGate = 0.20;

Policy router = Policy.Define("ticket-router")
    .Enforce()
    .Rule(Policy.Rule("route")
        .Choice("Which team should handle this support request?")
        .Option("billing", "Invoices, charges, refunds, payment methods and tax.", Verdict.Allow)
        .Option("technical", "Builds, errors, integrations and anything that doesn't work as it should.", Verdict.Allow)
        .Option("account", "Signing in, passwords, two-factor authentication, members and permissions.", Verdict.Allow)
        .Option("sales", "Plans, prices, discounts and trials for teams choosing or changing a plan.", Verdict.Allow)
        .Build())
    .Using("jev", binding => binding.WhenProbabilityMarginBelow(MarginGate))
    .OnFailure(FailureBehavior.Allow)
    .Budget(TimeSpan.FromSeconds(5)) // illustrative too
    .Build();

ServiceCollection services = new();
services.AddSemanticPolicy()
    .AddTypeSafeJev("jev", o => o.Route = TypeSafeJevRoute.OpenRouter)
    .AddPolicy(router);
```

Every option maps to `Allow`, because a route is not a judgement about the ticket: the policy says
who answers, not whether anyone should. A Choice binding carries no thresholds, so its margin gate
is all it has. The margin is how far the likeliest option leads the runner-up, and below the gate the
binding does not decide. With no binding after it, the rule ends in `Abstain`. A provider that
reports a score rather than a probability, such as a System One server, takes
`WhenScoreMarginBelow` instead.

`OnFailure(FailureBehavior.Allow)` means a call that fails or runs out of budget picks no team, and
the code below sends that ticket to a person as it does a close call.

## Classify a ticket

Ask the evaluator about the ticket's text, outside any agent:

```csharp
using ServiceProvider serviceProvider = services.BuildServiceProvider();
IPolicyEvaluator evaluator = serviceProvider.GetRequiredService<IPolicyEvaluator>();

PolicyVerdict verdict = await evaluator.EvaluateAsync("ticket-router", SemanticContext.FromText(ticket));
string? team = ChosenOption(verdict);
```

`ticket` is the text the customer wrote.

## Read the chosen option

Every option maps to `Allow`, so the verdict alone does not say which team was picked. The option is
in the answer of the attempt that decided the rule, and `examples/AgentRouter` reads it from there:

```csharp
static string? ChosenOption(PolicyVerdict verdict) =>
    (Deciding(verdict.Rules[0])?.Result.Value as ChoiceValue)?.Option;

static Attempt? Deciding(RuleVerdict rule) =>
    rule.DecidingBinding is { } index
        ? rule.Attempts.FirstOrDefault(attempt => attempt.BindingIndex == index)
        : null;
```

`DecidingBinding` is null when no binding decided the rule, so `ChosenOption` is null then too. Each
attempt also carries the provider's number for every option in its result's evidence, which the
example prints: the highest one names the pick, and none of them is a calibrated probability of
being right.

## Send a close call to a person

When the two likeliest teams sit within the gate, the rule abstains: the verdict's `Evaluated` is
`Abstain`, the rule's `Source` is `VerdictSource.UncertaintyExhausted`, and no team is chosen. A
router that took the top option anyway would be guessing, so hand the ticket to a person:

```csharp
if (team is null)
{
    // The gate abstained, or the call failed and OnFailure answered: nothing picked a team.
    AssignToPerson(ticket);
}
else
{
    AssignToTeam(ticket, team);
}
```

`AssignToPerson` and `AssignToTeam` stand for your application's own code. The library reports the
pick and the margin, and what happens to the ticket is your decision.

## Measure it

The gate above is a guess. The evaluation CLI measures a Choice rule on tickets you labelled
yourself, and its `sweep` and `compare` commands choose the gate that meets a goal you set, such as
how often the tickets it decides must go to the right team. Its
[README](../tools/SemanticPolicy.Evals/README.md#compare) shows `compare` on a router set with this
rule's four teams.
