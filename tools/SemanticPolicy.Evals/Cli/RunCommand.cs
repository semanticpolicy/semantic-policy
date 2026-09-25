using System.CommandLine;
using System.Globalization;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Evals.Running;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Evals.Cli;

// `run`: calls every bound provider on every selected row, records what they answered, then reports on the
// recording exactly as `report` would, so the numbers printed are the numbers the file reproduces.
internal static class RunCommand
{
    private static readonly Option<string?> _record = new("--record")
    {
        Description = "Where the recording goes; ./<dataset>.<policy>.recording.jsonl when omitted.",
        HelpName = "file",
    };

    private static readonly Option<int> _parallel = new("--parallel")
    {
        Description = "How many provider calls may be in flight at once.",
        HelpName = "n",
        DefaultValueFactory = _ => 4,
    };

    private static readonly Option<int> _timeout = new("--timeout")
    {
        Description = "How long one provider call may take before it is recorded as a timeout.",
        HelpName = "seconds",
        DefaultValueFactory = _ => 30,
    };

    public static Command Create(CliIo io, Action<ISemanticPolicyBuilder>? configureProviders)
    {
        Command command = new("run", "Call the policy's providers on every row, record the answers, and report on them.");
        foreach (Option option in SharedOptions.InputOptions)
        {
            command.Options.Add(option);
        }

        command.Options.Add(SharedOptions.Out);
        command.Options.Add(_record);
        command.Options.Add(_parallel);
        command.Options.Add(_timeout);
        command.SetAction((parseResult, cancellationToken) =>
            EvalsCli.GuardAsync(io, () => RunAsync(parseResult, io, configureProviders, cancellationToken)));
        return command;
    }

    private static async Task<int> RunAsync(
        ParseResult parseResult,
        CliIo io,
        Action<ISemanticPolicyBuilder>? configureProviders,
        CancellationToken cancellationToken)
    {
        InputSelection selection = InputSelection.From(parseResult);
        int parallel = parseResult.GetValue(_parallel);
        if (parallel < 1)
        {
            throw new EvalsException("--parallel must be at least 1.");
        }

        int seconds = parseResult.GetValue(_timeout);
        if (seconds < 1)
        {
            throw new EvalsException("--timeout must be at least 1 second.");
        }

        LoadedInputs inputs = Inputs.Load(selection);
        IReadOnlyDictionary<string, IDecisionProvider> providers = Providers.Resolve(configureProviders, inputs.Policy);
        if (inputs.Policy.Budget is not null)
        {
            io.Error.WriteLine("policy budget ignored: run is eager");
        }

        string recordPath = parseResult.GetValue(_record) ?? DefaultRecordPath(selection, inputs.Policy);
        TimeSpan timeout = TimeSpan.FromSeconds(seconds);
        RecordingHeader header = new(
            RecordingHeader.FormatV0,
            inputs.Policy,
            RecordedDatasets(inputs.Datasets),

            // An adapter reports its model per answer, so the header leaves it open until every row is in.
            [.. inputs.Policy.Bindings.Select(binding => new RecordedProvider(binding.ProviderId, Model: null))],
            RecordingHeader.CurrentToolVersion,
            DateTimeOffset.UtcNow,
            parallel,
            timeout);

        EvalRunner runner = new(providers, parallel, timeout);
        await using (RecordingWriter writer = await RecordingWriter.CreateAsync(recordPath, header, cancellationToken)
            .ConfigureAwait(false))
        {
            await runner.RunAsync(
                inputs.Policy,
                inputs.Selected,
                writer,
                new RowProgress(io.Error, inputs.Selected.Count),
                cancellationToken).ConfigureAwait(false);
        }

        // Only a finished run has heard from every provider; a run cut short keeps the header it started with.
        Recording recorded = RecordingReader.Read(recordPath);
        await RecordingWriter.ReplaceHeaderAsync(recordPath, WithModels(header, recorded), cancellationToken)
            .ConfigureAwait(false);

        // The report is built from the file, not from the run's memory, so what is printed is exactly what a
        // later `report` on the same recording prints.
        Recording recording = RecordingReader.Read(recordPath);
        EvalsResult result = ReportPipeline.Build("run", inputs, recording, force: false);
        ReportCommand.Publish(result, parseResult.GetValue(SharedOptions.Out), io);
        return ExitCodes.Success;
    }

    // Each provider's model is the first one its answers reported, as the report's provider table takes it.
    private static RecordingHeader WithModels(RecordingHeader header, Recording recording) =>
        header with
        {
            Providers =
            [
                .. header.Providers.Select(provider => provider with
                {
                    Model = recording.Rows
                        .SelectMany(row => row.Attempts.Values)
                        .Select(byProvider => byProvider.GetValueOrDefault(provider.Name)?.Provider.Model)
                        .FirstOrDefault(model => model is not null),
                }),
            ],
        };

    // Two files are the tune half and the test half, in that order; one file carries its split per row, if at all.
    private static RecordedDataset[] RecordedDatasets(IReadOnlyList<Dataset> datasets) =>
        datasets.Count == 1
            ? [new RecordedDataset(datasets[0].Path, datasets[0].Sha256, Split: null)]
            : [new RecordedDataset(datasets[0].Path, datasets[0].Sha256, "tune"), new RecordedDataset(datasets[1].Path, datasets[1].Sha256, "test")];

    private static string DefaultRecordPath(InputSelection selection, Policy policy)
    {
        string dataset = selection.DatasetPath ?? selection.TunePath
            ?? throw new InvalidOperationException("The selection names neither --dataset nor --tune.");
        return $"./{Path.GetFileNameWithoutExtension(dataset)}.{policy.Id}.recording.jsonl";
    }

    // Written as each row lands rather than posted to a synchronization context, so the lines arrive in order
    // and none is still pending when the report starts printing.
    private sealed class RowProgress(TextWriter error, int total) : IProgress<int>
    {
        public void Report(int value) =>
            error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"row {value} of {total}"));
    }
}
