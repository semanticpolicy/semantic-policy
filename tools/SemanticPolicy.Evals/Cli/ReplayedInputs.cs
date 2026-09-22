using System.CommandLine;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Evals.Sweeping;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Cli;

// What a verb that works from a recording has read: the inputs, the recording joined to them, and how the two
// halves are named wherever a number chosen on one is reported on the other.
internal sealed record ReplayedInputs(
    LoadedInputs Inputs,
    ReplaySet Set,
    int RecordedRows,
    IReadOnlyList<DatasetRow> Tune,
    IReadOnlyList<DatasetRow> Test,
    SplitWording Wording)
{
    public static ReplayedInputs Load(ParseResult parse)
    {
        InputSelection selection = InputSelection.From(parse);
        string recordingPath = parse.GetValue(SharedOptions.Recording)
            ?? throw new EvalsException("--recording <file> is required: this verb replays a recording.");
        LoadedInputs inputs = Evals.Inputs.Load(selection);
        Recording recording = RecordingReader.Read(recordingPath);
        ReplaySet set = ReplaySet.Load(recording, inputs, parse.GetValue(SharedOptions.Force));
        HashSet<string> inRecording = new(recording.Rows.Select(row => row.Id), StringComparer.Ordinal);
        int recordedRows = inputs.Datasets.Sum(dataset => dataset.Rows.Count(row => inRecording.Contains(row.Id)));

        // A half is described by the rows that were actually scored: a row the recording never reached has
        // no answer to count, and the counts the lines print are the rows behind the numbers beside them.
        HashSet<string> replayed = new(set.Rows.Select(row => row.Row.Id), StringComparer.Ordinal);
        DatasetRow[] tune = [.. inputs.Splits.Tune.Where(row => replayed.Contains(row.Id))];
        DatasetRow[] test = [.. inputs.Splits.Test.Where(row => replayed.Contains(row.Id))];
        SplitWording wording = Describe(inputs.Splits.Source, selection, tune.Length, test.Length);
        if (tune.Length == 0)
        {
            throw new EvalsException(
                $"Recording '{recordingPath}' holds none of the rows to choose on ({wording.ChosenOn}), so there is "
                + "nothing to sweep. Record those rows first, or select rows the recording has.");
        }

        return new ReplayedInputs(inputs, set, recordedRows, tune, test, wording);
    }

    public RowSelection Rows() =>
        new(
            Inputs.RowsBeforeFilter,
            RecordedRows,
            Set.RecordedRowCount,
            [.. Inputs.Filters.Select(filter => $"metadata.{filter.Key}={filter.Value}")],
            Names.Camel(Inputs.Splits.Source),
            Tune.Count,
            Test.Count);

    // The binding --provider names; with no --provider, the only one. A policy with several bindings has no
    // binding a sweep could take by default, since the order of a chain says nothing about which to tune.
    public int BindingIndex(string? provider)
    {
        IReadOnlyList<ProviderBinding> bindings = Inputs.Policy.Bindings;
        if (provider is null)
        {
            return bindings.Count == 1
                ? 0
                : throw new EvalsException(
                    $"Policy '{Inputs.Policy.Id}' binds {List(bindings)}; name the one to sweep with --provider <name>.");
        }

        return IndexOf(provider);
    }

    public int IndexOf(string provider)
    {
        IReadOnlyList<ProviderBinding> bindings = Inputs.Policy.Bindings;
        for (int index = 0; index < bindings.Count; index++)
        {
            if (string.Equals(bindings[index].ProviderId, provider, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new EvalsException(
            $"--provider '{provider}' is not a binding of policy '{Inputs.Policy.Id}'; its bindings are {List(bindings)}.");
    }

    public static IReadOnlyList<RungConstraint> RungConstraints(ParseResult parse) =>
    [
        .. (parse.GetValue(SharedOptions.Warn) ?? []).Select(token => RungConstraint.Parse(Verdict.Warn, token)),
        .. (parse.GetValue(SharedOptions.Escalate) ?? []).Select(token => RungConstraint.Parse(Verdict.Escalate, token)),
        .. (parse.GetValue(SharedOptions.Deny) ?? []).Select(token => RungConstraint.Parse(Verdict.Deny, token)),
    ];

    public static IReadOnlyList<GateConstraint> GateConstraints(ParseResult parse) =>
        [.. (parse.GetValue(SharedOptions.Gate) ?? []).Select(GateConstraint.Parse)];

    public EvalsResult Result(string verb, SweepSection? sweep = null, CompareSection? compare = null) =>
        new(
            EvalsResult.FormatV0,
            verb,
            RecordingHeader.CurrentToolVersion,
            DateTimeOffset.UtcNow,
            Inputs.Policy.Id,
            Inputs.Policy.Mode,
            Set.Rule.Id,
            Set.Rule.Type,
            Rows(),
            Report: null,
            Sweep: sweep,
            Compare: compare);

    private static SplitWording Describe(SplitSource source, InputSelection selection, int tune, int test) => source switch
    {
        SplitSource.Metadata => Apart(
            $"split '{selection.SplitNames.Tune}' ({Count(tune)})",
            $"split '{selection.SplitNames.Test}' ({Count(test)})"),
        SplitSource.Files => Apart($"file '{selection.TunePath}' ({Count(tune)})", $"file '{selection.TestPath}' ({Count(test)})"),
        _ => new SplitWording(
            $"the same data, no split ({Count(tune)})",
            $"the same data, no split ({Count(test)})",
            $"chosen and reported on the same data (no split), {Count(tune)}"),
    };

    private static SplitWording Apart(string tune, string test) => new(tune, test, $"chosen on {tune}, reported on {test}");

    private static string Count(int rows) => rows == 1 ? "1 row" : $"{rows} rows";

    private static string List(IReadOnlyList<ProviderBinding> bindings) =>
        string.Join(", ", bindings.Select(binding => $"'{binding.ProviderId}'"));
}
