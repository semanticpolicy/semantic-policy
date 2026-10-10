using System.Text.Json;
using SemanticPolicy.Mcp.Gateway.Tests.Support;

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed class SampleFilesTests
{
    private static readonly string[] _rowKeys = ["id", "input", "label", "metadata"];
    private static readonly string[] _toolKeys = ["name", "description"];

    // A structural guard: a set measures what its point sends a provider only while each row's input holds exactly the
    // parts that point sends. A row with other parts would measure a context the gateway never sends, and nothing at
    // run time would show it. The second part is what the point sends beside the tool: a result is text, or an object
    // for a result with only structured content; a definition's input schema is an object.
    [Theory]
    [InlineData("results", "result", JsonValueKind.String, JsonValueKind.Object)]
    [InlineData("definitions", "input_schema", JsonValueKind.Object)]
    public void Sample_Set_Has_The_Shape_Its_Point_Sends(string set, string part, params JsonValueKind[] kinds)
    {
        JsonElement[] rows =
        [
            .. File.ReadAllLines(Repository.Sample($"{set}.jsonl"))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()),
        ];

        rows.Length.Should().BeInRange(90, 110);
        rows.Select(row => row.GetProperty("id").GetString()).Should().OnlyHaveUniqueItems();
        rows.Select(row => row.GetProperty("label").GetBoolean()).Distinct().Should().BeEquivalentTo(new[] { true, false });
        rows.Select(row => row.GetProperty("metadata").GetProperty("split").GetString()).Distinct()
            .Should().BeEquivalentTo(new[] { "tune", "test" });
        foreach (JsonElement row in rows)
        {
            string? id = row.GetProperty("id").GetString();
            JsonElement input = row.GetProperty("input");
            Names(row).Should().BeEquivalentTo(_rowKeys, "row {0} has the dataset's keys", id);
            Names(input).Should().BeEquivalentTo(new[] { "tool", part }, "row {0} holds the parts the {1} point sends", id, set);
            Names(input.GetProperty("tool")).Should().Contain("name").And.BeSubsetOf(_toolKeys, "row {0}'s tool is a name and a description", id);
            input.GetProperty(part).ValueKind.Should().BeOneOf(kinds, "row {0}'s {1}", id, part);
            row.GetProperty("metadata").GetProperty("source").GetString().Should().Be("synthetic", "row {0} says it is made up", id);
        }
    }

    // A structural guard: the sample gateway file is the one a first-time user copies, so it loads as written, with an
    // action for every verdict at both points, names the sample files beside it, and runs both policies in Shadow.
    [Fact]
    public void Sample_Gateway_File_Loads()
    {
        GatewayFileContent file = GatewayFile.Read(Repository.Sample("gateway.json"));

        file.ProvidersPath.Should().Be(Path.GetFullPath(Repository.Sample("providers.json")));
        file.Results.Should().NotBeNull();
        file.Definitions.Should().NotBeNull();
        foreach (PointEntry point in new[] { file.Results!, file.Definitions! })
        {
            point.PolicyPath.Should().Be(Path.GetFullPath(Repository.Sample($"{point.Name}.policy.json")));
            new[] { point.Mapping.Warn, point.Mapping.Escalate, point.Mapping.Deny, point.Mapping.Abstain }
                .Should().AllSatisfy(action => action.Should().NotBeNull());
            using JsonDocument policy = JsonDocument.Parse(File.ReadAllText(point.PolicyPath));
            policy.RootElement.GetProperty("mode").GetString().Should().Be("shadow", "the {0} policy runs in Shadow", point.Name);
        }
    }

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(property => property.Name)];
}
