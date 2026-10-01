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

    private static readonly string[] _outcomeIntervalHeaders = ["abstention", "failure"];

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
                table.AddRow([Rounded(point.Threshold), point.Observed ? "observed" : "grid", .. MatrixCells(point.Matrix)]);
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
            TextTable intervals = new(["rung", .. ReportRenderer.IntervalHeaders]);
            foreach (SweptRung rung in tested)
            {
                intervals.AddRow([Names.Camel(rung.Rung), .. ReportRenderer.IntervalCells(rung.Test!.Matrix)]);
            }

            ReportRenderer.WriteIntervals(intervals, writer);
        }

        if (section.Gate?.Test is { } test)
        {
            writer.WriteLine();
            writer.WriteLine(
                $"gate on {section.Gate.ReportedOn}: {Gate(test.Below)}, {PassedOn(test)}{Count(test.Abstained)} abstained "
                + $"(abstention rate {ReportRenderer.RateAndBounds(test.AbstentionRate, test.AbstentionRateInterval)}), "
                + $"{Count(test.Decided)} decided, accuracy {ReportRenderer.RateAndBounds(test.Accuracy, test.AccuracyInterval)}");
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

            // No wider than the value table above, which fits: per rung it has five columns where that one has nine, and
            // two where that one has four and the usage fields.
            TextTable intervals = new(
            [
                "provider",
                .. rungs.SelectMany(rung => ReportRenderer.IntervalHeaders.Select(header => $"{Names.Camel(rung)} {header}")),
                .. _outcomeIntervalHeaders,
            ]);
            foreach (CompareEntry entry in section.Bindings)
            {
                intervals.AddRow(
                    [entry.Sweep.Provider, .. rungs.SelectMany(rung => RungIntervalCells(entry, rung)), .. OutcomeIntervalCells(entry)]);
            }

            ReportRenderer.WriteIntervals(intervals, writer);
        }
        else
        {
            foreach (Verdict rung in rungs)
            {
                writer.WriteLine($"{Names.Camel(rung)} on {section.Split.ReportedOn}");
                TextTable table = new(["provider", .. _rateHeaders]);
                TextTable intervals = new(["provider", .. ReportRenderer.IntervalHeaders]);
                foreach (CompareEntry entry in section.Bindings)
                {
                    table.AddRow([entry.Sweep.Provider, .. RungCells(entry, rung)]);
                    intervals.AddRow([entry.Sweep.Provider, .. RungIntervalCells(entry, rung)]);
                }

                table.Write(writer);
                ReportRenderer.WriteIntervals(intervals, writer);
                writer.WriteLine();
            }

            writer.WriteLine($"outcomes, latency and usage on {section.Split.ReportedOn}");
            TextTable summary = new(["provider", .. summaryHeaders]);
            TextTable outcomeIntervals = new(["provider", .. _outcomeIntervalHeaders]);
            foreach (CompareEntry entry in section.Bindings)
            {
                summary.AddRow([entry.Sweep.Provider, .. SummaryCells(entry, usage)]);
                outcomeIntervals.AddRow([entry.Sweep.Provider, .. OutcomeIntervalCells(entry)]);
            }

            summary.Write(writer);
            ReportRenderer.WriteIntervals(outcomeIntervals, writer);
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

    internal static void WriteRows(TextWriter writer, RowSelection rows)
    {
        string filters = rows.Filters.Count == 0 ? string.Empty : $" ({string.Join(", ", rows.Filters)})";
        writer.WriteLine(
            $"rows: {rows.DatasetRows} in the dataset, {rows.RecordedRows} recorded, {rows.AfterFilter} after the filters{filters}");
        if (rows.TornLine is { } line)
        {
            writer.WriteLine(ReportRenderer.TornLineNote(line));
        }
    }

    private static string NoGateCurve(EvalsResult result, SweepSection section) =>
        $"no gate curve for binding '{section.Provider}': it carries no margin gate for rule '{result.RuleId}' in the "
        + "policy file, and that gate's evidence kind is what a margin would be read on";

    private static void WriteGateCurve(TextWriter writer, string provider, SweptGate gate)
    {
        writer.WriteLine($"gate curve for binding '{provider}', margin on {Names.Camel(gate.Kind)}, on {gate.ChosenOn}");

        // With no later binding there is nowhere to pass a row on to, and a column of zeros would suggest there was.
        string[] passedOn = gate.Curve.Points.Any(point => point.PassedOn.HasValue) ? ["passed on"] : [];
        TextTable table = new(["gate", .. passedOn, "abstained", "abstention rate", "decided", "accuracy"]);
        foreach (GatePoint point in gate.Curve.Points)
        {
            string[] passed = point.PassedOn is { } count ? [Count(count)] : [];
            table.AddRow(
            [
                point.Below is { } below ? Rounded(below) : "none",
                .. passed,
                Count(point.Abstained),
                Rate(point.AbstentionRate),
                Count(point.Decided),
                Rate(point.Accuracy),
            ]);
        }

        table.Write(writer);
    }

    private static void WriteRecommendationLines(TextWriter writer, SweepSection section)
    {
        foreach (SweptRung rung in section.Rungs)
        {
            writer.WriteLine(RungLine(rung, section.Split));
        }

        foreach ((SweptRung lower, SweptRung higher) in OperatingPointSweep.Conflicts(section.Rungs))
        {
            string low = Names.Camel(lower.Rung);
            string high = Names.Camel(higher.Rung);
            double lowAt = lower.Recommendation.Threshold!.Value;
            double highAt = higher.Recommendation.Threshold!.Value;
            writer.WriteLine(
                $"conflict: {low} {Number(lowAt)} is not below {high} {Number(highAt)}, so any row that crosses {low} "
                + $"crosses {high} too and {low} is never reached; the library refuses thresholds that do not increase "
                + "with severity, so the pair cannot go into the policy file as printed");
            string covered = Covers(lower, highAt)
                ? $"at {Number(highAt)} {low} would meet "
                    + string.Join(", ", lower.Recommendation.Constraints.Select(constraint => constraint.ToString()))
                    + $" too, so {high} alone already does what {low} was asked to; "
                : string.Empty;
            writer.WriteLine(
                $"  {covered}set {low} below {Number(highAt)} or {high} above {Number(lowAt)} by hand, reading on the "
                + "curves what each would flag, or change a goal");
        }

        if (section.Gate is { } gate)
        {
            writer.WriteLine(GateLine(gate, section.Split));
        }

        WritePasses(writer, section);
    }

    // A rung or gate whose goals cannot be met picks nothing and keeps the number its pass was swept at, so the
    // passes can settle on that number, or stop on it, without it being a pick.
    private static void WritePasses(TextWriter writer, SweepSection section)
    {
        SweepPasses passes = section.Passes;
        string count = passes.Count == 1 ? "1 pass" : $"{Count(passes.Count)} passes";
        switch (passes.End)
        {
            case PassesEnd.Settled when passes.Count == 1:
                writer.WriteLine(section.Feasible
                    ? "settled in 1 pass: the policy file already holds these picks"
                    : "settled in 1 pass with a goal unmet: what is infeasible above keeps the policy file's number, "
                        + "and the file already holds every pick");
                return;
            case PassesEnd.Settled:
                writer.WriteLine(section.Feasible
                    ? $"settled in {count}: each pass was swept at the picks of the one before, and the last one picked "
                        + "what it was swept at"
                    : $"settled in {count} with a goal unmet: each pass was swept at the picks of the one before, the last "
                        + "one picked what it was swept at, and what is infeasible above keeps the number it was swept at");
                return;
            case PassesEnd.Conflict:
                // With no pair named above, the pair that does not increase takes in an infeasible rung's kept number.
                string held = OperatingPointSweep.Conflicts(section.Rungs).Count > 0
                    ? "the thresholds picked do not increase"
                    : "the thresholds picked and the number an infeasible rung keeps do not increase";
                writer.WriteLine(
                    $"stopped after {count}: {held} with severity, so no policy can hold them for another pass, and the "
                    + "gate is measured at the ones the pass started from");
                return;
            case PassesEnd.Alternating:
                writer.WriteLine($"no fixed point after {count}: successive passes alternate between these two sets of picks");
                break;
            default:
                writer.WriteLine(
                    $"no fixed point after {count}: the picks were still changing, the last pass from the first set below "
                    + "to the second");
                break;
        }

        writer.WriteLine($"  {Picks(passes.SweptAt!)}");
        writer.WriteLine($"  {Picks(passes.Picked!)}");
        writer.WriteLine("  neither is recommended: put each in the policy file and run report to see what it does");
    }

    private static string Picks(RuleOperatingPoint point) =>
        string.Join(
            ", ",
            [.. point.Thresholds.Select(threshold => $"{Names.Camel(threshold.Verdict)} {Number(threshold.AtOrAbove)}"), Gate(point.Gate?.Below)]);

    // Every rung's curve is the same table, each one replayed on a single-rung ladder, so a higher rung's threshold
    // is a point on the lower rung's curve too. Where the lower rung's goals hold at that point, the higher rung on
    // its own already flags the rows the lower one was asked to.
    private static bool Covers(SweptRung lower, double threshold) =>
        lower.Recommendation.Swept
        && lower.Curve.Points.FirstOrDefault(point => point.Threshold == threshold) is { } point
        && ThresholdSweep.Meets(point.Matrix, lower.Recommendation.Constraints);

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
            string met = recommendation.Chosen is { } point ? $" ({ConstrainedRates(recommendation, point.Matrix)})" : string.Empty;
            return $"{name}: {constraints} → threshold {Number(chosen)}{met}, {split.Sentence}";
        }

        if (recommendation.Nearest is not { } nearest)
        {
            return $"{name}: {constraints} → infeasible, and no threshold comes near: a constrained rate is undefined at "
                + $"every point, {split.Sentence}";
        }

        return $"{name}: {constraints} → infeasible, nearest threshold {Number(nearest.Threshold)} "
            + $"({ConstrainedRates(recommendation, nearest.Matrix)}), {split.Sentence}";
    }

    // Each rate the rung's constraints name, once, in the order first named, with its interval: whether a goal met on
    // forty rows would still be met on the next forty is the interval's to say.
    private static string ConstrainedRates(RungRecommendation recommendation, BinaryConfusion matrix) =>
        string.Join(
            ", ",
            recommendation.Constraints.Select(constraint => constraint.Kind).Distinct().Select(kind => kind switch
            {
                ConstraintKind.MinRecall => $"recall {ReportRenderer.RateAndBounds(matrix.Recall, matrix.RecallInterval)}",
                ConstraintKind.MaxFpr =>
                    $"fpr {ReportRenderer.RateAndBounds(matrix.FalsePositiveRate, matrix.FalsePositiveRateInterval)}",
                _ => $"precision {ReportRenderer.RateAndBounds(matrix.Precision, matrix.PrecisionInterval)}",
            }));

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

    private static string[] RungIntervalCells(CompareEntry entry, Verdict rung) =>
        entry.Sweep.Rungs.FirstOrDefault(candidate => candidate.Rung == rung)?.Test is { } test
            ? ReportRenderer.IntervalCells(test.Matrix)
            : [.. Enumerable.Repeat("n/a", ReportRenderer.IntervalHeaders.Length)];

    private static string[] OutcomeIntervalCells(CompareEntry entry) =>
    [
        ReportRenderer.Bounds(entry.Outcomes.AbstentionRateInterval),
        ReportRenderer.Bounds(entry.Outcomes.FailureRateInterval),
    ];

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
        $"({PassedOn(point)}abstention rate {ReportRenderer.RateAndBounds(point.AbstentionRate, point.AbstentionRateInterval)}, "
        + $"accuracy {ReportRenderer.RateAndBounds(point.Accuracy, point.AccuracyInterval)})";

    // A gate on a binding with a later one is cheap in abstentions and paid for in the rows that binding decides
    // instead, so a line that shows the one shows the other.
    private static string PassedOn(GatePoint point) =>
        point.PassedOn is { } count ? $"{Count(count)} passed on to the next binding, " : string.Empty;

    private static string Gate(double? below) => below is { } value ? $"gate {Number(value)}" : "no gate";

    // A rate to three places, or n/a where its denominator is zero: an undefined rate printed as 0 would read as
    // a measured one.
    private static string Rate(double? value) =>
        value is { } rate ? rate.ToString("0.000", CultureInfo.InvariantCulture) : "n/a";

    // A threshold or a gate someone may copy into a policy file, exactly. Core compares with >= and <, so a rounded
    // number can flag or gate different rows from the point that was measured; a margin's noise such as
    // 0.30000000000000004 is the price of that.
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // A curve table's threshold or gate column to at most four places: it shows the curve's shape, not a number
    // to copy.
    private static string Rounded(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Latency(double? milliseconds) =>
        milliseconds is { } value ? value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
