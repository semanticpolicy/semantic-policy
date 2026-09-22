using System.Text.Json;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class CompareVerbTests
{
    private static readonly string[] _labels =
        ["false", "false", "false", "true", "false", "true", "false", "true", "true", "true"];

    // Local is the graded curve the sweep tests use: min-recall=0.8 lands on 0.55 with TP 4, FP 1, TN 4, FN 1.
    private static readonly double[] _local = [0.05, 0.15, 0.25, 0.35, 0.45, 0.55, 0.65, 0.75, 0.85, 0.95];

    // Hosted separates the labels on its own scale: every negative sits at or below 0.5 and the positives at 0.72
    // to 0.8, so min-recall=0.8 lands on 0.74 with TP 4, FP 0, TN 5, FN 1. Under the real chain local answers
    // every row first, so a number of hosted's that came from the cascade would be local's.
    private static readonly double[] _hosted = [0.1, 0.2, 0.3, 0.72, 0.4, 0.74, 0.5, 0.76, 0.78, 0.8];

    [Fact]
    public async Task Compare_Evaluates_Each_Binding_Alone_At_Its_Own_Recommended_Point_On_Test()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Pair(), Rows());

        CliRun run = await fixture.RunAsync("compare", "--warn", "min-recall=0.8");

        run.ExitCode.Should().Be(ExitCodes.Success);
        JsonElement[] bindings = Bindings(fixture.ReadOut());
        bindings.Select(binding => binding.GetProperty("sweep").GetProperty("provider").GetString())
            .Should().Equal("local", "hosted");
        JsonElement local = SweepVerbTests.Rung(bindings[0].GetProperty("sweep"), "warn");
        JsonElement hosted = SweepVerbTests.Rung(bindings[1].GetProperty("sweep"), "warn");
        local.GetProperty("recommendation").GetProperty("threshold").GetDouble().Should().Be(0.55);
        hosted.GetProperty("recommendation").GetProperty("threshold").GetDouble().Should().Be(0.74);
        SweepVerbTests.Counts(local.GetProperty("test")).Should().Equal(4, 1, 4, 1);
        SweepVerbTests.Counts(hosted.GetProperty("test")).Should().Equal(4, 0, 5, 1);
        run.Output.Should().MatchRegex(@"(?m)^local +0\.55 ").And.MatchRegex(@"(?m)^hosted +0\.74 ");
    }

    [Fact]
    public async Task Compare_Filters_Bindings_With_Provider_And_Rejects_An_Unknown_Name()
    {
        using CliFixture fixture = await CliFixture.CreateAsync(Pair(), Rows());

        CliRun filtered = await fixture.RunAsync("compare", "--provider", "hosted");
        JsonElement[] bindings = Bindings(fixture.ReadOut());
        CliRun unknown = await fixture.RunAsync("compare", "--provider", "remote");

        filtered.ExitCode.Should().Be(ExitCodes.Success);
        bindings.Should().ContainSingle()
            .Which.GetProperty("sweep").GetProperty("provider").GetString().Should().Be("hosted");
        filtered.Output.Should().NotContain("local");
        unknown.ExitCode.Should().Be(ExitCodes.UsageOrData);
        unknown.Error.Should().Contain("'remote'").And.Contain("'local'").And.Contain("'hosted'");
        unknown.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task Compare_Table_Carries_Aucs_Abstention_Failure_Latency_And_Usage_Columns_Per_Binding()
    {
        // Hosted bills every answer at 0.25 and takes 20 ms; its first row timed out, and a failed attempt carries
        // no usage. Local reports no usage at all, so its cost cell has nothing to sum.
        using JsonDocument usage = JsonDocument.Parse("""{"cost":0.25}""");
        FixtureRow[] rows =
        [
            .. Rows().Select((row, index) => row with
            {
                Attempts = new Dictionary<string, ProviderResult>(StringComparer.Ordinal)
                {
                    ["local"] = row.Attempts["local"],
                    ["hosted"] = index == 0
                        ? Samples.Failed(FailureKind.Timeout, "hosted")
                        : row.Attempts["hosted"] with
                        {
                            Provider = row.Attempts["hosted"].Provider with { LatencyMs = 20, Usage = usage.RootElement.Clone() },
                        },
                },
            }),
        ];
        using CliFixture fixture = await CliFixture.CreateAsync(Pair(), rows);

        CliRun run = await fixture.RunAsync("compare");

        run.ExitCode.Should().Be(ExitCodes.Success);
        run.Output.Should().Contain("roc-auc").And.Contain("pr-auc").And.Contain("abstention").And.Contain("failure")
            .And.Contain("p50 ms").And.Contain("p95 ms").And.Contain("cost");
        run.Output.Should().MatchRegex(@"(?m)^local .* n/a\r?$").And.MatchRegex(@"(?m)^hosted .* 2\.25\r?$");
        JsonElement[] bindings = Bindings(fixture.ReadOut());
        bindings[0].GetProperty("provider").GetProperty("usage").TryGetProperty("cost", out _).Should().BeFalse();
        bindings[1].GetProperty("provider").GetProperty("usage").GetProperty("cost").GetDouble().Should().Be(2.25);
        bindings[1].GetProperty("provider").GetProperty("latencyP95Ms").GetDouble().Should().Be(20);
        bindings[1].GetProperty("outcomes").GetProperty("failureRate").GetDouble().Should().Be(0.1);
        bindings[0].GetProperty("outcomes").GetProperty("failureRate").GetDouble().Should().Be(0);
        bindings.Should().AllSatisfy(binding =>
            binding.GetProperty("discrimination").EnumerateArray()
                .Select(rung => rung.GetProperty("values").GetProperty("rocAuc").ValueKind)
                .Should().Equal(JsonValueKind.Number, JsonValueKind.Number));
    }

    [Fact]
    public async Task Sweep_And_Compare_Write_The_Json_Result_With_The_Full_Curve_And_Both_Splits_Named()
    {
        FixtureRow[] test =
        [
            SweepVerbTests.Row("true", 0.6, "test"),
            SweepVerbTests.Row("true", 0.5, "test"),
            SweepVerbTests.Row("false", 0.7, "test"),
            SweepVerbTests.Row("false", 0.2, "test"),
            SweepVerbTests.Row("true", 0.9, "test"),
            SweepVerbTests.Row("true", 0.4, "test"),
        ];
        using CliFixture fixture = await CliFixture.CreateAsync(
            SweepVerbTests.Guard(),
            [.. SweepVerbTests.Graded("tune"), .. test]);

        CliRun sweep = await fixture.RunAsync("sweep", "--warn", "min-recall=0.8", "--gate", "max-abstain=0.5");
        JsonElement swept = fixture.ReadOut();
        CliRun compare = await fixture.RunAsync("compare", "--warn", "min-recall=0.8", "--gate", "max-abstain=0.5");
        JsonElement compared = fixture.ReadOut();

        sweep.ExitCode.Should().Be(ExitCodes.Success);
        compare.ExitCode.Should().Be(ExitCodes.Success);
        swept.TryGetProperty("compare", out _).Should().BeFalse();
        compared.TryGetProperty("sweep", out _).Should().BeFalse();
        JsonElement section = swept.GetProperty("sweep");
        foreach (string rung in (string[])["warn", "deny"])
        {
            int printed = TableLength(sweep.Output, $"{rung} curve ");
            SweepVerbTests.Rung(section, rung).GetProperty("curve").GetProperty("points").GetArrayLength()
                .Should().Be(printed).And.BeGreaterThan(0);
        }

        JsonElement[] named =
        [
            SweepVerbTests.Rung(section, "warn"),
            SweepVerbTests.Rung(section, "deny"),
            section.GetProperty("gate"),
            section.GetProperty("split"),
            compared.GetProperty("compare").GetProperty("split"),
        ];
        named.Should().AllSatisfy(entry =>
        {
            entry.GetProperty("chosenOn").GetString().Should().Be("split 'tune' (10 rows)");
            entry.GetProperty("reportedOn").GetString().Should().Be("split 'test' (6 rows)");
        });
        JsonElement binding = Bindings(compared).Should().ContainSingle().Subject;
        SweepVerbTests.Rung(binding.GetProperty("sweep"), "warn").GetProperty("curve").GetProperty("points")
            .GetArrayLength().Should().Be(TableLength(sweep.Output, "warn curve "));
    }

    private static Policy Pair() => SweepVerbTests.Guard(EvidenceKind.Probability, "local", "hosted");

    private static FixtureRow[] Rows() =>
    [
        .. _labels.Select((label, index) => FixtureRow.Of(
            label,
            null,
            ("local", SweepVerbTests.Answer(EvidenceKind.Probability, _local[index])),
            ("hosted", SweepVerbTests.Answer(EvidenceKind.Probability, _hosted[index], "hosted")))),
    ];

    private static JsonElement[] Bindings(JsonElement result) =>
        [.. result.GetProperty("compare").GetProperty("bindings").EnumerateArray()];

    // The rows of the table under the first line that starts with the title: after its header and dashes, up to
    // the next blank line.
    private static int TableLength(string output, string title)
    {
        string[] lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        int start = Array.FindIndex(lines, line => line.StartsWith(title, StringComparison.Ordinal));
        start.Should().BeGreaterThanOrEqualTo(0, $"the output carries a table titled '{title}'");
        return lines.Skip(start + 3).TakeWhile(line => line.Length > 0).Count();
    }
}
