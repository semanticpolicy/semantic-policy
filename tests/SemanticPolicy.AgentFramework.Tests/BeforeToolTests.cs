using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Agents.AI;
using SemanticPolicy.AgentFramework.Tests.Support;

namespace SemanticPolicy.AgentFramework.Tests;

public sealed class BeforeToolTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Proceed_Before_Tool_Invokes_The_Function_And_Continues_The_Loop()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", Named("test-old")),
            ScriptedTurn.Says("final-1"));
        AIAgent guarded = Guarded(client, Proceeds(), branches);

        AgentResponse response = await guarded.RunAsync("delete branch test-old", cancellationToken: Token);

        branches.Arguments.Should().Equal("test-old");
        client.Requests.Should().HaveCount(2);
        ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result)
            .Should().Be("deleted-ok");
        response.Text.Should().Be("final-1");
    }

    [Fact]
    public async Task Refuse_Before_Tool_Returns_The_Message_As_The_Function_Result_And_Continues()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", Named("test-old")),
            ScriptedTurn.Says("final-1"));
        AIAgent guarded = Guarded(client, Refuses("refused-1"), branches);

        AgentResponse response = await guarded.RunAsync("delete branch test-old", cancellationToken: Token);

        branches.Arguments.Should().BeEmpty();
        client.Requests.Should().HaveCount(2);
        ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result)
            .Should().Be("refused-1");
        response.Text.Should().Be("final-1");
    }

    [Fact]
    public async Task Stop_Before_Tool_Returns_The_Message_And_Ends_The_Loop()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", Named("test-old")),
            ScriptedTurn.Says("never-reached"));
        AIAgent guarded = Guarded(client, Stops("stopped-1"), branches);

        AgentResponse response = await guarded.RunAsync("delete branch test-old", cancellationToken: Token);

        branches.Arguments.Should().BeEmpty();
        client.Requests.Should().ContainSingle();
        ToolResults.TextOf(ToolResults.In(response.Messages).Should().ContainSingle().Which.Result)
            .Should().Be("stopped-1");
    }

    [Fact]
    public async Task Handler_Receives_The_Call_As_The_Model_Proposed_It()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", Named("test-old")),
            ScriptedTurn.Says("final-1"));
        ToolCall? seen = null;
        PreToolHandler handler = (call, _, _) =>
        {
            seen = call;
            return ValueTask.FromResult(PreToolOutcome.Proceed);
        };
        AIAgent guarded = Guarded(client, handler, branches);

        await guarded.RunAsync("delete branch test-old", cancellationToken: Token);

        seen.Should().NotBeNull();
        seen!.Name.Should().Be("delete_branch");
        seen.Description.Should().Be("Deletes a branch.");
        seen.Arguments.GetProperty("name").GetString().Should().Be("test-old");
        seen.UserRequest.Should().Be("delete branch test-old");
        seen.CorrelationId.Should().Be("call_1");
    }

    [Fact]
    public async Task Every_Function_Call_In_One_Iteration_Is_Evaluated()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        StubTool tags = new("delete_tag", "Deletes a tag.", "tag-ok");
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", Named("test-old"))
                .ThenCalls("call_2", "delete_tag", Named("v0-old")),
            ScriptedTurn.Says("final-1"));
        ScriptedDecisionProvider provider = ScriptedAgent.Flagging();
        int handled = 0;
        PreToolHandler handler = (_, _, _) =>
        {
            Interlocked.Increment(ref handled);
            return ValueTask.FromResult(PreToolOutcome.Proceed);
        };
        AIAgent guarded = new AIAgentBuilder(ScriptedAgent.Over(client, branches, tags))
            .UseSemanticPolicyBeforeTool(ScriptedAgent.DenyPolicy(), ScriptedAgent.Evaluator(provider), handler)
            .Build();

        await guarded.RunAsync("clean up test-old and v0-old", cancellationToken: Token);

        handled.Should().Be(2);
        provider.Requests.Select(request => request.Context.GetProperty("tool").GetProperty("name").GetString())
            .Should().BeEquivalentTo(["delete_branch", "delete_tag"]);
        ToolResults.In(client.Requests[1].Messages).Select(result => ToolResults.TextOf(result.Result))
            .Should().BeEquivalentTo(["deleted-ok", "tag-ok"]);
    }

    [Fact]
    public async Task Tool_Call_Evaluation_Correlates_To_The_Call_Id()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        // The listener hears every source in the process, and test classes run in parallel, so the call
        // id has to be one no other test uses or a sibling's evaluation lands in this one's assertion.
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_traced_1", "delete_branch", Named("test-old")),
            ScriptedTurn.Says("final-1"));
        AIAgent guarded = Guarded(client, Proceeds(), branches);

        Activity[] traced = await Traced(() => guarded.RunAsync("delete branch test-old", cancellationToken: Token));

        traced.Where(activity => activity.OperationName == "semanticpolicy.evaluate"
                && activity.GetTagItem("semanticpolicy.correlation_id") as string == "call_traced_1")
            .Should().ContainSingle();
        traced.Select(activity => activity.Source.Name)
            .Where(name => name.StartsWith("SemanticPolicy", StringComparison.Ordinal))
            .Distinct()
            .Should().Equal("SemanticPolicy");
    }

    [Fact]
    public void Adapter_Assembly_Declares_No_Activity_Source_Or_Meter()
    {
        Assembly adapter = typeof(ModelInput).Assembly;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        IEnumerable<string> declared =
            from type in adapter.GetTypes()
            from member in type.GetFields(statics).Cast<MemberInfo>().Concat(type.GetProperties(statics))
            let held = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType
            where held == typeof(ActivitySource) || held == typeof(Meter)
            select $"{type.FullName}.{member.Name}";

        declared.Should().BeEmpty();
    }

    [Fact]
    public async Task Evaluator_Exception_Inside_The_Loop_Is_Not_Caught_By_The_Adapter()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", Named("test-old")),
            ScriptedTurn.Says("final-1"));
        AIAgent guarded = new AIAgentBuilder(ScriptedAgent.Over(client, branches))
            .UseSemanticPolicyBeforeTool(ScriptedAgent.DenyPolicy(), new PolicyEvaluator([], []), Proceeds())
            .Build();

        AgentResponse response = await guarded.RunAsync("delete branch test-old", cancellationToken: Token);

        branches.Arguments.Should().BeEmpty();
        ToolResults.In(client.Requests[1].Messages).Should().ContainSingle()
            .Which.Exception.Should().BeOfType<PolicyConfigurationException>();
        response.Text.Should().Be("final-1");
    }

    private static AIAgent Guarded(ScriptedChatClient client, PreToolHandler handler, params StubTool[] tools) =>
        new AIAgentBuilder(ScriptedAgent.Over(client, tools))
            .UseSemanticPolicyBeforeTool(ScriptedAgent.DenyPolicy(), ScriptedAgent.Evaluator(ScriptedAgent.Flagging()), handler)
            .Build();

    private static Dictionary<string, object?> Named(string name) => new(StringComparer.Ordinal) { ["name"] = name };

    private static PreToolHandler Proceeds() => (_, _, _) => ValueTask.FromResult(PreToolOutcome.Proceed);

    private static PreToolHandler Refuses(string message) => (_, _, _) => ValueTask.FromResult(PreToolOutcome.Refuse(message));

    private static PreToolHandler Stops(string message) => (_, _, _) => ValueTask.FromResult(PreToolOutcome.Stop(message));

    // Every activity, from any source, that stopped while the action ran. The source names are the
    // point: a telemetry dimension the adapter added would show up here as a second source.
    private static async Task<Activity[]> Traced(Func<Task> action)
    {
        List<Activity> stopped = [];
        ActivityListener listener = new()
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (stopped)
                {
                    stopped.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(listener);
        try
        {
            await action();
        }
        finally
        {
            listener.Dispose();
        }

        lock (stopped)
        {
            return [.. stopped];
        }
    }
}
