using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evals.Sweeping;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class GateSweepTests
{
    [Fact]
    public async Task Gate_Sweep_On_A_Binding_Without_A_File_Gate_Still_Enumerates_The_Observed_Margins()
    {
        using TempFile file = TempFile.Write("");
        BooleanRule rule = Samples.Flagged();
        Policy policy = new(
            "guard",
            PolicyMode.Enforce,
            [rule],
            [
                new ProviderBinding(
                    "local",
                    [
                        new RuleOperatingPoint(
                            rule.Id,
                            [
                                new Threshold(Verdict.Warn, EvidenceKind.Probability, 0.6),
                                new Threshold(Verdict.Deny, EvidenceKind.Probability, 0.9),
                            ]),
                    ]),
            ],
            FailureBehavior.Deny);

        // Dyadic probabilities, so every margin |2p - 1| is exact: 0.75, 0.5, 0.25, 0, 0.25, 0.5, 0.75, 0.875,
        // 0.875, 1. A margin of 0 is no gate at all, since only a gate above zero is a valid policy. The warn
        // cut at 0.6 gets r00, r05..r09 right and r01..r04 wrong.
        double[] flagged = [0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 0.9375, 0.0625, 1.0];
        string[] labels = ["false", "true", "true", "true", "false", "true", "true", "true", "false", "true"];
        DatasetRow[] rows = [.. labels.Select((label, index) => Samples.Row($"r{index:00}", label, index + 1))];
        RecordedRow[] recorded =
        [
            .. flagged.Select((value, index) => Samples.Recorded(rows[index].Id, (rule.Id, "local", Samples.BooleanAnswer(value)))),
        ];
        Recording recording = await Samples.RecordAsync(file.Path, Samples.Header(policy), recorded);
        ReplaySet set = ReplaySet.Load(recording, Samples.Inputs(policy, Samples.Dataset(rows)), force: false);

        GateCurve curve = GateSweep.Compute(set, policy, 0, rows);

        curve.Points.Select(point => point.Below).Should().Equal(null, 0.25, 0.5, 0.75, 0.875, 1.0);

        // Every count comes from replaying the variant, so a row whose margin is under the gate abstains.
        curve.Points.Select(point => point.Abstained).Should().Equal(0, 1, 3, 5, 7, 9);
        curve.Points.Select(point => point.AbstentionRate).Should().Equal(0, 0.1, 0.3, 0.5, 0.7, 0.9);
        curve.Points.Select(point => point.Decided).Should().Equal(10, 9, 7, 5, 3, 1);
        curve.Points.Select(point => point.Accuracy).Should().Equal(0.6, 6.0 / 9, 6.0 / 7, 1, 1, 1);
    }

    [Fact]
    public async Task Gate_Sweep_Offers_Each_Margin_As_The_Decimal_A_Policy_Would_Carry_And_Replays_That_Gate()
    {
        using TempFile file = TempFile.Write("");
        BooleanRule rule = Samples.Flagged();
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [rule]);

        // A margin is 2p - 1 in binary floating point: 0.3999999999999999 for 0.7, 0.5 for 0.75,
        // 0.6200000000000001 for 0.81 and 0.8999999999999999 for 0.95.
        double[] flagged = [0.7, 0.75, 0.81, 0.95];
        DatasetRow[] rows = [.. flagged.Select((_, index) => Samples.Row($"r{index}", "true", index + 1))];
        RecordedRow[] recorded =
        [
            .. flagged.Select((value, index) => Samples.Recorded(rows[index].Id, (rule.Id, "local", Samples.BooleanAnswer(value)))),
        ];
        Recording recording = await Samples.RecordAsync(file.Path, Samples.Header(policy), recorded);
        ReplaySet set = ReplaySet.Load(recording, Samples.Inputs(policy, Samples.Dataset(rows)), force: false);

        GateCurve curve = GateSweep.Compute(set, policy, 0, rows);

        curve.Points.Select(point => point.Below).Should().Equal(null, 0.4, 0.5, 0.62, 0.9);

        // The replay runs at the decimal, so the row whose margin fell just under it abstains there, as it would
        // under a policy carrying that gate.
        curve.Points.Select(point => point.Abstained).Should().Equal(0, 1, 1, 2, 4);
    }

    [Fact]
    public async Task Gate_Sweep_On_Hundreds_Of_Distinct_Margins_Considers_At_Most_101()
    {
        using TempFile file = TempFile.Write("");
        BooleanRule rule = Samples.Flagged();
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [rule]);

        // 0.5 + k/2048 is exact in binary, so every margin is exactly k/1024 and prints as that decimal: three
        // hundred distinct margins, each one a gate a policy could carry as written.
        double[] margins = [.. Enumerable.Range(1, 300).Select(k => k / 1024.0)];
        DatasetRow[] rows = [.. margins.Select((_, index) => Samples.Row($"r{index:000}", "true", index + 1))];
        RecordedRow[] recorded =
        [
            .. margins.Select((_, index) =>
                Samples.Recorded(rows[index].Id, (rule.Id, "local", Samples.BooleanAnswer(0.5 + (index + 1) / 2048.0)))),
        ];
        Recording recording = await Samples.RecordAsync(file.Path, Samples.Header(policy), recorded);
        ReplaySet set = ReplaySet.Load(recording, Samples.Inputs(policy, Samples.Dataset(rows)), force: false);

        GateCurve curve = GateSweep.Compute(set, policy, 0, rows);

        curve.Points[0].Below.Should().BeNull();
        List<double> gates = [.. curve.Points.Skip(1).Select(point => point.Below!.Value)];
        gates.Should().HaveCountLessThanOrEqualTo(101).And.BeInAscendingOrder().And.OnlyHaveUniqueItems();
        gates.Should().BeSubsetOf(margins);
        gates[^1].Should().Be(margins[^1]);
    }
}
