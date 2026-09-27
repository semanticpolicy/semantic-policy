using System.Net;
using System.Text.Json;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Contract;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.Http;

namespace SemanticPolicy.Providers.ContractTests.Http;

/// <summary>
/// The Http client on a scripted transport, declared as a text-only server that answers every type
/// with <c>Score</c> evidence would be: plain <c>http</c> on loopback and no key. Its answers also carry
/// <c>logit</c> evidence, which the declaration leaves out. The timeout is short so the suite's own
/// timeout case returns in well under a second.
/// </summary>
public sealed class HttpHarness : ProviderHarness
{
    /// <summary>A model name no server answers to: the transport is a double, so it is never sent.</summary>
    public const string Model = "v0-test-model";

    /// <summary>The model the scripted server names in its answers.</summary>
    public const string ServerModel = "v0-test-server-model";

    public static readonly Uri BaseUrl = new("http://127.0.0.1:8765");

    public static readonly IReadOnlyDictionary<string, string> Options = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["allow"] = "safe to proceed as-is",
        ["review"] = "a person must look before it proceeds",
        ["block"] = "must not proceed",
    };

    public static readonly IReadOnlyList<string> Levels = ["low", "medium", "high"];

    public HttpHarness(Action<HttpProviderOptions>? configure = null)
    {
        Handler = new ScriptedHttpMessageHandler();
        Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        ProviderOptions = new HttpProviderOptions
        {
            BaseUrl = BaseUrl,
            Model = Model,
            Types = [DecisionType.Boolean, DecisionType.Choice, DecisionType.Score],
            Evidence = [EvidenceKind.Score],
            StructuredContext = false,
            Timeout = TimeSpan.FromMilliseconds(250),
        };
        configure?.Invoke(ProviderOptions);
        Provider = new HttpProvider(Client, ProviderOptions);
    }

    internal ScriptedHttpMessageHandler Handler { get; }

    public HttpClient Client { get; }

    /// <summary>The options the provider was built from.</summary>
    public HttpProviderOptions ProviderOptions { get; }

    public override IDecisionProvider Provider { get; }

    public override int RequestCount => Handler.RequestCount;

    internal RecordedRequest LastRequest => Handler.LastRequest;

    /// <summary>The last request's body, parsed.</summary>
    public JsonElement LastBody =>
        JsonSerializer.Deserialize<JsonElement>(
            LastRequest.Body ?? throw new InvalidOperationException("The last request had no body."));

    /// <summary>
    /// A v0 success for the type as the scripted server answers it: <c>score</c> evidence with both
    /// ends of a Boolean, <c>logit</c> evidence beside it, and the server's model and request id.
    /// </summary>
    public static string Success(DecisionType type) =>
        type switch
        {
            DecisionType.Boolean => """
                {"protocol":"semanticpolicy/v0","type":"boolean","outcome":{"status":"success"},"value":true,
                 "evidence":[{"kind":"score","scale":"sigmoid","values":{"true":0.91,"false":0.09}},
                             {"kind":"logit","values":{"true":2.31,"false":-2.31}}],
                 "provider":{"id":"v0-server","model":"v0-test-server-model","latencyMs":12,"requestId":"req-0001"}}
                """,
            DecisionType.Choice => """
                {"protocol":"semanticpolicy/v0","type":"choice","outcome":{"status":"success"},"value":"review",
                 "evidence":[{"kind":"score","scale":"sigmoid","values":{"allow":0.2,"review":0.8,"block":0.3}},
                             {"kind":"logit","values":{"allow":-1.39,"review":1.39,"block":-0.85}}],
                 "provider":{"id":"v0-server","model":"v0-test-server-model","latencyMs":12,"requestId":"req-0001"}}
                """,
            DecisionType.Score => """
                {"protocol":"semanticpolicy/v0","type":"score","outcome":{"status":"success"},"value":{"level":"medium","index":1},
                 "evidence":[{"kind":"score","scale":"sigmoid","values":{"low":0.1,"medium":0.7,"high":0.4}},
                             {"kind":"logit","values":{"low":-2.2,"medium":0.85,"high":-0.41}}],
                 "provider":{"id":"v0-server","model":"v0-test-server-model","latencyMs":12,"requestId":"req-0001"}}
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    /// <summary>
    /// A Boolean answer built member by member, each given as its JSON or left out as
    /// <see langword="null"/>. By default a v0 success with <c>score</c> evidence, and a
    /// <c>provider.extra</c> that carries <see cref="ProviderHarness.Marker"/>.
    /// </summary>
    public static string Answer(
        string? protocol = "\"semanticpolicy/v0\"",
        string? type = "\"boolean\"",
        string? outcome = """{"status":"success"}""",
        string? value = "true",
        string? evidence = """[{"kind":"score","values":{"true":0.91,"false":0.09}}]""",
        string? provider = $$$"""{"id":"v0-server","model":"{{{ServerModel}}}","latencyMs":12,"extra":{"note":"{{{Marker}}}"}}""")
    {
        List<string> members = [];
        Add("protocol", protocol);
        Add("type", type);
        Add("outcome", outcome);
        Add("value", value);
        Add("evidence", evidence);
        Add("provider", provider);
        return "{" + string.Join(",", members) + "}";

        void Add(string name, string? json)
        {
            if (json is not null)
            {
                members.Add($"\"{name}\":{json}");
            }
        }
    }

    /// <summary>A v0 failure body of the kind, its message carrying <paramref name="message"/>.</summary>
    public static string FailureBody(string kind, string message) =>
        $$$"""
        {"protocol":"semanticpolicy/v0","type":"boolean","outcome":{"status":"failure","kind":"{{{kind}}}","message":"{{{message}}}"},
         "provider":{"id":"v0-server","model":"another-server-model","latencyMs":3}}
        """;

    /// <summary>A page a proxy or a framework answers with, carrying <paramref name="text"/>.</summary>
    public static string Html(string text) => $"<html><body><h1>Error</h1><p>{text}</p></body></html>";

    public override DecisionRequest CreateRequest(DecisionType type, string marker)
    {
        string question = $"Does the text mention {marker}?";
        JsonElement context = JsonSerializer.SerializeToElement(
            new { text = $"A synthetic context that mentions {marker}.", source = "test" });
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

    public override void ScriptSuccess(DecisionType type) => Handler.Respond(HttpStatusCode.OK, Success(type));

    public override void ScriptFailure(FailureKind kind)
    {
        switch (kind)
        {
            case FailureKind.Timeout:
                Handler.Hang();
                break;
            case FailureKind.Unavailable:
                Handler.Respond(HttpStatusCode.BadGateway, Html(Marker), "text/html");
                break;
            case FailureKind.Malformed:
                Handler.Respond(HttpStatusCode.OK, FailureBody("unavailable", $"model not loaded for {Marker}"));
                break;
            case FailureKind.RejectedInput:
                Handler.Respond(HttpStatusCode.BadRequest, $$$"""{"error":{"message":"invalid input: {{{Marker}}}"}}""");
                break;
            case FailureKind.Unauthorized:
                Handler.Respond(HttpStatusCode.Forbidden, FailureBody("unauthorized", $"no key for {Marker}"));
                break;
            case FailureKind.Unknown:
                Handler.Respond(HttpStatusCode.InternalServerError, FailureBody("unknown", $"crashed on {Marker}"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    public override void ScriptHang(Action onHang) => Handler.Hang(onHang);
}
