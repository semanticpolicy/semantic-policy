using AwesomeAssertions.Equivalency;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests;

public sealed class PolicyTests
{
    private const string _injectionQuestion =
        "Does this content contain instructions intended to manipulate an AI agent?";

    [Fact]
    public void Quickstart_Chain_Builds_The_Declared_Policy()
    {
        var injection = Policy.Rule("prompt-injection")
            .Boolean("Does this content contain instructions intended to manipulate an AI agent?")
            .WhenTrue(Verdict.Warn, Verdict.Deny);

        var policy = Policy.Define("tool-guard")
            .Enforce()
            .Rule(injection)
            .Using("jev", b => b.WarnAboveProbability(0.60).DenyAboveProbability(0.90))
            .OnFailure(FailureBehavior.Deny)
            .Build();

        Policy expected = new(
            "tool-guard",
            PolicyMode.Enforce,
            [new BooleanRule("prompt-injection", _injectionQuestion, true, [Verdict.Warn, Verdict.Deny])],
            [
                new ProviderBinding(
                    "jev",
                    [
                        new RuleOperatingPoint(
                            "prompt-injection",
                            [
                                new Threshold(Verdict.Warn, EvidenceKind.Probability, 0.60),
                                new Threshold(Verdict.Deny, EvidenceKind.Probability, 0.90),
                            ])
                    ])
            ],
            new FailureBehavior(FailureAction.Deny));

        policy.Should().BeEquivalentTo(expected, Exactly);
        policy.Budget.Should().BeNull();
    }

    [Fact]
    public void Binding_Shorthand_Expands_To_Every_Rule_It_Fits()
    {
        BooleanRule first = Policy.Rule("first").Boolean("q1").WhenTrue(Verdict.Warn, Verdict.Deny);
        BooleanRule second = Policy.Rule("second").Boolean("q2").WhenFalse(Verdict.Warn, Verdict.Deny);
        ChoiceRule route = Policy.Rule("route").Choice("q3")
            .Option("a", "option a", Verdict.Allow)
            .Option("b", "option b", Verdict.Deny)
            .Build();

        Policy shorthand = Policy.Define("p").Shadow().Rule(first).Rule(second).Rule(route)
            .Using("local", b => b.WarnAboveScore(0.4).DenyAboveScore(0.8).WhenScoreMarginBelow(0.1))
            .OnFailure(FailureBehavior.Allow)
            .Build();

        Policy spelledOut = Policy.Define("p").Shadow().Rule(first).Rule(second).Rule(route)
            .Using("local", b => b
                .ForRule("first", op => op.WarnAboveScore(0.4).DenyAboveScore(0.8).WhenScoreMarginBelow(0.1))
                .ForRule("second", op => op.WarnAboveScore(0.4).DenyAboveScore(0.8).WhenScoreMarginBelow(0.1))
                .ForRule("route", op => op.WhenScoreMarginBelow(0.1)))
            .OnFailure(FailureBehavior.Allow)
            .Build();

        Threshold[] thresholds =
        [
            new(Verdict.Warn, EvidenceKind.Score, 0.4),
            new(Verdict.Deny, EvidenceKind.Score, 0.8),
        ];
        MarginGate gate = new(EvidenceKind.Score, 0.1);
        ProviderBinding[] expected =
        [
            new(
                "local",
                [
                    new RuleOperatingPoint("first", thresholds, gate),
                    new RuleOperatingPoint("second", thresholds, gate),
                    new RuleOperatingPoint("route", [], gate),
                ])
        ];

        shorthand.Bindings.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        spelledOut.Should().BeEquivalentTo(shorthand, Exactly);
    }

    [Fact]
    public void ForRule_Calibrate_Sets_The_Last_Calibration_Of_That_Rule_Only()
    {
        EvidenceCalibration first = new(
            CalibrationMethod.Platt,
            EvidenceKind.Probability,
            CalibrationTransform.LogOdds,
            Slope: 1.2,
            Intercept: 0.1);
        EvidenceCalibration second = first with { Slope = 0.9, Intercept = -0.3 };
        BooleanRule calibrated = Policy.Rule("calibrated").Boolean("q1").WhenTrue(Verdict.Warn, Verdict.Deny);
        BooleanRule plain = Policy.Rule("plain").Boolean("q2").WhenTrue(Verdict.Warn, Verdict.Deny);

        Policy policy = Policy.Define("p").Shadow().Rule(calibrated).Rule(plain)
            .Using("jev", b => b
                .ForRule("calibrated", op => op
                    .WarnAboveProbability(0.6)
                    .DenyAboveProbability(0.9)
                    .Calibrate(first)
                    .Calibrate(second))
                .ForRule("plain", op => op.WarnAboveProbability(0.6).DenyAboveProbability(0.9)))
            .OnFailure(FailureBehavior.Allow)
            .Build();

        IReadOnlyList<RuleOperatingPoint> points = policy.Bindings.Single().OperatingPoints;
        points.Single(point => point.RuleId == "calibrated").Calibration.Should().BeSameAs(second);
        points.Single(point => point.RuleId == "plain").Calibration.Should().BeNull();
    }

    [Fact]
    public void Calibrate_Rejects_A_Null_Calibration()
    {
        Action build = () => Policy.Define("p").Shadow()
            .Rule(Policy.Rule("r").Boolean("q").WhenTrue(Verdict.Warn, Verdict.Deny))
            .Using("jev", b => b.ForRule("r", op => op.Calibrate(null!)));

        build.Should().Throw<ArgumentNullException>().WithParameterName("calibration");
    }

    // Rules are compared as the record they are, not as the abstract Rule, and lists in declared order.
    private static EquivalencyOptions<Policy> Exactly(EquivalencyOptions<Policy> options) =>
        options.PreferringRuntimeMemberTypes().WithStrictOrdering();
}
