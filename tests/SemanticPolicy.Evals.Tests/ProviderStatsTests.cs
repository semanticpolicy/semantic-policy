using System.Text.Json;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class ProviderStatsTests
{
    // Two usage objects of the shape a hosted provider reports, plus a string and a nested object that
    // carry no number to add up.
    private const string _usageOfTheFirstCall = """
        {"cost": 0.002, "input_tokens": 120, "output_tokens": 15, "model": "m-1", "detail": {"cached": 3}}
        """;

    private const string _usageOfTheSecondCall = """
        {"cost": 0.003, "input_tokens": 80, "output_tokens": 20, "model": "m-1", "detail": {"cached": 1}}
        """;

    [Fact]
    public void Provider_Stats_Report_Nearest_Rank_P50_And_P95_Latency_Per_Provider()
    {
        // Ten local latencies, which sort to 3, 5, 8, 9, 12, 17, 21, 30, 45, 60: nearest rank takes the
        // 5th for p50 and the 10th for p95. The timed-out call is an attempt like any other.
        (string Provider, ProviderResult Result)[] attempts =
        [
            Attempt("local", 12), Attempt("local", 5), Attempt("local", 30), Attempt("local", 8),
            Attempt("local", 21), Attempt("local", 3), Attempt("local", 17), Attempt("local", 45),
            Attempt("local", 9), Failure("local", 60),
            // Four hosted latencies sort to 50, 100, 150, 200: the 2nd for p50, the 4th for p95.
            Attempt("hosted", 100), Attempt("hosted", 50), Attempt("hosted", 200), Attempt("hosted", 150),
        ];

        IReadOnlyList<ProviderStats> stats = ProviderStats.Compute(attempts);

        stats.Select(entry => entry.Provider).Should().Equal("local", "hosted");
        stats[0].Attempts.Should().Be(10);
        stats[0].Model.Should().Be("model-local");
        stats[0].LatencyP50Ms.Should().Be(12);
        stats[0].LatencyP95Ms.Should().Be(60);
        stats[1].Attempts.Should().Be(4);
        stats[1].LatencyP50Ms.Should().Be(100);
        stats[1].LatencyP95Ms.Should().Be(200);
    }

    [Fact]
    public void Provider_Stats_Sum_Every_Top_Level_Numeric_Usage_Field_Under_Its_Own_Name_And_Ignore_The_Rest()
    {
        (string Provider, ProviderResult Result)[] attempts =
        [
            Attempt("hosted", 100, _usageOfTheFirstCall),
            Attempt("hosted", 150, _usageOfTheSecondCall),
            Attempt("hosted", 120),
        ];

        ProviderStats stats = ProviderStats.Compute(attempts).Should().ContainSingle().Subject;

        // Only the three numeric properties survive, each under the name the provider gave it; "model" is a
        // string and "detail" is an object, and neither is a number this tool may add up.
        stats.Usage.Keys.Should().BeEquivalentTo("cost", "input_tokens", "output_tokens");
        stats.Usage["cost"].Should().BeApproximately(0.005, 1e-12);
        stats.Usage["input_tokens"].Should().Be(200);
        stats.Usage["output_tokens"].Should().Be(35);
        stats.Attempts.Should().Be(3);
    }

    private static (string Provider, ProviderResult Result) Attempt(string provider, double latencyMs, string? usage = null)
    {
        ProviderMetadata metadata = new(provider, "model-" + provider, latencyMs, Usage: Parse(usage));
        return (provider, new ProviderResult(
            DecisionType.Boolean,
            ProviderOutcome.Success,
            new BooleanValue(true),
            [],
            metadata));
    }

    private static (string Provider, ProviderResult Result) Failure(string provider, double latencyMs) =>
        (provider, ProviderResult.Failed(
            DecisionType.Boolean,
            FailureKind.Timeout,
            "no answer",
            new ProviderMetadata(provider, "model-" + provider, latencyMs)));

    private static JsonElement? Parse(string? usage) =>
        usage is null ? null : JsonDocument.Parse(usage).RootElement.Clone();
}
