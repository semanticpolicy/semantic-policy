using System.Text.Json;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests.Protocol;

public sealed class ProviderResultTests
{
    private static readonly ProviderMetadata _provider = new("provider-a", "model-a", 12);

    [Fact]
    public void Rendered_Result_Names_Its_Answer_And_Never_The_Raw_Response()
    {
        ProviderResult result = new(
            DecisionType.Choice,
            ProviderOutcome.Success,
            new ChoiceValue("allow"),
            [new Evidence(EvidenceKind.Probability, new Dictionary<string, double> { ["allow"] = 0.9, ["block"] = 0.1 })],
            _provider,
            Raw: JsonSerializer.Deserialize<JsonElement>("""{ "text": "raw-marker" }"""));

        string text = result.ToString();

        text.Should()
            .Be(
                "ProviderResult { Protocol = semanticpolicy/v0, Type = Choice, Outcome = Success, "
                + "Value = ChoiceValue { Option = allow }, Evidence = 1, Provider = provider-a, Raw = Object }")
            .And.NotContain("raw-marker");
    }

    [Fact]
    public void Rendered_Failure_Names_Its_Kind()
    {
        ProviderResult result = ProviderResult.Failed(DecisionType.Boolean, FailureKind.Timeout, "timed out", _provider);

        result.ToString().Should().Be(
            "ProviderResult { Protocol = semanticpolicy/v0, Type = Boolean, Outcome = Failure, Kind = Timeout, "
            + "Evidence = 0, Provider = provider-a }");
    }
}
