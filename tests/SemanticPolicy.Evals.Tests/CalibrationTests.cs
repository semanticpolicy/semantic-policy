using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class CalibrationTests
{
    [Fact]
    public void Calibration_Computes_Ece_Brier_And_Reliability_Bins_On_Probability_Evidence()
    {
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);

        // One row per bin, at the middle of each: the flagged answer's probability against whether the row
        // is labelled the flagged answer.
        RowOutcome[] rows =
        [
            Outcome(policy, "r01", "false", Samples.BooleanAnswer(0.05)),
            Outcome(policy, "r02", "false", Samples.BooleanAnswer(0.15)),
            Outcome(policy, "r03", "false", Samples.BooleanAnswer(0.25)),
            Outcome(policy, "r04", "true", Samples.BooleanAnswer(0.35)),
            Outcome(policy, "r05", "false", Samples.BooleanAnswer(0.45)),
            Outcome(policy, "r06", "true", Samples.BooleanAnswer(0.55)),
            Outcome(policy, "r07", "false", Samples.BooleanAnswer(0.65)),
            Outcome(policy, "r08", "true", Samples.BooleanAnswer(0.75)),
            Outcome(policy, "r09", "true", Samples.BooleanAnswer(0.85)),
            Outcome(policy, "r10", "true", Samples.BooleanAnswer(0.95)),
        ];

        Calibration calibration = Calibration.Compute(rows, policy.Rules[0]);

        calibration.Applicable.Should().BeTrue();
        calibration.KindsFound.Should().BeEmpty();
        calibration.Rows.Should().Be(10);

        // Each bin holds one row, so its gap is |p - outcome|: 0.05, 0.15, 0.25, 0.65, 0.45, 0.45, 0.65,
        // 0.25, 0.15, 0.05, which sum to 3.10 over ten rows.
        calibration.Ece.Should().BeApproximately(0.31, 1e-12);

        // The squared errors sum to 1.425 over the same ten rows.
        calibration.Brier.Should().BeApproximately(0.1425, 1e-12);

        calibration.Bins.Should().HaveCount(10);
        calibration.Bins.Should().AllSatisfy(bin => bin.Count.Should().Be(1));
        calibration.Bins[0].Lower.Should().Be(0);
        calibration.Bins[0].Upper.Should().BeApproximately(0.1, 1e-12);
        calibration.Bins[0].MeanPrediction.Should().BeApproximately(0.05, 1e-12);
        calibration.Bins[0].ObservedFrequency.Should().Be(0);

        // The last bin is closed at 1.0 so a probability of exactly one has somewhere to go.
        calibration.Bins[9].Lower.Should().BeApproximately(0.9, 1e-12);
        calibration.Bins[9].Upper.Should().Be(1);
        calibration.Bins[9].MeanPrediction.Should().BeApproximately(0.95, 1e-12);
        calibration.Bins[9].ObservedFrequency.Should().Be(1);
        calibration.Bins[3].MeanPrediction.Should().BeApproximately(0.35, 1e-12);
        calibration.Bins[3].ObservedFrequency.Should().Be(1);
    }

    [Fact]
    public void Calibration_Uses_The_Top_1_Probability_For_A_Choice_Rule()
    {
        ChoiceRule rule = Samples.Route();
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [rule]);

        // The option picked is always the provider's own top answer; the number calibrated is that top
        // probability, and the outcome is whether the picked option is the labelled one.
        RowOutcome[] rows =
        [
            Outcome(policy, "r01", "allow", Picked("allow", 0.95, 0.03, 0.02)),
            Outcome(policy, "r02", "allow", Picked("allow", 0.85, 0.10, 0.05)),
            Outcome(policy, "r03", "review", Picked("allow", 0.75, 0.15, 0.10)),
            Outcome(policy, "r04", "allow", Picked("allow", 0.65, 0.20, 0.15)),
            Outcome(policy, "r05", "review", Picked("review", 0.25, 0.55, 0.20)),
            Outcome(policy, "r06", "deny", Picked("review", 0.30, 0.45, 0.25)),
            Outcome(policy, "r07", "review", Picked("review", 0.33, 0.35, 0.32)),
            Outcome(policy, "r08", "deny", Picked("deny", 0.02, 0.03, 0.95)),
            Outcome(policy, "r09", "allow", Picked("deny", 0.05, 0.10, 0.85)),
            Outcome(policy, "r10", "deny", Picked("deny", 0.20, 0.25, 0.55)),
        ];

        Calibration calibration = Calibration.Compute(rows, rule);

        calibration.Applicable.Should().BeTrue();
        calibration.Rows.Should().Be(10);

        // Gaps weighted by bin count: 0.65, 0.45, 2x0.45, 0.35, 0.75, 2x0.35 and 2x0.05 sum to 3.90.
        calibration.Ece.Should().BeApproximately(0.39, 1e-12);

        // The squared errors sum to 2.465 over ten rows.
        calibration.Brier.Should().BeApproximately(0.2465, 1e-12);

        // Nothing landed below 0.3, because a top answer of three cannot be lower than a third.
        calibration.Bins.Take(3).Should().AllSatisfy(bin =>
        {
            bin.Count.Should().Be(0);
            bin.MeanPrediction.Should().BeNull();
            bin.ObservedFrequency.Should().BeNull();
        });
        calibration.Bins[8].Count.Should().Be(2);
        calibration.Bins[8].MeanPrediction.Should().BeApproximately(0.85, 1e-12);
        calibration.Bins[8].ObservedFrequency.Should().BeApproximately(0.5, 1e-12);
    }

    private static ProviderResult Picked(string option, double allow, double review, double deny) =>
        Samples.Answer(
            new ChoiceValue(option),
            "local",
            Samples.Probability(("allow", allow), ("review", review), ("deny", deny)));

    private static RowOutcome Outcome(Policy policy, string id, string label, ProviderResult result)
    {
        Rule rule = policy.Rules[0];
        Dictionary<AttemptKey, ProviderResult> attempts = new() { [new AttemptKey(rule.Id, 0)] = result };
        RuleVerdict verdict = PolicyEvaluation.Evaluate(policy, attempts).Verdict!.Rules.Single();
        return RowCounting.Classify(new EvaluatedRow(Samples.Row(id, label), verdict), rule);
    }

    [Theory]
    [InlineData("score")]
    [InlineData("logit")]
    [InlineData("scoreRule")]
    public void Calibration_Is_Not_Applicable_On_Non_Probability_Evidence_And_Names_The_Kind_Found(string shape)
    {
        (Policy policy, RowOutcome[] rows, string kind) = shape switch
        {
            "score" => Ladder(EvidenceKind.Score, 0.6, 0.9),
            "logit" => Ladder(EvidenceKind.Logit, 0.0, 1.0),
            _ => Levels(),
        };

        Calibration calibration = Calibration.Compute(rows, policy.Rules[0]);

        calibration.Applicable.Should().BeFalse(shape);
        calibration.Ece.Should().BeNull(shape);
        calibration.Brier.Should().BeNull(shape);
        calibration.Bins.Should().BeEmpty(shape);
        calibration.Rows.Should().Be(0, shape);
        calibration.KindsFound.Should().Equal([kind], shape);
    }

    // A Boolean ladder read on a kind that is not a probability. Every row carries both answers: Core
    // completes only a one-sided probability and calls a one-sided score or logit malformed, which would
    // bucket the row as a failure rather than a classified one.
    private static (Policy Policy, RowOutcome[] Rows, string Kind) Ladder(EvidenceKind kind, double warn, double deny)
    {
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
                            [new Threshold(Verdict.Warn, kind, warn), new Threshold(Verdict.Deny, kind, deny)]),
                    ]),
            ],
            FailureBehavior.Deny);
        double[] flagged = [0.95, 0.88, 0.80, 0.72, 0.64, 0.41, 0.33, 0.25, 0.17, 0.05];
        string[] labels = ["true", "true", "false", "true", "false", "true", "false", "false", "true", "false"];
        RowOutcome[] rows =
        [
            .. flagged.Select((value, index) => Outcome(
                policy,
                $"r{index:00}",
                labels[index],
                Samples.Answer(
                    new BooleanValue(value >= 0.5),
                    "local",
                    new Evidence(
                        kind,
                        new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            ["true"] = value,
                            ["false"] = 1 - value,
                        })))),
        ];
        return (policy, rows, kind == EvidenceKind.Score ? "score" : "logit");
    }

    // A Score rule, which this release does not calibrate whatever its provider returned.
    private static (Policy Policy, RowOutcome[] Rows, string Kind) Levels()
    {
        ScoreRule rule = Samples.Severity();
        Policy policy = Samples.Guard(FailureBehavior.Deny, ["local"], [rule]);
        string[] picked =
        [
            "serious", "moderate", "harmless", "serious", "moderate",
            "harmless", "serious", "moderate", "harmless", "serious",
        ];
        string[] labels =
        [
            "serious", "moderate", "harmless", "moderate", "moderate",
            "harmless", "serious", "serious", "harmless", "serious",
        ];
        RowOutcome[] rows =
        [
            .. picked.Select((level, index) => Outcome(
                policy,
                $"r{index:00}",
                labels[index],
                Samples.Answer(
                    new ScoreValue(level, rule.Levels.ToList().IndexOf(level)),
                    "local",
                    new Evidence(
                        EvidenceKind.Score,
                        new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            ["harmless"] = 0.1,
                            ["moderate"] = 0.2,
                            ["serious"] = 0.7,
                        })))),
        ];
        return (policy, rows, "score");
    }
}
