using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SemanticPolicy.Mcp.Gateway.Tests.Support;
using static SemanticPolicy.Mcp.Gateway.Tests.Support.GatewayHarness;

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed class PassThroughTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Each host request gets one page and its cursor, so the host decides whether and when to page on.
    [Theory]
    [InlineData("one page")]
    [InlineData("two pages")]
    public async Task Host_Lists_And_Calls_Upstream_Tools_Unchanged_Through_The_Gateway(string catalogue)
    {
        ToolsUpstream upstream = catalogue == "one page"
            ? new([ToolsUpstream.Lookup])
            : new([ToolsUpstream.Lookup], [ToolsUpstream.Archive]);
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options());

        List<Tool> listed = [];
        string? cursor = null;
        for (int page = 0; page < upstream.Pages.Count; page++)
        {
            ListToolsResult result = await gateway.Host!.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, Token);

            Wire(result.Tools).Should().Be(Wire(upstream.Pages[page]));
            result.NextCursor.Should().Be(page + 1 < upstream.Pages.Count ? ToolsUpstream.CursorOf(page + 1) : null);
            listed.AddRange(result.Tools);
            cursor = result.NextCursor;
        }

        upstream.Cursors.Should().Equal([null, .. Enumerable.Range(1, upstream.Pages.Count - 1).Select(ToolsUpstream.CursorOf)]);
        List<CallToolRequestParams> sent = [];
        foreach (Tool tool in listed)
        {
            CallToolRequestParams call = new()
            {
                Name = tool.Name,
                Arguments = new Dictionary<string, JsonElement> { ["id"] = ToolsUpstream.Json("\"order-1\"") },
                Meta = new JsonObject { [ToolsUpstream.TraceKey] = $"trace-call-{tool.Name}" },
            };
            sent.Add(call);

            CallToolResult result = await gateway.Host!.CallToolAsync(call, Token);

            Wire(result).Should().Be(Wire(ToolsUpstream.ResultOf(tool.Name)));
        }

        Wire(upstream.Calls).Should().Be(Wire(sent));
    }

    [Theory]
    [InlineData("resources list")]
    [InlineData("resource read")]
    [InlineData("templates list")]
    [InlineData("resource subscribe")]
    [InlineData("resource unsubscribe")]
    [InlineData("prompts list")]
    [InlineData("prompt get")]
    [InlineData("completion")]
    public async Task Upstream_Primitive_Reaches_The_Host_Unchanged(string primitive)
    {
        PrimitivesUpstream upstream = new();
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options());
        ModelContextProtocol.Client.McpClient host = gateway.Host!;

        switch (primitive)
        {
            case "resources list":
                Wire(await host.ListResourcesAsync(new ListResourcesRequestParams(), Token)).Should().Be(Wire(PrimitivesUpstream.Resources));
                break;
            case "resource read":
                Wire(await host.ReadResourceAsync(PrimitivesUpstream.Uri, cancellationToken: Token)).Should().Be(Wire(PrimitivesUpstream.Read));
                upstream.Seen.Should().Equal("resources/read " + PrimitivesUpstream.Uri);
                break;
            case "templates list":
                Wire(await host.ListResourceTemplatesAsync(new ListResourceTemplatesRequestParams(), Token)).Should().Be(Wire(PrimitivesUpstream.Templates));
                break;
            case "resource subscribe":
                await host.SubscribeToResourceAsync(PrimitivesUpstream.Uri, cancellationToken: Token);
                upstream.Seen.Should().Equal("resources/subscribe " + PrimitivesUpstream.Uri);
                break;
            case "resource unsubscribe":
                await host.UnsubscribeFromResourceAsync(PrimitivesUpstream.Uri, cancellationToken: Token);
                upstream.Seen.Should().Equal("resources/unsubscribe " + PrimitivesUpstream.Uri);
                break;
            case "prompts list":
                Wire(await host.ListPromptsAsync(new ListPromptsRequestParams(), Token)).Should().Be(Wire(PrimitivesUpstream.Prompts));
                break;
            case "prompt get":
                GetPromptResult prompt = await host.GetPromptAsync(
                    "prompt-a",
                    new Dictionary<string, object?> { ["topic"] = "topic-a" },
                    cancellationToken: Token);
                Wire(prompt).Should().Be(Wire(PrimitivesUpstream.Prompt));
                upstream.Seen.Should().Equal("prompts/get prompt-a topic-a");
                break;
            case "completion":
                CompleteResult completion = await host.CompleteAsync(
                    new PromptReference { Name = "prompt-a" },
                    "topic",
                    "top",
                    cancellationToken: Token);
                Wire(completion).Should().Be(Wire(PrimitivesUpstream.Completion));
                upstream.Seen.Should().Equal("completion/complete prompt-a topic top");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(primitive));
        }
    }

    // The host's own client words an error response once, as it would the upstream's answer to it directly.
    [Fact]
    public async Task Upstream_Error_Response_Reaches_The_Host_Unchanged()
    {
        McpServerOptions options = new ToolsUpstream([ToolsUpstream.Lookup]).Options();
        options.Handlers.CallToolHandler = (request, _) =>
        {
            McpProtocolException refusal = new($"Unknown tool: '{request.Params!.Name}'", McpErrorCode.InvalidParams);
            refusal.Data["detail"] = "detail-a";
            throw refusal;
        };
        await using GatewayHarness gateway = await ConnectAsync(options);

        Func<Task> act = async () => await gateway.Host!.CallToolAsync("tool-x", cancellationToken: Token);

        McpProtocolException seen = (await act.Should().ThrowAsync<McpProtocolException>()).Which;
        seen.Message.Should().Be("Request failed (remote): Unknown tool: 'tool-x'");
        seen.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
        seen.Data["detail"].Should().Be("detail-a");
    }

    [Fact]
    public async Task Host_Sees_The_Upstreams_Instructions_And_Server_Info()
    {
        Implementation info = new() { Name = "upstream-server-a", Title = "Upstream A", Version = "9.8.7", WebsiteUrl = "https://upstream.example" };
        McpServerOptions options = new ToolsUpstream([ToolsUpstream.Lookup]).Options();
        options.ServerInfo = info;
        options.ServerInstructions = "instructions-a";
        await using GatewayHarness gateway = await ConnectAsync(options);

        gateway.Host!.ServerInstructions.Should().Be("instructions-a");
        Wire(gateway.Host.ServerInfo).Should().Be(Wire(info));
    }

    // The four capabilities the gateway mirrors; logging is the host-side server's own.
    [Theory]
    [InlineData("tools only")]
    [InlineData("tools, resources and prompts")]
    [InlineData("resources with subscribe")]
    public async Task Host_Gets_Only_The_Capabilities_The_Upstream_Declared(string declared)
    {
        McpServerHandlers handlers = new ToolsUpstream([ToolsUpstream.Lookup]).Options().Handlers;
        PrimitivesUpstream primitives = new();
        (McpServerOptions Upstream, ServerCapabilities Expected) setup = declared switch
        {
            "tools only" => (
                new McpServerOptions
                {
                    Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } },
                    Handlers = handlers,
                },
                new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } }),
            "tools, resources and prompts" => (
                new McpServerOptions
                {
                    Capabilities = new ServerCapabilities { Prompts = new PromptsCapability { ListChanged = true } },
                    Handlers = new McpServerHandlers
                    {
                        ListToolsHandler = handlers.ListToolsHandler,
                        CallToolHandler = handlers.CallToolHandler,
                        ListResourcesHandler = primitives.Options().Handlers.ListResourcesHandler,
                        ListPromptsHandler = primitives.Options().Handlers.ListPromptsHandler,
                    },
                },
                new ServerCapabilities
                {
                    Tools = new ToolsCapability(),
                    Resources = new ResourcesCapability(),
                    Prompts = new PromptsCapability { ListChanged = true },
                }),
            "resources with subscribe" => (
                new McpServerOptions
                {
                    Capabilities = new ServerCapabilities { Resources = new ResourcesCapability { Subscribe = true, ListChanged = true } },
                    Handlers = new McpServerHandlers { ListResourcesHandler = primitives.Options().Handlers.ListResourcesHandler },
                },
                new ServerCapabilities { Resources = new ResourcesCapability { Subscribe = true, ListChanged = true } }),
            _ => throw new ArgumentOutOfRangeException(nameof(declared)),
        };
        await using GatewayHarness gateway = await ConnectAsync(setup.Upstream);

        ServerCapabilities seen = gateway.Host!.ServerCapabilities;
        ServerCapabilities mirrored = new()
        {
            Tools = seen.Tools,
            Resources = seen.Resources,
            Prompts = seen.Prompts,
            Completions = seen.Completions,
        };
        Wire(mirrored).Should().Be(Wire(setup.Expected));
    }

    [Theory]
    [InlineData(NotificationMethods.ToolListChangedNotification)]
    [InlineData(NotificationMethods.ResourceListChangedNotification)]
    [InlineData(NotificationMethods.PromptListChangedNotification)]
    [InlineData(NotificationMethods.ResourceUpdatedNotification)]
    public async Task Upstream_Notification_Reaches_The_Host(string method)
    {
        McpServerOptions options = new PrimitivesUpstream().Options();
        options.Handlers.ListToolsHandler = new ToolsUpstream([ToolsUpstream.Lookup]).Options().Handlers.ListToolsHandler;
        options.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability { ListChanged = true },
            Resources = new ResourcesCapability { Subscribe = true, ListChanged = true },
            Prompts = new PromptsCapability { ListChanged = true },
        };
        await using GatewayHarness gateway = await ConnectAsync(options);
        TaskCompletionSource<JsonRpcNotification> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using IAsyncDisposable registration = gateway.Host!.RegisterNotificationHandler(method, (notification, _) =>
        {
            received.TrySetResult(notification);
            return ValueTask.CompletedTask;
        });

        if (method == NotificationMethods.ResourceUpdatedNotification)
        {
            await gateway.Upstream.SendNotificationAsync(method, new ResourceUpdatedNotificationParams { Uri = PrimitivesUpstream.Uri }, cancellationToken: Token);
        }
        else
        {
            await gateway.Upstream.SendNotificationAsync(method, Token);
        }

        JsonRpcNotification notification = await received.Task.WaitAsync(gateway.Deadline);
        if (method == NotificationMethods.ResourceUpdatedNotification)
        {
            notification.Params!["uri"]!.GetValue<string>().Should().Be(PrimitivesUpstream.Uri);
        }
    }

    // Resources, templates, prompts and completion, answered from fixed values, keeping what each request named.
    private sealed class PrimitivesUpstream
    {
        public const string Uri = "test://resource/a";

        private readonly Lock _lock = new();
        private readonly List<string> _seen = [];

        public static ListResourcesResult Resources { get; } = new()
        {
            Resources = [new Resource { Uri = Uri, Name = "resource-a", Description = "description-c", MimeType = "text/plain" }],
        };

        public static ReadResourceResult Read { get; } = new()
        {
            Contents = [new TextResourceContents { Uri = Uri, MimeType = "text/plain", Text = "content-a" }],
        };

        public static ListResourceTemplatesResult Templates { get; } = new()
        {
            ResourceTemplates = [new ResourceTemplate { UriTemplate = "test://resource/{id}", Name = "template-a", Description = "description-f" }],
        };

        public static ListPromptsResult Prompts { get; } = new()
        {
            Prompts = [new Prompt { Name = "prompt-a", Description = "description-d", Arguments = [new PromptArgument { Name = "topic", Required = true }] }],
        };

        public static GetPromptResult Prompt { get; } = new()
        {
            Description = "description-e",
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = "message-a" } }],
        };

        public static CompleteResult Completion { get; } = new()
        {
            Completion = new Completion { Values = ["topic-a", "topic-b"], Total = 2, HasMore = false },
        };

        // Each entry is the method and what the request named.
        public IReadOnlyList<string> Seen
        {
            get
            {
                lock (_lock)
                {
                    return [.. _seen];
                }
            }
        }

        public McpServerOptions Options() => new()
        {
            Capabilities = new ServerCapabilities { Resources = new ResourcesCapability { Subscribe = true } },
            Handlers = new McpServerHandlers
            {
                ListResourcesHandler = (_, _) => ValueTask.FromResult(Resources),
                ReadResourceHandler = (request, _) => Answer($"resources/read {request.Params!.Uri}", Read),
                ListResourceTemplatesHandler = (_, _) => ValueTask.FromResult(Templates),
                SubscribeToResourcesHandler = (request, _) => Answer($"resources/subscribe {request.Params!.Uri}", new EmptyResult()),
                UnsubscribeFromResourcesHandler = (request, _) => Answer($"resources/unsubscribe {request.Params!.Uri}", new EmptyResult()),
                ListPromptsHandler = (_, _) => ValueTask.FromResult(Prompts),
                GetPromptHandler = (request, _) => Answer(
                    $"prompts/get {request.Params!.Name} {request.Params.Arguments!["topic"].GetString()}",
                    Prompt),
                CompleteHandler = (request, _) => Answer(
                    $"completion/complete {((PromptReference)request.Params!.Ref).Name} {request.Params.Argument.Name} {request.Params.Argument.Value}",
                    Completion),
            },
        };

        private ValueTask<T> Answer<T>(string seen, T result)
        {
            lock (_lock)
            {
                _seen.Add(seen);
            }

            return ValueTask.FromResult(result);
        }
    }
}
