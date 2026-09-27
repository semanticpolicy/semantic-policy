using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using SemanticPolicy;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace CustomProvider;

/// <summary>
/// A decision provider over a Text Embeddings Inference (TEI) server that serves a two-label text
/// classifier on <c>POST /predict</c>. It answers Boolean questions only, and the answer is the
/// classifier's: a fixed-task classifier answers its one question whatever the rule asks, so bind it
/// to the rule that asks that question. The result is a probabilistic signal for the policy to
/// threshold, never a ruling.
/// </summary>
public sealed class TeiClassifierProvider : IDecisionProvider
{
    /// <summary>
    /// The longest body the provider reads, 1 MiB. TEI's answer is a few dozen bytes, so a longer body
    /// is not an answer, and reading it whole would let a misbehaving server fill the host's memory.
    /// </summary>
    public const int MaxBodyBytes = 1024 * 1024;

    // TEI returns a softmax over the classifier's labels. Protocol v0 leaves the scale's name open,
    // and `calibrated` or `sigmoid` would each claim something the numbers are not.
    private const string _scale = "softmax";

    // The `error_type` values TEI answers with.
    private static readonly HashSet<string> _errorTypes =
        new(StringComparer.Ordinal) { "Unhealthy", "Backend", "Overloaded", "Validation", "Tokenizer", "Empty" };

    private readonly Func<HttpClient> _clientSource;
    private readonly TeiClassifierOptions _options;
    private readonly Uri _predict;

    /// <param name="id">The provider's id, normally the name it is registered under.</param>
    /// <param name="clientSource">
    /// Where each call gets the client to send with. It is asked once per call, so that a factory's
    /// handler rotation reaches a provider registered for the life of the process. The client needs no
    /// timeout of its own, because the provider keeps the timer, and the provider never disposes it.
    /// </param>
    /// <param name="options">The server and what it serves.</param>
    public TeiClassifierProvider(string id, Func<HttpClient> clientSource, TeiClassifierOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(clientSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BaseUrl, nameof(options.BaseUrl));
        if (!options.BaseUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The base URL must be absolute.", nameof(options));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model, nameof(options.Model));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PositiveLabel, nameof(options.PositiveLabel));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Timeout, TimeSpan.Zero, nameof(options.Timeout));

        Id = id;
        _clientSource = clientSource;
        _options = options;
        _predict = new Uri(options.BaseUrl.AbsoluteUri.TrimEnd('/') + "/predict");
    }

    public string Id { get; }

    public ProviderCapabilities Capabilities { get; } = new(
        new HashSet<DecisionType> { DecisionType.Boolean },
        new HashSet<EvidenceKind> { EvidenceKind.Score },
        RawOutput: true,
        StructuredContext: false);

    public async Task<ProviderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Type != DecisionType.Boolean)
        {
            throw new ArgumentException("This provider answers Boolean questions only.", nameof(request));
        }

        // Only the context goes out, rendered to text the way every text-only provider receives it.
        // `truncate: false` overrides TEI's default of cutting an over-long input silently, which would
        // turn an instruction past the cut into benign text; TEI refuses such an input with a 422 instead.
        string inputs = SemanticContext.ToCanonicalText(request.Context);
        using HttpRequestMessage message = new(HttpMethod.Post, _predict)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { inputs, truncate = false }),
                Encoding.UTF8,
                "application/json"),
        };

        // The timer is the provider's own and covers reading the body too. Only it ends a call as a
        // Timeout; the caller's cancellation propagates as an OperationCanceledException.
        using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(_options.Timeout);
        Stopwatch clock = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        try
        {
            byte[]? body;
            try
            {
                // Headers first, so a body too long to be an answer is refused rather than buffered.
                response = await _clientSource()
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timer.Token)
                    .ConfigureAwait(false);
                body = await ReadBoundedAsync(response.Content, timer.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                long milliseconds = (long)Math.Round(_options.Timeout.TotalMilliseconds);
                return Failed(FailureKind.Timeout, $"no response within {milliseconds} ms", clock);
            }
            catch (HttpRequestException exception)
            {
                return Failed(FailureKind.Unavailable, $"HttpRequestException: {exception.HttpRequestError}", clock);
            }
            catch (HttpIOException exception)
            {
                return Failed(FailureKind.Unavailable, $"HttpIOException: {exception.HttpRequestError}", clock);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A handler the host added, such as a circuit breaker, throws its own type, and its
                // message may quote the request, so only the type is kept.
                return Failed(FailureKind.Unknown, $"{exception.GetType().Name} from the HTTP pipeline", clock);
            }

            if (body is null)
            {
                FailureKind kind = response.StatusCode == HttpStatusCode.OK ? FailureKind.Malformed : KindOf(response.StatusCode);
                return Failed(kind, $"HTTP {(int)response.StatusCode}, body over {MaxBodyBytes} bytes", clock);
            }

            return Read(response.StatusCode, body, clock.Elapsed.TotalMilliseconds);
        }
        finally
        {
            response?.Dispose();
        }
    }

    // The body, or null when it is longer than the limit: a declared length over it is refused before
    // any byte is read, and an undeclared one is read no further than the chunk that crosses it.
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream body = new();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > MaxBodyBytes)
            {
                return null;
            }

            body.Write(chunk, 0, read);
        }

        return body.ToArray();
    }

    private static FailureKind KindOf(HttpStatusCode status) =>
        (int)status switch
        {
            401 or 403 => FailureKind.Unauthorized,
            400 or 404 or 413 or 422 => FailureKind.RejectedInput,
            408 or 429 => FailureKind.Unavailable,
            >= 500 and <= 599 => FailureKind.Unavailable,
            >= 200 and <= 299 => FailureKind.Malformed,
            _ => FailureKind.Unknown,
        };

    // TEI's `error` text can quote the input, so the message keeps only the status, the error type and
    // the body's length; the parsed body stays in memory, in the result's raw element. The error type
    // is one of TEI's own names or left out, so a server that writes something else there is not quoted.
    private static string DescribeError(HttpStatusCode status, JsonElement? body, int length)
    {
        StringBuilder text = new StringBuilder("HTTP ").Append((int)status);
        if (body is { ValueKind: JsonValueKind.Object } json
            && json.TryGetProperty("error_type", out JsonElement errorType)
            && errorType.ValueKind == JsonValueKind.String
            && _errorTypes.Contains(errorType.GetString()!))
        {
            text.Append(", error_type ").Append(errorType.GetString());
        }

        return text.Append(", body ").Append(length).Append(" bytes").ToString();
    }

    private static JsonElement? TryParse(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ProviderResult Read(HttpStatusCode status, byte[] body, double latencyMs)
    {
        JsonElement? json = TryParse(body);
        if (status != HttpStatusCode.OK)
        {
            return Failed(KindOf(status), DescribeError(status, json, body.Length), latencyMs, json);
        }

        if (json is not { } answer)
        {
            return Failed(FailureKind.Malformed, $"HTTP 200, the body is not JSON, body {body.Length} bytes", latencyMs, null);
        }

        if (ReadLabels(answer) is not { } scores)
        {
            return Failed(
                FailureKind.Malformed,
                $"HTTP 200, the body is not two labels one of which is {_options.PositiveLabel}, body {body.Length} bytes",
                latencyMs,
                answer);
        }

        (double positive, double other) = scores;
        return new ProviderResult(
            DecisionType.Boolean,
            ProviderOutcome.Success,
            new BooleanValue(positive >= other),
            [new Evidence(EvidenceKind.Score, new Dictionary<string, double> { ["true"] = positive, ["false"] = other }, _scale)],
            Metadata(latencyMs),
            answer);
    }

    // One input answers `[{"score": …, "label": …}, …]`. Read strictly: exactly two labels, one of them
    // the positive one, each score a number in [0, 1]. Anything else could let a threshold read a
    // partial answer as a whole one.
    private (double Positive, double Other)? ReadLabels(JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Array || answer.GetArrayLength() != 2)
        {
            return null;
        }

        double? positive = null;
        double? other = null;
        foreach (JsonElement entry in answer.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("label", out JsonElement label)
                || label.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("score", out JsonElement score)
                || score.ValueKind != JsonValueKind.Number
                || !score.TryGetDouble(out double value)
                || value is < 0 or > 1)
            {
                return null;
            }

            if (string.Equals(label.GetString(), _options.PositiveLabel, StringComparison.Ordinal))
            {
                positive = value;
            }
            else
            {
                other = value;
            }
        }

        return positive is { } p && other is { } o ? (p, o) : null;
    }

    private ProviderResult Failed(FailureKind kind, string message, Stopwatch clock) =>
        Failed(kind, message, clock.Elapsed.TotalMilliseconds, null);

    private ProviderResult Failed(FailureKind kind, string message, double latencyMs, JsonElement? raw) =>
        ProviderResult.Failed(DecisionType.Boolean, kind, message, Metadata(latencyMs)) with { Raw = raw };

    private ProviderMetadata Metadata(double latencyMs) => new(Id, _options.Model, latencyMs);
}
