using System.CommandLine;
using System.Text.Json;
using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class ReportVerbTests
{
    [Fact]
    public async Task Report_Prints_One_Table_Per_Ladder_Rung_With_The_Six_Rates_And_The_Outcome_Counts()
    {
        using BooleanFixture fixture = await BooleanFixture.CreateAsync();

        (int exit, string output, string error) = await InvokeAsync(fixture.ReportArgs());

        exit.Should().Be(ExitCodes.Success, error);
        (string[] headers, string[] warn) = TableAfter(output, "rung warn:");
        headers.Should().Equal(
            "tp", "fp", "tn", "fn", "accuracy", "precision", "recall", "f1", "fpr", "fnr", "failed", "abstained", "ambiguous");
        warn.Should().Equal("3", "2", "2", "1", "0.625", "0.600", "0.750", "0.667", "0.500", "0.250", "1", "0", "1");
        (_, string[] deny) = TableAfter(output, "rung deny:");
        deny.Should().Equal("2", "1", "3", "2", "0.625", "0.667", "0.500", "0.571", "0.250", "0.500", "1", "0", "1");
        output.Should().Contain("classified 8")
            .And.Contain("failed 1 (timeout 1), failure rate 0.100")
            .And.Contain("abstained 0, abstention rate 0.000")
            .And.Contain("ambiguous 1 (deny 1)");
        error.Should().BeEmpty();
    }

    [Fact]
    public async Task Report_Prints_The_Class_Table_And_Macro_F1_For_A_Choice_Rule()
    {
        Policy policy = Samples.Guard(FailureBehavior.Escalate, ["local"], [Samples.Route()]);
        (string Id, string Label, string Answer)[] rows =
        [
            ("c1", "allow", "allow"), ("c2", "allow", "allow"), ("c3", "review", "review"),
            ("c4", "review", "allow"), ("c5", "deny", "deny"), ("c6", "deny", "review"),
        ];
        using TempFile policyFile = WritePolicy(policy);
        using TempFile dataset = TempFile.Write(Lines(rows.Select(row => Line(row.Id, row.Label))));
        using TempFile recording = TempFile.Write("");
        await Samples.RecordAsync(
            recording.Path,
            Samples.Header(policy, DatasetReader.Read(dataset.Path).Sha256),
            [.. rows.Select(row => Samples.Recorded(row.Id, ("route", "local", Choice(row.Answer))))]);

        (int exit, string output, string error) = await InvokeAsync(
            ["report", "--policy", policyFile.Path, "--dataset", dataset.Path, "--recording", recording.Path]);

        exit.Should().Be(ExitCodes.Success, error);
        output.Should().Contain("accuracy 0.667, macro-F1 0.656");
        (string[] headers, _) = TableAfter(output, "classes:");
        headers.Should().Equal("label", "allow", "review", "deny", "support", "precision", "recall", "f1");
        ClassRow(output, "review").Should().Equal("review", "1", "1", "0", "2", "0.500", "0.500", "0.500");
        ClassRow(output, "deny").Should().Equal("deny", "0", "1", "1", "2", "1.000", "0.500", "0.667");
        output.Should().NotContain("rung ");
    }

    [Fact]
    public async Task Report_Prints_Not_Applicable_Naming_The_Kind_When_Evidence_Is_Not_Probability()
    {
        BooleanRule rule = Samples.Flagged();
        Policy policy = new(
            "guard",
            PolicyMode.Enforce,
            [rule],
            [
                new ProviderBinding("local", [
                    new RuleOperatingPoint(rule.Id, [
                        new Threshold(Verdict.Warn, EvidenceKind.Score, 0.5),
                        new Threshold(Verdict.Deny, EvidenceKind.Score, 0.8)]),
                ]),
            ],
            FailureBehavior.Escalate);
        (string Id, string Label, double Score)[] rows = [("s1", "true", 0.9), ("s2", "false", 0.2), ("s3", "true", 0.6)];
        using TempFile policyFile = WritePolicy(policy);
        using TempFile dataset = TempFile.Write(Lines(rows.Select(row => Line(row.Id, row.Label))));
        using TempFile recording = TempFile.Write("");
        await Samples.RecordAsync(
            recording.Path,
            Samples.Header(policy, DatasetReader.Read(dataset.Path).Sha256),
            [.. rows.Select(row => Samples.Recorded(row.Id, (Samples.Injection, "local", Scored(row.Score))))]);

        (int exit, string output, string error) = await InvokeAsync(
            ["report", "--policy", policyFile.Path, "--dataset", dataset.Path, "--recording", recording.Path]);

        exit.Should().Be(ExitCodes.Success, error);
        output.Should().Contain("calibration: not applicable: deciding evidence is score");
        output.Should().NotContain("ECE").And.NotContain("Brier");
    }

    public static TheoryData<string> Layouts => ["metadata", "files", "none"];

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task Report_States_How_Many_Rows_Passed_The_Filter_And_Which_Split_Layout_Applies(string layout)
    {
        // Five rows, three from source a; the recording misses s5, so four of the five were recorded.
        (string Id, string Split, string Source)[] rows =
            [("s1", "tune", "a"), ("s2", "tune", "a"), ("s3", "test", "a"), ("s4", "test", "b"), ("s5", "tune", "b")];
        Policy policy = Samples.Guard(FailureBehavior.Escalate, ["local"], [Samples.Flagged()]);
        using TempFile policyFile = WritePolicy(policy);
        using TempFile first = TempFile.Write(Lines(rows
            .Where(row => layout != "files" || row.Split == "tune")
            .Select(row => Line(row.Id, "true", split: layout == "metadata" ? row.Split : null, source: row.Source))));
        using TempFile second = TempFile.Write(Lines(rows
            .Where(row => row.Split == "test")
            .Select(row => Line(row.Id, "true", source: row.Source))));
        using TempFile recording = TempFile.Write("");
        RecordedDataset[] datasets = layout == "files"
            ? [
                new RecordedDataset(first.Path, DatasetReader.Read(first.Path).Sha256, "tune"),
                new RecordedDataset(second.Path, DatasetReader.Read(second.Path).Sha256, "test"),
            ]
            : [new RecordedDataset(first.Path, DatasetReader.Read(first.Path).Sha256, null)];
        await Samples.RecordAsync(
            recording.Path,
            Samples.Header(policy) with { Datasets = datasets },
            [.. rows.Take(4).Select(row => Samples.Recorded(row.Id, (Samples.Injection, "local", Samples.BooleanAnswer(0.8))))]);
        string[] inputs = layout == "files"
            ? ["--tune", first.Path, "--test", second.Path]
            : ["--dataset", first.Path];

        (int exit, string output, string error) = await InvokeAsync(
            ["report", "--policy", policyFile.Path, .. inputs, "--where", "metadata.source=a", "--recording", recording.Path]);

        exit.Should().Be(ExitCodes.Success, error);
        output.Should().Contain("4 of 5 recorded rows").And.Contain("3 of 4 passed the filter (metadata.source=a)");
        output.Should().Contain(layout switch
        {
            "metadata" => "split from metadata.split: 2 tune, 1 test, 0 unassigned",
            "files" => "split from --tune and --test: 2 tune, 1 test, 0 unassigned",
            _ => "no split: 0 tune, 0 test, 3 unassigned",
        });
    }

    [Fact]
    public async Task Report_Names_The_Policy_Mode_And_A_Shadow_Recording_Reports_The_Same_Numbers_As_Enforce()
    {
        using BooleanFixture enforce = await BooleanFixture.CreateAsync(PolicyMode.Enforce);
        using BooleanFixture shadow = await BooleanFixture.CreateAsync(PolicyMode.Shadow);
        using TempFile enforceJson = TempFile.Write("", ".json");
        using TempFile shadowJson = TempFile.Write("", ".json");

        (int enforceExit, string enforceText, _) = await InvokeAsync([.. enforce.ReportArgs(), "--out", enforceJson.Path]);
        (int shadowExit, string shadowText, _) = await InvokeAsync([.. shadow.ReportArgs(), "--out", shadowJson.Path]);

        enforceExit.Should().Be(ExitCodes.Success);
        shadowExit.Should().Be(ExitCodes.Success);
        enforceText.Should().Contain("mode enforce");
        shadowText.Should().Contain("mode shadow");
        Numbers(shadowJson.Path).Should().Be(Numbers(enforceJson.Path));
        TableAfter(shadowText, "rung deny:").Cells.Should().Equal(TableAfter(enforceText, "rung deny:").Cells);
        TableAfter(shadowText, "rung warn:").Cells.Should().Equal(TableAfter(enforceText, "rung warn:").Cells);
    }

    [Fact]
    public async Task Report_Writes_The_Json_Result_With_Out_And_Its_Counts_Match_The_Text()
    {
        using BooleanFixture fixture = await BooleanFixture.CreateAsync();
        using TempFile json = TempFile.Write("", ".json");

        (int exit, string output, _) = await InvokeAsync([.. fixture.ReportArgs(), "--out", json.Path]);

        exit.Should().Be(ExitCodes.Success);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(json.Path));
        JsonElement root = document.RootElement;
        root.GetProperty("verb").GetString().Should().Be("report");
        JsonElement report = root.GetProperty("report");
        JsonElement outcomes = report.GetProperty("outcomes");
        output.Should().Contain($"classified {outcomes.GetProperty("classified").GetInt32()}")
            .And.Contain($"failed {outcomes.GetProperty("failed").GetInt32()} (timeout 1)")
            .And.Contain($"ambiguous {outcomes.GetProperty("ambiguous").GetInt32()} (deny 1)");
        foreach (JsonElement rung in report.GetProperty("rungs").EnumerateArray())
        {
            JsonElement matrix = rung.GetProperty("matrix");
            (_, string[] cells) = TableAfter(output, $"rung {rung.GetProperty("rung").GetString()}:");
            cells[..4].Should().Equal(
                matrix.GetProperty("truePositives").GetInt32().ToString(),
                matrix.GetProperty("falsePositives").GetInt32().ToString(),
                matrix.GetProperty("trueNegatives").GetInt32().ToString(),
                matrix.GetProperty("falseNegatives").GetInt32().ToString());
        }

        JsonElement rows = root.GetProperty("rows");
        output.Should().Contain(
            $"{rows.GetProperty("recordedRows").GetInt32()} of {rows.GetProperty("datasetRows").GetInt32()} recorded rows");
    }

    [Fact]
    public async Task Report_Exits_1_On_A_Hash_Mismatch_And_0_With_Force()
    {
        using BooleanFixture fixture = await BooleanFixture.CreateAsync(sha: Samples.DatasetSha);

        (int refused, string refusedOutput, string refusedError) = await InvokeAsync(fixture.ReportArgs());
        (int forced, string forcedOutput, _) = await InvokeAsync([.. fixture.ReportArgs(), "--force"]);

        refused.Should().Be(ExitCodes.UsageOrData);
        refusedError.Should().Contain("--force");
        refusedOutput.Should().BeEmpty();
        forced.Should().Be(ExitCodes.Success);
        forcedOutput.Should().Contain("rung warn:");
    }

    private static async Task<(int Exit, string Output, string Error)> InvokeAsync(string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        RootCommand root = EvalsCli.Build(new CliIo(output, error));
        int exit = await root.Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false },
            TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    // The column headers and the first data row of the table printed under a heading line.
    private static (string[] Headers, string[] Cells) TableAfter(string output, string heading)
    {
        string[] lines = output.Split('\n');
        int at = Array.FindIndex(lines, line => line.StartsWith(heading, StringComparison.Ordinal));
        at.Should().BeGreaterThanOrEqualTo(0, $"the report has a '{heading}' section");
        return (Tokens(lines[at + 1]), Tokens(lines[at + 3]));
    }

    private static string[] ClassRow(string output, string label)
    {
        string[] lines = output.Split('\n');
        int at = Array.FindIndex(lines, line => line.StartsWith("classes:", StringComparison.Ordinal));
        return lines.Skip(at + 3).Select(Tokens).First(tokens => tokens.Length > 0 && tokens[0] == label);
    }

    private static string[] Tokens(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Every measured section of a result file, notes left out: the notes are where the mode is named.
    private static string Numbers(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return string.Join('\n', document.RootElement.GetProperty("report").EnumerateObject()
            .Where(section => section.Name != "notes")
            .Select(section => section.Name + "=" + section.Value.GetRawText()));
    }

    private static TempFile WritePolicy(Policy policy) =>
        TempFile.Write(JsonSerializer.Serialize(policy, SemanticPolicyJson.Options), ".json");

    private static string Lines(IEnumerable<string> lines) => string.Join('\n', lines) + "\n";

    private static string Line(string id, string label, string? split = null, string? source = null)
    {
        Dictionary<string, object> row = new()
        {
            ["id"] = id,
            ["input"] = "context",
            ["label"] = label is "true" or "false" ? bool.Parse(label) : label,
        };
        Dictionary<string, string> metadata = [];
        if (split is not null)
        {
            metadata["split"] = split;
        }

        if (source is not null)
        {
            metadata["source"] = source;
        }

        if (metadata.Count > 0)
        {
            row["metadata"] = metadata;
        }

        return JsonSerializer.Serialize(row);
    }

    private static ProviderResult Choice(string answer) =>
        Samples.Answer(new ChoiceValue(answer), "local", Samples.Probability((answer, 0.8)));

    private static ProviderResult Scored(double score) =>
        Samples.Answer(
            new BooleanValue(score >= 0.5),
            "local",
            new Evidence(EvidenceKind.Score, new Dictionary<string, double> { ["true"] = score, ["false"] = -score }));

    // Ten rows with hand-counted outcomes on the "warn at 0.6, deny at 0.9" policy: eight classified, one
    // timed out, one ambiguous. Warn: TP 3, FP 2, TN 2, FN 1. Deny: TP 2, FP 1, TN 3, FN 2.
    private sealed class BooleanFixture : IDisposable
    {
        private readonly TempFile _policy;
        private readonly TempFile _dataset;
        private readonly TempFile _recording;

        private BooleanFixture(TempFile policy, TempFile dataset, TempFile recording)
        {
            _policy = policy;
            _dataset = dataset;
            _recording = recording;
        }

        public static async Task<BooleanFixture> CreateAsync(PolicyMode mode = PolicyMode.Enforce, string? sha = null)
        {
            Policy policy = Samples.Guard(FailureBehavior.Escalate, ["local"], [Samples.Flagged()], mode);
            (string Id, string Label, double? PTrue)[] rows =
            [
                ("r01", "true", 0.95), ("r02", "true", 0.92), ("r03", "true", 0.70), ("r04", "true", 0.30),
                ("r05", "false", 0.10), ("r06", "false", 0.20), ("r07", "false", 0.65), ("r08", "false", 0.95),
                ("r09", "true", null), ("r10", "ambiguous", 0.95),
            ];
            TempFile policyFile = WritePolicy(policy);
            TempFile dataset = TempFile.Write(Lines(rows.Select(row => Line(row.Id, row.Label))));
            TempFile recording = TempFile.Write("");
            await Samples.RecordAsync(
                recording.Path,
                Samples.Header(policy, sha ?? DatasetReader.Read(dataset.Path).Sha256),
                [
                    .. rows.Select(row => Samples.Recorded(
                        row.Id,
                        (Samples.Injection, "local", row.PTrue is { } p ? Samples.BooleanAnswer(p) : Samples.Failed(FailureKind.Timeout)))),
                ]);
            return new BooleanFixture(policyFile, dataset, recording);
        }

        public string[] ReportArgs() =>
            ["report", "--policy", _policy.Path, "--dataset", _dataset.Path, "--recording", _recording.Path];

        public void Dispose()
        {
            _policy.Dispose();
            _dataset.Dispose();
            _recording.Dispose();
        }
    }
}
