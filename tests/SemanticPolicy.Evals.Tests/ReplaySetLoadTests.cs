using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class ReplaySetLoadTests
{
    [Fact]
    public async Task Reader_Accepts_A_Recording_With_Fewer_Rows_Than_The_Dataset_And_Counts_N_Of_M()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(policy),
            Samples.Recorded("a", (Samples.Injection, "local", Samples.BooleanAnswer(0.95))),
            Samples.Recorded("c", (Samples.Injection, "local", Samples.BooleanAnswer(0.1))));
        LoadedInputs inputs = Samples.Inputs(
            policy,
            Samples.Dataset(Samples.Row("a", "true"), Samples.Row("b", "false", 2), Samples.Row("c", "false", 3)));

        ReplaySet set = ReplaySet.Load(recording, inputs, force: false);

        set.RecordedRowCount.Should().Be(2);
        set.DatasetRowCount.Should().Be(3);
        set.Rows.Select(row => row.Row.Id).Should().Equal("a", "c");
        set.Rows[0].AttemptsByProvider.Keys.Should().Equal("local");
        set.Rows[0].AttemptsByProvider["local"].Value.Should().Be(new BooleanValue(true));
        set.Policy.Should().BeSameAs(inputs.Policy);
        set.Rule.Should().BeSameAs(inputs.Rule);
        set.Header.Should().BeSameAs(recording.Header);
    }

    [Fact]
    public async Task Loader_Refuses_A_Dataset_Whose_Hash_Differs_Unless_Forced()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
        const string edited = "0000000000000000000000000000000000000000000000000000000000000000";
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(policy, sha: edited),
            Samples.Recorded("a", (Samples.Injection, "local", Samples.BooleanAnswer(0.95))));
        LoadedInputs inputs = Samples.Inputs(policy, Samples.Dataset(Samples.Row("a", "true")));

        Action refuse = () => ReplaySet.Load(recording, inputs, force: false);
        ReplaySet forced = ReplaySet.Load(recording, inputs, force: true);

        refuse.Should().Throw<EvalsException>().Which.Message.Should()
            .Contain(Samples.DatasetPath).And.Contain("--force");
        forced.Rows.Should().ContainSingle().Which.Row.Id.Should().Be("a");
    }

    [Fact]
    public async Task Loader_Rejects_Selected_Rows_That_Share_An_Id_Naming_The_Id()
    {
        using TempFile file = TempFile.Write("");
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(policy),
            Samples.Recorded("a", (Samples.Injection, "local", Samples.BooleanAnswer(0.95))));

        // The shape --tune and --test produce: two files each numbering their rows from the same sequence,
        // concatenated into one selection, so the same id carries two different labels.
        LoadedInputs inputs = Samples.Inputs(
            policy,
            Samples.Dataset(Samples.Row("a", "true"), Samples.Row("a", "false", 2)));

        Action load = () => ReplaySet.Load(recording, inputs, force: false);

        load.Should().Throw<EvalsException>().Which.Message.Should().Contain("'a'");
    }

    public static TheoryData<string, Rule, Rule> DifferingRules
    {
        get
        {
            BooleanRule flagged = Samples.Flagged();
            ChoiceRule route = Samples.Route(Samples.Injection);
            ScoreRule severity = Samples.Severity(Samples.Injection);
            return new TheoryData<string, Rule, Rule>
            {
                { "question", flagged, Samples.Flagged(question: "question-x") },
                { "flaggedAnswer", flagged, Samples.Flagged(answer: false) },
                { "ladder", flagged, flagged with { Ladder = [Verdict.Deny] } },
                { "criteria", flagged, flagged with { Criteria = new BooleanCriteria("criterion-a", null) } },
                { "type", flagged, route },
                {
                    "options",
                    route,
                    route with { Options = [.. route.Options.Select(option => option with { Description = "other" })] }
                },
                { "levels", severity, severity with { Levels = ["harmless", "moderate", "grave"] } },
                { "rungs", severity, severity with { Rungs = [new ScoreRung("moderate", Verdict.Deny)] } },
            };
        }
    }

    [Theory]
    [MemberData(nameof(DifferingRules))]
    public async Task Loader_Rejects_A_Policy_Whose_Rule_Differs_Naming_The_Rule_And_The_Field_Not_The_Question(
        string field,
        Rule recorded,
        Rule candidate)
    {
        using TempFile file = TempFile.Write("");
        Policy recordedPolicy = Samples.Guard(FailureBehavior.Deny, ["local"], [recorded], gate: 0.2);
        Policy userPolicy = Samples.Guard(FailureBehavior.Deny, ["local"], [candidate], gate: 0.2);
        Recording recording = await Samples.RecordAsync(file.Path, Samples.Header(recordedPolicy));
        LoadedInputs inputs = Samples.Inputs(userPolicy, Samples.Dataset(Samples.Row("a", "ambiguous")));

        Action load = () => ReplaySet.Load(recording, inputs, force: false);

        string message = load.Should().Throw<EvalsException>().Which.Message;
        message.Should().Contain(Samples.Injection).And.Contain(field);
        message.Should().NotContain(recorded.Question).And.NotContain(candidate.Question);
        RuleShape.Matches(recorded, candidate, out string? differing).Should().BeFalse();
        differing.Should().Be(field);
        RuleShape.Matches(recorded, recorded, out string? same).Should().BeTrue();
        same.Should().BeNull();
    }

    [Fact]
    public async Task Loader_Rejects_A_Policy_Whose_Rule_The_Recording_Lacks_Naming_The_Rule()
    {
        using TempFile file = TempFile.Write("");
        Policy recordedPolicy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
        Policy userPolicy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged("other")]);
        Recording recording = await Samples.RecordAsync(file.Path, Samples.Header(recordedPolicy));
        LoadedInputs inputs = Samples.Inputs(userPolicy, Samples.Dataset(Samples.Row("a", "true")));

        Action load = () => ReplaySet.Load(recording, inputs, force: false);

        load.Should().Throw<EvalsException>().Which.Message.Should().Contain("other").And.Contain(file.Path);
    }

    public static TheoryData<string, Policy> AcceptedVariants
    {
        get
        {
            Policy baseline = Samples.Guard(FailureBehavior.Deny, ["local", "jev"], [Samples.Flagged()]);
            return new TheoryData<string, Policy>
            {
                { "thresholds", Retuned(baseline, warn: 0.3, deny: 0.7) },
                { "gate", Samples.Guard(FailureBehavior.Deny, ["local", "jev"], [Samples.Flagged()], gate: 0.25) },
                { "binding order", baseline with { Bindings = [.. baseline.Bindings.Reverse()] } },
                { "failure behaviour", baseline with { OnFailure = FailureBehavior.Fallback(Verdict.Deny) } },
                { "mode", baseline with { Mode = PolicyMode.Shadow } },
                { "budget", baseline with { Budget = TimeSpan.FromSeconds(2) } },
            };
        }
    }

    [Theory]
    [MemberData(nameof(AcceptedVariants))]
    public async Task Loader_Accepts_A_Policy_With_Different_Thresholds_Gates_Binding_Order_Failure_Mode_And_Budget(
        string differs,
        Policy userPolicy)
    {
        differs.Should().NotBeEmpty();
        using TempFile file = TempFile.Write("");
        Policy recordedPolicy = Samples.Guard(FailureBehavior.Deny, ["local", "jev"], [Samples.Flagged()]);
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(recordedPolicy),
            Samples.Recorded(
                "a",
                (Samples.Injection, "local", Samples.BooleanAnswer(0.95)),
                (Samples.Injection, "jev", Samples.BooleanAnswer(0.4, "jev"))));
        LoadedInputs inputs = Samples.Inputs(userPolicy, Samples.Dataset(Samples.Row("a", "true")));

        ReplaySet set = ReplaySet.Load(recording, inputs, force: false);

        set.Rows.Should().ContainSingle().Which.AttemptsByProvider.Keys.Should().BeEquivalentTo(["local", "jev"]);
    }

    private static Policy Retuned(Policy policy, double warn, double deny) => policy with
    {
        Bindings = [.. policy.Bindings.Select(binding => binding with
        {
            OperatingPoints = [.. binding.OperatingPoints.Select(point => point with
            {
                Thresholds = [.. point.Thresholds.Select(threshold =>
                    threshold with { AtOrAbove = threshold.Verdict == Verdict.Deny ? deny : warn })],
            })],
        })],
    };
}
