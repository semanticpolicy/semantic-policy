using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class MulticlassConfusionTests
{
    public static TheoryData<string, Rule, (string Label, string Predicted)[], string[], int[][], double, double?[],
        double?[], double?[], double> Cases =>
        new()
        {
            {
                // The router: "deny" is labelled three times and predicted never, so its F1 is 0 and still
                // enters the macro average. Skipping it would read 0.583 instead of 0.389.
                "choice",
                Samples.Route(),
                [
                    ("allow", "allow"), ("allow", "allow"), ("allow", "allow"), ("allow", "review"),
                    ("review", "review"), ("review", "review"), ("review", "allow"),
                    ("deny", "review"), ("deny", "review"), ("deny", "allow"),
                ],
                ["allow", "review", "deny"],
                [[3, 1, 0], [1, 2, 0], [1, 2, 0]],
                0.5,
                [3d / 5, 2d / 5, null],
                [3d / 4, 2d / 3, 0d],
                [2d / 3, 0.5, 0d],
                (2d / 3 + 0.5 + 0) / 3
            },
            {
                // The severity scale: every level is predicted at least once, and the errors are one step
                // off in each direction.
                "score",
                Samples.Severity(),
                [
                    ("harmless", "harmless"), ("harmless", "harmless"), ("harmless", "moderate"),
                    ("harmless", "harmless"), ("moderate", "moderate"), ("moderate", "moderate"),
                    ("moderate", "serious"), ("serious", "serious"), ("serious", "serious"),
                    ("serious", "moderate"),
                ],
                ["harmless", "moderate", "serious"],
                [[3, 1, 0], [0, 2, 1], [0, 1, 2]],
                0.7,
                [1d, 1d / 2, 2d / 3],
                [3d / 4, 2d / 3, 2d / 3],
                [6d / 7, 4d / 7, 2d / 3],
                (6d / 7 + 4d / 7 + 2d / 3) / 3
            },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Multiclass_Confusion_Reports_Per_Class_Precision_Recall_And_Macro_F1(
        string shape,
        Rule rule,
        (string Label, string Predicted)[] fixture,
        string[] classes,
        int[][] counts,
        double accuracy,
        double?[] precision,
        double?[] recall,
        double?[] f1,
        double macroF1)
    {
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [rule]);
        RowOutcome[] rows =
            [.. fixture.Select((pair, index) => Outcome(policy, rule, index, pair.Label, pair.Predicted))];

        MulticlassConfusion confusion = rule switch
        {
            ChoiceRule choice => MulticlassConfusion.Compute(rows, choice),
            ScoreRule score => MulticlassConfusion.Compute(rows, score),
            _ => throw new InvalidOperationException(shape),
        };

        confusion.Classes.Should().Equal(classes, shape);
        confusion.Accuracy.Should().BeApproximately(accuracy, 1e-12, shape);
        confusion.MacroF1.Should().BeApproximately(macroF1, 1e-12, shape);
        confusion.PerClass.Select(entry => entry.Class).Should().Equal(classes, shape);
        for (int actual = 0; actual < classes.Length; actual++)
        {
            for (int predicted = 0; predicted < classes.Length; predicted++)
            {
                confusion.Counts[classes[actual]][classes[predicted]].Should()
                    .Be(counts[actual][predicted], "{0}: {1} answered {2}", shape, classes[actual], classes[predicted]);
            }

            ClassMetrics metrics = confusion.PerClass[actual];
            metrics.Support.Should().Be(counts[actual].Sum(), shape);
            metrics.Precision.Should().BeApproximately(precision[actual], 1e-12, shape);
            metrics.Recall.Should().BeApproximately(recall[actual], 1e-12, shape);
            metrics.F1.Should().BeApproximately(f1[actual], 1e-12, shape);
        }
    }

    private static RowOutcome Outcome(Policy policy, Rule rule, int index, string label, string predicted)
    {
        DecisionValue value = rule is ScoreRule score
            ? new ScoreValue(predicted, score.Levels.ToList().IndexOf(predicted))
            : new ChoiceValue(predicted);
        Dictionary<AttemptKey, ProviderResult> attempts =
            new() { [new AttemptKey(rule.Id, 0)] = Samples.Answer(value) };
        RuleVerdict verdict = PolicyEvaluation.Evaluate(policy, attempts).Verdict!.Rules.Single();
        return RowCounting.Classify(new EvaluatedRow(Samples.Row($"r{index:00}", label), verdict), rule);
    }
}
