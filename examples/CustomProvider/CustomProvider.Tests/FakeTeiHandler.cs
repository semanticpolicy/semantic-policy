using System.Net;
using System.Text;

namespace CustomProvider.Tests;

/// <summary>
/// Stands in for a TEI server: answers the way a test scripted it and keeps the last request's
/// method, URI and body, copied on arrival because the provider disposes the message afterwards.
/// No test reaches a real server.
/// </summary>
internal sealed class FakeTeiHandler : HttpMessageHandler
{
    private Func<CancellationToken, Task<HttpResponseMessage>> _script =
        _ => throw new InvalidOperationException("The fake handler has no script.");

    public HttpMethod? LastMethod { get; private set; }

    public Uri? LastUri { get; private set; }

    public string? LastBody { get; private set; }

    /// <summary>
    /// Answer with the status and the body. With <paramref name="declareLength"/> the response carries
    /// its <c>Content-Length</c>, as a buffered one does; without it the reader learns the length only
    /// by reading, as from a server that streams its answer.
    /// </summary>
    public void Respond(HttpStatusCode status, string body, bool declareLength = true) =>
        _script = _ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = declareLength
                ? new StringContent(body, Encoding.UTF8, "application/json")
                : new StreamedContent(Encoding.UTF8.GetBytes(body)),
        });

    /// <summary>Fail before any response exists, as a transport does when nothing listens.</summary>
    public void Throw(Exception exception) => _script = _ => throw exception;

    /// <summary>
    /// Never answer; finish only when the token the transport was given is cancelled.
    /// <paramref name="onHang"/> runs once the request is waiting, so a cancellation it requests lands
    /// during the hang.
    /// </summary>
    public void Hang(Action? onHang = null) =>
        _script = async cancellationToken =>
        {
            Task hang = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            onHang?.Invoke();
            await hang;
            throw new InvalidOperationException("unreachable");
        };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastMethod = request.Method;
        LastUri = request.RequestUri;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await _script(cancellationToken);
    }

    private sealed class StreamedContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            stream.WriteAsync(body, cancellationToken).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
