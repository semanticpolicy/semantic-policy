using System.Globalization;
using System.Text.Json;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

// Rows that carry their own Choice options, through replay, the rule-shape check and the metrics. Every option
// description here carries the marker, which nothing the tool prints may contain.
public sealed class RowOptionsTests
{
    private const string _secret = "secret-description";

    [Fact]
    public async Task Replay_Decides_Each_Row_Under_Its_Own_Options()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Samples.Guard(FailureBehavior.Escalate, ["local"], [Samples.Route()]);
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(policy),
            Samples.Recorded("denied", ("route", "local", Choice("same"))),
            Samples.Recorded("allowed", ("route", "local", Choice("same"))));
        LoadedInputs inputs = Samples.Inputs(
            policy,
            Samples.Dataset(
                Samples.Row("denied", "same") with { Options = Options(("same", Verdict.Deny), ("other", Verdict.Allow)) },
                Samples.Row("allowed", "same", 2) with { Options = Options(("same", Verdict.Allow), ("other", Verdict.Deny)) }));

        IReadOnlyList<EvaluatedRow> replayed = ReplaySet.Load(recording, inputs, force: false).Evaluate();

        replayed.Select(row => (row.Row.Id, row.Verdict.Verdict)).Should().Equal(
            ("denied", Verdict.Deny),
            ("allowed", Verdict.Allow));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Load_Compares_Recorded_Options_Only_When_A_Row_Uses_The_Policys(bool everyRowCarriesItsOwn)
    {
        using TempFile file = TempFile.Write("");
        ChoiceRule route = Samples.Route();
        Policy recorded = Samples.Guard(FailureBehavior.Escalate, ["local"], [
            route with { Options = [.. route.Options.Select(option => option with { Description = "other" })] },
        ]);
        Policy current = Samples.Guard(FailureBehavior.Escalate, ["local"], [route]);
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(recorded),
            Samples.Recorded("a", ("route", "local", Choice("same"))),
            Samples.Recorded("b", ("route", "local", Choice("allow"))));
        DatasetRow second = Samples.Row("b", "allow", 2);
        LoadedInputs inputs = Samples.Inputs(
            current,
            Samples.Dataset(
                Samples.Row("a", "same") with { Options = Options(("same", Verdict.Deny), ("other", Verdict.Allow)) },
                everyRowCarriesItsOwn ? second with { Options = Options(("allow", Verdict.Allow), ("other", Verdict.Deny)) } : second));

        Func<ReplaySet> load = () => ReplaySet.Load(recording, inputs, force: false);

        if (everyRowCarriesItsOwn)
        {
            load.Should().NotThrow().Which.Rows.Should().HaveCount(2);
        }
        else
        {
            load.Should().Throw<EvalsException>().Which.Message.Should().Contain("'route'").And.Contain("'options'");
        }
    }

    [Fact]
    public async Task Report_Computes_Choice_Metrics_Over_The_Union_Of_Seen_Keys_When_Options_Vary()
    {
        // Two option sets: the policy's (allow, review, deny) and a row set (billing, refund). The classes are the keys
        // seen as a label or an answer, in ordinal order, so deny, which no row uses, is not one.
        using OptionsFixture fixture = await OptionsFixture.CreateAsync(
            Samples.Guard(FailureBehavior.Escalate, ["local"], [Samples.Route()]),
            [
                new("c1", "allow", "allow", null),
                new("c2", "review", "allow", null),
                new("c3", "refund", "refund", ["billing", "refund"]),
                new("c4", "billing", "refund", ["billing", "refund"]),
                new("c5", "refund", "billing", ["billing", "refund"]),
            ]);

        CliRun run = await CliFixture.InvokeAsync([.. fixture.ReportArgs, "--out", fixture.OutPath]);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(fixture.OutPath));
        JsonElement report = result.RootElement.GetProperty("report");
        JsonElement classes = report.GetProperty("classes");
        classes.GetProperty("classes").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("allow", "billing", "refund", "review");
        classes.GetProperty("accuracy").GetDouble().Should().BeApproximately(2d / 5, 1e-9);
        classes.GetProperty("macroF1").GetDouble().Should().BeApproximately((2d / 3 + 0 + 0.5 + 0) / 4, 1e-9);
        report.GetProperty("optionSets").GetInt32().Should().Be(2);
        run.Output.Should().Contain("accuracy 0.400").And.Contain("macro-F1 0.292")
            .And.Contain("the options vary between rows: 2 distinct sets");
        TableAfter(run.Output, "classes:").Should().Equal(
            "label", "allow", "billing", "refund", "review", "support", "precision", "recall", "f1");
        string[] everything = [run.Output, run.Error, File.ReadAllText(fixture.OutPath)];
        everything.Should().AllSatisfy(written => written.Should().NotContain(_secret));
    }

    [Fact]
    public async Task Report_Of_One_Option_Set_Carries_No_Option_Sets()
    {
        // Every row carries the same set of its own, so the classes are that set's keys in declared order, as a policy
        // with those options would give.
        using OptionsFixture fixture = await OptionsFixture.CreateAsync(
            Samples.Guard(FailureBehavior.Escalate, ["local"], [Samples.Route()]),
            [
                new("c1", "refund", "refund", ["refund", "billing"]),
                new("c2", "billing", "refund", ["refund", "billing"]),
            ]);

        CliRun run = await CliFixture.InvokeAsync([.. fixture.ReportArgs, "--out", fixture.OutPath]);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(fixture.OutPath));
        JsonElement report = result.RootElement.GetProperty("report");
        report.TryGetProperty("optionSets", out _).Should().BeFalse();
        report.GetProperty("classes").GetProperty("classes").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("refund", "billing");
        run.Output.Should().NotContain("options vary");
    }

    [Theory]
    [InlineData("varying sets, 10 classes", 10, true)]
    [InlineData("varying sets, 11 classes", 11, false)]
    [InlineData("one set of 11 options", 11, true)]
    public async Task Report_Prints_The_Confusion_Matrix_Up_To_Ten_Classes(string shape, int classes, bool matrix)
    {
        string[] keys = [.. Enumerable.Range(1, shape.StartsWith("varying", StringComparison.Ordinal) ? classes - 1 : classes)
            .Select(index => string.Create(CultureInfo.InvariantCulture, $"k{index:D2}"))];
        Policy policy;
        List<OptionRow> rows = [];
        if (shape.StartsWith("varying", StringComparison.Ordinal))
        {
            // The policy's set gives "allow"; the row set gives the rest.
            policy = Samples.Guard(FailureBehavior.Escalate, ["local"], [Samples.Route()]);
            rows.Add(new("p", "allow", "allow", null));
            rows.AddRange(keys.Select(key => new OptionRow(key, key, key, keys)));
        }
        else
        {
            ChoiceRule wide = Samples.Route() with
            {
                Options = [.. keys.Select(key => new ChoiceOption(key, _secret + "-" + key, Verdict.Allow))],
            };
            policy = Samples.Guard(FailureBehavior.Escalate, ["local"], [wide]);
            rows.AddRange(keys.Select(key => new OptionRow(key, key, key, null)));
        }

        using OptionsFixture fixture = await OptionsFixture.CreateAsync(policy, rows);

        CliRun run = await CliFixture.InvokeAsync([.. fixture.ReportArgs, "--out", fixture.OutPath]);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        string[] headers = TableAfter(run.Output, "classes:");
        if (matrix)
        {
            headers.Should().HaveCount(classes + 5);
            run.Output.Should().NotContain("--out");
        }
        else
        {
            headers.Should().Equal("label", "support", "precision", "recall", "f1");
            run.Output.Should().Contain($"the confusion matrix of {classes} classes is in the JSON result");
        }

        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(fixture.OutPath));
        JsonElement counts = result.RootElement.GetProperty("report").GetProperty("classes").GetProperty("counts");
        counts.EnumerateObject().Should().HaveCount(classes)
            .And.AllSatisfy(row => row.Value.EnumerateObject().Should().HaveCount(classes));
    }

    [Fact]
    public async Task Min_Accuracy_Requirement_Counts_Keys_Only_A_Rows_Own_Options_Have()
    {
        // Three of four rows are answered as labelled, two of them with keys the policy does not have. Counting the
        // policy's keys alone would score one of two.
        using OptionsFixture fixture = await OptionsFixture.CreateAsync(
            Samples.Guard(FailureBehavior.Escalate, ["local"], [Samples.Route()]),
            [
                new("c1", "allow", "allow", null),
                new("c2", "refund", "refund", ["billing", "refund"]),
                new("c3", "billing", "billing", ["billing", "refund"]),
                new("c4", "review", "allow", null),
            ]);

        CliRun run = await CliFixture.InvokeAsync([.. fixture.ReportArgs, "--require", "min-accuracy=0.7"]);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        run.Output.Should().Contain("min-accuracy=0.7: passed at 0.750 (3/4)");
    }

    private static ProviderResult Choice(string answer) =>
        Samples.Answer(new ChoiceValue(answer), "local", Samples.Probability((answer, 0.8)));

    private static Dictionary<string, IReadOnlyList<ChoiceOption>> Options(params (string Key, Verdict Verdict)[] options) =>
        new(StringComparer.Ordinal)
        {
            ["route"] = [.. options.Select(option => new ChoiceOption(option.Key, _secret + "-" + option.Key, option.Verdict))],
        };

    // The column headers of the table printed right under a heading line.
    private static string[] TableAfter(string output, string heading)
    {
        string[] lines = output.Split('\n');
        int at = Array.FindIndex(lines, line => line.StartsWith(heading, StringComparison.Ordinal));
        at.Should().BeGreaterThanOrEqualTo(0, $"the report has a '{heading}' section");
        return lines[at + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // One row: its label, what the provider answered, and its own option keys, or null for the policy's.
    private sealed record OptionRow(string Id, string Label, string Answer, string[]? Keys);

    // A policy, a dataset written by the package's writer and a recording of it, each in a temp file.
    private sealed class OptionsFixture : IDisposable
    {
        private readonly TempFile _policy;
        private readonly TempFile _dataset;
        private readonly TempFile _recording;
        private readonly TempFile _out;

        private OptionsFixture(TempFile policy, TempFile dataset, TempFile recording, TempFile output)
        {
            _policy = policy;
            _dataset = dataset;
            _recording = recording;
            _out = output;
        }

        public string[] ReportArgs =>
            ["report", "--policy", _policy.Path, "--dataset", _dataset.Path, "--recording", _recording.Path];

        public string OutPath => _out.Path;

        public static async Task<OptionsFixture> CreateAsync(Policy policy, IReadOnlyList<OptionRow> rows)
        {
            CancellationToken cancellation = TestContext.Current.CancellationToken;
            OptionsFixture fixture = new(
                TempFile.Write(JsonSerializer.Serialize(policy, SemanticPolicyJson.Options), ".json"),
                TempFile.Write(""),
                TempFile.Write(""),
                TempFile.Write("", ".json"));
            string sha256;
            await using (DatasetWriter writer = await DatasetWriter.CreateAsync(fixture._dataset.Path, cancellation))
            {
                foreach (OptionRow row in rows)
                {
                    DatasetRow written = new(row.Id, SemanticContext.FromText("context"), Samples.Label(row.Label));
                    if (row.Keys is { } keys)
                    {
                        written = written with
                        {
                            Options = Options([.. keys.Select((key, index) => (key, index == 0 ? Verdict.Allow : Verdict.Deny))]),
                        };
                    }

                    await writer.WriteAsync(written, cancellation);
                }

                sha256 = await writer.CompleteAsync(cancellation);
            }

            await Samples.RecordAsync(
                fixture._recording.Path,
                Samples.Header(policy, sha256),
                [.. rows.Select(row => Samples.Recorded(row.Id, ("route", "local", Choice(row.Answer))))]);
            return fixture;
        }

        public void Dispose()
        {
            _policy.Dispose();
            _dataset.Dispose();
            _recording.Dispose();
            _out.Dispose();
        }
    }
}
