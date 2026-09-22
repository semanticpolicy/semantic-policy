using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.TypeSafe;

/// <summary>
/// The TypeSafe Jev decision model as an <see cref="IDecisionProvider"/>: one HTTP call per request,
/// one question per call, the answer normalised onto protocol v0. It reports what the model estimated
/// and decides nothing. Thresholds, verdicts and what a failure means belong to the policy, and a
/// denied verdict downstream is not proof of an attack any more than an allowed one is proof of safety.
/// </summary>
/// <remarks>
/// The adapter retries nothing and reads no <c>Retry-After</c>: a host that wants either configures the
/// <see cref="HttpClient"/> it hands in. It logs nothing and starts no activity, and no message,
/// exception or <see cref="ProviderResult.ToString"/> it produces carries the question, the context, a
/// request body or a response body.
/// </remarks>
public sealed class TypeSafeJevProvider : IDecisionProvider
{
    private const string _product = "SemanticPolicy.Providers.TypeSafe";
    private const string _requestIdHeader = "x-typesafe-request-id";

    private static readonly ProductInfoHeaderValue _userAgent = new(_product, PackageVersion());
    private static readonly ProviderCapabilities _capabilities = new(
        new HashSet<DecisionType> { DecisionType.Boolean, DecisionType.Choice, DecisionType.Score },
        new HashSet<EvidenceKind> { EvidenceKind.Probability },
        RawOutput: true,
        StructuredContext: true);

    private readonly Func<HttpClient> _clientSource;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Builds the provider on a client the caller owns. The client's <see cref="HttpClient.Timeout"/>
    /// should be <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>: the adapter keeps its own
    /// timer, and a shorter client timeout surfaces as an <see cref="OperationCanceledException"/> with
    /// no token cancelled, which the evaluator treats as a programming error. The client is neither
    /// modified nor disposed here. The options are validated and copied, so a later change to them
    /// changes nothing; the key must be on them, because this constructor never reads the environment.
    /// </summary>
    /// <param name="httpClient">The client every call is sent through.</param>
    /// <param name="options">Where to call, as what, with which key.</param>
    /// <exception cref="ArgumentException">
    /// The options fail <see cref="TypeSafeJevOptions.EnsureValid"/>, or carry no
    /// <see cref="TypeSafeJevOptions.ApiKey"/>.
    /// </exception>
    public TypeSafeJevProvider(HttpClient httpClient, TypeSafeJevOptions options)
        : this(Constant(httpClient), options)
    {
    }

    // The registration's path: a source invoked once per call, so a factory's handler rotation applies
    // to the adapter's traffic for the life of the process. What the source returns is not disposed
    // here, because a factory client's handler outlives the client and a caller's client is the
    // caller's to dispose.
    internal TypeSafeJevProvider(Func<HttpClient> clientSource, TypeSafeJevOptions options)
    {
        ArgumentNullException.ThrowIfNull(clientSource);
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("The options carry no ApiKey.", nameof(options));
        }

        TypeSafeJevRoute route = options.Route!;
        _clientSource = clientSource;

        // Joined as text: Uri's own combination would drop a base path such as OpenRouter's /api.
        _endpoint = new Uri(route.BaseUrl.AbsoluteUri.TrimEnd('/') + route.Path);
        _model = options.Model ?? route.Model;
        _apiKey = options.ApiKey;
        _timeout = options.Timeout;
        Id = options.Id;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public ProviderCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
    public async Task<ProviderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.EnsureValid();

        long started = Stopwatch.GetTimestamp();
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(_timeout);
        try
        {
            using HttpRequestMessage message = CreateMessage(request);
            using HttpResponseMessage response = await _clientSource()
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, timer.Token)
                .ConfigureAwait(false);
            byte[] body = await response.Content.ReadAsByteArrayAsync(timer.Token).ConfigureAwait(false);
            return Interpret(request, response, body, started);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Only the adapter's own timer is a Timeout. The caller's cancellation propagates above,
            // and a cancellation with neither token cancelled is a programming error that propagates too.
            long milliseconds = (long)Math.Round(_timeout.TotalMilliseconds);
            return Failed(request.Type, FailureKind.Timeout, $"no response within {milliseconds} ms", started);
        }
        catch (HttpRequestException exception)
        {
            return Failed(request.Type, FailureKind.Unavailable, $"HttpRequestException: {exception.HttpRequestError}", started);
        }
        catch (HttpIOException exception)
        {
            return Failed(request.Type, FailureKind.Unavailable, $"HttpIOException: {exception.HttpRequestError}", started);
        }
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
        Assembly assembly = typeof(TypeSafeJevProvider).Assembly;
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
            Content = new ByteArrayContent(JevRequest.Write(request, _model)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Per message, never on the client: the key then never lives on an object the caller shares.
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        message.Headers.UserAgent.Add(_userAgent);
        return message;
    }

    private ProviderResult Interpret(DecisionRequest request, HttpResponseMessage response, byte[] body, long started)
    {
        JsonElement? raw = JevResponse.TryParse(body);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            FailureKind kind = JevResponse.KindOf(response.StatusCode);
            string message = JevResponse.DescribeError(response.StatusCode, raw, body.Length);
            return Failed(request.Type, kind, message, started) with { Raw = raw };
        }

        if (raw is not { } json)
        {
            string length = body.Length.ToString(CultureInfo.InvariantCulture);
            return Failed(request.Type, FailureKind.Malformed, $"the body is not JSON ({length} bytes)", started);
        }

        JevResponse.Reading reading = JevResponse.Read(request, json);
        if (reading.Failure is { } shape)
        {
            return Failed(request.Type, FailureKind.Malformed, shape, started) with { Raw = json };
        }

        ProviderMetadata metadata = new(
            Id,
            JevResponse.Model(json) ?? _model,
            Elapsed(started),
            JevResponse.RequestId(json) ?? HeaderRequestId(response),
            JevResponse.Usage(json),
            reading.Extra);
        return new ProviderResult(
            request.Type,
            ProviderOutcome.Success,
            reading.Value,
            [reading.Evidence!],
            metadata,
            json);
    }

    // A gateway puts the call's id in the body; the vendor's own endpoint is reported to put it in a
    // header instead, so the header is the fallback and its absence is not an error.
    private static string? HeaderRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues(_requestIdHeader, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

    private ProviderResult Failed(DecisionType type, FailureKind kind, string message, long started) =>
        ProviderResult.Failed(type, kind, message, new ProviderMetadata(Id, _model, Elapsed(started)));
}
