using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SemanticPolicy.Mcp.Gateway;

/// <summary>
/// The proxy between one MCP host and one upstream MCP server. It opens the upstream session first, then serves the
/// host exactly what the upstream declared, forwarding each request, result and notification unchanged. Both hops run
/// <see cref="ProtocolRevision"/> and nothing else, so nothing has to be translated between them.
/// </summary>
public static class GatewayProxy
{
    /// <summary>The one MCP revision both hops run.</summary>
    public const string ProtocolRevision = "2025-06-18";

    // What the upstream announces about its own lists and resources. Anything else it might send, such as a log
    // message or a progress update, stays on its own hop.
    private static readonly string[] _forwardedNotifications =
    [
        NotificationMethods.ToolListChangedNotification,
        NotificationMethods.ResourceListChangedNotification,
        NotificationMethods.PromptListChangedNotification,
        NotificationMethods.ResourceUpdatedNotification,
    ];

    // Strings, so they never meet the numbers the SDK's client gives the requests it sends on its own.
    private static long _lastRequestId;

    /// <summary>
    /// Opens the upstream session, then serves the host until the host ends its session or the upstream ends.
    /// </summary>
    /// <param name="command">The upstream server's command, for the gateway's own messages. Never its arguments.</param>
    /// <param name="host">The host's side: the gateway serves on it.</param>
    /// <param name="upstream">The upstream server's side: the gateway is its client.</param>
    /// <param name="log">The gateway's stderr.</param>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <returns>
    /// <see cref="ExitCodes.Success"/> when the host ends the session, <see cref="ExitCodes.UpstreamFailure"/> when the
    /// upstream fails to start or ends first.
    /// </returns>
    public static async Task<int> RunAsync(
        string command,
        ITransport host,
        IClientTransport upstream,
        TextWriter log,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(log);

        // A failure is reported in the gateway's words and nothing of the exception: the SDK's message for a child that
        // exited, and the exceptions inside it, carry the child's last stderr lines.
        ITransport session;
        try
        {
            session = await upstream.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(log, command, "could not be started", exitCode: null).ConfigureAwait(false);
        }

        // No logger factory reaches the SDK, here or below: its stdio transport logs every line the child writes to
        // stderr, and the command's arguments.
        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(
                new Opened(upstream.Name, session),
                new McpClientOptions { ProtocolVersion = ProtocolRevision },
                loggerFactory: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(
                log,
                command,
                "did not complete the MCP handshake",
                ExitCodeOf((failure as ClientTransportClosedException)?.Details)).ConfigureAwait(false);
        }

        await using (client)
        {
            return await ServeAsync(command, host, client, log, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> ServeAsync(
        string command,
        ITransport host,
        McpClient client,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        await using McpServer server = McpServer.Create(host, ServerOptions(client));

        // A handler the server serves never announces a list change itself, so the upstream's announcements are
        // re-sent through the server.
        List<IAsyncDisposable> forwarding = [.. _forwardedNotifications.Select(method => client.RegisterNotificationHandler(
            method,
            (notification, token) => new ValueTask(server.SendMessageAsync(
                new JsonRpcNotification { Method = notification.Method, Params = notification.Params?.DeepClone() },
                token))))];
        using CancellationTokenSource serving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            Task run = server.RunAsync(serving.Token);
            if (await Task.WhenAny(run, client.Completion).ConfigureAwait(false) == run)
            {
                await run.ConfigureAwait(false);
                return ExitCodes.Success;
            }

            // The upstream ended first. A host request still waiting on it has already failed with it.
            await serving.CancelAsync().ConfigureAwait(false);
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Serving stopped because the upstream had ended.
            }

            ClientCompletionDetails ended = await client.Completion.ConfigureAwait(false);
            return await FailAsync(log, command, "ended while the gateway was serving", ExitCodeOf(ended)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping from outside ends the session as the host ending it would.
            return ExitCodes.Success;
        }
        finally
        {
            foreach (IAsyncDisposable registration in forwarding)
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // The one message the gateway writes about its upstream. Of the SDK's details it takes the exit code alone: the
    // details also hold the child's last stderr lines.
    private static async Task<int> FailAsync(TextWriter log, string command, string step, int? exitCode)
    {
        string code = exitCode is int value ? $" (exit code {value})" : "";
        await log.WriteLineAsync(
            $"The upstream server '{command}' {step}{code}. Run its command on its own to see its messages.").ConfigureAwait(false);
        return ExitCodes.UpstreamFailure;
    }

    private static int? ExitCodeOf(ClientCompletionDetails? details) =>
        details is StdioClientCompletionDetails { ExitCode: int code } ? code : null;

    // The host is offered the upstream's own identity, instructions and capabilities, and a handler only for what the
    // upstream declared, because the server declares a capability for every handler it is given.
    private static McpServerOptions ServerOptions(McpClient upstream)
    {
        ServerCapabilities declared = upstream.ServerCapabilities;
        McpServerHandlers handlers = new();
        if (declared.Tools is not null)
        {
            handlers.ListToolsHandler = Forward<ListToolsRequestParams, ListToolsResult>(upstream, RequestMethods.ToolsList);
            handlers.CallToolHandler = Forward<CallToolRequestParams, CallToolResult>(upstream, RequestMethods.ToolsCall);
        }

        if (declared.Resources is not null)
        {
            handlers.ListResourcesHandler = Forward<ListResourcesRequestParams, ListResourcesResult>(upstream, RequestMethods.ResourcesList);
            handlers.ReadResourceHandler = Forward<ReadResourceRequestParams, ReadResourceResult>(upstream, RequestMethods.ResourcesRead);
            handlers.ListResourceTemplatesHandler = Forward<ListResourceTemplatesRequestParams, ListResourceTemplatesResult>(upstream, RequestMethods.ResourcesTemplatesList);
            handlers.SubscribeToResourcesHandler = Forward<SubscribeRequestParams, EmptyResult>(upstream, RequestMethods.ResourcesSubscribe);
            handlers.UnsubscribeFromResourcesHandler = Forward<UnsubscribeRequestParams, EmptyResult>(upstream, RequestMethods.ResourcesUnsubscribe);
        }

        if (declared.Prompts is not null)
        {
            handlers.ListPromptsHandler = Forward<ListPromptsRequestParams, ListPromptsResult>(upstream, RequestMethods.PromptsList);
            handlers.GetPromptHandler = Forward<GetPromptRequestParams, GetPromptResult>(upstream, RequestMethods.PromptsGet);
        }

        if (declared.Completions is not null)
        {
            handlers.CompleteHandler = Forward<CompleteRequestParams, CompleteResult>(upstream, RequestMethods.CompletionComplete);
        }

        return new McpServerOptions
        {
            ServerInfo = upstream.ServerInfo,
            ServerInstructions = upstream.ServerInstructions,
            ProtocolVersion = ProtocolRevision,
            Capabilities = new ServerCapabilities
            {
                Tools = declared.Tools is { } tools ? new ToolsCapability { ListChanged = tools.ListChanged } : null,
                Resources = declared.Resources is { } resources
                    ? new ResourcesCapability { Subscribe = resources.Subscribe, ListChanged = resources.ListChanged }
                    : null,
                Prompts = declared.Prompts is { } prompts ? new PromptsCapability { ListChanged = prompts.ListChanged } : null,
                Completions = declared.Completions is null ? null : new CompletionsCapability(),
            },
            Handlers = handlers,
        };
    }

    // The host's params go upstream as the protocol model holds them, _meta included, under a request id of the
    // gateway's own, never the host's. The host's revision and capabilities are not part of them: on this revision the
    // server refuses a request that carries the protocol's reserved metadata before any handler runs.
    private static McpRequestHandler<TParams, TResult> Forward<TParams, TResult>(McpClient upstream, string method)
        where TResult : notnull =>
        async (request, cancellationToken) =>
        {
            RequestId id = new($"gateway-{Interlocked.Increment(ref _lastRequestId)}");
            try
            {
                return await upstream.SendRequestAsync<TParams?, TResult>(
                    method,
                    request.Params,
                    requestId: id,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The SDK's client stops waiting for a cancelled request without telling the server, so the gateway
                // tells it; were the SDK to start telling it too, the protocol has the server ignore the second notice.
                try
                {
                    await upstream.SendNotificationAsync(
                        NotificationMethods.CancelledNotification,
                        new CancelledNotificationParams { RequestId = id },
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // An upstream that has gone cannot be told, and its ending stops the gateway anyway.
                }

                throw;
            }
        };

    // A session the gateway opened before the handshake, so a failed start and a failed handshake are told apart.
    private sealed class Opened(string name, ITransport session) : IClientTransport
    {
        public string Name => name;

        public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(session);
    }
}
