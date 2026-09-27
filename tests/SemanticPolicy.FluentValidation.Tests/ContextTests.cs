using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.FluentValidation.Tests.Support;

namespace SemanticPolicy.FluentValidation.Tests;

public sealed class ContextTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Null_Or_Blank_Value_Passes_Without_Reaching_The_Provider(string? notes)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Deny));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        InlineValidator<Ticket> validator = new();
        validator.RuleFor(ticket => ticket.Notes).Semantic(container.GetRequiredService<IPolicyEvaluator>(), Semantics.PolicyId);

        ValidationResult result = await validator.ValidateAsync(new Ticket("category-1", "text-1", notes), Token);

        provider.Requests.Should().BeEmpty();
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Default_Context_Sends_The_Value_As_One_Part_Named_Text()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Allow));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        InlineValidator<Ticket> validator = new();
        validator.RuleFor(ticket => ticket.Description).Semantic(container.GetRequiredService<IPolicyEvaluator>(), Semantics.PolicyId);

        await validator.ValidateAsync(new Ticket("category-1", "text-1"), Token);

        provider.Requests.Should().ContainSingle().Which.Context.GetRawText().Should().Be("""{"text":"text-1"}""");
    }

    [Theory]
    [InlineData(nameof(Ticket.Description))]
    [InlineData(nameof(Ticket.Priority))]
    public async Task Context_Delegate_Sends_The_Parts_It_Builds(string property)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(Semantics.Answer(Verdict.Allow));
        using ServiceProvider container = Semantics.Container(provider, Semantics.Ladder());
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        Func<Ticket, SemanticContext> parts = ticket => new SemanticContext(
            [ContextPart.Text("description", ticket.Description), ContextPart.Text("category", ticket.Category)]);
        InlineValidator<Ticket> validator = new();
        if (property == nameof(Ticket.Description))
        {
            validator.RuleFor(ticket => ticket.Description).Semantic(evaluator, Semantics.PolicyId, parts);
        }
        else
        {
            validator.RuleFor(ticket => ticket.Priority).Semantic(evaluator, Semantics.PolicyId, parts);
        }

        await validator.ValidateAsync(new Ticket("category-1", "text-1", Priority: 3), Token);

        provider.Requests.Should().ContainSingle().Which.Context.GetRawText()
            .Should().Be("""{"description":"text-1","category":"category-1"}""");
    }
}
