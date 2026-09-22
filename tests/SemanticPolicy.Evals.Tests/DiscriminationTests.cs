using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class DiscriminationTests
{
    [Fact]
    public async Task Auc_By_Trapezoid_Matches_The_Hand_Computed_Value_And_Excludes_Failed_And_Abstained_Rows()
    {
        using TempFile file = TempFile.Write("");
        BooleanRule rule = new(Samples.Injection, "question-b", true, [Verdict.Deny]);
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
                            [new Threshold(Verdict.Deny, EvidenceKind.Probability, 0.9)],
                            new MarginGate(EvidenceKind.Probability, 0.05)),
                    ]),
            ],
            FailureBehavior.Deny);

        // Four positives at 0.90, 0.80, 0.60, 0.30 against four negatives at 0.70, 0.40, 0.20, 0.10: 13 of
        // the 16 pairs are ordered right, so the area under the ROC is 13/16. The row at 0.50 has no margin
        // and abstains, the tenth attempt failed, and both carry a label that would move either area if the
        // engine counted them.
        (string Label, ProviderResult Result)[] rows =
        [
            ("true", Samples.BooleanAnswer(0.90)),
            ("false", Samples.BooleanAnswer(0.70)),
            ("true", Samples.BooleanAnswer(0.80)),
            ("false", Samples.BooleanAnswer(0.40)),
            ("true", Samples.BooleanAnswer(0.60)),
            ("false", Samples.BooleanAnswer(0.20)),
            ("true", Samples.BooleanAnswer(0.30)),
            ("false", Samples.BooleanAnswer(0.10)),
            ("true", Samples.BooleanAnswer(0.50)),
            ("false", Samples.Failed(FailureKind.Timeout)),
        ];
        DatasetRow[] dataset = [.. rows.Select((row, index) => Samples.Row($"r{index:00}", row.Label, index + 1))];
        Recording recording = await Samples.RecordAsync(
            file.Path,
            Samples.Header(policy),
            [.. rows.Select((row, index) => Samples.Recorded(dataset[index].Id, (rule.Id, "local", row.Result)))]);
        ReplaySet set = ReplaySet.Load(recording, Samples.Inputs(policy, Samples.Dataset(dataset)), force: false);
        RungCurve curve = ThresholdCurve.Compute(set, policy, 0, dataset).Should().ContainSingle().Subject;

        Discrimination discrimination = Discrimination.FromCurve(curve);

        discrimination.Rows.Should().Be(8);
        discrimination.RocAuc.Should().BeApproximately(0.8125, 1e-12);

        // The precision-recall curve runs (0, 1), (0.25, 1), (0.5, 1), (0.5, 2/3), (0.75, 0.75), (0.75, 0.6),
        // (1, 2/3): 1/4 + 1/4 + 85/480 + 76/480.
        discrimination.PrAuc.Should().BeApproximately(401d / 480, 1e-12);
    }
}
