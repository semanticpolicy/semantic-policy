using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Calibrating;

// One row a map is fitted on: the provider's value under the flagged answer, as the operating point reads it before
// any calibration, whether the label is the flagged answer, and the model the result names, if any.
internal sealed record FittingRow(string Id, double Value, bool Flagged, string? Model);

// The steps of a fit that are not the minimiser: which rows it runs on, what the value goes through first, which
// model it is said to be fitted for, and the operating point rewritten around the fitted map so that every row it
// was replayed on keeps its verdict.
internal static class CalibrationFit
{
    // Fewer rows of either label than this and the fitted map says more about those few rows than about the provider.
    public const int MinimumRows = 10;

    // The tune rows labelled with an answer whose attempt at the binding succeeded with the evidence the point reads.
    // The attempts come from a replay of a chain holding that binding alone, so every row has one, and a success that
    // broke the contract is already a failure there. A one-sided probability is completed as the library completes it.
    public static IReadOnlyList<FittingRow> Rows(
        IReadOnlyList<EvaluatedRow> replayed,
        IReadOnlyList<DatasetRow> tune,
        BooleanRule rule,
        EvidenceKind source)
    {
        HashSet<string> tuneIds = new(tune.Select(row => row.Id), StringComparer.Ordinal);
        string flaggedKey = rule.FlaggedAnswer ? "true" : "false";
        List<FittingRow> rows = [];
        foreach (EvaluatedRow row in replayed)
        {
            if (!tuneIds.Contains(row.Row.Id) || row.Row.Label.Kind != RowLabelKind.Answer)
            {
                continue;
            }

            Attempt? attempt = row.Verdict.Attempts.FirstOrDefault(candidate => candidate.BindingIndex == 0);
            if (attempt is null || attempt.EffectiveOutcome.Status != OutcomeStatus.Success)
            {
                continue;
            }

            Evidence? evidence = attempt.Result.Evidence?.FirstOrDefault(entry => entry is not null && entry.Kind == source);
            if (evidence is null
                || !EvidenceMath.WithBooleanComplement(evidence).Values.TryGetValue(flaggedKey, out double value))
            {
                continue;
            }

            rows.Add(new FittingRow(
                row.Row.Id,
                value,
                string.Equals(row.Row.Label.Answer, flaggedKey, StringComparison.Ordinal),
                attempt.Result.Provider?.Model));
        }

        return rows;
    }

    // Log-odds spread a probability over the line, and so a score that lives where a probability does: every fitting
    // value and every threshold in [0, 1]. Any other score, and a logit, which is already on the line, go in as they
    // are; the log-odds would hold everything above 1 to one value and give rows on either side of a threshold the same
    // probability.
    public static CalibrationTransform Transform(
        EvidenceKind source,
        IReadOnlyList<FittingRow> rows,
        IEnumerable<double> rawThresholds) =>
        source switch
        {
            EvidenceKind.Probability => CalibrationTransform.LogOdds,
            EvidenceKind.Score when rows.All(row => InUnit(row.Value)) && rawThresholds.All(InUnit) =>
                CalibrationTransform.LogOdds,
            _ => CalibrationTransform.Identity,
        };

    // The model most fitting rows name, ties going to the ordinally first, and how many fitting rows name another.
    // A row that names none counts for neither.
    public static (string? Model, int Others) Model(IReadOnlyList<FittingRow> rows)
    {
        List<(string Model, int Rows)> named = rows
            .Where(row => row.Model is not null)
            .GroupBy(row => row.Model!, StringComparer.Ordinal)
            .Select(group => (Model: group.Key, Rows: group.Count()))
            .ToList();
        if (named.Count == 0)
        {
            return (null, 0);
        }

        (string model, int count) = named
            .OrderByDescending(group => group.Rows)
            .ThenBy(group => group.Model, StringComparer.Ordinal)
            .First();
        return (model, named.Sum(group => group.Rows) - count);
    }

    // The value each threshold sits at on the provider's own scale: as written on an uncalibrated point, and mapped
    // back through the old calibration on a calibrated one.
    public static double Raw(RuleOperatingPoint point, double threshold) =>
        point.Calibration is { } old ? old.Invert(threshold) : threshold;

    // The point with the new map, each threshold moved to the probability the map gives the raw value it stood at, so
    // a row at or above a threshold before is at or above it after, and the gate as it was: it reads the provider's
    // margin, which no calibration changes.
    public static RuleOperatingPoint Calibrate(RuleOperatingPoint point, EvidenceCalibration calibration) =>
        point with
        {
            Thresholds =
            [
                .. point.Thresholds.Select(threshold => new Threshold(
                    threshold.Verdict,
                    EvidenceKind.Probability,
                    calibration.Apply(Raw(point, threshold.AtOrAbove)))),
            ],
            Calibration = calibration,
        };

    private static bool InUnit(double value) => value is >= 0 and <= 1;
}
