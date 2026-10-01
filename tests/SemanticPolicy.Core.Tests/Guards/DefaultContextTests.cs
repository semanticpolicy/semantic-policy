using System.Text.Json;

namespace SemanticPolicy.Core.Tests.Guards;

public sealed class DefaultContextTests
{
    [Fact]
    public void Model_Input_Context_Is_The_Input_As_One_Text_Part_Under_Its_Correlation_Id()
    {
        ModelInput input = new([new("system", "text-system"), User("text-user"), new("assistant", " \n ")], "run_context");

        SemanticContext context = input.ToSemanticContext();

        context.Parts.Should().Equal(ContextPart.Text("input", "text-system\n\ntext-user"));
        context.CorrelationId.Should().Be("run_context");
    }

    [Fact]
    public void Tool_Call_Context_Carries_User_Request_Tool_And_Arguments_Under_The_Call_Id()
    {
        ToolCall call = new(
            "search",
            "description-1",
            Json("""{"query":"text-query"}"""),
            [User("text-user-1"), new("assistant", "text-assistant"), User("text-user-2"), new("tool", "text-tool")],
            "call_context");

        SemanticContext context = call.ToSemanticContext();

        context.Parts.Select(part => part.Name).Should().Equal("user_request", "tool", "arguments");
        context.Parts[0].Should().Be(ContextPart.Text("user_request", "text-user-1\n\ntext-user-2"));
        JsonElement json = context.ToJson();
        json.GetProperty("tool").GetProperty("name").GetString().Should().Be("search");
        json.GetProperty("tool").GetProperty("description").GetString().Should().Be("description-1");
        JsonElement.DeepEquals(json.GetProperty("arguments"), call.Arguments).Should().BeTrue();
        context.CorrelationId.Should().Be("call_context");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("description-1", true)]
    public void Tool_Part_Carries_The_Description_Only_When_There_Is_One(string? description, bool carried)
    {
        ToolCall call = new("search", description, Json("{}"), [], "call_context");

        JsonElement tool = call.ToSemanticContext().ToJson().GetProperty("tool");

        tool.EnumerateObject().Select(property => property.Name).Should().Equal(carried ? ["name", "description"] : ["name"]);
        if (carried)
        {
            tool.GetProperty("description").GetString().Should().Be(description);
        }
    }

    // The part's printed kind tells a text part from a JSON one: a string result stays text, so a
    // text-only provider reads it as it is, while JSON of any kind prints its value kind.
    [Theory]
    [InlineData("string", "Text", "\"text-result\"")]
    [InlineData("object", "Object", """{"firstName":"text-name","count":2}""")]
    [InlineData("element", "Object", """{"k":[1,2]}""")]
    [InlineData("null", "Null", "null")]
    public void Tool_Result_Context_Encodes_The_Result_By_Its_Type(string kind, string partKind, string expected)
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

        SemanticContext context = result.ToSemanticContext();

        context.Parts.Select(part => part.Name).Should().Equal("user_request", "tool", "result");
        context.Parts[2].ToString().Should().Be($"ContextPart {{ Name = result, Kind = {partKind} }}");
        context.ToJson().GetProperty("result").GetRawText().Should().Be(expected);
        context.CorrelationId.Should().Be("call_context");
    }

    private static ConversationMessage User(string text) => new("user", text);

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
