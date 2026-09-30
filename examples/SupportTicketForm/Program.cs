// Demo E - validation: a support-ticket form outside any agent. A minimal API's FluentValidation validator
// runs its cheap rules first and two semantic rules after them, one on the description alone and one on
// the description against the category the customer chose. Running it starts the app on a loopback port,
// posts a fixed scenario of made-up tickets to its own endpoint and prints each answer.
// A verdict is a probabilistic signal, never a security boundary; here it only decides whether a ticket is
// filed, put in front of a person first, or sent back to the customer.
using System.Net;
using FluentValidation;
using FluentValidation.Results;
using SemanticPolicy;
using SemanticPolicy.Providers.TypeSafe;
using SupportTicketForm;

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
{
    Console.Error.WriteLine("OPENROUTER_API_KEY is not set; the decision model behind both semantic rules needs it.");
    return 2;
}

// Both policies run in Enforce, so that the form shows what a verdict does to a ticket. A production rollout
// starts in Shadow instead, where the validation never fails, and reads each verdict's Evaluated in telemetry
// until an evaluation on its own tickets justifies Enforce.
// Every threshold and budget below is illustrative, not measured: choose yours from the precision and recall
// you measure on your own data.
Policy descriptionPolicy = Policy.Define("ticket-description")
    .Enforce()
    .Rule(Policy.Rule("ticket-description")
        .Boolean("Does the description say what the customer needs?")
        .WhenFalse(Verdict.Deny))
    .Using("jev", binding => binding.DenyAboveProbability(0.8))
    // An outage must not reject a customer, so a provider that gives no answer lets the ticket through as if
    // the rule had passed.
    .OnFailure(FailureBehavior.Allow)
    .Budget(TimeSpan.FromSeconds(5))
    .Build();

// The categories and their descriptions are the AgentRouter example's, word for word: the same Core, with no
// agent. tools/SemanticPolicy.Evals/datasets/examples/support-ticket.policy.json holds this rule for the
// evaluation tool, so a change to the question belongs in both.
Policy categoryPolicy = Policy.Define("ticket-category")
    .Enforce()
    .Rule(Policy.Rule("ticket-category")
        .Boolean(
            "Does the description fit the category the customer chose? "
            + "billing: Invoices, charges, refunds, payment methods and tax. "
            + "technical: Builds, errors, integrations and anything that doesn't work as it should. "
            + "account: Signing in, passwords, two-factor authentication, members and permissions. "
            + "sales: Plans, prices, discounts and trials for teams choosing or changing a plan.")
        .WhenFalse(Verdict.Escalate))
    .Using("jev", binding => binding.EscalateAboveProbability(0.7))
    // Here an outage sends the ticket to a person, who checks the category, rather than back to the customer.
    .OnFailure(FailureBehavior.Escalate)
    .Budget(TimeSpan.FromSeconds(5))
    .Build();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Port 0 lets the system pick a free loopback port, so the program never collides with whatever else is
// listening; the address it actually bound is read back once the app has started.
builder.WebHost.UseUrls("http://127.0.0.1:0");

// The framework's startup and per-request lines would interleave with the scenario's.
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSemanticPolicy()
    // Jev through OpenRouter's gateway. The route reads OPENROUTER_API_KEY itself when the evaluator is first
    // resolved, so the key checked above is never handed to it; the decision model is the route's pin.
    .AddTypeSafeJev("jev", o => o.Route = TypeSafeJevRoute.OpenRouter)
    .AddPolicy(descriptionPolicy)
    .AddPolicy(categoryPolicy);

// By hand: FluentValidation's automatic validation for ASP.NET Core cannot run an asynchronous rule, and the
// endpoint below calls ValidateAsync itself.
builder.Services.AddSingleton<IValidator<SupportTicket>, SupportTicketValidator>();

await using WebApplication app = builder.Build();

app.MapPost("/tickets", async (SupportTicket ticket, IValidator<SupportTicket> validator, CancellationToken cancellationToken) =>
{
    ValidationResult result = await validator.ValidateAsync(ticket, cancellationToken);
    if (result.IsValid)
    {
        return Results.Created();
    }

    // Every severity fails IsValid, so the endpoint reads it: an Error sends the ticket back to the customer,
    // while a Warning, which a semantic rule reports for an Escalate, is a question for a person to answer and
    // no reason to reject the customer.
    return result.Errors.Any(failure => failure.Severity == Severity.Error)
        ? Results.ValidationProblem(result.ToDictionary())
        : Results.Accepted(value: result.ToDictionary());
});

// Resolving the validator resolves the evaluator, which checks every registered policy against its provider,
// so a configuration mistake stops the program here rather than on the first ticket.
try
{
    _ = app.Services.GetRequiredService<IValidator<SupportTicket>>();
}
catch (PolicyConfigurationException error)
{
    Console.Error.WriteLine($"configuration error: {error.Message}");
    return 2;
}

await app.StartAsync();
using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()) };

Console.WriteLine($"decision model: {TypeSafeJevRoute.OpenRouter.Model} through OpenRouter");
Console.WriteLine(
    "A semantic rule's verdict is a probabilistic signal about the text, not a security boundary: a flagged "
    + "ticket is a model's reading, and what happens to it is the application's decision.");

// Made-up tickets, each written for one outcome. A verdict can still send one elsewhere, because it is a
// model's reading of the text; the program reports where each one went.
(string WrittenFor, SupportTicket Ticket)[] scenario =
[
    ("filed", new("billing", "I was charged twice for September. Please refund one of the two charges.")),
    ("put in front of a person", new("sales", "Since this morning every build fails with the error 'runner image not found'. Please fix it.")),
    ("sent back by a semantic rule", new("technical", "Please see the subject line. Thanks in advance for your help.")),
    ("sent back by a cheap rule, with no model asked", new("urgent", "Our deploys stopped after we switched the card on file.")),
];

foreach ((string writtenFor, SupportTicket ticket) in scenario)
{
    Console.WriteLine();
    Console.WriteLine($"--- ticket, written to be {writtenFor}");
    Console.WriteLine($"    category: {ticket.Category}");
    Console.WriteLine($"    description: {ticket.Description}");

    using HttpResponseMessage response = await client.PostAsJsonAsync("/tickets", ticket);
    await ReportAsync(response);
}

await app.StopAsync();
return 0;

static async Task ReportAsync(HttpResponseMessage response)
{
    int status = (int)response.StatusCode;
    switch (response.StatusCode)
    {
        case HttpStatusCode.Created:
            Console.WriteLine($"{status} Created: the ticket is filed.");
            break;

        case HttpStatusCode.Accepted:
            Console.WriteLine($"{status} Accepted: a rule flagged the ticket, so a person checks it before it is filed.");
            PrintMessages(await response.Content.ReadFromJsonAsync<Dictionary<string, string[]>>());
            break;

        case HttpStatusCode.BadRequest:
            Console.WriteLine($"{status} Bad Request: the ticket goes back to the customer to change.");
            PrintMessages((await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>())?.Errors);
            break;

        default:
            Console.WriteLine($"{status} {response.StatusCode}: not an answer this endpoint gives.");
            break;
    }
}

static void PrintMessages(IDictionary<string, string[]>? failures)
{
    foreach (string message in failures?.Values.SelectMany(messages => messages) ?? [])
    {
        Console.WriteLine($"    {message}");
    }
}
