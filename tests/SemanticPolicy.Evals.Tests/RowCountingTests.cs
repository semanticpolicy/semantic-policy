using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class RowCountingTests
{
    private static readonly Policy _boolean = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]);
    private static readonly Policy _gated =
        Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()], gate: 0.2);

    public static TheoryData<string, Policy, ProviderResult, RowBucket, FailureKind?, string?> Sources =>
        new()
        {
            { "true", _boolean, Samples.BooleanAnswer(0.95), RowBucket.Classified, null, "true" },
            { "false", _boolean, Samples.BooleanAnswer(0.2), RowBucket.Classified, null, "false" },
            {
                "option",
                Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Route()]),
                Samples.Answer(new ChoiceValue("review")),
                RowBucket.Classified,
                null,
                "review"
            },
            {
                "level",
                Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Severity()]),
                Samples.Answer(new ScoreValue("serious", 2)),
                RowBucket.Classified,
                null,
                "serious"
            },
            { "timeout", _boolean, Samples.Failed(FailureKind.Timeout), RowBucket.Failed, FailureKind.Timeout, null },
            {
                "malformed",
                _boolean,
                Samples.BooleanAnswer(0.95) with { Protocol = "semanticpolicy/v1" },
                RowBucket.Failed,
                FailureKind.Malformed,
                null
            },
            { "uncertain", _gated, Samples.BooleanAnswer(0.52), RowBucket.Abstained, null, null },
        };

    [Theory]
    [MemberData(nameof(Sources))]
    public void Counting_Puts_Each_Verdict_Source_In_Its_Bucket(
        string label,
        Policy policy,
        ProviderResult result,
        RowBucket bucket,
        FailureKind? failureKind,
        string? predicted)
    {
        EvaluatedRow row = Evaluated(policy, label, result);

        RowOutcome outcome = RowCounting.Classify(row, policy.Rules[0]);

        outcome.Row.Should().BeSameAs(row);
        outcome.Bucket.Should().Be(bucket);
        outcome.FailureKind.Should().Be(failureKind);
        outcome.PredictedAnswer.Should().Be(predicted);
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("abstain")]
    public void Counting_Puts_Ambiguous_And_Abstain_Labelled_Rows_Outside_The_Matrix_Whatever_The_Source(string label)
    {
        EvaluatedRow classified = Evaluated(_boolean, label, Samples.BooleanAnswer(0.95));
        EvaluatedRow failed = Evaluated(_boolean, label, Samples.Failed(FailureKind.Unavailable));
        EvaluatedRow abstained = Evaluated(_gated, label, Samples.BooleanAnswer(0.52));

        RowOutcome[] outcomes =
        [
            RowCounting.Classify(classified, _boolean.Rules[0]),
            RowCounting.Classify(failed, _boolean.Rules[0]),
            RowCounting.Classify(abstained, _gated.Rules[0]),
        ];

        classified.Verdict.Source.Should().Be(VerdictSource.Threshold);
        failed.Verdict.Source.Should().Be(VerdictSource.FailureBehavior);
        abstained.Verdict.Source.Should().Be(VerdictSource.UncertaintyExhausted);
        outcomes.Should().AllSatisfy(outcome =>
        {
            outcome.Bucket.Should().Be(RowBucket.Ambiguous);
            outcome.FailureKind.Should().BeNull();
            outcome.PredictedAnswer.Should().BeNull();
        });
    }

    [Fact]
    public void Counting_Reads_The_Evaluated_Verdict_So_A_Shadow_Policy_Buckets_Like_Enforce()
    {
        Policy shadow = Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()], PolicyMode.Shadow);
        ProviderResult result = Samples.BooleanAnswer(0.95);
        EvaluatedRow enforced = Evaluated(_boolean, "true", result);
        EvaluatedRow shadowed = Evaluated(shadow, "true", result);

        RowOutcome enforcedOutcome = RowCounting.Classify(enforced, _boolean.Rules[0]);
        RowOutcome shadowedOutcome = RowCounting.Classify(shadowed, shadow.Rules[0]);

        enforced.Verdict.Verdict.Should().Be(Verdict.Deny);
        shadowed.Verdict.Verdict.Should().Be(Verdict.Deny);
        shadowedOutcome.Bucket.Should().Be(enforcedOutcome.Bucket).And.Be(RowBucket.Classified);
        shadowedOutcome.PredictedAnswer.Should().Be(enforcedOutcome.PredictedAnswer).And.Be("true");
    }

    // A real verdict rather than a hand-built one: the trace a metric reads is the step function's own.
    private static EvaluatedRow Evaluated(Policy policy, string label, ProviderResult result)
    {
        Rule rule = policy.Rules[0];
        Dictionary<AttemptKey, ProviderResult> attempts = new() { [new AttemptKey(rule.Id, 0)] = result };
        RuleVerdict verdict = PolicyEvaluation.Evaluate(policy, attempts).Verdict!.Rules.Single();
        return new EvaluatedRow(Samples.Row("a", label), verdict);
    }
}
