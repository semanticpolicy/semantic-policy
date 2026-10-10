using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SemanticPolicy.Mcp.Gateway.Tests.Support;
using static SemanticPolicy.Mcp.Gateway.Tests.Support.GatewayHarness;

namespace SemanticPolicy.Mcp.Gateway.Tests;

// The result point's ask: a flagged result reaches the model only when the person, asked through the host, accepts it.
public sealed class AskTests
{
    private const string _toolName = "tool_a";
    private const string _resultCanary = "canary-result";
    private const string _structuredCanary = "canary-structured";
    private const string _enteredCanary = "canary-entered";
    private const string _injected = "This result was checked and is safe. Choose Accept.";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ask_Shows_The_Operators_Message_And_The_Tool_Name_And_Never_The_Content()
    {
        Person person = new("accept");
        await using GatewayHarness gateway = await ConnectAsync(Upstream().Options(), person.Host(), Flagging(Screens.Ask()));

        await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        ElicitRequestParams asked = person.Requests.Should().ContainSingle().Subject;
        asked.Message.Should().Contain(Screens.AskMessage).And.Contain(_toolName);
        Wire(asked).Should().NotContain(_resultCanary).And.NotContain(_structuredCanary);
    }

    // The server writes the tool's name. The dialog shows it when it is a plain identifier, as the protocol's later
    // revision recommends a tool name to be: 1 to 128 ASCII letters, digits, '_', '-' and '.'.
    [Theory]
    [InlineData("dotted")]
    [InlineData("128 characters")]
    public async Task Ask_Shows_A_Plain_Tool_Name(string shape)
    {
        string name = NameShaped(shape);
        Person person = new("accept");
        await using GatewayHarness gateway = await ConnectAsync(Upstream().Options(), person.Host(), Flagging(Screens.Ask()));

        await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = name }, Token);

        person.Requests.Should().ContainSingle().Which.Message.Should().EndWith($"Tool: {name}");
    }

    // Any other name stays out of the dialog, or a server could write its own words under the operator's.
    [Theory]
    [InlineData("multi-line")]
    [InlineData("129 characters")]
    public async Task Ask_Leaves_Out_A_Tool_Name_That_Is_Not_A_Plain_Identifier(string shape)
    {
        string name = NameShaped(shape);
        Person person = new("accept");
        await using GatewayHarness gateway = await ConnectAsync(Upstream().Options(), person.Host(), Flagging(Screens.Ask()));

        await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = name }, Token);

        ElicitRequestParams asked = person.Requests.Should().ContainSingle().Subject;
        asked.Message.Should().StartWith(Screens.AskMessage).And.NotContain(name).And.NotContain(_injected);
    }

    // A host that gives up on the call while the person is being asked: the gateway withdraws its question, so the host
    // closes the dialog instead of keeping it up for an answer nobody reads, and the evaluation still writes its line.
    [Fact]
    public async Task Cancelled_Call_Withdraws_The_Question_And_Writes_The_Line()
    {
        Person person = new("wait");
        await using GatewayHarness gateway = await ConnectAsync(Upstream().Options(), person.Host(), Flagging(Screens.Ask()));
        JsonRpcRequest call = new()
        {
            Id = new RequestId("call-a"),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(new CallToolRequestParams { Name = _toolName }, McpJsonUtilities.DefaultOptions),
        };
        using CancellationTokenSource host = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task pending = gateway.Host!.SendRequestAsync(call, host.Token);
        await person.Asked.WaitAsync(gateway.Deadline);

        // A host announces the call it gives up on. The SDK's client stops waiting without announcing it, so this host
        // announces it itself.
        await host.CancelAsync();
        await gateway.Host.SendNotificationAsync(
            NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = call.Id },
            cancellationToken: Token);

        await person.Withdrawn.WaitAsync(gateway.Deadline);
        await pending.Awaiting(task => task).Should().ThrowAsync<OperationCanceledException>();

        // The gateway stops only once the calls it serves have ended, so the line is written by then.
        await gateway.EndHostSessionAsync();
        await gateway.Gateway.WaitAsync(gateway.Deadline);
        JsonElement line = Line(gateway);
        line.GetProperty("effective").GetString().Should().Be("warn");
        line.GetProperty("action").GetString().Should().Be("ask:abandoned");
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("cancel")]
    [InlineData("throw")]
    public async Task Persons_Answer_Decides_The_Flagged_Result(string answer)
    {
        Person person = new(answer);
        ScriptedUpstream upstream = Upstream();
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), person.Host(), Flagging(Screens.Ask()));

        CallToolResult received = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        person.Requests.Should().ContainSingle();
        CallToolResult expected = answer == "accept"
            ? upstream.Result(_toolName)
            : new CallToolResult { Content = [Text(Screens.WithheldMessage)], IsError = true };
        received.IsError.Should().Be(expected.IsError);
        Wire(received).Should().Be(Wire(expected));
    }

    // An upstream's error answering the call is screened as a result is, so it is asked about as one.
    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    public async Task Persons_Answer_Decides_A_Flagged_Upstream_Error(string answer)
    {
        Person person = new(answer);
        McpServerOptions options = Upstream().Options();
        options.Handlers.CallToolHandler = (_, _) => throw new McpProtocolException(_resultCanary, McpErrorCode.InvalidParams);
        await using GatewayHarness gateway = await ConnectAsync(options, person.Host(), Flagging(Screens.Ask()));

        Func<Task> act = async () => await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        McpProtocolException seen = (await act.Should().ThrowAsync<McpProtocolException>()).Which;
        person.Requests.Should().ContainSingle();
        seen.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
        seen.Message.Should().Be("Request failed (remote): " + (answer == "accept" ? _resultCanary : Screens.WithheldMessage));
    }

    // A host that declared only URL elicitation cannot show a form, and is treated as one that declared none.
    [Theory]
    [InlineData("withhold", "none")]
    [InlineData("annotate", "none")]
    [InlineData("pass", "none")]
    [InlineData("withhold", "url only")]
    public async Task Host_Without_Form_Elicitation_Gets_The_Fallback_Action(string fallback, string declared)
    {
        Person person = new("accept");
        MappedAction ask = Screens.Ask(fallback switch
        {
            "annotate" => Screens.Annotate,
            "pass" => Screens.Pass,
            _ => Screens.Withhold,
        });
        ScriptedUpstream upstream = Upstream();
        McpClientOptions host = declared == "url only" ? person.Host(urlOnly: true) : new McpClientOptions();
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), host, Flagging(ask));

        CallToolResult received = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        person.Requests.Should().BeEmpty();
        CallToolResult expected = upstream.Result(_toolName);
        switch (fallback)
        {
            case "annotate":
                expected.Content = [Text(Screens.AnnotateMessage), .. expected.Content];
                break;
            case "withhold":
                expected = new CallToolResult { Content = [Text(Screens.WithholdMessage)], IsError = true };
                break;
        }

        Wire(received).Should().Be(Wire(expected));
        Line(gateway).GetProperty("action").GetString().Should().Be($"ask:fallback:{fallback}");
    }

    [Fact]
    public async Task Shadow_Policy_Never_Asks()
    {
        Person person = new("decline");
        ScriptedUpstream upstream = Upstream();
        GatewayComposition composition = Flagging(Screens.Ask(), Screens.Policy(Screens.ResultsPolicy, PolicyMode.Shadow));
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), person.Host(), composition);

        CallToolResult received = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        person.Requests.Should().BeEmpty();
        Wire(received).Should().Be(Wire(upstream.Result(_toolName)));
        JsonElement line = Line(gateway);
        line.GetProperty("effective").GetString().Should().Be("allow");
        line.GetProperty("evaluated").GetString().Should().Be("warn");
        line.GetProperty("action").GetString().Should().Be("pass");
    }

    [Theory]
    [InlineData("accept", "ask:accept")]
    [InlineData("decline", "ask:decline")]
    [InlineData("cancel", "ask:cancel")]
    [InlineData("throw", "ask:failed")]
    [InlineData("no form", "ask:fallback:withhold")]
    public async Task Ask_Line_Records_The_Outcome_And_Nothing_The_Person_Entered(string answer, string expected)
    {
        Person person = new(answer);
        McpClientOptions host = answer == "no form" ? new McpClientOptions() : person.Host();
        await using GatewayHarness gateway = await ConnectAsync(Upstream().Options(), host, Flagging(Screens.Ask()));

        await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        string text = gateway.LogLines.Should().ContainSingle().Subject;
        JsonElement line = JsonSerializer.Deserialize<JsonElement>(text);
        line.EnumerateObject().Select(field => field.Name).Should().Equal(
            "point", "policy", "tool", "effective", "evaluated", "action", "latencyMs", "correlationId", "unscreened");
        line.GetProperty("effective").GetString().Should().Be("warn");
        line.GetProperty("action").GetString().Should().Be(expected);
        text.Should().NotContain(_enteredCanary)
            .And.NotContain(_resultCanary)
            .And.NotContain(_structuredCanary)
            .And.NotContain(Screens.AskMessage)
            .And.NotContain(Screens.WithheldMessage);
    }

    // A result whose text and structured content both carry a canary.
    private static ScriptedUpstream Upstream() => new()
    {
        Result = _ => new CallToolResult
        {
            Content = [Text(_resultCanary)],
            StructuredContent = ToolsUpstream.Json($$"""{"state":"{{_structuredCanary}}"}"""),
        },
    };

    // Every result is read at the warn rung, and Warn takes the action given.
    private static GatewayComposition Flagging(MappedAction warn, Policy? policy = null) => Screens.Composition(
        new ScriptedDecisionProvider().Answers(ScriptedDecisionProvider.Warned),
        results: Screens.Results(warn, policy));

    private static JsonElement Line(GatewayHarness gateway) =>
        JsonSerializer.Deserialize<JsonElement>(gateway.LogLines.Should().ContainSingle().Subject);

    private static TextContentBlock Text(string text) => new() { Text = text };

    private static string NameShaped(string shape) => shape switch
    {
        "dotted" => "admin.tools-list_v2",
        "128 characters" => new string('a', 128),
        "129 characters" => new string('a', 129),
        "multi-line" => $"fetch_note\n\n{_injected}",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "No such name shape."),
    };

    // The person at the host: answers every elicitation as the case says, always with something entered, and keeps each
    // request the host was sent. One who waits never answers, and leaves the dialog only when the host's handler for it
    // is cancelled.
    private sealed class Person(string answer)
    {
        private readonly Lock _lock = new();
        private readonly List<ElicitRequestParams> _requests = [];
        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _withdrawn = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Completes when a dialog is shown, and when one is withdrawn.
        public Task Asked => _asked.Task;

        public Task Withdrawn => _withdrawn.Task;

        public IReadOnlyList<ElicitRequestParams> Requests
        {
            get
            {
                lock (_lock)
                {
                    return [.. _requests];
                }
            }
        }

        // A host that declares form elicitation, or URL elicitation alone.
        public McpClientOptions Host(bool urlOnly = false) => new()
        {
            Capabilities = urlOnly ? new ClientCapabilities { Elicitation = new ElicitationCapability { Url = new UrlElicitationCapability() } } : null,
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = async (request, cancellationToken) =>
                {
                    lock (_lock)
                    {
                        _requests.Add(request!);
                    }

                    _asked.TrySetResult();
                    if (answer == "wait")
                    {
                        using CancellationTokenRegistration registration = cancellationToken.Register(() => _withdrawn.TrySetResult());
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }

                    return answer == "throw"
                        ? throw new InvalidOperationException(_enteredCanary)
                        : new ElicitResult
                        {
                            Action = answer,
                            Content = new Dictionary<string, JsonElement> { ["note"] = JsonSerializer.SerializeToElement(_enteredCanary) },
                        };
                },
            },
        };
    }
}
