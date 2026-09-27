using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.Http;

/// <summary>
/// Any server that speaks protocol v0 over HTTP as an <see cref="IDecisionProvider"/>: one
/// <c>POST</c> of the v0 request per call, the server's v0 result read back inside the capabilities
/// the options declare. It decides nothing itself. Thresholds, verdicts and what a failure means
/// belong to the policy, and a denied verdict downstream is not proof of an attack any more than an
/// allowed one is proof of safety.
/// </summary>
/// <remarks>
/// A <c>200</c> carries a <c>success</c> or an <c>abstain</c>; anything else on a <c>200</c>, and any
/// other 2xx, is <see cref="FailureKind.Malformed"/>. A status outside 2xx is a failure whose kind a
/// v0 failure body names, or else the status. The provider retries nothing and reads no
/// <c>Retry-After</c>: a host that wants either configures the <see cref="HttpClient"/> it hands in.
/// It logs nothing and starts no activity, and no message, exception or
/// <see cref="ProviderResult.ToString"/> it produces carries the question, the context or any text
/// the server wrote; the server's body stays in <see cref="ProviderResult.Raw"/>.
/// </remarks>
public sealed class HttpProvider : IDecisionProvider
{
    private const string _product = "SemanticPolicy.Providers.Http";

    private static readonly ProductInfoHeaderValue _userAgent = new(_product, PackageVersion());

    private readonly Func<HttpClient> _clientSource;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly bool _structuredContext;
    private readonly int? _maxContextLength;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Builds the provider on a client the caller owns. The client's <see cref="HttpClient.Timeout"/>
    /// should be <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>: the provider keeps its own
    /// timer, and a shorter client timeout surfaces as an <see cref="OperationCanceledException"/> with
    /// no token cancelled, which the evaluator treats as a programming error. The client is neither
    /// modified nor disposed here. The options are validated and copied, so a later change to them
    /// changes nothing. This constructor never reads the environment: only a registration reads
    /// <see cref="HttpProviderOptions.ApiKeyVariable"/>, so a key for a provider built here goes on
    /// <see cref="HttpProviderOptions.ApiKey"/>, or there is none.
    /// </summary>
    /// <param name="httpClient">The client every call is sent through.</param>
    /// <param name="options">Where to call, what the server answers, and with which key if any.</param>
    /// <exception cref="ArgumentException">
    /// The options fail <see cref="HttpProviderOptions.EnsureValid"/>, or name an
    /// <see cref="HttpProviderOptions.ApiKeyVariable"/> without an <see cref="HttpProviderOptions.ApiKey"/>:
    /// the variable would never be read, and the provider would send no key and report no error.
    /// </exception>
    public HttpProvider(HttpClient httpClient, HttpProviderOptions options)
        : this(Constant(httpClient), options)
    {
    }

    // The registration's path: a source invoked once per call, so a factory's handler rotation applies
    // to the provider's traffic for the life of the process. What the source returns is not disposed
    // here, because a factory client's handler outlives the client and a caller's client is the
    // caller's to dispose.
    internal HttpProvider(Func<HttpClient> clientSource, HttpProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(clientSource);
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        if (!string.IsNullOrWhiteSpace(options.ApiKeyVariable) && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException(
                "The options name an ApiKeyVariable but carry no ApiKey; only a registration reads the environment.",
                nameof(HttpProviderOptions.ApiKeyVariable));
        }

        _clientSource = clientSource;

        // Joined as text: Uri's own combination would drop a base path such as a gateway's /api.
        _endpoint = new Uri(options.BaseUrl!.AbsoluteUri.TrimEnd('/') + options.Path);
        _model = options.Model!;
        _apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? null : options.ApiKey;
        _structuredContext = options.StructuredContext!.Value;
        _maxContextLength = options.MaxContextLength;
        _timeout = options.Timeout;
        Id = options.Id;
        Capabilities = new ProviderCapabilities(
            new HashSet<DecisionType>(options.Types!),
            new HashSet<EvidenceKind>(options.Evidence!),
            RawOutput: true,
            StructuredContext: _structuredContext);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public ProviderCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async Task<ProviderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();

        long started = Stopwatch.GetTimestamp();

        // Rendered once, for the limit and for a text-only server alike. The limit counts the canonical
        // text even when the structure is what goes out: some servers cut a long input without saying
        // so, and an instruction past the cut would then read like the text before it.
        string canonical = SemanticContext.ToCanonicalText(request.Context);
        if (_maxContextLength is { } limit && canonical.Length > limit)
        {
            string lengths = string.Create(
                CultureInfo.InvariantCulture,
                $"the context is {canonical.Length} characters after flattening, over the limit of {limit}");
            return Failed(request.Type, FailureKind.RejectedInput, lengths, started);
        }

        DecisionRequest wire = _structuredContext
            ? request
            : request with { Context = JsonSerializer.SerializeToElement(canonical, SemanticPolicyJson.Options) };
        using HttpRequestMessage message = CreateMessage(wire);
        return await HttpProviderCall.SendAsync(
                _clientSource(),
                message,
                _timeout,
                (response, body) => Interpret(request, response, body, started),
                (kind, text) => Failed(request.Type, kind, text, started),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static Func<HttpClient> Constant(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        return () => httpClient;
    }

    // The informational version cut at the first '+': the SDK appends the commit hash there, and a
    // hash is not a version. The assembly version's three components when the attribute is absent.
    private static string PackageVersion()
    {
        Assembly assembly = typeof(HttpProvider).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return (assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
    }

    private static double Elapsed(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private HttpRequestMessage CreateMessage(DecisionRequest request)
    {
        HttpRequestMessage message = new(HttpMethod.Post, _endpoint)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, SemanticPolicyJson.Options)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Per message, never on the client: the key then never lives on an object the caller shares.
        if (_apiKey is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        message.Headers.UserAgent.Add(_userAgent);
        return message;
    }

    private ProviderResult Interpret(DecisionRequest request, HttpResponseMessage response, byte[] body, long started)
    {
        HttpStatusCode status = response.StatusCode;
        JsonElement? raw = HttpProviderResponse.TryParse(body);
        if (status != HttpStatusCode.OK)
        {
            // A v0 failure body names the exact kind a status can only approximate. On a 2xx it names
            // nothing: an answer other than 200 is outside the binding whatever it carries.
            FailureKind? named = response.IsSuccessStatusCode ? null : HttpProviderResponse.FailureKindOf(raw);
            string text = HttpProviderResponse.Describe(
                status,
                body.Length,
                named is { } kind ? $"kind {HttpProviderResponse.WireName(kind)}" : null);
            return Failed(request.Type, named ?? HttpProviderResponse.KindOf(status), text, started) with { Raw = raw };
        }

        if (raw is not { } json)
        {
            return Failed(request.Type, FailureKind.Malformed, HttpProviderResponse.Describe(status, body.Length, "not JSON"), started);
        }

        // A failure on 200 is refused rather than relayed: the binding keeps 200 for an answer, so that
        // infrastructure between the two sees a failure as one.
        ProviderResult? answer = HttpProviderResponse.ReadResult(json);
        string? deviation = answer switch
        {
            null => "not a protocol v0 result",
            { Outcome.Status: OutcomeStatus.Failure } => "a failure outcome",
            { } other when other.Type != request.Type => "a result of another type",
            _ => null,
        };
        if (deviation is not null)
        {
            string text = HttpProviderResponse.Describe(status, body.Length, deviation);
            return Failed(request.Type, FailureKind.Malformed, text, started) with { Raw = json };
        }

        return Relay(request, answer!, json, started);
    }

    // What the server answered, inside the declaration and under this client's name: the value, the
    // outcome's status, the evidence of declared kinds, the server's model and request id. Its outcome
    // message, usage and extra are text and JSON the library cannot vouch for, so they stay in Raw,
    // which is never serialized.
    private ProviderResult Relay(DecisionRequest request, ProviderResult answer, JsonElement json, long started)
    {
        ProviderOutcome outcome = answer.Outcome.Status == OutcomeStatus.Success
            ? ProviderOutcome.Success
            : ProviderOutcome.Abstain(message: null);
        List<Evidence> evidence = [.. answer.Evidence.Where(entry => Capabilities.Evidence.Contains(entry.Kind))];
        string model = string.IsNullOrWhiteSpace(answer.Provider.Model) ? _model : answer.Provider.Model;
        ProviderMetadata metadata = new(Id, model, Elapsed(started), answer.Provider.RequestId);
        return new ProviderResult(request.Type, outcome, answer.Value, evidence, metadata, json);
    }

    private ProviderResult Failed(DecisionType type, FailureKind kind, string message, long started) =>
        ProviderResult.Failed(type, kind, message, new ProviderMetadata(Id, _model, Elapsed(started)));
}
