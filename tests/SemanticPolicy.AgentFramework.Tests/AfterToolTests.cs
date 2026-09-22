using System.Text.Json;
using Microsoft.Agents.AI;
using SemanticPolicy.AgentFramework.Tests.Support;

namespace SemanticPolicy.AgentFramework.Tests;

public sealed class AfterToolTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Proceed_After_Tool_Passes_The_Functions_Result_To_The_Model()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = Scripted();
        AIAgent guarded = Guarded(client, Proceeds(), branches);

        AgentResponse response = await guarded.RunAsync("delete branch test-old", cancellationToken: Token);

        branches.Arguments.Should().Equal("test-old");
        ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result)
            .Should().Be("deleted-ok");
        response.Text.Should().Be("final-1");
    }

    [Fact]
    public async Task Replace_After_Tool_Sends_The_Replacement_To_The_Model_Instead_Of_The_Result()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = Scripted();
        AIAgent guarded = Guarded(client, Replaces("replacement-1"), branches);

        AgentResponse response = await guarded.RunAsync("delete branch test-old", cancellationToken: Token);

        branches.Arguments.Should().Equal("test-old");
        ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result)
            .Should().Be("replacement-1");
        response.Text.Should().Be("final-1");
    }

    [Fact]
    public async Task Stop_After_Tool_Returns_The_Message_And_Ends_The_Loop()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = Scripted();
        AIAgent guarded = Guarded(client, Stops("stopped-1"), branches);

        AgentResponse response = await guarded.RunAsync("delete branch test-old", cancellationToken: Token);

        branches.Arguments.Should().Equal("test-old");
        client.Requests.Should().ContainSingle();
        ToolResults.TextOf(ToolResults.In(response.Messages).Should().ContainSingle().Which.Result)
            .Should().Be("stopped-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_Receives_The_Result_As_The_Loop_Hands_It_Over(bool structured)
    {
        object returns = structured ? new StubResult("test-old", Deleted: true) : "deleted-ok";
        StubTool branches = new("delete_branch", "Deletes a branch.", returns);
        ScriptedChatClient client = Scripted();
        ToolResult? seen = null;
        PostToolHandler handler = (result, _, _) =>
        {
            seen = result;
            return ValueTask.FromResult(PostToolOutcome.Proceed);
        };
        AIAgent guarded = Guarded(client, handler, branches);

        await guarded.RunAsync("delete branch test-old", cancellationToken: Token);

        seen.Should().NotBeNull();
        seen!.Call.Name.Should().Be("delete_branch");
        seen.Call.CorrelationId.Should().Be("call_1");
        JsonElement value = seen.Value.Should().BeOfType<JsonElement>().Which;
        if (structured)
        {
            value.ValueKind.Should().Be(JsonValueKind.Object);
            value.EnumerateObject().Select(property => property.Name).Should().Equal("branchName", "deleted");
        }
        else
        {
            value.ValueKind.Should().Be(JsonValueKind.String);
            value.GetString().Should().Be("deleted-ok");
        }
    }

    [Fact]
    public async Task Before_And_After_Tool_Guards_Compose_On_One_Agent()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = Scripted();
        int before = 0;
        int after = 0;
        PreToolHandler beforeTool = (_, _, _) =>
        {
            Interlocked.Increment(ref before);
            return ValueTask.FromResult(PreToolOutcome.Proceed);
        };
        PostToolHandler afterTool = (_, _, _) =>
        {
            Interlocked.Increment(ref after);
            return ValueTask.FromResult(PostToolOutcome.Replace("replacement-1"));
        };
        AIAgent guarded = new AIAgentBuilder(ScriptedAgent.Over(client, branches))
            .UseSemanticPolicyBeforeTool(ScriptedAgent.DenyPolicy(), ScriptedAgent.Evaluator(ScriptedAgent.Flagging()), beforeTool)
            .UseSemanticPolicyAfterTool(ScriptedAgent.DenyPolicy(), ScriptedAgent.Evaluator(ScriptedAgent.Flagging()), afterTool)
            .Build();

        await guarded.RunAsync("delete branch test-old", cancellationToken: Token);

        branches.Arguments.Should().Equal("test-old");
        before.Should().Be(1);
        after.Should().Be(1);
        ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result)
            .Should().Be("replacement-1");
    }

    private static ScriptedChatClient Scripted() =>
        new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = "test-old" }),
            ScriptedTurn.Says("final-1"));

    private static AIAgent Guarded(ScriptedChatClient client, PostToolHandler handler, params StubTool[] tools) =>
        new AIAgentBuilder(ScriptedAgent.Over(client, tools))
            .UseSemanticPolicyAfterTool(ScriptedAgent.DenyPolicy(), ScriptedAgent.Evaluator(ScriptedAgent.Flagging()), handler)
            .Build();

    private static PostToolHandler Proceeds() => (_, _, _) => ValueTask.FromResult(PostToolOutcome.Proceed);

    private static PostToolHandler Replaces(object? result) => (_, _, _) => ValueTask.FromResult(PostToolOutcome.Replace(result));

    private static PostToolHandler Stops(string message) => (_, _, _) => ValueTask.FromResult(PostToolOutcome.Stop(message));
}
