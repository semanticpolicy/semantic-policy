namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// The files a gateway starts from, written as an operator would write them. Every text is a placeholder: no real
// content, key or endpoint.
internal static class Samples
{
    // Planted in the value a case breaks; no message may carry it.
    public const string Canary = "CANARY-VALUE-3e9d";

    // A System One server on loopback. Building the adapter opens no connection, so a composition can build it.
    public const string Von = """{ "kind": "systemone", "options": { "baseUrl": "http://127.0.0.1:8000", "model": "von-1.2.2" } }""";

    // A protocol v0 server on loopback that declares what it answers, so a policy can be checked against it.
    public const string V0 = """{ "kind": "http", "options": { "baseUrl": "http://127.0.0.1:8765", "model": "stub-decider-1", "types": ["boolean"], "evidence": ["score"], "structuredContext": false } }""";

    public const string ResultsPolicyPath = "\"policy\": \"results.policy.json\"";
    public const string ResultsWarn = "\"warn\": { \"action\": \"annotate\", \"message\": \"message-a\" }";
    public const string ResultsEscalate = "\"escalate\": { \"action\": \"withhold\", \"message\": \"message-b\" }";
    public const string ResultsDeny = "\"deny\": { \"action\": \"withhold\", \"message\": \"message-c\" }";
    public const string ResultsAbstain = "\"abstain\": { \"action\": \"pass\" }";

    public const string DefinitionsPolicyPath = "\"policy\": \"definitions.policy.json\"";
    public const string DefinitionsWarn = "\"warn\": { \"action\": \"pass\" }";
    public const string DefinitionsEscalate = "\"escalate\": { \"action\": \"hide\", \"message\": \"message-d\" }";
    public const string DefinitionsDeny = "\"deny\": { \"action\": \"hide\", \"message\": \"message-e\" }";
    public const string DefinitionsAbstain = "\"abstain\": { \"action\": \"pass\" }";

    public const string ProvidersPath = "\"providers\": \"providers.json\"";

    public static string Results { get; } = Object(ResultsPolicyPath, ResultsWarn, ResultsEscalate, ResultsDeny, ResultsAbstain);

    public static string Definitions { get; } = Object(DefinitionsPolicyPath, DefinitionsWarn, DefinitionsEscalate, DefinitionsDeny, DefinitionsAbstain);

    public static string Object(params string[] members) => "{ " + string.Join(", ", members) + " }";

    public static string Gateway(string results, string definitions) =>
        Object(ProvidersPath, "\"results\": " + results, "\"definitions\": " + definitions);

    public static string ProvidersFile(string entries) => $$"""{ "providers": {{entries}} }""";

    // OpenRouter's route spelled out, keyed by the variable given.
    public static string JevRoute(string keyVariable, string model = "typesafe/jev-1.13") =>
        $$"""{ "baseUrl": "https://openrouter.ai/api", "path": "/v1/systemone", "model": "{{model}}", "apiKeyVariable": "{{keyVariable}}" }""";

    // Variables are process-wide and test classes run in parallel, so a test names one nobody sets.
    public static string UnsetVariable() => $"SEMANTICPOLICY_TEST_{Guid.NewGuid():N}";

    // One Boolean rule in Core's JSON, bound to von unless the bindings are given.
    public static string Policy(string id, string question = "question-a", string? bindings = null) =>
        $$"""
        {
          "id": "{{id}}",
          "mode": "shadow",
          "rules": [
            { "type": "boolean", "id": "flagged", "question": "{{question}}", "flaggedAnswer": true, "ladder": [ "warn", "deny" ] }
          ],
          "bindings": [{{bindings ?? Binding("von")}}],
          "onFailure": { "action": "fallback", "then": "escalate" }
        }
        """;

    public static string Binding(string provider, string evidence = "score") =>
        $$"""
        {
          "providerId": "{{provider}}",
          "operatingPoints": [
            {
              "ruleId": "flagged",
              "thresholds": [
                { "verdict": "warn", "kind": "{{evidence}}", "atOrAbove": 0.5 },
                { "verdict": "deny", "kind": "{{evidence}}", "atOrAbove": 0.9 }
              ]
            }
          ]
        }
        """;
}
