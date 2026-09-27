using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Evaluation;
using SemanticPolicy.FluentValidation.Tests.Support;

namespace SemanticPolicy.FluentValidation.Tests;

public sealed class FailureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Failure_Carries_The_PolicyVerdict_And_The_Validator_Error_Code()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Deny));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        InlineValidator<Ticket> validator = new();
        validator.RuleFor(ticket => ticket.Description).Semantic(evaluator, Semantics.PolicyId);

        ValidationResult result = await validator.ValidateAsync(new Ticket("category-1", "text-1"), Token);

        ValidationFailure failure = result.Errors.Should().ContainSingle().Subject;
        PolicyVerdict verdict = failure.CustomState.Should().BeOfType<PolicyVerdict>().Subject;
        verdict.PolicyId.Should().Be("ticket-description");
        verdict.Evaluated.Should().Be(Verdict.Deny);
        verdict.Rules.Should().ContainSingle().Which.RuleId.Should().Be("flagged");
        failure.ErrorCode.Should().Be("SemanticPolicyValidator");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("policy")]
    public async Task Failure_Names_The_Property_And_The_Policy_By_Default(string overload)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Deny));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        InlineValidator<Ticket> validator = new();
        if (overload == "id")
        {
            validator.RuleFor(ticket => ticket.Description).Semantic(evaluator, Semantics.PolicyId);
        }
        else
        {
            validator.RuleFor(ticket => ticket.Description).Semantic(evaluator, Semantics.Ladder());
        }

        ValidationResult result = await validator.ValidateAsync(new Ticket("category-1", "text-1"), Token);

        result.Errors.Should().ContainSingle().Which.ErrorMessage
            .Should().Be("'Description' was flagged by semantic policy 'ticket-description'.");
    }

    [Fact]
    public async Task WithMessage_Replaces_The_Default_And_Can_Name_The_Policy_And_Verdict()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Deny));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        InlineValidator<Ticket> validator = new();
        validator.RuleFor(ticket => ticket.Description)
            .Semantic(container.GetRequiredService<IPolicyEvaluator>(), Semantics.PolicyId)
            .WithMessage("{PolicyId}: {Verdict}");

        ValidationResult result = await validator.ValidateAsync(new Ticket("category-1", "text-1"), Token);

        ValidationFailure failure = result.Errors.Should().ContainSingle().Subject;
        failure.ErrorMessage.Should().Be("ticket-description: Deny");
        failure.FormattedMessagePlaceholderValues.Should()
            .Contain("PolicyId", "ticket-description")
            .And.Contain("Verdict", "Deny");
    }

    [Theory]
    [InlineData("WithSeverity")]
    [InlineData("WithState")]
    [InlineData("WithErrorCode")]
    public async Task Chained_Override_Replaces_What_It_Names_And_The_Rule_Still_Fails(string overload)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Deny));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        object marker = new();
        InlineValidator<Ticket> validator = new();
        IRuleBuilderOptions<Ticket, string?> rule = validator.RuleFor(ticket => ticket.Description)
            .Semantic(container.GetRequiredService<IPolicyEvaluator>(), Semantics.PolicyId);
        switch (overload)
        {
            case "WithSeverity":
                rule.WithSeverity(Severity.Info);
                break;
            case "WithState":
                rule.WithState(_ => marker);
                break;
            default:
                rule.WithErrorCode("custom");
                break;
        }

        ValidationResult result = await validator.ValidateAsync(new Ticket("category-1", "text-1"), Token);

        ValidationFailure failure = result.Errors.Should().ContainSingle().Subject;
        switch (overload)
        {
            case "WithSeverity":
                failure.Severity.Should().Be(Severity.Info);
                break;
            case "WithState":
                failure.CustomState.Should().BeSameAs(marker);
                break;
            default:
                failure.ErrorCode.Should().Be("custom");
                break;
        }
    }
}
