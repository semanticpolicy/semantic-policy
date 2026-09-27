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
    // TEI returns a softmax over the classifier's labels. Protocol v0 leaves the scale's name open,
    // and `calibrated` or `sigmoid` would each claim something the numbers are not.
    private const string _scale = "softmax";

    private readonly HttpClient _client;
    private readonly TeiClassifierOptions _options;
    private readonly Uri _predict;

    /// <param name="id">The provider's id, normally the name it is registered under.</param>
    /// <param name="client">
    /// The client to send with, with no timeout of its own: the provider keeps the timer. The caller
    /// owns it.
    /// </param>
    /// <param name="options">The server and what it serves.</param>
    public TeiClassifierProvider(string id, HttpClient client, TeiClassifierOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(client);
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
        _client = client;
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
        try
        {
            using HttpResponseMessage response = await _client
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, timer.Token)
                .ConfigureAwait(false);
            byte[] body = await response.Content.ReadAsByteArrayAsync(timer.Token).ConfigureAwait(false);
            return Read(response.StatusCode, body, clock.Elapsed.TotalMilliseconds);
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
    // the body's length; the parsed body stays in memory, in the result's raw element.
    private static string DescribeError(HttpStatusCode status, JsonElement? body, int length)
    {
        StringBuilder text = new StringBuilder("HTTP ").Append((int)status);
        if (body is { ValueKind: JsonValueKind.Object } json
            && json.TryGetProperty("error_type", out JsonElement errorType)
            && errorType.ValueKind == JsonValueKind.String)
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
