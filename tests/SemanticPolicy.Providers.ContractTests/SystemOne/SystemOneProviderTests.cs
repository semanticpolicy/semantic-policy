using System.Net;
using System.Text.Json;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.SystemOne;

namespace SemanticPolicy.Providers.ContractTests.SystemOne;

public sealed class SystemOneProviderTests
{
    [Fact]
    public async Task Request_Identifies_As_SystemOne_And_Posts_The_Configured_Model()
    {
        SystemOneHarness harness = new();
        harness.ScriptSuccess(DecisionType.Boolean);

        await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        RecordedRequest sent = harness.LastRequest;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.Should().Be(new Uri("http://127.0.0.1:8000/v1/systemone"));
        sent.Header("User-Agent").Should().StartWith("SemanticPolicy.Providers.SystemOne/");
        sent.Header("Content-Type").Should().Be("application/json");
        harness.LastBody.GetProperty("model").GetString().Should().Be(SystemOneHarness.Model);
    }

    // A provider built by hand never reads the environment, so options that name only a variable would
    // send no bearer and report no error; the constructor refuses them instead.
    [Fact]
    public void Constructor_Rejects_A_Key_Variable_It_Would_Never_Read()
    {
        using HttpClient client = new(new ScriptedHttpMessageHandler());
        SystemOneOptions options = new()
        {
            BaseUrl = SystemOneHarness.BaseUrl,
            Model = SystemOneHarness.Model,
            ApiKeyVariable = $"SEMANTICPOLICY_TEST_{Guid.NewGuid():N}",
        };

        Action variableOnly = () => _ = new SystemOneProvider(client, options);

        variableOnly.Should().Throw<ArgumentException>().Which.ParamName.Should().Be(nameof(SystemOneOptions.ApiKeyVariable));

        options.ApiKey = "test-key-not-a-credential";
        Action withKey = () => _ = new SystemOneProvider(client, options);

        withKey.Should().NotThrow();
    }

    // The numbers are the server's under either kind; the declaration picks the kind and the scale, and
    // under Score a Boolean answer carries both ends, because a score has no complement to derive.
    [Theory]
    [InlineData(EvidenceKind.Score, DecisionType.Boolean, "systemone")]
    [InlineData(EvidenceKind.Score, DecisionType.Choice, "systemone")]
    [InlineData(EvidenceKind.Score, DecisionType.Score, "systemone")]
    [InlineData(EvidenceKind.Probability, DecisionType.Boolean, "calibrated")]
    [InlineData(EvidenceKind.Probability, DecisionType.Choice, "calibrated")]
    [InlineData(EvidenceKind.Probability, DecisionType.Score, "calibrated")]
    public async Task Answers_Carry_The_Declared_Evidence_Kind_On_Its_Scale(
        EvidenceKind declared,
        DecisionType type,
        string scale)
    {
        SystemOneHarness harness = new(options => options.Evidence = declared);
        harness.ScriptSuccess(type);
        Dictionary<string, double> expected = (type, declared) switch
        {
            (DecisionType.Boolean, EvidenceKind.Score) => new(StringComparer.Ordinal) { ["true"] = 0.91, ["false"] = 0.09 },
            (DecisionType.Boolean, _) => new(StringComparer.Ordinal) { ["true"] = 0.91 },
            (DecisionType.Choice, _) => new(StringComparer.Ordinal) { ["allow"] = 0.15, ["review"] = 0.7, ["block"] = 0.15 },
            _ => new(StringComparer.Ordinal) { ["low"] = 0.1, ["medium"] = 0.6, ["high"] = 0.3 },
        };

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(type, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        harness.Provider.Capabilities.Evidence.Should().BeEquivalentTo([declared]);
        result.Outcome.Should().Be(ProviderOutcome.Success);
        Evidence evidence = result.Evidence.Should().ContainSingle().Which;
        evidence.Kind.Should().Be(declared);
        evidence.Scale.Should().Be(scale);
        evidence.Values.Should().BeEquivalentTo(
            expected,
            options => options
                .Using<double>(pair => pair.Subject.Should().BeApproximately(pair.Expectation, 1e-9))
                .WhenTypeIs<double>());
    }

    [Theory]
    [InlineData(null, 20000, true)]
    [InlineData(100, 100, true)]
    [InlineData(100, 101, false)]
    public async Task Context_Length_Against_MaxContextLength_Decides_Whether_The_Server_Is_Called(
        int? maxContextLength,
        int length,
        bool sent)
    {
        SystemOneHarness harness = new(options => options.MaxContextLength = maxContextLength);
        harness.ScriptSuccess(DecisionType.Boolean);

        // An object with one string property renders as that string alone, so the canonical text is
        // exactly this long.
        string text = ProviderHarness.Marker + new string('x', length - ProviderHarness.Marker.Length);
        DecisionRequest request = new(
            DecisionType.Boolean,
            $"Does the text mention {ProviderHarness.Marker}?",
            JsonSerializer.SerializeToElement(new { text }));

        ProviderResult result = await harness.Provider.DecideAsync(request, TestContext.Current.CancellationToken);

        if (sent)
        {
            result.Outcome.Should().Be(ProviderOutcome.Success);
            harness.RequestCount.Should().Be(1);
        }
        else
        {
            harness.RequestCount.Should().Be(0);
            result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
            result.Outcome.Kind.Should().Be(FailureKind.RejectedInput);
            result.Outcome.Message.Should().Contain("101").And.Contain("100").And.NotContain(ProviderHarness.Marker);
            result.ToString().Should().NotContain(ProviderHarness.Marker);
        }
    }

    [Theory]
    [InlineData("server-reported-model", "server-reported-model")]
    [InlineData(null, SystemOneHarness.Model)]
    public async Task Result_Model_Is_The_Response_Model_Else_The_Configured_One(string? answered, string expected)
    {
        SystemOneHarness harness = new();
        harness.Handler.Respond(
            HttpStatusCode.OK,
            SystemOneFixtures.BooleanAnswer(0.91, envelope: new SystemOneFixtures.Envelope(Model: answered)));

        ProviderResult result = await harness.Provider.DecideAsync(
            harness.CreateRequest(DecisionType.Boolean, ProviderHarness.Marker),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ProviderOutcome.Success);
        result.Provider.Model.Should().Be(expected);
    }
}
