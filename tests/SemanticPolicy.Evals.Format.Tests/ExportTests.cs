using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Format.Tests;

// A recording exported from verdicts an application stored: every verdict here comes from Core's own step
// function, the way the application's evaluator produced it.
public sealed class ExportTests
{
    private static readonly DateTimeOffset _exportedAt = new(2026, 11, 16, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task FromVerdict_Keys_Attempts_By_Rule_And_Provider_And_Carries_The_Digest()
    {
        using TempFile file = TempFile.Write("");
        const string marker = "raw-marker-41c9";
        Policy policy = Samples.Guard(["local", "jev"], [Samples.Flagged(), Samples.Route()]) with
        {
            OnFailure = FailureBehavior.Fallback(Verdict.Escalate),
        };
        JsonElement raw = JsonSerializer.Deserialize<JsonElement>($$"""{ "text": "{{marker}}" }""");
        ProviderResult flagged = Samples.BooleanAnswer(0.95) with { Raw = raw };
        ProviderResult failed = Samples.Failed(FailureKind.Unavailable);
        ProviderResult routed = Samples.Answer(new ChoiceValue("deny"), "jev");
        PolicyVerdict verdict = Evaluated(policy, new()
        {
            [new AttemptKey(Samples.Injection, 0)] = flagged,
            [new AttemptKey("route", 0)] = failed,
            [new AttemptKey("route", 1)] = routed,
        });

        RecordedRow row = RecordedRow.FromVerdict("r-001", verdict);

        row.Id.Should().Be("r-001");
        row.PolicyDigest.Should().Be(policy.Digest).And.Be(verdict.PolicyDigest);
        row.Retries.Should().BeNull();
        row.Attempts.Keys.Should().Equal(Samples.Injection, "route");
        row.Attempts[Samples.Injection].Keys.Should().Equal("local");
        row.Attempts[Samples.Injection]["local"].Should().BeSameAs(flagged);
        row.Attempts["route"].Keys.Should().Equal("local", "jev");
        row.Attempts["route"]["local"].Should().BeSameAs(failed);
        row.Attempts["route"]["jev"].Should().BeSameAs(routed);

        // A stored verdict can come back with a rule that holds no attempt; the row says nothing about that rule.
        PolicyVerdict trimmed = verdict with { Rules = [verdict.Rules[0] with { Attempts = [] }, verdict.Rules[1]] };
        RecordedRow.FromVerdict("r-002", trimmed).Attempts.Keys.Should().Equal("route");

        await Samples.RecordAsync(file.Path, Samples.Header(policy), row);
        string line = (await File.ReadAllLinesAsync(file.Path, TestContext.Current.CancellationToken))[1];
        line.Should().NotContain(marker).And.NotContain("\"raw\"");
        JsonNode.Parse(line)!["policyDigest"]!.GetValue<string>().Should().Be(policy.Digest);
    }

    [Fact]
    public void FromVerdict_Of_A_Cascade_That_Stopped_Early_Keeps_Only_The_Attempts_Made()
    {
        Policy policy = Samples.Guard(["local", "jev"], [Samples.Flagged()]);

        // local decides at binding 0, so the evaluator never asked jev.
        PolicyVerdict verdict = Evaluated(policy, new() { [new AttemptKey(Samples.Injection, 0)] = Samples.BooleanAnswer(0.95) });

        RecordedRow row = RecordedRow.FromVerdict("r-001", verdict);

        verdict.Rules.Single().DecidingBinding.Should().Be(0);
        row.Attempts.Should().ContainSingle().Which.Value.Keys.Should().Equal("local");
    }

    [Fact]
    public async Task ForExport_Writes_No_Run_Fields_And_Names_The_Source()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Samples.Guard(["local", "jev"], [Samples.Flagged()]);

        RecordingHeader header = Export(policy);
        await Samples.RecordAsync(file.Path, header);

        JsonObject line = JsonNode.Parse((await File.ReadAllLinesAsync(file.Path, TestContext.Current.CancellationToken))[0])!.AsObject();
        line.Select(property => property.Key).Should().BeEquivalentTo(
            "format", "policy", "datasets", "providers", "toolVersion", "recordedAt", "policyDigest", "source");
        line["format"]!.GetValue<string>().Should().Be(RecordingHeader.FormatV0);
        line["policyDigest"]!.GetValue<string>().Should().Be(policy.Digest);
        line["toolVersion"]!.GetValue<string>().Should().Be(PackageVersion());
        line["source"]!.ToJsonString().Should().Be("""{"kind":"export","name":"exporter","version":"2.1.0"}""");
        line["datasets"]!.AsArray().Should().ContainSingle();
        line["providers"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public void ForExport_Refuses_An_Empty_Source_Name_Or_Version()
    {
        Policy policy = Samples.Guard(["local"], [Samples.Flagged()]);

        Action noName = () => RecordingHeader.ForExport(policy, Datasets(), Providers(policy), " ", "2.1.0", _exportedAt);
        Action noVersion = () => RecordingHeader.ForExport(policy, Datasets(), Providers(policy), "exporter", "", _exportedAt);

        noName.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("sourceName");
        noVersion.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("sourceVersion");
    }

    [Fact]
    public async Task Recording_Round_Trips_Source_And_Policy_Digests()
    {
        using TempFile exported = TempFile.Write("");
        using TempFile run = TempFile.Write("");
        Policy policy = Samples.Guard(["local", "jev"], [Samples.Flagged()]);
        Policy shadow = policy with { Mode = PolicyMode.Shadow };
        RecordedRow first = RecordedRow.FromVerdict(
            "r-001",
            Evaluated(policy, new() { [new AttemptKey(Samples.Injection, 0)] = Samples.BooleanAnswer(0.95) }));
        RecordedRow second = RecordedRow.FromVerdict(
            "r-002",
            Evaluated(shadow, new() { [new AttemptKey(Samples.Injection, 0)] = Samples.BooleanAnswer(0.1) }));

        Recording export = await Samples.RecordAsync(exported.Path, Export(policy), first, second);
        Recording written = await Samples.RecordAsync(
            run.Path,
            Samples.Header(policy) with { PolicyDigest = policy.Digest },
            Samples.Recorded("r-001", (Samples.Injection, "local", Samples.BooleanAnswer(0.95))));

        export.Header.Source.Should().Be(new RecordingSource(RecordingSourceKind.Export, "exporter", "2.1.0"));
        export.Header.PolicyDigest.Should().Be(policy.Digest);
        export.Header.Parallel.Should().BeNull();
        export.Header.Timeout.Should().BeNull();
        export.Rows.Select(row => row.PolicyDigest).Should().Equal(policy.Digest, shadow.Digest);
        written.Header.Source.Should().BeNull("a header without a source is a run's");
        written.Header.PolicyDigest.Should().Be(policy.Digest);
        written.Header.Parallel.Should().Be(4);
        written.Header.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        written.Rows.Single().PolicyDigest.Should().BeNull();

        string[] lines = await File.ReadAllLinesAsync(run.Path, TestContext.Current.CancellationToken);
        lines[0].Should().NotContain("\"source\"");
        lines[1].Should().NotContain("\"policyDigest\"");
    }

    public static TheoryData<string, string> InvalidHeaders => new()
    {
        { "parallel", "'parallel'" },
        { "timeout", "'timeout'" },
        { "no name", "'source.name'" },
        { "no version", "'source.version'" },
        { "no kind", "'source.kind'" },
    };

    [Theory]
    [MemberData(nameof(InvalidHeaders))]
    public async Task Reader_Rejects_An_Invalid_Source_Or_An_Export_Header_With_Run_Fields(string change, string field)
    {
        using TempFile written = TempFile.Write("");
        Policy policy = Samples.Guard(["local"], [Samples.Flagged()]);
        await Samples.RecordAsync(written.Path, Export(policy));
        string[] lines = await File.ReadAllLinesAsync(written.Path, TestContext.Current.CancellationToken);
        JsonObject header = JsonNode.Parse(lines[0])!.AsObject();
        JsonObject source = header["source"]!.AsObject();
        switch (change)
        {
            case "parallel":
                header["parallel"] = 4;
                break;
            case "timeout":
                header["timeout"] = "00:00:30";
                break;
            case "no name":
                source.Remove("name");
                break;
            case "no version":
                source.Remove("version");
                break;
            default:
                source.Remove("kind");
                break;
        }

        using TempFile file = TempFile.Write(header.ToJsonString() + "\n");

        Action read = () => RecordingReader.Read(file.Path);

        read.Should().Throw<EvalsFormatException>().Which.Message.Should()
            .Contain(file.Path).And.Contain("line 1").And.Contain(field);
    }

    private static PolicyVerdict Evaluated(Policy policy, Dictionary<AttemptKey, ProviderResult> attempts) =>
        PolicyEvaluation.Evaluate(policy, attempts).Verdict
            ?? throw new InvalidOperationException("The attempts given do not complete the policy.");

    private static RecordingHeader Export(Policy policy) =>
        RecordingHeader.ForExport(policy, Datasets(), Providers(policy), "exporter", "2.1.0", _exportedAt);

    private static RecordedDataset[] Datasets() => [new RecordedDataset(Samples.DatasetPath, Samples.DatasetSha, Split: null)];

    private static RecordedProvider[] Providers(Policy policy) =>
        [.. policy.Bindings.Select(binding => new RecordedProvider(binding.ProviderId, "model-" + binding.ProviderId))];

    private static string PackageVersion() =>
        typeof(RecordingHeader).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
