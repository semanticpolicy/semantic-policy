using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SemanticPolicy.Extensions.AI.Tests.Support;

namespace SemanticPolicy.Extensions.AI.Tests;

public sealed class McpToolTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mcp_Tool_Call_Reaches_The_Before_Tool_Handler_With_The_Servers_Name_And_Description()
    {
        await using InProcessMcp mcp = await InProcessMcp.StartAsync(Token);
        ScriptedChatClient client = OneCallThenText(InProcessMcp.Plain);
        ToolCall? seen = null;
        PreToolHandler handler = (call, _, _) =>
        {
            seen = call;
            return ValueTask.FromResult(PreToolOutcome.Proceed);
        };
        IChatClient guarded = BeforeTool(client, handler);

        await guarded.GetResponseAsync("request-1", ScriptedLoop.With(await mcp.ListToolsAsync(Token)), Token);

        seen.Should().NotBeNull();
        seen!.Name.Should().Be(InProcessMcp.Plain);
        seen.Description.Should().Be(InProcessMcp.PlainDescription);
        JsonProperty argument = seen.Arguments.EnumerateObject().Should().ContainSingle().Which;
        argument.Name.Should().Be("name");
        argument.Value.GetString().Should().Be("test-old");
        seen.CorrelationId.Should().Be("call_1");
        mcp.Invocations.Should().Be(1);
    }

    [Theory]
    [InlineData("Refuse")]
    [InlineData("Stop")]
    public async Task Refused_Mcp_Tool_Call_Never_Reaches_The_Server(string outcome)
    {
        await using InProcessMcp mcp = await InProcessMcp.StartAsync(Token);
        ScriptedChatClient client = OneCallThenText(InProcessMcp.Plain);
        bool stops = outcome == "Stop";
        PreToolOutcome refusal = stops ? PreToolOutcome.Stop("stopped-1") : PreToolOutcome.Refuse("refused-1");
        IChatClient guarded = BeforeTool(client, (_, _, _) => ValueTask.FromResult(refusal));

        ChatResponse response = await guarded.GetResponseAsync("request-1", ScriptedLoop.With(await mcp.ListToolsAsync(Token)), Token);

        mcp.Invocations.Should().Be(0);
        if (stops)
        {
            client.Requests.Should().ContainSingle();
            ToolResults.TextOf(ToolResults.In(response.Messages).Should().ContainSingle().Which.Result)
                .Should().Be("stopped-1");
        }
        else
        {
            client.Requests.Should().HaveCount(2);
            ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result)
                .Should().Be("refused-1");
            response.Text.Should().Be("final-1");
        }
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("structured")]
    [InlineData("error")]
    public async Task Mcp_Tool_Result_Reaches_The_After_Tool_Handler_As_The_Loop_Hands_It_Over(string kind)
    {
        string tool = kind switch
        {
            "plain" => InProcessMcp.Plain,
            "structured" => InProcessMcp.Structured,
            _ => InProcessMcp.Error,
        };
        await using InProcessMcp mcp = await InProcessMcp.StartAsync(Token);
        ScriptedChatClient client = OneCallThenText(tool);
        ToolResult? seen = null;
        PostToolHandler handler = (result, _, _) =>
        {
            seen = result;
            return ValueTask.FromResult(PostToolOutcome.Proceed);
        };
        IChatClient guarded = AfterTool(client, handler);

        await guarded.GetResponseAsync("request-1", ScriptedLoop.With(await mcp.ListToolsAsync(Token)), Token);

        mcp.Invocations.Should().Be(1);
        seen.Should().NotBeNull();
        seen!.Call.Name.Should().Be(tool);
        seen.Call.CorrelationId.Should().Be("call_1");
        switch (kind)
        {
            case "plain":
                seen.Value.Should().BeOfType<TextContent>().Which.Text.Should().Be("plain-ok");
                break;
            case "structured":
                JsonElement structured = seen.Value.Should().BeOfType<JsonElement>().Which;
                JsonElement content = structured.GetProperty("structuredContent");
                content.GetProperty("name").GetString().Should().Be("test-old");
                content.GetProperty("state").GetString().Should().Be("structured-ok");
                structured.TryGetProperty("isError", out _).Should().BeFalse();
                break;
            default:
                JsonElement error = seen.Value.Should().BeOfType<JsonElement>().Which;
                error.GetProperty("isError").GetBoolean().Should().BeTrue();
                error.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("error-1");
                break;
        }
    }

    [Fact]
    public async Task Replaced_Mcp_Tool_Result_Is_What_The_Model_Receives()
    {
        await using InProcessMcp mcp = await InProcessMcp.StartAsync(Token);
        ScriptedChatClient client = OneCallThenText(InProcessMcp.Plain);
        IChatClient guarded = AfterTool(client, (_, _, _) => ValueTask.FromResult(PostToolOutcome.Replace("replacement-1")));

        ChatResponse response = await guarded.GetResponseAsync("request-1", ScriptedLoop.With(await mcp.ListToolsAsync(Token)), Token);

        mcp.Invocations.Should().Be(1);
        ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result)
            .Should().Be("replacement-1");
        response.Text.Should().Be("final-1");
    }

    private static ScriptedChatClient OneCallThenText(string tool) =>
        new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", tool, ScriptedLoop.Named("test-old")),
            ScriptedTurn.Says("final-1"));

    private static IChatClient BeforeTool(ScriptedChatClient client, PreToolHandler handler) =>
        new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), ScriptedLoop.Evaluator(ScriptedLoop.Flagging()), handler)
            .UseFunctionInvocation()
            .Build();

    private static IChatClient AfterTool(ScriptedChatClient client, PostToolHandler handler) =>
        new ChatClientBuilder(client)
            .UseSemanticPolicyAfterTool(ScriptedLoop.DenyPolicy(), ScriptedLoop.Evaluator(ScriptedLoop.Flagging()), handler)
            .UseFunctionInvocation()
            .Build();

    /// <summary>
    /// A real MCP server and client in this process, joined by two pipes, with three tools on the
    /// server that count every invocation they receive. Nothing listens on a port or starts a process.
    /// </summary>
    private sealed class InProcessMcp : IAsyncDisposable
    {
        public const string Plain = "plain_tool";
        public const string PlainDescription = "Answers with plain text.";
        public const string Structured = "structured_tool";
        public const string Error = "error_tool";

        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private readonly McpServer _server;
        private Task _serverRun = Task.CompletedTask;
        private McpClient? _client;
        private int _invocations;

        private InProcessMcp()
        {
            _server = McpServer.Create(
                new StreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream()),
                new McpServerOptions
                {
                    ToolCollection =
                    [
                        Tool(Plain, PlainDescription, _ => "plain-ok"),
                        Tool(Structured, "Answers with structured content.", name => new CallToolResult
                        {
                            Content = [new TextContentBlock { Text = "structured-ok" }],
                            StructuredContent = JsonSerializer.SerializeToElement(
                                new Dictionary<string, string?> { ["name"] = name, ["state"] = "structured-ok" }),
                        }),
                        Tool(Error, "Answers with an error.", _ => new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = "error-1" }],
                        }),
                    ],
                });
        }

        /// <summary>How many calls reached a tool on the server.</summary>
        public int Invocations => Volatile.Read(ref _invocations);

        public static async Task<InProcessMcp> StartAsync(CancellationToken cancellationToken)
        {
            InProcessMcp mcp = new();
            mcp._serverRun = mcp._server.RunAsync(cancellationToken);
            mcp._client = await McpClient.CreateAsync(
                new StreamClientTransport(mcp._clientToServer.Writer.AsStream(), mcp._serverToClient.Reader.AsStream()),
                cancellationToken: cancellationToken);
            return mcp;
        }

        /// <summary>The server's tools as the client lists them, each one an <see cref="AIFunction"/>.</summary>
        public async Task<IList<McpClientTool>> ListToolsAsync(CancellationToken cancellationToken) =>
            await _client!.ListToolsAsync(cancellationToken: cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            await _server.DisposeAsync();
            await _serverRun.WaitAsync(TestContext.Current.CancellationToken);
        }

        // The SDK turns a string into one text block and passes a CallToolResult through unchanged, so
        // the structured and error tools answer with exactly the result they build.
        private McpServerTool Tool<TResult>(string tool, string description, Func<string?, TResult> answer) =>
            McpServerTool.Create(
                (string? name) =>
                {
                    Interlocked.Increment(ref _invocations);
                    return answer(name);
                },
                new McpServerToolCreateOptions { Name = tool, Description = description });
    }
}
