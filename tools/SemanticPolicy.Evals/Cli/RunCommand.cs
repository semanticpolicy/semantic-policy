using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Gating;
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

    private static readonly Option<int> _retries = new("--retries")
    {
        Description = "How many times a call answered unavailable is made again, with a growing wait between.",
        HelpName = "n",
        DefaultValueFactory = _ => 2,
    };

    private static readonly Option<string?> _resume = new("--resume")
    {
        Description = "Finish this recording: call the rows it lacks and the attempts it holds as unavailable, then rewrite it.",
        HelpName = "recording",
    };

    // The wait before a first retry at most, doubled for each retry after it.
    private static readonly TimeSpan _retryBase = TimeSpan.FromSeconds(1);

    private static readonly Option<string?> _providers = new("--providers")
    {
        Description = "A providers file; its entries are then the only providers run may call.",
        HelpName = "file",
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
        command.Options.Add(_retries);
        command.Options.Add(_resume);
        command.Options.Add(_providers);
        command.Options.Add(SharedOptions.Require);
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
        IReadOnlyList<Requirement> requirements = ReportCommand.Requirements(parseResult);
        string? resumePath = parseResult.GetValue(_resume);
        string? providersPath = parseResult.GetValue(_providers);
        if (resumePath is not null)
        {
            SharedOptions.EnsureInputPath(resumePath, "--resume", "the recording to resume");
        }

        if (providersPath is not null)
        {
            SharedOptions.EnsureInputPath(providersPath, "--providers", "the providers file to read");
        }

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

        int retries = parseResult.GetValue(_retries);
        if (retries < 0)
        {
            throw new EvalsException("--retries must be at least 0.");
        }

        if (resumePath is not null)
        {
            RefuseSharedFiles(parseResult, "--resume", resumePath);
            return await ResumeAsync(
                parseResult, io, configureProviders, selection, requirements, resumePath, parallel, retries, cancellationToken)
                .ConfigureAwait(false);
        }

        string? requestedRecordPath = parseResult.GetValue(_record);
        RefuseSharedFiles(parseResult, requestedRecordPath is null ? null : "--record", requestedRecordPath);

        LoadedInputs inputs = ReportCommand.Load(selection, requirements);
        string recordPath = requestedRecordPath ?? DefaultRecordPath(selection, inputs.Policy);
        if (requestedRecordPath is null)
        {
            RefuseSharedFiles(parseResult, "--record", recordPath);
        }

        IReadOnlyDictionary<string, IDecisionProvider> providers = ResolveProviders(parseResult, configureProviders, inputs.Policy);
        if (inputs.Policy.Budget is not null)
        {
            io.Error.WriteLine("policy budget ignored: run is eager");
        }

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
            timeout,
            retries,
            [.. selection.Filters.Select(Token)]);

        EvalRunner runner = new(providers, parallel, timeout, retries, _retryBase);
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
        return ReportCommand.Publish(result, requirements, parseResult.GetValue(SharedOptions.Out), io);
    }

    // Finishes a recording under the conditions it was started with, so that it ends up holding the rows the first
    // run selected, asked the same way. Whatever could differ is checked before any call, and the file is left as
    // it was when anything does.
    private static async Task<int> ResumeAsync(
        ParseResult parseResult,
        CliIo io,
        Action<ISemanticPolicyBuilder>? configureProviders,
        InputSelection selection,
        IReadOnlyList<Requirement> requirements,
        string path,
        int parallel,
        int retries,
        CancellationToken cancellationToken)
    {
        if (parseResult.GetValue(_record) is not null)
        {
            throw new EvalsException("--record does not go with --resume: a resume rewrites the recording it finishes.");
        }

        Recording recording = RecordingReader.Read(path);
        RecordingHeader header = recording.Header;
        if (header.Where is not { } recordedWhere || header.Retries is null)
        {
            throw new EvalsException(
                $"Recording '{path}' does not say which --where and --retries it was made with, so --resume cannot tell "
                + $"which rows it selected; it was written by tool version {header.ToolVersion}.");
        }

        TimeSpan timeout = header.Timeout;
        if (parseResult.GetResult(_timeout) is { Implicit: false })
        {
            int seconds = parseResult.GetValue(_timeout);
            if (TimeSpan.FromSeconds(seconds) != timeout)
            {
                throw new EvalsException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"--timeout {seconds} differs from the {timeout.TotalSeconds} s recording '{path}' was made with; omit it to take the recording's."));
            }
        }

        if (parseResult.GetResult(SharedOptions.Where) is null)
        {
            selection = selection with { Filters = [.. recordedWhere.Select(MetadataFilter.Parse)] };
        }
        else
        {
            string[] given = [.. selection.Filters.Select(Token)];
            if (!new HashSet<string>(given, StringComparer.Ordinal).SetEquals(recordedWhere))
            {
                throw new EvalsException(
                    $"--where {string.Join(", ", given)} differs from the filters recording '{path}' was made with "
                    + $"({(recordedWhere.Count == 0 ? "none" : string.Join(", ", recordedWhere))}); omit it to take the recording's.");
            }
        }

        LoadedInputs inputs = ReportCommand.Load(selection, requirements);
        CheckSameInputs(path, recording, inputs);
        Dictionary<string, RecordedRow> recorded = new(StringComparer.Ordinal);
        foreach (RecordedRow row in recording.Rows)
        {
            recorded.TryAdd(row.Id, row);
        }

        if (EvalRunner.AttemptsToFinish(inputs.Policy, inputs.Selected, recorded) == 0)
        {
            io.Error.WriteLine(
                $"nothing needed calling: recording '{path}' holds every selected row and no unavailable attempt; it is left as it was");
            return ReportCommand.Publish(
                ReportPipeline.Build("run", inputs, recording, force: false),
                requirements,
                parseResult.GetValue(SharedOptions.Out),
                io);
        }

        IReadOnlyDictionary<string, IDecisionProvider> providers = ResolveProviders(parseResult, configureProviders, inputs.Policy);
        if (inputs.Policy.Budget is not null)
        {
            io.Error.WriteLine("policy budget ignored: run is eager");
        }

        RecordingHeader resumed = header with
        {
            Resumptions =
            [
                .. header.Resumptions ?? [],
                new RecordedResumption(DateTimeOffset.UtcNow, parallel, retries, RecordingHeader.CurrentToolVersion),
            ],
        };
        EvalRunner runner = new(providers, parallel, timeout, retries, _retryBase);
        string rewrite = path + ".tmp";
        try
        {
            await using RecordingWriter writer = await RecordingWriter.CreateAsync(rewrite, resumed, cancellationToken)
                .ConfigureAwait(false);
            await runner.ResumeAsync(
                inputs.Policy,
                inputs.Selected,
                recorded,
                writer,
                new RowProgress(io.Error, inputs.Selected.Count),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cut short or failed, the resume has still written every row it had; the rewrite keeps the rows it
            // finished if it is whole.
            KeepIfWhole(rewrite, path, recorded.Keys);
            throw;
        }

        if (!KeepIfWhole(rewrite, path, recorded.Keys))
        {
            throw new EvalsException(
                $"Recording '{path}': the rewrite does not hold every row the recording did, so the recording was left as it was.");
        }

        await RecordingWriter.ReplaceHeaderAsync(path, WithModels(resumed, RecordingReader.Read(path)), cancellationToken)
            .ConfigureAwait(false);
        EvalsResult result = ReportPipeline.Build("run", inputs, RecordingReader.Read(path), force: false);
        return ReportCommand.Publish(result, requirements, parseResult.GetValue(SharedOptions.Out), io);
    }

    // The policy and the data a resume was given against the ones the recording names. The messages name what
    // differs — a policy id, a file and its digest — and never a row or a rule's text.
    private static void CheckSameInputs(string path, Recording recording, LoadedInputs inputs)
    {
        RecordingHeader header = recording.Header;

        // Compared as the library writes a policy, so a file that differs only in layout is the same policy.
        if (!string.Equals(
                JsonSerializer.Serialize(inputs.Policy, SemanticPolicyJson.Options),
                JsonSerializer.Serialize(header.Policy, SemanticPolicyJson.Options),
                StringComparison.Ordinal))
        {
            throw new EvalsException(
                $"Policy '{inputs.Policy.Id}' is not the policy recording '{path}' was made with; a resume asks what the first run asked.");
        }

        if (header.Datasets.Count != inputs.Datasets.Count)
        {
            throw new EvalsException(
                $"Recording '{path}' was made on {Files(header.Datasets.Count)}, and {Files(inputs.Datasets.Count)} given.");
        }

        for (int index = 0; index < header.Datasets.Count; index++)
        {
            Dataset dataset = inputs.Datasets[index];
            string digest = header.Datasets[index].Sha256;
            if (!string.Equals(dataset.Sha256, digest, StringComparison.OrdinalIgnoreCase))
            {
                throw new EvalsException(
                    $"Dataset '{dataset.Path}' has digest {dataset.Sha256}, not the {digest} recording '{path}' was made on; "
                    + "a resume calls the rows the first run read.");
            }
        }

        HashSet<string> selected = new(inputs.Selected.Select(row => row.Id), StringComparer.Ordinal);
        int outside = recording.Rows.Count(row => !selected.Contains(row.Id));
        if (outside > 0)
        {
            throw new EvalsException(string.Create(
                CultureInfo.InvariantCulture,
                $"Recording '{path}' holds {outside} rows the dataset and filters do not select, which a resume would drop."));
        }
    }

    // The rewrite takes the recording's place only when it holds every row the recording held, so nothing that goes
    // wrong while it is written can cost a row already paid for; otherwise it is deleted and the recording stays.
    private static bool KeepIfWhole(string rewrite, string path, IEnumerable<string> held)
    {
        bool whole;
        try
        {
            HashSet<string> written = new(RecordingReader.Read(rewrite).Rows.Select(row => row.Id), StringComparer.Ordinal);
            whole = held.All(written.Contains);
        }
        catch (EvalsException)
        {
            whole = false;
        }

        try
        {
            if (whole)
            {
                File.Move(rewrite, path, overwrite: true);
            }
            else
            {
                File.Delete(rewrite);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new EvalsException($"Recording '{path}' cannot be replaced by its rewrite '{rewrite}': {e.Message}");
        }

        return whole;
    }

    private static string Token(MetadataFilter filter) => $"metadata.{filter.Key}={filter.Value}";

    private static string Files(int count) => count == 1 ? "one dataset file" : $"{count.ToString(CultureInfo.InvariantCulture)} dataset files";

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

    // The file replaces the providers the tool was built with rather than adding to them, so the file alone
    // shows where a dataset's content goes; a resume reads it the same way.
    private static IReadOnlyDictionary<string, IDecisionProvider> ResolveProviders(
        ParseResult parseResult,
        Action<ISemanticPolicyBuilder>? configureProviders,
        Policy policy)
    {
        string? providersFile = parseResult.GetValue(_providers);
        return Providers.Resolve(providersFile is null ? configureProviders : ProvidersFile.Read(providersFile), policy);
    }

    private static string DefaultRecordPath(InputSelection selection, Policy policy)
    {
        string dataset = selection.DatasetPath ?? selection.TunePath
            ?? throw new InvalidOperationException("The selection names neither --dataset nor --tune.");
        return $"./{Path.GetFileNameWithoutExtension(dataset)}.{policy.Id}.recording.jsonl";
    }

    private static void RefuseSharedFiles(ParseResult parseResult, string? recordOption, string? recordPath) =>
        CliFiles.RefuseSharedFiles(
            "run",
            [
                ("--policy", parseResult.GetValue(SharedOptions.Policy)),
                ("--dataset", parseResult.GetValue(SharedOptions.Dataset)),
                ("--tune", parseResult.GetValue(SharedOptions.Tune)),
                ("--test", parseResult.GetValue(SharedOptions.Test)),
                ("--providers", parseResult.GetValue(_providers)),
            ],
            [
                (recordOption ?? "--record", recordPath),
                ("--out", parseResult.GetValue(SharedOptions.Out)),
            ]);

    // Written as each row lands rather than posted to a synchronization context, so the lines arrive in order
    // and none is still pending when the report starts printing.
    private sealed class RowProgress(TextWriter error, int total) : IProgress<int>
    {
        public void Report(int value) =>
            error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"row {value} of {total}"));
    }
}
