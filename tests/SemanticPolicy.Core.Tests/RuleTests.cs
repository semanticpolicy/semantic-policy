using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests;

public sealed class RuleTests
{
    public static TheoryData<Rule, DecisionRequest> Requests => new()
    {
        {
            new BooleanRule(
                "b",
                "question-b",
                true,
                [Verdict.Deny],
                new BooleanCriteria("true-marker", "false-marker")),
            new DecisionRequest(
                DecisionType.Boolean,
                "question-b",
                default,
                Criteria: new BooleanCriteria("true-marker", "false-marker"))
        },
        {
            Policy.Rule("c").Choice("question-c")
                .Option("a", "description-a", Verdict.Allow)
                .Option("b", "description-b", Verdict.Deny)
                .Build(),
            new DecisionRequest(
                DecisionType.Choice,
                "question-c",
                default,
                Options: new Dictionary<string, string> { ["a"] = "description-a", ["b"] = "description-b" })
        },
        {
            Policy.Rule("s").Score("question-s", "low", "mid", "high").DenyAtOrAbove("high").Build(),
            new DecisionRequest(DecisionType.Score, "question-s", default, Levels: ["low", "mid", "high"])
        },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void Rule_Creates_A_Valid_Protocol_Request_For_Its_Type(Rule rule, DecisionRequest expected)
    {
        SemanticContext context = new(
            [ContextPart.Text("text", "context-marker"), ContextPart.Text("tool", "tool-marker")],
            "correlation-marker");

        DecisionRequest request = rule.CreateRequest(context);

        request.Invoking(r => r.EnsureValid()).Should().NotThrow();
        request.Should().BeEquivalentTo(expected, options => options.Excluding(r => r.Context));
        JsonNode.DeepEquals(JsonNode.Parse(request.Context.GetRawText()), JsonNode.Parse(context.ToJson().GetRawText()))
            .Should().BeTrue();
        JsonSerializer.Serialize(request, SemanticPolicyJson.Options).Should().NotContain("correlation-marker");
    }
}
