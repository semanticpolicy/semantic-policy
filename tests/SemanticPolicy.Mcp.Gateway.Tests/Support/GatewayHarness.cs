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

    private GatewayHarness(McpServerOptions upstreamOptions, Func<GatewayHarness, IClientTransport>? upstream, string command)
    {
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
        new(upstreamOptions, upstream, command);

    // Starts the upstream and the gateway, then connects a host through the gateway.
    public static async Task<GatewayHarness> ConnectAsync(McpServerOptions upstreamOptions, McpClientOptions? hostOptions = null)
    {
        GatewayHarness harness = new(upstreamOptions, upstream: null, Command);
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
            return await GatewayProxy.RunAsync(command, _hostSide, upstream, Log, Deadline);
        }
        finally
        {
            await _gatewayToHost.Writer.CompleteAsync();
        }
    }

    // The protocol's own serialization, so two values compare as the wire would carry them.
    public static string Wire<T>(T value) => JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions);
}
