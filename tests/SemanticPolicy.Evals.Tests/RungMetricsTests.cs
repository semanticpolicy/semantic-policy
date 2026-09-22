using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class RungMetricsTests
{
    private static readonly Policy _policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
    private static readonly BooleanRule _rule = (BooleanRule)_policy.Rules[0];

    [Fact]
    public void Rung_Matrices_Count_Each_Row_Against_Verdict_At_Or_Above_The_Rung()
    {
        // Warn is reached at 0.6 and Deny at 0.9 on the flagged answer's probability, so these ten rows are
        // three Deny, three Warn and four Allow; the label beside each is the truth they are scored against.
        RowOutcome[] rows =
        [
            Outcome("r01", "true", 0.95),
            Outcome("r02", "true", 0.92),
            Outcome("r03", "false", 0.91),
            Outcome("r04", "true", 0.75),
            Outcome("r05", "false", 0.65),
            Outcome("r06", "false", 0.60),
            Outcome("r07", "true", 0.40),
            Outcome("r08", "false", 0.30),
            Outcome("r09", "false", 0.10),
            Outcome("r10", "false", 0.05),
        ];

        IReadOnlyList<RungMetrics> metrics = RungMetrics.Compute(rows, _rule);

        metrics.Select(entry => entry.Rung).Should().Equal(Verdict.Warn, Verdict.Deny);

        // Warn and above predicts r01-r06; r01, r02 and r04 of those are labelled true, and r07 is the
        // labelled-true row it misses.
        BinaryConfusion warn = metrics[0].Matrix;
        warn.Should().Be(new BinaryConfusion(TruePositives: 3, FalsePositives: 3, TrueNegatives: 3, FalseNegatives: 1));
        warn.Accuracy.Should().BeApproximately(0.6, 1e-12);
        warn.Precision.Should().BeApproximately(0.5, 1e-12);
        warn.Recall.Should().BeApproximately(0.75, 1e-12);
        warn.F1.Should().BeApproximately(0.6, 1e-12);
        warn.FalsePositiveRate.Should().BeApproximately(0.5, 1e-12);
        warn.FalseNegativeRate.Should().BeApproximately(0.25, 1e-12);

        // Deny and above predicts r01-r03; r01 and r02 of those are labelled true, and r04 and r07 are the
        // labelled-true rows it misses.
        BinaryConfusion deny = metrics[1].Matrix;
        deny.Should().Be(new BinaryConfusion(TruePositives: 2, FalsePositives: 1, TrueNegatives: 5, FalseNegatives: 2));
        deny.Accuracy.Should().BeApproximately(0.7, 1e-12);
        deny.Precision.Should().BeApproximately(2d / 3, 1e-12);
        deny.Recall.Should().BeApproximately(0.5, 1e-12);
        deny.F1.Should().BeApproximately(4d / 7, 1e-12);
        deny.FalsePositiveRate.Should().BeApproximately(1d / 6, 1e-12);
        deny.FalseNegativeRate.Should().BeApproximately(0.5, 1e-12);
    }

    public static TheoryData<string, BinaryConfusion, double?, double?, double?, double?, double?, double?> Denominators =>
        new()
        {
            // No row is labelled positive, so recall and the false-negative rate have nothing to divide by.
            { "no positives", new BinaryConfusion(0, 2, 3, 0), 0.6, 0d, null, 0d, 0.4, null },
            // Nothing is predicted positive, so precision has nothing to divide by.
            { "no predictions", new BinaryConfusion(0, 0, 4, 1), 0.8, null, 0d, 0d, 0d, 1d },
            // No row is labelled negative, so the false-positive rate has nothing to divide by.
            { "no negatives", new BinaryConfusion(3, 0, 0, 2), 0.6, 1d, 0.6, 0.75, null, 0.4 },
            // Nothing was classified at all: accuracy and F1 go undefined with the rest.
            { "no rows", new BinaryConfusion(0, 0, 0, 0), null, null, null, null, null, null },
        };

    [Theory]
    [MemberData(nameof(Denominators))]
    public void Rates_Are_Null_When_Their_Denominator_Is_Zero(
        string label,
        BinaryConfusion matrix,
        double? accuracy,
        double? precision,
        double? recall,
        double? f1,
        double? falsePositiveRate,
        double? falseNegativeRate)
    {
        matrix.Accuracy.Should().Be(accuracy, label);
        matrix.Precision.Should().Be(precision, label);
        matrix.Recall.Should().Be(recall, label);
        matrix.F1.Should().Be(f1, label);
        matrix.FalsePositiveRate.Should().Be(falsePositiveRate, label);
        matrix.FalseNegativeRate.Should().Be(falseNegativeRate, label);
    }

    // A real verdict rather than a hand-built one: the rung a metric counts against is the one the step
    // function reached on the policy's own thresholds.
    private static RowOutcome Outcome(string id, string label, double probabilityOfTrue)
    {
        Dictionary<AttemptKey, ProviderResult> attempts =
            new() { [new AttemptKey(Samples.Injection, 0)] = Samples.BooleanAnswer(probabilityOfTrue) };
        RuleVerdict verdict = PolicyEvaluation.Evaluate(_policy, attempts).Verdict!.Rules.Single();
        return RowCounting.Classify(new EvaluatedRow(Samples.Row(id, label), verdict), _rule);
    }
}
