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

    public void Respond(HttpStatusCode status, string body) =>
        _script = _ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
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
}
