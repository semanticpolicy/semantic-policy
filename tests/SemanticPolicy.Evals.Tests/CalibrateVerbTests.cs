using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Policies;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class CalibrateVerbTests
{
    private const string _local = "local";

    public static TheoryData<string> Sources { get; } =
        new("score", "score-wide", "score-deny-above-1", "probability", "probability-separable", "logit");

    // Each case pairs a point's evidence kind and thresholds with the readings a provider of that kind returns, and
    // names the transform calibrate must choose for it.
    [Theory]
    [MemberData(nameof(Sources))]
    public async Task Calibrated_Policy_Gives_Every_Recorded_Row_The_Verdict_The_Original_Gave(string source)
    {
        SourceCase test = source switch
        {
            "score" => new(EvidenceKind.Score, 0.5, 0.8, Overlapping(), v => Both(EvidenceKind.Score, v, 1 - v), CalibrationTransform.LogOdds),

            // A score on 0 to 10, with both thresholds among the values: the log-odds would hold everything above 1 to one value.
            "score-wide" => new(EvidenceKind.Score, 4, 7, Overlapping(), v => Both(EvidenceKind.Score, 10 * v, 10 - (10 * v)),
                CalibrationTransform.Identity),

            // Every value in [0, 1], but deny above 1 says the scale goes further, and a row at 1.0 only warns.
            "score-deny-above-1" => new(EvidenceKind.Score, 0.5, 1.5, Overlapping(), v => Both(EvidenceKind.Score, v, 1 - v),
                CalibrationTransform.Identity),
            "probability" => new(EvidenceKind.Probability, 0.6, 0.9, Overlapping(), OneSided, CalibrationTransform.LogOdds),
            "probability-separable" => new(EvidenceKind.Probability, 0.6, 0.9, Separable(),
                v => Both(EvidenceKind.Probability, v, 1 - v), CalibrationTransform.LogOdds),
            "logit" => new(EvidenceKind.Logit, 0, 2, Overlapping(), v => Both(EvidenceKind.Logit, (10 * v) - 5, 5 - (10 * v)),
                CalibrationTransform.Identity),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        RuleOperatingPoint point = Point(test.Kind, test.Warn, test.Deny);
        using CliFixture fixture = await CliFixture.CreateAsync(
            Single(point),
            [.. test.Grid.Select(row => Row(row.Label, null, test.Reading(row.Value)))]);
        string written = OutPolicy(fixture);

        CliRun run = await fixture.RunAsync("calibrate", "--out-policy", written);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        RuleOperatingPoint calibrated = PolicyFile.Load(written).Bindings[0].OperatingPoints[0];
        calibrated.Calibration!.Transform.Should().Be(test.Transform);
        calibrated.Thresholds.Should().OnlyContain(threshold => threshold.Kind == EvidenceKind.Probability);
        calibrated.Gate.Should().Be(point.Gate);
        (string Id, Verdict Verdict)[] original = Verdicts(fixture.PolicyPath, fixture);
        original.Select(row => row.Verdict).Should().Contain([Verdict.Allow, Verdict.Warn]);
        if (source != "score-deny-above-1")
        {
            original.Select(row => row.Verdict).Should().Contain(Verdict.Deny);
        }

        Verdicts(written, fixture).Should().Equal(original);
    }

    [Fact]
    public async Task Calibrate_Recovers_The_Map_The_Labels_Were_Drawn_From()
    {
        // At each score, the flagged share of 200 rows is σ(1.5 · logit(s) − 0.5), rounded to whole rows.
        const double slope = 1.5;
        const double intercept = -0.5;
        List<FixtureRow> rows = [];
        for (int step = 1; step <= 9; step++)
        {
            double score = step / 10.0;
            double share = 1 / (1 + Math.Exp(-((slope * Math.Log(score / (1 - score))) + intercept)));
            int flagged = (int)Math.Round(200 * share);
            rows.AddRange(Enumerable.Range(0, 200)
                .Select(index => Row(index < flagged ? "true" : "false", null, Both(EvidenceKind.Score, score, 1 - score))));
        }

        using CliFixture fixture = await CliFixture.CreateAsync(Single(Point(EvidenceKind.Score, 0.5, 0.8)), rows);
        string written = OutPolicy(fixture);

        CliRun run = await fixture.RunAsync("calibrate", "--out-policy", written);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        EvidenceCalibration calibration = PolicyFile.Load(written).Bindings[0].OperatingPoints[0].Calibration!;
        calibration.Transform.Should().Be(CalibrationTransform.LogOdds);
        calibration.Slope.Should().BeApproximately(slope, 0.05);
        calibration.Intercept.Should().BeApproximately(intercept, 0.05);
    }

    [Fact]
    public async Task Calibrate_On_A_Calibrated_Point_Replaces_Its_Calibration_And_Keeps_Every_Verdict()
    {
        EvidenceCalibration old = new(CalibrationMethod.Platt, EvidenceKind.Score, CalibrationTransform.LogOdds, 2.0, 1.0);
        using CliFixture fixture = await CliFixture.CreateAsync(
            Single(Calibrated(old)),
            [.. Overlapping().Select(row => Row(row.Label, null, Both(EvidenceKind.Score, row.Value, 1 - row.Value)))]);
        string written = OutPolicy(fixture);

        CliRun run = await fixture.RunAsync("calibrate", "--out-policy", written);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        EvidenceCalibration refitted = PolicyFile.Load(written).Bindings[0].OperatingPoints[0].Calibration!;
        refitted.SourceKind.Should().Be(EvidenceKind.Score);
        refitted.Slope.Should().NotBe(old.Slope);
        refitted.Intercept.Should().NotBe(old.Intercept);
        Verdicts(written, fixture).Should().Equal(Verdicts(fixture.PolicyPath, fixture));
    }

    public static TheoryData<string> Layouts { get; } = new("metadata", "none", "files");

    // Both runs are in this process, so on one machine: another operating system may differ in the last digits.
    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task Calibrated_Policy_Records_What_It_Was_Fitted_On_And_Is_Byte_Identical_On_A_Second_Run(string layout)
    {
        // Every third row of each label is a test row, so tune holds ten of each.
        (string Label, double Value)[] grid = Overlapping();
        FixtureRow[] rows =
        [
            .. grid.Select((row, index) => Row(
                row.Label,
                layout == "none" ? null : index % 15 % 3 == 2 ? "test" : "tune",
                Both(EvidenceKind.Score, row.Value, 1 - row.Value))),
        ];
        Policy policy = Single(Point(EvidenceKind.Score, 0.5, 0.8));
        using CliFixture fixture = await CliFixture.CreateAsync(policy, rows);
        string written = OutPolicy(fixture);
        string[] inputs = layout == "files"
            ? await TwoFilesAsync(fixture, policy, rows)
            : ["--dataset", fixture.DatasetPath, "--recording", fixture.RecordingPath];
        string[] args = ["calibrate", "--policy", fixture.PolicyPath, .. inputs, "--out-policy", written];

        CliRun first = await CliFixture.InvokeAsync(args);
        byte[] firstBytes = await File.ReadAllBytesAsync(written, TestContext.Current.CancellationToken);
        CliRun second = await CliFixture.InvokeAsync(args);

        first.ExitCode.Should().Be(ExitCodes.Success, first.Error);
        second.ExitCode.Should().Be(ExitCodes.Success, second.Error);
        (await File.ReadAllBytesAsync(written, TestContext.Current.CancellationToken)).Should().Equal(firstBytes);
        string text = Encoding.UTF8.GetString(firstBytes);
        firstBytes[0].Should().Be((byte)'{', "the file starts without a byte-order mark");
        text.Should().NotContain("\r").And.EndWith("}\n").And.Contain("\n  \"");

        JsonElement provenance = JsonDocument.Parse(text).RootElement
            .GetProperty("bindings")[0].GetProperty("operatingPoints")[0].GetProperty("calibration").GetProperty("provenance");
        string digestOf = layout == "files" ? inputs[1] : fixture.DatasetPath;
        provenance.GetProperty("model").GetString().Should().Be("model-local");
        provenance.GetProperty("datasetDigest").GetString().Should().Be("sha256:" + DatasetReader.Read(digestOf).Sha256);
        int perLabel = layout == "none" ? 15 : 10;
        provenance.GetProperty("flaggedRows").GetInt32().Should().Be(perLabel);
        provenance.GetProperty("otherRows").GetInt32().Should().Be(perLabel);
        string[] members = [.. provenance.EnumerateObject().Select(member => member.Name)];
        if (layout == "metadata")
        {
            provenance.GetProperty("split").GetString().Should().Be("tune");
            members.Should().Equal("model", "datasetDigest", "split", "flaggedRows", "otherRows");
        }
        else
        {
            members.Should().Equal("model", "datasetDigest", "flaggedRows", "otherRows");
        }

        if (layout == "none")
        {
            first.Output.Should().Contain("the same data (no split)");
        }
    }

    [Fact]
    public async Task Calibrate_Names_The_Model_Most_Fitting_Rows_Report_And_Counts_The_Others()
    {
        // Twelve rows of each label, alternating; the first 14 name model-a, the next 8 model-b, the last 2 none.
        FixtureRow[] rows =
        [
            .. Enumerable.Range(0, 24).Select(index =>
            {
                bool flagged = index % 2 == 0;
                double value = flagged ? (6 + (index / 2)) / 20.0 : index / 2 / 20.0;
                string? model = index < 14 ? "model-a" : index < 22 ? "model-b" : null;
                return Row(flagged ? "true" : "false", null, Both(EvidenceKind.Score, value, 1 - value, model));
            }),
        ];
        using CliFixture fixture = await CliFixture.CreateAsync(Single(Point(EvidenceKind.Score, 0.5, 0.8)), rows);
        string written = OutPolicy(fixture);

        CliRun run = await fixture.RunAsync("calibrate", "--out-policy", written);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        PolicyFile.Load(written).Bindings[0].OperatingPoints[0].Calibration!.Provenance!.Model.Should().Be("model-a");
        JsonElement section = fixture.ReadOut().GetProperty("calibrate");
        section.GetProperty("model").GetString().Should().Be("model-a");
        section.GetProperty("otherModelRows").GetInt32().Should().Be(8);
        run.Output.Should().Contain("model-a").And.Contain("8 fitting rows name another model");
    }

    // Ambiguous rows and rows the binding failed on sit beside the counted ones, labelled both ways, and count for nothing.
    [Theory]
    [InlineData(9, 10, ExitCodes.UsageOrData)]
    [InlineData(10, 9, ExitCodes.UsageOrData)]
    [InlineData(10, 10, ExitCodes.Success)]
    public async Task Calibrate_Exits_1_Naming_Both_Counts_Below_Ten_Flagged_Or_Ten_Other_Fitting_Rows(
        int flagged,
        int other,
        int exitCode)
    {
        FixtureRow[] rows =
        [
            .. Enumerable.Range(0, flagged).Select(k => Row("true", null, Both(EvidenceKind.Score, (k + 6) / 20.0, 1 - ((k + 6) / 20.0)))),
            .. Enumerable.Range(0, other).Select(k => Row("false", null, Both(EvidenceKind.Score, k / 20.0, 1 - (k / 20.0)))),
            .. Enumerable.Range(0, 3).Select(_ => Row("ambiguous", null, Both(EvidenceKind.Score, 0.9, 0.1))),
            .. Enumerable.Range(0, 2).Select(_ => Row("true", null, Samples.Failed(FailureKind.Timeout))),
            .. Enumerable.Range(0, 2).Select(_ => Row("false", null, Samples.Failed(FailureKind.Timeout))),
        ];
        using CliFixture fixture = await CliFixture.CreateAsync(Single(Point(EvidenceKind.Score, 0.5, 0.8)), rows);
        string written = OutPolicy(fixture);

        CliRun run = await fixture.RunAsync("calibrate", "--out-policy", written);

        run.ExitCode.Should().Be(exitCode, run.Error);
        if (exitCode == ExitCodes.Success)
        {
            File.Exists(written).Should().BeTrue();
            return;
        }

        run.Error.Should().Contain($"{flagged} flagged").And.Contain($"{other} other");
        File.Exists(written).Should().BeFalse();
    }

    public static TheoryData<string> BeforeCases { get; } = new("probability", "score", "refit-score");

    [Theory]
    [MemberData(nameof(BeforeCases))]
    public async Task Calibrate_Reports_Before_Calibration_Only_When_The_Input_Point_Reads_A_Probability(string input)
    {
        using CliFixture fixture = await BeforeFixtureAsync(input);

        CliRun run = await fixture.RunAsync("calibrate", "--out-policy", OutPolicy(fixture));

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        JsonElement section = fixture.ReadOut().GetProperty("calibrate");
        JsonElement before = section.GetProperty("before");
        JsonElement after = section.GetProperty("after");
        after.GetProperty("applicable").GetBoolean().Should().BeTrue();
        after.GetProperty("ece").ValueKind.Should().Be(JsonValueKind.Number);
        if (input == "score")
        {
            before.GetProperty("applicable").GetBoolean().Should().BeFalse();
            before.GetProperty("kindsFound").EnumerateArray().Select(kind => kind.GetString()).Should().Equal("score");
            run.Output.Should().Contain("before calibration: not applicable: deciding evidence is score");
            return;
        }

        before.GetProperty("applicable").GetBoolean().Should().BeTrue();
        before.GetProperty("ece").ValueKind.Should().Be(JsonValueKind.Number);
        before.GetProperty("brier").ValueKind.Should().Be(JsonValueKind.Number);
        run.Output.Should().Contain("before calibration: ECE").And.Contain("after calibration: ECE");
        if (input == "refit-score")
        {
            before.GetProperty("ece").GetDouble().Should().NotBe(after.GetProperty("ece").GetDouble());
            before.GetProperty("brier").GetDouble().Should().NotBe(after.GetProperty("brier").GetDouble());
        }
    }

    public static TheoryData<string> Refusals { get; } =
        new("choice-rule", "score-rule", "margin", "unknown", "anti-ranked", "empty-out", "two-bindings",
            "threshold-in-clamp", "refit-above-old-top");

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Calibrate_Refuses_What_It_Cannot_Calibrate_And_Writes_No_File(string refusal)
    {
        IEnumerable<FixtureRow> scores = Overlapping()
            .Select(row => Row(row.Label, null, Both(EvidenceKind.Score, row.Value, 1 - row.Value)));
        RefusalCase test = refusal switch
        {
            "choice-rule" => new(
                new Policy("guard", PolicyMode.Enforce, [Samples.Route()], [new ProviderBinding(_local, [])], FailureBehavior.Escalate),
                [Row("allow", null, Samples.Answer(new ChoiceValue("allow")))],
                "Boolean"),
            "score-rule" => new(
                new Policy("guard", PolicyMode.Enforce, [Samples.Severity()], [new ProviderBinding(_local, [])], FailureBehavior.Escalate),
                [Row("serious", null, Samples.Answer(new ScoreValue("serious", 2)))],
                "Boolean"),
            "margin" => new(Single(Point(EvidenceKind.Margin, 0.2, 0.5, gate: null)), [.. scores], "margin"),
            "unknown" => new(Single(Point(EvidenceKind.Unknown, 0.2, 0.5, gate: null)), [.. scores], "unknown"),

            // Flagged rows score low and the others high: the fitted slope comes out below zero.
            "anti-ranked" => new(
                Single(Point(EvidenceKind.Score, 0.5, 0.8)),
                [.. Overlapping().Select(row => Row(row.Label == "true" ? "false" : "true", null,
                    Both(EvidenceKind.Score, row.Value, 1 - row.Value)))],
                "slope"),
            "empty-out" => new(Single(Point(EvidenceKind.Score, 0.5, 0.8)), [.. scores], "--out-policy names no file"),

            // Deny at 1.0 and a row just below it: the log-odds clamp gives both one probability, so the row would deny.
            "threshold-in-clamp" => new(
                Single(Point(EvidenceKind.Probability, 0.6, 1.0)),
                [
                    .. Overlapping().Select(row => Row(row.Label, null, Both(EvidenceKind.Probability, row.Value, 1 - row.Value))),
                    Row("true", null, Both(EvidenceKind.Probability, 0.9999995, 0.0000005)),
                ],
                "'r30' warn → deny"),

            // The old map tops out near 0.8, so its deny at 0.9 never fired; taken back, it lands in the clamp, where
            // the new map puts the row at 1.0, which only warned.
            "refit-above-old-top" => new(
                Single(Calibrated(new EvidenceCalibration(
                    CalibrationMethod.Platt, EvidenceKind.Score, CalibrationTransform.LogOdds, 0.1, 0))),
                [.. scores],
                "'r14' warn → deny"),
            "two-bindings" => new(
                new Policy(
                    "guard",
                    PolicyMode.Enforce,
                    [Samples.Flagged()],
                    [new ProviderBinding(_local, [Point(EvidenceKind.Score, 0.5, 0.8)]), new ProviderBinding("jev", [Point(EvidenceKind.Score, 0.5, 0.8)])],
                    FailureBehavior.Escalate),
                [
                    .. Overlapping().Select(row => FixtureRow.Of(
                        row.Label,
                        null,
                        (_local, Both(EvidenceKind.Score, row.Value, 1 - row.Value)),
                        ("jev", Both(EvidenceKind.Score, row.Value, 1 - row.Value)))),
                ],
                "--provider"),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
        };
        using CliFixture fixture = await CliFixture.CreateAsync(test.Policy, test.Rows);
        string written = refusal == "empty-out" ? string.Empty : OutPolicy(fixture);

        CliRun run = await fixture.RunAsync("calibrate", "--out-policy", written);

        run.ExitCode.Should().Be(ExitCodes.UsageOrData, run.Output);
        run.Error.Should().Contain(test.Cause);
        File.Exists(written).Should().BeFalse();
    }

    // Each case points one output at the file of another option, an input or an output named before it.
    public static TheoryData<string, string> SharedFiles { get; } = new()
    {
        { "--out-policy", "--policy" },
        { "--out", "--policy" },
        { "--diagram", "--recording" },
        { "--out", "--dataset" },
        { "--out", "--out-policy" },
        { "--diagram", "--out" },
    };

    [Theory]
    [MemberData(nameof(SharedFiles))]
    public async Task Calibrate_Refuses_An_Output_That_Is_An_Input_Or_Another_Output_And_Changes_No_File(
        string output,
        string other)
    {
        using CliFixture fixture = await CliFixture.CreateAsync(
            Single(Point(EvidenceKind.Score, 0.5, 0.8)),
            [.. Overlapping().Select(row => Row(row.Label, null, Both(EvidenceKind.Score, row.Value, 1 - row.Value)))]);
        string directory = Path.GetDirectoryName(fixture.PolicyPath)!;
        string[] inputs = [fixture.PolicyPath, fixture.DatasetPath, fixture.RecordingPath];
        Dictionary<string, string> paths = new(StringComparer.Ordinal)
        {
            ["--policy"] = fixture.PolicyPath,
            ["--dataset"] = fixture.DatasetPath,
            ["--recording"] = fixture.RecordingPath,
            ["--out-policy"] = OutPolicy(fixture),
            ["--out"] = Path.Combine(directory, "result.json"),
            ["--diagram"] = Path.Combine(directory, "diagram.svg"),
        };
        string[] outputs = [paths["--out-policy"], paths["--out"], paths["--diagram"]];
        paths[output] = paths[other];
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        byte[][] before = await Task.WhenAll(inputs.Select(path => File.ReadAllBytesAsync(path, cancellation)));

        CliRun run = await CliFixture.InvokeAsync(
        [
            "calibrate",
            "--policy", paths["--policy"],
            "--dataset", paths["--dataset"],
            "--recording", paths["--recording"],
            "--out-policy", paths["--out-policy"],
            "--out", paths["--out"],
            "--diagram", paths["--diagram"],
        ]);

        run.ExitCode.Should().Be(ExitCodes.UsageOrData, run.Output);
        run.Error.Should().Contain($"{output} '{paths[output]}' is").And.Contain($"the {other} file");
        for (int index = 0; index < inputs.Length; index++)
        {
            (await File.ReadAllBytesAsync(inputs[index], cancellation)).Should().Equal(before[index]);
        }

        outputs.Should().OnlyContain(path => !File.Exists(path));
    }

    [Theory]
    [MemberData(nameof(BeforeCases))]
    public async Task Calibrate_Diagram_Overlays_Before_And_After_Or_Draws_After_Alone_Saying_Why(string input)
    {
        using CliFixture fixture = await BeforeFixtureAsync(input);
        string diagram = Path.Combine(Path.GetDirectoryName(fixture.PolicyPath)!, "diagram.svg");

        CliRun run = await fixture.RunAsync("calibrate", "--out-policy", OutPolicy(fixture), "--diagram", diagram);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        string svg = await File.ReadAllTextAsync(diagram, TestContext.Current.CancellationToken);
        if (input == "score")
        {
            svg.Should().NotContain("class=\"series").And.Contain("class=\"point\"");
            run.Error.Should().Contain("before not drawn: deciding evidence is score");
            return;
        }

        svg.Should().Contain("class=\"series before\"").And.Contain("class=\"series after\"");
        svg.Should().Contain(">before calibration<").And.Contain(">after calibration<");
        run.Error.Should().NotContain("before not drawn");
    }

    // The probability case reads one-sided evidence, as a hosted provider returns it; the re-fit starts from a point
    // calibrated on a score.
    private static Task<CliFixture> BeforeFixtureAsync(string input)
    {
        RuleOperatingPoint point = input switch
        {
            "probability" => Point(EvidenceKind.Probability, 0.6, 0.9),
            "score" => Point(EvidenceKind.Score, 0.5, 0.8),
            _ => Calibrated(new EvidenceCalibration(
                CalibrationMethod.Platt, EvidenceKind.Score, CalibrationTransform.LogOdds, 2.0, 1.0)),
        };
        return CliFixture.CreateAsync(
            Single(point),
            [
                .. Overlapping().Select(row => Row(row.Label, null, input == "probability"
                    ? OneSided(row.Value)
                    : Both(EvidenceKind.Score, row.Value, 1 - row.Value))),
            ]);
    }

    // Fifteen rows of each label on a 0.05 grid: flagged from 0.3 to 1, the others from 0 to 0.7, so the two overlap
    // and the extremes sit on the clamp of the log-odds.
    private static (string Label, double Value)[] Overlapping() =>
    [
        .. Enumerable.Range(6, 15).Select(k => ("true", k / 20.0)),
        .. Enumerable.Range(0, 15).Select(k => ("false", k / 20.0)),
    ];

    // Every flagged row above every other one, so an unsmoothed fit has no finite slope.
    private static (string Label, double Value)[] Separable() =>
    [
        .. Enumerable.Range(10, 11).Select(k => ("true", k / 20.0)),
        .. Enumerable.Range(0, 10).Select(k => ("false", k / 20.0)),
    ];

    private static Policy Single(RuleOperatingPoint point) =>
        new("guard", PolicyMode.Enforce, [Samples.Flagged()], [new ProviderBinding(_local, [point])], FailureBehavior.Escalate);

    private static RuleOperatingPoint Point(EvidenceKind kind, double warn, double deny, double? gate = 0.05) =>
        new(
            Samples.Injection,
            [new Threshold(Verdict.Warn, kind, warn), new Threshold(Verdict.Deny, kind, deny)],
            gate is { } below ? new MarginGate(kind, below) : null);

    private static RuleOperatingPoint Calibrated(EvidenceCalibration calibration) =>
        new(
            Samples.Injection,
            [new Threshold(Verdict.Warn, EvidenceKind.Probability, 0.6), new Threshold(Verdict.Deny, EvidenceKind.Probability, 0.9)],
            new MarginGate(calibration.SourceKind, 0.05),
            calibration);

    private static FixtureRow Row(string label, string? split, ProviderResult result) => FixtureRow.Of(label, split, (_local, result));

    private static ProviderResult Both(EvidenceKind kind, double flagged, double other, string? model = "model-local") =>
        Reading(new Evidence(kind, new Dictionary<string, double>(StringComparer.Ordinal) { ["true"] = flagged, ["false"] = other }), model);

    private static ProviderResult OneSided(double probability) =>
        Reading(new Evidence(EvidenceKind.Probability, new Dictionary<string, double>(StringComparer.Ordinal) { ["true"] = probability }));

    private static ProviderResult Reading(Evidence evidence, string? model = "model-local") =>
        new(
            DecisionType.Boolean,
            ProviderOutcome.Success,
            new BooleanValue(evidence.Values["true"] >= 0.5),
            [evidence],
            new ProviderMetadata(_local, model, 5));

    private static string OutPolicy(CliFixture fixture) =>
        Path.Combine(Path.GetDirectoryName(fixture.PolicyPath)!, "calibrated.policy.json");

    // Every row's verdict under the whole policy, gate included, replayed through Core as `report` replays it.
    private static (string Id, Verdict Verdict)[] Verdicts(string policyPath, CliFixture fixture)
    {
        LoadedInputs inputs = Inputs.Load(new InputSelection(policyPath, null, fixture.DatasetPath, null, null, new SplitNames(), []));
        ReplaySet set = ReplaySet.Load(RecordingReader.Read(fixture.RecordingPath), inputs, force: false);
        return [.. set.Evaluate().Select(row => (row.Row.Id, row.Verdict.Verdict))];
    }

    // The rows as a tune file and a test file beside the fixture's own, with a recording naming both, as `run --tune
    // --test` writes it. Returns the options that select them.
    private static async Task<string[]> TwoFilesAsync(CliFixture fixture, Policy policy, IReadOnlyList<FixtureRow> rows)
    {
        string directory = Path.GetDirectoryName(fixture.PolicyPath)!;
        string tune = Path.Combine(directory, "tune.jsonl");
        string test = Path.Combine(directory, "test.jsonl");
        string recording = Path.Combine(directory, "files.recording.jsonl");
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(tune, Lines(rows, "tune"), cancellation);
        await File.WriteAllTextAsync(test, Lines(rows, "test"), cancellation);
        await Samples.RecordAsync(
            recording,
            Samples.Header(policy) with
            {
                Datasets =
                [
                    new RecordedDataset(tune, DatasetReader.Read(tune).Sha256, "tune"),
                    new RecordedDataset(test, DatasetReader.Read(test).Sha256, "test"),
                ],
            },
            [
                .. rows.Select((row, index) => Samples.Recorded(
                    CliFixture.Id(index),
                    [.. row.Attempts.Select(attempt => (Samples.Injection, attempt.Key, attempt.Value))])),
            ]);
        return ["--tune", tune, "--test", test, "--recording", recording];
    }

    // The rows of one split, without the split, keeping each row's id from its place in the whole list.
    private static string Lines(IReadOnlyList<FixtureRow> rows, string split)
    {
        StringBuilder lines = new();
        for (int index = 0; index < rows.Count; index++)
        {
            if (rows[index].Split == split)
            {
                JsonObject row = new()
                {
                    ["id"] = CliFixture.Id(index),
                    ["input"] = "context",
                    ["label"] = rows[index].Label == "true",
                };
                lines.Append(row.ToJsonString()).Append('\n');
            }
        }

        return lines.ToString();
    }

    private sealed record SourceCase(
        EvidenceKind Kind,
        double Warn,
        double Deny,
        (string Label, double Value)[] Grid,
        Func<double, ProviderResult> Reading,
        CalibrationTransform Transform);

    private sealed record RefusalCase(Policy Policy, FixtureRow[] Rows, string Cause);
}
