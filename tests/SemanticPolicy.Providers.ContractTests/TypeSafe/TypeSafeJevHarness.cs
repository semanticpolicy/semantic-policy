using System.Net;
using System.Text.Json;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.TypeSafe;

namespace SemanticPolicy.Providers.ContractTests.TypeSafe;

/// <summary>
/// The Jev adapter on a scripted transport. The timeout is short so the suite's own timeout case
/// returns in well under a second and a 50 ms caller cancellation fires before the adapter's timer;
/// tests that need another timeout configure their own.
/// </summary>
public sealed class TypeSafeJevHarness : ProviderHarness
{
    /// <summary>A key that authenticates nowhere: the transport is a double, so it is never sent.</summary>
    public const string ApiKey = "test-key-not-a-credential";

    public static readonly IReadOnlyDictionary<string, string> Options = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["allow"] = "safe to proceed as-is",
        ["review"] = "a person must look before it proceeds",
        ["block"] = "must not proceed",
    };

    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high"];

    public TypeSafeJevHarness(Action<TypeSafeJevOptions>? configure = null)
    {
        Handler = new ScriptedHttpMessageHandler();
        Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        JevOptions = new TypeSafeJevOptions
        {
            Route = TypeSafeJevRoute.TypeSafe,
            ApiKey = ApiKey,
            Timeout = TimeSpan.FromMilliseconds(250),
        };
        configure?.Invoke(JevOptions);
        Provider = new TypeSafeJevProvider(Client, JevOptions);
    }

    internal ScriptedHttpMessageHandler Handler { get; }

    public HttpClient Client { get; }

    /// <summary>The options the provider was built from; mutate them to prove they were snapshotted.</summary>
    public TypeSafeJevOptions JevOptions { get; }

    public override IDecisionProvider Provider { get; }

    public override int RequestCount => Handler.RequestCount;

    internal RecordedRequest LastRequest => Handler.LastRequest;

    /// <summary>The last request's body, parsed.</summary>
    public JsonElement LastBody =>
        JsonSerializer.Deserialize<JsonElement>(
            LastRequest.Body ?? throw new InvalidOperationException("The last request had no body."));

    /// <summary>A three-option distribution over <see cref="Options"/>.</summary>
    public static Dictionary<string, double> Distribution(double allow, double review, double block) =>
        new(StringComparer.Ordinal) { ["allow"] = allow, ["review"] = review, ["block"] = block };

    public override DecisionRequest CreateRequest(DecisionType type, string marker)
    {
        string question = $"Does the text mention {marker}?";
        JsonElement context = JsonSerializer.SerializeToElement(
            new { text = $"A synthetic context that mentions {marker}." });
        return type switch
        {
            DecisionType.Boolean => new DecisionRequest(
                type,
                question,
                context,
                new BooleanCriteria($"it mentions {marker}", "it does not")),
            DecisionType.Choice => new DecisionRequest(type, question, context, Options: Options),
            DecisionType.Score => new DecisionRequest(type, question, context, Levels: Levels),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    public override void ScriptSuccess(DecisionType type) =>
        Handler.Respond(
            HttpStatusCode.OK,
            type switch
            {
                DecisionType.Boolean => JevFixtures.BooleanAnswer(0.91),
                DecisionType.Choice => JevFixtures.ChoiceAnswer("review", Distribution(0.15, 0.7, 0.15), confidence: 0.7),
                DecisionType.Score => JevFixtures.ScoreAnswer(Levels, [0.1, 0.6, 0.3], score: 1.2, confidence: 0.6),
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            });

    public override void ScriptFailure(FailureKind kind)
    {
        switch (kind)
        {
            case FailureKind.Timeout:
                Handler.Hang();
                break;
            case FailureKind.Unavailable:
                Handler.Respond(HttpStatusCode.ServiceUnavailable, JevFixtures.Html(Marker), "text/html");
                break;
            case FailureKind.Malformed:
                Handler.Respond(HttpStatusCode.OK, JevFixtures.Response(JevFixtures.Answer("noul", ("explanation", Marker))));
                break;
            case FailureKind.RejectedInput:
                Handler.Respond(HttpStatusCode.BadRequest, JevFixtures.OpenRouterError(400, $"invalid state: {Marker}"));
                break;
            case FailureKind.Unauthorized:
                Handler.Respond(
                    HttpStatusCode.Unauthorized,
                    JevFixtures.TypeSafeError("authentication_error", $"no key for {Marker}"));
                break;
            case FailureKind.Unknown:
                Handler.Respond(HttpStatusCode.Found, JevFixtures.Html(Marker), "text/html");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    public override void ScriptHang() => Handler.Hang();
}
