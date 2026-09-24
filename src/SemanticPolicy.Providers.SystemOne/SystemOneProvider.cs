using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.SystemOne;

/// <summary>
/// Any server that answers the System One wire (<c>/v1/systemone</c>) as an
/// <see cref="IDecisionProvider"/>: one HTTP call per request, one question per call, the answer
/// normalised onto protocol v0. It reports the server's numbers as <see cref="EvidenceKind.Score"/>
/// unless the options declare them a probability, and decides nothing. Thresholds, verdicts and what a
/// failure means belong to the policy, and a denied verdict downstream is not proof of an attack any
/// more than an allowed one is proof of safety.
/// </summary>
/// <remarks>
/// The provider retries nothing and reads no <c>Retry-After</c>: a host that wants either configures
/// the <see cref="HttpClient"/> it hands in. It logs nothing and starts no activity, and no message,
/// exception or <see cref="ProviderResult.ToString"/> it produces carries the question, the context, a
/// request body or a response body.
/// </remarks>
public sealed class SystemOneProvider : IDecisionProvider
{
    private const string _product = "SemanticPolicy.Providers.SystemOne";

    private static readonly ProductInfoHeaderValue _userAgent = new(_product, PackageVersion());

    private readonly Func<HttpClient> _clientSource;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly EvidenceKind _evidence;
    private readonly int? _maxContextLength;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Builds the provider on a client the caller owns. The client's <see cref="HttpClient.Timeout"/>
    /// should be <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>: the provider keeps its own
    /// timer, and a shorter client timeout surfaces as an <see cref="OperationCanceledException"/> with
    /// no token cancelled, which the evaluator treats as a programming error. The client is neither
    /// modified nor disposed here. The options are validated and copied, so a later change to them
    /// changes nothing. This constructor never reads the environment: only a registration reads
    /// <see cref="SystemOneOptions.ApiKeyVariable"/>, so a key for a provider built here goes on
    /// <see cref="SystemOneOptions.ApiKey"/>, or there is none.
    /// </summary>
    /// <param name="httpClient">The client every call is sent through.</param>
    /// <param name="options">Where to call, as what, and with which key if any.</param>
    /// <exception cref="ArgumentException">
    /// The options fail <see cref="SystemOneOptions.EnsureValid"/>, or name an
    /// <see cref="SystemOneOptions.ApiKeyVariable"/> without an <see cref="SystemOneOptions.ApiKey"/>:
    /// the variable would never be read, and the provider would send no key and report no error.
    /// </exception>
    public SystemOneProvider(HttpClient httpClient, SystemOneOptions options)
        : this(Constant(httpClient), options)
    {
    }

    // The registration's path: a source invoked once per call, so a factory's handler rotation applies
    // to the provider's traffic for the life of the process. What the source returns is not disposed
    // here, because a factory client's handler outlives the client and a caller's client is the
    // caller's to dispose.
    internal SystemOneProvider(Func<HttpClient> clientSource, SystemOneOptions options)
    {
        ArgumentNullException.ThrowIfNull(clientSource);
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        if (!string.IsNullOrWhiteSpace(options.ApiKeyVariable) && string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException(
                "The options name an ApiKeyVariable but carry no ApiKey; only a registration reads the environment.",
                nameof(SystemOneOptions.ApiKeyVariable));
        }

        _clientSource = clientSource;

        // Joined as text: Uri's own combination would drop a base path such as a gateway's /api.
        _endpoint = new Uri(options.BaseUrl!.AbsoluteUri.TrimEnd('/') + options.Path);
        _model = options.Model!;
        _apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? null : options.ApiKey;
        _evidence = options.Evidence;
        _maxContextLength = options.MaxContextLength;
        _timeout = options.Timeout;
        Id = options.Id;
        Capabilities = new ProviderCapabilities(
            new HashSet<DecisionType> { DecisionType.Boolean, DecisionType.Choice, DecisionType.Score },
            new HashSet<EvidenceKind> { _evidence },
            RawOutput: true,
            StructuredContext: true);
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

        // Counted on the canonical text, the flattening every client shares, although the structure is
        // what goes out: some servers cut a long input without saying so, and an instruction past the
        // cut would then score like the text before it. The policy's failure behaviour decides.
        if (_maxContextLength is { } limit)
        {
            int length = SemanticContext.ToCanonicalText(request.Context).Length;
            if (length > limit)
            {
                string lengths = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the context is {length} characters after flattening, over the limit of {limit}");
                return Failed(request.Type, FailureKind.RejectedInput, lengths, started);
            }
        }

        using HttpRequestMessage message = CreateMessage(request);
        return await SystemOneCall.SendAsync(
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
        Assembly assembly = typeof(SystemOneProvider).Assembly;
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
            Content = new ByteArrayContent(SystemOneRequest.Write(request, _model)),
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
        JsonElement? raw = SystemOneResponse.TryParse(body);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            FailureKind kind = SystemOneResponse.KindOf(response.StatusCode);
            string message = SystemOneResponse.DescribeError(response.StatusCode, raw, body.Length);
            return Failed(request.Type, kind, message, started) with { Raw = raw };
        }

        if (raw is not { } json)
        {
            string length = body.Length.ToString(CultureInfo.InvariantCulture);
            return Failed(request.Type, FailureKind.Malformed, $"the body is not JSON ({length} bytes)", started);
        }

        SystemOneResponse.Reading reading = SystemOneResponse.Read(request, json, _evidence);
        if (reading.Failure is { } shape)
        {
            return Failed(request.Type, FailureKind.Malformed, shape, started) with { Raw = json };
        }

        ProviderMetadata metadata = new(
            Id,
            SystemOneResponse.Model(json) ?? _model,
            Elapsed(started),
            SystemOneResponse.RequestId(json),
            SystemOneResponse.Usage(json),
            reading.Extra);
        return new ProviderResult(
            request.Type,
            ProviderOutcome.Success,
            reading.Value,
            [reading.Evidence!],
            metadata,
            json);
    }

    private ProviderResult Failed(DecisionType type, FailureKind kind, string message, long started) =>
        ProviderResult.Failed(type, kind, message, new ProviderMetadata(Id, _model, Elapsed(started)));
}
