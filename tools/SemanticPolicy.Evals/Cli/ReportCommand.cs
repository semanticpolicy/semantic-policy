using System.CommandLine;
using SemanticPolicy.Evals.Gating;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Output;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Cli;

// `report`: replays a recording at the policy file's numbers and prints what it measured. It calls no provider.
internal static class ReportCommand
{
    private static readonly Option<string?> _diagram = new("--diagram")
    {
        Description = "Write the reliability diagram as SVG to this file; only where the calibration section applies.",
        HelpName = "file",
    };

    public static Command Create(CliIo io)
    {
        Command command = new("report", "Replay a recording at the policy's thresholds and print what it measured.");
        foreach (Option option in SharedOptions.InputOptions)
        {
            command.Options.Add(option);
        }

        command.Options.Add(SharedOptions.Out);
        command.Options.Add(SharedOptions.Recording);
        command.Options.Add(SharedOptions.Force);
        command.Options.Add(_diagram);
        command.Options.Add(SharedOptions.Require);
        command.SetAction(parseResult => EvalsCli.Guard(io, () => Report(parseResult, io)));
        return command;
    }

    // Read before any file is opened, so a mistyped requirement costs no replay and, in `run`, no call.
    internal static IReadOnlyList<Requirement> Requirements(ParseResult parseResult) =>
        [.. (parseResult.GetValue(SharedOptions.Require) ?? []).Select(Requirement.Parse)];

    // The inputs, once every requirement is known to fit the rule they select.
    internal static LoadedInputs Load(InputSelection selection, IReadOnlyList<Requirement> requirements)
    {
        LoadedInputs inputs = Inputs.Load(selection);
        foreach (Requirement requirement in requirements)
        {
            requirement.EnsureFits(inputs.Rule);
        }

        return inputs;
    }

    // Judges the requirements, then writes and prints the result, and says how the verb exits: 2 when a requirement
    // failed, once everything is written. The JSON file is written before the text is printed, so a result that cannot
    // be saved fails the verb before a script reading the output has seen a report it will then be told is incomplete.
    public static int Publish(EvalsResult result, IReadOnlyList<Requirement> requirements, string? outPath, CliIo io)
    {
        if (requirements.Count > 0)
        {
            ReportSection report = result.Report ?? throw new InvalidOperationException("A requirement needs a report to judge.");
            result = result with { Requirements = [.. requirements.Select(requirement => requirement.Judge(report))] };
        }

        if (outPath is not null)
        {
            ResultWriter.Write(outPath, result);
        }

        ReportRenderer.Write(result, io.Output);
        return result.Requirements?.Any(requirement => !requirement.Passed) == true
            ? ExitCodes.InfeasibleConstraint
            : ExitCodes.Success;
    }

    private static int Report(ParseResult parseResult, CliIo io)
    {
        CliFiles.RefuseSharedFiles(
            "report",
            [
                ("--policy", parseResult.GetValue(SharedOptions.Policy)),
                ("--dataset", parseResult.GetValue(SharedOptions.Dataset)),
                ("--tune", parseResult.GetValue(SharedOptions.Tune)),
                ("--test", parseResult.GetValue(SharedOptions.Test)),
                ("--recording", parseResult.GetValue(SharedOptions.Recording)),
            ],
            [
                ("--out", parseResult.GetValue(SharedOptions.Out)),
                ("--diagram", parseResult.GetValue(_diagram)),
            ]);
        IReadOnlyList<Requirement> requirements = Requirements(parseResult);
        InputSelection selection = InputSelection.From(parseResult);
        string recordingPath = parseResult.GetValue(SharedOptions.Recording)
            ?? throw new EvalsException("--recording <file> is required.");
        LoadedInputs inputs = Load(selection, requirements);
        Recording recording = RecordingReader.Read(recordingPath);
        EvalsResult result = ReportPipeline.Build("report", inputs, recording, parseResult.GetValue(SharedOptions.Force));

        // Written before the report is printed, for the same reason as the JSON file.
        if (parseResult.GetValue(_diagram) is { } diagramPath && result.Report is { } report)
        {
            Diagram(diagramPath, report.Calibration, result.DecisionType, io);
        }

        return Publish(result, requirements, parseResult.GetValue(SharedOptions.Out), io);
    }

    // Without a probability there is nothing to draw, but the report still stands: the verb says why on standard
    // error, in the calibration section's words, and leaves whatever is at the path as it was.
    private static void Diagram(string path, Calibration calibration, DecisionType type, CliIo io)
    {
        if (calibration.Applicable)
        {
            ReliabilityDiagram.Write(path, calibration);
            return;
        }

        io.Error.WriteLine($"diagram not written: {ReportRenderer.NotApplicableReason(calibration, type)}");
    }
}
