// Demo B - the pre-tool point: a policy reads the call the model proposed, before the tool runs.
// A probabilistic check is one layer of defence in depth, never a security boundary on its own;
// a verdict here is a signal this program acts on, and neither a Deny nor an Allow proves anything
// about the call it judged.
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Providers.TypeSafe;
using ToolIntentGuard;

const string Instructions =
    "You look after a source repository for the user. Use the tools you are given to answer.";

Policy shadow = Policy.Define("tool-intent")
    .Shadow()
    .Rule(Policy.Rule("intent")
        .Boolean("Is this tool call consistent with what the user asked for?")
        .WhenFalse(Verdict.Escalate, Verdict.Deny))
    // Illustrative numbers, not measured ones: an operating point belongs to one policy on one provider
    // on one dataset, and tools/SemanticPolicy.Evals is what measures it. The margin gate is what makes
    // Abstain reachable - an answer too close to call moves on instead of crossing a rung.
    .Using("jev", binding => binding
        .EscalateAboveProbability(0.60)
        .DenyAboveProbability(0.90)
        .WhenProbabilityMarginBelow(0.10))
    .OnFailure(FailureBehavior.Deny)
    .Budget(TimeSpan.FromSeconds(5))
    .Build();

Policy enforce = shadow with { Id = "tool-intent-enforce", Mode = PolicyMode.Enforce };

ServiceCollection services = new();
services.AddSemanticPolicy()
    // Jev through OpenRouter's gateway; the decision model is the route's pin. This demo's chat client is
    // scripted and needs no key, so the route is the only reader of OPENROUTER_API_KEY here: it reads it
    // when the evaluator is first resolved, and a missing one is the configuration error caught below.
    .AddTypeSafeJev("jev", o => o.Route = TypeSafeJevRoute.OpenRouter)
    .AddPolicy(shadow)
    .AddPolicy(enforce);

using ServiceProvider container = services.BuildServiceProvider();
try
{
    _ = container.GetRequiredService<IPolicyEvaluator>();
}
catch (PolicyConfigurationException error)
{
    Console.Error.WriteLine($"configuration error: {error.Message}");
    return 2;
}

Console.WriteLine(
    $"chat model: scripted, no model   decision model: {TypeSafeJevRoute.OpenRouter.Model} through OpenRouter");

// The only tool is a stub that says what it would have done and returns a fixed string. Nothing here
// touches a repository, a disk, a shell or the network.
AIFunction deleteBranch = AIFunctionFactory.Create(
    (string name) =>
    {
        Console.WriteLine($"    tool delete_branch: would have deleted branch {name}.");
        return $"Branch {name} deleted.";
    },
    "delete_branch",
    "Deletes one branch by name.");

List<AITool> tools = [deleteBranch];

// Three scenarios, one tool. The scripted model makes the mistakes a real one can make: it reaches for the
// only branch tool it has when the user asked for something that tool does not do, or it names the wrong
// branch. An allow-list of tools passes all three calls; only a check that reads the request and the
// arguments together can tell them apart.
(string Request, string Branch)[] scenarios =
[
    ("Delete branch test-old.", "test-old"),
    ("Archive branch test-old.", "test-old"),
    ("Delete branch test-old.", "main"),
];

// Shadow first, then Enforce: the same policy, the same handler, the same three scenarios. What changes is
// Effective, and with it what the application's own code does about the verdict it already saw.
Policy[] modes = [shadow, enforce];
foreach (Policy policy in modes)
{
    Console.WriteLine();
    Console.WriteLine($"=== {policy.Id} ({policy.Mode})");

    foreach ((string request, string branch) in scenarios)
    {
        Console.WriteLine();
        Console.WriteLine($"--- user: {request}");
        Console.WriteLine($"scripted model proposes: delete_branch(name: \"{branch}\")");

        ScriptedChatClient model = new("delete_branch", new Dictionary<string, object?> { ["name"] = branch });
        AIAgent guarded = new AIAgentBuilder(
                new ChatClientAgent(model, Instructions, "repository-assistant", description: null, tools: tools))
            .UseSemanticPolicyBeforeTool(policy.Id, OnToolCall)
            .Build(container);

        AgentResponse response = await guarded.RunAsync(request);
        Console.WriteLine($"agent: {response.Text}");
    }
}

return 0;

static ValueTask<PreToolOutcome> OnToolCall(ToolCall call, PolicyVerdict verdict, CancellationToken cancellationToken)
{
    Report(verdict);
    switch (verdict.Effective)
    {
        case Verdict.Deny:
            Console.WriteLine("application: refused the call, and the model is free to try something else.");
            return ValueTask.FromResult(PreToolOutcome.Refuse(
                $"The {verdict.PolicyId} policy did not read that call as part of the request, so it was not run."));
        case Verdict.Escalate:
            Console.WriteLine(
                "application: refused the call and would ask a person - this demo prints the note instead of asking.");
            return ValueTask.FromResult(PreToolOutcome.Refuse(
                "That call was not run; someone has to confirm it first."));
        case Verdict.Warn:
            Console.WriteLine("application: ran the tool, with the warning recorded beside the result.");
            return ValueTask.FromResult(PreToolOutcome.Proceed);
        case Verdict.Abstain:
            Console.WriteLine("application: refused the call - a call the policy could not judge is not one it runs.");
            return ValueTask.FromResult(PreToolOutcome.Refuse(
                "That call was not run: the policy could not judge it."));
        case Verdict.Allow:
        default:
            Console.WriteLine("application: ran the tool.");
            return ValueTask.FromResult(PreToolOutcome.Proceed);
    }
}

// What the policy concluded and what it read to get there. Evaluated is what the rule decided in either
// mode; Effective is what the mode makes binding, and the handler above acts on that one.
static void Report(PolicyVerdict verdict)
{
    Console.WriteLine(
        $"policy: {verdict.PolicyId}  mode: {verdict.Mode}  "
        + $"evaluated: {verdict.Evaluated}  effective: {verdict.Effective}");
    foreach (RuleVerdict rule in verdict.Rules)
    {
        string evidence = rule.EvidenceKind is { } kind && rule.EvidenceValue is { } value
            ? $"{kind} {value:F2}"
            : "none";
        Console.WriteLine($"  rule {rule.RuleId}: {rule.Verdict}  evidence: {evidence}  source: {rule.Source}");
        Attempt? deciding = rule.DecidingBinding is { } index
            ? rule.Attempts.FirstOrDefault(attempt => attempt.BindingIndex == index)
            : null;
        Console.WriteLine(deciding is null
            ? "    no attempt decided it"
            : $"    decided by {deciding.ProviderId} in {deciding.Result.Provider.LatencyMs:F0} ms");
    }
}
