using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.AI;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Extensions.AI.Tests.Support;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Extensions.AI.Tests;

public sealed class BeforeToolTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Proceed_Before_Tool_Invokes_The_Function_And_Continues_The_Loop()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = OneCallThenText();
        IChatClient guarded = Guarded(client, Proceeds());

        ChatResponse response = await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);

        branches.Arguments.Should().Equal("test-old");
        client.Requests.Should().HaveCount(2);
        ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result)
            .Should().Be("deleted-ok");
        response.Text.Should().Be("final-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refuse_Before_Tool_Returns_The_Message_As_The_Function_Result_And_Continues(bool streaming)
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = OneCallThenText();
        IChatClient guarded = Guarded(client, Refuses("refused-1"));

        ChatResponse response = await Respond(guarded, streaming, branches);

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
            ScriptedTurn.Calls("call_1", "delete_branch", ScriptedLoop.Named("test-old")),
            ScriptedTurn.Says("never-reached"));
        IChatClient guarded = Guarded(client, Stops("stopped-1"));

        ChatResponse response = await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);

        branches.Arguments.Should().BeEmpty();
        client.Requests.Should().ContainSingle();
        ToolResults.TextOf(ToolResults.In(response.Messages).Should().ContainSingle().Which.Result)
            .Should().Be("stopped-1");
    }

    [Fact]
    public async Task Handler_And_Provider_See_The_Call_As_The_Model_Proposed_It()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = OneCallThenText();
        ScriptedDecisionProvider provider = ScriptedLoop.Flagging();
        ToolCall? seen = null;
        PreToolHandler handler = (call, _, _) =>
        {
            seen = call;
            return ValueTask.FromResult(PreToolOutcome.Proceed);
        };
        IChatClient guarded = new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), ScriptedLoop.Evaluator(provider), handler)
            .UseFunctionInvocation()
            .Build();

        await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);

        seen.Should().NotBeNull();
        seen!.Name.Should().Be("delete_branch");
        seen.Description.Should().Be("Deletes a branch.");
        seen.Arguments.GetProperty("name").GetString().Should().Be("test-old");
        seen.UserRequest.Should().Be("delete branch test-old");
        seen.CorrelationId.Should().Be("call_1");
        SemanticContext expected = seen.ToSemanticContext();
        expected.CorrelationId.Should().Be("call_1");
        provider.Requests.Should().ContainSingle()
            .Which.Context.GetRawText().Should().Be(expected.ToJson().GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_Function_Call_In_One_Response_Gets_The_Outcome_Of_Its_Own_Verdict(bool concurrent)
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        StubTool tags = new("delete_tag", "Deletes a tag.", "tag-ok");
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", ScriptedLoop.Named("test-old"))
                .ThenCalls("call_2", "delete_tag", ScriptedLoop.Named("v0-old")),
            ScriptedTurn.Says("final-1"));

        // Only the branch is flagged, so a call that got its sibling's verdict shows in its result.
        Func<DecisionRequest, ProviderResult> answer = request => ToolOf(request) == "delete_branch"
            ? ScriptedDecisionProvider.Boolean(true, 0.95)
            : ScriptedDecisionProvider.Boolean(false, 0.05);
        ScriptedDecisionProvider provider = concurrent
            ? new ScriptedDecisionProvider().Holds(answer)
            : new ScriptedDecisionProvider().Returns(answer);
        PreToolHandler handler = (call, verdict, _) => ValueTask.FromResult(
            verdict.Effective == Verdict.Deny ? PreToolOutcome.Refuse($"refused-{call.Name}") : PreToolOutcome.Proceed);
        IChatClient guarded = new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), ScriptedLoop.Evaluator(provider), handler)
            .UseFunctionInvocation(configure: loop => loop.AllowConcurrentInvocation = concurrent)
            .Build();

        Task<ChatResponse> responding =
            guarded.GetResponseAsync("clean up test-old and v0-old", ScriptedLoop.With(branches, tags), Token);
        if (concurrent)
        {
            // Both evaluations are in flight before either answers, so the two calls genuinely overlap.
            await provider.WaitForCallsAsync(2, Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            provider.Release();
        }

        await responding;

        branches.Arguments.Should().BeEmpty();
        tags.Arguments.Should().Equal("v0-old");
        ToolResults.In(client.Requests[1].Messages)
            .Select(result => (result.CallId, ToolResults.TextOf(result.Result)))
            .Should().BeEquivalentTo([("call_1", "refused-delete_branch"), ("call_2", "tag-ok")]);
    }

    [Theory]
    [InlineData(PolicyMode.Shadow)]
    [InlineData(PolicyMode.Enforce)]
    public async Task Frontend_Applies_The_Handlers_Outcome_In_Every_Mode(PolicyMode mode)
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = OneCallThenText();
        PolicyVerdict? verdictSeen = null;
        PreToolHandler handler = (_, verdict, _) =>
        {
            verdictSeen = verdict;
            return ValueTask.FromResult(PreToolOutcome.Refuse("refused-1"));
        };
        Policy policy = ScriptedLoop.DenyPolicy(mode);
        IChatClient guarded = new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(policy, ScriptedLoop.Evaluator(ScriptedLoop.Flagging(), policy), handler)
            .UseFunctionInvocation()
            .Build();

        await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);

        verdictSeen.Should().NotBeNull();
        verdictSeen!.Mode.Should().Be(mode);
        branches.Arguments.Should().BeEmpty();
        ToolResults.TextOf(ToolResults.In(client.Requests[1].Messages).Should().ContainSingle().Which.Result)
            .Should().Be("refused-1");
    }

    [Fact]
    public async Task Evaluator_Exception_Inside_The_Loop_Is_Not_Caught_By_The_Frontend()
    {
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = OneCallThenText();
        IChatClient guarded = new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), new PolicyEvaluator([], []), Proceeds())
            .UseFunctionInvocation()
            .Build();

        ChatResponse response = await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);

        branches.Arguments.Should().BeEmpty();
        ToolResults.In(client.Requests[1].Messages).Should().ContainSingle()
            .Which.Exception.Should().BeOfType<PolicyConfigurationException>();
        response.Text.Should().Be("final-1");
    }

    [Fact]
    public void Frontend_Assembly_Declares_No_Activity_Source_Or_Meter()
    {
        Assembly frontend = typeof(SemanticPolicyChatClientBuilderExtensions).Assembly;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        IEnumerable<string> declared =
            from type in frontend.GetTypes()
            from member in type.GetFields(statics).Cast<MemberInfo>().Concat(type.GetProperties(statics))
            let held = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType
            where held == typeof(ActivitySource) || held == typeof(Meter)
            select $"{type.FullName}.{member.Name}";

        declared.Should().BeEmpty();
    }

    private static ScriptedChatClient OneCallThenText() =>
        new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", ScriptedLoop.Named("test-old")),
            ScriptedTurn.Says("final-1"));

    private static IChatClient Guarded(ScriptedChatClient client, PreToolHandler handler) =>
        new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), ScriptedLoop.Evaluator(ScriptedLoop.Flagging()), handler)
            .UseFunctionInvocation()
            .Build();

    private static PreToolHandler Proceeds() => (_, _, _) => ValueTask.FromResult(PreToolOutcome.Proceed);

    private static PreToolHandler Refuses(string message) => (_, _, _) => ValueTask.FromResult(PreToolOutcome.Refuse(message));

    private static PreToolHandler Stops(string message) => (_, _, _) => ValueTask.FromResult(PreToolOutcome.Stop(message));

    private static string? ToolOf(DecisionRequest request) =>
        request.Context.GetProperty("tool").GetProperty("name").GetString();

    // The same request on either path; a streamed one is collected into the response it adds up to.
    private static async Task<ChatResponse> Respond(IChatClient guarded, bool streaming, params StubTool[] tools) =>
        streaming
            ? await guarded.GetStreamingResponseAsync("delete branch test-old", ScriptedLoop.With(tools), Token).ToChatResponseAsync(Token)
            : await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(tools), Token);
}
