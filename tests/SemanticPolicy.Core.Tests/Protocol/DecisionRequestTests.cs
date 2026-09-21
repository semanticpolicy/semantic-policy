using System.Text.Json;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests.Protocol;

public sealed class DecisionRequestTests
{
    private static readonly JsonElement _context = JsonSerializer.Deserialize<JsonElement>("\"context-marker\"");

    private static readonly Dictionary<string, string> _twoOptions = new() { ["a"] = "a", ["b"] = "b" };

    public static TheoryData<string, DecisionRequest> InvalidRequests => new()
    {
        { "empty question", Boolean("") },
        { "whitespace question", Boolean("   ") },
        { "undefined context", new DecisionRequest(DecisionType.Boolean, "question-marker", default) },
        {
            "null context",
            new DecisionRequest(DecisionType.Boolean, "question-marker", JsonSerializer.Deserialize<JsonElement>("null"))
        },
        { "choice without options", Choice(null) },
        { "choice with one option", Choice(new Dictionary<string, string> { ["a"] = "a" }) },
        { "score without levels", Score(null) },
        { "score with one level", Score(["a"]) },
        { "score with eleven levels", Score(["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k"]) },
        { "score with a duplicate level", Score(["a", "b", "a"]) },
        { "score with an empty level", Score(["a", ""]) },
        { "options on a boolean", Boolean("question-marker") with { Options = _twoOptions } },
        { "levels on a choice", Choice(_twoOptions) with { Levels = ["a", "b"] } },
        { "criteria on a score", Score(["a", "b"]) with { Criteria = new BooleanCriteria("t", "f") } },
    };

    public static TheoryData<DecisionRequest> ValidRequests => new()
    {
        Boolean("question-marker") with { Criteria = new BooleanCriteria("t", null) },
        Choice(_twoOptions),
        Score(["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"]),
    };

    public static TheoryData<DecisionRequest, string> RenderedRequests => new()
    {
        {
            Boolean("question-marker") with { Criteria = new BooleanCriteria("true-marker", null) },
            "DecisionRequest { Protocol = semanticpolicy/v0, Type = Boolean, Context = String, Criteria = 1 }"
        },
        {
            new DecisionRequest(
                DecisionType.Choice,
                "question-marker",
                JsonSerializer.Deserialize<JsonElement>("""{ "text": "context-marker" }"""),
                Options: new Dictionary<string, string> { ["a"] = "option-marker-a", ["b"] = "option-marker-b" }),
            "DecisionRequest { Protocol = semanticpolicy/v0, Type = Choice, Context = Object, Options = 2 }"
        },
        {
            Score(["level-marker-a", "level-marker-b"]),
            "DecisionRequest { Protocol = semanticpolicy/v0, Type = Score, Context = String, Levels = 2 }"
        },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Invalid_Request_Is_Rejected_As_An_Argument_Error(string label, DecisionRequest request)
    {
        Action act = request.EnsureValid;

        ArgumentException error = act.Should().Throw<ArgumentException>(label).Which;
        error.Message.Should().NotContain("question-marker").And.NotContain("context-marker");
    }

    [Theory]
    [MemberData(nameof(ValidRequests))]
    public void Valid_Request_Is_Accepted(DecisionRequest request)
    {
        Action act = request.EnsureValid;

        act.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(RenderedRequests))]
    public void Rendered_Request_Names_Its_Shape_And_Never_Its_Content(DecisionRequest request, string expected)
    {
        string text = request.ToString();

        text.Should().Be(expected).And.NotContain("marker");
    }

    private static DecisionRequest Boolean(string question) => new(DecisionType.Boolean, question, _context);

    private static DecisionRequest Choice(IReadOnlyDictionary<string, string>? options) =>
        new(DecisionType.Choice, "question-marker", _context, Options: options);

    private static DecisionRequest Score(IReadOnlyList<string>? levels) =>
        new(DecisionType.Score, "question-marker", _context, Levels: levels);
}
