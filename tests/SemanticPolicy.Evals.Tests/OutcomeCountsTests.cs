using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class OutcomeCountsTests
{
    // Warn at 0.6, Deny at 0.9 and a margin gate at 0.2 on one binding, so a probability inside
    // (0.4, 0.6) leaves the chain with nothing behind it and the rule abstains.
    private static readonly Policy _policy =
        Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()], gate: 0.2);

    // Five classified rows, two failures of different kinds, one abstention and two rows the annotators
    // left outside the matrix.
    private static RowOutcome[] Fixture() =>
    [
        Outcome("r01", "true", Samples.BooleanAnswer(0.95)),
        Outcome("r02", "true", Samples.BooleanAnswer(0.70)),
        Outcome("r03", "false", Samples.BooleanAnswer(0.10)),
        Outcome("r04", "false", Samples.BooleanAnswer(0.20)),
        Outcome("r05", "true", Samples.BooleanAnswer(0.92)),
        Outcome("r06", "true", Samples.Failed(FailureKind.Timeout)),
        Outcome("r07", "false", Samples.Failed(FailureKind.Unavailable)),
        Outcome("r08", "true", Samples.BooleanAnswer(0.52)),
        Outcome("r09", "ambiguous", Samples.BooleanAnswer(0.95)),
        Outcome("r10", "abstain", Samples.BooleanAnswer(0.30)),
    ];

    [Fact]
    public void Outcome_Counts_Keep_Failures_By_Kind_Abstentions_And_Ambiguous_Rows_Out_Of_The_Matrix()
    {
        RowOutcome[] rows = Fixture();

        OutcomeCounts counts = OutcomeCounts.Compute(rows);

        counts.Rows.Should().Be(10);
        counts.Classified.Should().Be(5);
        counts.Failed.Should().Be(2);
        counts.FailedByKind.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["timeout"] = 1,
            ["unavailable"] = 1,
        });
        counts.Abstained.Should().Be(1);
        counts.Ambiguous.Should().Be(2);
        counts.AmbiguousVerdicts.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["allow"] = 1,
            ["deny"] = 1,
        });

        // Both rates are over every row of the selection, not over the classified ones: two of ten failed
        // and one of ten abstained.
        counts.FailureRate.Should().BeApproximately(0.2, 1e-12);
        counts.AbstentionRate.Should().BeApproximately(0.1, 1e-12);

        // The matrix is the five classified rows and nothing else.
        rows.Count(row => row.Bucket == RowBucket.Classified).Should().Be(5);
    }

    [Fact]
    public void Verdict_Distribution_Counts_Every_Row_Including_Failed_And_Abstained_Ones()
    {
        RowOutcome[] rows = Fixture();

        IReadOnlyDictionary<string, int> distribution = Verdicts.Distribution(rows);

        // r03, r04 and r10 allow; r02 warns; r08 abstains under the gate; r01, r05 and r09 deny on the
        // ladder and r06 and r07 deny through the failure behaviour.
        distribution.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["allow"] = 3,
            ["warn"] = 1,
            ["abstain"] = 1,
            ["deny"] = 5,
        });
        distribution.Values.Sum().Should().Be(rows.Length);
    }

    private static RowOutcome Outcome(string id, string label, ProviderResult result)
    {
        Dictionary<AttemptKey, ProviderResult> attempts = new() { [new AttemptKey(Samples.Injection, 0)] = result };
        RuleVerdict verdict = PolicyEvaluation.Evaluate(_policy, attempts).Verdict!.Rules.Single();
        return RowCounting.Classify(new EvaluatedRow(Samples.Row(id, label), verdict), _policy.Rules[0]);
    }
}
