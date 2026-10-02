using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Extensions.AI.Tests.Support;

namespace SemanticPolicy.Extensions.AI.Tests;

public sealed class RegistrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> MissingArguments()
    {
        TheoryData<string, string> cases = [];
        foreach (string point in new[] { "before-tool", "after-tool" })
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
        ScriptedDecisionProvider provider = ScriptedLoop.Flagging();
        ServiceCollection services = new();
        services.AddSemanticPolicy().AddProvider(provider).AddPolicy(ScriptedLoop.DenyPolicy());
        using ServiceProvider container = services.BuildServiceProvider();
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = OneCallThenText();
        IChatClient guarded = new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.PolicyId, Proceeds())
            .UseFunctionInvocation()
            .Build(container);

        ChatResponse response = await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);

        response.Text.Should().Be("final-1");
        provider.Requests.Should().ContainSingle();
        branches.Arguments.Should().Equal("test-old");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DI_Road_Without_An_Evaluator_Fails_At_Build(bool withContainer)
    {
        using ServiceProvider container = new ServiceCollection().BuildServiceProvider();
        ScriptedChatClient client = OneCallThenText();
        ChatClientBuilder builder = new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.PolicyId, Proceeds())
            .UseFunctionInvocation();

        Action build = withContainer ? () => builder.Build(container) : () => builder.Build();

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*IPolicyEvaluator*AddSemanticPolicy()*Build(services)*");
        client.Requests.Should().BeEmpty();
    }

    [Fact]
    public void DI_Road_With_An_Unregistered_Policy_Id_Fails_At_Build()
    {
        ServiceCollection services = new();
        services.AddSemanticPolicy().AddProvider(ScriptedLoop.Flagging()).AddPolicy(ScriptedLoop.DenyPolicy());
        using ServiceProvider container = services.BuildServiceProvider();
        ScriptedChatClient client = OneCallThenText();
        ChatClientBuilder builder = new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool("no-such-policy", Proceeds())
            .UseFunctionInvocation();

        Action build = () => builder.Build(container);

        build.Should().Throw<ArgumentException>()
            .WithMessage("*no-such-policy*")
            .Which.ParamName.Should().Be("policyId");
        client.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Guard_Without_A_Function_Invoking_Client_Below_Fails_At_Build(bool writtenAfter)
    {
        ScriptedChatClient client = OneCallThenText();
        ChatClientBuilder builder = new(client);
        if (writtenAfter)
        {
            builder.UseFunctionInvocation();
        }

        builder.UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), ScriptedLoop.Evaluator(ScriptedLoop.Flagging()), Proceeds());

        Action build = () => builder.Build();

        build.Should().Throw<InvalidOperationException>().WithMessage("*before UseFunctionInvocation()*");
        client.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_Road_Needs_No_Container()
    {
        ScriptedDecisionProvider provider = ScriptedLoop.Flagging();
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = OneCallThenText();
        IChatClient guarded = new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), ScriptedLoop.Evaluator(provider), Proceeds())
            .UseFunctionInvocation()
            .Build();

        ChatResponse response = await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);

        response.Text.Should().Be("final-1");
        provider.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Builder_Over_A_Client_Constructed_By_Hand_Fails_On_A_Second_Build()
    {
        ScriptedDecisionProvider provider = ScriptedLoop.Flagging();
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        FunctionInvokingChatClient loop = new(OneCallThenText());
        ChatClientBuilder builder = new ChatClientBuilder(loop)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), ScriptedLoop.Evaluator(provider), Proceeds());
        IChatClient guarded = builder.Build();

        Action second = () => builder.Build();

        second.Should().Throw<InvalidOperationException>()
            .WithMessage("*UseSemanticPolicyBeforeTool*already wrapped*UseFunctionInvocation()*");
        await guarded.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);
        provider.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Builder_With_Its_Own_Function_Invocation_Can_Be_Built_Again()
    {
        ScriptedDecisionProvider provider = ScriptedLoop.Flagging();
        StubTool branches = new("delete_branch", "Deletes a branch.", "deleted-ok");
        ScriptedChatClient client = new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", ScriptedLoop.Named("test-old")),
            ScriptedTurn.Says("final-1"),
            ScriptedTurn.Calls("call_2", "delete_branch", ScriptedLoop.Named("test-older")),
            ScriptedTurn.Says("final-2"));
        ChatClientBuilder builder = new ChatClientBuilder(client)
            .UseSemanticPolicyBeforeTool(ScriptedLoop.DenyPolicy(), ScriptedLoop.Evaluator(provider), Proceeds())
            .UseFunctionInvocation();

        IChatClient first = builder.Build();
        IChatClient second = builder.Build();
        await first.GetResponseAsync("delete branch test-old", ScriptedLoop.With(branches), Token);
        await second.GetResponseAsync("delete branch test-older", ScriptedLoop.With(branches), Token);

        provider.Requests.Should().HaveCount(2);
        branches.Arguments.Should().Equal("test-old", "test-older");
    }

    private static ScriptedChatClient OneCallThenText() =>
        new ScriptedChatClient().Responds(
            ScriptedTurn.Calls("call_1", "delete_branch", ScriptedLoop.Named("test-old")),
            ScriptedTurn.Says("final-1"));

    private static PreToolHandler Proceeds() => (_, _, _) => ValueTask.FromResult(PreToolOutcome.Proceed);

    // One registration with exactly one argument missing. The builder is never built, so whatever the
    // methods reject here they reject at the call, where the application can still see it.
    private static void Register(string point, string missing)
    {
        ChatClientBuilder? builder = missing == "builder" ? null : new ChatClientBuilder(new ScriptedChatClient());
        string id = missing == "id" ? "  " : ScriptedLoop.PolicyId;
        Policy? policy = missing == "policy" ? null : ScriptedLoop.DenyPolicy();
        IPolicyEvaluator? evaluator = missing == "evaluator" ? null : ScriptedLoop.Evaluator(ScriptedLoop.Flagging());
        bool explicitRoad = missing is "policy" or "evaluator" or "policy-handler";
        bool handler = !missing.EndsWith("handler", StringComparison.Ordinal);

        if (point == "before-tool")
        {
            PreToolHandler? preTool = handler ? Proceeds() : null;
            if (explicitRoad)
            {
                builder!.UseSemanticPolicyBeforeTool(policy!, evaluator!, preTool!);
            }
            else
            {
                builder!.UseSemanticPolicyBeforeTool(id, preTool!);
            }
        }
        else
        {
            PostToolHandler? postTool = handler ? (_, _, _) => ValueTask.FromResult(PostToolOutcome.Proceed) : null;
            if (explicitRoad)
            {
                builder!.UseSemanticPolicyAfterTool(policy!, evaluator!, postTool!);
            }
            else
            {
                builder!.UseSemanticPolicyAfterTool(id, postTool!);
            }
        }
    }
}
