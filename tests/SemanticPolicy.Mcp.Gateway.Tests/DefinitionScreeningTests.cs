using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SemanticPolicy.Mcp.Gateway.Tests.Support;
using static SemanticPolicy.Mcp.Gateway.Tests.Support.GatewayHarness;

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed class DefinitionScreeningTests
{
    private const string _toolName = "tool_a";

    // The provider flags a definition whose description holds this.
    private const string _flag = "flagged";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Definition_Context_Reaching_The_Provider_Has_Only_Tool_And_Input_Schema()
    {
        const string schema = """{"type":"object","properties":{"id":{"type":"string","description":"schema-a"}}}""";
        ScriptedDecisionProvider provider = new();
        ScriptedUpstream upstream = new ScriptedUpstream().Lists(ScriptedUpstream.Tool(_toolName, "description-a", schema));
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, definitions: Screens.Definitions()));

        await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);

        JsonElement context = provider.Requests.Should().ContainSingle().Subject.Context;
        context.EnumerateObject().Select(part => part.Name).Should().Equal("tool", "input_schema");
        Compact(context.GetProperty("tool")).Should().Be("""{"name":"tool_a","description":"description-a"}""");
        Compact(context.GetProperty("input_schema")).Should().Be(schema);
    }

    [Fact]
    public async Task Flagged_Definition_Is_Hidden_And_A_Call_To_It_Is_Withheld_Without_Reaching_The_Upstream()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Flags(_flag);
        ScriptedUpstream upstream = new ScriptedUpstream().Lists(
            ScriptedUpstream.Tool(_toolName, "description-a-" + _flag),
            ScriptedUpstream.Tool("tool_b", "description-b"));
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, definitions: Screens.Definitions()));

        ListToolsResult listed = await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
        CallToolResult hidden = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);
        CallToolResult passed = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = "tool_b" }, Token);

        listed.Tools.Select(tool => tool.Name).Should().Equal("tool_b");
        Wire(listed.Tools.Single()).Should().Be(Wire(ScriptedUpstream.Tool("tool_b", "description-b")));
        Withheld(hidden);
        passed.IsError.Should().NotBe(true);
        upstream.Calls.Should().Equal("tool_b");
    }

    [Fact]
    public async Task Call_To_A_Name_The_Upstream_Never_Listed_Is_Forwarded_And_Its_Result_Screened()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Answers(ScriptedDecisionProvider.Flagged);
        ScriptedUpstream upstream = new();
        await using GatewayHarness gateway = await ConnectAsync(
            upstream.Options(),
            composition: Screens.Composition(provider, Screens.Results(), Screens.Definitions()));

        CallToolResult result = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token);

        upstream.Calls.Should().Equal(_toolName);
        provider.Requests.Should().ContainSingle().Which.Context.GetProperty("result").GetString().Should().Be("result-a");
        result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text.Should().Be(Screens.WithholdMessage);
    }

    [Theory]
    [InlineData("the same list twice", 1)]
    [InlineData("a changed description", 2)]
    [InlineData("a changed schema", 2)]
    public async Task Definition_Is_Evaluated_Once_Until_It_Changes(string change, int expectedCalls)
    {
        Tool first = ScriptedUpstream.Tool(_toolName, "description-a");
        Tool second = change switch
        {
            "the same list twice" => ScriptedUpstream.Tool(_toolName, "description-a"),
            "a changed description" => ScriptedUpstream.Tool(_toolName, "description-b"),
            "a changed schema" => ScriptedUpstream.Tool(_toolName, "description-a", """{ "type": "object", "properties": { "id": { "type": "string" } } }"""),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        ScriptedDecisionProvider provider = new();
        ScriptedUpstream upstream = new ScriptedUpstream().Lists(first).Lists(second);
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, definitions: Screens.Definitions()));

        await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
        await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);

        provider.Requests.Should().HaveCount(expectedCalls);
    }

    [Fact]
    public async Task Call_To_A_Tool_Hidden_On_An_Earlier_Page_Never_Reaches_The_Upstream()
    {
        // Lookup is A, which the policy flags, on page one; Archive is B, which it allows, on page two.
        ToolsUpstream upstream = new([ToolsUpstream.Lookup], [ToolsUpstream.Archive]);
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Flags(ToolsUpstream.Lookup.Description!);
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, definitions: Screens.Definitions()));

        ListToolsResult pageOne = await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
        ListToolsResult pageTwo = await gateway.Host!.ListToolsAsync(new ListToolsRequestParams { Cursor = pageOne.NextCursor }, Token);
        CallToolResult a = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = ToolsUpstream.Lookup.Name }, Token);
        CallToolResult b = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = ToolsUpstream.Archive.Name }, Token);

        upstream.Calls.Select(call => call.Name).Should().Equal(ToolsUpstream.Archive.Name);
        pageOne.Tools.Should().BeEmpty();
        pageOne.NextCursor.Should().Be(ToolsUpstream.CursorOf(1));
        pageTwo.Tools.Select(tool => tool.Name).Should().Equal(ToolsUpstream.Archive.Name);
        Withheld(a);
        Wire(b).Should().Be(Wire(ToolsUpstream.ResultOf(ToolsUpstream.Archive.Name)));
    }

    // Two lists in flight: the upstream answers the first with A's older definition and the second with its newer one,
    // and the provider holds both verdicts until the test releases them.
    [Theory]
    [InlineData("older passes, newer hides", "older first")]
    [InlineData("older passes, newer hides", "older last")]
    [InlineData("older hides, newer passes", "older first")]
    [InlineData("older hides, newer passes", "older last")]
    [InlineData("older passes, newer hides", "call while the newer is held")]
    public async Task Call_Follows_The_Newest_Listed_Definition_Whatever_Order_Its_Verdicts_Finish_In(string verdicts, string order)
    {
        bool newerHides = verdicts == "older passes, newer hides";
        Tool older = ScriptedUpstream.Tool(_toolName, "description-old" + (newerHides ? "" : "-" + _flag));
        Tool newer = ScriptedUpstream.Tool(_toolName, "description-new" + (newerHides ? "-" + _flag : ""));
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Flags(_flag).Holds("description-old").Holds("description-new");
        ScriptedUpstream upstream = new ScriptedUpstream().Lists(older).Lists(newer);
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, definitions: Screens.Definitions()));

        (Task<ListToolsResult> first, Task<ListToolsResult> second) = await ListOlderThenNewerAsync(gateway, upstream, provider);
        CallToolResult result;
        if (order == "call while the newer is held")
        {
            provider.Release("description-old");
            await first.WaitAsync(gateway.Deadline);
            Task<CallToolResult> call = Call(gateway);
            await gateway.WaitForWaitingCallsAsync(1);

            call.IsCompleted.Should().BeFalse();
            provider.Release("description-new");
            result = await call.WaitAsync(gateway.Deadline);
        }
        else
        {
            string[] releases = order == "older first" ? ["description-old", "description-new"] : ["description-new", "description-old"];
            foreach (string release in releases)
            {
                provider.Release(release);
            }

            await Task.WhenAll(first, second).WaitAsync(gateway.Deadline);
            result = await Call(gateway);
        }

        if (newerHides)
        {
            upstream.Calls.Should().BeEmpty();
            Withheld(result);
        }
        else
        {
            upstream.Calls.Should().Equal(_toolName);
            result.IsError.Should().NotBe(true);
        }
    }

    // A's older definition is listed and its verdict held; the host calls A, and the test waits until the call waits.
    [Theory]
    [InlineData("published during the wait")]
    [InlineData("published after admission")]
    public async Task Call_Is_Admitted_Against_The_Definition_Current_When_Its_Wait_Ends(string publication)
    {
        Tool older = ScriptedUpstream.Tool(_toolName, "description-old");
        Tool newer = ScriptedUpstream.Tool(_toolName, "description-new-" + _flag);
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Flags(_flag).Holds("description-old").Holds("description-new");
        ScriptedUpstream upstream = new ScriptedUpstream().Lists(older).Lists(newer);
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, definitions: Screens.Definitions()));
        Task<ListToolsResult> first = List(gateway);
        await provider.WaitForCallsAsync(1, gateway.Deadline);
        if (publication == "published after admission")
        {
            upstream.HoldCalls();
        }

        Task<CallToolResult> call = Call(gateway);
        await gateway.WaitForWaitingCallsAsync(1);

        if (publication == "published during the wait")
        {
            Task<ListToolsResult> second = List(gateway);
            await provider.WaitForCallsAsync(2, gateway.Deadline);
            provider.Release("description-old");

            // Either the call waits again, on the newer verdict, or the older one let it through to the upstream.
            await Task.WhenAny(gateway.WaitForWaitingCallsAsync(2), upstream.WaitForCallsAsync(1, gateway.Deadline));

            upstream.Calls.Should().BeEmpty();
            call.IsCompleted.Should().BeFalse();
            provider.Release("description-new");
            Withheld(await call.WaitAsync(gateway.Deadline));
            upstream.Calls.Should().BeEmpty();
            await Task.WhenAll(first, second).WaitAsync(gateway.Deadline);
        }
        else
        {
            provider.Release("description-old");
            await upstream.WaitForCallsAsync(1, gateway.Deadline);
            Task<ListToolsResult> second = List(gateway);
            await provider.WaitForCallsAsync(2, gateway.Deadline);
            provider.Release("description-new");
            await second.WaitAsync(gateway.Deadline);
            upstream.OpenCalls();

            CallToolResult admitted = await call.WaitAsync(gateway.Deadline);
            CallToolResult later = await Call(gateway);

            Wire(admitted.Content).Should().Be(Wire(upstream.Result(_toolName).Content));
            Withheld(later);
            upstream.Calls.Should().Equal(_toolName);
        }
    }

    [Fact]
    public async Task Cancelled_List_Leaves_Its_Evaluation_To_Decide_Later_Calls()
    {
        Tool flagged = ScriptedUpstream.Tool(_toolName, "description-a-" + _flag);
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Flags(_flag).Holds(_flag);
        ScriptedUpstream upstream = new ScriptedUpstream().Lists(flagged).Lists(flagged);
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, definitions: Screens.Definitions()));
        JsonRpcRequest list = new()
        {
            Id = new RequestId("list-a"),
            Method = RequestMethods.ToolsList,
            Params = JsonSerializer.SerializeToNode(new ListToolsRequestParams(), McpJsonUtilities.DefaultOptions),
        };
        using CancellationTokenSource host = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task pending = gateway.Host!.SendRequestAsync(list, host.Token);
        await provider.WaitForCallsAsync(1, gateway.Deadline);

        // The SDK's client stops waiting without announcing it, so this host announces it itself; the ping's answer
        // comes after the gateway has read the announcement.
        await host.CancelAsync();
        await gateway.Host.SendNotificationAsync(
            NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = list.Id },
            cancellationToken: Token);
        await gateway.Host.PingAsync(cancellationToken: Token);
        await pending.Awaiting(task => task).Should().ThrowAsync<OperationCanceledException>();
        provider.Release(_flag);

        ListToolsResult again = await gateway.Host.ListToolsAsync(new ListToolsRequestParams(), Token);
        CallToolResult call = await Call(gateway);

        provider.Requests.Should().ContainSingle();
        again.Tools.Should().BeEmpty();
        Withheld(call);
        upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Simultaneous_Lists_Of_One_Uncached_Definition_Make_One_Provider_Call()
    {
        Tool tool = ScriptedUpstream.Tool(_toolName, "description-a");
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Holds("description-a");
        ScriptedUpstream upstream = new ScriptedUpstream().Lists(tool).Lists(tool);
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, definitions: Screens.Definitions()));

        Task<ListToolsResult> first = List(gateway);
        Task<ListToolsResult> second = List(gateway);
        await upstream.WaitForListsAsync(2, gateway.Deadline);
        await provider.WaitForCallsAsync(1, gateway.Deadline);
        provider.Release("description-a");
        ListToolsResult[] lists = await Task.WhenAll(first, second).WaitAsync(gateway.Deadline);

        Wire(lists[0]).Should().Be(Wire(lists[1]));
        lists[0].Tools.Select(listed => listed.Name).Should().Equal(_toolName);
        provider.Requests.Should().ContainSingle();
    }

    // Two lists in flight each round: the upstream serves A's older definition, which passes, then its newer one, which
    // hides. Which page would publish last is a race between two handlers resuming, so one round proves little; a page
    // published out of order in any round lets that round's call through.
    [Fact]
    public async Task Pages_Become_Current_In_The_Order_The_Upstream_Answered_Them()
    {
        const int rounds = 20;
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Flags(_flag);
        ScriptedUpstream upstream = new();
        for (int round = 0; round < rounds; round++)
        {
            upstream
                .Lists(ScriptedUpstream.Tool(_toolName, $"description-old-{round}"))
                .Lists(ScriptedUpstream.Tool(_toolName, $"description-new-{round}-{_flag}"));
        }

        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, definitions: Screens.Definitions()));

        for (int round = 0; round < rounds; round++)
        {
            await Task.WhenAll(List(gateway), List(gateway)).WaitAsync(gateway.Deadline);
            Withheld(await Call(gateway));
        }

        upstream.Calls.Should().BeEmpty();
    }

    // A ToolCall refuses a blank name, but an upstream can list one and a host can call it.
    [Fact]
    public async Task Blank_Named_Tool_Is_Screened_Like_Any_Other()
    {
        Tool blank = ScriptedUpstream.Tool(" ", "description-blank");
        Tool other = ScriptedUpstream.Tool("tool_b", "description-b");
        ScriptedDecisionProvider provider = new();
        ScriptedUpstream upstream = new ScriptedUpstream().Lists(blank, other).Lists(blank, other);
        await using GatewayHarness gateway = await ConnectAsync(
            upstream.Options(),
            composition: Screens.Composition(provider, Screens.Results(), Screens.Definitions()));

        ListToolsResult first = await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
        ListToolsResult second = await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
        CallToolResult call = await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = " " }, Token);

        first.Tools.Select(tool => tool.Name).Should().Equal(" ", "tool_b");
        Wire(second).Should().Be(Wire(first));
        call.IsError.Should().NotBe(true);
        upstream.Calls.Should().Equal(" ");
        provider.Requests.Select(request => Compact(request.Context.GetProperty("tool"))).Should().BeEquivalentTo(
            """{"name":" ","description":"description-blank"}""",
            """{"name":"tool_b","description":"description-b"}""",
            """{"name":" ","description":"description-blank"}""");
    }

    // A provider that throws is a programming error the evaluator lets through, and it fails the list the definition is
    // on. The next list evaluates the definition again instead of failing on the same exception.
    [Fact]
    public async Task Definition_Whose_Evaluation_Threw_Is_Evaluated_Again_By_The_Next_List()
    {
        int answers = 0;
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Answers(_ =>
            Interlocked.Increment(ref answers) == 1 ? throw new InvalidOperationException("failure-a") : ScriptedDecisionProvider.Clear);
        Tool tool = ScriptedUpstream.Tool(_toolName, "description-a");
        ScriptedUpstream upstream = new ScriptedUpstream().Lists(tool).Lists(tool);
        await using GatewayHarness gateway = await ConnectAsync(upstream.Options(), composition: Screens.Composition(provider, definitions: Screens.Definitions()));

        Func<Task> first = async () => await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
        await first.Should().ThrowAsync<McpProtocolException>();
        ListToolsResult second = await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token);
        CallToolResult call = await Call(gateway);

        second.Tools.Select(listed => listed.Name).Should().Equal(_toolName);
        provider.Requests.Should().HaveCount(2);
        call.IsError.Should().NotBe(true);
        upstream.Calls.Should().Equal(_toolName);
    }

    // Sends the first list and waits until its definition is being evaluated, then the same for the second, so the
    // upstream's two answers reach the gateway in that order.
    private static async Task<(Task<ListToolsResult> First, Task<ListToolsResult> Second)> ListOlderThenNewerAsync(
        GatewayHarness gateway,
        ScriptedUpstream upstream,
        ScriptedDecisionProvider provider)
    {
        Task<ListToolsResult> first = List(gateway);
        await upstream.WaitForListsAsync(1, gateway.Deadline);
        await provider.WaitForCallsAsync(1, gateway.Deadline);
        Task<ListToolsResult> second = List(gateway);
        await upstream.WaitForListsAsync(2, gateway.Deadline);
        await provider.WaitForCallsAsync(2, gateway.Deadline);
        return (first, second);
    }

    private static Task<ListToolsResult> List(GatewayHarness gateway) =>
        Task.Run(async () => await gateway.Host!.ListToolsAsync(new ListToolsRequestParams(), Token), Token);

    private static Task<CallToolResult> Call(GatewayHarness gateway) =>
        Task.Run(async () => await gateway.Host!.CallToolAsync(new CallToolRequestParams { Name = _toolName }, Token), Token);

    private static void Withheld(CallToolResult result)
    {
        result.IsError.Should().BeTrue();
        Wire(result.Content).Should().Be(Wire(new List<ContentBlock> { new TextContentBlock { Text = Screens.HideMessage } }));
    }

    private static string Compact(JsonElement element) => JsonSerializer.Serialize(element);
}
