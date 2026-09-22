using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SemanticPolicy.AgentFramework.Tests.Support;
using SemanticPolicy.Evaluation;

namespace SemanticPolicy.AgentFramework.Tests;

public sealed class BeforeModelTests
{
    private static readonly TimeSpan _patience = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Proceed_Before_Model_Runs_The_Inner_Agent_With_The_Original_Messages()
    {
        ScriptedChatClient client = new ScriptedChatClient().Responds(ScriptedTurn.Says("answer-1"));
        ScriptedDecisionProvider provider = ScriptedAgent.Flagging();
        ModelInput? seen = null;
        PreModelHandler handler = (input, _, _) =>
        {
            seen = input;
            return ValueTask.FromResult(PreModelOutcome.Proceed);
        };
        AIAgent guarded = new AIAgentBuilder(ScriptedAgent.Over(client))
            .UseSemanticPolicyBeforeModel(ScriptedAgent.DenyPolicy(), ScriptedAgent.Evaluator(provider), handler)
            .Build();

        AgentResponse response = await guarded.RunAsync("user-text-1", cancellationToken: Token);

        response.Text.Should().Be("answer-1");
        client.Requests.Should().ContainSingle().Which.Messages[^1].Text.Should().Be("user-text-1");
        seen.Should().NotBeNull();
        seen!.Text.Should().Be("user-text-1");
        seen.CorrelationId.Should().HaveLength(32);
    }

    [Fact]
    public async Task Stop_Before_Model_Returns_The_Handlers_Message_And_Never_Calls_The_Model()
    {
        ScriptedChatClient client = new ScriptedChatClient().Responds(ScriptedTurn.Says("answer-1"));
        ScriptedDecisionProvider provider = ScriptedAgent.Flagging();
        AIAgent guarded = new AIAgentBuilder(ScriptedAgent.Over(client))
            .UseSemanticPolicyBeforeModel(ScriptedAgent.DenyPolicy(), ScriptedAgent.Evaluator(provider), Stops("stopped-1"))
            .Build();

        AgentResponse response = await guarded.RunAsync("user-text-1", cancellationToken: Token);

        response.Text.Should().Be("stopped-1");
        client.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Streaming_Run_Forwards_The_First_Update_Before_The_Inner_Stream_Completes()
    {
        TaskCompletionSource gate = new();
        ScriptedChatClient client = new ScriptedChatClient()
            .Responds(ScriptedTurn.Says("first-1").ThenSays("second-1", gate));
        ScriptedDecisionProvider provider = ScriptedAgent.Flagging();
        AIAgent guarded = new AIAgentBuilder(ScriptedAgent.Over(client))
            .UseSemanticPolicyBeforeModel(ScriptedAgent.DenyPolicy(), ScriptedAgent.Evaluator(provider), Proceeds())
            .Build();

        IAsyncEnumerator<AgentResponseUpdate> updates = guarded.RunStreamingAsync("user-text-1", cancellationToken: Token).GetAsyncEnumerator(Token);
        try
        {
            string first = await NextText(updates).WaitAsync(_patience, Token);
            gate.SetResult();
            string second = await NextText(updates).WaitAsync(_patience, Token);

            first.Should().Be("first-1");
            second.Should().Be("second-1");
        }
        finally
        {
            await updates.DisposeAsync();
        }
    }

    [Fact]
    public async Task Streaming_Stop_Yields_One_Update_With_The_Message_And_Never_Calls_The_Model()
    {
        ScriptedChatClient client = new ScriptedChatClient().Responds(ScriptedTurn.Says("answer-1"));
        ScriptedDecisionProvider provider = ScriptedAgent.Flagging();
        AIAgent guarded = new AIAgentBuilder(ScriptedAgent.Over(client))
            .UseSemanticPolicyBeforeModel(ScriptedAgent.DenyPolicy(), ScriptedAgent.Evaluator(provider), Stops("stopped-1"))
            .Build();

        List<AgentResponseUpdate> updates = [];
        await foreach (AgentResponseUpdate update in guarded.RunStreamingAsync("user-text-1", cancellationToken: Token))
        {
            updates.Add(update);
        }

        AgentResponseUpdate only = updates.Should().ContainSingle().Which;
        only.Text.Should().Be("stopped-1");
        only.Role.Should().Be(ChatRole.Assistant);
        client.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PolicyMode.Shadow)]
    [InlineData(PolicyMode.Enforce)]
    public async Task Adapter_Applies_The_Handlers_Outcome_In_Every_Mode(PolicyMode mode)
    {
        ScriptedChatClient client = new ScriptedChatClient().Responds(ScriptedTurn.Says("answer-1"));
        ScriptedDecisionProvider provider = ScriptedAgent.Flagging();
        Policy policy = ScriptedAgent.DenyPolicy(mode);
        Verdict? effective = null;
        PreModelHandler handler = (_, verdict, _) =>
        {
            effective = verdict.Effective;
            return ValueTask.FromResult(PreModelOutcome.Stop("stopped-1"));
        };
        AIAgent guarded = new AIAgentBuilder(ScriptedAgent.Over(client))
            .UseSemanticPolicyBeforeModel(policy, ScriptedAgent.Evaluator(provider, policy), handler)
            .Build();

        AgentResponse response = await guarded.RunAsync("user-text-1", cancellationToken: Token);

        response.Text.Should().Be("stopped-1");
        client.Requests.Should().BeEmpty();
        effective.Should().Be(mode == PolicyMode.Enforce ? Verdict.Deny : Verdict.Allow);
    }

    private static PreModelHandler Proceeds() => (_, _, _) => ValueTask.FromResult(PreModelOutcome.Proceed);

    private static PreModelHandler Stops(string message) => (_, _, _) => ValueTask.FromResult(PreModelOutcome.Stop(message));

    // The next update that says something. An agent is free to emit updates that carry only metadata,
    // and the question here is when text reaches the consumer, not how many updates it took.
    private static async Task<string> NextText(IAsyncEnumerator<AgentResponseUpdate> updates)
    {
        while (await updates.MoveNextAsync())
        {
            if (!string.IsNullOrEmpty(updates.Current.Text))
            {
                return updates.Current.Text;
            }
        }

        return string.Empty;
    }
}
