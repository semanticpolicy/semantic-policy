using System.Text.Json;
using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class EvalsResultTests
{
    [Fact]
    public void Result_Serializes_With_The_Version_String_String_Keys_And_Camel_Case_Enums()
    {
        using TempFile file = TempFile.Write("", ".json");
        EvalsResult result = new(
            EvalsResult.FormatV0,
            "report",
            "1.2.3",
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero),
            "guard",
            PolicyMode.Enforce,
            Samples.Injection,
            DecisionType.Boolean,
            new RowSelection(10, 9, 8, ["label=answer"], "metadata", 4, 4),
            new ReportSection(
                new OutcomeCounts(
                    8,
                    6,
                    1,
                    new Dictionary<string, int>(StringComparer.Ordinal) { ["timeout"] = 1 },
                    1,
                    0,
                    new Dictionary<string, int>(StringComparer.Ordinal),
                    0.125,
                    0.125),
                new Dictionary<string, int>(StringComparer.Ordinal) { ["allow"] = 4, ["deny"] = 2 },
                [new RungMetrics(Verdict.Warn, new BinaryConfusion(3, 1, 2, 0))],
                Classes: null,
                [new RungDiscrimination(Verdict.Deny, new Discrimination(0.8125, 0.75, 6))],
                new Calibration(true, ["probability"], 6, 0.2, 0.15, [new ReliabilityBin(0.9, 1.0, 2, 0.95, 1.0)]),
                [new ProviderStats("local", "model-local", 8, 12, 60, new Dictionary<string, double>(StringComparer.Ordinal) { ["cost"] = 0.5 })],
                ["one provider answered every row"]));

        ResultWriter.Write(file.Path, result);

        string json = File.ReadAllText(file.Path);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        root.GetProperty("format").GetString().Should().Be("semanticpolicy/evals-result/v0");

        // Every enum on the way out is its camel-case name. A number here would still parse and would still
        // mean something to this tool alone, so nothing but the spelling can catch it.
        root.GetProperty("mode").Should().Match<JsonElement>(mode =>
            mode.ValueKind == JsonValueKind.String && mode.GetString() == "enforce");
        root.GetProperty("decisionType").GetString().Should().Be("boolean");
        JsonElement report = root.GetProperty("report");
        report.GetProperty("rungs")[0].GetProperty("rung").GetString().Should().Be("warn");
        report.GetProperty("discrimination")[0].GetProperty("rung").GetString().Should().Be("deny");

        // A verdict and a failure kind are dictionary keys, which are written by the key's own spelling
        // rather than by the enum converter; they have to agree with it anyway.
        report.GetProperty("verdicts").EnumerateObject().Select(property => property.Name).Should()
            .Equal("allow", "deny");
        report.GetProperty("outcomes").GetProperty("failedByKind").GetProperty("timeout").GetInt32().Should().Be(1);

        // A section a run did not produce is absent, not null: later verbs add sections, and a reader tells
        // them apart by presence.
        report.TryGetProperty("classes", out _).Should().BeFalse();
        root.GetProperty("rows").GetProperty("splitSource").GetString().Should().Be("metadata");
        json.Should().Contain("\n  \"verb\": \"report\"").And.NotContain("\r");
    }
}
