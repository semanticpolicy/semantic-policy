using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SemanticPolicy.Providers.ContractTests.Support;

/// <summary>
/// A transport double: answers every request the way it was scripted, honouring the token it is
/// given, and keeps a copy of each request as it passed through. The copy is taken on arrival because
/// the message and its content may be disposed by the time a test reads them.
/// </summary>
internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly List<RecordedRequest> _requests = [];
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _script =
        (_, _) => throw new InvalidOperationException("The scripted handler has no script.");

    /// <summary>Every request received so far, in arrival order.</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public int RequestCount
    {
        get
        {
            lock (_lock)
            {
                return _requests.Count;
            }
        }
    }

    public RecordedRequest LastRequest
    {
        get
        {
            lock (_lock)
            {
                return _requests.Count == 0
                    ? throw new InvalidOperationException("The scripted handler received no request.")
                    : _requests[^1];
            }
        }
    }

    /// <summary>Answer every request with the status and, when given, the body under the media type.</summary>
    public void Respond(
        HttpStatusCode status,
        string? body = null,
        string mediaType = "application/json",
        IReadOnlyDictionary<string, string>? headers = null)
    {
        _script = (_, _) =>
        {
            HttpResponseMessage response = new(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, mediaType);
            }

            foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        };
    }

    /// <summary>Throw the exception from the transport, before any response exists.</summary>
    public void Throw(Exception exception) => _script = (_, _) => throw exception;

    /// <summary>
    /// Never answer; complete only when the token the transport was given is cancelled.
    /// <paramref name="onHang"/>, when given, runs once the request is waiting, so a cancellation it
    /// requests lands during the hang.
    /// </summary>
    public void Hang(Action? onHang = null) =>
        _script = async (_, cancellationToken) =>
        {
            Task hang = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            onHang?.Invoke();
            await hang;
            throw new InvalidOperationException("unreachable");
        };

    /// <summary>
    /// Answer the status and headers at once with a body that never finishes: reading it completes
    /// only when the token the read was given is cancelled.
    /// </summary>
    public void HangBody(HttpStatusCode status) =>
        _script = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new HangingContent() });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        RecordedRequest recorded = new(
            request.Method,
            request.RequestUri,
            Snapshot(request.Headers, request.Content?.Headers),
            body,
            cancellationToken);
        lock (_lock)
        {
            _requests.Add(recorded);
        }

        return await _script(request, cancellationToken);
    }

    private static Dictionary<string, IReadOnlyList<string>> Snapshot(
        HttpRequestHeaders headers,
        HttpContentHeaders? contentHeaders)
    {
        Dictionary<string, IReadOnlyList<string>> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IEnumerable<string> values) in headers)
        {
            snapshot[name] = [.. values];
        }

        if (contentHeaders is not null)
        {
            foreach ((string name, IEnumerable<string> values) in contentHeaders)
            {
                snapshot[name] = [.. values];
            }
        }

        return snapshot;
    }

    private sealed class HangingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

/// <summary>One request as the transport saw it.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The request URI.</param>
/// <param name="Headers">Request and content headers together, keyed case-insensitively.</param>
/// <param name="Body">The request body as a string, or <see langword="null"/> when there was none.</param>
/// <param name="Token">The token the transport was given; read it later to see whether it was cancelled.</param>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Headers,
    string? Body,
    CancellationToken Token)
{
    /// <summary>The header's values joined as one string, or <see langword="null"/> when absent.</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name, out IReadOnlyList<string>? values) ? string.Join(", ", values) : null;
}
