using SemanticPolicy.Evals.Policies;

namespace SemanticPolicy.Evals.Tests;

public sealed class PolicyFileTests
{
    [Fact]
    public void PolicyFile_Loads_A_Round_Trip_Policy_And_Rejects_An_Invalid_One_Naming_The_Rule()
    {
        using TempFile valid = TempFile.Write(Document(), ".json");
        using TempFile invalid = TempFile.Write(Document(ladder: """[ "deny", "warn" ]"""), ".json");
        using TempFile torn = TempFile.Write("""{ "id": "guard", """, ".json");

        Policy policy = PolicyFile.Load(valid.Path);
        Action loadInvalid = () => PolicyFile.Load(invalid.Path);
        Action loadTorn = () => PolicyFile.Load(torn.Path);

        policy.Id.Should().Be("guard");
        policy.Rules.Should().ContainSingle().Which.Id.Should().Be("prompt-injection");
        loadInvalid.Should().Throw<EvalsException>().Which.Message.Should()
            .Contain("prompt-injection").And.Contain(invalid.Path);
        loadTorn.Should().Throw<EvalsException>().Which.Message.Should().Contain(torn.Path);
    }

    [Fact]
    public void Rule_Selection_Requires_A_Rule_Id_When_The_Policy_Has_Several_Rules()
    {
        using TempFile single = TempFile.Write(Document(), ".json");
        using TempFile several = TempFile.Write(Document(withRoute: true), ".json");
        Policy one = PolicyFile.Load(single.Path);
        Policy two = PolicyFile.Load(several.Path);

        Action unnamed = () => PolicyFile.SelectRule(two, null);
        Action unknown = () => PolicyFile.SelectRule(two, "missing");

        PolicyFile.SelectRule(one, null).Id.Should().Be("prompt-injection");
        PolicyFile.SelectRule(two, "route").Id.Should().Be("route");
        unnamed.Should().Throw<EvalsException>().Which.Message.Should().Contain("prompt-injection").And.Contain("route");
        unknown.Should().Throw<EvalsException>().Which.Message.Should().Contain("missing").And.Contain("route");
    }

    private const string _route = """
        ,
            { "type": "choice", "id": "route", "question": "question-c",
              "options": [ { "key": "answer", "description": "description-a", "verdict": "allow" },
                           { "key": "refuse", "description": "description-b", "verdict": "deny" } ] }
        """;

    private static string Document(string ladder = """[ "warn", "deny" ]""", bool withRoute = false) => $$"""
        {
          "id": "guard",
          "mode": "shadow",
          "rules": [
            { "id": "prompt-injection", "question": "question-b", "flaggedAnswer": true, "ladder": {{ladder}}, "type": "boolean" }{{(withRoute ? _route : "")}}
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
                }
              ]
            }
          ],
          "onFailure": { "action": "fallback", "then": "deny" }
        }
        """;
}
