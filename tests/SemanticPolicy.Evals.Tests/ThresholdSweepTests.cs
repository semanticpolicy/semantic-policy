using System.Collections.ObjectModel;
using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Sweeping;

namespace SemanticPolicy.Evals.Tests;

public sealed class ThresholdSweepTests
{
    // Ten rows, five labelled flagged, cut at six observed thresholds:
    //
    //   threshold  TP FP TN FN  recall  fpr  precision
    //   0.1         5  5  0  0  1.0     1.0  0.5
    //   0.2         5  3  2  0  1.0     0.6  0.625
    //   0.3         4  2  3  1  0.8     0.4  0.667
    //   0.4         4  1  4  1  0.8     0.2  0.8
    //   0.5         3  0  5  2  0.6     0.0  1.0
    //   0.6         1  0  5  4  0.2     0.0  1.0
    //
    // plus a grid point at 0.45 that was never observed. It would be the answer to min-recall=0.8 and to
    // min-precision=0.9 if the grid were a candidate, and a recommendation is only ever a measured point.
    private static readonly RungCurve _curve = new(
        Verdict.Warn,
        [
            Point(0.1, 5, 5, 0, 0),
            Point(0.2, 5, 3, 2, 0),
            Point(0.3, 4, 2, 3, 1),
            Point(0.4, 4, 1, 4, 1),
            Point(0.45, 4, 0, 5, 1, observed: false),
            Point(0.5, 3, 0, 5, 2),
            Point(0.6, 1, 0, 5, 4),
        ]);

    [Theory]
    [InlineData("min-recall=0.8", 0.4)]
    [InlineData("max-fpr=0.4", 0.3)]
    [InlineData("min-precision=0.9", 0.5)]
    public void A_Constraint_Picks_Its_Own_End_Of_The_Feasible_Set(string token, double expected)
    {
        RungConstraint constraint = RungConstraint.Parse(Verdict.Warn, token);

        RungRecommendation recommendation = ThresholdSweep.Recommend(_curve, [constraint]);

        recommendation.Rung.Should().Be(Verdict.Warn);
        recommendation.Swept.Should().BeTrue();
        recommendation.Feasible.Should().BeTrue();
        recommendation.Threshold.Should().Be(expected);
        recommendation.Chosen!.Threshold.Should().Be(expected);
        recommendation.Chosen.Observed.Should().BeTrue();
        recommendation.Nearest.Should().BeNull();
    }

    [Theory]
    [InlineData(new[] { "min-recall=0.6", "max-fpr=0.4" }, 0.5)]
    [InlineData(new[] { "max-fpr=0.4", "min-recall=0.6" }, 0.3)]
    public void Several_Constraints_On_One_Rung_Intersect_And_The_First_Named_Decides_The_End(
        string[] tokens,
        double expected)
    {
        // Recall of at least 0.6 holds from 0.1 to 0.5 and an FPR of at most 0.4 from 0.3 to 0.6, so both hold
        // on 0.3, 0.4 and 0.5. Min-recall takes the top of that and max-fpr the bottom.
        RungConstraint[] constraints = [.. tokens.Select(token => RungConstraint.Parse(Verdict.Warn, token))];

        RungRecommendation recommendation = ThresholdSweep.Recommend(_curve, constraints);

        recommendation.Feasible.Should().BeTrue();
        recommendation.Threshold.Should().Be(expected);
        recommendation.Constraints.Should().Equal(constraints);
    }

    private static CurvePoint Point(
        double threshold,
        int truePositives,
        int falsePositives,
        int trueNegatives,
        int falseNegatives,
        bool observed = true) =>
        new(
            threshold,
            observed,
            new BinaryConfusion(truePositives, falsePositives, trueNegatives, falseNegatives),
            new OutcomeCounts(
                10,
                10,
                0,
                ReadOnlyDictionary<string, int>.Empty,
                0,
                0,
                ReadOnlyDictionary<string, int>.Empty,
                0,
                0));
}
