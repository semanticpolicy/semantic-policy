using System.Globalization;
using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Evals.Sweeping;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Output;

/// <summary>
/// The text <c>sweep</c> and <c>compare</c> print. Every number is shown; a recommendation line appears only when a
/// constraint was given, and every one of them names the rows it was chosen on and the rows it is reported on.
/// </summary>
public static class SweepRenderer
{
    private const int _maxWidth = 120;

    private static readonly string[] _matrixHeaders =
        ["TP", "FP", "TN", "FN", "accuracy", "precision", "recall", "f1", "fpr", "fnr"];

    private static readonly string[] _rateHeaders = ["threshold", "accuracy", "precision", "recall", "f1", "fpr", "fnr", "roc-auc", "pr-auc"];

    /// <summary>Writes a <c>sweep</c> result: every curve, then what was recommended and how it did on the test rows.</summary>
    /// <param name="writer">Where the text goes.</param>
    /// <param name="result">The result envelope, for the rule and the row counts.</param>
    /// <param name="section">The swept binding.</param>
    public static void WriteSweep(TextWriter writer, EvalsResult result, SweepSection section)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(section);
        writer.WriteLine($"sweep of rule '{result.RuleId}' on binding '{section.Provider}', policy '{result.PolicyId}'");
        WriteRows(writer, result.Rows);
        writer.WriteLine($"curves on {section.Split.ChosenOn}");
        if (section.Rungs.Count == 0)
        {
            writer.WriteLine(
                $"rule '{result.RuleId}' is a {Names.Camel(result.DecisionType)} rule, with no ladder to cut; only its gate is swept");
        }

        foreach (SweptRung rung in section.Rungs)
        {
            writer.WriteLine();
            writer.WriteLine($"{Names.Camel(rung.Rung)} curve on {rung.ChosenOn}");
            TextTable table = new(["threshold", "point", .. _matrixHeaders]);
            foreach (CurvePoint point in rung.Curve.Points)
            {
                table.AddRow([Number(point.Threshold), point.Observed ? "observed" : "grid", .. MatrixCells(point.Matrix)]);
            }

            table.Write(writer);
        }

        writer.WriteLine();
        if (section.Gate is { } gate)
        {
            WriteGateCurve(writer, section.Provider, gate);
        }
        else
        {
            writer.WriteLine(NoGateCurve(result, section));
        }

        if (!Constrained(section))
        {
            return;
        }

        writer.WriteLine();
        WriteRecommendationLines(writer, section);
        SweptRung[] tested = [.. section.Rungs.Where(rung => rung.Test is not null)];
        if (tested.Length > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"rates at these thresholds on {section.Split.ReportedOn}");
            TextTable table = new(["rung", "threshold", .. _matrixHeaders]);
            foreach (SweptRung rung in tested)
            {
                table.AddRow([Names.Camel(rung.Rung), Number(rung.Test!.Threshold), .. MatrixCells(rung.Test.Matrix)]);
            }

            table.Write(writer);
        }

        if (section.Gate?.Test is { } test)
        {
            writer.WriteLine();
            writer.WriteLine(
                $"gate on {section.Gate.ReportedOn}: {Gate(test.Below)}, {Count(test.Abstained)} abstained (abstention "
                + $"rate {Rate(test.AbstentionRate)}), {Count(test.Decided)} decided, accuracy {Rate(test.Accuracy)}");
        }
    }

    /// <summary>
    /// Writes a <c>compare</c> result: one row per binding with its numbers on the test rows, then each binding's
    /// recommendation lines. When that row would run past 120 characters it is laid out as one table per rung and
    /// one for the rest, each with a row per binding in the same order.
    /// </summary>
    /// <param name="writer">Where the text goes.</param>
    /// <param name="result">The result envelope, for the rule and the row counts.</param>
    /// <param name="section">The compared bindings.</param>
    public static void WriteCompare(TextWriter writer, EvalsResult result, CompareSection section)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(section);
        string names = string.Join(", ", section.Bindings.Select(entry => $"'{entry.Sweep.Provider}'"));
        writer.WriteLine(
            $"compare of rule '{result.RuleId}', policy '{result.PolicyId}': "
            + (section.Bindings.Count == 1 ? $"binding {names}, alone" : $"bindings {names}, each alone"));
        WriteRows(writer, result.Rows);
        bool constrained = section.Bindings.Any(entry => Constrained(entry.Sweep));
        writer.WriteLine(constrained
            ? section.Split.Sentence
            : $"at the policy file's thresholds and gates, reported on {section.Split.ReportedOn}");

        Verdict[] rungs = [.. section.Bindings.SelectMany(entry => entry.Sweep.Rungs.Select(rung => rung.Rung)).Distinct()];
        string[] usage = [.. section.Bindings.SelectMany(entry => entry.Provider.Usage.Keys).Distinct(StringComparer.Ordinal)];
        string[] summaryHeaders = ["abstention", "failure", "p50 ms", "p95 ms", .. usage];

        TextTable combined = new(
        [
            "provider",
            .. rungs.SelectMany(rung => _rateHeaders.Select(header => $"{Names.Camel(rung)} {header}")),
            .. summaryHeaders,
        ]);
        foreach (CompareEntry entry in section.Bindings)
        {
            combined.AddRow([entry.Sweep.Provider, .. rungs.SelectMany(rung => RungCells(entry, rung)), .. SummaryCells(entry, usage)]);
        }

        using StringWriter wide = new(CultureInfo.InvariantCulture);
        combined.Write(wide);
        string[] lines = wide.ToString().Split(wide.NewLine, StringSplitOptions.RemoveEmptyEntries);
        writer.WriteLine();
        if (lines.Max(line => line.Length) <= _maxWidth)
        {
            writer.WriteLine($"each binding on {section.Split.ReportedOn}");
            foreach (string line in lines)
            {
                writer.WriteLine(line);
            }
        }
        else
        {
            foreach (Verdict rung in rungs)
            {
                writer.WriteLine($"{Names.Camel(rung)} on {section.Split.ReportedOn}");
                TextTable table = new(["provider", .. _rateHeaders]);
                foreach (CompareEntry entry in section.Bindings)
                {
                    table.AddRow([entry.Sweep.Provider, .. RungCells(entry, rung)]);
                }

                table.Write(writer);
                writer.WriteLine();
            }

            writer.WriteLine($"outcomes, latency and usage on {section.Split.ReportedOn}");
            TextTable summary = new(["provider", .. summaryHeaders]);
            foreach (CompareEntry entry in section.Bindings)
            {
                summary.AddRow([entry.Sweep.Provider, .. SummaryCells(entry, usage)]);
            }

            summary.Write(writer);
        }

        foreach (CompareEntry entry in section.Bindings)
        {
            if (entry.Sweep.Gate is null)
            {
                writer.WriteLine();
                writer.WriteLine(NoGateCurve(result, entry.Sweep));
            }

            if (constrained)
            {
                writer.WriteLine();
                writer.WriteLine($"binding '{entry.Sweep.Provider}'");
                WriteRecommendationLines(writer, entry.Sweep);
            }
        }
    }

    private static bool Constrained(SweepSection section) =>
        section.Rungs.Any(rung => rung.Recommendation.Swept) || section.Gate?.Recommendation.Swept == true;

    private static void WriteRows(TextWriter writer, RowSelection rows)
    {
        string filters = rows.Filters.Count == 0 ? string.Empty : $" ({string.Join(", ", rows.Filters)})";
        writer.WriteLine(
            $"rows: {rows.DatasetRows} in the dataset, {rows.RecordedRows} recorded, {rows.AfterFilter} after the filters{filters}");
    }

    private static string NoGateCurve(EvalsResult result, SweepSection section) =>
        $"no gate curve for binding '{section.Provider}': it carries no margin gate for rule '{result.RuleId}' in the "
        + "policy file, and that gate's evidence kind is what a margin would be read on";

    private static void WriteGateCurve(TextWriter writer, string provider, SweptGate gate)
    {
        writer.WriteLine($"gate curve for binding '{provider}', margin on {Names.Camel(gate.Kind)}, on {gate.ChosenOn}");
        TextTable table = new("gate", "abstained", "abstention rate", "decided", "accuracy");
        foreach (GatePoint point in gate.Curve.Points)
        {
            table.AddRow(
                point.Below is { } below ? Number(below) : "none",
                Count(point.Abstained),
                Rate(point.AbstentionRate),
                Count(point.Decided),
                Rate(point.Accuracy));
        }

        table.Write(writer);
    }

    private static void WriteRecommendationLines(TextWriter writer, SweepSection section)
    {
        foreach (SweptRung rung in section.Rungs)
        {
            writer.WriteLine(RungLine(rung, section.Split));
        }

        IReadOnlyList<(SweptRung Lower, SweptRung Higher)> conflicts = OperatingPointSweep.Conflicts(section.Rungs);
        if (conflicts.Count > 0)
        {
            string pairs = string.Join(
                "; ",
                conflicts.Select(pair =>
                    $"{Names.Camel(pair.Lower.Rung)} {Number(pair.Lower.Recommendation.Threshold!.Value)} is not below "
                    + $"{Names.Camel(pair.Higher.Rung)} {Number(pair.Higher.Recommendation.Threshold!.Value)}"));
            writer.WriteLine(
                $"conflict: {pairs}; thresholds must increase with severity, and these are reported as chosen, not reordered");
        }

        if (section.Gate is { } gate)
        {
            writer.WriteLine(GateLine(gate, section.Split));
        }
    }

    private static string RungLine(SweptRung rung, SplitWording split)
    {
        RungRecommendation recommendation = rung.Recommendation;
        string name = Names.Camel(rung.Rung);
        if (!recommendation.Swept)
        {
            return recommendation.Threshold is { } file
                ? $"{name}: not swept, keeps {Number(file)} from the policy file"
                : $"{name}: not swept, and the policy file has no threshold for it";
        }

        string constraints = string.Join(", ", recommendation.Constraints.Select(constraint => constraint.ToString()));
        if (recommendation.Feasible && recommendation.Threshold is { } chosen)
        {
            return $"{name}: {constraints} → threshold {Number(chosen)}, {split.Sentence}";
        }

        if (recommendation.Nearest is not { } nearest)
        {
            return $"{name}: {constraints} → infeasible, and no threshold comes near: a constrained rate is undefined at "
                + $"every point, {split.Sentence}";
        }

        string rates = string.Join(
            ", ",
            recommendation.Constraints.Select(constraint => constraint.Kind).Distinct().Select(kind => kind switch
            {
                ConstraintKind.MinRecall => $"recall {Rate(nearest.Matrix.Recall)}",
                ConstraintKind.MaxFpr => $"fpr {Rate(nearest.Matrix.FalsePositiveRate)}",
                _ => $"precision {Rate(nearest.Matrix.Precision)}",
            }));
        return $"{name}: {constraints} → infeasible, nearest threshold {Number(nearest.Threshold)} ({rates}), {split.Sentence}";
    }

    private static string GateLine(SweptGate gate, SplitWording split)
    {
        GateRecommendation recommendation = gate.Recommendation;
        if (!recommendation.Swept)
        {
            return $"gate: not swept, keeps {Gate(recommendation.Below)} from the policy file";
        }

        string constraints = string.Join(", ", recommendation.Constraints.Select(constraint => constraint.ToString()));
        if (recommendation.Feasible && recommendation.Chosen is { } chosen)
        {
            return $"gate: {constraints} → {Gate(chosen.Below)} {GateRates(chosen)}, {split.Sentence}";
        }

        return recommendation.Nearest is { } nearest
            ? $"gate: {constraints} → infeasible, nearest {Gate(nearest.Below)} {GateRates(nearest)}, {split.Sentence}"
            : $"gate: {constraints} → infeasible, and no gate comes near: nothing is decided at any of them, {split.Sentence}";
    }

    private static string[] RungCells(CompareEntry entry, Verdict rung)
    {
        SweptRung? swept = entry.Sweep.Rungs.FirstOrDefault(candidate => candidate.Rung == rung);
        Discrimination? values = entry.Discrimination?.FirstOrDefault(candidate => candidate.Rung == rung)?.Values;
        string[] rates = swept?.Test is { } test
            ? [.. MatrixCells(test.Matrix).Skip(4)]
            : [.. Enumerable.Repeat("n/a", 6)];
        string threshold = swept?.Recommendation.Threshold is { } chosen ? Number(chosen) : "infeasible";
        return [threshold, .. rates, Rate(values?.RocAuc), Rate(values?.PrAuc)];
    }

    private static string[] SummaryCells(CompareEntry entry, IEnumerable<string> usage) =>
    [
        Rate(entry.Outcomes.AbstentionRate),
        Rate(entry.Outcomes.FailureRate),
        Latency(entry.Provider.LatencyP50Ms),
        Latency(entry.Provider.LatencyP95Ms),
        .. usage.Select(field => entry.Provider.Usage.TryGetValue(field, out double sum)
            ? sum.ToString("G", CultureInfo.InvariantCulture)
            : "n/a"),
    ];

    private static string[] MatrixCells(BinaryConfusion matrix) =>
    [
        Count(matrix.TruePositives),
        Count(matrix.FalsePositives),
        Count(matrix.TrueNegatives),
        Count(matrix.FalseNegatives),
        Rate(matrix.Accuracy),
        Rate(matrix.Precision),
        Rate(matrix.Recall),
        Rate(matrix.F1),
        Rate(matrix.FalsePositiveRate),
        Rate(matrix.FalseNegativeRate),
    ];

    private static string GateRates(GatePoint point) =>
        $"(abstention rate {Rate(point.AbstentionRate)}, accuracy {Rate(point.Accuracy)})";

    private static string Gate(double? below) => below is { } value ? $"gate {Number(value)}" : "no gate";

    // A rate to three places, or n/a where its denominator is zero: an undefined rate printed as 0 would read as
    // a measured one.
    private static string Rate(double? value) =>
        value is { } rate ? rate.ToString("0.000", CultureInfo.InvariantCulture) : "n/a";

    // A threshold or a gate to at most four places. A margin is computed, so it carries noise such as
    // 0.30000000000000004 that says nothing; the JSON result keeps every number exactly.
    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Latency(double? milliseconds) =>
        milliseconds is { } value ? value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
