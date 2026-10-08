using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SemanticPolicy.Mcp.Gateway.Tests.Support;
using static SemanticPolicy.Mcp.Gateway.Tests.Support.GatewayHarness;

// Sampling and roots are deprecated from the 2026-07-28 revision on; at 2025-06-18, the revision these sessions run, a host
// still declares them, and an upstream can still ask for them.
#pragma warning disable MCP9005

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed class SessionBoundaryTests
{
    private const string _revision = "2025-06-18";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // The SDK's default client asks for a newer revision first and falls back when the server refuses it.
    [Theory]
    [InlineData("pinned")]
    [InlineData("default")]
    public async Task Host_And_Upstream_Both_Negotiate_2025_06_18(string host)
    {
        McpClientOptions? options = host == "pinned" ? new McpClientOptions { ProtocolVersion = _revision } : null;

        await using GatewayHarness gateway = await ConnectAsync(new ToolsUpstream([ToolsUpstream.Lookup]).Options(), options);

        gateway.Host!.NegotiatedProtocolVersion.Should().Be(_revision);
        gateway.Upstream.NegotiatedProtocolVersion.Should().Be(_revision);
    }

    [Theory]
    [InlineData("host declares sampling, roots and elicitation")]
    [InlineData("reserved _meta key on a tools/call")]
    public async Task Upstream_Sees_None_Of_The_Hosts_Capabilities_Or_Protocol_Metadata(string host)
    {
        ToolsUpstream upstream = new([ToolsUpstream.Lookup]);
        bool declares = host == "host declares sampling, roots and elicitation";
        McpClientOptions? options = declares ? new McpClientOptions { Handlers = AnsweringHandlers(new HostRequests()) } : null;
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), options);

        if (declares)
        {
            ClientCapabilities seen = gateway.Upstream.ClientCapabilities!;
            seen.Sampling.Should().BeNull();
            seen.Roots.Should().BeNull();
            seen.Elicitation.Should().BeNull();
            return;
        }

        CallToolRequestParams call = new()
        {
            Name = ToolsUpstream.Lookup.Name,
            Meta = new JsonObject { ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject() },
        };

        Func<Task> act = async () => await gateway.Host!.CallToolAsync(call, Token);

        await act.Should().ThrowAsync<McpException>();

        // An ordinary call afterwards shows what crossing looks like, so the one tools/call seen upstream is that one.
        await gateway.Host!.CallToolAsync(ToolsUpstream.Lookup.Name, cancellationToken: Token);
        gateway.UpstreamSide.SentRequests.Where(method => method == RequestMethods.ToolsCall).Should().ContainSingle();
    }

    [Theory]
    [InlineData(RequestMethods.ElicitationCreate)]
    [InlineData(RequestMethods.SamplingCreateMessage)]
    public async Task Upstream_Request_To_The_Host_Fails_And_Never_Reaches_It(string method)
    {
        // Sent raw, past the upstream SDK's own check of the client's capabilities, so it reaches the gateway.
        JsonObject parameters = method == RequestMethods.ElicitationCreate
            ? new JsonObject { ["message"] = "question-a", ["requestedSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }
            : new JsonObject
            {
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "message-a" } }),
                ["maxTokens"] = 10,
            };
        McpServerOptions upstream = new()
        {
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [ToolsUpstream.Lookup] }),
                CallToolHandler = async (request, cancellationToken) =>
                {
                    string outcome;
                    try
                    {
                        await request.Server.SendRequestAsync(new JsonRpcRequest { Method = method, Params = parameters }, cancellationToken);
                        outcome = "answered";
                    }
                    catch (McpException)
                    {
                        outcome = "failed";
                    }

                    return new CallToolResult { Content = [new TextContentBlock { Text = outcome }] };
                },
            },
        };
        HostRequests received = new();
        await using GatewayHarness gateway = await ConnectAsync(upstream, new McpClientOptions { Handlers = AnsweringHandlers(received) });

        CallToolResult result = await gateway.Host!.CallToolAsync(ToolsUpstream.Lookup.Name, cancellationToken: Token);

        result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text.Should().Be("failed");
        received.Count.Should().Be(0);
    }

    [Fact]
    public async Task Cancelled_Host_Call_Cancels_The_Upstream_Call()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using GatewayHarness gateway = await ConnectAsync(HangingUpstream(started, cancelled));
        JsonRpcRequest call = new()
        {
            Id = new RequestId("call-a"),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(new CallToolRequestParams { Name = ToolsUpstream.Lookup.Name }, McpJsonUtilities.DefaultOptions),
        };
        using CancellationTokenSource host = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task pending = gateway.Host!.SendRequestAsync(call, host.Token);
        await started.Task.WaitAsync(gateway.Deadline);

        // A host announces the call it gives up on. The SDK's client stops waiting without announcing it, so this host
        // announces it itself.
        await host.CancelAsync();
        await gateway.Host.SendNotificationAsync(
            NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = call.Id },
            cancellationToken: Token);

        await cancelled.Task.WaitAsync(gateway.Deadline);
        await pending.Awaiting(task => task).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Host_Ending_The_Session_Ends_The_Gateway_With_Exit_Code_Zero()
    {
        await using GatewayHarness gateway = await ConnectAsync(new ToolsUpstream([ToolsUpstream.Lookup]).Options());

        await gateway.EndHostSessionAsync();

        (await gateway.Gateway.WaitAsync(gateway.Deadline)).Should().Be(ExitCodes.Success);
        await gateway.UpstreamRun.WaitAsync(gateway.Deadline);
        gateway.Log.ToString().Should().BeEmpty();
    }

    // The SDK's server stops reading when the host's input ends, but goes on waiting for the calls it is serving, and
    // an upstream call may never be answered.
    [Fact]
    public async Task Host_Ending_The_Session_During_A_Call_Cancels_The_Upstream_Call_And_Ends_The_Gateway()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using GatewayHarness gateway = await ConnectAsync(HangingUpstream(started, cancelled));
        Task pending = gateway.Host!.CallToolAsync(ToolsUpstream.Lookup.Name, cancellationToken: Token).AsTask();
        await started.Task.WaitAsync(gateway.Deadline);

        await gateway.EndHostSessionAsync();

        // The host that went away stopped waiting for its answer.
        await pending.Awaiting(task => task).Should().ThrowAsync<OperationCanceledException>();
        (await gateway.Gateway.WaitAsync(gateway.Deadline)).Should().Be(ExitCodes.Success);
        await cancelled.Task.WaitAsync(gateway.Deadline);
        gateway.Log.ToString().Should().BeEmpty();
    }

    // An upstream whose one tool never answers: it signals when a call starts and when that call is cancelled.
    private static McpServerOptions HangingUpstream(TaskCompletionSource started, TaskCompletionSource cancelled) => new()
    {
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [ToolsUpstream.Lookup] }),
            CallToolHandler = async (_, cancellationToken) =>
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(() => cancelled.TrySetResult());
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new CallToolResult();
            },
        },
    };

    // Handlers that would answer each server-to-client request, so the host declares all three capabilities and
    // counts any such request that reaches it.
    private static McpClientHandlers AnsweringHandlers(HostRequests received) => new()
    {
        SamplingHandler = (_, _, _) =>
        {
            received.Add();
            return ValueTask.FromResult(new CreateMessageResult { Model = "model-a", Role = Role.Assistant, Content = [new TextContentBlock { Text = "answer-a" }] });
        },
        RootsHandler = (_, _) =>
        {
            received.Add();
            return ValueTask.FromResult(new ListRootsResult { Roots = [] });
        },
        ElicitationHandler = (_, _) =>
        {
            received.Add();
            return ValueTask.FromResult(new ElicitResult { Action = "decline" });
        },
    };

    private sealed class HostRequests
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add() => Interlocked.Increment(ref _count);
    }
}
