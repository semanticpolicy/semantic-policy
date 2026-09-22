using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class RecordingTests
{
    [Fact]
    public async Task Writer_Then_Reader_Round_Trips_The_Header_And_Rows_Keyed_By_Rule_And_Provider()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local", "jev"], [Samples.Flagged(), Samples.Route()]);
        RecordingHeader header = Samples.Header(policy);
        ProviderResult local = Samples.BooleanAnswer(0.95);
        ProviderResult jev = Samples.BooleanAnswer(0.2, "jev");
        ProviderResult route = Samples.Answer(new ChoiceValue("allow"), "local", Samples.Probability(("allow", 0.8), ("deny", 0.2)));
        ProviderResult timedOut = Samples.Failed(FailureKind.Timeout, "jev");
        ProviderResult withoutEvidence = Samples.Answer(new BooleanValue(true));

        Recording recording = await Samples.RecordAsync(
            file.Path,
            header,
            Samples.Recorded(
                "a",
                (Samples.Injection, "local", local),
                (Samples.Injection, "jev", jev),
                ("route", "local", route),
                ("route", "jev", timedOut)),
            Samples.Recorded("b", (Samples.Injection, "local", withoutEvidence)));

        recording.Path.Should().Be(file.Path);
        recording.Header.Should().BeEquivalentTo(header, options => options.PreferringRuntimeMemberTypes());
        recording.Header.Format.Should().Be(RecordingHeader.FormatV0);
        recording.Rows.Select(row => row.Id).Should().Equal("a", "b");
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ProviderResult>> first = recording.Rows[0].Attempts;
        first.Keys.Should().BeEquivalentTo([Samples.Injection, "route"]);
        first[Samples.Injection]["local"].Should().BeEquivalentTo(local, options => options.PreferringRuntimeMemberTypes());
        first[Samples.Injection]["jev"].Should().BeEquivalentTo(jev, options => options.PreferringRuntimeMemberTypes());
        first["route"]["local"].Should().BeEquivalentTo(route, options => options.PreferringRuntimeMemberTypes());
        first["route"]["jev"].Should().BeEquivalentTo(timedOut, options => options.PreferringRuntimeMemberTypes());
        recording.Rows[1].Attempts[Samples.Injection]["local"].Evidence.Should().BeEmpty();

        // The absence of evidence is an empty list on the file too, never a dropped property.
        string[] lines = await File.ReadAllLinesAsync(file.Path, TestContext.Current.CancellationToken);
        lines.Should().HaveCount(3);
        JsonNode second = JsonNode.Parse(lines[2])!;
        second["attempts"]![Samples.Injection]!["local"]!["evidence"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Recording_File_Contains_No_Input_No_Label_And_No_Raw()
    {
        using TempFile file = TempFile.Write("");
        const string marker = "marker-7f3a";
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
        JsonElement raw = JsonSerializer.Deserialize<JsonElement>($$"""{ "text": "{{marker}}" }""");
        DatasetRow row = Samples.Row("a", marker) with
        {
            Input = SemanticContext.FromText(marker),
            Metadata = new Dictionary<string, JsonElement> { ["source"] = raw },
        };
        ProviderResult result = Samples.BooleanAnswer(0.95) with { Raw = raw };

        await Samples.RecordAsync(file.Path, Samples.Header(policy), Samples.Recorded(row.Id, (Samples.Injection, "local", result)));

        string text = await File.ReadAllTextAsync(file.Path, TestContext.Current.CancellationToken);
        text.Should().NotContain(marker)
            .And.NotContain("\"input\"")
            .And.NotContain("\"label\"")
            .And.NotContain("\"metadata\"")
            .And.NotContain("\"raw\"");
        text.Should().Contain("\"a\"").And.Contain("\"local\"").And.Contain("\"model-local\"");
    }

    [Fact]
    public async Task Reader_Rejects_An_Unknown_Format_Version_And_A_Torn_Line_Naming_The_Line()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
        await Samples.RecordAsync(
            file.Path,
            Samples.Header(policy),
            Samples.Recorded("a", (Samples.Injection, "local", Samples.BooleanAnswer(0.95))),
            Samples.Recorded("b", (Samples.Injection, "local", Samples.BooleanAnswer(0.1))));
        string[] lines = await File.ReadAllLinesAsync(file.Path, TestContext.Current.CancellationToken);
        const string unknownFormat = "semanticpolicy/evals-recording/v1";
        using TempFile torn = TempFile.Write(lines[0] + "\n" + lines[1] + "\n" + lines[2][..(lines[2].Length / 2)]);
        using TempFile unknown = TempFile.Write(lines[0].Replace(RecordingHeader.FormatV0, unknownFormat) + "\n" + lines[1] + "\n");
        using TempFile empty = TempFile.Write("");

        Action readTorn = () => RecordingReader.Read(torn.Path);
        Action readUnknown = () => RecordingReader.Read(unknown.Path);
        Action readEmpty = () => RecordingReader.Read(empty.Path);

        readTorn.Should().Throw<EvalsException>().Which.Message.Should().Contain("line 3").And.Contain(torn.Path);
        readUnknown.Should().Throw<EvalsException>().Which.Message.Should()
            .Contain("line 1").And.Contain(unknownFormat).And.Contain(RecordingHeader.FormatV0);
        readEmpty.Should().Throw<EvalsException>().Which.Message.Should().Contain(empty.Path);
    }
}
