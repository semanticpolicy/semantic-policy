using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SemanticPolicy.Mcp.Gateway.Tests.Support;
using SemanticPolicy.Protocol;
using static SemanticPolicy.Mcp.Gateway.Tests.Support.GatewayHarness;

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed class ResultScreeningTests
{
    private const string _toolName = "tool_a";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("text blocks")]
    [InlineData("text and an embedded text resource")]
    [InlineData("structured only")]
    [InlineData("text with structured content")]
    public async Task Result_Context_Reaching_The_Provider_Has_Only_Tool_And_Result(string shape)
    {
        (CallToolResult result, string expected) = shape switch
        {
            "text blocks" => (new CallToolResult { Content = [Text("text-a"), Text("text-b")] }, "\"text-a\\ntext-b\""),
            "text and an embedded text resource" => (
                new CallToolResult { Content = [Text("text-a"), TextResource("resource-a"), Text("text-b")] },
                "\"text-a\\nresource-a\\ntext-b\""),
            "structured only" => (new CallToolResult { Content = [], StructuredContent = Json("""{"state":"state-a"}""") }, """{"state":"state-a"}"""),
            "text with structured content" => (
                new CallToolResult { Content = [Text("text-a")], StructuredContent = Json("""{"state":"state-a"}""") },
                "\"text-a\""),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        ScriptedDecisionProvider provider = new();
        ScriptedUpstream upstream = new ScriptedUpstream { Result = _ => result }.Lists(ScriptedUpstream.Tool(_toolName, "description-a"));
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, results: Screens.Results()));
        await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);

        await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        JsonElement context = provider.Requests.Should().ContainSingle().Subject.Context;
        context.EnumerateObject().Select(part => part.Name).Should().Equal("tool", "result");
        Compact(context.GetProperty("tool")).Should().Be("""{"name":"tool_a","description":"description-a"}""");
        Compact(context.GetProperty("result")).Should().Be(Compact(Json(expected)));
    }

    // The newest definition the upstream listed names the tool; a name it never listed is the name alone.
    [Theory]
    [InlineData("listed twice")]
    [InlineData("never listed")]
    public async Task Result_Context_Names_The_Tool_By_Its_Current_Definition(string listing)
    {
        ScriptedDecisionProvider provider = new();
        ScriptedUpstream upstream = new ScriptedUpstream()
            .Lists(ScriptedUpstream.Tool(_toolName, "description-old"))
            .Lists(ScriptedUpstream.Tool(_toolName, "description-new"));
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, results: Screens.Results()));
        if (listing == "listed twice")
        {
            await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
            await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
        }

        await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        Compact(provider.Requests.Should().ContainSingle().Subject.Context.GetProperty("tool")).Should().Be(
            listing == "listed twice" ? """{"name":"tool_a","description":"description-new"}""" : """{"name":"tool_a"}""");
    }

    [Theory]
    [InlineData("image")]
    [InlineData("audio")]
    [InlineData("blob resource")]
    [InlineData("resource link")]
    [InlineData("image only")]
    public async Task Result_With_Unscreened_Blocks_Is_Screened_On_Its_Text_And_Flagged(string block)
    {
        ContentBlock unscreened = block switch
        {
            "image" or "image only" => ImageContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "image/png"),
            "audio" => AudioContentBlock.FromBytes(new byte[] { 1, 2, 3 }, "audio/wav"),
            "blob resource" => new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(new byte[] { 1, 2, 3 }, "file:///blob-a", "application/octet-stream") },
            "resource link" => new ResourceLinkBlock { Uri = "file:///link-a", Name = "link-a" },
            _ => throw new ArgumentOutOfRangeException(nameof(block)),
        };
        CallToolResult result = new() { Content = block == "image only" ? [unscreened] : [Text("text-a"), unscreened] };
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Answers(ScriptedDecisionProvider.Flagged);
        ScriptedUpstream upstream = new() { Result = _ => result };
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, results: Screens.Results()));

        CallToolResult received = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        JsonElement line = JsonDocument.Parse(gateway.LogLines.Should().ContainSingle().Subject).RootElement;
        line.GetProperty("unscreened").GetBoolean().Should().BeTrue();
        if (block == "image only")
        {
            provider.Requests.Should().BeEmpty();
            Wire(received).Should().Be(Wire(result));
            line.TryGetProperty("effective", out _).Should().BeFalse();
            line.TryGetProperty("evaluated", out _).Should().BeFalse();
            line.GetProperty("action").GetString().Should().Be("pass");
        }
        else
        {
            provider.Requests.Should().ContainSingle().Which.Context.GetProperty("result").GetString().Should().Be("text-a");
            line.GetProperty("effective").GetString().Should().Be("deny");
        }
    }

    [Theory]
    [InlineData("withhold")]
    [InlineData("pass")]
    [InlineData("annotate")]
    [InlineData("withhold an upstream error")]
    public async Task Flagged_Result_Gets_The_Operators_Action(string action)
    {
        MappedAction warn = action switch
        {
            "pass" => Screens.Pass,
            "annotate" => Screens.Annotate,
            _ => Screens.Withhold,
        };
        bool upstreamError = action == "withhold an upstream error";
        CallToolResult Upstream() => new()
        {
            Content = [Text("text-a")],
            StructuredContent = upstreamError ? null : Json("""{"state":"state-a"}"""),
            IsError = upstreamError,
        };
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Answers(ScriptedDecisionProvider.Warned);
        ScriptedUpstream upstream = new() { Result = _ => Upstream() };
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, results: Screens.Results(warn)));

        CallToolResult received = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        CallToolResult expected = Upstream();
        switch (action)
        {
            case "annotate":
                expected.Content = [Text(Screens.AnnotateMessage), .. expected.Content];
                break;
            case "withhold" or "withhold an upstream error":
                expected = new CallToolResult { Content = [Text(Screens.WithholdMessage)], IsError = true };
                break;
        }

        received.IsError.Should().Be(expected.IsError);
        Wire(received).Should().Be(Wire(expected));
    }

    // An upstream can answer a call with an error instead of a result, and a host may hand the error's message to the
    // model as it would a result's text.
    [Theory]
    [InlineData("pass", true)]
    [InlineData("annotate", true)]
    [InlineData("withhold", true)]
    [InlineData("pass", false)]
    public async Task Upstream_Error_Answering_A_Call_Is_Screened_And_Gets_The_Operators_Action(string action, bool data)
    {
        MappedAction warn = action switch
        {
            "pass" => Screens.Pass,
            "annotate" => Screens.Annotate,
            _ => Screens.Withhold,
        };
        McpServerOptions options = new ScriptedUpstream().Options();
        options.Handlers.CallToolHandler = (_, _) =>
        {
            McpProtocolException refusal = new("error-a", McpErrorCode.InvalidParams);
            if (data)
            {
                refusal.Data["detail"] = "detail-a";
            }

            throw refusal;
        };
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Answers(ScriptedDecisionProvider.Warned);
        await using GatewayHarness gateway = await ConnectAsync(options, composition: Screens.Composition(provider, results: Screens.Results(warn)));

        Func<Task> act = async () => await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        McpProtocolException seen = (await act.Should().ThrowAsync<McpProtocolException>()).Which;
        Compact(provider.Requests.Should().ContainSingle().Subject.Context.GetProperty("result")).Should().Be(
            data ? """{"message":"error-a","data":{"detail":"detail-a"}}""" : "\"error-a\"");
        seen.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
        seen.Message.Should().Be("Request failed (remote): " + action switch
        {
            "annotate" => Screens.AnnotateMessage + "\nerror-a",
            "withhold" => Screens.WithholdMessage,
            _ => "error-a",
        });
        seen.Data.Contains("detail").Should().Be(data && action != "withhold");
        JsonDocument.Parse(gateway.LogLines.Should().ContainSingle().Subject).RootElement.GetProperty("action").GetString().Should().Be(action);
    }

    [Theory]
    [InlineData(FailureKind.RejectedInput)]
    [InlineData(FailureKind.Timeout)]
    [InlineData(null)]
    public async Task Provider_Failure_Takes_The_Policys_OnFailure_Verdict_Into_The_Mapping(FailureKind? failure)
    {
        // Longer than any provider's limit is not the point; that nothing is cut from it is.
        string text = string.Concat(Enumerable.Repeat("text-a ", 20_000)) + "text-end";
        ScriptedDecisionProvider provider = new();
        TimeSpan? budget = null;
        if (failure is { } kind)
        {
            provider.Answers(ScriptedDecisionProvider.Failed(kind));
        }
        else
        {
            // Never released: the policy's budget expires first.
            provider.Holds("text-end");
            budget = TimeSpan.FromMilliseconds(200);
        }

        ScriptedUpstream upstream = new() { Result = _ => new CallToolResult { Content = [Text(text)] } };
        GatewayPoint results = Screens.Results(policy: Screens.Policy(Screens.ResultsPolicy, budget: budget));
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, results: results));

        CallToolResult received = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        provider.Requests.Should().ContainSingle().Which.Context.GetProperty("result").GetString().Should().Be(text);
        received.IsError.Should().BeTrue();
        Wire(received.Content).Should().Be(Wire(new List<ContentBlock> { Text(Screens.EscalateMessage) }));
        JsonDocument.Parse(gateway.LogLines.Should().ContainSingle().Subject).RootElement.GetProperty("evaluated").GetString().Should().Be("escalate");
    }

    private static TextContentBlock Text(string text) => new() { Text = text };

    private static EmbeddedResourceBlock TextResource(string text) =>
        new() { Resource = new TextResourceContents { Uri = "file:///resource-a", Text = text } };

    private static JsonElement Json(string json) => ToolsUpstream.Json(json);

    private static string Compact(JsonElement element) => JsonSerializer.Serialize(element);
}
