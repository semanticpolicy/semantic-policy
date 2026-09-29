using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests.Evaluation;

public sealed class PolicyEvaluationTests
{
    private const string _rule = "injection";
    private static readonly ProviderMetadata _provider = new("local", "model-l", 5);

    public static TheoryData<bool, Evidence, Verdict> Ladders => new()
    {
        { true, Probability(("true", 0.95), ("false", 0.05)), Verdict.Deny },
        { true, Probability(("true", 0.7), ("false", 0.3)), Verdict.Warn },
        { true, Probability(("true", 0.6), ("false", 0.4)), Verdict.Warn },
        { true, Probability(("true", 0.5), ("false", 0.5)), Verdict.Allow },
        { false, Probability(("true", 0.3)), Verdict.Warn },
    };

    [Theory]
    [MemberData(nameof(Ladders))]
    public void Boolean_Rule_Takes_The_Most_Severe_Rung_Its_Evidence_Reaches(
        bool flaggedAnswer,
        Evidence evidence,
        Verdict expected)
    {
        Policy policy = Define(FailureBehavior.Deny, ["local"], [Flagged(answer: flaggedAnswer)]);
        var attempts = Attempts((_rule, 0, Answer(new BooleanValue(true), evidence)));

        EvaluationStep step = PolicyEvaluation.Evaluate(policy, attempts);

        step.IsComplete.Should().BeTrue();
        step.Required.Should().BeEmpty();
        step.Verdict!.Evaluated.Should().Be(expected);
        step.Verdict.Effective.Should().Be(expected);
        RuleVerdict rule = step.Verdict.Rules.Should().ContainSingle().Subject;
        rule.RuleId.Should().Be(_rule);
        rule.Verdict.Should().Be(expected);
        rule.Source.Should().Be(VerdictSource.Threshold);
    }

    [Fact]
    public void Boolean_Verdict_Ignores_The_Providers_Value_Cut()
    {
        Policy policy = Define(FailureBehavior.Deny, ["local"], [Flagged()]);
        var attempts = Attempts((_rule, 0, BooleanAnswer(0.55)));

        EvaluationStep step = PolicyEvaluation.Evaluate(policy, attempts);

        step.Verdict!.Evaluated.Should().Be(Verdict.Allow);
    }

    [Fact]
    public void Shadow_Returns_Allow_As_Effective_And_Keeps_The_Evaluated_Verdict()
    {
        Policy policy = Define(FailureBehavior.Deny, ["local"], [Flagged()], PolicyMode.Shadow);
        var attempts = Attempts((_rule, 0, BooleanAnswer(0.95)));

        PolicyVerdict verdict = PolicyEvaluation.Evaluate(policy, attempts).Verdict!;

        verdict.PolicyId.Should().Be("p");
        verdict.Mode.Should().Be(PolicyMode.Shadow);
        verdict.Evaluated.Should().Be(Verdict.Deny);
        verdict.Effective.Should().Be(Verdict.Allow);
        verdict.Rules.Should().ContainSingle().Which.Verdict.Should().Be(Verdict.Deny);
    }

    [Fact]
    public void Step_Never_Asks_For_An_Attempt_It_Holds_And_Is_Deterministic()
    {
        Policy policy = Define(FailureBehavior.Deny, ["local", "jev"], [Flagged("a"), Flagged("b")]);
        ProviderResult answer = BooleanAnswer(0.95);

        var none = Attempts();
        EvaluationStep empty = PolicyEvaluation.Evaluate(policy, none);
        empty.IsComplete.Should().BeFalse();
        empty.Verdict.Should().BeNull();
        empty.Required.Should().Equal(new AttemptKey("a", 0), new AttemptKey("b", 0));
        PolicyEvaluation.Evaluate(policy, none).Should().BeEquivalentTo(empty);

        var one = Attempts(("a", 0, answer));
        PolicyEvaluation.Evaluate(policy, one).Required.Should().Equal(new AttemptKey("b", 0));

        var both = Attempts(("a", 0, answer), ("b", 0, answer));
        EvaluationStep complete = PolicyEvaluation.Evaluate(policy, both);
        complete.IsComplete.Should().BeTrue();
        complete.Required.Should().BeEmpty();
        PolicyEvaluation.Evaluate(policy, both).Should().BeEquivalentTo(complete);
    }

    [Fact]
    public void Evaluate_Rejects_An_Invalid_Policy_And_A_Stray_Attempt_Key()
    {
        Policy incomplete = new("p", PolicyMode.Enforce, [], [], FailureBehavior.Deny);
        Action invalid = () => PolicyEvaluation.Evaluate(incomplete, Attempts());
        invalid.Should().Throw<PolicyConfigurationException>();

        Policy policy = Define(FailureBehavior.Deny, ["local"], [Flagged()]);
        ProviderResult answer = BooleanAnswer(0.95);
        Action strayRule = () => PolicyEvaluation.Evaluate(policy, Attempts(("ghost", 0, answer)));
        strayRule.Should().Throw<ArgumentException>().WithMessage("*ghost*");
        Action strayBinding = () => PolicyEvaluation.Evaluate(policy, Attempts((_rule, 1, answer)));
        strayBinding.Should().Throw<ArgumentException>().WithMessage($"*{_rule}*1*");
    }

    [Fact]
    public void Serialized_Verdict_Carries_Every_Attempt_Without_Raw()
    {
        Policy policy = Define(FailureBehavior.Deny, ["local"], [Flagged("a"), Flagged("b")]);
        JsonElement raw = JsonSerializer.Deserialize<JsonElement>("""{ "text": "raw-marker" }""");
        var attempts = Attempts(
            ("a", 0, BooleanAnswer(0.95) with { Raw = raw }),
            ("b", 0, Answer(new BooleanValue(false), Probability(("true", 0.1), ("false", 0.9))) with { Raw = raw }));
        PolicyVerdict verdict = PolicyEvaluation.Evaluate(policy, attempts).Verdict!;

        string json = JsonSerializer.Serialize(verdict, SemanticPolicyJson.Options);

        JsonNode rules = JsonNode.Parse(json)!["rules"]!;
        rules.AsArray().Should().HaveCount(2);
        JsonNode first = rules[0]!["attempts"]![0]!["result"]!;
        first["value"]!.GetValue<bool>().Should().BeTrue();
        first["evidence"]![0]!["values"]!["true"]!.GetValue<double>().Should().Be(0.95);
        first["provider"]!["id"]!.GetValue<string>().Should().Be("local");
        rules[1]!["attempts"]![0]!["result"]!["value"]!.GetValue<bool>().Should().BeFalse();
        json.Should().NotContain("raw-marker").And.NotContain("\"raw\"");
        json.Should().NotContain("calibratedEvidence").And.NotContain("calibrationModelMismatch");

        EvidenceCalibration calibration = Platt(EvidenceKind.Score, CalibrationTransform.LogOdds, 2, 3 * Math.Log(3)) with
        {
            Provenance = new CalibrationProvenance("model-x"),
        };
        ProviderResult scored = Answer(new BooleanValue(false), Of(EvidenceKind.Score, ("true", 0.25), ("false", 0.75))) with
        {
            Raw = raw,
        };
        PolicyVerdict calibrated = PolicyEvaluation.Evaluate(Calibrated(calibration), Attempts((_rule, 0, scored))).Verdict!;

        string calibratedJson = JsonSerializer.Serialize(calibrated, SemanticPolicyJson.Options);

        JsonNode attempt = JsonNode.Parse(calibratedJson)!["rules"]![0]!["attempts"]![0]!;
        attempt["result"]!["evidence"]![0]!["kind"]!.GetValue<string>().Should().Be("score");
        attempt["calibratedEvidence"]!["kind"]!.GetValue<string>().Should().Be("probability");
        attempt["calibratedEvidence"]!["values"]!["true"]!.GetValue<double>().Should().BeApproximately(0.75, 1e-9);
        attempt["calibratedEvidence"]!["values"]!["false"]!.GetValue<double>().Should().BeApproximately(0.25, 1e-9);
        attempt["calibrationModelMismatch"]!.GetValue<bool>().Should().BeTrue();
        calibratedJson.Should().NotContain("raw-marker").And.NotContain("\"raw\"");
    }

    [Fact]
    public void Uncertain_Attempt_Moves_To_The_Next_Binding()
    {
        Policy policy = Define(FailureBehavior.Deny, ["local", "jev"], [Flagged()], gate: 0.2);
        ProviderResult flat = BooleanAnswer(0.52);

        EvaluationStep first = PolicyEvaluation.Evaluate(policy, Attempts((_rule, 0, flat)));

        first.IsComplete.Should().BeFalse();
        first.Required.Should().Equal(new AttemptKey(_rule, 1));

        ProviderResult clear = BooleanAnswer(0.95);
        EvaluationStep second = PolicyEvaluation.Evaluate(policy, Attempts((_rule, 0, flat), (_rule, 1, clear)));

        second.Required.Should().BeEmpty();
        RuleVerdict rule = second.Verdict!.Rules.Should().ContainSingle().Subject;
        rule.Verdict.Should().Be(Verdict.Deny);
        rule.DecidingBinding.Should().Be(1);
        rule.Attempts.Should().HaveCount(2);
        rule.Attempts[0].ProviderId.Should().Be("local");
        rule.Attempts[0].Disposition.Should().Be(AttemptDisposition.MovedOnByGate);
        rule.Attempts[0].Margin.Should().BeApproximately(0.04, 1e-9);
        rule.Attempts[1].ProviderId.Should().Be("jev");
        rule.Attempts[1].Disposition.Should().Be(AttemptDisposition.Decided);
        rule.Attempts[1].Margin.Should().BeApproximately(0.9, 1e-9);
    }

    [Fact]
    public void Uncertainty_On_The_Last_Binding_Yields_Abstain()
    {
        Policy policy = Define(FailureBehavior.Deny, ["local"], [Flagged()], gate: 0.2);
        var attempts = Attempts((_rule, 0, BooleanAnswer(0.52)));

        PolicyVerdict verdict = PolicyEvaluation.Evaluate(policy, attempts).Verdict!;

        verdict.Evaluated.Should().Be(Verdict.Abstain);
        RuleVerdict rule = verdict.Rules.Should().ContainSingle().Subject;
        rule.Verdict.Should().Be(Verdict.Abstain);
        rule.Source.Should().Be(VerdictSource.UncertaintyExhausted);
        rule.DecidingBinding.Should().BeNull();
        rule.RungCrossed.Should().BeNull();
        Attempt attempt = rule.Attempts.Should().ContainSingle().Subject;
        attempt.Disposition.Should().Be(AttemptDisposition.ExhaustedByGate);
        attempt.Margin.Should().BeApproximately(0.04, 1e-9);
    }

    [Fact]
    public void Verdict_Trace_Names_The_Deciding_Binding_And_The_Rung_Crossed()
    {
        Policy policy = Define(FailureBehavior.Deny, ["local", "jev"], [Flagged(answer: false)], gate: 0.2);
        ProviderResult flat = Answer(new BooleanValue(false), Probability(("true", 0.45), ("false", 0.55)));
        ProviderResult clear = Answer(new BooleanValue(false), Probability(("true", 0.3), ("false", 0.7)));

        var attempts = Attempts((_rule, 0, flat), (_rule, 1, clear));
        RuleVerdict rule = PolicyEvaluation.Evaluate(policy, attempts).Verdict!.Rules.Single();

        rule.Should().BeEquivalentTo(new
        {
            RuleId = _rule,
            Verdict = Verdict.Warn,
            Source = VerdictSource.Threshold,
            DecidingBinding = 1,
            RungCrossed = Verdict.Warn,
            EvidenceKind = EvidenceKind.Probability,
            EvidenceValue = 0.7,
        });
        rule.Attempts.Select(attempt => (attempt.BindingIndex, attempt.ProviderId, attempt.Disposition))
            .Should().Equal((0, "local", AttemptDisposition.MovedOnByGate), (1, "jev", AttemptDisposition.Decided));
        rule.Attempts[0].Result.Should().BeSameAs(flat);
        rule.Attempts[1].Result.Should().BeSameAs(clear);
    }

    public static TheoryData<ProviderOutcome, FailureBehavior, Verdict> FailureBehaviours
    {
        get
        {
            TheoryData<ProviderOutcome, FailureBehavior, Verdict> rows = new();
            IEnumerable<ProviderOutcome> outcomes = Enum.GetValues<FailureKind>()
                .Select(kind => ProviderOutcome.Failure(kind, "no answer"))
                .Append(ProviderOutcome.Abstain("declined"));
            foreach (ProviderOutcome outcome in outcomes)
            {
                rows.Add(outcome, FailureBehavior.Allow, Verdict.Allow);
                rows.Add(outcome, FailureBehavior.Deny, Verdict.Deny);
                rows.Add(outcome, FailureBehavior.Escalate, Verdict.Escalate);
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(FailureBehaviours))]
    public void Failed_Or_Abstained_Attempt_Applies_The_Declared_Failure_Behaviour(
        ProviderOutcome outcome,
        FailureBehavior behaviour,
        Verdict expected)
    {
        Policy policy = Define(behaviour, ["local", "jev"], [Flagged()]);
        ProviderResult result = new(DecisionType.Boolean, outcome, Value: null, Evidence: [], _provider);

        EvaluationStep step = PolicyEvaluation.Evaluate(policy, Attempts((_rule, 0, result)));

        step.Required.Should().BeEmpty();
        step.Verdict!.Evaluated.Should().Be(expected);
        RuleVerdict rule = step.Verdict.Rules.Single();
        rule.Verdict.Should().Be(expected);
        rule.Source.Should().Be(VerdictSource.FailureBehavior);
        rule.DecidingBinding.Should().BeNull();
        Attempt attempt = rule.Attempts.Should().ContainSingle().Subject;
        attempt.Result.Should().BeSameAs(result);
        attempt.EffectiveOutcome.Should().Be(outcome);
        attempt.Disposition.Should().Be(AttemptDisposition.TerminatedByFailure);
        attempt.Margin.Should().BeNull();
    }

    [Fact]
    public void Fallback_Re_Evaluates_Only_The_Failed_Rule_On_The_Next_Binding()
    {
        Policy policy = Define(FailureBehavior.Fallback(Verdict.Deny), ["local", "jev"], [Flagged("a"), Flagged("b")]);
        ProviderResult flagged = BooleanAnswer(0.95);
        ProviderResult timedOut = Failed(FailureKind.Timeout);

        EvaluationStep first = PolicyEvaluation.Evaluate(policy, Attempts(("a", 0, flagged), ("b", 0, timedOut)));

        first.IsComplete.Should().BeFalse();
        first.Required.Should().Equal(new AttemptKey("b", 1));

        ProviderResult clear = Answer(new BooleanValue(false), Probability(("true", 0.1), ("false", 0.9)));
        var attempts = Attempts(("a", 0, flagged), ("b", 0, timedOut), ("b", 1, clear));
        PolicyVerdict verdict = PolicyEvaluation.Evaluate(policy, attempts).Verdict!;

        verdict.Evaluated.Should().Be(Verdict.Deny);
        verdict.Rules.Select(rule => (rule.RuleId, rule.Verdict, rule.DecidingBinding))
            .Should().Equal(("a", Verdict.Deny, 0), ("b", Verdict.Allow, 1));
        verdict.Rules[0].Attempts.Should().ContainSingle().Which.Disposition.Should().Be(AttemptDisposition.Decided);
        verdict.Rules[1].Attempts.Select(attempt => attempt.Disposition)
            .Should().Equal(AttemptDisposition.MovedOnByFailure, AttemptDisposition.Decided);
    }

    [Fact]
    public void Exhausted_Fallback_Yields_The_Declared_Terminal()
    {
        Policy policy = Define(FailureBehavior.Fallback(Verdict.Deny), ["local", "jev"], [Flagged()]);
        ProviderResult unavailable = Failed(FailureKind.Unavailable);
        ProviderResult timedOut = Failed(FailureKind.Timeout);

        var attempts = Attempts((_rule, 0, unavailable), (_rule, 1, timedOut));
        PolicyVerdict verdict = PolicyEvaluation.Evaluate(policy, attempts).Verdict!;

        verdict.Evaluated.Should().Be(Verdict.Deny);
        RuleVerdict rule = verdict.Rules.Single();
        rule.Source.Should().Be(VerdictSource.FailureBehavior);
        rule.DecidingBinding.Should().BeNull();
        rule.Attempts.Select(attempt => attempt.Disposition)
            .Should().Equal(AttemptDisposition.MovedOnByFailure, AttemptDisposition.TerminatedByFailure);
        rule.Attempts[1].EffectiveOutcome.Kind.Should().Be(FailureKind.Timeout);
    }

    [Theory]
    [InlineData("allow", Verdict.Allow)]
    [InlineData("human_review", Verdict.Escalate)]
    [InlineData("block", Verdict.Deny)]
    public void Choice_Rule_Maps_The_Winning_Option_To_Its_Verdict(string option, Verdict expected)
    {
        ChoiceRule route = Policy.Rule("route").Choice("q")
            .Option("allow", "a", Verdict.Allow)
            .Option("human_review", "h", Verdict.Escalate)
            .Option("block", "b", Verdict.Deny)
            .Build();
        Policy policy = Define(FailureBehavior.Deny, ["local"], [route]);
        var attempts = Attempts(("route", 0, Answer(new ChoiceValue(option))));

        RuleVerdict rule = PolicyEvaluation.Evaluate(policy, attempts).Verdict!.Rules.Single();

        rule.Verdict.Should().Be(expected);
        rule.Source.Should().Be(VerdictSource.OptionMap);
        rule.DecidingBinding.Should().Be(0);
        rule.RungCrossed.Should().BeNull();
        rule.EvidenceKind.Should().BeNull();
        rule.EvidenceValue.Should().BeNull();
    }

    [Theory]
    [InlineData("harmless", 0, Verdict.Allow, null)]
    [InlineData("minor", 1, Verdict.Allow, null)]
    [InlineData("moderate", 2, Verdict.Warn, Verdict.Warn)]
    [InlineData("serious", 3, Verdict.Deny, Verdict.Deny)]
    [InlineData("critical", 4, Verdict.Deny, Verdict.Deny)]
    public void Score_Rule_Takes_The_Most_Severe_Rung_At_Or_Below_The_Level(
        string level,
        int index,
        Verdict expected,
        Verdict? rungCrossed)
    {
        ScoreRule severity = Policy.Rule("severity").Score("q", "harmless", "minor", "moderate", "serious", "critical")
            .WarnAtOrAbove("moderate")
            .DenyAtOrAbove("serious")
            .Build();
        Policy policy = Define(FailureBehavior.Deny, ["local"], [severity]);
        var attempts = Attempts(("severity", 0, Answer(new ScoreValue(level, index))));

        RuleVerdict rule = PolicyEvaluation.Evaluate(policy, attempts).Verdict!.Rules.Single();

        rule.Verdict.Should().Be(expected);
        rule.Source.Should().Be(VerdictSource.LevelMap);
        rule.DecidingBinding.Should().Be(0);
        rule.RungCrossed.Should().Be(rungCrossed);
    }

    [Theory]
    [InlineData(Verdict.Allow, Verdict.Warn, Verdict.Warn)]
    [InlineData(Verdict.Abstain, Verdict.Warn, Verdict.Abstain)]
    [InlineData(Verdict.Deny, Verdict.Abstain, Verdict.Deny)]
    [InlineData(Verdict.Escalate, Verdict.Abstain, Verdict.Escalate)]
    public void Policy_Verdict_Is_The_Most_Severe_Rule_Verdict(Verdict first, Verdict second, Verdict expected)
    {
        Policy policy = Define(FailureBehavior.Deny, ["local"], [Graded("a"), Graded("b")], gate: 0.2);
        var attempts = Attempts(("a", 0, Yielding(first)), ("b", 0, Yielding(second)));

        PolicyVerdict verdict = PolicyEvaluation.Evaluate(policy, attempts).Verdict!;

        verdict.Rules.Select(rule => rule.Verdict).Should().Equal(first, second);
        verdict.Evaluated.Should().Be(expected);
        verdict.Effective.Should().Be(expected);
    }

    public static TheoryData<string, Policy, ProviderResult> BrokenContracts
    {
        get
        {
            Policy probability = Define(FailureBehavior.Deny, ["local"], [Flagged()]);
            Policy score = Policy.Define("p").Enforce().Rule(Flagged())
                .Using("local", b => b.WarnAboveScore(1.0).DenyAboveScore(3.0))
                .OnFailure(FailureBehavior.Deny)
                .Build();
            Policy choice = Define(FailureBehavior.Deny, ["local"], [Graded("route")]);
            Policy gatedChoice = Define(FailureBehavior.Deny, ["local"], [Graded("route")], gate: 0.2);
            ScoreRule severity = Policy.Rule("severity").Score("q", "harmless", "moderate", "serious")
                .DenyAtOrAbove("serious")
                .Build();
            Policy level = Define(FailureBehavior.Deny, ["local"], [severity]);
            Policy calibrated = Calibrated(Platt(EvidenceKind.Score, CalibrationTransform.LogOdds, 1.5, 0));
            ProviderResult good = BooleanAnswer(0.95);

            return new TheoryData<string, Policy, ProviderResult>
            {
                { "wrong protocol", probability, good with { Protocol = "semanticpolicy/v1" } },
                { "wrong type", probability, good with { Type = DecisionType.Choice } },
                { "null value", probability, good with { Value = null } },
                { "wrong value shape", probability, good with { Value = new ChoiceValue("true") } },
                { "null evidence list", probability, good with { Evidence = null! } },
                { "unknown option", choice, Answer(new ChoiceValue("maybe")) },
                { "unknown level", level, Answer(new ScoreValue("nope", 1)) },
                { "level index mismatch", level, Answer(new ScoreValue("moderate", 0)) },
                {
                    "missing evidence kind",
                    probability,
                    Answer(new BooleanValue(true), Of(EvidenceKind.Score, ("true", 2.0), ("false", 0.5)))
                },
                {
                    "one-key score evidence",
                    score,
                    Answer(new BooleanValue(true), Of(EvidenceKind.Score, ("true", 2.0)))
                },
                {
                    "flagged key absent after the complement",
                    probability,
                    Answer(new BooleanValue(true), Probability(("yes", 0.9)))
                },
                { "no evidence under a gate", gatedChoice, Answer(new ChoiceValue("allow")) },
                { "null evidence list under a gate", gatedChoice, Answer(new ChoiceValue("allow")) with { Evidence = null! } },
                {
                    "single-value evidence under a gate",
                    gatedChoice,
                    Answer(new ChoiceValue("allow"), Probability(("allow", 0.9)))
                },
                {
                    "calibrated point without its source kind's evidence",
                    calibrated,
                    Answer(new BooleanValue(true), Probability(("true", 0.95), ("false", 0.05)))
                },
                {
                    "one-key score evidence at a calibrated point",
                    calibrated,
                    Answer(new BooleanValue(true), Of(EvidenceKind.Score, ("true", 0.8)))
                },
            };
        }
    }

    [Theory]
    [MemberData(nameof(BrokenContracts))]
    public void Success_That_Breaks_The_Contract_Is_Malformed_For_That_Attempt(
        string reason,
        Policy policy,
        ProviderResult result)
    {
        reason.Should().NotBeEmpty();
        var attempts = Attempts((policy.Rules[0].Id, 0, result));

        EvaluationStep step = PolicyEvaluation.Evaluate(policy, attempts);

        step.Required.Should().BeEmpty();
        RuleVerdict rule = step.Verdict!.Rules.Single();
        rule.Verdict.Should().Be(Verdict.Deny);
        rule.Source.Should().Be(VerdictSource.FailureBehavior);
        rule.DecidingBinding.Should().BeNull();
        Attempt attempt = rule.Attempts.Should().ContainSingle().Subject;
        attempt.Result.Should().BeSameAs(result);
        attempt.Result.Outcome.Status.Should().Be(OutcomeStatus.Success);
        attempt.EffectiveOutcome.Status.Should().Be(OutcomeStatus.Failure);
        attempt.EffectiveOutcome.Kind.Should().Be(FailureKind.Malformed);
        attempt.Disposition.Should().Be(AttemptDisposition.TerminatedByFailure);
        attempt.Margin.Should().BeNull();
    }

    // Each expected value is a closed form, σ(ln k) = k / (1 + k): the Score row maps its raw 0.25 through
    // 2 · ln(1/3) + 3 · ln 3 = ln 3 to 0.75, the Logit row its raw 0.5 through 2 · 0.5 + ln 19 − 1 = ln 19 to
    // 0.95, and the one-key Probability row, flagged on false, completes 0.75 on true to 0.25 on false before
    // mapping it through ln(1/3) + ln 9 = ln 3 to 0.75 — where 1 − Apply(0.75) would be 1/28, an Allow.
    public static TheoryData<string, bool, EvidenceCalibration, Evidence, Verdict, double> CalibratedLadders => new()
    {
        {
            "score under log-odds",
            true,
            Platt(EvidenceKind.Score, CalibrationTransform.LogOdds, 2, 3 * Math.Log(3)),
            Of(EvidenceKind.Score, ("true", 0.25), ("false", 0.75)),
            Verdict.Warn,
            0.75
        },
        {
            "logit under identity",
            true,
            Platt(EvidenceKind.Logit, CalibrationTransform.Identity, 2, Math.Log(19) - 1),
            Of(EvidenceKind.Logit, ("true", 0.5), ("false", -0.5)),
            Verdict.Deny,
            0.95
        },
        {
            "one-key probability flagged on false",
            false,
            Platt(EvidenceKind.Probability, CalibrationTransform.LogOdds, 1, Math.Log(9)),
            Probability(("true", 0.75)),
            Verdict.Warn,
            0.75
        },
    };

    [Theory]
    [MemberData(nameof(CalibratedLadders))]
    public void Calibrated_Point_Decides_The_Ladder_On_The_Calibrated_Probability(
        string label,
        bool flaggedAnswer,
        EvidenceCalibration calibration,
        Evidence evidence,
        Verdict expected,
        double calibrated)
    {
        Policy policy = Calibrated(calibration, Flagged(answer: flaggedAnswer));
        var attempts = Attempts((_rule, 0, Answer(new BooleanValue(true), evidence)));

        RuleVerdict rule = PolicyEvaluation.Evaluate(policy, attempts).Verdict!.Rules.Single();

        rule.Verdict.Should().Be(expected, label);
        rule.Source.Should().Be(VerdictSource.Threshold, label);
        rule.RungCrossed.Should().Be(expected, label);
        rule.EvidenceKind.Should().Be(EvidenceKind.Probability, label);
        rule.EvidenceValue.Should().BeApproximately(calibrated, 1e-9, label);
        Evidence produced = rule.Attempts.Single().CalibratedEvidence!;
        string flagged = flaggedAnswer ? "true" : "false";
        string other = flaggedAnswer ? "false" : "true";
        produced.Values.Keys.Should().BeEquivalentTo(["true", "false"], label);
        produced.Values[flagged].Should().BeApproximately(calibrated, 1e-9, label);
        produced.Values[other].Should().BeApproximately(1 - calibrated, 1e-9, label);
    }

    [Fact]
    public void Calibrated_Attempt_Keeps_The_Result_And_Carries_The_Calibrated_Evidence()
    {
        EvidenceCalibration calibration = Platt(EvidenceKind.Score, CalibrationTransform.LogOdds, 1, Math.Log(9));
        Policy policy = Policy.Define("p").Enforce().Rule(Flagged())
            .Using("local", b => b.ForRule(_rule, op => op
                .WarnAboveProbability(0.6)
                .DenyAboveProbability(0.9)
                .WhenScoreMarginBelow(0.2)
                .Calibrate(calibration)))
            .Using("plain", b => b.WarnAboveProbability(0.6).DenyAboveProbability(0.9).WhenProbabilityMarginBelow(0.2))
            .Using("jev", b => b.ForRule(_rule, op => op.WarnAboveProbability(0.6).DenyAboveProbability(0.9).Calibrate(calibration)))
            .OnFailure(FailureBehavior.Deny)
            .Build();
        ProviderResult gated = Answer(new BooleanValue(true), Of(EvidenceKind.Score, ("true", 0.55), ("false", 0.45)));
        ProviderResult flat = BooleanAnswer(0.52);
        ProviderResult deciding = Answer(new BooleanValue(false), Of(EvidenceKind.Score, ("true", 0.25), ("false", 0.75)));

        RuleVerdict rule = PolicyEvaluation.Evaluate(policy, Attempts((_rule, 0, gated), (_rule, 1, flat), (_rule, 2, deciding)))
            .Verdict!.Rules.Single();

        rule.Attempts.Select(attempt => attempt.Disposition).Should().Equal(
            AttemptDisposition.MovedOnByGate,
            AttemptDisposition.MovedOnByGate,
            AttemptDisposition.Decided);
        rule.Attempts.Select(attempt => attempt.Result).Should().Equal(gated, flat, deciding);
        gated.Evidence.Should().ContainSingle().Which.Values["true"].Should().Be(0.55);
        rule.Attempts[1].CalibratedEvidence.Should().BeNull();
        (Attempt Attempt, double True)[] calibrated = [(rule.Attempts[0], 11.0 / 12), (rule.Attempts[2], 0.75)];
        foreach ((Attempt attempt, double pTrue) in calibrated)
        {
            Evidence evidence = attempt.CalibratedEvidence!;
            evidence.Kind.Should().Be(EvidenceKind.Probability);
            evidence.Scale.Should().BeNull();
            evidence.Values.Keys.Should().BeEquivalentTo(["true", "false"]);
            evidence.Values["true"].Should().BeApproximately(pTrue, 1e-9);
            evidence.Values["false"].Should().BeApproximately(1 - pTrue, 1e-9);
        }

        rule.Attempts.Should().AllSatisfy(attempt => attempt.CalibrationModelMismatch.Should().BeFalse());
        rule.Verdict.Should().Be(Verdict.Warn);
    }

    public static TheoryData<string, CalibrationProvenance?, string?, bool> ModelPairs => new()
    {
        { "same model", new CalibrationProvenance("model-l"), "model-l", false },
        { "another model", new CalibrationProvenance("model-x"), "model-l", true },
        { "the same name in another case", new CalibrationProvenance("Model-L"), "model-l", true },
        { "result names no model", new CalibrationProvenance("model-x"), null, false },
        { "provenance names no model", new CalibrationProvenance(DatasetDigest: "sha256:digest-marker"), "model-l", false },
        { "no provenance", null, "model-l", false },
    };

    [Theory]
    [MemberData(nameof(ModelPairs))]
    public void Model_Mismatch_Is_Marked_Only_When_Both_Models_Are_Named_And_Differ(
        string label,
        CalibrationProvenance? provenance,
        string? model,
        bool marked)
    {
        EvidenceCalibration calibration = Platt(EvidenceKind.Score, CalibrationTransform.LogOdds, 2, 3 * Math.Log(3)) with
        {
            Provenance = provenance,
        };
        ProviderResult result = Answer(new BooleanValue(false), Of(EvidenceKind.Score, ("true", 0.25), ("false", 0.75))) with
        {
            Provider = _provider with { Model = model },
        };

        RuleVerdict rule = PolicyEvaluation.Evaluate(Calibrated(calibration), Attempts((_rule, 0, result)))
            .Verdict!.Rules.Single();

        rule.Attempts.Single().CalibrationModelMismatch.Should().Be(marked, label);
        rule.Verdict.Should().Be(Verdict.Warn, label);
        rule.EvidenceValue.Should().BeApproximately(0.75, 1e-9, label);
    }

    [Fact]
    public void Native_Probability_Does_Not_Replace_The_Calibrated_Value()
    {
        Policy policy = Calibrated(Platt(EvidenceKind.Score, CalibrationTransform.LogOdds, 2, 3 * Math.Log(3)));
        ProviderResult result = Answer(
            new BooleanValue(true),
            Of(EvidenceKind.Score, ("true", 0.25), ("false", 0.75)),
            Probability(("true", 0.95), ("false", 0.05)));

        RuleVerdict rule = PolicyEvaluation.Evaluate(policy, Attempts((_rule, 0, result))).Verdict!.Rules.Single();

        rule.Verdict.Should().Be(Verdict.Warn);
        rule.EvidenceKind.Should().Be(EvidenceKind.Probability);
        rule.EvidenceValue.Should().BeApproximately(0.75, 1e-9);
    }

    // A raw margin of 0.1 under the 0.2 gate, which the calibration spreads to 11/12 − 1/12; and a raw margin
    // of 0.6 above it, which the calibration flattens to σ(0) − σ(0) = 0.
    public static TheoryData<string, EvidenceCalibration, Evidence, bool, double> CalibratedGates => new()
    {
        {
            "raw margin under the gate, calibrated margin above it",
            Platt(EvidenceKind.Score, CalibrationTransform.LogOdds, 1, Math.Log(9)),
            Of(EvidenceKind.Score, ("true", 0.55), ("false", 0.45)),
            true,
            0.1
        },
        {
            "raw margin above the gate, calibrated margin under it",
            Platt(EvidenceKind.Score, CalibrationTransform.LogOdds, 1, -Math.Log(4)),
            Of(EvidenceKind.Score, ("true", 0.8), ("false", 0.2)),
            false,
            0.6
        },
    };

    [Theory]
    [MemberData(nameof(CalibratedGates))]
    public void Calibrated_Point_Gate_Reads_The_Providers_Own_Margin(
        string label,
        EvidenceCalibration calibration,
        Evidence evidence,
        bool movesOn,
        double margin)
    {
        Policy policy = Calibrated(calibration, providers: ["local", "jev"], gate: 0.2);
        var attempts = Attempts((_rule, 0, Answer(new BooleanValue(true), evidence)));

        EvaluationStep step = PolicyEvaluation.Evaluate(policy, attempts);

        if (movesOn)
        {
            step.Required.Should().Equal([new AttemptKey(_rule, 1)], label);
            return;
        }

        RuleVerdict rule = step.Verdict!.Rules.Single();
        rule.DecidingBinding.Should().Be(0, label);
        rule.Verdict.Should().Be(Verdict.Allow, label);
        rule.EvidenceValue.Should().BeApproximately(0.5, 1e-9, label);
        Attempt attempt = rule.Attempts.Should().ContainSingle().Subject;
        attempt.Disposition.Should().Be(AttemptDisposition.Decided, label);
        attempt.Margin.Should().BeApproximately(margin, 1e-9, label);
    }

    // A policy's thresholds converted through a calibration's arithmetic, computed here in one process, flag
    // the rows the original flagged. Every stored value sits well clear of every raw threshold — 0.4 and 0.8
    // before the first calibration, about 0.54 and 0.82 behind the re-fit's — so an ulp decides nothing.
    public static TheoryData<string, Policy, Policy> Conversions
    {
        get
        {
            EvidenceCalibration first = Platt(EvidenceKind.Score, CalibrationTransform.LogOdds, 1.3, 0.2);
            EvidenceCalibration refit = Platt(EvidenceKind.Score, CalibrationTransform.LogOdds, 0.8, -0.5);
            Policy raw = Policy.Define("p").Enforce().Rule(Flagged())
                .Using("local", b => b.WarnAboveScore(0.4).DenyAboveScore(0.8))
                .OnFailure(FailureBehavior.Deny)
                .Build();

            return new TheoryData<string, Policy, Policy>
            {
                {
                    "first calibration",
                    raw,
                    Calibrated(first, warn: first.Apply(0.4), deny: first.Apply(0.8))
                },
                {
                    "re-fit",
                    Calibrated(first),
                    Calibrated(refit, warn: refit.Apply(first.Invert(0.6)), deny: refit.Apply(first.Invert(0.9)))
                },
            };
        }
    }

    [Theory]
    [MemberData(nameof(Conversions))]
    public void Thresholds_Converted_Through_The_Calibration_Flag_The_Same_Rows(string label, Policy before, Policy after)
    {
        double[] stored = [0.05, 0.35, 0.45, 0.6, 0.7, 0.85, 0.95];
        foreach (double value in stored)
        {
            var attempts = Attempts(
                (_rule, 0, Answer(new BooleanValue(value >= 0.5), Of(EvidenceKind.Score, ("true", value), ("false", 1 - value)))));

            RuleVerdict was = PolicyEvaluation.Evaluate(before, attempts).Verdict!.Rules.Single();
            RuleVerdict now = PolicyEvaluation.Evaluate(after, attempts).Verdict!.Rules.Single();

            now.Source.Should().Be(VerdictSource.Threshold, label);
            (now.Verdict, now.RungCrossed).Should().Be((was.Verdict, was.RungCrossed), $"{label}, stored {value}");
        }
    }

    // Every policy here is "p": the rules given, the providers given in chain order, and on each provider
    // Warn at 0.6 / Deny at 0.9 on probability for every Boolean rule plus the gate when one is asked for.
    private static Policy Define(
        FailureBehavior onFailure,
        string[] providers,
        Rule[] rules,
        PolicyMode mode = PolicyMode.Enforce,
        double? gate = null)
    {
        PolicyBuilder builder = mode == PolicyMode.Enforce ? Policy.Define("p").Enforce() : Policy.Define("p").Shadow();
        foreach (Rule rule in rules)
        {
            builder.Rule(rule);
        }

        bool thresholds = rules.Any(rule => rule is BooleanRule);
        foreach (string provider in providers)
        {
            builder.Using(provider, b =>
            {
                if (thresholds)
                {
                    b.WarnAboveProbability(0.6).DenyAboveProbability(0.9);
                }

                if (gate is { } below)
                {
                    b.WhenProbabilityMarginBelow(below);
                }
            });
        }

        return builder.OnFailure(onFailure).Build();
    }

    // Policy "p" with the rule given, or the flagged "injection", on each provider given, or "local" alone:
    // Warn at warn / Deny at deny on the probability the calibration produces, and the gate on the
    // calibration's source kind when one is asked for.
    private static Policy Calibrated(
        EvidenceCalibration calibration,
        BooleanRule? rule = null,
        string[]? providers = null,
        double? gate = null,
        double warn = 0.6,
        double deny = 0.9)
    {
        BooleanRule flagged = rule ?? Flagged();
        PolicyBuilder builder = Policy.Define("p").Enforce().Rule(flagged);
        foreach (string provider in providers ?? ["local"])
        {
            builder.Using(provider, b => b.ForRule(flagged.Id, op =>
            {
                op.WarnAboveProbability(warn).DenyAboveProbability(deny).Calibrate(calibration);
                if (gate is not { } below)
                {
                    return;
                }

                if (calibration.SourceKind == EvidenceKind.Score)
                {
                    op.WhenScoreMarginBelow(below);
                }
                else
                {
                    op.WhenProbabilityMarginBelow(below);
                }
            }));
        }

        return builder.OnFailure(FailureBehavior.Deny).Build();
    }

    private static EvidenceCalibration Platt(
        EvidenceKind source,
        CalibrationTransform transform,
        double slope,
        double intercept) =>
        new(CalibrationMethod.Platt, source, transform, slope, intercept);

    private static BooleanRule Flagged(string id = _rule, bool answer = true) =>
        answer
            ? Policy.Rule(id).Boolean("q").WhenTrue(Verdict.Warn, Verdict.Deny)
            : Policy.Rule(id).Boolean("q").WhenFalse(Verdict.Warn, Verdict.Deny);

    // A Choice rule whose option keys are the verdicts they map to, so a result can be made to yield
    // any authored verdict; Abstain comes from a flat distribution under the gate.
    private static ChoiceRule Graded(string id) =>
        Policy.Rule(id).Choice("q")
            .Option("allow", "a", Verdict.Allow)
            .Option("warn", "w", Verdict.Warn)
            .Option("escalate", "e", Verdict.Escalate)
            .Option("deny", "d", Verdict.Deny)
            .Build();

    private static ProviderResult Yielding(Verdict verdict)
    {
        if (verdict == Verdict.Abstain)
        {
            return Answer(new ChoiceValue("allow"), Probability(("allow", 0.5), ("deny", 0.5)));
        }

        string option = verdict.ToString().ToLowerInvariant();
        string other = option == "allow" ? "deny" : "allow";
        return Answer(new ChoiceValue(option), Probability((option, 0.9), (other, 0.1)));
    }

    private static Dictionary<AttemptKey, ProviderResult> Attempts(
        params (string Rule, int Binding, ProviderResult Result)[] entries)
    {
        Dictionary<AttemptKey, ProviderResult> attempts = [];
        foreach ((string rule, int binding, ProviderResult result) in entries)
        {
            attempts[new AttemptKey(rule, binding)] = result;
        }

        return attempts;
    }

    // A Boolean answer with pTrue on `true` and the rest on `false`; the value follows the evidence.
    private static ProviderResult BooleanAnswer(double pTrue) =>
        Answer(new BooleanValue(pTrue >= 0.5), Probability(("true", pTrue), ("false", 1 - pTrue)));

    private static ProviderResult Failed(FailureKind kind) =>
        ProviderResult.Failed(DecisionType.Boolean, kind, "no answer", _provider);

    private static ProviderResult Answer(DecisionValue value, params Evidence[] evidence)
    {
        DecisionType type = value switch
        {
            BooleanValue => DecisionType.Boolean,
            ChoiceValue => DecisionType.Choice,
            _ => DecisionType.Score,
        };
        return new ProviderResult(type, ProviderOutcome.Success, value, evidence, _provider);
    }

    private static Evidence Probability(params (string Key, double Value)[] values) =>
        Of(EvidenceKind.Probability, values);

    private static Evidence Of(EvidenceKind kind, params (string Key, double Value)[] values) =>
        new(kind, values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}
