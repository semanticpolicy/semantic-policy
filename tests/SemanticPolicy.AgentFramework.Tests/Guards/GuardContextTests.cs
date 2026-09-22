using System.Text.Json;
using SemanticPolicy.AgentFramework.Tests.Support;
using SemanticPolicy.Providers;

namespace SemanticPolicy.AgentFramework.Tests.Guards;

public sealed class GuardContextTests
{
    [Fact]
    public async Task Pre_Model_Default_Context_Carries_The_Run_Input_As_One_Text_Part()
    {
        ModelInput input = new([new("system", "text-system"), User("text-user"), new("assistant", " \n ")], "run_context");

        JsonElement context = await ContextSeen(GuardSubject.PreModel, input, PreModelOutcome.Proceed);

        context.EnumerateObject().Select(property => property.Name).Should().Equal("input");
        context.GetProperty("input").GetString().Should().Be("text-system\n\ntext-user");
    }

    [Fact]
    public async Task Pre_Tool_Default_Context_Carries_User_Request_Tool_And_Arguments()
    {
        ToolCall call = new(
            "search",
            "description-1",
            Json("""{"query":"text-query"}"""),
            [User("text-user-1"), new("assistant", "text-assistant"), User("text-user-2"), new("tool", "text-tool")],
            "call_context");

        JsonElement context = await ContextSeen(GuardSubject.PreTool, call, PreToolOutcome.Proceed);

        context.EnumerateObject().Select(property => property.Name).Should().Equal("user_request", "tool", "arguments");
        context.GetProperty("user_request").GetString().Should().Be("text-user-1\n\ntext-user-2");
        context.GetProperty("tool").GetProperty("name").GetString().Should().Be("search");
        context.GetProperty("tool").GetProperty("description").GetString().Should().Be("description-1");
        JsonElement.DeepEquals(context.GetProperty("arguments"), call.Arguments).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("description-1", true)]
    public async Task Tool_Part_Carries_The_Description_Only_When_There_Is_One(string? description, bool carried)
    {
        ToolCall call = new("search", description, Json("{}"), [], "call_context");

        JsonElement context = await ContextSeen(GuardSubject.PreTool, call, PreToolOutcome.Proceed);

        JsonElement tool = context.GetProperty("tool");
        tool.EnumerateObject().Select(property => property.Name).Should().Equal(carried ? ["name", "description"] : ["name"]);
        if (carried)
        {
            tool.GetProperty("description").GetString().Should().Be(description);
        }
    }

    [Theory]
    [InlineData("user,assistant,user", "text-1\n\ntext-3")]
    [InlineData("assistant,tool", "")]
    public void User_Request_Is_Every_User_Message_In_Order_And_Empty_Without_One(string roles, string expected)
    {
        ConversationMessage[] conversation = [.. roles.Split(',').Select((role, index) => new ConversationMessage(role, $"text-{index + 1}"))];

        ToolCall call = new("search", null, Json("{}"), conversation, "call_context");

        call.UserRequest.Should().Be(expected);
    }

    [Theory]
    [InlineData("string", "\"text-result\"")]
    [InlineData("object", """{"firstName":"text-name","count":2}""")]
    [InlineData("element", """{"k":[1,2]}""")]
    [InlineData("null", "null")]
    public async Task Post_Tool_Default_Context_Encodes_The_Result_By_Its_Type(string kind, string expected)
    {
        object? value = kind switch
        {
            "string" => "text-result",
            "object" => new { FirstName = "text-name", Count = 2 },
            "element" => Json("""{"k":[1,2]}"""),
            "null" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        ToolResult result = new(new ToolCall("search", null, Json("{}"), [User("text-user")], "call_context"), value);

        JsonElement context = await ContextSeen(GuardSubject.PostTool, result, PostToolOutcome.Proceed);

        context.EnumerateObject().Select(property => property.Name).Should().Equal("user_request", "tool", "result");
        context.GetProperty("result").GetRawText().Should().Be(expected);
    }

    [Theory]
    [InlineData("blank name")]
    [InlineData("blank correlation id")]
    [InlineData("blank role")]
    [InlineData("null messages")]
    [InlineData("undefined arguments")]
    public void Subjects_Reject_A_Missing_Identity(string @case)
    {
        Action construct = @case switch
        {
            "blank name" => () => new ToolCall(" ", null, Json("{}"), [User("text-1")], "call_context"),
            "blank correlation id" => () => new ModelInput([User("text-1")], ""),
            "blank role" => () => new ConversationMessage("", "text-1"),
            "null messages" => () => new ModelInput(null!, "run_context"),
            "undefined arguments" => () => new ToolCall("search", null, default, [User("text-1")], "call_context"),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

        construct.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("text-1");
    }

    // The context the provider saw for one evaluation of the subject through the guard's evaluate half.
    private static async Task<JsonElement> ContextSeen<TSubject, TOutcome>(
        GuardSubject<TSubject> at,
        TSubject subject,
        TOutcome outcome)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(ScriptedDecisionProvider.Boolean(true, 0.95));
        PolicyEvaluator evaluator = new([new ProviderRegistration("scripted", provider)], []);
        PolicyGuard<TSubject, TOutcome> guard = new(at, evaluator, DenyPolicy(), (_, _, _) => ValueTask.FromResult(outcome));

        await guard.EvaluateAsync(subject, CancellationToken.None);

        return provider.Requests.Should().ContainSingle().Which.Context;
    }

    private static Policy DenyPolicy() =>
        Policy.Define("p")
            .Enforce()
            .Rule(Policy.Rule("r").Boolean("question-1").WhenTrue(Verdict.Deny))
            .Using("scripted", binding => binding.DenyAboveProbability(0.9))
            .OnFailure(FailureBehavior.Deny)
            .Build();

    private static ConversationMessage User(string text) => new("user", text);

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
