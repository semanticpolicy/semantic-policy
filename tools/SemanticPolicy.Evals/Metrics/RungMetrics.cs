using SemanticPolicy.Evals.Counting;

namespace SemanticPolicy.Evals.Metrics;

/// <summary>
/// One ladder rung read as a binary classifier: "the rule reached this rung or a more severe one" against
/// "the row is labelled the flagged answer". Each rung is its own question, and the precision of Deny —
/// the number that justifies enforcing it — is invisible in a single flagged-or-not matrix.
/// </summary>
/// <param name="Rung">The ladder rung the matrix is about.</param>
/// <param name="Matrix">The counts at that rung.</param>
public sealed record RungMetrics(Verdict Rung, BinaryConfusion Matrix)
{
    /// <summary>
    /// One entry per ladder rung, least severe first. Only classified rows are counted: a provider failure
    /// and an abstention are reported beside the matrix and never inside it, so an outage cannot read as
    /// poor recall.
    /// </summary>
    /// <param name="rows">The bucketed rows of the selection.</param>
    /// <param name="rule">The rule whose ladder is read; its flagged answer is the positive class.</param>
    public static IReadOnlyList<RungMetrics> Compute(IReadOnlyList<RowOutcome> rows, BooleanRule rule)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rule);
        string flagged = rule.FlaggedAnswer ? "true" : "false";
        List<RungMetrics> metrics = new(rule.Ladder.Count);
        foreach (Verdict rung in rule.Ladder)
        {
            int truePositives = 0;
            int falsePositives = 0;
            int trueNegatives = 0;
            int falseNegatives = 0;
            foreach (RowOutcome row in rows)
            {
                if (row.Bucket != RowBucket.Classified)
                {
                    continue;
                }

                bool predicted = row.Row.Verdict.Verdict >= rung;
                bool actual = string.Equals(row.Row.Row.Label.Answer, flagged, StringComparison.Ordinal);
                if (predicted && actual)
                {
                    truePositives++;
                }
                else if (predicted)
                {
                    falsePositives++;
                }
                else if (actual)
                {
                    falseNegatives++;
                }
                else
                {
                    trueNegatives++;
                }
            }

            metrics.Add(new RungMetrics(
                rung,
                new BinaryConfusion(truePositives, falsePositives, trueNegatives, falseNegatives)));
        }

        return metrics;
    }
}
