using System.Text.Json;
using SemanticPolicy.Core.Tests.Support;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Core.Tests;

public sealed class PolicyEvaluatorTests
{
    private const string _rule = "r";
    private static readonly SemanticContext _context = SemanticContext.FromText("part-a");

    public static TheoryData<Rule, ProviderResult, Verdict> Decisions => new()
    {
        { Flagged(), ScriptedProvider.Success(new BooleanValue(true), ("true", 0.95), ("false", 0.05)), Verdict.Deny },
        { Graded(), ScriptedProvider.Success(new ChoiceValue("warn"), ("warn", 0.8), ("allow", 0.2)), Verdict.Warn },
        {
            Scaled(),
            ScriptedProvider.Success(new ScoreValue("high", 2), ("low", 0.1), ("mid", 0.2), ("high", 0.7)),
            Verdict.Escalate
        },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public async Task Each_Decision_Type_Evaluates_End_To_End_Through_A_Provider(
        Rule rule,
        ProviderResult scripted,
        Verdict expected)
    {
        ScriptedProvider provider = new ScriptedProvider("scripted").Returns(scripted);
        PolicyEvaluator evaluator = new([new ProviderRegistration("primary", provider)], []);
        Policy policy = Define(FailureBehavior.Deny, ["primary"], [rule]);

        PolicyVerdict verdict = await evaluator.EvaluateAsync(policy, _context, TestContext.Current.CancellationToken);

        verdict.PolicyId.Should().Be("p");
        verdict.Evaluated.Should().Be(expected);
        verdict.Effective.Should().Be(expected);
        RuleVerdict ruleVerdict = verdict.Rules.Should().ContainSingle().Subject;
        ruleVerdict.RuleId.Should().Be(_rule);
        ruleVerdict.DecidingBinding.Should().Be(0);
        Attempt attempt = ruleVerdict.Attempts.Should().ContainSingle().Subject;
        attempt.ProviderId.Should().Be("primary");
        attempt.Result.Should().BeSameAs(scripted);
        attempt.Disposition.Should().Be(AttemptDisposition.Decided);
        provider.Calls.Should().ContainSingle().Which.Request.Type.Should().Be(rule.Type);
    }

    [Fact]
    public async Task Rules_Are_Issued_Concurrently_In_One_Round()
    {
        ScriptedProvider provider = new ScriptedProvider().Holds(Unflagged());
        PolicyEvaluator evaluator = Evaluator(("primary", provider));
        Policy policy = Define(FailureBehavior.Deny, ["primary"], [Flagged("a"), Flagged("b"), Flagged("c")]);
        using CancellationTokenSource deadline = Deadline();

        Task<PolicyVerdict> evaluation = evaluator.EvaluateAsync(policy, _context, deadline.Token);
        await provider.WaitForCallsAsync(3, deadline.Token);

        evaluation.IsCompleted.Should().BeFalse();
        provider.Calls.Select(call => call.Request.Type)
            .Should().Equal(DecisionType.Boolean, DecisionType.Boolean, DecisionType.Boolean);
        provider.Release();
        PolicyVerdict verdict = await evaluation;
        verdict.Evaluated.Should().Be(Verdict.Allow);
        verdict.Rules.Select(rule => rule.RuleId).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Budget_Expiry_Becomes_Failure_Timeout_Under_The_Failure_Behaviour()
    {
        ScriptedProvider provider = new ScriptedProvider().Delays(TimeSpan.FromSeconds(30), Unflagged());
        PolicyEvaluator evaluator = Evaluator(("primary", provider));
        Policy policy = Define(FailureBehavior.Deny, ["primary"], [Flagged()], budget: TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource deadline = Deadline();

        PolicyVerdict verdict = await evaluator.EvaluateAsync(policy, _context, deadline.Token);

        verdict.Evaluated.Should().Be(Verdict.Deny);
        RuleVerdict rule = verdict.Rules.Should().ContainSingle().Subject;
        rule.Source.Should().Be(VerdictSource.FailureBehavior);
        Attempt attempt = rule.Attempts.Should().ContainSingle().Subject;
        attempt.Disposition.Should().Be(AttemptDisposition.TerminatedByFailure);
        attempt.Result.Type.Should().Be(DecisionType.Boolean);
        attempt.Result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        attempt.Result.Outcome.Kind.Should().Be(FailureKind.Timeout);
        attempt.Result.Provider.Id.Should().Be("primary");
        attempt.Result.Provider.Model.Should().BeNull();
        attempt.Result.Provider.LatencyMs.Should().BeGreaterThan(0);
        ScriptedProvider.Call call = provider.Calls.Should().ContainSingle().Subject;
        call.CancelledOnArrival.Should().BeFalse();
        call.Token.IsCancellationRequested.Should().BeTrue();
        deadline.IsCancellationRequested.Should().BeFalse();
    }

    public static TheoryData<string> Misconfigurations => new()
    {
        "unknown provider",
        "undeclared threshold kind",
        "undeclared gate kind",
        "unsupported decision type",
        "invalid policy",
        "duplicate provider name",
        "duplicate policy id",
    };

    [Theory]
    [MemberData(nameof(Misconfigurations))]
    public void Registered_Policies_Are_Validated_When_The_Evaluator_Is_Constructed(string @case)
    {
        Misconfiguration setup = Misconfigure(@case);

        Action construct = () => new PolicyEvaluator(setup.Providers, setup.Policies);

        PolicyConfigurationException error = construct.Should().Throw<PolicyConfigurationException>().Which;
        error.PolicyId.Should().Be(setup.PolicyId);
        error.ProviderId.Should().Be(setup.ProviderId);
        error.RuleId.Should().Be(setup.RuleId);
        error.Message.Should().NotContain("question-a");
        setup.Primary.Calls.Should().BeEmpty();
    }

    public static TheoryData<ProviderResult, FailureKind?> Undecided => new()
    {
        { ScriptedProvider.Failure(DecisionType.Boolean, FailureKind.Timeout), FailureKind.Timeout },
        { ScriptedProvider.Failure(DecisionType.Boolean, FailureKind.Unavailable), FailureKind.Unavailable },
        { ScriptedProvider.Failure(DecisionType.Boolean, FailureKind.Malformed), FailureKind.Malformed },
        { ScriptedProvider.Failure(DecisionType.Boolean, FailureKind.RejectedInput), FailureKind.RejectedInput },
        { ScriptedProvider.Failure(DecisionType.Boolean, FailureKind.Unauthorized), FailureKind.Unauthorized },
        { ScriptedProvider.Failure(DecisionType.Boolean, FailureKind.Unknown), FailureKind.Unknown },
        { ScriptedProvider.Abstain(DecisionType.Boolean, "declined"), null },
    };

    [Theory]
    [MemberData(nameof(Undecided))]
    public async Task Every_Failure_Kind_Flows_Through_The_Failure_Behaviour(ProviderResult scripted, FailureKind? kind)
    {
        ScriptedProvider provider = new ScriptedProvider().Returns(scripted);
        PolicyEvaluator evaluator = Evaluator(("primary", provider));
        Policy policy = Define(FailureBehavior.Escalate, ["primary"], [Flagged()]);

        PolicyVerdict verdict = await evaluator.EvaluateAsync(policy, _context, TestContext.Current.CancellationToken);

        verdict.Evaluated.Should().Be(Verdict.Escalate);
        RuleVerdict rule = verdict.Rules.Should().ContainSingle().Subject;
        rule.Source.Should().Be(VerdictSource.FailureBehavior);
        Attempt attempt = rule.Attempts.Should().ContainSingle().Subject;
        attempt.Result.Should().BeSameAs(scripted);
        attempt.EffectiveOutcome.Status.Should().Be(kind is null ? OutcomeStatus.Abstain : OutcomeStatus.Failure);
        attempt.EffectiveOutcome.Kind.Should().Be(kind);
        attempt.Disposition.Should().Be(AttemptDisposition.TerminatedByFailure);
    }

    [Fact]
    public async Task Evaluator_Sends_One_Request_Per_Rule_Carrying_The_Context_Object()
    {
        SemanticContext context = new([ContextPart.Text("a", "part-a"), ContextPart.Text("b", "part-b")], "corr-1");
        ScriptedProvider provider = new ScriptedProvider().Returns(request => request.Type switch
        {
            DecisionType.Boolean => Unflagged(),
            DecisionType.Choice => ScriptedProvider.Success(new ChoiceValue("allow"), ("allow", 0.9), ("deny", 0.1)),
            _ => ScriptedProvider.Success(new ScoreValue("low", 0), ("low", 0.9), ("mid", 0.05), ("high", 0.05)),
        });
        PolicyEvaluator evaluator = Evaluator(("primary", provider));
        Policy policy = Define(FailureBehavior.Deny, ["primary"], [Flagged("a"), Graded("b"), Scaled("c")]);

        PolicyVerdict verdict = await evaluator.EvaluateAsync(policy, context, TestContext.Current.CancellationToken);

        verdict.Evaluated.Should().Be(Verdict.Allow);
        IReadOnlyList<ScriptedProvider.Call> calls = provider.Calls;
        calls.Select(call => call.Request.Type)
            .Should().Equal(DecisionType.Boolean, DecisionType.Choice, DecisionType.Score);
        string wire = context.ToJson().GetRawText();
        foreach (ScriptedProvider.Call call in calls)
        {
            call.Request.Question.Should().Be("question-a");
            call.Request.Context.GetRawText().Should().Be(wire);
            JsonSerializer.Serialize(call.Request, SemanticPolicyJson.Options).Should().NotContain("corr-1");
        }

        calls[1].Request.Options!.Keys.Should().Equal("allow", "warn", "escalate", "deny");
        calls[2].Request.Levels.Should().Equal("low", "mid", "high");
    }

    public static TheoryData<ProviderResult, int, AttemptDisposition> Handoffs => new()
    {
        { Unflagged(), 0, AttemptDisposition.Decided },
        {
            ScriptedProvider.Success(new BooleanValue(true), ("true", 0.55), ("false", 0.45)),
            1,
            AttemptDisposition.MovedOnByGate
        },
        {
            ScriptedProvider.Failure(DecisionType.Boolean, FailureKind.Unavailable),
            1,
            AttemptDisposition.MovedOnByFailure
        },
    };

    [Theory]
    [MemberData(nameof(Handoffs))]
    public async Task Next_Binding_Is_Called_Only_After_The_Previous_One_Misses_The_Gate_Or_Fails(
        ProviderResult first,
        int fallbackCalls,
        AttemptDisposition disposition)
    {
        ScriptedProvider primary = new ScriptedProvider("one").Returns(first);
        ScriptedProvider fallback = new ScriptedProvider("two").Returns(Unflagged());
        PolicyEvaluator evaluator = Evaluator(("primary", primary), ("fallback", fallback));
        Policy policy = Define(FailureBehavior.Fallback(Verdict.Deny), ["primary", "fallback"], [Flagged()], gate: 0.2);

        PolicyVerdict verdict = await evaluator.EvaluateAsync(policy, _context, TestContext.Current.CancellationToken);

        verdict.Evaluated.Should().Be(Verdict.Allow);
        primary.Calls.Should().ContainSingle();
        fallback.Calls.Should().HaveCount(fallbackCalls);
        RuleVerdict rule = verdict.Rules.Should().ContainSingle().Subject;
        rule.Attempts.Select(attempt => attempt.ProviderId)
            .Should().Equal(fallbackCalls == 0 ? ["primary"] : ["primary", "fallback"]);
        rule.Attempts[0].Disposition.Should().Be(disposition);
        rule.DecidingBinding.Should().Be(fallbackCalls);
    }

    [Fact]
    public async Task Attempt_Required_After_Expiry_Is_Synthesized_Without_A_Provider_Call()
    {
        ScriptedProvider primary = new ScriptedProvider("one").Delays(TimeSpan.FromSeconds(30), Unflagged());
        ScriptedProvider fallback = new ScriptedProvider("two").Returns(Unflagged());
        PolicyEvaluator evaluator = Evaluator(("primary", primary), ("fallback", fallback));
        Policy policy = Define(
            FailureBehavior.Fallback(Verdict.Escalate),
            ["primary", "fallback"],
            [Flagged()],
            budget: TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource deadline = Deadline();

        PolicyVerdict verdict = await evaluator.EvaluateAsync(policy, _context, deadline.Token);

        verdict.Evaluated.Should().Be(Verdict.Escalate);
        fallback.Calls.Should().BeEmpty();
        RuleVerdict rule = verdict.Rules.Should().ContainSingle().Subject;
        rule.Source.Should().Be(VerdictSource.FailureBehavior);
        rule.Attempts.Should().HaveCount(2);
        Attempt timedOut = rule.Attempts[0];
        timedOut.BindingIndex.Should().Be(0);
        timedOut.ProviderId.Should().Be("primary");
        timedOut.Disposition.Should().Be(AttemptDisposition.MovedOnByFailure);
        timedOut.Result.Outcome.Kind.Should().Be(FailureKind.Timeout);
        timedOut.Result.Provider.Model.Should().BeNull();
        timedOut.Result.Provider.LatencyMs.Should().BeGreaterThan(0);
        Attempt synthesized = rule.Attempts[1];
        synthesized.BindingIndex.Should().Be(1);
        synthesized.ProviderId.Should().Be("fallback");
        synthesized.Disposition.Should().Be(AttemptDisposition.TerminatedByFailure);
        synthesized.Result.Type.Should().Be(DecisionType.Boolean);
        synthesized.Result.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        synthesized.Result.Outcome.Kind.Should().Be(FailureKind.Timeout);
        synthesized.Result.Provider.Id.Should().Be("fallback");
        synthesized.Result.Provider.Model.Should().BeNull();
        synthesized.Result.Provider.LatencyMs.Should().Be(0);
    }

    [Fact]
    public async Task Caller_Cancellation_Throws_And_Yields_No_Verdict()
    {
        ScriptedProvider provider = new ScriptedProvider().Holds(Unflagged());
        PolicyEvaluator evaluator = Evaluator(("primary", provider));
        Policy policy = Define(FailureBehavior.Allow, ["primary"], [Flagged()], budget: TimeSpan.FromSeconds(30));
        using CancellationTokenSource deadline = Deadline();
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);

        Task<PolicyVerdict> evaluation = evaluator.EvaluateAsync(policy, _context, caller.Token);
        await provider.WaitForCallsAsync(1, deadline.Token);
        await caller.CancelAsync();

        Func<Task> awaiting = () => evaluation.WaitAsync(deadline.Token);
        await awaiting.Should().ThrowAsync<OperationCanceledException>();
        evaluation.IsCompletedSuccessfully.Should().BeFalse();
        provider.Calls.Should().ContainSingle().Which.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Registered_Policy_Is_Evaluated_By_Id_And_An_Unknown_Id_Is_An_Argument_Error()
    {
        ScriptedProvider provider = new ScriptedProvider().Returns(Unflagged());
        Policy policy = Define(FailureBehavior.Deny, ["primary"], [Flagged()]);
        PolicyEvaluator evaluator = new([new ProviderRegistration("primary", provider)], [policy]);

        PolicyVerdict verdict = await evaluator.EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        verdict.PolicyId.Should().Be("p");
        verdict.Evaluated.Should().Be(Verdict.Allow);
        provider.Calls.Should().ContainSingle();

        Func<Task> unknown = () => evaluator.EvaluateAsync("ghost", _context, TestContext.Current.CancellationToken);

        (await unknown.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("ghost");
        provider.Calls.Should().ContainSingle();
    }

    public static TheoryData<string> PolicyMisconfigurations => new()
    {
        "unknown provider",
        "undeclared threshold kind",
        "undeclared gate kind",
        "unsupported decision type",
        "invalid policy",
    };

    [Theory]
    [MemberData(nameof(PolicyMisconfigurations))]
    public async Task Ad_Hoc_Policy_Is_Validated_Before_Its_First_Provider_Call(string @case)
    {
        Misconfiguration setup = Misconfigure(@case);
        PolicyEvaluator evaluator = new(setup.Providers, []);

        Func<Task> evaluate = () =>
            evaluator.EvaluateAsync(setup.Policies[0], _context, TestContext.Current.CancellationToken);

        PolicyConfigurationException error = (await evaluate.Should().ThrowAsync<PolicyConfigurationException>()).Which;
        error.PolicyId.Should().Be(setup.PolicyId);
        error.ProviderId.Should().Be(setup.ProviderId);
        error.RuleId.Should().Be(setup.RuleId);
        error.Message.Should().NotContain("question-a");
        setup.Primary.Calls.Should().BeEmpty();
    }

    public static TheoryData<Exception> Escapes => new()
    {
        new InvalidOperationException("scripted"),
        new OperationCanceledException("scripted"),
    };

    [Theory]
    [MemberData(nameof(Escapes))]
    public async Task Provider_Exception_Propagates_Unchanged(Exception scripted)
    {
        ScriptedProvider provider = new ScriptedProvider().Throws(scripted);
        PolicyEvaluator evaluator = Evaluator(("primary", provider));
        Policy policy = Define(FailureBehavior.Allow, ["primary"], [Flagged()], budget: TimeSpan.FromSeconds(30));

        Func<Task> evaluate = () => evaluator.EvaluateAsync(policy, _context, TestContext.Current.CancellationToken);

        (await evaluate.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(scripted);
        provider.Calls.Should().ContainSingle().Which.Token.IsCancellationRequested.Should().BeFalse();
    }

    private static PolicyEvaluator Evaluator(params (string Name, ScriptedProvider Provider)[] providers) =>
        new(providers.Select(entry => new ProviderRegistration(entry.Name, entry.Provider)), []);

    // A hold that is never released, or a call that is never answered, fails the test instead of hanging it.
    private static CancellationTokenSource Deadline()
    {
        CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return deadline;
    }

    private static ProviderResult Unflagged() =>
        ScriptedProvider.Success(new BooleanValue(false), ("true", 0.1), ("false", 0.9));

    // Every policy here is "p": the rules given, the providers given in chain order, and on each provider
    // Warn at 0.6 / Deny at 0.9 on probability for every Boolean rule plus the gate when one is asked for.
    private static Policy Define(
        FailureBehavior onFailure,
        string[] providers,
        Rule[] rules,
        PolicyMode mode = PolicyMode.Enforce,
        double? gate = null,
        TimeSpan? budget = null)
    {
        PolicyBuilder builder = mode == PolicyMode.Enforce ? Policy.Define("p").Enforce() : Policy.Define("p").Shadow();
        foreach (Rule rule in rules)
        {
            builder.Rule(rule);
        }

        bool thresholds = rules.Any(rule => rule is BooleanRule);
        foreach (string provider in providers)
        {
            builder.Using(provider, b =>
            {
                if (thresholds)
                {
                    b.WarnAboveProbability(0.6).DenyAboveProbability(0.9);
                }

                if (gate is { } below)
                {
                    b.WhenProbabilityMarginBelow(below);
                }
            });
        }

        if (budget is { } span)
        {
            builder.Budget(span);
        }

        return builder.OnFailure(onFailure).Build();
    }

    // One way a policy cannot be served, as the registrations and policies that exhibit it, with the ids
    // the exception must name. The policy always binds to "primary".
    private static Misconfiguration Misconfigure(string @case)
    {
        ProviderCapabilities everything = ScriptedProvider.Everything;
        ScriptedProvider primary = new();
        switch (@case)
        {
            case "unknown provider":
                return One(primary, Define(FailureBehavior.Deny, ["ghost"], [Flagged()]), "p", "ghost", null);
            case "undeclared threshold kind":
                primary = new(capabilities: everything with { Evidence = Kinds(EvidenceKind.Score) });
                return One(primary, Define(FailureBehavior.Deny, ["primary"], [Flagged()]), "p", "primary", _rule);
            case "undeclared gate kind":
                primary = new(capabilities: everything with { Evidence = Kinds(EvidenceKind.Probability) });
                Policy scoreGated = Policy.Define("p").Enforce().Rule(Graded())
                    .Using("primary", b => b.WhenScoreMarginBelow(0.2))
                    .OnFailure(FailureBehavior.Deny)
                    .Build();
                return One(primary, scoreGated, "p", "primary", _rule);
            case "unsupported decision type":
                primary = new(capabilities: everything with { Types = Types(DecisionType.Boolean) });
                return One(primary, Define(FailureBehavior.Deny, ["primary"], [Graded()]), "p", "primary", _rule);
            case "invalid policy":
                Policy noRules = new(
                    "p",
                    PolicyMode.Enforce,
                    Rules: [],
                    Bindings: [new ProviderBinding("primary", [])],
                    FailureBehavior.Deny);
                return One(primary, noRules, "p", null, null);
            case "duplicate provider name":
                ProviderRegistration[] twice = [Register(primary), Register(new ScriptedProvider("other"))];
                return new(twice, [], primary, PolicyId: null, ProviderId: "primary", RuleId: null);
            case "duplicate policy id":
                Policy policy = Define(FailureBehavior.Deny, ["primary"], [Flagged()]);
                Policy[] shared = [policy, policy with { Mode = PolicyMode.Shadow }];
                return new([Register(primary)], shared, primary, PolicyId: "p", ProviderId: null, RuleId: null);
            default:
                throw new ArgumentOutOfRangeException(nameof(@case));
        }

        static Misconfiguration One(
            ScriptedProvider primary,
            Policy policy,
            string? policyId,
            string? providerId,
            string? ruleId) =>
            new([Register(primary)], [policy], primary, policyId, providerId, ruleId);

        static HashSet<EvidenceKind> Kinds(params EvidenceKind[] kinds) => [.. kinds];

        static HashSet<DecisionType> Types(params DecisionType[] types) => [.. types];
    }

    private static ProviderRegistration Register(ScriptedProvider provider) => new("primary", provider);

    private sealed record Misconfiguration(
        ProviderRegistration[] Providers,
        Policy[] Policies,
        ScriptedProvider Primary,
        string? PolicyId,
        string? ProviderId,
        string? RuleId);

    private static BooleanRule Flagged(string id = _rule) =>
        Policy.Rule(id).Boolean("question-a").WhenTrue(Verdict.Warn, Verdict.Deny);

    // A Choice rule whose option keys are the verdicts they map to.
    private static ChoiceRule Graded(string id = _rule) =>
        Policy.Rule(id).Choice("question-a")
            .Option("allow", "a", Verdict.Allow)
            .Option("warn", "w", Verdict.Warn)
            .Option("escalate", "e", Verdict.Escalate)
            .Option("deny", "d", Verdict.Deny)
            .Build();

    private static ScoreRule Scaled(string id = _rule) =>
        Policy.Rule(id).Score("question-a", "low", "mid", "high")
            .WarnAtOrAbove("mid")
            .EscalateAtOrAbove("high")
            .Build();
}
