using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.AgentFramework.Tests.Support;

namespace SemanticPolicy.AgentFramework.Tests;

public sealed class RegistrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> MissingArguments()
    {
        TheoryData<string, string> cases = [];
        foreach (string point in new[] { "before-model", "before-tool", "after-tool" })
        {
            foreach (string missing in new[] { "builder", "id", "id-handler", "policy", "evaluator", "policy-handler" })
            {
                cases.Add(point, missing);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(MissingArguments))]
    public void Use_Methods_Reject_Missing_Arguments(string point, string missing)
    {
        Action use = () => Register(point, missing);

        if (missing == "id")
        {
            use.Should().Throw<ArgumentException>().Which.Should().NotBeOfType<ArgumentNullException>();
        }
        else
        {
            use.Should().Throw<ArgumentNullException>();
        }
    }

    [Fact]
    public async Task DI_Road_Resolves_The_Evaluator_From_The_Container_Given_To_Build()
    {
        ScriptedDecisionProvider provider = ScriptedAgent.Flagging();
        ServiceCollection services = new();
        services.AddSemanticPolicy().AddProvider(provider).AddPolicy(ScriptedAgent.DenyPolicy());
        using ServiceProvider container = services.BuildServiceProvider();
        ScriptedChatClient client = new ScriptedChatClient().Responds(ScriptedTurn.Says("answer-1"));
        AIAgent guarded = new AIAgentBuilder(ScriptedAgent.Over(client))
            .UseSemanticPolicyBeforeModel(ScriptedAgent.PolicyId, Proceeds())
            .Build(container);

        AgentResponse response = await guarded.RunAsync("user-text-1", cancellationToken: Token);

        response.Text.Should().Be("answer-1");
        provider.Requests.Should().ContainSingle();
    }

    [Fact]
    public void DI_Road_Without_A_Container_Fails_At_Build()
    {
        ScriptedChatClient client = new ScriptedChatClient().Responds(ScriptedTurn.Says("answer-1"));
        AIAgentBuilder builder = new AIAgentBuilder(ScriptedAgent.Over(client))
            .UseSemanticPolicyBeforeModel(ScriptedAgent.PolicyId, Proceeds());

        Action build = () => builder.Build();

        build.Should().Throw<InvalidOperationException>().WithMessage("*IPolicyEvaluator*");
        client.Requests.Should().BeEmpty();
    }

    [Fact]
    public void DI_Road_With_An_Unregistered_Policy_Id_Fails_At_Build()
    {
        ServiceCollection services = new();
        services.AddSemanticPolicy().AddProvider(ScriptedAgent.Flagging()).AddPolicy(ScriptedAgent.DenyPolicy());
        using ServiceProvider container = services.BuildServiceProvider();
        ScriptedChatClient client = new ScriptedChatClient().Responds(ScriptedTurn.Says("answer-1"));
        AIAgentBuilder builder = new AIAgentBuilder(ScriptedAgent.Over(client))
            .UseSemanticPolicyBeforeModel("no-such-policy", Proceeds());

        Action build = () => builder.Build(container);

        build.Should().Throw<ArgumentException>().WithMessage("*no-such-policy*");
        client.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_Road_Needs_No_Container()
    {
        ScriptedDecisionProvider provider = ScriptedAgent.Flagging();
        ScriptedChatClient client = new ScriptedChatClient().Responds(ScriptedTurn.Says("answer-1"));
        AIAgent guarded = new AIAgentBuilder(ScriptedAgent.Over(client))
            .UseSemanticPolicyBeforeModel(ScriptedAgent.DenyPolicy(), ScriptedAgent.Evaluator(provider), Proceeds())
            .Build();

        AgentResponse response = await guarded.RunAsync("user-text-1", cancellationToken: Token);

        response.Text.Should().Be("answer-1");
        provider.Requests.Should().ContainSingle();
    }

    private static PreModelHandler Proceeds() => (_, _, _) => ValueTask.FromResult(PreModelOutcome.Proceed);

    // One registration with exactly one argument missing. The builder is never built, so whatever the
    // methods reject here they reject at the call, where the application can still see it.
    private static void Register(string point, string missing)
    {
        AIAgentBuilder? builder = missing == "builder"
            ? null
            : new AIAgentBuilder(ScriptedAgent.Over(new ScriptedChatClient()));
        string id = missing == "id" ? "  " : ScriptedAgent.PolicyId;
        Policy? policy = missing == "policy" ? null : ScriptedAgent.DenyPolicy();
        IPolicyEvaluator? evaluator = missing == "evaluator" ? null : ScriptedAgent.Evaluator(ScriptedAgent.Flagging());
        bool explicitRoad = missing is "policy" or "evaluator" or "policy-handler";
        bool handler = !missing.EndsWith("handler", StringComparison.Ordinal);

        switch (point)
        {
            case "before-model":
                PreModelHandler? preModel = handler ? Proceeds() : null;
                if (explicitRoad)
                {
                    builder!.UseSemanticPolicyBeforeModel(policy!, evaluator!, preModel!);
                }
                else
                {
                    builder!.UseSemanticPolicyBeforeModel(id, preModel!);
                }

                break;
            case "before-tool":
                PreToolHandler? preTool = handler ? (_, _, _) => ValueTask.FromResult(PreToolOutcome.Proceed) : null;
                if (explicitRoad)
                {
                    builder!.UseSemanticPolicyBeforeTool(policy!, evaluator!, preTool!);
                }
                else
                {
                    builder!.UseSemanticPolicyBeforeTool(id, preTool!);
                }

                break;
            default:
                PostToolHandler? postTool = handler ? (_, _, _) => ValueTask.FromResult(PostToolOutcome.Proceed) : null;
                if (explicitRoad)
                {
                    builder!.UseSemanticPolicyAfterTool(policy!, evaluator!, postTool!);
                }
                else
                {
                    builder!.UseSemanticPolicyAfterTool(id, postTool!);
                }

                break;
        }
    }
}
