using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests;

public sealed class PolicyValidationTests
{
    private const string _question = "question-marker";

    private static readonly Action<BindingBuilder> _warnDeny =
        b => b.WarnAboveProbability(0.6).DenyAboveProbability(0.9);

    private static readonly Action<OperatingPointBuilder> _warnDenyPoint =
        op => op.WarnAboveProbability(0.6).DenyAboveProbability(0.9);

    private static readonly Action<BindingBuilder> _noPoints = b => { };

    private static readonly ChoiceRule _choice =
        Choice(("a", "description-marker", Verdict.Allow), ("b", "description-marker", Verdict.Deny));

    public static TheoryData<string, Func<Policy>, string?, string?> Rejections => new()
    {
        // Policy level.
        {
            "empty policy id",
            () => Policy.Define("").Enforce().Rule(Flag()).Using("p", _warnDeny)
                .OnFailure(FailureBehavior.Deny).Build(),
            null, null
        },
        { "no rules", () => Base().Using("p", _noPoints).Build(), null, null },
        { "duplicate rule ids", () => Base(Flag("r"), Flag("r")).Using("p", _warnDeny).Build(), "r", null },
        { "no bindings", () => Base(Flag()).Build(), null, null },
        { "empty provider id", () => Base(Flag()).Using("", _warnDeny).Build(), null, null },
        { "duplicate provider id", () => Base(Flag()).Using("p", _warnDeny).Using("p", _warnDeny).Build(), null, "p" },
        { "fallback without then", () => Failing(new FailureBehavior(FailureAction.Fallback)), null, null },
        { "then of warn", () => Failing(FailureBehavior.Fallback(Verdict.Warn)), null, null },
        { "then of abstain", () => Failing(FailureBehavior.Fallback(Verdict.Abstain)), null, null },
        { "then on a non-fallback", () => Failing(new FailureBehavior(FailureAction.Deny, Verdict.Allow)), null, null },
        { "zero budget", () => Base(Flag()).Using("p", _warnDeny).Budget(TimeSpan.Zero).Build(), null, null },
        {
            "negative budget",
            () => Base(Flag()).Using("p", _warnDeny).Budget(TimeSpan.FromSeconds(-1)).Build(),
            null, null
        },

        // Rule level.
        { "empty rule id", () => Base(Flag("")).Using("p", _warnDeny).Build(), null, null },
        {
            "empty question",
            () => Base(Policy.Rule("r").Boolean(" ").WhenTrue(Verdict.Warn, Verdict.Deny))
                .Using("p", _warnDeny).Build(),
            "r", null
        },
        { "empty ladder", () => Base(Ladder()).Using("p", _warnDeny).Build(), "r", null },
        {
            "ladder with allow",
            () => Base(Ladder(Verdict.Allow, Verdict.Deny)).Using("p", _warnDeny).Build(),
            "r", null
        },
        {
            "ladder with abstain",
            () => Base(Ladder(Verdict.Warn, Verdict.Abstain)).Using("p", _warnDeny).Build(),
            "r", null
        },
        {
            "ladder repeats a verdict",
            () => Base(Ladder(Verdict.Warn, Verdict.Warn)).Using("p", _warnDeny).Build(),
            "r", null
        },
        {
            "ladder not increasing",
            () => Base(Ladder(Verdict.Deny, Verdict.Warn)).Using("p", _warnDeny).Build(),
            "r", null
        },
        {
            "choice with one option",
            () => Base(Choice(("a", "description-marker", Verdict.Allow))).Using("p", _noPoints).Build(),
            "c", null
        },
        {
            "choice duplicate key",
            () => Base(Choice(("a", "description-marker", Verdict.Allow), ("a", "description-marker", Verdict.Deny)))
                .Using("p", _noPoints).Build(),
            "c", null
        },
        {
            "choice empty key",
            () => Base(Choice(("", "description-marker", Verdict.Allow), ("b", "description-marker", Verdict.Deny)))
                .Using("p", _noPoints).Build(),
            "c", null
        },
        {
            "choice empty description",
            () => Base(Choice(("a", "", Verdict.Allow), ("b", "description-marker", Verdict.Deny)))
                .Using("p", _noPoints).Build(),
            "c", null
        },
        {
            "choice abstain verdict",
            () => Base(Choice(("a", "description-marker", Verdict.Abstain), ("b", "description-marker", Verdict.Deny)))
                .Using("p", _noPoints).Build(),
            "c", null
        },
        {
            "score with one level",
            () => Base(Score("only").DenyAtOrAbove("only").Build()).Using("p", _noPoints).Build(),
            "s", null
        },
        {
            "score with eleven levels",
            () => Base(Score("a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k").DenyAtOrAbove("k").Build())
                .Using("p", _noPoints).Build(),
            "s", null
        },
        {
            "score duplicate level",
            () => Base(Score("a", "a").DenyAtOrAbove("a").Build()).Using("p", _noPoints).Build(),
            "s", null
        },
        {
            "score empty level",
            () => Base(Score("a", "").DenyAtOrAbove("a").Build()).Using("p", _noPoints).Build(),
            "s", null
        },
        { "score with no rungs", () => Base(Score("a", "b").Build()).Using("p", _noPoints).Build(), "s", null },
        {
            "score rung on an unknown level",
            () => Base(Score("a", "b").DenyAtOrAbove("level-marker").Build()).Using("p", _noPoints).Build(),
            "s", null
        },
        {
            "score rung repeats a level",
            () => Base(Score("a", "b").WarnAtOrAbove("b").DenyAtOrAbove("b").Build()).Using("p", _noPoints).Build(),
            "s", null
        },
        {
            "score rungs not increasing with the level",
            () => Base(Score("a", "b").DenyAtOrAbove("a").WarnAtOrAbove("b").Build()).Using("p", _noPoints).Build(),
            "s", null
        },
        {
            "score rung of allow",
            () => Base(new ScoreRule("s", _question, ["a", "b"], [new ScoreRung("b", Verdict.Allow)]))
                .Using("p", _noPoints).Build(),
            "s", null
        },
        {
            "score rung of abstain",
            () => Base(new ScoreRule("s", _question, ["a", "b"], [new ScoreRung("b", Verdict.Abstain)]))
                .Using("p", _noPoints).Build(),
            "s", null
        },

        // Binding level.
        {
            "operating point for an unknown rule",
            () => Base(Flag("r"))
                .Using("p", b => b
                    .ForRule("r", _warnDenyPoint)
                    .ForRule("ghost", op => op.WhenProbabilityMarginBelow(0.1)))
                .Build(),
            "ghost", "p"
        },
        {
            "two operating points for one rule",
            () => Base(Flag("r")).Using("p", b => b.ForRule("r", _warnDenyPoint).ForRule("r", _warnDenyPoint)).Build(),
            "r", "p"
        },
        { "boolean rule with no operating point", () => Base(Flag("r")).Using("p", _noPoints).Build(), "r", "p" },
        {
            "boolean operating point with no thresholds",
            () => Base(Flag("r")).Using("p", b => b.ForRule("r", op => op.WhenProbabilityMarginBelow(0.1))).Build(),
            "r", "p"
        },
        {
            "a ladder rung with no threshold",
            () => Base(Flag("r")).Using("p", b => b.DenyAboveProbability(0.9)).Build(),
            "r", "p"
        },
        { "a threshold with no ladder rung", () => Base(Ladder(Verdict.Deny)).Using("p", _warnDeny).Build(), "r", "p" },
        {
            "a threshold repeated",
            () => Base(Ladder(Verdict.Deny))
                .Using("p", b => b.DenyAboveProbability(0.8).DenyAboveProbability(0.9)).Build(),
            "r", "p"
        },
        {
            "thresholds not increasing with severity",
            () => Base(Flag("r")).Using("p", b => b.WarnAboveProbability(0.9).DenyAboveProbability(0.6)).Build(),
            "r", "p"
        },
        {
            "thresholds equal across severity",
            () => Base(Flag("r")).Using("p", b => b.WarnAboveProbability(0.6).DenyAboveProbability(0.6)).Build(),
            "r", "p"
        },
        {
            "two evidence kinds across thresholds",
            () => Base(Flag("r")).Using("p", b => b.WarnAboveProbability(0.6).DenyAboveScore(0.9)).Build(),
            "r", "p"
        },
        {
            "gate kind differs from the thresholds",
            () => Base(Flag("r")).Using("p", b => _warnDeny(b.WhenScoreMarginBelow(0.1))).Build(),
            "r", "p"
        },
        {
            "probability threshold above one",
            () => Base(Ladder(Verdict.Deny)).Using("p", b => b.DenyAboveProbability(1.5)).Build(),
            "r", "p"
        },
        {
            "probability threshold below zero",
            () => Base(Ladder(Verdict.Deny)).Using("p", b => b.DenyAboveProbability(-0.1)).Build(),
            "r", "p"
        },
        {
            "score threshold of NaN on a single rung",
            () => Base(Ladder(Verdict.Deny)).Using("p", b => b.DenyAboveScore(double.NaN)).Build(),
            "r", "p"
        },
        {
            "score threshold of positive infinity",
            () => Base(Flag("r")).Using("p", b => b.WarnAboveScore(0.4).DenyAboveScore(double.PositiveInfinity))
                .Build(),
            "r", "p"
        },
        {
            "score threshold of negative infinity",
            () => Base(Flag("r")).Using("p", b => b.WarnAboveScore(double.NegativeInfinity).DenyAboveScore(0.9))
                .Build(),
            "r", "p"
        },
        {
            "gate of zero",
            () => Base(Flag("r")).Using("p", b => _warnDeny(b.WhenProbabilityMarginBelow(0))).Build(),
            "r", "p"
        },
        {
            "gate below zero on a choice",
            () => Base(_choice).Using("p", b => b.WhenScoreMarginBelow(-1)).Build(),
            "c", "p"
        },
        {
            "gate of positive infinity",
            () => Base(Flag("r")).Using("p", b => _warnDeny(b.WhenProbabilityMarginBelow(double.PositiveInfinity)))
                .Build(),
            "r", "p"
        },
        {
            "gate of NaN on a choice",
            () => Base(_choice).Using("p", b => b.WhenScoreMarginBelow(double.NaN)).Build(),
            "c", "p"
        },
        {
            "thresholds on a choice operating point",
            () => Base(_choice).Using("p", b => b.ForRule("c", op => op.DenyAboveProbability(0.9))).Build(),
            "c", "p"
        },
        {
            "thresholds on a score operating point",
            () => Base(Score("a", "b").DenyAtOrAbove("b").Build())
                .Using("p", b => b.ForRule("s", op => op.DenyAboveScore(0.9))).Build(),
            "s", "p"
        },

        // Build() only.
        {
            "no mode",
            () => Policy.Define("pol").Rule(Flag()).Using("p", _warnDeny).OnFailure(FailureBehavior.Deny).Build(),
            null, null
        },
        {
            "no failure behaviour",
            () => Policy.Define("pol").Enforce().Rule(Flag()).Using("p", _warnDeny).Build(),
            null, null
        },
        {
            "shorthand mixed with ForRule",
            () => Base(Flag("r")).Using("p", b => b.WarnAboveProbability(0.6).ForRule("r", _warnDenyPoint)).Build(),
            null, "p"
        },
        { "shorthand thresholds with no boolean rule", () => Base(_choice).Using("p", _warnDeny).Build(), null, "p" },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void Build_Rejects_Incomplete_Or_Contradictory_Definitions(
        string label,
        Func<Policy> build,
        string? ruleId,
        string? providerId)
    {
        Action act = () => build();

        PolicyConfigurationException error = act.Should().Throw<PolicyConfigurationException>(label).Which;
        error.Should().BeEquivalentTo(new { RuleId = ruleId, ProviderId = providerId }, label);
        error.Message.Should().StartWith($"Policy '{error.PolicyId}'", label)
            .And.NotContain("question-marker")
            .And.NotContain("description-marker")
            .And.NotContain("level-marker");
    }

    [Fact]
    public void Validate_Holds_A_Directly_Constructed_Policy_To_The_Same_Rules()
    {
        BooleanRule rule = new("r", _question, true, [Verdict.Warn, Verdict.Deny]);
        Threshold warn = new(Verdict.Warn, EvidenceKind.Probability, 0.6);
        Threshold deny = new(Verdict.Deny, EvidenceKind.Probability, 0.9);
        Policy valid = new(
            "pol",
            PolicyMode.Enforce,
            [rule],
            [new ProviderBinding("p", [new RuleOperatingPoint("r", [warn, deny])])],
            FailureBehavior.Deny);
        Policy fallbackWithoutThen = valid with { OnFailure = new FailureBehavior(FailureAction.Fallback) };
        Policy rungWithoutThreshold = valid with
        {
            Bindings = [new ProviderBinding("p", [new RuleOperatingPoint("r", [deny])])],
        };

        valid.Invoking(p => p.Validate()).Should().NotThrow();
        valid.Invoking(p => p.Validate()).Should().NotThrow("validation is idempotent");

        PolicyConfigurationException noThen = fallbackWithoutThen.Invoking(p => p.Validate())
            .Should().Throw<PolicyConfigurationException>().Which;
        noThen.PolicyId.Should().Be("pol");
        noThen.Message.Should().Contain("pol");

        PolicyConfigurationException noThreshold = rungWithoutThreshold.Invoking(p => p.Validate())
            .Should().Throw<PolicyConfigurationException>().Which;
        noThreshold.Should().BeEquivalentTo(new { PolicyId = "pol", RuleId = "r", ProviderId = "p" });
        noThreshold.Message.Should().Contain("pol").And.Contain("r").And.Contain("p");
    }

    private static PolicyBuilder Base(params Rule[] rules)
    {
        PolicyBuilder builder = Policy.Define("pol").Enforce().OnFailure(FailureBehavior.Deny);
        foreach (Rule rule in rules)
        {
            builder.Rule(rule);
        }

        return builder;
    }

    private static Policy Failing(FailureBehavior behavior) =>
        Base(Flag()).Using("p", _warnDeny).OnFailure(behavior).Build();

    private static BooleanRule Flag(string id = "r") =>
        Policy.Rule(id).Boolean(_question).WhenTrue(Verdict.Warn, Verdict.Deny);

    private static BooleanRule Ladder(params Verdict[] ladder) => Policy.Rule("r").Boolean(_question).WhenTrue(ladder);

    private static ScoreRuleBuilder Score(params string[] levels) => Policy.Rule("s").Score(_question, levels);

    private static ChoiceRule Choice(params (string Key, string Description, Verdict Verdict)[] options)
    {
        ChoiceRuleBuilder builder = Policy.Rule("c").Choice(_question);
        foreach ((string key, string description, Verdict verdict) in options)
        {
            builder.Option(key, description, verdict);
        }

        return builder.Build();
    }
}
