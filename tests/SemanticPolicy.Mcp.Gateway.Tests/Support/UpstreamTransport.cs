using System.Collections.Concurrent;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// The gateway's upstream side in a test: an in-process server behind it, which stops when the gateway lets go of it, as
// a child process does when its client is disposed, and which fails the ways the stdio transport fails. The SDK's
// stream transports leave their streams open on disposal, so the stop is the pipe closing.
internal sealed class UpstreamTransport(IClientTransport inner, Action stopUpstream) : IClientTransport
{
    private readonly ConcurrentQueue<string> _sentRequests = new();
    private Session? _session;

    public string Name => inner.Name;

    // Thrown by the connect, where the stdio transport throws when the process cannot be started.
    public Exception? ConnectFailure { get; set; }

    // Thrown by every send, as the stdio transport throws when the process has exited during the handshake.
    public Exception? SendFailure { get; set; }

    // The method of each request the gateway sent upstream, in order. The upstream's server can refuse a request before
    // any handler of the test's sees it, so this, not a handler's count, shows what crossed.
    public IReadOnlyList<string> SentRequests => [.. _sentRequests];

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }

        _session = new Session(await inner.ConnectAsync(cancellationToken), SendFailure, _sentRequests, stopUpstream);
        return _session;
    }

    // Ends the session with the exception the stdio transport ends it with when the child exits on its own.
    public void Fail(Exception exception) =>
        (_session ?? throw new InvalidOperationException("The upstream is not connected.")).Fail(exception);

    private sealed class Session : ITransport
    {
        private readonly ITransport _inner;
        private readonly Exception? _sendFailure;
        private readonly ConcurrentQueue<string> _sentRequests;
        private readonly Action _stop;
        private readonly Channel<JsonRpcMessage> _messages = Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly Task _pump;

        public Session(ITransport inner, Exception? sendFailure, ConcurrentQueue<string> sentRequests, Action stop)
        {
            _inner = inner;
            _sendFailure = sendFailure;
            _sentRequests = sentRequests;
            _stop = stop;
            _pump = PumpAsync();
        }

        public string? SessionId => _inner.SessionId;

        public ChannelReader<JsonRpcMessage> MessageReader => _messages.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            if (message is JsonRpcRequest request)
            {
                _sentRequests.Enqueue(request.Method);
            }

            return _sendFailure is null ? _inner.SendMessageAsync(message, cancellationToken) : Task.FromException(_sendFailure);
        }

        public void Fail(Exception exception) => _messages.Writer.TryComplete(exception);

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            _stop();
            _messages.Writer.TryComplete();
            await _pump;
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (JsonRpcMessage message in _inner.MessageReader.ReadAllAsync())
                {
                    _messages.Writer.TryWrite(message);
                }

                _messages.Writer.TryComplete();
            }
            catch (Exception failure)
            {
                _messages.Writer.TryComplete(failure);
            }
        }
    }
}
