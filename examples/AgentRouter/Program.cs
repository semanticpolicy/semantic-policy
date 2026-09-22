// Demo D - routing: a Choice policy picks which specialist answers, with no guard adapter anywhere in
// this program. The other three demos judge content; this one shows the same runtime making an ordinary
// product decision, which is the point - it is not a security library.
// A probabilistic check is one layer of defence in depth, never a security boundary on its own; a
// verdict here decides nothing more than which agent gets the request.
using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;
using SemanticPolicy;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

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

Policy router = Policy.Define("agent-router")
    .Enforce()
    .Rule(Policy.Rule("route")
        .Choice("Which specialist should answer this request?")
        .Option("coding", "Writing, reading, debugging or reviewing software.", Verdict.Allow)
        .Option("research", "Finding, comparing or summarising information about something.", Verdict.Allow)
        .Option("general", "Everyday requests that fit none of the other routes.", Verdict.Allow)
        .Option("finance", "Money: invoices, budgets, pricing, tax.", Verdict.Allow)
        .Build())
    // A Choice binding carries no thresholds: the option the provider picks is the answer, and the rule
    // maps each option to a verdict. Every option here is Allow, because a route is not a judgement
    // about the request - the policy says who answers, not whether anyone should.
    .Using("jev", _ => { })
    .OnFailure(FailureBehavior.Allow)
    .Budget(TimeSpan.FromSeconds(5))
    .Build();

ServiceCollection services = new();
services.AddSemanticPolicy()
    // The decision provider is registered here; none is yet, so the first resolve of IPolicyEvaluator
    // checks the bindings, finds nothing named "jev", and says so before an agent runs.
    .AddPolicy(router);

using ServiceProvider container = services.BuildServiceProvider();
IPolicyEvaluator evaluator;
try
{
    evaluator = container.GetRequiredService<IPolicyEvaluator>();
}
catch (PolicyConfigurationException error)
{
    Console.Error.WriteLine($"provider not configured: {error.Message}");
    return 2;
}

// Four agents over one chat client, differing only in what they are told to be.
Dictionary<string, AIAgent> specialists = new(StringComparer.Ordinal)
{
    ["coding"] = chat.AsAIAgent(
        instructions: "You are a software engineer. Answer with code and with the reasoning behind it.",
        name: "coding-specialist"),
    ["research"] = chat.AsAIAgent(
        instructions: "You summarise and compare information. Say plainly when you do not know something.",
        name: "research-specialist"),
    ["general"] = chat.AsAIAgent(
        instructions: "You are a helpful assistant. Answer briefly and ask when the request is unclear.",
        name: "general-assistant"),
    ["finance"] = chat.AsAIAgent(
        instructions: "You answer questions about invoices, budgets, pricing and tax in general terms.",
        name: "finance-specialist"),
};

string[] requests =
[
    "Why does this method throw a null reference when the list comes back empty?",
    "Summarise what changed in project X between version 1.4 and version 2.0.",
    "Draft a short note to the team about Friday's release.",
    "What is the VAT on a EUR 1,200 invoice to a client in Ireland?",
    "Can you take a look at the numbers for project X?",
];

foreach (string request in requests)
{
    Console.WriteLine();
    Console.WriteLine($"--- user: {request}");

    PolicyVerdict verdict = await evaluator.EvaluateAsync("agent-router", SemanticContext.FromText(request));
    string? chosen = ChosenRoute(verdict);
    Report(verdict, chosen);

    string route = chosen ?? "general";
    AgentResponse response = await specialists[route].RunAsync(request);
    Console.WriteLine($"{route}: {response.Text}");
}

return 0;

// Core does not yet expose a Choice rule's chosen option on the verdict, so the router reads it out of
// the attempt that decided the rule. Null when nothing decided - a provider failure, say - and the
// application routes that to the general agent rather than dropping the request.
static string? ChosenRoute(PolicyVerdict verdict) =>
    (Deciding(verdict.Rules[0])?.Result.Value as ChoiceValue)?.Option;

static Attempt? Deciding(RuleVerdict rule) =>
    rule.DecidingBinding is { } index
        ? rule.Attempts.FirstOrDefault(attempt => attempt.BindingIndex == index)
        : null;

static void Report(PolicyVerdict verdict, string? chosen)
{
    RuleVerdict rule = verdict.Rules[0];
    Console.WriteLine(
        $"policy: {verdict.PolicyId}  mode: {verdict.Mode}  "
        + $"evaluated: {verdict.Evaluated}  effective: {verdict.Effective}");
    Console.WriteLine($"  route: {chosen ?? "none, so the request falls back to general"}  source: {rule.Source}");

    Attempt? deciding = Deciding(rule);
    if (deciding is null)
    {
        Console.WriteLine("    no attempt decided it");
        return;
    }

    Console.WriteLine($"    decided by {deciding.ProviderId} in {deciding.Result.Provider.LatencyMs:F0} ms");
    foreach (Evidence evidence in deciding.Result.Evidence)
    {
        // Keyed by option, and the provider's own numbers on its own scale: nothing here is calibrated,
        // and a route with the highest number is not a route the provider is confident about.
        string perOption = string.Join("  ", evidence.Values.Select(pair => $"{pair.Key} {pair.Value:F2}"));
        Console.WriteLine($"    {evidence.Kind} per option: {perOption}");
    }
}
