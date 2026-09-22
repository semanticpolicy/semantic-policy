using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class ReplaySetEvaluateTests
{
    [Fact]
    public async Task Replay_Of_The_Recorded_Policy_Yields_The_Verdict_A_Direct_Evaluate_Call_Yields()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local", "jev"], [Samples.Flagged()], gate: 0.2);
        ProviderResult local = Samples.BooleanAnswer(0.52);
        ProviderResult jev = Samples.BooleanAnswer(0.95, "jev");
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(policy),
            Samples.Recorded("a", (Samples.Injection, "local", local), (Samples.Injection, "jev", jev)));
        ReplaySet set = ReplaySet.Load(recording, Samples.Inputs(policy, Samples.Dataset(Samples.Row("a", "true"))), false);

        IReadOnlyList<EvaluatedRow> replayed = set.Evaluate();

        Dictionary<AttemptKey, ProviderResult> attempts = new()
        {
            [new AttemptKey(Samples.Injection, 0)] = local,
            [new AttemptKey(Samples.Injection, 1)] = jev,
        };
        RuleVerdict direct = PolicyEvaluation.Evaluate(policy, attempts).Verdict!.Rules.Single();
        EvaluatedRow row = replayed.Should().ContainSingle().Subject;
        row.Row.Id.Should().Be("a");
        row.Verdict.Should().BeEquivalentTo(direct, options => options.PreferringRuntimeMemberTypes());
        row.Verdict.Verdict.Should().Be(Verdict.Deny);
        row.Verdict.DecidingBinding.Should().Be(1);
        row.Verdict.Attempts.Select(attempt => attempt.ProviderId).Should().Equal("local", "jev");
    }

    [Fact]
    public async Task Replay_Under_Reordered_Bindings_Reads_The_Same_Results_In_The_New_Order()
    {
        using TempFile file = TempFile.Write("");
        Policy recorded = Samples.Guard(FailureBehavior.Deny, ["local", "jev"], [Samples.Flagged()]);
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(recorded),
            Samples.Recorded(
                "a",
                (Samples.Injection, "local", Samples.BooleanAnswer(0.95)),
                (Samples.Injection, "jev", Samples.BooleanAnswer(0.2, "jev"))));
        ReplaySet set = ReplaySet.Load(
            recording,
            Samples.Inputs(recorded, Samples.Dataset(Samples.Row("a", "true"))),
            force: false);
        Policy reordered = recorded with { Bindings = [.. recorded.Bindings.Reverse()] };

        RuleVerdict asRecorded = set.Evaluate().Single().Verdict;
        RuleVerdict swapped = set.Evaluate(reordered).Single().Verdict;

        asRecorded.Attempts.Should().ContainSingle().Which.ProviderId.Should().Be("local");
        asRecorded.Verdict.Should().Be(Verdict.Deny);
        swapped.Attempts.Should().ContainSingle().Which.ProviderId.Should().Be("jev");
        swapped.Attempts[0].Result.Provider.Id.Should().Be("jev");
        swapped.Verdict.Should().Be(Verdict.Allow);
        swapped.DecidingBinding.Should().Be(0);
    }

    [Fact]
    public async Task Evaluate_Rejects_A_Binding_Whose_Provider_Has_No_Recorded_Attempt_Naming_Rule_Provider_And_Row()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local", "jev"], [Samples.Flagged()]);
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(policy),
            Samples.Recorded("a", (Samples.Injection, "local", Samples.BooleanAnswer(0.95))),
            Samples.Recorded("b", (Samples.Injection, "local", Samples.BooleanAnswer(0.1))));
        LoadedInputs inputs = Samples.Inputs(
            policy,
            Samples.Dataset(Samples.Row("a", "true"), Samples.Row("b", "false", 2)));
        ReplaySet set = ReplaySet.Load(recording, inputs, force: false);

        Action evaluate = () => set.Evaluate();

        evaluate.Should().Throw<EvalsException>().Which.Message.Should()
            .Contain(Samples.Injection).And.Contain("jev").And.Contain("'a'");
    }

    [Fact]
    public async Task Replay_Narrows_To_The_Selected_Rule_So_Other_Rules_Attempts_Are_Not_Required()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged(), Samples.Route()]);
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(policy),
            Samples.Recorded("a", ("route", "local", Samples.Answer(new ChoiceValue("deny")))));
        LoadedInputs inputs = Samples.Inputs(policy, Samples.Dataset(Samples.Row("a", "deny")), "route");

        IReadOnlyList<EvaluatedRow> rows = ReplaySet.Load(recording, inputs, force: false).Evaluate();

        // The Choice rule carries no gate, so after narrowing the binding holds no operating point at all;
        // Core accepts that shape and this pins it.
        policy.Bindings.Should().ContainSingle().Which.OperatingPoints.Should().ContainSingle()
            .Which.RuleId.Should().Be(Samples.Injection);
        RuleVerdict verdict = rows.Should().ContainSingle().Subject.Verdict;
        verdict.RuleId.Should().Be("route");
        verdict.Verdict.Should().Be(Verdict.Deny);
        verdict.Source.Should().Be(VerdictSource.OptionMap);
    }
}
