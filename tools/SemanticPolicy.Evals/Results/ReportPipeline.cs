using SemanticPolicy.Evals.Counting;
using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Replay;

namespace SemanticPolicy.Evals.Results;

/// <summary>
/// The one way a recording becomes a report. <c>run</c> calls it on the file it has just written and
/// <c>report</c> on a file written earlier, so the two print the same thing about the same recording and a
/// report can always be reproduced from a committed recording without calling a provider.
/// </summary>
public static class ReportPipeline
{
    /// <summary>
    /// Replays every selected row at the policy file's own thresholds and gates, buckets it, and measures the
    /// selected rule. The discrimination curves sweep the first binding's threshold with every other binding
    /// at its file numbers; the provider table reads every recorded attempt of the rule, including those of
    /// bindings the cascade never reached, because the run made them.
    /// </summary>
    /// <param name="verb">The verb the result is written for.</param>
    /// <param name="inputs">The policy, rule and rows the verb loaded.</param>
    /// <param name="recording">The recording to replay.</param>
    /// <param name="force">Replay even though a dataset's digest differs from the recorded one.</param>
    /// <exception cref="EvalsException">
    /// The recording does not fit the inputs: a digest differs without <paramref name="force"/>, the rule asks a
    /// different question, or a binding has no recorded attempt on a selected row.
    /// </exception>
    public static EvalsResult Build(string verb, LoadedInputs inputs, Recording recording, bool force)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(recording);
        ReplaySet set = ReplaySet.Load(recording, inputs, force);
        Rule rule = inputs.Rule;
        List<RowOutcome> outcomes = [.. set.Evaluate().Select(row => RowCounting.Classify(row, rule))];

        IReadOnlyList<RungMetrics>? rungs = null;
        MulticlassConfusion? classes = null;
        IReadOnlyList<RungDiscrimination>? discrimination = null;
        string? swept = null;
        switch (rule)
        {
            case BooleanRule boolean:
                rungs = RungMetrics.Compute(outcomes, boolean);
                swept = inputs.Policy.Bindings[0].ProviderId;
                discrimination =
                [
                    .. ThresholdCurve.Compute(set, inputs.Policy, 0, [.. set.Rows.Select(row => row.Row)])
                        .Select(curve => new RungDiscrimination(curve.Rung, Discrimination.FromCurve(curve))),
                ];
                break;
            case ChoiceRule choice:
                classes = MulticlassConfusion.Compute(outcomes, choice);
                break;
            case ScoreRule score:
                classes = MulticlassConfusion.Compute(outcomes, score);
                break;
            default:
                break;
        }

        ReportSection report = new(
            OutcomeCounts.Compute(outcomes),
            Verdicts.Distribution(outcomes),
            rungs,
            classes,
            discrimination,
            Calibration.Compute(outcomes, rule),
            ProviderStats.Compute(set.Rows.SelectMany(row =>
                row.AttemptsByProvider.Select(attempt => (attempt.Key, attempt.Value)))),
            Notes(inputs, recording, force),
            swept);

        return new EvalsResult(
            EvalsResult.FormatV0,
            verb,
            RecordingHeader.CurrentToolVersion,
            DateTimeOffset.UtcNow,
            inputs.Policy.Id,
            inputs.Policy.Mode,
            rule.Id,
            rule.Type,
            Selection(inputs, recording, set),
            report,
            recording.Path);
    }

    // The drop from the dataset to the scored rows, stage by stage: what the files hold, what the recording
    // has results for, and what of that the filters kept. The split is counted on the kept rows.
    private static RowSelection Selection(LoadedInputs inputs, Recording recording, ReplaySet set)
    {
        HashSet<string> recorded = new(recording.Rows.Select(row => row.Id), StringComparer.Ordinal);
        int recordedRows = inputs.Datasets.Sum(dataset => dataset.Rows.Count(row => recorded.Contains(row.Id)));
        SplitSelection splits = inputs.Splits;
        int tune = 0;
        int test = 0;
        if (splits.Source != SplitSource.None)
        {
            HashSet<string> tuneIds = new(splits.Tune.Select(row => row.Id), StringComparer.Ordinal);
            HashSet<string> testIds = new(splits.Test.Select(row => row.Id), StringComparer.Ordinal);
            tune = set.Rows.Count(row => tuneIds.Contains(row.Row.Id));
            test = set.Rows.Count(row => testIds.Contains(row.Row.Id));
        }

        return new RowSelection(
            inputs.RowsBeforeFilter,
            recordedRows,
            set.RecordedRowCount,
            [.. inputs.Filters.Select(filter => $"metadata.{filter.Key}={filter.Value}")],
            Names.Camel(splits.Source),
            tune,
            test);
    }

    private static List<string> Notes(LoadedInputs inputs, Recording recording, bool force)
    {
        List<string> notes =
        [
            "Every verdict here is a provider's estimate put through the policy's thresholds. It can be wrong in "
                + "either direction on inputs this dataset does not cover, and these numbers describe this dataset only.",
            inputs.Policy.Mode == PolicyMode.Shadow
                ? "The policy is in shadow mode. Every number reads the rule's own verdict, so they are the numbers "
                    + "enforce mode would give on the same recording."
                : "The policy is in enforce mode. Every number reads the rule's own verdict, never the policy's "
                    + "effective one.",
        ];
        if (inputs.Policy.Budget is not null)
        {
            notes.Add("The policy's budget was not applied: a run makes every attempt, each under its own per-attempt "
                + "timeout, and a replay reads what was recorded.");
        }

        HashSet<string> recordedDigests = new(
            recording.Header.Datasets.Select(dataset => dataset.Sha256),
            StringComparer.OrdinalIgnoreCase);
        if (force && !recordedDigests.SetEquals(inputs.Datasets.Select(dataset => dataset.Sha256)))
        {
            notes.Add("A dataset's digest differs from the recorded one. The results were matched to rows by id under "
                + "--force and may be about different content.");
        }

        return notes;
    }
}
