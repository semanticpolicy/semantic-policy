using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests;

public sealed class PolicyJsonTests
{
    // The same policy as Sample(), written by a person: "type" is the last property of every rule.
    private const string _handWritten = """
        {
          "id": "tool-guard",
          "mode": "enforce",
          "rules": [
            {
              "id": "prompt-injection", "question": "question-b", "flaggedAnswer": true,
              "ladder": [ "warn", "deny" ], "criteria": { "true": "true-marker" },
              "type": "boolean"
            },
            {
              "id": "route", "question": "question-c",
              "options": [
                { "key": "allow", "description": "description-a", "verdict": "allow" },
                { "key": "block", "description": "description-b", "verdict": "deny" }
              ],
              "type": "choice"
            },
            {
              "id": "harm", "question": "question-s",
              "levels": [ "harmless", "minor", "serious" ],
              "rungs": [ { "level": "minor", "verdict": "warn" }, { "level": "serious", "verdict": "deny" } ],
              "type": "score"
            }
          ],
          "bindings": [
            {
              "providerId": "local",
              "operatingPoints": [
                {
                  "ruleId": "prompt-injection",
                  "thresholds": [
                    { "verdict": "warn", "kind": "score", "atOrAbove": 0.4 },
                    { "verdict": "deny", "kind": "score", "atOrAbove": 0.8 }
                  ],
                  "gate": { "kind": "score", "below": 0.1 }
                },
                { "ruleId": "route", "thresholds": [], "gate": { "kind": "score", "below": 0.1 } },
                { "ruleId": "harm", "thresholds": [], "gate": { "kind": "score", "below": 0.1 } }
              ]
            },
            {
              "providerId": "jev",
              "operatingPoints": [
                {
                  "ruleId": "prompt-injection",
                  "thresholds": [
                    { "verdict": "warn", "kind": "probability", "atOrAbove": 0.6 },
                    { "verdict": "deny", "kind": "probability", "atOrAbove": 0.9 }
                  ],
                  "gate": { "kind": "probability", "below": 0.05 }
                }
              ]
            }
          ],
          "onFailure": { "action": "fallback", "then": "deny" },
          "budget": "00:00:02"
        }
        """;

    public static TheoryData<string, string> Documents => new()
    {
        { "serialized by the library", JsonSerializer.Serialize(Sample(), SemanticPolicyJson.Options) },
        { "written by hand with the discriminator last", _handWritten },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void Policy_Round_Trips_Through_Json(string label, string json)
    {
        Policy? read = JsonSerializer.Deserialize<Policy>(json, SemanticPolicyJson.Options);

        JsonNode document = JsonNode.Parse(json)!;
        document["mode"]!.GetValue<string>().Should().Be("enforce", label);
        document["rules"]![0]!["type"]!.GetValue<string>().Should().Be("boolean", label);
        document["rules"]![1]!["type"]!.GetValue<string>().Should().Be("choice", label);
        document["rules"]![2]!["type"]!.GetValue<string>().Should().Be("score", label);
        JsonNode threshold = document["bindings"]![1]!["operatingPoints"]![0]!["thresholds"]![0]!;
        threshold["kind"]!.GetValue<string>().Should().Be("probability", label);
        document["onFailure"]!["action"]!.GetValue<string>().Should().Be("fallback", label);
        document["onFailure"]!["then"]!.GetValue<string>().Should().Be("deny", label);

        read.Should().NotBeNull(label);
        read.Invoking(p => p.Validate()).Should().NotThrow(label);
        read.Should().BeEquivalentTo(
            Sample(),
            options => options.PreferringRuntimeMemberTypes().WithStrictOrdering(),
            label);
    }

    private static Policy Sample()
    {
        BooleanRule injection = Policy.Rule("prompt-injection")
            .Boolean("question-b", new BooleanCriteria("true-marker", null))
            .WhenTrue(Verdict.Warn, Verdict.Deny);
        ChoiceRule route = Policy.Rule("route").Choice("question-c")
            .Option("allow", "description-a", Verdict.Allow)
            .Option("block", "description-b", Verdict.Deny)
            .Build();
        ScoreRule harm = Policy.Rule("harm").Score("question-s", "harmless", "minor", "serious")
            .WarnAtOrAbove("minor")
            .DenyAtOrAbove("serious")
            .Build();

        return Policy.Define("tool-guard")
            .Enforce()
            .Rule(injection)
            .Rule(route)
            .Rule(harm)
            .Using("local", b => b.WarnAboveScore(0.4).DenyAboveScore(0.8).WhenScoreMarginBelow(0.1))
            .Using("jev", b => b.ForRule(
                "prompt-injection",
                op => op.WarnAboveProbability(0.6).DenyAboveProbability(0.9).WhenProbabilityMarginBelow(0.05)))
            .OnFailure(FailureBehavior.Fallback(Verdict.Deny))
            .Budget(TimeSpan.FromSeconds(2))
            .Build();
    }
}
