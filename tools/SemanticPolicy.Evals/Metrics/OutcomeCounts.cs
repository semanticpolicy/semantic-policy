using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Metrics;

/// <summary>
/// What became of the rows of a selection. A provider failure and an abstention are counted here, beside
/// the confusion matrix rather than inside it, so a report cannot present an outage as poor recall or a
/// model that declined the hard rows as a precise one. Both rates are over every row of the selection.
/// </summary>
/// <param name="Rows">Every row of the selection.</param>
/// <param name="Classified">Rows a provider's answer decided, which are the rows the matrices count.</param>
/// <param name="Failed">Rows the policy's failure behaviour decided.</param>
/// <param name="FailedByKind">
/// The failures by the kind of the outcome that ended the chain. A provider that declined rather than
/// failed carries no kind, so these need not add up to <paramref name="Failed"/>.
/// </param>
/// <param name="Abstained">Rows the chain ran out of bindings on under the margin gate.</param>
/// <param name="Ambiguous">Rows labelled <c>ambiguous</c> or <c>abstain</c>, which carry no truth to score.</param>
/// <param name="AmbiguousVerdicts">What the rule concluded on those rows, which is reported rather than scored.</param>
/// <param name="FailureRate">Failed rows over every row; <see langword="null"/> for an empty selection.</param>
/// <param name="AbstentionRate">Abstained rows over every row; <see langword="null"/> for an empty selection.</param>
public sealed record OutcomeCounts(
    int Rows,
    int Classified,
    int Failed,
    IReadOnlyDictionary<string, int> FailedByKind,
    int Abstained,
    int Ambiguous,
    IReadOnlyDictionary<string, int> AmbiguousVerdicts,
    double? FailureRate,
    double? AbstentionRate)
{
    /// <summary>Counts one selection.</summary>
    /// <param name="rows">The bucketed rows of the selection.</param>
    public static OutcomeCounts Compute(IReadOnlyList<RowOutcome> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int classified = 0;
        int failed = 0;
        int abstained = 0;
        Dictionary<FailureKind, int> kinds = [];
        List<RowOutcome> ambiguous = [];
        foreach (RowOutcome row in rows)
        {
            switch (row.Bucket)
            {
                case RowBucket.Classified:
                    classified++;
                    break;
                case RowBucket.Failed:
                    failed++;
                    if (row.FailureKind is { } kind)
                    {
                        kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                    }

                    break;
                case RowBucket.Abstained:
                    abstained++;
                    break;
                default:
                    ambiguous.Add(row);
                    break;
            }
        }

        Dictionary<string, int> failedByKind = new(kinds.Count, StringComparer.Ordinal);
        foreach (FailureKind kind in kinds.Keys.Order())
        {
            failedByKind[Names.Camel(kind)] = kinds[kind];
        }

        return new OutcomeCounts(
            rows.Count,
            classified,
            failed,
            failedByKind,
            abstained,
            ambiguous.Count,
            Verdicts.Distribution(ambiguous),
            rows.Count == 0 ? null : (double)failed / rows.Count,
            rows.Count == 0 ? null : (double)abstained / rows.Count);
    }
}
