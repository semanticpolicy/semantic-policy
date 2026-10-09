using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SemanticPolicy.Mcp.Gateway.Tests.Support;
using SemanticPolicy.Telemetry;
using static SemanticPolicy.Mcp.Gateway.Tests.Support.GatewayHarness;

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed class ScreeningOutputTests
{
    private const string _toolName = "tool_a";
    private const string _resultCanary = "canary-result";
    private const string _descriptionCanary = "canary-description";
    private const string _schemaCanary = "canary-schema";
    private const string _questionCanary = "canary-question";

    private static readonly string[] _canaries = [_resultCanary, _descriptionCanary, _schemaCanary, _questionCanary];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("result")]
    [InlineData("definition")]
    public async Task Shadow_Policy_Changes_Nothing_For_The_Host_But_Logs_The_Evaluated_Verdict(string point)
    {
        bool results = point == "result";
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider()
            .Answers(ScriptedDecisionProvider.Flagged)
            .Holds(results ? "result-a" : "description-a");
        ScriptedUpstream upstream = new ScriptedUpstream().Lists(ScriptedUpstream.Tool(_toolName, "description-a"));
        GatewayComposition composition = results
            ? Screens.Composition(provider, results: Screens.Results(policy: Screens.Policy(Screens.ResultsPolicy, PolicyMode.Shadow)))
            : Screens.Composition(provider, definitions: Screens.Definitions(Screens.Policy(Screens.DefinitionsPolicy, PolicyMode.Shadow)));
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: composition);

        Task<string> answer = results
            ? Task.Run(async () => Wire((await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token)).Content), Token)
            : Task.Run(async () => Wire((await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token)).Tools), Token);
        await provider.WaitForCallsAsync(1, gateway.Deadline);

        answer.IsCompleted.Should().BeFalse();
        provider.Release(results ? "result-a" : "description-a");
        (await answer.WaitAsync(gateway.Deadline)).Should().Be(results
            ? Wire(upstream.Result(_toolName).Content)
            : Wire(new List<Tool> { ScriptedUpstream.Tool(_toolName, "description-a") }));
        if (!results)
        {
            await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);
            upstream.Calls.Should().Equal(_toolName);
        }

        JsonElement line = JsonSerializer.Deserialize<JsonElement>(gateway.LogLines.Should().ContainSingle().Subject);
        line.GetProperty("point").GetString().Should().Be(point);
        line.GetProperty("effective").GetString().Should().Be("allow");
        line.GetProperty("evaluated").GetString().Should().Be("deny");
        line.GetProperty("action").GetString().Should().Be("pass");
    }

    [Fact]
    public async Task Log_Line_Carries_Metadata_And_No_Content()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Answers(ScriptedDecisionProvider.Warned);
        ScriptedUpstream upstream = Canaries();
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: CanaryComposition(provider));

        await SendAsync(gateway, "list-a", RequestMethods.ToolsList, new ListToolsRequestParams());
        await SendAsync(gateway, "call-a", RequestMethods.ToolsCall, new CallToolRequestParams { Name = _toolName });
        await gateway.EndHostSessionAsync();
        await gateway.Gateway.WaitAsync(gateway.Deadline);

        // Every line the gateway wrote is one of its lines: anything else on stderr would fail to parse here.
        JsonElement[] lines = [.. gateway.LogLines.Select(line => JsonSerializer.Deserialize<JsonElement>(line))];
        JsonElement result = lines.Should().ContainSingle(line => line.GetProperty("point").GetString() == "result").Subject;
        JsonElement definition = lines.Should().ContainSingle(line => line.GetProperty("point").GetString() == "definition").Subject;
        lines.Should().HaveCount(2);
        result.EnumerateObject().Select(field => field.Name).Should().Equal(
            "point", "policy", "tool", "effective", "evaluated", "action", "latencyMs", "correlationId", "unscreened");
        Fields(result).Should().Equal(
            "result", Screens.ResultsPolicy, _toolName, "warn", "warn", "annotate", "call-a", "False");
        definition.EnumerateObject().Select(field => field.Name).Should().Equal(
            "point", "policy", "tool", "effective", "evaluated", "action", "latencyMs", "correlationId");
        Fields(definition).Should().Equal(
            "definition", Screens.DefinitionsPolicy, _toolName, "warn", "warn", "pass", "list-a");
        lines.Should().AllSatisfy(line => line.GetProperty("latencyMs").GetInt64().Should().BeGreaterThanOrEqualTo(0));
        gateway.Log.ToString().Should().NotContainAny(_canaries);
    }

    [Fact]
    public async Task Otlp_Export_Carries_The_Evaluators_Span_And_No_Content()
    {
        using OtlpStub stub = OtlpStub.Start();
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = stub.Endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        };
        ScriptedUpstream upstream = Canaries();
        await using GatewayHarness gateway = await ConnectAsync(
            upstream.Options(),
            composition: CanaryComposition(new ScriptedDecisionProvider()),
            environment: environment);

        await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
        await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);
        await gateway.EndHostSessionAsync();
        await gateway.Gateway.WaitAsync(gateway.Deadline);

        // Protobuf carries strings as UTF-8, so a name or a canary in the export shows in its bytes.
        stub.Paths.Should().Contain("/v1/traces");
        string traces = Encoding.UTF8.GetString(stub.Body("/v1/traces"));
        string exported = Encoding.UTF8.GetString(stub.AllBodies());
        traces.Should().Contain(SemanticPolicyTelemetry.ActivitySourceName);
        exported.Should().NotContain("ModelContextProtocol");
        exported.Should().NotContainAny(_canaries);
    }

    // A signal's own endpoint is used as it is given, with nothing appended, and under that signal's own protocol.
    [Theory]
    [InlineData("TRACES", "/collector-a/traces")]
    [InlineData("METRICS", "/collector-a/metrics")]
    public async Task Otlp_Export_Goes_To_A_Signals_Own_Endpoint(string signal, string path)
    {
        using OtlpStub stub = OtlpStub.Start();
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            [$"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT"] = stub.Endpoint + path,
            [$"OTEL_EXPORTER_OTLP_{signal}_PROTOCOL"] = "http/protobuf",
        };
        await using GatewayHarness gateway = await ConnectAsync(
            Canaries().Options(),
            composition: CanaryComposition(new ScriptedDecisionProvider()),
            environment: environment);

        await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
        await gateway.EndHostSessionAsync();
        await gateway.Gateway.WaitAsync(gateway.Deadline);

        stub.Paths.Should().NotBeEmpty().And.OnlyContain(received => received == path);
    }

    // The protocol allows a blank string as a request's id. The evaluations go on under it, and the lines name it.
    [Fact]
    public async Task Requests_Under_A_Blank_Id_Are_Screened_And_Their_Lines_Carry_It()
    {
        ScriptedDecisionProvider provider = new();
        await using GatewayHarness gateway = await ConnectAsync(Canaries().Options(), composition: CanaryComposition(provider));

        await SendAsync(gateway, "", RequestMethods.ToolsList, new ListToolsRequestParams());
        await SendAsync(gateway, " ", RequestMethods.ToolsCall, new CallToolRequestParams { Name = _toolName });

        provider.Requests.Should().HaveCount(2);
        gateway.LogLines.Select(line => JsonSerializer.Deserialize<JsonElement>(line).GetProperty("correlationId").GetString())
            .Should().Equal("", " ");
    }

    [Theory]
    [InlineData(null, "", false, false)]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", " ", false, false)]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4318", true, true)]
    [InlineData("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://localhost:4318/v1/traces", true, false)]
    [InlineData("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "http://localhost:4318/v1/metrics", false, true)]
    public void Telemetry_Is_Built_Only_For_A_Signal_With_An_Endpoint(string? variable, string value, bool traces, bool metrics)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal) { ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf" };
        if (variable is not null)
        {
            environment[variable] = value;
        }

        using GatewayTelemetry? telemetry = GatewayTelemetry.Start(environment);

        if (!traces && !metrics)
        {
            telemetry.Should().BeNull();
            return;
        }

        telemetry!.ExportsTraces.Should().Be(traces);
        telemetry.ExportsMetrics.Should().Be(metrics);
    }

    // An upstream whose tool and result carry the canaries, as the policies' question does.
    private static ScriptedUpstream Canaries() => new ScriptedUpstream
    {
        Result = _ => new CallToolResult { Content = [new TextContentBlock { Text = _resultCanary }] },
    }.Lists(ScriptedUpstream.Tool(
        _toolName,
        _descriptionCanary,
        """{"type":"object","properties":{"id":{"type":"string","description":"canary-schema"}}}"""));

    private static GatewayComposition CanaryComposition(ScriptedDecisionProvider provider) => Screens.Composition(
        provider,
        Screens.Results(policy: Screens.Policy(Screens.ResultsPolicy, question: _questionCanary)),
        Screens.Definitions(Screens.Policy(Screens.DefinitionsPolicy, question: _questionCanary)));

    // Sends a request under an id the test chose, which the line names as its correlation id.
    private static Task<JsonRpcResponse> SendAsync<TParams>(GatewayHarness gateway, string id, string method, TParams parameters) =>
        gateway.Host!.SendRequestAsync(
            new JsonRpcRequest
            {
                Id = new RequestId(id),
                Method = method,
                Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
            },
            Token);

    // Every field but latencyMs, whose value varies, as text.
    private static IEnumerable<string> Fields(JsonElement line) => line.EnumerateObject()
        .Where(field => field.Name != "latencyMs")
        .Select(field => field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString()! : field.Value.GetRawText() switch
        {
            "true" => "True",
            "false" => "False",
            var other => other,
        });

    // An OTLP collector on a loopback port that keeps the body of every export it receives, by path, and answers each
    // with an empty success.
    private sealed class OtlpStub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Lock _lock = new();
        private readonly List<(string Path, byte[] Body)> _received = [];
        private readonly Task _serving;

        private OtlpStub(int port)
        {
            Endpoint = $"http://localhost:{port}";
            _listener.Prefixes.Add(Endpoint + "/");
            _listener.Start();
            _serving = ServeAsync();
        }

        public string Endpoint { get; }

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_lock)
                {
                    return [.. _received.Select(received => received.Path)];
                }
            }
        }

        public static OtlpStub Start()
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return new OtlpStub(port);
        }

        public byte[] Body(string path)
        {
            lock (_lock)
            {
                return [.. _received.Where(received => received.Path == path).SelectMany(received => received.Body)];
            }
        }

        public byte[] AllBodies()
        {
            lock (_lock)
            {
                return [.. _received.SelectMany(received => received.Body)];
            }
        }

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The listener closing ends the wait for the next request with an exception.
            }
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception failure) when (failure is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                using MemoryStream body = new();
                await context.Request.InputStream.CopyToAsync(body);
                lock (_lock)
                {
                    _received.Add((context.Request.Url!.AbsolutePath, body.ToArray()));
                }

                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/x-protobuf";
                context.Response.Close();
            }
        }
    }
}
