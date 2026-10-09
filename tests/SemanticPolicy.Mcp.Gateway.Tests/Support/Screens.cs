using System.Text.Json;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// The screening points a test serves, over the scripted provider: one Boolean rule read on probability, Warn from 0.5
// and Deny from 0.9, and a failure that falls back to Escalate. Each verdict's action is the operator's: the messages are
// placeholders a test can find in what the host receives.
internal static class Screens
{
    public const string ResultsPolicy = "results-guard";
    public const string DefinitionsPolicy = "definitions-guard";

    public const string AnnotateMessage = "message-annotate";
    public const string WithholdMessage = "message-withhold";
    public const string EscalateMessage = "message-escalate";
    public const string HideMessage = "message-hide";

    public static MappedAction Pass { get; } = new(GatewayAction.Pass, null);

    public static MappedAction Annotate { get; } = new(GatewayAction.Annotate, AnnotateMessage);

    public static MappedAction Withhold { get; } = new(GatewayAction.Withhold, WithholdMessage);

    public static MappedAction Hide { get; } = new(GatewayAction.Hide, HideMessage);

    public static Policy Policy(string id, PolicyMode mode = PolicyMode.Enforce, TimeSpan? budget = null, string question = "question-a")
    {
        string json = Samples.Policy(id, question, Samples.Binding(ScriptedDecisionProvider.Name, "probability"));
        return JsonSerializer.Deserialize<Policy>(json, SemanticPolicyJson.Options)! with { Mode = mode, Budget = budget };
    }

    // Warn takes the action given, Escalate withholds, Deny withholds, and Abstain passes.
    public static GatewayPoint Results(MappedAction? warn = null, Policy? policy = null) => new(
        "results",
        policy ?? Policy(ResultsPolicy),
        new VerdictMapping(warn ?? Annotate, new MappedAction(GatewayAction.Withhold, EscalateMessage), Withhold, Pass));

    // Warn passes, and every other verdict but Abstain hides.
    public static GatewayPoint Definitions(Policy? policy = null) => new(
        "definitions",
        policy ?? Policy(DefinitionsPolicy),
        new VerdictMapping(Pass, Hide, Hide, Pass));

    public static GatewayComposition Composition(IDecisionProvider provider, GatewayPoint? results = null, GatewayPoint? definitions = null) => new(
        new PolicyEvaluator(
            [new ProviderRegistration(ScriptedDecisionProvider.Name, provider)],
            new[] { results, definitions }.OfType<GatewayPoint>().Select(point => point.Policy)),
        results,
        definitions,
        []);

    // What the gateway serves when the gateway file screens nothing.
    public static GatewayComposition None { get; } = new(new PolicyEvaluator([], []), null, null, []);
}
