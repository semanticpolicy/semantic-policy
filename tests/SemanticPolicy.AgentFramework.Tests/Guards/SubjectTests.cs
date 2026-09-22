using System.Text.Json;

namespace SemanticPolicy.AgentFramework.Tests.Guards;

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

    private static ToolCall Call() =>
        new("search", null, Json("""{"k":"text-marker"}"""), [new ConversationMessage("user", "text-marker")], "call_1");

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
