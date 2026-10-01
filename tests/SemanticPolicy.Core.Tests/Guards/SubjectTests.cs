using System.Text.Json;

namespace SemanticPolicy.Core.Tests.Guards;

public sealed class SubjectTests
{
    private const string _printedCall =
        "ToolCall { Name = search, Arguments = Object, Conversation = 1, CorrelationId = call_1 }";

    public static TheoryData<object, string> PrintedSubjects => new()
    {
        { new ConversationMessage("user", "text-marker"), "ConversationMessage { Role = user }" },
        {
            new ModelInput([new("user", "text-marker"), new("assistant", "text-marker")], "run_1"),
            "ModelInput { Messages = 2, CorrelationId = run_1 }"
        },
        { Call(), _printedCall },
        { new ToolResult(Call(), "text-marker"), $"ToolResult {{ Call = {_printedCall}, Value = String }}" },
        {
            new ToolResult(Call(), Json("""{"k":"text-marker"}""")),
            $"ToolResult {{ Call = {_printedCall}, Value = Json Object }}"
        },
        { new ToolResult(Call(), null), $"ToolResult {{ Call = {_printedCall}, Value = null }}" },
    };

    [Theory]
    [MemberData(nameof(PrintedSubjects))]
    public void Subjects_Print_Their_Shape_And_Never_Their_Content(object subject, string expected)
    {
        string text = subject.ToString()!;

        text.Should().Be(expected).And.NotContain("text-marker");
    }

    [Fact]
    public void Tool_Result_Rejects_An_Undefined_Json_Value()
    {
        Action construct = () => new ToolResult(Call(), default(JsonElement));

        construct.Should().Throw<ArgumentException>().WithParameterName("Value");
    }

    [Fact]
    public void Tool_Result_Keeps_A_Json_Value_Readable_After_Its_Document_Is_Disposed()
    {
        JsonDocument document = JsonDocument.Parse("""{"k":"text-marker"}""");

        ToolResult result = new(Call(), document.RootElement);
        document.Dispose();

        result.Value.Should().BeOfType<JsonElement>()
            .Which.GetProperty("k").GetString().Should().Be("text-marker");
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

    private static ConversationMessage User(string text) => new("user", text);

    private static ToolCall Call() =>
        new("search", null, Json("""{"k":"text-marker"}"""), [new ConversationMessage("user", "text-marker")], "call_1");

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
