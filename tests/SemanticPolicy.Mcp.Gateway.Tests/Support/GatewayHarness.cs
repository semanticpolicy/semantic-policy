using System.Collections;
using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// A host client, the gateway and an upstream server in this process, each link over two pipes, so a test watches both
// ends of the gateway and starts no process. The upstream's options decide what it declares and answers.
internal sealed class GatewayHarness : IAsyncDisposable
{
    public const string Command = "upstream-server";

    private readonly Pipe _hostToGateway = new();
    private readonly Pipe _gatewayToHost = new();
    private readonly Pipe _gatewayToUpstream = new();
    private readonly Pipe _upstreamToGateway = new();
    private readonly StreamServerTransport _hostSide;
    private readonly CancellationTokenSource _deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly Task _upstreamRun;
    private readonly GatewayComposition _composition;
    private readonly IDictionary _environment;
    private readonly Lock _lock = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waitingCalls = [];
    private int _waited;

    private GatewayHarness(
        McpServerOptions upstreamOptions,
        Func<GatewayHarness, IClientTransport>? upstream,
        string command,
        GatewayComposition? composition,
        IDictionary? environment)
    {
        _composition = composition ?? Screens.None;
        _environment = environment ?? new Dictionary<string, string>();
        _deadline.CancelAfter(TimeSpan.FromSeconds(10));

        // The upstream runs on the test's own token, not the deadline: stopping it would fire its handlers' tokens, and
        // a test that waits for one of them to fire would pass.
        Upstream = McpServer.Create(
            new StreamServerTransport(_gatewayToUpstream.Reader.AsStream(), _upstreamToGateway.Writer.AsStream()),
            upstreamOptions);
        _upstreamRun = Upstream.RunAsync(TestContext.Current.CancellationToken);
        UpstreamSide = new UpstreamTransport(
            new StreamClientTransport(_gatewayToUpstream.Writer.AsStream(), _upstreamToGateway.Reader.AsStream()),
            () => _gatewayToUpstream.Writer.Complete());
        _hostSide = new StreamServerTransport(_hostToGateway.Reader.AsStream(), _gatewayToHost.Writer.AsStream());
        Gateway = RunGatewayAsync(command, upstream?.Invoke(this) ?? UpstreamSide);
    }

    // A gateway that never answers, or a signal that never comes, fails the test at this point instead of hanging it.
    public CancellationToken Deadline => _deadline.Token;

    public McpServer Upstream { get; }

    // What the gateway connects to, unless a test hands it something else in its place.
    public UpstreamTransport UpstreamSide { get; }

    // Null until a host connects.
    public McpClient? Host { get; private set; }

    // The gateway's stderr.
    public StringWriter Log { get; } = new();

    // The gateway's exit code, once it stops.
    public Task<int> Gateway { get; }

    // Ends when the upstream's session does, which is how an in-process upstream shows its child stopping.
    public Task UpstreamRun => _upstreamRun;

    // Starts the upstream and the gateway, and no host: for a gateway that is expected to stop before it serves. The
    // upstream function returns what the gateway connects to: the in-process upstream's transport with a fault set, or
    // another transport in its place, which can write to the gateway's log as the real one could.
    public static GatewayHarness Start(
        McpServerOptions upstreamOptions,
        Func<GatewayHarness, IClientTransport>? upstream = null,
        string command = Command) =>
        new(upstreamOptions, upstream, command, composition: null, environment: null);

    // Starts the upstream and the gateway, then connects a host through the gateway. With no composition the gateway
    // screens nothing; the environment is the one the gateway reads, never the process's.
    public static async Task<GatewayHarness> ConnectAsync(
        McpServerOptions upstreamOptions,
        McpClientOptions? hostOptions = null,
        GatewayComposition? composition = null,
        IDictionary? environment = null)
    {
        GatewayHarness harness = new(upstreamOptions, upstream: null, Command, composition, environment);
        harness.Host = await McpClient.CreateAsync(
            new StreamClientTransport(harness._hostToGateway.Writer.AsStream(), harness._gatewayToHost.Reader.AsStream()),
            hostOptions,
            cancellationToken: harness.Deadline);
        return harness;
    }

    // Ends the host's session the way a host does: its client goes away and the gateway's input ends. The SDK's stream
    // transports leave their streams open on disposal, so the input ends by closing the pipe.
    public async Task EndHostSessionAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
            Host = null;
        }

        await _hostToGateway.Writer.CompleteAsync();
    }

    // Completes once host calls have started waiting on a definition's verdict count times; a call that waits again after
    // a newer definition was published counts again.
    public Task WaitForWaitingCallsAsync(int count)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (_waited >= count)
            {
                return Task.CompletedTask;
            }

            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waitingCalls.Add((count, signal));
        }

        return signal.Task.WaitAsync(Deadline);
    }

    // The gateway's stderr, one line per entry.
    public IReadOnlyList<string> LogLines => Log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    // Whether the gateway wrote anything at all towards the host. Call it only once the gateway has stopped.
    public async Task<bool> HostReceivedAnythingAsync()
    {
        await Gateway.WaitAsync(Deadline);
        ReadResult read = await _gatewayToHost.Reader.ReadAsync(Deadline);
        return !read.Buffer.IsEmpty;
    }

    public async ValueTask DisposeAsync()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await EndHostSessionAsync();

        // The host ending ends the gateway, as the deadline does; a gateway that already stopped returns at once.
        await Gateway.WaitAsync(token);
        await _hostSide.DisposeAsync();

        // A gateway that never connected upstream never stopped it.
        await _gatewayToUpstream.Writer.CompleteAsync();
        await Upstream.DisposeAsync();
        try
        {
            await _upstreamRun.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            // The upstream's run ends by cancellation when the test's token stops it.
        }

        _deadline.Dispose();
    }

    // The gateway's stdout closes when it stops, so a host still waiting for an answer fails instead of waiting on.
    private async Task<int> RunGatewayAsync(string command, IClientTransport upstream)
    {
        try
        {
            return await GatewayProxy.RunAsync(command, _hostSide, upstream, _composition, Log, _environment, OnCallWaiting, Deadline);
        }
        finally
        {
            await _gatewayToHost.Writer.CompleteAsync();
        }
    }

    private void OnCallWaiting(string tool)
    {
        lock (_lock)
        {
            _waited++;
            _waitingCalls.RemoveAll(waiter =>
            {
                if (_waited < waiter.Count)
                {
                    return false;
                }

                waiter.Signal.TrySetResult();
                return true;
            });
        }
    }

    // The protocol's own serialization, so two values compare as the wire would carry them.
    public static string Wire<T>(T value) => JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions);
}
