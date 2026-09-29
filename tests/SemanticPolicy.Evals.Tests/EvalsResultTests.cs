using System.Text.Json;
using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class EvalsResultTests
{
    private static readonly string _smoke =
        Path.Combine(ExampleDatasetsTests.RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "smoke");

    // The proportions a Wilson interval is read on, as the result names them.
    private static readonly string[] _rates =
        ["accuracy", "precision", "recall", "falsePositiveRate", "falseNegativeRate", "failureRate", "abstentionRate"];

    // Values that are not a count over a count, which an interval from counts would misrepresent.
    private static readonly string[] _scores = ["f1", "macroF1", "rocAuc", "prAuc", "ece", "brier"];

    [Fact]
    public void Result_Serializes_With_The_Version_String_String_Keys_And_Camel_Case_Enums()
    {
        using TempFile file = TempFile.Write("", ".json");
        EvalsResult result = Result(new BinaryConfusion(3, 1, 2, 0));

        ResultWriter.Write(file.Path, result);

        string json = File.ReadAllText(file.Path);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        root.GetProperty("format").GetString().Should().Be("semanticpolicy/evals-result/v0");
        json.Should().Contain("\"toolVersion\": \"1.2.3+abc\"");

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

    [Theory]
    [InlineData(0, 0, 3, 2, "precision")]
    [InlineData(2, 0, 0, 1, "falsePositiveRate")]
    public void Proportion_With_Nothing_To_Divide_By_Has_No_Interval(
        int truePositives,
        int falsePositives,
        int trueNegatives,
        int falseNegatives,
        string undefined)
    {
        using TempFile file = TempFile.Write("", ".json");

        ResultWriter.Write(file.Path, Result(new BinaryConfusion(truePositives, falsePositives, trueNegatives, falseNegatives)));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file.Path));
        JsonElement matrix = document.RootElement.GetProperty("report").GetProperty("rungs")[0].GetProperty("matrix");
        matrix.TryGetProperty(undefined, out _).Should().BeFalse();
        matrix.TryGetProperty($"{undefined}Interval", out _).Should().BeFalse();
        matrix.TryGetProperty("recallInterval", out _).Should().BeTrue("recall has rows to divide by in both cases");
    }

    public static TheoryData<string, string, string[], string[]> Verbs => new()
    {
        { "report", "prompt-injection", [], _rates },
        { "report", "support-router", [], ["accuracy", "precision", "recall", "failureRate", "abstentionRate"] },
        { "sweep", "prompt-injection", ["--provider", "local", "--deny", "min-precision=0.95"], _rates },
        { "compare", "prompt-injection", ["--deny", "min-precision=0.95"], _rates },
        { "compare", "support-router", ["--gate", "min-accuracy=0.9"], ["accuracy", "failureRate", "abstentionRate"] },
    };

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task Every_Proportion_Of_The_Result_Carries_Its_Interval(
        string verb,
        string set,
        string[] options,
        string[] expected)
    {
        using TempFile result = TempFile.Write("", ".json");

        CliRun run = await CliFixture.InvokeAsync(
        [
            verb,
            "--policy", Path.Combine(_smoke, $"{set}.policy.json"),
            "--dataset", Path.Combine(_smoke, $"{set}.smoke.jsonl"),
            "--recording", Path.Combine(_smoke, $"{set}.recording.jsonl"),
            "--out", result.Path,
            .. options,
        ]);

        run.ExitCode.Should().BeOneOf([ExitCodes.Success, ExitCodes.InfeasibleConstraint], run.Error);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(result.Path));
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonElement member in Objects(document.RootElement))
        {
            foreach (JsonProperty property in member.EnumerateObject())
            {
                if (_rates.Contains(property.Name))
                {
                    double value = property.Value.GetDouble();
                    member.TryGetProperty($"{property.Name}Interval", out JsonElement interval).Should()
                        .BeTrue("'{0}' is a rate in {1}", property.Name, member.GetRawText());
                    interval.GetProperty("lower").GetDouble().Should().BeInRange(0, value);
                    interval.GetProperty("upper").GetDouble().Should().BeInRange(value, 1);
                    seen.Add(property.Name);
                }
                else if (property.Name.EndsWith("Interval", StringComparison.Ordinal))
                {
                    // An interval is left out exactly when its value is.
                    member.TryGetProperty(property.Name[..^"Interval".Length], out _).Should().BeTrue(property.Name);
                }
            }

            foreach (string score in _scores)
            {
                member.TryGetProperty($"{score}Interval", out _).Should().BeFalse("{0} is not a count over a count", score);
            }
        }

        seen.Should().BeEquivalentTo(expected);
    }

    private static EvalsResult Result(BinaryConfusion matrix) =>
        new(
            EvalsResult.FormatV0,
            "report",
            "1.2.3+abc",
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
                [new RungMetrics(Verdict.Warn, matrix)],
                Classes: null,
                [new RungDiscrimination(Verdict.Deny, new Discrimination(0.8125, 0.75, 6))],
                new Calibration(true, ["probability"], 6, 0.2, 0.15, [new ReliabilityBin(0.9, 1.0, 2, 0.95, 1.0)]),
                [new ProviderStats("local", "model-local", 8, 0, 12, 60, new Dictionary<string, double>(StringComparer.Ordinal) { ["cost"] = 0.5 })],
                ["one provider answered every row"]));

    // Every object in a JSON document, the root first, then depth first through objects and arrays.
    private static IEnumerable<JsonElement> Objects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
        }

        IEnumerable<JsonElement> children = element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Select(property => property.Value),
            JsonValueKind.Array => element.EnumerateArray(),
            _ => [],
        };
        foreach (JsonElement child in children)
        {
            foreach (JsonElement inner in Objects(child))
            {
                yield return inner;
            }
        }
    }
}
