// Demo D - routing: a Choice policy picks which support team's agent answers a customer, with no guard
// adapter anywhere in this program. The other three demos judge content; this one uses the same library
// for an ordinary product decision.
// A verdict is a probabilistic signal, never a security boundary; here it only picks the agent.
using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;
using SemanticPolicy;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.TypeSafe;

string? key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("OPENROUTER_API_KEY is not set; the chat model and the decision model both need it.");
    return 2;
}

string? configuredModel = Environment.GetEnvironmentVariable("OPENROUTER_MODEL");
string model = string.IsNullOrWhiteSpace(configuredModel) ? "openai/gpt-4.1-mini" : configuredModel;

ChatClient chat = new OpenAIClient(
        new ApiKeyCredential(key),
        new OpenAIClientOptions { Endpoint = new Uri("https://openrouter.ai/api/v1") })
    .GetChatClient(model);

// Illustrative, not measured: when the two likeliest teams are closer than this, the rule abstains instead
// of picking one.
const double MarginGate = 0.20;

Policy router = Policy.Define("agent-router")
    .Enforce()
    .Rule(Policy.Rule("route")
        .Choice("Which team should handle this support request?")
        .Option("billing", "Invoices, charges, refunds, payment methods and tax.", Verdict.Allow)
        .Option("technical", "Builds, errors, integrations and anything that doesn't work as it should.", Verdict.Allow)
        .Option("account", "Signing in, passwords, two-factor authentication, members and permissions.", Verdict.Allow)
        .Option("sales", "Plans, prices, discounts and trials for teams choosing or changing a plan.", Verdict.Allow)
        .Build())
    // Every option is Allow, because a route is not a judgement about the request - the policy says who
    // answers, not whether anyone should. A Choice binding carries no thresholds; the gate is all it has.
    .Using("jev", binding => binding.WhenProbabilityMarginBelow(MarginGate))
    .OnFailure(FailureBehavior.Allow)
    .Budget(TimeSpan.FromSeconds(5))
    .Build();

ServiceCollection services = new();
services.AddSemanticPolicy()
    // Jev through OpenRouter's gateway. The route reads OPENROUTER_API_KEY itself when the evaluator is
    // first resolved, so the key checked above is never handed to it; the decision model is the route's pin.
    .AddTypeSafeJev("jev", o => o.Route = TypeSafeJevRoute.OpenRouter)
    .AddPolicy(router);

using ServiceProvider container = services.BuildServiceProvider();
IPolicyEvaluator evaluator;
try
{
    evaluator = container.GetRequiredService<IPolicyEvaluator>();
}
catch (PolicyConfigurationException error)
{
    Console.Error.WriteLine($"configuration error: {error.Message}");
    return 2;
}

Console.WriteLine(
    $"chat model: {model}   decision model: {TypeSafeJevRoute.OpenRouter.Model}   both through OpenRouter");

// Brindle, the hosted build service these customers write to, is made up. Each team's agent is told the
// few facts that team would know, so a request that reaches the right team is answered from them, and the
// same request at another team's desk would not be.
const string Reply = " Reply to the customer in at most two sentences, using only these facts.";
Dictionary<string, AIAgent> teams = new(StringComparer.Ordinal)
{
    ["billing"] = chat.AsAIAgent(
        instructions: "You answer for Brindle's billing team. A duplicate charge is refunded within five working "
            + "days of the team confirming it. When a card payment fails, deploys pause until the open invoice is "
            + "paid, and they resume within minutes of payment." + Reply,
        name: "billing-agent"),
    ["technical"] = chat.AsAIAgent(
        instructions: "You answer for Brindle's technical support team. Since 06:00 UTC today, build runners fail "
            + "to pull images with the error 'runner image not found'; a fix is rolling out, and "
            + "status.brindle.example has updates." + Reply,
        name: "technical-agent"),
    ["account"] = chat.AsAIAgent(
        instructions: "You answer for Brindle's account team. A lost two-factor device is replaced by signing in "
            + "with a recovery code; without one, the team confirms the owner's identity by email before it "
            + "resets two-factor." + Reply,
        name: "account-agent"),
    ["sales"] = chat.AsAIAgent(
        instructions: "You answer for Brindle's sales team. Paying yearly takes 20% off any plan, and teams of 25 "
            + "or more can ask for a quote with a volume discount." + Reply,
        name: "sales-agent"),
};

// Four requests that name their team plainly; one whose words point at the technical team although only
// billing can help; and one that sits between two teams.
string[] requests =
[
    "I was charged twice for September. Can you refund one of the charges?",
    "Since this morning every build fails with 'runner image not found'.",
    "I changed phones and can't get past the two-factor prompt anymore.",
    "We are a team of 40. Is there a discount if we pay for a year up front?",
    "Our deploys stopped after we switched the card on file.",
    "The invoices page shows an error when I try to download last month's invoice.",
];

foreach (string request in requests)
{
    Console.WriteLine();
    Console.WriteLine($"--- customer: {request}");

    PolicyVerdict verdict = await evaluator.EvaluateAsync("agent-router", SemanticContext.FromText(request));
    string? team = ChosenRoute(verdict);
    Report(verdict, team);

    if (team is null)
    {
        // Nothing decided the rule - the two likeliest teams were too close to call, or the decision model gave
        // no answer - and the application does not guess: a person reads the request and picks the team.
        Console.WriteLine("application: put the request in front of a person to pick the team; no agent answers it.");
        continue;
    }

    AgentResponse response = await teams[team].RunAsync(request);
    Console.WriteLine($"{team}: {response.Text}");
}

return 0;

// Core does not yet expose a Choice rule's chosen option on the verdict, so the router reads it out of
// the attempt that decided the rule. Null when nothing decided - the gate abstained, or the provider
// failed - and the application hands that request to a person rather than guessing.
static string? ChosenRoute(PolicyVerdict verdict) =>
    (Deciding(verdict.Rules[0])?.Result.Value as ChoiceValue)?.Option;

static Attempt? Deciding(RuleVerdict rule) =>
    rule.DecidingBinding is { } index
        ? rule.Attempts.FirstOrDefault(attempt => attempt.BindingIndex == index)
        : null;

static void Report(PolicyVerdict verdict, string? team)
{
    RuleVerdict rule = verdict.Rules[0];
    Console.WriteLine(
        $"policy: {verdict.PolicyId}  mode: {verdict.Mode}  "
        + $"evaluated: {verdict.Evaluated}  effective: {verdict.Effective}");
    Console.WriteLine($"  route: {team ?? "none"}  source: {rule.Source}");

    // The last attempt is the one that ended the rule: it decided, its margin fell under the gate, or it failed.
    Attempt? last = rule.Attempts.LastOrDefault();
    if (last is null)
    {
        Console.WriteLine("    no attempt was made");
        return;
    }

    string latency = $"{last.Result.Provider.LatencyMs:F0} ms";
    string margin = last.Margin is { } value ? $"margin {value:F2}" : "no margin";
    ProviderOutcome outcome = last.EffectiveOutcome;
    Console.WriteLine(last.Disposition switch
    {
        AttemptDisposition.Decided => $"    decided by {last.ProviderId} in {latency}, {margin}",
        AttemptDisposition.ExhaustedByGate =>
            $"    {last.ProviderId} answered in {latency}, but its {margin} is under the gate, so the rule abstained",
        _ => $"    {last.ProviderId} gave no answer: {outcome.Kind?.ToString() ?? outcome.Status.ToString()}",
    });
    foreach (Evidence evidence in last.Result.Evidence)
    {
        // Keyed by option and printed highest first, the provider's own numbers on its own scale: nothing
        // here is calibrated, and the top number is the provider's pick, not a measure of how often it is right.
        string perOption = string.Join(
            "  ",
            evidence.Values.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value:F2}"));
        Console.WriteLine($"    {evidence.Kind} per option: {perOption}");
    }
}
