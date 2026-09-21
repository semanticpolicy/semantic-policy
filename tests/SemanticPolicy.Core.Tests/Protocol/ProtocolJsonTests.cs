using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests.Protocol;

public sealed class ProtocolJsonTests
{
    public static TheoryData<DecisionRequest, string> RequestSamples => new()
    {
        {
            new DecisionRequest(
                DecisionType.Boolean,
                "q",
                Element("\"part-a\""),
                Criteria: new BooleanCriteria("t", "f")),
            """
            {
              "protocol": "semanticpolicy/v0",
              "type": "boolean",
              "question": "q",
              "context": "part-a",
              "criteria": { "true": "t", "false": "f" }
            }
            """
        },
        {
            new DecisionRequest(
                DecisionType.Choice,
                "q",
                Element("""{ "text": "part-a" }"""),
                Options: new Dictionary<string, string> { ["allow"] = "a", ["human_review"] = "h", ["block"] = "b" }),
            """
            {
              "protocol": "semanticpolicy/v0",
              "type": "choice",
              "question": "q",
              "context": { "text": "part-a" },
              "options": { "allow": "a", "human_review": "h", "block": "b" }
            }
            """
        },
        {
            new DecisionRequest(
                DecisionType.Score,
                "q",
                Element("""[ "part-a", "part-b" ]"""),
                Levels: ["harmless", "minor", "moderate", "serious", "critical"]),
            """
            {
              "protocol": "semanticpolicy/v0",
              "type": "score",
              "question": "q",
              "context": [ "part-a", "part-b" ],
              "levels": [ "harmless", "minor", "moderate", "serious", "critical" ]
            }
            """
        },
    };

    public static TheoryData<string, ProviderOutcome, DecisionValue?> ResultSamples => new()
    {
        {
            """
            {
              "protocol": "semanticpolicy/v0",
              "type": "boolean",
              "outcome": { "status": "success" },
              "value": true,
              "evidence": [
                { "kind": "probability", "scale": "calibrated", "values": { "true": 0.91, "false": 0.09 } }
              ],
              "provider": { "id": "provider-a", "model": "model-a", "latencyMs": 349, "requestId": "r-1" }
            }
            """,
            ProviderOutcome.Success,
            new BooleanValue(true)
        },
        {
            """
            {
              "protocol": "semanticpolicy/v0",
              "type": "choice",
              "outcome": { "status": "success" },
              "value": "allow",
              "evidence": [
                { "kind": "score", "scale": "sigmoid", "values": { "allow": 0.64, "human_review": 0.7, "block": 0.43 } },
                { "kind": "logit", "values": { "allow": 0.6, "human_review": 0.83, "block": -0.3 } }
              ],
              "provider": {
                "id": "provider-b", "model": "model-b", "latencyMs": 12.5,
                "usage": { "tokens": 7 }, "extra": { "confidence": 0.5 }
              }
            }
            """,
            ProviderOutcome.Success,
            new ChoiceValue("allow")
        },
        {
            """
            {
              "protocol": "semanticpolicy/v0",
              "type": "score",
              "outcome": { "status": "success" },
              "value": { "level": "critical", "index": 4 },
              "evidence": [
                { "kind": "probability", "scale": "calibrated",
                  "values": { "harmless": 0, "minor": 0, "moderate": 0, "serious": 0.04, "critical": 0.96 } }
              ],
              "provider": { "id": "provider-a", "model": "model-a", "latencyMs": 349 }
            }
            """,
            ProviderOutcome.Success,
            new ScoreValue("critical", 4)
        },
        {
            """
            {
              "protocol": "semanticpolicy/v0",
              "type": "boolean",
              "outcome": { "status": "abstain", "message": "declined" },
              "evidence": [
                { "kind": "probability", "values": { "true": 0.5, "false": 0.5 } }
              ],
              "provider": { "id": "provider-a", "model": "model-a", "latencyMs": 3 }
            }
            """,
            ProviderOutcome.Abstain("declined"),
            null
        },
        {
            """
            {
              "protocol": "semanticpolicy/v0",
              "type": "choice",
              "outcome": { "status": "failure", "kind": "rejectedInput", "message": "refused" },
              "evidence": [],
              "provider": { "id": "provider-a", "latencyMs": 0 }
            }
            """,
            ProviderOutcome.Failure(FailureKind.RejectedInput, "refused"),
            null
        },
    };

    [Theory]
    [MemberData(nameof(RequestSamples))]
    public void Request_Serializes_To_The_Protocol_Wire_Shape(DecisionRequest request, string expected)
    {
        string written = JsonSerializer.Serialize(request, SemanticPolicyJson.Options);
        DecisionRequest? read = JsonSerializer.Deserialize<DecisionRequest>(expected, SemanticPolicyJson.Options);

        AssertJsonEquivalent(written, expected);
        read.Should().NotBeNull();
        read.Protocol.Should().Be(ProtocolVersion.V0);
        read.Type.Should().Be(request.Type);
        AssertJsonEquivalent(JsonSerializer.Serialize(read, SemanticPolicyJson.Options), expected);
    }

    [Theory]
    [MemberData(nameof(ResultSamples))]
    public void Result_Round_Trips_Through_The_Wire_Shape(string json, ProviderOutcome outcome, DecisionValue? value)
    {
        ProviderResult? result = JsonSerializer.Deserialize<ProviderResult>(json, SemanticPolicyJson.Options);

        result.Should().NotBeNull();
        result.Protocol.Should().Be(ProtocolVersion.V0);
        result.Outcome.Should().Be(outcome);
        result.Value.Should().Be(value);
        AssertJsonEquivalent(JsonSerializer.Serialize(result, SemanticPolicyJson.Options), json);
    }

    [Fact]
    public void Serialized_Result_Omits_Raw()
    {
        ProviderMetadata provider = new("provider-a", "model-a", 12);
        ProviderResult result = new(
            DecisionType.Boolean,
            ProviderOutcome.Success,
            new BooleanValue(false),
            [],
            provider,
            Raw: Element("""{ "part-a": 1 }"""));

        string written = JsonSerializer.Serialize(result, SemanticPolicyJson.Options);
        ProviderResult? read = JsonSerializer.Deserialize<ProviderResult>(
            """
            {
              "protocol": "semanticpolicy/v0",
              "type": "boolean",
              "outcome": { "status": "success" },
              "value": false,
              "evidence": [],
              "raw": { "part-a": 1 },
              "provider": { "id": "provider-a", "model": "model-a", "latencyMs": 12 }
            }
            """,
            SemanticPolicyJson.Options);

        JsonNode.Parse(written)!.AsObject().ContainsKey("raw").Should().BeFalse();
        read.Should().NotBeNull();
        read.Raw.Should().BeNull();
    }

    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static void AssertJsonEquivalent(string actual, string expected)
    {
        JsonNode.DeepEquals(JsonNode.Parse(actual), JsonNode.Parse(expected))
            .Should().BeTrue("the JSON should be {0} but was {1}", expected, actual);
    }
}
