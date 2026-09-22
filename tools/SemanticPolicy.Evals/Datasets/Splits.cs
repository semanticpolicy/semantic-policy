using System.Text.Json;

namespace SemanticPolicy.Evals.Datasets;

/// <summary>The two values <c>metadata.split</c> may take; <c>--tune-split</c> and <c>--test-split</c> override them.</summary>
/// <param name="Tune">The value that puts a row in the tune half.</param>
/// <param name="Test">The value that puts a row in the test half.</param>
public sealed record SplitNames(string Tune = SplitNames.DefaultTune, string Test = SplitNames.DefaultTest)
{
    /// <summary>The value that puts a row in the tune half unless <c>--tune-split</c> says otherwise.</summary>
    public const string DefaultTune = "tune";

    /// <summary>The value that puts a row in the test half unless <c>--test-split</c> says otherwise.</summary>
    public const string DefaultTest = "test";
}

/// <summary>Where a split came from, so the report can say on which data a recommendation was chosen and tested.</summary>
public enum SplitSource
{
    /// <summary>No row carried a split and one file was given: tune and test are the same rows.</summary>
    None,

    /// <summary>Every row carried <c>metadata.split</c>.</summary>
    Metadata,

    /// <summary>Two files: <c>--tune</c> and <c>--test</c>.</summary>
    Files,
}

/// <summary>The rows a threshold is chosen on and the rows it is reported on.</summary>
/// <param name="Tune">The rows a recommendation is chosen on.</param>
/// <param name="Test">The rows a recommendation is reported on.</param>
/// <param name="Source">How the two were told apart; <see cref="SplitSource.None"/> means they were not.</param>
public sealed record SplitSelection(IReadOnlyList<DatasetRow> Tune, IReadOnlyList<DatasetRow> Test, SplitSource Source);

/// <summary>
/// Resolves the tune and test halves. There is no automatic split: either every row says which half it is
/// in, or two files do, or the halves are the same rows and the caller says so in the report.
/// </summary>
public static class Splits
{
    private const string _key = "split";

    /// <summary>Splits one dataset by <c>metadata.split</c>. The rows have already been filtered.</summary>
    /// <param name="rows">The rows, in dataset order.</param>
    /// <param name="names">The two values a split may take.</param>
    /// <exception cref="EvalsException">
    /// The names are equal or empty; some rows carry a split and others do not; a split is not a string or is
    /// neither name.
    /// </exception>
    public static SplitSelection FromMetadata(IReadOnlyList<DatasetRow> rows, SplitNames names)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);
        if (string.IsNullOrWhiteSpace(names.Tune) || string.IsNullOrWhiteSpace(names.Test) || names.Tune == names.Test)
        {
            throw new EvalsException("--tune-split and --test-split are two different, non-empty names.");
        }

        List<DatasetRow> tune = [];
        List<DatasetRow> test = [];
        DatasetRow? withSplit = null;
        DatasetRow? withoutSplit = null;
        foreach (DatasetRow row in rows)
        {
            if (!row.Metadata.TryGetValue(_key, out JsonElement value))
            {
                withoutSplit ??= row;
                continue;
            }

            withSplit ??= row;
            string? split = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (split == names.Tune)
            {
                tune.Add(row);
            }
            else if (split == names.Test)
            {
                test.Add(row);
            }
            else
            {
                throw new EvalsException(
                    $"Row '{row.Id}' on line {row.Line}: metadata.split is neither '{names.Tune}' nor '{names.Test}'.");
            }
        }

        if (withSplit is null)
        {
            return new SplitSelection(rows, rows, SplitSource.None);
        }

        if (withoutSplit is not null)
        {
            throw new EvalsException(
                $"Row '{withoutSplit.Id}' on line {withoutSplit.Line} has no metadata.split while row '{withSplit.Id}' " +
                $"on line {withSplit.Line} has one; either every row carries a split or none does.");
        }

        return new SplitSelection(tune, test, SplitSource.Metadata);
    }

    /// <summary>Takes the split from two files. The rows of each have already been filtered.</summary>
    /// <param name="tuneRows">The rows of the <c>--tune</c> file.</param>
    /// <param name="testRows">The rows of the <c>--test</c> file.</param>
    /// <exception cref="EvalsException">A row carries <c>metadata.split</c>, or an id is in both files.</exception>
    public static SplitSelection FromFiles(IReadOnlyList<DatasetRow> tuneRows, IReadOnlyList<DatasetRow> testRows)
    {
        ArgumentNullException.ThrowIfNull(tuneRows);
        ArgumentNullException.ThrowIfNull(testRows);
        RejectSplitMetadata(tuneRows, "--tune");
        RejectSplitMetadata(testRows, "--test");
        Dictionary<string, DatasetRow> tuneById = new(StringComparer.Ordinal);
        foreach (DatasetRow row in tuneRows)
        {
            tuneById[row.Id] = row;
        }

        foreach (DatasetRow row in testRows)
        {
            if (tuneById.TryGetValue(row.Id, out DatasetRow? twin))
            {
                throw new EvalsException(
                    $"Id '{row.Id}' is in both files: --tune line {twin.Line} and --test line {row.Line}.");
            }
        }

        return new SplitSelection(tuneRows, testRows, SplitSource.Files);
    }

    private static void RejectSplitMetadata(IReadOnlyList<DatasetRow> rows, string option)
    {
        foreach (DatasetRow row in rows)
        {
            if (row.Metadata.ContainsKey(_key))
            {
                throw new EvalsException(
                    $"Row '{row.Id}' on line {row.Line} of {option} carries metadata.split; with two files the file is the split.");
            }
        }
    }
}
