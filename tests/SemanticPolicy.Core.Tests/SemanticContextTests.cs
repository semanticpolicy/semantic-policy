using System.Text.Json;

namespace SemanticPolicy.Core.Tests;

public sealed class SemanticContextTests
{
    public static TheoryData<string, string> WireContexts => new()
    {
        { "\"bare\"", "bare" },
        { """{ "text": "only" }""", "only" },
        { """{ "n": 1 }""", "n:\n1" },
        {
            """{ "part-a": "alpha", "part-b": { "k": "ключ", "arr": [ 1, 2 ] } }""",
            "part-a:\nalpha\n\npart-b:\n{\"k\":\"ключ\",\"arr\":[1,2]}"
        },
        { """[ "a", 1, { "k": "ключ" } ]""", "[\"a\",1,{\"k\":\"ключ\"}]" },
        { "true", "true" },
        { "{}", "" },
    };

    public static TheoryData<SemanticContext, string> Contexts => new()
    {
        { SemanticContext.FromText("line-1\nline-2"), "line-1\nline-2" },
        {
            new SemanticContext([ContextPart.Text("part-a", "alpha"), ContextPart.Json("part-b", Element("""{ "k": "ключ" }"""))]),
            "part-a:\nalpha\n\npart-b:\n{\"k\":\"ключ\"}"
        },
        { new SemanticContext([ContextPart.Json("part-a", Element("\"s\""))]), "s" },
        { new SemanticContext([ContextPart.Json("part-a", Element("""{ "n": 1 }"""))]), "part-a:\n{\"n\":1}" },
    };

    public static TheoryData<string, Func<SemanticContext>> InvalidContexts => new()
    {
        { "no parts", () => new SemanticContext([]) },
        {
            "duplicate name",
            () => new SemanticContext([ContextPart.Text("part-a", "x"), ContextPart.Text("part-a", "y")])
        },
        { "empty name", () => new SemanticContext([ContextPart.Text("", "x")]) },
        { "whitespace name", () => new SemanticContext([ContextPart.Text("  ", "x")]) },
    };

    [Fact]
    public void Context_Serializes_Parts_In_Declared_Order_Without_The_Correlation_Id()
    {
        SemanticContext context = new(
            [
                ContextPart.Text("part-b", "text-b"),
                ContextPart.Json("part-a", Element("""{ "k": "v" }""")),
                ContextPart.Text("part-c", "text-c"),
            ],
            CorrelationId: "corr-1");

        JsonElement json = context.ToJson();

        json.ValueKind.Should().Be(JsonValueKind.Object);
        json.EnumerateObject().Select(property => property.Name).Should().Equal("part-b", "part-a", "part-c");
        json.GetProperty("part-b").GetString().Should().Be("text-b");
        json.GetProperty("part-a").GetProperty("k").GetString().Should().Be("v");
        json.GetProperty("part-c").GetString().Should().Be("text-c");
        json.GetRawText().Should().NotContain("corr-1");
    }

    [Theory]
    [MemberData(nameof(WireContexts))]
    public void Canonical_Text_Renders_Deterministically(string wireContext, string expected)
    {
        SemanticContext.ToCanonicalText(Element(wireContext)).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Canonical_Text_Of_A_Context_Is_The_Rendering_Of_Its_Wire_Shape(SemanticContext context, string expected)
    {
        context.ToCanonicalText().Should().Be(expected);
        SemanticContext.ToCanonicalText(context.ToJson()).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(InvalidContexts))]
    public void Context_Requires_Uniquely_Named_Parts(string label, Func<SemanticContext> construct)
    {
        construct.Should().Throw<ArgumentException>(label);
    }

    [Fact]
    public void Json_Part_Survives_Disposal_Of_Its_Source_Document()
    {
        SemanticContext context;
        using (JsonDocument document = JsonDocument.Parse("""{ "k": "v" }"""))
        {
            context = new([ContextPart.Json("part-a", document.RootElement)]);
        }

        context.ToCanonicalText().Should().Be("part-a:\n{\"k\":\"v\"}");
    }

    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
