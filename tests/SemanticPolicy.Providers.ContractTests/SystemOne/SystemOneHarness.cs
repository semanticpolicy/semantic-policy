using System.Net;
using System.Text.Json;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.SystemOne;

namespace SemanticPolicy.Providers.ContractTests.SystemOne;

/// <summary>
/// The System One client on a scripted transport, as a user would first register it: <c>Score</c>
/// evidence, plain <c>http</c> on loopback and no key. The timeout is short so the suite's own timeout
/// case returns in well under a second.
/// </summary>
public sealed class SystemOneHarness : ProviderHarness
{
    /// <summary>A model name no server answers to: the transport is a double, so it is never sent.</summary>
    public const string Model = "systemone-test-model";

    public static readonly Uri BaseUrl = new("http://127.0.0.1:8000");

    public static readonly IReadOnlyDictionary<string, string> Options = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["allow"] = "safe to proceed as-is",
        ["review"] = "a person must look before it proceeds",
        ["block"] = "must not proceed",
    };

    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high"];

    public SystemOneHarness(Action<SystemOneOptions>? configure = null)
    {
        Handler = new ScriptedHttpMessageHandler();
        Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        ProviderOptions = new SystemOneOptions
        {
            BaseUrl = BaseUrl,
            Model = Model,
            Timeout = TimeSpan.FromMilliseconds(250),
        };
        configure?.Invoke(ProviderOptions);
        Provider = new SystemOneProvider(Client, ProviderOptions);
    }

    internal ScriptedHttpMessageHandler Handler { get; }

    public HttpClient Client { get; }

    /// <summary>The options the provider was built from.</summary>
    public SystemOneOptions ProviderOptions { get; }

    public override IDecisionProvider Provider { get; }

    public override int RequestCount => Handler.RequestCount;

    internal RecordedRequest LastRequest => Handler.LastRequest;

    /// <summary>The last request's body, parsed.</summary>
    public JsonElement LastBody =>
        JsonSerializer.Deserialize<JsonElement>(
            LastRequest.Body ?? throw new InvalidOperationException("The last request had no body."));

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
                DecisionType.Boolean => SystemOneFixtures.BooleanAnswer(0.91),
                DecisionType.Choice => SystemOneFixtures.ChoiceAnswer(
                    "review",
                    new Dictionary<string, double>(StringComparer.Ordinal) { ["allow"] = 0.15, ["review"] = 0.7, ["block"] = 0.15 },
                    confidence: 0.7),
                DecisionType.Score => SystemOneFixtures.ScoreAnswer(Levels, [0.1, 0.6, 0.3], score: 1.2, confidence: 0.6),
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
                Handler.Respond(HttpStatusCode.ServiceUnavailable, SystemOneFixtures.Html(Marker), "text/html");
                break;
            case FailureKind.Malformed:
                Handler.Respond(
                    HttpStatusCode.OK,
                    SystemOneFixtures.Response(SystemOneFixtures.Answer("noul", ("explanation", Marker))));
                break;
            case FailureKind.RejectedInput:
                Handler.Respond(HttpStatusCode.BadRequest, SystemOneFixtures.OpenRouterError(400, $"invalid state: {Marker}"));
                break;
            case FailureKind.Unauthorized:
                Handler.Respond(
                    HttpStatusCode.Unauthorized,
                    SystemOneFixtures.TypeSafeError("authentication_error", $"no key for {Marker}"));
                break;
            case FailureKind.Unknown:
                Handler.Respond(HttpStatusCode.Found, SystemOneFixtures.Html(Marker), "text/html");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    public override void ScriptHang(Action onHang) => Handler.Hang(onHang);
}
