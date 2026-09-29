using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class SweepVerbTests
{
    // Ten rows, five of them flagged. Cut at each observed value the warn and deny curves read:
    //
    //   threshold  0.05 0.15 0.25 0.35 0.45 0.55 0.65 0.75 0.85 0.95
    //   recall     1    1    1    1    0.8  0.8  0.6  0.6  0.4  0.2
    //   fpr        1    0.8  0.6  0.4  0.4  0.2  0.2  0    0    0
    //   precision  0.5  .556 .625 .714 .667 0.8  0.75 1    1    1
    private static readonly double[] _flagged = [0.05, 0.15, 0.25, 0.35, 0.45, 0.55, 0.65, 0.75, 0.85, 0.95];

    private static readonly string[] _labels =
        ["false", "false", "false", "true", "false", "true", "false", "true", "true", "true"];

    [Fact]
    public async Task An_Infeasible_Constraint_Reports_The_Nearest_Point_And_Exits_2_Still_Writing_Out()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        // Recall 1 holds up to 0.35 and precision 1 from 0.75 on. Summed, 0.35 misses by 0.286 and every other
        // point by more.
        CliRun run = await fixture.RunAsync("sweep", "--deny", "min-recall=1", "--deny", "min-precision=1");

        run.ExitCode.Should().Be(ExitCodes.InfeasibleConstraint);
        run.Output.Should().Contain("deny: min-recall=1, min-precision=1 → infeasible, nearest threshold 0.35 ");
        run.Output.Should().Contain("warn: not swept, keeps 0.6 from the policy file");
        run.Output.Should().Contain("gate curve");
        JsonElement deny = Rung(fixture.ReadOut().GetProperty("sweep"), "deny").GetProperty("recommendation");
        deny.GetProperty("feasible").GetBoolean().Should().BeFalse();
        deny.TryGetProperty("threshold", out _).Should().BeFalse();
        deny.GetProperty("nearest").GetProperty("threshold").GetDouble().Should().Be(0.35);
    }

    [Fact]
    public async Task A_Rung_Without_A_Constraint_Keeps_The_File_Threshold_And_Is_Reported_As_Not_Swept()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        CliRun run = await fixture.RunAsync("sweep", "--warn", "min-recall=0.8");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain("deny: not swept, keeps 0.9 from the policy file");
        run.Output.Should().NotMatchRegex("(?m)^deny: .*→");
        JsonElement deny = Rung(fixture.ReadOut().GetProperty("sweep"), "deny").GetProperty("recommendation");
        deny.GetProperty("swept").GetBoolean().Should().BeFalse();
        deny.GetProperty("threshold").GetDouble().Should().Be(0.9);
    }

    [Fact]
    public async Task Deny_Recommended_Below_Warn_Is_Reported_As_A_Conflict_And_Not_Repaired()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        // No false positive at all first happens at 0.75; every positive is still found up to 0.35.
        CliRun run = await fixture.RunAsync("sweep", "--warn", "max-fpr=0", "--deny", "min-recall=1");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain(
            "conflict: warn 0.75 is not below deny 0.35, so any row that crosses warn crosses deny too and warn is never "
            + "reached; the library refuses thresholds that do not increase with severity, so the pair cannot go into "
            + "the policy file as printed");

        // At 0.35 warn's max-fpr=0 fails, so there is nothing to say about deny covering warn's goal.
        run.Output.Should().Contain(
            "\n  set warn below 0.35 or deny above 0.75 by hand, reading on the curves what each would flag, or change a goal");
        run.Output.Should().NotContain("would meet");
        JsonElement sweep = fixture.ReadOut().GetProperty("sweep");
        sweep.GetProperty("conflict").GetBoolean().Should().BeTrue();
        Rung(sweep, "warn").GetProperty("recommendation").GetProperty("threshold").GetDouble().Should().Be(0.75);
        Rung(sweep, "deny").GetProperty("recommendation").GetProperty("threshold").GetDouble().Should().Be(0.35);
    }

    [Fact]
    public async Task A_Conflict_Says_When_The_Higher_Threshold_Already_Meets_The_Lower_Rungs_Goal()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        // Recall 0.6 holds up to 0.75 and precision 0.75 first holds at 0.55, where recall is 0.8: deny's threshold
        // alone flags what warn was asked to.
        CliRun run = await fixture.RunAsync("sweep", "--warn", "min-recall=0.6", "--deny", "min-precision=0.75");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain("conflict: warn 0.75 is not below deny 0.55, ");
        run.Output.Should().Contain(
            "\n  at 0.55 warn would meet min-recall=0.6 too, so deny alone already does what warn was asked to; set warn "
            + "below 0.55 or deny above 0.75 by hand, reading on the curves what each would flag, or change a goal");
    }

    [Fact]
    public async Task Recommendation_Is_Chosen_On_Tune_And_Reported_On_Test_With_Both_Named()
    {
        // At 0.55 the tune rows score TP 4, FP 1, TN 4, FN 1; the test rows, drawn differently on purpose, score
        // TP 2 (0.6, 0.9), FP 1 (0.7), TN 1 (0.2) and FN 2 (0.5, 0.4) at the same cut.
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), [.. Graded("tune"), .. TestSplit()]);

        CliRun run = await fixture.RunAsync("sweep", "--warn", "min-recall=0.8");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain(
            "warn: min-recall=0.8 → threshold 0.55 (recall 0.800 [0.376, 0.964]), chosen on split 'tune' (10 rows), "
            + "reported on split 'test' (6 rows)");
        JsonElement warn = Rung(fixture.ReadOut().GetProperty("sweep"), "warn");
        Counts(warn.GetProperty("recommendation").GetProperty("chosen")).Should().Equal(4, 1, 4, 1);
        Counts(warn.GetProperty("test")).Should().Equal(2, 1, 1, 2);
        warn.GetProperty("chosenOn").GetString().Should().Be("split 'tune' (10 rows)");
        warn.GetProperty("reportedOn").GetString().Should().Be("split 'test' (6 rows)");
    }

    [Fact]
    public async Task Sweep_Recommendation_And_Test_Lines_Carry_Intervals()
    {
        using CliFixture rungs = await CliFixture.CreateAsync(Guard(), [.. Graded("tune"), .. TestSplit()]);
        using CliFixture gate = await CliFixture.CreateAsync(Guard(), [.. Graded("tune"), .. TestSplit()]);

        // On the tune rows warn meets its goal at 0.55 with 4 of the 5 attacks, and deny misses its goals by the least
        // at 0.35: all 5 attacks, and 5 of the 7 rows it flags.
        CliRun swept = await rungs.RunAsync(
            "sweep", "--warn", "min-recall=0.8", "--deny", "min-recall=1", "--deny", "min-precision=1");
        CliRun gated = await gate.RunAsync("sweep", "--gate", "max-abstain=0.5");

        swept.ExitCode.Should().Be(ExitCodes.InfeasibleConstraint, swept.Error);
        gated.ExitCode.Should().Be(ExitCodes.Success, gated.Error);
        string output = swept.Output.ReplaceLineEndings("\n");
        output.Should().Contain(
            "\nwarn: min-recall=0.8 → threshold 0.55 (recall 0.800 [0.376, 0.964]), chosen on split 'tune' (10 rows), ");
        output.Should().Contain(
            "\ndeny: min-recall=1, min-precision=1 → infeasible, nearest threshold 0.35 (recall 1.000 [0.566, 1.000], "
            + "precision 0.714 [0.359, 0.918]), chosen on split 'tune' (10 rows), ");

        // Warn at 0.55 on the test rows: tp 2, fp 1, tn 1, fn 2.
        string[] lines = output.Split('\n');
        int rates = Array.FindIndex(lines, line => line.StartsWith("rates at these thresholds", StringComparison.Ordinal));
        int title = Array.FindIndex(lines, rates, line => line == "95% Wilson intervals");
        title.Should().BeGreaterThan(rates);
        string[] intervals = [.. lines.Skip(title).TakeWhile(line => line.Length > 0)];
        intervals[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Should()
            .Equal("rung", "accuracy", "precision", "recall", "fpr", "fnr");
        intervals[3].Should().Be("warn  [0.188, 0.812]  [0.208, 0.939]  [0.150, 0.850]  [0.095, 0.905]  [0.150, 0.850]");
        intervals.Should().HaveCount(4).And.OnlyContain(line => line.Length <= 120);
        lines.SkipWhile(line => !line.StartsWith("warn curve", StringComparison.Ordinal)).TakeWhile(line => line.Length > 0)
            .Should().NotContain(line => line.Contains('['));

        const string Rate = @"\d\.\d{3} \[\d\.\d{3}, \d\.\d{3}\]";
        gated.Output.Should().MatchRegex(
            $@"(?m)^gate: max-abstain=0\.5 → gate [0-9.]+ \(abstention rate {Rate}, accuracy {Rate}\), chosen on split 'tune'");
        gated.Output.Should().MatchRegex(
            $@"(?m)^gate on split 'test' \(6 rows\): gate [0-9.]+, \d+ abstained \(abstention rate {Rate}\), \d+ decided, "
            + $@"accuracy {Rate}\r?$");
        gated.Output.ReplaceLineEndings("\n").Split('\n')
            .SkipWhile(line => !line.StartsWith("gate curve", StringComparison.Ordinal)).TakeWhile(line => line.Length > 0)
            .Should().NotContain(line => line.Contains('['));
    }

    [Fact]
    public async Task A_Recommended_Threshold_Is_Printed_Exactly_So_Copying_It_Selects_The_Chosen_Rows()
    {
        // Positives at 0.6, 0.7, 0.8807970779778823, 0.9 and 0.95: min-recall=0.6 needs three of them, so the
        // highest cut that keeps it is the full-precision value. Rounded to 0.8808 it would flag only two, recall 0.4.
        double[] flagged = [0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8807970779778823, 0.9, 0.95];
        string[] labels = ["false", "false", "false", "false", "false", "true", "true", "true", "true", "true"];
        using CliFixture fixture = await CliFixture.CreateAsync(
            Guard(),
            [.. flagged.Select((value, index) => Row(labels[index], value))]);

        CliRun run = await fixture.RunAsync("sweep", "--warn", "min-recall=0.6");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain("warn: min-recall=0.6 → threshold 0.8807970779778823 (recall 0.600 ");
        Rung(fixture.ReadOut().GetProperty("sweep"), "warn").GetProperty("recommendation").GetProperty("threshold")
            .GetDouble().Should().Be(0.8807970779778823);
    }

    [Fact]
    public async Task Without_A_Split_The_Recommendation_Says_Chosen_And_Reported_On_The_Same_Data()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        CliRun run = await fixture.RunAsync("sweep", "--warn", "min-recall=0.8");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain(
            "warn: min-recall=0.8 → threshold 0.55 (recall 0.800 [0.376, 0.964]), chosen and reported on the same data (no split)");
    }

    [Fact]
    public async Task Sweep_Prints_The_Curves_And_No_Recommendation_Without_Any_Constraint()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        CliRun run = await fixture.RunAsync("sweep");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain("warn curve").And.Contain("deny curve").And.Contain("gate curve");
        run.Output.Should().NotContain("→").And.NotContain("not swept").And.NotContain("recommended");
    }

    [Fact]
    public async Task Probability_Curve_Table_Lists_The_0_05_Grid_Beside_Observed_Values_And_Score_Does_Not()
    {
        using CliFixture probability = await CliFixture.CreateAsync(Guard(), Graded());
        using CliFixture score = await CliFixture.CreateAsync(
            Guard(EvidenceKind.Score),
            Graded(kind: EvidenceKind.Score));

        CliRun onProbability = await probability.RunAsync("sweep");
        CliRun onScore = await score.RunAsync("sweep");

        // The threshold column is right-aligned, so a shorter value is padded on the left.
        onProbability.Output.Should().MatchRegex(@"(?m)^ *0\.35 +observed ");
        onProbability.Output.Should().MatchRegex(@"(?m)^ *0\.4 +grid ");
        onScore.Output.Should().MatchRegex(@"(?m)^ *0\.35 +observed ");
        onScore.Output.Should().NotMatchRegex(@"(?m)^ *\S+ +grid ");
    }

    [Theory]
    [InlineData(new[] { "max-abstain=0.3" }, 0.5)]
    [InlineData(new[] { "min-accuracy=0.9" }, 0.75)]
    [InlineData(new[] { "max-abstain=0.6", "min-accuracy=0.8" }, 0.75)]
    [InlineData(new[] { "min-accuracy=0.8", "max-abstain=0.6" }, 0.5)]
    public async Task Gate_Sweep_Picks_Under_Max_Abstain_And_Min_Accuracy_And_Prints_The_Curve(
        string[] tokens,
        double expected)
    {
        // The gate curve of these rows, with warn at the file's 0.6:
        //
        //   gate        none  0.25  0.5   0.75  0.875  1
        //   abstention  0     0.1   0.3   0.5   0.7    0.9
        //   accuracy    0.6   .667  .857  1     1      1
        double[] flagged = [0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 0.9375, 0.0625, 1.0];
        string[] labels = ["false", "true", "true", "true", "false", "true", "true", "true", "false", "true"];
        using CliFixture fixture = await CliFixture.CreateAsync(
            Guard(),
            [.. flagged.Select((value, index) => Row(labels[index], value))]);

        CliRun run = await fixture.RunAsync("sweep", [.. tokens.SelectMany(token => new[] { "--gate", token })]);

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain("gate curve").And.Contain("abstention rate");
        run.Output.Should().Contain($"gate: {string.Join(", ", tokens)} → gate {expected.ToString(CultureInfo.InvariantCulture)} ");
        JsonElement gate = fixture.ReadOut().GetProperty("sweep").GetProperty("gate");
        gate.GetProperty("curve").GetProperty("points").GetArrayLength().Should().Be(6);
        gate.GetProperty("recommendation").GetProperty("below").GetDouble().Should().Be(expected);
    }

    [Fact]
    public async Task A_Gate_On_A_Binding_With_A_Later_One_Counts_The_Rows_It_Passes_On()
    {
        // Margins 0.875, 0.75, 0.5, 0.25, 0.25, 0.5, 0.75, 0.875. With 'hosted' after it and no gate there nothing
        // abstains, so max-abstain takes the highest gate, which passes six of the eight rows on to 'hosted'.
        using CliFixture fixture = await CliFixture.CreateAsync(
            Guard(EvidenceKind.Probability, "local", "hosted"),
            Chained());

        CliRun run = await fixture.RunAsync("sweep", "--provider", "local", "--gate", "max-abstain=0.05");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().MatchRegex(@"(?m)^gate +passed on +abstained +abstention rate +decided +accuracy *\r?$");
        run.Output.Should().Contain(
            "gate: max-abstain=0.05 → gate 0.875 (6 passed on to the next binding, abstention rate 0.000 [0.000, 0.324], ");
        run.Output.Should().Contain(": gate 0.875, 6 passed on to the next binding, 0 abstained ");
        JsonElement gate = fixture.ReadOut().GetProperty("sweep").GetProperty("gate");
        gate.GetProperty("curve").GetProperty("points").EnumerateArray()
            .Select(point => point.GetProperty("passedOn").GetInt32())
            .Should().Equal(0, 0, 2, 4, 6);
        gate.GetProperty("recommendation").GetProperty("chosen").GetProperty("passedOn").GetInt32().Should().Be(6);
    }

    [Fact]
    public async Task A_Gate_On_The_Last_Binding_Has_Nothing_To_Pass_On_And_Says_Nothing_About_It()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(
            Guard(EvidenceKind.Probability, "local", "hosted"),
            Chained());

        CliRun run = await fixture.RunAsync("sweep", "--provider", "hosted", "--gate", "max-abstain=0.05");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain("gate curve").And.NotContain("passed on");
        fixture.ReadOut().GetProperty("sweep").GetProperty("gate").GetProperty("curve").GetProperty("points")
            .EnumerateArray().Should().AllSatisfy(point => point.TryGetProperty("passedOn", out _).Should().BeFalse());
    }

    [Fact]
    public async Task Gate_Constraint_On_A_Choice_Binding_Without_A_File_Gate_Is_An_Error_Naming_The_Provider()
    {
        ChoiceRule rule = Samples.Route();
        Policy policy = new(
            "router-policy",
            PolicyMode.Enforce,
            [rule],
            [new ProviderBinding("router", [])],
            FailureBehavior.Deny);
        string[] options = ["allow", "review", "deny", "allow", "review", "deny"];
        using CliFixture fixture = await CliFixture.CreateAsync(
            policy,
            [
                .. options.Select(option => FixtureRow.Of(
                    option,
                    null,
                    ("router", Samples.Answer(
                        new ChoiceValue(option),
                        "router",
                        Samples.Probability(("allow", 0.25), ("review", 0.25), ("deny", 0.5)))))),
            ]);

        CliRun run = await fixture.RunAsync("sweep", "--gate", "max-abstain=0.1");

        run.ExitCode.Should().Be(ExitCodes.UsageOrData);
        run.Error.Should().Contain("'route'").And.Contain("'router'").And.Contain("gate");
        run.Error.Should().NotMatchRegex("[0-9]");
        run.Output.Should().NotContain("gate curve");
    }

    [Fact]
    public async Task Sweep_Requires_Provider_When_The_Policy_Has_Several_Bindings()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(
            Guard(EvidenceKind.Probability, "local", "hosted"),
            [
                .. _flagged.Select((value, index) => FixtureRow.Of(
                    _labels[index],
                    null,
                    ("local", Answer(EvidenceKind.Probability, value)),
                    ("hosted", Answer(EvidenceKind.Probability, 1 - value, "hosted")))),
            ]);

        CliRun unnamed = await fixture.RunAsync("sweep");
        CliRun named = await fixture.RunAsync("sweep", "--provider", "hosted");

        unnamed.ExitCode.Should().Be(ExitCodes.UsageOrData);
        unnamed.Error.Should().Contain("'local'").And.Contain("'hosted'").And.Contain("--provider");
        unnamed.Output.Should().BeEmpty();
        named.ExitCode.Should().Be(ExitCodes.Success);
        fixture.ReadOut().GetProperty("sweep").GetProperty("provider").GetString().Should().Be("hosted");
    }

    [Fact]
    public async Task Sweep_Of_Distinct_Scores_Prints_A_Table_A_Person_Can_Read()
    {
        // Unrounded scores, one per row, and their margins nearly as varied: a few hundred distinct candidates
        // for the rung curves and the gate curve alike.
        using CliFixture fixture = await CliFixture.CreateAsync(
            Guard(EvidenceKind.Score),
            [
                .. Enumerable.Range(1, 300).Select(index => index / 301.0).Select(value =>
                    Row(value >= 0.5 ? "true" : "false", value, kind: EvidenceKind.Score)),
            ]);

        CliRun run = await fixture.RunAsync("sweep", "--provider", "local");

        run.ExitCode.Should().Be(ExitCodes.Success);
        string[] lines = run.Output.ReplaceLineEndings("\n").Split('\n');
        TableRows(lines, "warn curve").Should().BeInRange(1, 101);
        TableRows(lines, "deny curve").Should().BeInRange(1, 101);
        TableRows(lines, "gate curve").Should().BeInRange(1, 102);
    }

    [Fact]
    public async Task Sweep_Feeds_Its_Picks_Back_Until_They_Stop_Changing_And_Says_How_Many_Passes_That_Took()
    {
        // The shipped smoke set, with local's numbers set to placeholders. Every curve replays the whole policy, so
        // the first pass, at gate 0.3, picks deny 0.9349; at the gate that pass picks, the second picks deny 0.843;
        // the third picks what it was swept at, which is the smoke policy as shipped.
        string smoke = Path.Combine(ExampleDatasetsTests.RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "smoke");
        JsonNode policy = JsonNode.Parse(File.ReadAllText(Path.Combine(smoke, "prompt-injection.policy.json")))!;
        JsonNode local = policy["bindings"]![0]!["operatingPoints"]![0]!;
        local["thresholds"]![0]!["atOrAbove"] = 0.5;
        local["thresholds"]![1]!["atOrAbove"] = 0.9;
        local["gate"]!["below"] = 0.3;
        using TempFile policyFile = TempFile.Write(policy.ToJsonString(), ".json");
        using TempFile outFile = TempFile.Write(string.Empty, ".json");

        CliRun run = await CliFixture.InvokeAsync(
        [
            "sweep",
            "--policy", policyFile.Path,
            "--dataset", Path.Combine(smoke, "prompt-injection.smoke.jsonl"),
            "--recording", Path.Combine(smoke, "prompt-injection.recording.jsonl"),
            "--out", outFile.Path,
            "--provider", "local",
            "--warn", "min-recall=0.9",
            "--deny", "min-precision=0.95",
            "--gate", "min-accuracy=0.9",
        ]);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        run.Output.Should().Contain("warn: min-recall=0.9 → threshold 0.194 (")
            .And.Contain("deny: min-precision=0.95 → threshold 0.843 (")
            .And.Contain("gate: min-accuracy=0.9 → gate 0.5418 ")
            .And.Contain("\nsettled in 3 passes: ");
        using JsonDocument written = JsonDocument.Parse(File.ReadAllText(outFile.Path));
        JsonElement sweep = written.RootElement.GetProperty("sweep");
        JsonElement passes = sweep.GetProperty("passes");
        passes.GetProperty("count").GetInt32().Should().Be(3);
        passes.GetProperty("end").GetString().Should().Be("settled");
        passes.TryGetProperty("sweptAt", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_Sweep_At_Numbers_It_Already_Recommends_Settles_In_One_Pass()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        // Warn's recall 0.8 holds up to 0.55; deny stays at the file's 0.9 and no gate is asked for.
        CliRun first = await fixture.RunAsync("sweep", "--warn", "min-recall=0.8");

        first.ExitCode.Should().Be(ExitCodes.Success);
        first.Output.Should().Contain("\nsettled in 2 passes: ");
        using CliFixture settled = await CliFixture.CreateAsync(Guard(warn: 0.55), Graded());
        CliRun again = await settled.RunAsync("sweep", "--warn", "min-recall=0.8");
        again.Output.Should().Contain("\nsettled in 1 pass: the policy file already holds these picks");
        settled.ReadOut().GetProperty("sweep").GetProperty("passes").GetProperty("count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_Choice_Binding_Whose_Sweep_Picks_No_Gate_Keeps_The_Kind_Its_File_Gate_Declares_For_The_Next_Pass()
    {
        // Every answer is right, so min-accuracy=0.9 takes no gate at all. The next pass is swept at no gate, and a
        // Choice operating point declares the kind a margin is read on only through its gate.
        ChoiceRule rule = Samples.Route();
        Policy policy = new(
            "router-policy",
            PolicyMode.Enforce,
            [rule],
            [new ProviderBinding("router", [new RuleOperatingPoint(rule.Id, [], new MarginGate(EvidenceKind.Probability, 0.5))])],
            FailureBehavior.Deny);
        string[] options = ["allow", "review", "deny", "allow", "review", "deny"];
        using CliFixture fixture = await CliFixture.CreateAsync(
            policy,
            [
                .. options.Select(option => FixtureRow.Of(
                    option,
                    null,
                    ("router", Samples.Answer(
                        new ChoiceValue(option),
                        "router",
                        Samples.Probability([.. options.Distinct().Select(key => (key, key == option ? 0.5 : 0.25))]))))),
            ]);

        CliRun run = await fixture.RunAsync("sweep", "--gate", "min-accuracy=0.9");

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        run.Output.Should().Contain("gate: min-accuracy=0.9 → no gate ").And.Contain("\nsettled in 2 passes: ");
    }

    [Fact]
    public async Task A_Sweep_Whose_Picks_Alternate_Prints_Both_Sets_And_Exits_2_Still_Writing_Out()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Unsettled(), Alternating());

        CliRun run = await fixture.RunAsync("sweep", "--warn", "min-recall=0.9", "--gate", "min-accuracy=0.9");

        run.ExitCode.Should().Be(ExitCodes.InfeasibleConstraint);
        run.Output.ReplaceLineEndings("\n").Should().Contain(
            "\nno fixed point after 3 passes: successive passes alternate between these two sets of picks\n"
            + "  warn 0.9375, deny 0.96875, no gate\n"
            + "  warn 0.25, deny 0.96875, gate 0.875\n"
            + "  neither is recommended: put each in the policy file and run report to see what it does");
        JsonElement sweep = fixture.ReadOut().GetProperty("sweep");
        JsonElement passes = sweep.GetProperty("passes");
        passes.GetProperty("count").GetInt32().Should().Be(3);
        passes.GetProperty("end").GetString().Should().Be("alternating");
        passes.GetProperty("sweptAt").GetProperty("thresholds")[0].GetProperty("atOrAbove").GetDouble().Should().Be(0.9375);
        passes.GetProperty("sweptAt").TryGetProperty("gate", out _).Should().BeFalse();
        passes.GetProperty("picked").GetProperty("thresholds")[0].GetProperty("atOrAbove").GetDouble().Should().Be(0.25);
        passes.GetProperty("picked").GetProperty("gate").GetProperty("below").GetDouble().Should().Be(0.875);
    }

    [Fact]
    public async Task A_Conflict_With_A_Gate_Goal_Stops_The_Passes_And_Sweeps_The_Gate_At_The_Thresholds_It_Started_From()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        // The conflict of Deny_Recommended_Below_Warn_Is_Reported_As_A_Conflict_And_Not_Repaired: a policy holding
        // warn 0.75 and deny 0.35 does not validate, so the gate cannot be replayed at them.
        CliRun run = await fixture.RunAsync(
            "sweep", "--warn", "max-fpr=0", "--deny", "min-recall=1", "--gate", "max-abstain=0.5");

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        run.Output.Should().Contain("conflict: warn 0.75 is not below deny 0.35, ");
        run.Output.Should().Contain(
            "\nstopped after 1 pass: the thresholds picked do not increase with severity, so no policy can hold them for "
            + "another pass, and the gate is measured at the ones the pass started from");
        JsonElement passes = fixture.ReadOut().GetProperty("sweep").GetProperty("passes");
        passes.GetProperty("count").GetInt32().Should().Be(1);
        passes.GetProperty("end").GetString().Should().Be("conflict");
    }

    [Fact]
    public async Task A_Pick_Above_The_Number_An_Infeasible_Rung_Keeps_Is_A_Conflict_No_Pair_Line_Can_Name()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        // Recall 0.2 holds up to 0.95, above the 0.9 deny keeps when recall 1 and fpr 0 never hold together. Deny
        // reports no threshold, so there is no pair for a conflict line to print.
        CliRun run = await fixture.RunAsync(
            "sweep", "--warn", "min-recall=0.2", "--deny", "min-recall=1", "--deny", "max-fpr=0");

        run.ExitCode.Should().Be(ExitCodes.InfeasibleConstraint);
        run.Output.Should().Contain("warn: min-recall=0.2 → threshold 0.95 (");
        run.Output.Should().Contain(
            "\nstopped after 1 pass: the thresholds picked and the number an infeasible rung keeps do not increase with "
            + "severity, so no policy can hold them for another pass, and the gate is measured at the ones the pass "
            + "started from");
        run.Output.Should().NotContain("conflict: ");
        JsonElement sweep = fixture.ReadOut().GetProperty("sweep");
        sweep.GetProperty("conflict").GetBoolean().Should().BeTrue();
        sweep.GetProperty("passes").GetProperty("end").GetString().Should().Be("conflict");
    }

    [Fact]
    public async Task A_Sweep_That_Settles_With_A_Goal_Unmet_Says_So_Rather_Than_That_It_Picked_The_Kept_Number()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        // Recall 1 holds up to 0.35 and fpr 0 from 0.75 on, so deny keeps the 0.9 it is swept at in every pass.
        CliRun once = await fixture.RunAsync("sweep", "--deny", "min-recall=1", "--deny", "max-fpr=0");
        CliRun twice = await fixture.RunAsync(
            "sweep", "--warn", "min-recall=0.8", "--deny", "min-recall=1", "--deny", "max-fpr=0");

        once.ExitCode.Should().Be(ExitCodes.InfeasibleConstraint);
        once.Output.Should().Contain(
            "\nsettled in 1 pass with a goal unmet: what is infeasible above keeps the policy file's number, and the file "
            + "already holds every pick");
        once.Output.Should().NotContain("these picks");
        twice.ExitCode.Should().Be(ExitCodes.InfeasibleConstraint);
        twice.Output.Should().Contain(
            "\nsettled in 2 passes with a goal unmet: each pass was swept at the picks of the one before, the last one "
            + "picked what it was swept at, and what is infeasible above keeps the number it was swept at");
    }

    // The rows of the table printed under the line that starts with the title: past the header and the rule of
    // dashes under it, up to the blank line that ends the table.
    private static int TableRows(string[] lines, string title)
    {
        int start = Array.FindIndex(lines, line => line.StartsWith(title, StringComparison.Ordinal));
        start.Should().BeGreaterThanOrEqualTo(0, $"the output has a '{title}' table");
        int rule = Array.FindIndex(lines, start + 1, line => line.Length > 0 && line.Trim(' ', '-').Length == 0);
        int end = Array.FindIndex(lines, rule + 1, line => line.Length == 0);
        return (end < 0 ? lines.Length : end) - rule - 1;
    }

    internal static Policy Guard(EvidenceKind kind = EvidenceKind.Probability, params string[] providers) =>
        Guard(0.6, kind, providers);

    internal static Policy Guard(double warn, EvidenceKind kind = EvidenceKind.Probability, params string[] providers)
    {
        BooleanRule rule = Samples.Flagged();
        Threshold[] ladder = [new(Verdict.Warn, kind, warn), new(Verdict.Deny, kind, 0.9)];
        return new Policy(
            "guard",
            PolicyMode.Enforce,
            [rule],
            [.. (providers.Length == 0 ? ["local"] : providers).Select(provider =>
                new ProviderBinding(provider, [new RuleOperatingPoint(rule.Id, ladder)]))],
            FailureBehavior.Deny);
    }

    internal static FixtureRow[] Graded(string? split = null, EvidenceKind kind = EvidenceKind.Probability) =>
        [.. _flagged.Select((value, index) => Row(_labels[index], value, split, kind))];

    // Six test rows to follow Graded("tune"), drawn unlike it on purpose.
    private static FixtureRow[] TestSplit() =>
    [
        Row("true", 0.6, "test"),
        Row("true", 0.5, "test"),
        Row("false", 0.7, "test"),
        Row("false", 0.2, "test"),
        Row("true", 0.9, "test"),
        Row("true", 0.4, "test"),
    ];

    // Warn at a placeholder, deny above every row, no gate: the policy Alternating's rows are swept on.
    internal static Policy Unsettled()
    {
        BooleanRule rule = Samples.Flagged();
        Threshold[] ladder =
            [new(Verdict.Warn, EvidenceKind.Probability, 0.5), new(Verdict.Deny, EvidenceKind.Probability, 0.96875)];
        return new Policy(
            "guard",
            PolicyMode.Enforce,
            [rule],
            [new ProviderBinding("local", [new RuleOperatingPoint(rule.Id, ladder)])],
            FailureBehavior.Deny);
    }

    // Twelve rows on which min-recall=0.9 on warn and min-accuracy=0.9 on the gate have no consistent pair, on
    // dyadic probabilities so every margin is exact: four attacks at 0.9375 and five safe rows at 0.0625, margin
    // 0.875; one attack at 0.25, margin 0.5; two safe rows at 0.875, margin 0.75.
    //
    //   With no gate, warn must drop to 0.25 to catch the attack at 0.25, which also flags the two safe rows at
    //   0.875: accuracy 0.833, and 0.818 at gate 0.75, so the gate goes to 0.875 and holds back both kinds.
    //   At gate 0.875 warn can rise to 0.9375, where the one missed attack leaves accuracy at 0.917 with no gate.
    internal static FixtureRow[] Alternating() =>
    [
        .. Enumerable.Repeat(Row("true", 0.9375), 4),
        Row("true", 0.25),
        .. Enumerable.Repeat(Row("false", 0.0625), 5),
        .. Enumerable.Repeat(Row("false", 0.875), 2),
    ];

    // Eight rows answered alike by 'local' and 'hosted', on dyadic probabilities so every margin |2p - 1| is exact.
    private static FixtureRow[] Chained()
    {
        double[] flagged = [0.0625, 0.125, 0.25, 0.375, 0.625, 0.75, 0.875, 0.9375];
        string[] labels = ["false", "false", "false", "true", "false", "true", "true", "true"];
        return
        [
            .. flagged.Select((value, index) => FixtureRow.Of(
                labels[index],
                null,
                ("local", Answer(EvidenceKind.Probability, value)),
                ("hosted", Answer(EvidenceKind.Probability, value, "hosted")))),
        ];
    }

    internal static FixtureRow Row(
        string label,
        double flagged,
        string? split = null,
        EvidenceKind kind = EvidenceKind.Probability) =>
        FixtureRow.Of(label, split, ("local", Answer(kind, flagged)));

    // Both answers in the entry: Core completes only a one-sided probability, and a one-sided score is malformed.
    internal static ProviderResult Answer(EvidenceKind kind, double flagged, string provider = "local") =>
        Samples.Answer(
            new BooleanValue(flagged >= 0.5),
            provider,
            new Evidence(kind, new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["true"] = flagged,
                ["false"] = 1 - flagged,
            }));

    internal static JsonElement Rung(JsonElement section, string rung) =>
        section.GetProperty("rungs").EnumerateArray().Single(entry => entry.GetProperty("rung").GetString() == rung);

    internal static int[] Counts(JsonElement point)
    {
        JsonElement matrix = point.GetProperty("matrix");
        return
        [
            matrix.GetProperty("truePositives").GetInt32(),
            matrix.GetProperty("falsePositives").GetInt32(),
            matrix.GetProperty("trueNegatives").GetInt32(),
            matrix.GetProperty("falseNegatives").GetInt32(),
        ];
    }
}
