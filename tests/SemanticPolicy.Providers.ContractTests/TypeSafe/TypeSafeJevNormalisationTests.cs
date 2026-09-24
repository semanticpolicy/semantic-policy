using System.Net;
using System.Text.Json;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;

namespace SemanticPolicy.Providers.ContractTests.TypeSafe;

public sealed class TypeSafeJevNormalisationTests
{
    private const string _marker = ProviderHarness.Marker;

    public static TheoryData<string, DecisionType, string, bool> Deviations => new()
    {
        { "not JSON", DecisionType.Boolean, $"noul: 0.9 {_marker}", false },
        { "HTML", DecisionType.Boolean, SystemOneFixtures.Html(_marker), false },
        { "a JSON string", DecisionType.Boolean, JsonSerializer.Serialize($"answers {_marker}"), true },
        { "no answers", DecisionType.Boolean, JsonSerializer.Serialize(new { model = "jev", note = _marker }), true },
        {
            "answers without decision",
            DecisionType.Boolean,
            JsonSerializer.Serialize(new { answers = new { other = new { type = "noul", noul = 0.5 } }, note = _marker }),
            true
        },
        { "a noul answer to a Choice request", DecisionType.Choice, Answer("noul", ("noul", 0.9)), true },
        { "noul missing", DecisionType.Boolean, Answer("noul", ("explanation", "none")), true },
        { "noul above one", DecisionType.Boolean, Answer("noul", ("noul", 1.2)), true },
        { "Choice without probabilities", DecisionType.Choice, Answer("choice", ("choice", "allow")), true },
        {
            "probabilities missing an option",
            DecisionType.Choice,
            Answer("choice", ("choice", "allow"), ("probabilities", Probabilities(("allow", 0.6), ("review", 0.4)))),
            true
        },
        {
            "probabilities with an extra key",
            DecisionType.Choice,
            Answer(
                "choice",
                ("choice", "allow"),
                ("probabilities", Probabilities(("allow", 0.5), ("review", 0.2), ("block", 0.2), ("other", 0.1)))),
            true
        },
        {
            "choice not an option",
            DecisionType.Choice,
            Answer("choice", ("choice", "escalate"), ("probabilities", TypeSafeJevHarness.Distribution(0.2, 0.3, 0.5))),
            true
        },
        {
            "legend not matching the levels",
            DecisionType.Score,
            Answer(
                "score",
                ("legend", SystemOneFixtures.ByIndex(["low", "high", "medium"])),
                ("probabilities", SystemOneFixtures.ByIndex<double>([0.2, 0.3, 0.5]))),
            true
        },
        {
            "probabilities missing an index",
            DecisionType.Score,
            Answer(
                "score",
                ("legend", SystemOneFixtures.ByIndex(TypeSafeJevHarness.Levels)),
                ("probabilities", Probabilities(("0", 0.5), ("1", 0.5)))),
            true
        },
    };

    [Theory]
    [InlineData(0.91, true)]
    [InlineData(0.5, true)]
    [InlineData(0.49, false)]
    [InlineData(0, false)]
    public async Task Boolean_Answer_Becomes_A_Value_At_The_Half_Cut_With_Calibrated_Evidence(double noul, bool expected)
    {
        TypeSafeJevHarness harness = new();
        harness.Handler.Respond(HttpStatusCode.OK, SystemOneFixtures.BooleanAnswer(noul));

        ProviderResult result = await Decide(harness, DecisionType.Boolean);

        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Value.Should().Be(new BooleanValue(expected));
        Evidence evidence = result.Evidence.Should().ContainSingle().Which;
        evidence.Kind.Should().Be(EvidenceKind.Probability);
        evidence.Scale.Should().Be("calibrated");
        evidence.Values.Should().Equal(new Dictionary<string, double> { ["true"] = noul });
        HasExtra(result, "confidence").Should().BeFalse();
    }

    [Fact]
    public async Task Choice_Answer_Keeps_The_Provider_Choice_And_The_Distribution_Over_The_Options()
    {
        TypeSafeJevHarness harness = new();
        harness.Handler.Respond(
            HttpStatusCode.OK,
            SystemOneFixtures.ChoiceAnswer("block", TypeSafeJevHarness.Distribution(0.05, 0.25, 0.7), confidence: 0.7));

        ProviderResult result = await Decide(harness, DecisionType.Choice);

        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Value.Should().Be(new ChoiceValue("block"));
        Evidence evidence = result.Evidence.Should().ContainSingle().Which;
        evidence.Kind.Should().Be(EvidenceKind.Probability);
        evidence.Scale.Should().Be("calibrated");
        evidence.Values.Should().Equal(
            new Dictionary<string, double> { ["allow"] = 0.05, ["review"] = 0.25, ["block"] = 0.7 });
        result.Provider.Extra!.Value.GetProperty("confidence").GetDouble().Should().Be(0.7);
    }

    [Theory]
    [InlineData(new[] { 0.1, 0.6, 0.3 }, 1)]
    [InlineData(new[] { 0.45, 0.45, 0.1 }, 0)]
    public async Task Score_Answer_Picks_The_Most_Probable_Level_With_Ties_To_The_Lowest_Index(
        double[] probabilities,
        int expectedIndex)
    {
        TypeSafeJevHarness harness = new();
        harness.Handler.Respond(
            HttpStatusCode.OK,
            SystemOneFixtures.ScoreAnswer(TypeSafeJevHarness.Levels, probabilities, score: 1.2));

        ProviderResult result = await Decide(harness, DecisionType.Score);

        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Value.Should().Be(new ScoreValue(TypeSafeJevHarness.Levels[expectedIndex], expectedIndex));
        Evidence evidence = result.Evidence.Should().ContainSingle().Which;
        evidence.Scale.Should().Be("calibrated");
        evidence.Values.Should().Equal(
            new Dictionary<string, double>
            {
                ["low"] = probabilities[0],
                ["medium"] = probabilities[1],
                ["high"] = probabilities[2],
            });
        result.Provider.Extra!.Value.GetProperty("expectedIndex").GetDouble().Should().Be(1.2);
    }

    [Theory]
    [InlineData("everything in the body")]
    [InlineData("a request id header only")]
    [InlineData("nothing")]
    public async Task Metadata_Comes_From_The_Response_With_Configured_Fallbacks(string shape)
    {
        TypeSafeJevHarness harness = new();
        string body = shape == "everything in the body"
            ? SystemOneFixtures.BooleanAnswer(
                0.8,
                envelope: new SystemOneFixtures.Envelope("jev-1.13.0-20260901", "gen-dec-0001", Usage: true, Provider: "TypeSafe"))
            : SystemOneFixtures.BooleanAnswer(0.8);
        Dictionary<string, string>? headers = shape == "a request id header only"
            ? new Dictionary<string, string> { ["x-typesafe-request-id"] = "req-0002" }
            : null;
        harness.Handler.Respond(HttpStatusCode.OK, body, headers: headers);

        ProviderResult result = await Decide(harness, DecisionType.Boolean);

        ProviderMetadata provider = result.Provider;
        switch (shape)
        {
            case "everything in the body":
                provider.Model.Should().Be("jev-1.13.0-20260901");
                provider.RequestId.Should().Be("gen-dec-0001");
                provider.Usage!.Value.GetProperty("input_tokens").GetInt32().Should().Be(128);
                provider.Extra!.Value.GetProperty("provider").GetString().Should().Be("TypeSafe");
                break;
            case "a request id header only":
                provider.Model.Should().Be("jev-1.13.0");
                provider.RequestId.Should().Be("req-0002");
                provider.Usage.Should().BeNull();
                HasExtra(result, "provider").Should().BeFalse();
                break;
            default:
                provider.Model.Should().Be("jev-1.13.0");
                provider.RequestId.Should().BeNull();
                break;
        }

        JsonElement.DeepEquals(result.Raw!.Value, JsonSerializer.Deserialize<JsonElement>(body)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Deviations))]
    public async Task Success_Body_Outside_The_Contract_Is_Malformed(
        string deviation,
        DecisionType type,
        string body,
        bool json)
    {
        TypeSafeJevHarness harness = new();
        harness.Handler.Respond(HttpStatusCode.OK, body, json ? "application/json" : "text/plain");

        ProviderResult result = await Decide(harness, type);

        result.Outcome.Status.Should().Be(OutcomeStatus.Failure, "{0} is outside the contract", deviation);
        result.Outcome.Kind.Should().Be(FailureKind.Malformed);
        result.Outcome.Message.Should().NotBeNullOrWhiteSpace().And.NotContain(_marker);
        result.Value.Should().BeNull();
        result.Evidence.Should().BeEmpty();
        (result.Raw is not null).Should().Be(json);
    }

    private static Task<ProviderResult> Decide(TypeSafeJevHarness harness, DecisionType type) =>
        harness.Provider.DecideAsync(harness.CreateRequest(type, _marker), TestContext.Current.CancellationToken);

    private static bool HasExtra(ProviderResult result, string name) =>
        result.Provider.Extra is { ValueKind: JsonValueKind.Object } extra && extra.TryGetProperty(name, out _);

    /// <summary>A 200 body whose answer has the fields given and a note carrying the marker.</summary>
    private static string Answer(string type, params (string Name, object? Value)[] fields) =>
        SystemOneFixtures.Response(SystemOneFixtures.Answer(type, [.. fields, ("note", _marker)]));

    private static Dictionary<string, double> Probabilities(params (string Key, double Value)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
}
