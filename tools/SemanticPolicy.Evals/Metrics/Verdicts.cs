using System.Text.Json;
using SemanticPolicy.Evals.Counting;

namespace SemanticPolicy.Evals.Metrics;

/// <summary>How often each verdict was reached, over every row of a selection.</summary>
public static class Verdicts
{
    /// <summary>
    /// The verdicts of every row, keyed by name in ascending severity. Failed and abstained rows are in
    /// here with the rest: a distribution that counted only the classified ones would not add up to the
    /// selection, which is the first thing a reader checks. The number counted is the rule's own verdict,
    /// never the policy's effective one, so a Shadow run reports what the rule concluded.
    /// </summary>
    /// <param name="rows">The bucketed rows of the selection.</param>
    public static IReadOnlyDictionary<string, int> Distribution(IReadOnlyList<RowOutcome> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Dictionary<Verdict, int> counts = [];
        foreach (RowOutcome row in rows)
        {
            Verdict verdict = row.Row.Verdict.Verdict;
            counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
        }

        Dictionary<string, int> named = new(counts.Count, StringComparer.Ordinal);
        foreach (Verdict verdict in counts.Keys.Order())
        {
            named[Names.Camel(verdict)] = counts[verdict];
        }

        return named;
    }
}

// A verdict, kind or class is a dictionary key in the JSON result, and the library writes enum values in
// camel case; a key spelled any other way would not match the enum the same document carries elsewhere.
internal static class Names
{
    internal static string Camel(Enum value) => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
