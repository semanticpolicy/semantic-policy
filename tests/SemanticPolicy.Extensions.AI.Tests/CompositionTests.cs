using Microsoft.Extensions.AI;
using SemanticPolicy.Extensions.AI.Tests.Support;

namespace SemanticPolicy.Extensions.AI.Tests;

public sealed class CompositionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guards_Run_In_The_Order_Written_Around_The_Applications_Invoker(bool firstRefuses)
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", ScriptedLoop.Named("test-old")),
            ScriptedTurn.Says("final-1"));
        PolicyEvaluator evaluator = ScriptedLoop.Evaluator(ScriptedLoop.Flagging());
        List<string> seen = [];
        IChatClient guarded = new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), evaluator, (_, _, _) =>
            {
                seen.Add("before-1");
                return ValueTask.FromResult(firstRefuses ? PreToolOutcome.Refuse("refused-1") : PreToolOutcome.Proceed);
            })
            .UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), evaluator, (_, _, _) =>
            {
                seen.Add("before-2");
                return ValueTask.FromResult(PreToolOutcome.Proceed);
            })
            .UseSemanticPolicyAfterTool(ScriptedLoop.DenyPolicy(), evaluator, (_, _, _) =>
            {
                seen.Add("after");
                return ValueTask.FromResult(PostToolOutcome.Proceed);
            })
            .UseFunctionInvocation(configure: loop => loop.FunctionInvoker = (context, cancellationToken) =>
            {
                seen.Add("invoker");
                return context.Function.InvokeAsync(context.Arguments, cancellationToken);
            })
            .Build();

        await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);

        string? result = ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result);
        if (firstRefuses)
        {
            seen.Should().Equal("before-1");
            branches.Arguments.Should().BeEmpty();
            result.Should().Be("refused-1");
        }
        else
        {
            seen.Should().Equal("before-1", "before-2", "invoker", "after");
            branches.Arguments.Should().Equal("test-old");
            result.Should().Be("deleted-ok");
        }
    }

    [Fact]
    public async Task Outer_After_Tool_Guard_Reads_The_Inner_Guards_Replacement()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", ScriptedLoop.Named("test-old")),
            ScriptedTurn.Says("final-1"));
        PolicyEvaluator evaluator = ScriptedLoop.Evaluator(ScriptedLoop.Flagging());
        List<string> seen = [];
        object? outerRead = null;
        IChatClient guarded = new ChatClientBuilder(client)
            .UseSemanticPolicyAfterTool(ScriptedLoop.DenyPolicy(), evaluator, (result, _, _) =>
            {
                seen.Add("after-1");
                outerRead = result.Value;
                return ValueTask.FromResult(PostToolOutcome.Proceed);
            })
            .UseSemanticPolicyAfterTool(ScriptedLoop.DenyPolicy(), evaluator, (_, _, _) =>
            {
                seen.Add("after-2");
                return ValueTask.FromResult(PostToolOutcome.Replace("replaced-2"));
            })
            .UseFunctionInvocation()
            .Build();

        await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);

        seen.Should().Equal("after-2", "after-1");
        outerRead.Should().Be("replaced-2");
        ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result)
            .Should().Be("replaced-2");
    }
}
