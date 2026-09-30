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

    // The rule hands a call's verdict and severity to the failure FluentValidation builds through the
    // validation's shared context data. Two rules flagged in one validation must each report their own,
    // never the one the other rule wrote.
    [Fact]
    public async Task Two_Flagged_Rules_Each_Carry_Their_Own_Verdict_And_Severity()
    {
        Dictionary<string, Verdict> verdicts = new(StringComparer.Ordinal)
        {
            ["""{"text":"text-1"}"""] = Verdict.Deny,
            ["""{"text":"text-2"}"""] = Verdict.Escalate,
        };
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider()
            .Answers(request => Semantics.Answer(verdicts[request.Context.GetRawText()]));
        using ServiceProvider container = Semantics.Container(
            provider, Semantics.Ladder(), Semantics.Ladder(id: "ticket-notes"));
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        InlineValidator<Ticket> validator = new();
        validator.RuleFor(ticket => ticket.Description).Semantic(evaluator, Semantics.PolicyId);
        validator.RuleFor(ticket => ticket.Notes).Semantic(evaluator, "ticket-notes");

        ValidationResult result = await validator.ValidateAsync(new Ticket("category-1", "text-1", "text-2"), Token);

        result.Errors
            .Select(failure =>
            {
                PolicyVerdict verdict = (PolicyVerdict)failure.CustomState;
                return (failure.PropertyName, failure.Severity, verdict.PolicyId, verdict.Evaluated);
            })
            .Should().Equal(
                ("Description", Severity.Error, "ticket-description", Verdict.Deny),
                ("Notes", Severity.Warning, "ticket-notes", Verdict.Escalate));
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
