using System.Globalization;
using SemanticPolicy.Evals.Gating;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Output;

/// <summary>
/// Prints a result as the text report. It reads the result and nothing else, which is what makes the report
/// of a run and the report of its recording the same text. Rates carry three decimals, thresholds up to
/// four, latency one, usage its own precision; a rate with nothing to divide by is <c>n/a</c>, never 0.
/// </summary>
public static class ReportRenderer
{
    // The rates of a matrix that carry an interval, in the order the rate tables print them; F1 has none.
    internal static readonly string[] IntervalHeaders = ["accuracy", "precision", "recall", "fpr", "fnr"];

    private static readonly CultureInfo _invariant = CultureInfo.InvariantCulture;

    /// <summary>Writes the report: header, rows, then every measured section and the notes.</summary>
    /// <param name="result">The result to print.</param>
    /// <param name="output">Where the report goes; nothing else is written there.</param>
    public static void Write(EvalsResult result, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(output);
        Header(result, output);
        Rows(result.Rows, output);
        if (result.Report is not { } report)
        {
            return;
        }

        Outcomes(report.Outcomes, output);
        VerdictTable(report.Verdicts, output);
        foreach (RungMetrics rung in report.Rungs ?? [])
        {
            RungTable(rung, report.Outcomes, output);
        }

        if (report.Classes is { } classes)
        {
            ClassTable(classes, report.Outcomes, output);
        }

        DiscriminationSection(report, result.DecisionType, output);
        CalibrationSection(report.Calibration, result.DecisionType, output);
        Providers(report.Providers, output);
        output.WriteLine();
        output.WriteLine("notes");
        foreach (string note in report.Notes)
        {
            output.WriteLine($"- {note}");
        }

        if (result.Requirements is { } requirements)
        {
            Requirements(requirements, output);
        }
    }

    private static void Header(EvalsResult result, TextWriter output)
    {
        output.WriteLine("SemanticPolicy evals report");
        output.WriteLine($"policy {result.PolicyId}, mode {Names.Camel(result.Mode)}");
        output.WriteLine($"rule {result.RuleId}, {Names.Camel(result.DecisionType)}");
        output.WriteLine($"recording {result.RecordingPath ?? "n/a"}");
        output.WriteLine($"tool version {result.ToolVersion}");
        output.WriteLine("Measured on this dataset only: a verdict is an estimate and can be wrong in either direction.");
    }

    private static void Rows(RowSelection rows, TextWriter output)
    {
        string filters = rows.Filters.Count == 0 ? "no filter" : string.Join(", ", rows.Filters);
        string split = rows.SplitSource switch
        {
            "metadata" => "split from metadata.split",
            "files" => "split from --tune and --test",
            _ => "no split",
        };
        int unassigned = rows.AfterFilter - rows.TuneRows - rows.TestRows;
        output.WriteLine();
        output.WriteLine("rows");
        output.WriteLine($"{Count(rows.RecordedRows)} of {Count(rows.DatasetRows)} recorded rows");
        output.WriteLine($"{Count(rows.AfterFilter)} of {Count(rows.RecordedRows)} passed the filter ({filters})");
        output.WriteLine(
            $"{split}: {Count(rows.TuneRows)} tune, {Count(rows.TestRows)} test, {Count(unassigned)} unassigned");
        if (rows.TornLine is { } line)
        {
            output.WriteLine(TornLineNote(line));
        }
    }

    // Shared with sweep and compare, which print their rows section in a form of their own.
    internal static string TornLineNote(int line) =>
        $"line {Count(line)} of the recording skipped: not valid JSON, as a run cut short leaves its last line; "
        + "its row counts as not recorded";

    private static void Outcomes(OutcomeCounts outcomes, TextWriter output)
    {
        List<string> kinds = [.. outcomes.FailedByKind.Select(kind => $"{kind.Key} {Count(kind.Value)}")];
        int withoutKind = outcomes.Failed - outcomes.FailedByKind.Values.Sum();
        if (withoutKind > 0)
        {
            kinds.Add($"no kind {Count(withoutKind)}");
        }

        output.WriteLine();
        output.WriteLine("outcomes");
        output.WriteLine($"classified {Count(outcomes.Classified)}");
        output.WriteLine(
            $"failed {Count(outcomes.Failed)}{Parenthesized(kinds)}, failure rate "
            + RateAndBounds(outcomes.FailureRate, outcomes.FailureRateInterval));
        output.WriteLine(
            $"abstained {Count(outcomes.Abstained)}, abstention rate "
            + RateAndBounds(outcomes.AbstentionRate, outcomes.AbstentionRateInterval));
        output.WriteLine(
            $"ambiguous {Count(outcomes.Ambiguous)}{Parenthesized([.. outcomes.AmbiguousVerdicts.Select(verdict => $"{verdict.Key} {Count(verdict.Value)}")])}");
    }

    private static void VerdictTable(IReadOnlyDictionary<string, int> verdicts, TextWriter output)
    {
        TextTable table = new("verdict", "rows");
        foreach ((string verdict, int rows) in verdicts)
        {
            table.AddRow(verdict, Count(rows));
        }

        output.WriteLine();
        output.WriteLine("verdicts: the rule's own verdict on every row");
        table.Write(output);
    }

    private static void RungTable(RungMetrics rung, OutcomeCounts outcomes, TextWriter output)
    {
        string name = Names.Camel(rung.Rung);
        BinaryConfusion matrix = rung.Matrix;
        TextTable table = new(
            "tp", "fp", "tn", "fn", "accuracy", "precision", "recall", "f1", "fpr", "fnr", "failed", "abstained", "ambiguous");
        table.AddRow(
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
            Count(outcomes.Failed),
            Count(outcomes.Abstained),
            Count(outcomes.Ambiguous));
        output.WriteLine();
        output.WriteLine($"rung {name}: verdict at or above {name} against the flagged label, classified rows only");
        table.Write(output);

        // A table of its own rather than a second row or a bracket beside each value: either would take the rung
        // table past 120 characters.
        TextTable intervals = new(IntervalHeaders);
        intervals.AddRow(IntervalCells(matrix));
        WriteIntervals(intervals, output);
    }

    private static void ClassTable(MulticlassConfusion classes, OutcomeCounts outcomes, TextWriter output)
    {
        TextTable table = new(["label", .. classes.Classes, "support", "precision", "recall", "f1"]);
        foreach (ClassMetrics metrics in classes.PerClass)
        {
            IReadOnlyDictionary<string, int> answered = classes.Counts[metrics.Class];
            table.AddRow(
            [
                metrics.Class,
                .. classes.Classes.Select(answer => Count(answered[answer])),
                Count(metrics.Support),
                Rate(metrics.Precision),
                Rate(metrics.Recall),
                Rate(metrics.F1),
            ]);
        }

        output.WriteLine();
        output.WriteLine(
            $"classes: accuracy {RateAndBounds(classes.Accuracy, classes.AccuracyInterval)}, macro-F1 {Rate(classes.MacroF1)}; "
            + "rows are labels, columns are answers");
        table.Write(output);
        output.WriteLine(
            $"outside the table: failed {Count(outcomes.Failed)}, abstained {Count(outcomes.Abstained)}, ambiguous {Count(outcomes.Ambiguous)}");
    }

    private static void DiscriminationSection(ReportSection report, DecisionType type, TextWriter output)
    {
        output.WriteLine();
        if (report.Discrimination is not { } rungs)
        {
            output.WriteLine($"discrimination: not applicable to a {Names.Camel(type)} rule, which has no threshold to sweep");
            return;
        }

        TextTable table = new("rung", "roc-auc", "pr-auc", "rows");
        foreach (RungDiscrimination rung in rungs)
        {
            table.AddRow(
                Names.Camel(rung.Rung),
                Rate(rung.Values.RocAuc),
                Rate(rung.Values.PrAuc),
                Count(rung.Values.Rows));
        }

        output.WriteLine(
            $"discrimination: threshold swept on binding 0 '{report.SweptProvider}', every other binding at its file thresholds");
        table.Write(output);
        output.WriteLine("Failed and abstained rows are excluded from ROC-AUC and PR-AUC.");
    }

    private static void CalibrationSection(Calibration calibration, DecisionType type, TextWriter output)
    {
        output.WriteLine();
        if (!calibration.Applicable)
        {
            output.WriteLine($"calibration: not applicable: {NotApplicableReason(calibration, type)}");
            return;
        }

        TextTable table = new("bin", "rows", "predicted", "observed");
        foreach (ReliabilityBin bin in calibration.Bins)
        {
            table.AddRow(
                $"{Cut(bin.Lower)}-{Cut(bin.Upper)}",
                Count(bin.Count),
                Rate(bin.MeanPrediction),
                Rate(bin.ObservedFrequency));
        }

        output.WriteLine(
            $"calibration: ECE {Rate(calibration.Ece)}, Brier {Rate(calibration.Brier)}, n {Count(calibration.Rows)}");
        table.Write(output);
        if (calibration.KindsFound.Count > 0)
        {
            output.WriteLine(
                $"Rows whose deciding evidence is {string.Join(", ", calibration.KindsFound)} are excluded.");
        }
    }

    // Why a calibration that does not apply was not measured, in the words of the report's section.
    internal static string NotApplicableReason(Calibration calibration, DecisionType type) =>
        type == DecisionType.Score
            ? "a score rule is not calibrated in this release"
            : calibration.KindsFound.Count == 0
                ? "no classified row carried evidence"
                : $"deciding evidence is {string.Join(", ", calibration.KindsFound)}";

    private static void Providers(IReadOnlyList<ProviderStats> providers, TextWriter output)
    {
        string[] fields = [.. providers.SelectMany(provider => provider.Usage.Keys).Distinct().Order(StringComparer.Ordinal)];
        TextTable table = new(["provider", "model", "attempts", "retried", "p50 ms", "p95 ms", .. fields]);
        foreach (ProviderStats provider in providers)
        {
            table.AddRow(
            [
                provider.Provider,
                provider.Model ?? "n/a",
                Count(provider.Attempts),
                Count(provider.Retried),
                Latency(provider.LatencyP50Ms),
                Latency(provider.LatencyP95Ms),
                .. fields.Select(field => provider.Usage.TryGetValue(field, out double sum) ? Usage(sum) : "n/a"),
            ]);
        }

        output.WriteLine();
        output.WriteLine("providers: every recorded attempt of the rule");
        table.Write(output);
        output.WriteLine("Usage is summed by field name as each provider reports it, without interpretation.");
    }

    // Last, so the report above reads the same with and without them. Each line quotes the requirement as it was typed,
    // and the warning names the bound that misses the goal.
    private static void Requirements(IReadOnlyList<RequirementResult> requirements, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("requirements: the value decides, and a bound of its interval that misses the goal warns");
        foreach (RequirementResult requirement in requirements)
        {
            string counts = requirement.Counts is { } fraction ? $" ({Count(fraction.Successes)}/{Count(fraction.Trials)})" : string.Empty;
            string interval = requirement.Interval is { } bounds ? $" {Bounds(bounds)}" : string.Empty;
            string warning = !requirement.Warned || requirement.Interval is not { } missed
                ? string.Empty
                : requirement.Minimum
                    ? $"; warning: lower bound {Rate(missed.Lower)} is below the goal"
                    : $"; warning: upper bound {Rate(missed.Upper)} is above the goal";
            output.WriteLine(
                $"{requirement.Requirement}: {(requirement.Passed ? "passed" : "failed")} at {Rate(requirement.Value)}{counts}{interval}{warning}");
        }
    }

    // Shared with sweep and compare: a table of intervals goes under the table of the rates it belongs to.
    internal static void WriteIntervals(TextTable intervals, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("95% Wilson intervals");
        intervals.Write(output);
    }

    // Shared with sweep and compare. Both bounds to three places, as the rate they belong to is printed, and n/a
    // where that rate has nothing to divide by.
    internal static string Bounds(Interval? interval) =>
        interval is { } bounds
            ? $"[{bounds.Lower.ToString("0.000", _invariant)}, {bounds.Upper.ToString("0.000", _invariant)}]"
            : "n/a";

    // A rate and its interval on a line rather than in a table, where an undefined rate reads n/a once, not twice.
    internal static string RateAndBounds(double? rate, Interval? interval) =>
        interval is null ? Rate(rate) : $"{Rate(rate)} {Bounds(interval)}";

    // One cell per entry of IntervalHeaders.
    internal static string[] IntervalCells(BinaryConfusion matrix) =>
    [
        Bounds(matrix.AccuracyInterval),
        Bounds(matrix.PrecisionInterval),
        Bounds(matrix.RecallInterval),
        Bounds(matrix.FalsePositiveRateInterval),
        Bounds(matrix.FalseNegativeRateInterval),
    ];

    private static string Parenthesized(IReadOnlyList<string> parts) =>
        parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";

    private static string Count(int value) => value.ToString(_invariant);

    private static string Rate(double? value) => value is { } rate ? rate.ToString("0.000", _invariant) : "n/a";

    private static string Cut(double value) => value.ToString("0.####", _invariant);

    private static string Latency(double? value) => value is { } ms ? ms.ToString("0.0", _invariant) : "n/a";

    private static string Usage(double value) => value.ToString("G", _invariant);
}
