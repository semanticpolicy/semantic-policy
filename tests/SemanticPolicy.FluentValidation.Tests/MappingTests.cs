using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Evaluation;
using SemanticPolicy.FluentValidation.Tests.Support;

namespace SemanticPolicy.FluentValidation.Tests;

public sealed class MappingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Verdict.Deny, new[] { Severity.Error })]
    [InlineData(Verdict.Escalate, new[] { Severity.Warning })]
    [InlineData(Verdict.Warn, new Severity[0])]
    [InlineData(Verdict.Abstain, new Severity[0])]
    [InlineData(Verdict.Allow, new Severity[0])]
    public async Task Enforced_Verdict_Fails_The_Validation_At_Its_Default_Severity(Verdict verdict, Severity[] expected)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(verdict));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        PolicyVerdict premise = await evaluator.EvaluateAsync(Semantics.PolicyId, SemanticContext.FromText("text-1"), Token);
        premise.Effective.Should().Be(verdict, "the scripted answer has to reach the verdict under test");
        InlineValidator<Ticket> validator = new();
        validator.RuleFor(ticket => ticket.Description).Semantic(evaluator, Semantics.PolicyId);

        ValidationResult result = await validator.ValidateAsync(new Ticket("category-1", "text-1"), Token);

        result.Errors.Select(failure => failure.Severity).Should().Equal(expected);
    }

    [Theory]
    [InlineData(Verdict.Deny)]
    [InlineData(Verdict.Escalate)]
    public async Task Shadow_Policy_Never_Fails_The_Validation(Verdict evaluated)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(evaluated));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder(PolicyMode.Shadow));
        InlineValidator<Ticket> validator = new();
        validator.RuleFor(ticket => ticket.Description).Semantic(container.GetRequiredService<IPolicyEvaluator>(), Semantics.PolicyId);

        ValidationResult result = await validator.ValidateAsync(new Ticket("category-1", "text-1"), Token);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(PolicyMode.Shadow, new[] { Severity.Info })]
    [InlineData(PolicyMode.Enforce, new Severity[0])]
    public async Task Severity_Delegate_Decides_Whether_And_How_A_Verdict_Fails(PolicyMode mode, Severity[] expected)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Deny));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder(mode));
        InlineValidator<Ticket> validator = new();
        validator.RuleFor(ticket => ticket.Description).Semantic(
            container.GetRequiredService<IPolicyEvaluator>(),
            Semantics.PolicyId,
            severity: verdict => verdict.Mode == PolicyMode.Shadow && verdict.Evaluated == Verdict.Deny ? Severity.Info : null);

        ValidationResult result = await validator.ValidateAsync(new Ticket("category-1", "text-1"), Token);

        result.Errors.Select(failure => failure.Severity).Should().Equal(expected);
    }
}
