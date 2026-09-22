using SemanticPolicy.Evals.Counting;

namespace SemanticPolicy.Evals.Metrics;

/// <summary>One class of a Choice or Score rule, read as a one-against-the-rest binary classifier.</summary>
/// <param name="Class">The option key or level.</param>
/// <param name="Support">How many classified rows are labelled this class.</param>
/// <param name="Precision">The share of rows predicted this class that are labelled it.</param>
/// <param name="Recall">The share of rows labelled this class that were predicted it.</param>
/// <param name="F1">The harmonic mean of the two, 0 for a class the classifier never predicted.</param>
public sealed record ClassMetrics(string Class, int Support, double? Precision, double? Recall, double? F1);

/// <summary>
/// The full answer-against-label table of a Choice or Score rule, with the per-class rates read off it.
/// Classes keep the rule's declared order — the options as written, the scale from lowest to highest — so
/// two runs of one rule produce tables that can be compared row by row.
/// </summary>
/// <param name="Classes">The rule's answers, in declared order.</param>
/// <param name="Counts">How often each labelled class was answered as each class: <c>[actual][predicted]</c>.</param>
/// <param name="Accuracy">The share of classified rows answered correctly.</param>
/// <param name="PerClass">The per-class rates, in the same order as <paramref name="Classes"/>.</param>
/// <param name="MacroF1">The unweighted mean F1 over the classes the data has rows for.</param>
public sealed record MulticlassConfusion(
    IReadOnlyList<string> Classes,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Counts,
    double? Accuracy,
    IReadOnlyList<ClassMetrics> PerClass,
    double? MacroF1)
{
    /// <summary>The table of a Choice rule, with the options as its classes.</summary>
    /// <param name="rows">The bucketed rows of the selection; only classified ones are counted.</param>
    /// <param name="rule">The rule whose options are the classes.</param>
    public static MulticlassConfusion Compute(IReadOnlyList<RowOutcome> rows, ChoiceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Tabulate(rows, [.. rule.Options.Select(option => option.Key)]);
    }

    /// <summary>The table of a Score rule, with the levels as its classes, lowest first.</summary>
    /// <param name="rows">The bucketed rows of the selection; only classified ones are counted.</param>
    /// <param name="rule">The rule whose levels are the classes.</param>
    public static MulticlassConfusion Compute(IReadOnlyList<RowOutcome> rows, ScoreRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Tabulate(rows, rule.Levels);
    }

    private static MulticlassConfusion Tabulate(IReadOnlyList<RowOutcome> rows, IReadOnlyList<string> classes)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Dictionary<string, int> indexOf = new(classes.Count, StringComparer.Ordinal);
        for (int index = 0; index < classes.Count; index++)
        {
            indexOf[classes[index]] = index;
        }

        int[,] cells = new int[classes.Count, classes.Count];
        int classified = 0;
        foreach (RowOutcome row in rows)
        {
            // Both sides were checked against this rule before any of it ran — the label by the dataset
            // reader, the answer by the step function — so a value outside the vocabulary cannot appear.
            if (row.Bucket != RowBucket.Classified
                || row.Row.Row.Label.Answer is not { } actual
                || row.PredictedAnswer is not { } predicted
                || !indexOf.TryGetValue(actual, out int actualIndex)
                || !indexOf.TryGetValue(predicted, out int predictedIndex))
            {
                continue;
            }

            cells[actualIndex, predictedIndex]++;
            classified++;
        }

        Dictionary<string, IReadOnlyDictionary<string, int>> counts = new(classes.Count, StringComparer.Ordinal);
        List<ClassMetrics> perClass = new(classes.Count);
        int correct = 0;
        double f1Total = 0;
        int withSupport = 0;
        for (int actual = 0; actual < classes.Count; actual++)
        {
            Dictionary<string, int> answered = new(classes.Count, StringComparer.Ordinal);
            int support = 0;
            int predicted = 0;
            for (int other = 0; other < classes.Count; other++)
            {
                answered[classes[other]] = cells[actual, other];
                support += cells[actual, other];
                predicted += cells[other, actual];
            }

            counts[classes[actual]] = answered;
            int truePositives = cells[actual, actual];
            int falsePositives = predicted - truePositives;
            int falseNegatives = support - truePositives;
            BinaryConfusion oneAgainstTheRest = new(
                truePositives,
                falsePositives,
                classified - support - falsePositives,
                falseNegatives);
            perClass.Add(new ClassMetrics(
                classes[actual],
                support,
                oneAgainstTheRest.Precision,
                oneAgainstTheRest.Recall,
                oneAgainstTheRest.F1));
            correct += truePositives;
            if (support > 0)
            {
                // Every class the data has rows for enters the average, including one the classifier never
                // predicted: its F1 is 0, and leaving it out would report a score the classifier did not earn.
                f1Total += oneAgainstTheRest.F1 ?? 0;
                withSupport++;
            }
        }

        return new MulticlassConfusion(
            classes,
            counts,
            classified == 0 ? null : (double)correct / classified,
            perClass,
            withSupport == 0 ? null : f1Total / withSupport);
    }
}
