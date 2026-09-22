using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Policies;

namespace SemanticPolicy.Evals;

/// <summary>Everything a verb reads, opened and checked, with the counts the report states.</summary>
/// <param name="Policy">The validated policy.</param>
/// <param name="Rule">The rule the verb works on.</param>
/// <param name="Datasets">The files read: one, or the tune file then the test file.</param>
/// <param name="Selected">Every row that passed the filters, in file order, tune file first.</param>
/// <param name="RowsBeforeFilter">How many rows the files held before the filters ran.</param>
/// <param name="Filters">The filters that ran.</param>
/// <param name="Splits">The two halves, and where the split came from.</param>
public sealed record LoadedInputs(
    Policy Policy,
    Rule Rule,
    IReadOnlyList<Dataset> Datasets,
    IReadOnlyList<DatasetRow> Selected,
    int RowsBeforeFilter,
    IReadOnlyList<MetadataFilter> Filters,
    SplitSelection Splits);

/// <summary>Opens what an <see cref="InputSelection"/> names, in the order the errors are most useful.</summary>
public static class Inputs
{
    /// <summary>Policy, then rule, then the dataset or pair, each bound to the rule, filtered, then split.</summary>
    /// <param name="selection">What to read.</param>
    /// <exception cref="EvalsException">Any file, row, label or split is wrong; the message says where.</exception>
    public static LoadedInputs Load(InputSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Policy policy = PolicyFile.Load(selection.PolicyPath);
        Rule rule = PolicyFile.SelectRule(policy, selection.RuleId);
        if (selection.DatasetPath is { } single)
        {
            Dataset dataset = DatasetReader.Read(single);
            IReadOnlyList<DatasetRow> rows = MetadataFilter.Apply(selection.Filters, dataset.ForRule(rule));

            // The filter runs before the split is read, so a filtered-out row is in neither half and the
            // report's row counts describe the same rows the halves hold.
            SplitSelection splits = Splits.FromMetadata(rows, selection.SplitNames);
            return new LoadedInputs(policy, rule, [dataset], rows, dataset.Rows.Count, selection.Filters, splits);
        }

        Dataset tune = DatasetReader.Read(
            selection.TunePath ?? throw new InvalidOperationException("The selection names neither --dataset nor --tune."));
        Dataset test = DatasetReader.Read(
            selection.TestPath ?? throw new InvalidOperationException("The selection names --tune without --test."));
        IReadOnlyList<DatasetRow> tuneRows = MetadataFilter.Apply(selection.Filters, tune.ForRule(rule));
        IReadOnlyList<DatasetRow> testRows = MetadataFilter.Apply(selection.Filters, test.ForRule(rule));
        return new LoadedInputs(
            policy,
            rule,
            [tune, test],
            [.. tuneRows, .. testRows],
            tune.Rows.Count + test.Rows.Count,
            selection.Filters,
            Splits.FromFiles(tuneRows, testRows));
    }
}
