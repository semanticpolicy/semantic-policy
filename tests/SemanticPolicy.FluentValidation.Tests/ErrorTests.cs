using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.FluentValidation.Tests.Support;

namespace SemanticPolicy.FluentValidation.Tests;

public sealed class ErrorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Validate_On_A_Semantic_Rule_Throws_AsyncValidatorInvokedSynchronouslyException()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Deny));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        InlineValidator<Ticket> validator = new();
        validator.RuleFor(ticket => ticket.Description).Semantic(container.GetRequiredService<IPolicyEvaluator>(), Semantics.PolicyId);

        Action validating = () => validator.Validate(new Ticket("category-1", "text-1"));

        validating.Should().Throw<AsyncValidatorInvokedSynchronouslyException>();
    }

    [Theory]
    [InlineData("id", "ruleBuilder", typeof(ArgumentNullException))]
    [InlineData("id", "evaluator", typeof(ArgumentNullException))]
    [InlineData("id", "policyId", typeof(ArgumentNullException), null)]
    [InlineData("id", "policyId", typeof(ArgumentException), "")]
    [InlineData("id", "policyId", typeof(ArgumentException), "   ")]
    [InlineData("policy", "ruleBuilder", typeof(ArgumentNullException))]
    [InlineData("policy", "evaluator", typeof(ArgumentNullException))]
    [InlineData("policy", "policy", typeof(ArgumentNullException))]
    [InlineData("id and context", "ruleBuilder", typeof(ArgumentNullException))]
    [InlineData("id and context", "evaluator", typeof(ArgumentNullException))]
    [InlineData("id and context", "policyId", typeof(ArgumentNullException), null)]
    [InlineData("id and context", "policyId", typeof(ArgumentException), "")]
    [InlineData("id and context", "policyId", typeof(ArgumentException), "   ")]
    [InlineData("id and context", "context", typeof(ArgumentNullException))]
    [InlineData("policy and context", "ruleBuilder", typeof(ArgumentNullException))]
    [InlineData("policy and context", "evaluator", typeof(ArgumentNullException))]
    [InlineData("policy and context", "policy", typeof(ArgumentNullException))]
    [InlineData("policy and context", "context", typeof(ArgumentNullException))]
    public void Semantic_Checks_Its_Arguments_At_The_Call(
        string overload, string parameter, Type exception, string? policyId = Semantics.PolicyId)
    {
        using ServiceProvider container = Semantics.Container(new ScriptedDecisionProvider());
        InlineValidator<Ticket> validator = new();
        IRuleBuilder<Ticket, string?>? ruleBuilder = parameter == "ruleBuilder" ? null : validator.RuleFor(ticket => ticket.Notes);
        IPolicyEvaluator? evaluator = parameter == "evaluator" ? null : container.GetRequiredService<IPolicyEvaluator>();
        Policy? policy = parameter == "policy" ? null : Semantics.Ladder();
        Func<Ticket, SemanticContext>? context = parameter == "context" ? null : ticket => SemanticContext.FromText(ticket.Description);

        Action semantic = overload switch
        {
            "id" => () => ruleBuilder!.Semantic(evaluator!, policyId!),
            "policy" => () => ruleBuilder!.Semantic(evaluator!, policy!),
            "id and context" => () => ruleBuilder!.Semantic(evaluator!, policyId!, context!),
            _ => () => ruleBuilder!.Semantic(evaluator!, policy!, context!),
        };

        semantic.Should().Throw<ArgumentException>().WithParameterName(parameter).Which.Should().BeOfType(exception);
    }

    [Fact]
    public async Task Unknown_Policy_Id_Throws_ArgumentException_At_The_First_ValidateAsync()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Deny));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        InlineValidator<Ticket> validator = new();

        Action building = () => validator.RuleFor(ticket => ticket.Description).Semantic(evaluator, "unknown-policy");
        Func<Task> validating = () => validator.ValidateAsync(new Ticket("category-1", "text-1"), Token);

        building.Should().NotThrow();
        await validating.Should().ThrowExactlyAsync<ArgumentException>();
    }

    [Fact]
    public async Task Cancelled_ValidateAsync_Throws_OperationCanceledException()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().AnswersOnlyOnCancellation();
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        InlineValidator<Ticket> validator = new();
        validator.RuleFor(ticket => ticket.Description).Semantic(container.GetRequiredService<IPolicyEvaluator>(), Semantics.PolicyId);
        using CancellationTokenSource cancellation = new();

        Task<ValidationResult> validation = validator.ValidateAsync(new Ticket("category-1", "text-1"), cancellation.Token);
        provider.Requests.Should().ContainSingle();
        await cancellation.CancelAsync();

        // A token that never reached the provider leaves it waiting forever; the bound turns that into a
        // TimeoutException rather than a hung run.
        Func<Task> completing = () => validation.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await completing.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("one field")]
    [InlineData("context delegate")]
    public async Task Policy_Overload_Evaluates_A_Policy_That_Is_Not_Registered(string overload)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Deny));
        using ServiceProvider container = Semantics.Container(provider);
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        InlineValidator<Ticket> validator = new();
        if (overload == "one field")
        {
            validator.RuleFor(ticket => ticket.Description).Semantic(evaluator, Semantics.Ladder());
        }
        else
        {
            validator.RuleFor(ticket => ticket.Priority)
                .Semantic(evaluator, Semantics.Ladder(), ticket => SemanticContext.FromText(ticket.Description));
        }

        ValidationResult result = await validator.ValidateAsync(new Ticket("category-1", "text-1"), Token);

        result.Errors.Should().ContainSingle().Which.Severity.Should().Be(Severity.Error);
    }
}
