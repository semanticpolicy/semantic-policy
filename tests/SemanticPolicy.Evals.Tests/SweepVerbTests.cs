using System.Globalization;
using System.Text.Json;
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
        run.Output.Should().Contain("conflict:");
        JsonElement sweep = fixture.ReadOut().GetProperty("sweep");
        sweep.GetProperty("conflict").GetBoolean().Should().BeTrue();
        Rung(sweep, "warn").GetProperty("recommendation").GetProperty("threshold").GetDouble().Should().Be(0.75);
        Rung(sweep, "deny").GetProperty("recommendation").GetProperty("threshold").GetDouble().Should().Be(0.35);
    }

    [Fact]
    public async Task Recommendation_Is_Chosen_On_Tune_And_Reported_On_Test_With_Both_Named()
    {
        // At 0.55 the tune rows score TP 4, FP 1, TN 4, FN 1; the test rows, drawn differently on purpose, score
        // TP 2 (0.6, 0.9), FP 1 (0.7), TN 1 (0.2) and FN 2 (0.5, 0.4) at the same cut.
        FixtureRow[] test =
        [
            Row("true", 0.6, "test"),
            Row("true", 0.5, "test"),
            Row("false", 0.7, "test"),
            Row("false", 0.2, "test"),
            Row("true", 0.9, "test"),
            Row("true", 0.4, "test"),
        ];
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), [.. Graded("tune"), .. test]);

        CliRun run = await fixture.RunAsync("sweep", "--warn", "min-recall=0.8");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain(
            "warn: min-recall=0.8 → threshold 0.55, chosen on split 'tune' (10 rows), reported on split 'test' (6 rows)");
        JsonElement warn = Rung(fixture.ReadOut().GetProperty("sweep"), "warn");
        Counts(warn.GetProperty("recommendation").GetProperty("chosen")).Should().Equal(4, 1, 4, 1);
        Counts(warn.GetProperty("test")).Should().Equal(2, 1, 1, 2);
        warn.GetProperty("chosenOn").GetString().Should().Be("split 'tune' (10 rows)");
        warn.GetProperty("reportedOn").GetString().Should().Be("split 'test' (6 rows)");
    }

    [Fact]
    public async Task Without_A_Split_The_Recommendation_Says_Chosen_And_Reported_On_The_Same_Data()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Guard(), Graded());

        CliRun run = await fixture.RunAsync("sweep", "--warn", "min-recall=0.8");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain(
            "warn: min-recall=0.8 → threshold 0.55, chosen and reported on the same data (no split)");
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

    internal static Policy Guard(EvidenceKind kind = EvidenceKind.Probability, params string[] providers)
    {
        BooleanRule rule = Samples.Flagged();
        Threshold[] ladder = [new(Verdict.Warn, kind, 0.6), new(Verdict.Deny, kind, 0.9)];
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
