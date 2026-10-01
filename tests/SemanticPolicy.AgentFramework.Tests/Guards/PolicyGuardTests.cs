using System.Diagnostics;
using System.Text.Json;
using SemanticPolicy.AgentFramework.Tests.Support;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Guards;
using SemanticPolicy.Providers;

namespace SemanticPolicy.AgentFramework.Tests.Guards;

public sealed class PolicyGuardTests
{
    [Fact]
    public async Task Evaluation_Activity_Carries_The_Subjects_Correlation_Id()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(ScriptedDecisionProvider.Boolean(true, 0.95));
        PolicyGuard<ToolCall, PreToolOutcome> guard = new(GuardSubject.PreTool, Evaluator(provider, PolicyMode.Enforce), "p", Proceed);

        // The listener hears every source in the process, and test classes run in parallel, so the call
        // id has to be one no other test uses or a sibling's evaluation lands in this one's assertion.
        Activity[] traced = await Traced(
            () => guard.EvaluateAsync(Call("call_guard_activity"), CancellationToken.None),
            "call_guard_activity");

        Activity activity = traced.Should().ContainSingle().Which;
        activity.OperationName.Should().Be("semanticpolicy.evaluate");
        activity.Source.Name.Should().Be("SemanticPolicy");
    }

    // A true at 0.95 crosses the Deny rung; a true at 0.55 sits under the margin gate, so the policy
    // abstains rather than decides.
    [Theory]
    [InlineData(PolicyMode.Shadow, 0.95, Verdict.Allow, Verdict.Deny)]
    [InlineData(PolicyMode.Enforce, 0.95, Verdict.Deny, Verdict.Deny)]
    [InlineData(PolicyMode.Enforce, 0.55, Verdict.Abstain, Verdict.Abstain)]
    public async Task Handler_Receives_The_Verdict_As_The_Policy_Decided_It(
        PolicyMode mode,
        double probabilityOfTrue,
        Verdict effective,
        Verdict evaluated)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(ScriptedDecisionProvider.Boolean(true, probabilityOfTrue));
        ToolCall call = Call("call_handler");
        PreToolOutcome decided = PreToolOutcome.Refuse("refused-1");
        ToolCall? received = null;
        PolicyVerdict? seen = null;
        PreToolHandler handler = (subject, verdict, _) =>
        {
            received = subject;
            seen = verdict;
            return ValueTask.FromResult(decided);
        };
        PolicyGuard<ToolCall, PreToolOutcome> guard = new(GuardSubject.PreTool, Evaluator(provider, mode), "p", handler.Invoke);

        (PolicyVerdict returned, PreToolOutcome outcome) = await guard.GuardAsync(call, CancellationToken.None);

        received.Should().BeSameAs(call);
        seen.Should().BeSameAs(returned);
        returned.Effective.Should().Be(effective);
        returned.Evaluated.Should().Be(evaluated);
        outcome.Should().BeSameAs(decided);
    }

    [Fact]
    public async Task Evaluate_Half_Returns_The_Verdict_Without_Calling_The_Handler()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(ScriptedDecisionProvider.Boolean(true, 0.95));
        bool invoked = false;
        PolicyGuard<ToolCall, PreToolOutcome> guard = new(
            GuardSubject.PreTool,
            Evaluator(provider, PolicyMode.Enforce),
            "p",
            (_, _, _) =>
            {
                invoked = true;
                return ValueTask.FromResult(PreToolOutcome.Proceed);
            });

        PolicyVerdict verdict = await guard.EvaluateAsync(Call("call_evaluate"), CancellationToken.None);

        verdict.Should().BeEquivalentTo(
            new { PolicyId = "p", Mode = PolicyMode.Enforce, Effective = Verdict.Deny, Evaluated = Verdict.Deny });
        invoked.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "call_override")]
    [InlineData("own_1", "own_1")]
    public async Task Override_Delegate_Replaces_The_Default_Context_And_Keeps_The_Correlation_Id(
        string? overrideId,
        string expectedId)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(ScriptedDecisionProvider.Boolean(true, 0.95));
        PolicyGuard<ToolCall, PreToolOutcome> guard = new(
            GuardSubject.PreTool,
            Evaluator(provider, PolicyMode.Enforce),
            "p",
            Proceed,
            _ => new SemanticContext([ContextPart.Text("question", "text-question")], overrideId));

        Activity[] traced = await Traced(() => guard.EvaluateAsync(Call("call_override"), CancellationToken.None), expectedId);

        provider.Requests.Should().ContainSingle().Which.Context.EnumerateObject().Select(property => property.Name).Should().Equal("question");
        traced.Should().ContainSingle().Which.OperationName.Should().Be("semanticpolicy.evaluate");
    }

    [Theory]
    [InlineData("pre-model")]
    [InlineData("pre-tool")]
    [InlineData("post-tool")]
    public async Task Without_An_Override_The_Provider_Receives_The_Subjects_Own_Context(string point)
    {
        ModelInput input = new([new("user", "text-user")], "run_default");
        ToolCall call = Call("call_default");
        ToolResult result = new(call, "text-result");

        (JsonElement seen, SemanticContext expected) = point switch
        {
            "pre-model" => (await ContextSeen(GuardSubject.PreModel, input, PreModelOutcome.Proceed), input.ToSemanticContext()),
            "pre-tool" => (await ContextSeen(GuardSubject.PreTool, call, PreToolOutcome.Proceed), call.ToSemanticContext()),
            "post-tool" => (await ContextSeen(GuardSubject.PostTool, result, PostToolOutcome.Proceed), result.ToSemanticContext()),
            _ => throw new ArgumentOutOfRangeException(nameof(point)),
        };

        seen.GetRawText().Should().Be(expected.ToJson().GetRawText());
    }

    [Fact]
    public async Task Cancelling_The_Token_Ends_The_Guard_Without_A_Verdict()
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().AnswersOnlyOnCancellation();
        bool invoked = false;
        PolicyGuard<ToolCall, PreToolOutcome> guard = new(
            GuardSubject.PreTool,
            Evaluator(provider, PolicyMode.Enforce),
            "p",
            (_, _, _) =>
            {
                invoked = true;
                return ValueTask.FromResult(PreToolOutcome.Proceed);
            });
        using CancellationTokenSource cancellation = new();

        Task<(PolicyVerdict Verdict, PreToolOutcome Outcome)> guarding = guard.GuardAsync(Call("call_cancel"), cancellation.Token);
        provider.Requests.Should().ContainSingle();
        cancellation.Cancel();

        Func<Task> completing = () => guarding;
        await completing.Should().ThrowAsync<OperationCanceledException>();
        invoked.Should().BeFalse();
    }

    // Every activity, from any source, that stopped during the action carrying the correlation id as
    // its tag. Test classes run in parallel and the listener sees all of them, hence the filter.
    private static async Task<Activity[]> Traced(Func<Task> action, string correlationId)
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
            return [.. stopped.Where(activity => activity.GetTagItem("semanticpolicy.correlation_id") as string == correlationId)];
        }
    }

    // The context the provider saw for one evaluation of the subject through the guard's evaluate half.
    private static async Task<JsonElement> ContextSeen<TSubject, TOutcome>(
        GuardSubject<TSubject> at,
        TSubject subject,
        TOutcome outcome)
    {
        ScriptedDecisionProvider provider = new ScriptedDecisionProvider().Returns(ScriptedDecisionProvider.Boolean(true, 0.95));
        PolicyGuard<TSubject, TOutcome> guard = new(
            at,
            Evaluator(provider, PolicyMode.Enforce),
            "p",
            (_, _, _) => ValueTask.FromResult(outcome));

        await guard.EvaluateAsync(subject, CancellationToken.None);

        return provider.Requests.Should().ContainSingle().Which.Context;
    }

    private static ValueTask<PreToolOutcome> Proceed(ToolCall call, PolicyVerdict verdict, CancellationToken cancellationToken) =>
        ValueTask.FromResult(PreToolOutcome.Proceed);

    private static PolicyEvaluator Evaluator(ScriptedDecisionProvider provider, PolicyMode mode) =>
        new([new ProviderRegistration("scripted", provider)], [DenyPolicy(mode)]);

    // "p": one Boolean rule on the scripted provider, Deny at 0.9, Abstain under a margin of 0.2, Deny
    // on failure.
    private static Policy DenyPolicy(PolicyMode mode)
    {
        PolicyBuilder policy = mode == PolicyMode.Enforce ? Policy.Define("p").Enforce() : Policy.Define("p").Shadow();
        return policy
            .Rule(Policy.Rule("r").Boolean("question-1").WhenTrue(Verdict.Deny))
            .Using("scripted", binding => binding.DenyAboveProbability(0.9).WhenProbabilityMarginBelow(0.2))
            .OnFailure(FailureBehavior.Deny)
            .Build();
    }

    private static ToolCall Call(string correlationId)
    {
        using JsonDocument arguments = JsonDocument.Parse("{}");
        return new ToolCall("search", null, arguments.RootElement, [new("user", "text-user")], correlationId);
    }
}
