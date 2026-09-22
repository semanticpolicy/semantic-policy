using System.CommandLine;
using SemanticPolicy.Evals.Output;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evals.Results;

namespace SemanticPolicy.Evals.Cli;

// `report`: replays a recording at the policy file's numbers and prints what it measured. It calls no provider.
internal static class ReportCommand
{
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
        command.SetAction(parseResult => EvalsCli.Guard(io, () => Report(parseResult, io)));
        return command;
    }

    // The JSON file is written before the text is printed, so a result that cannot be saved fails the verb
    // before a script reading the output has seen a report it will then be told is incomplete.
    public static void Publish(EvalsResult result, string? outPath, CliIo io)
    {
        if (outPath is not null)
        {
            ResultWriter.Write(outPath, result);
        }

        ReportRenderer.Write(result, io.Output);
    }

    private static int Report(ParseResult parseResult, CliIo io)
    {
        InputSelection selection = InputSelection.From(parseResult);
        string recordingPath = parseResult.GetValue(SharedOptions.Recording)
            ?? throw new EvalsException("--recording <file> is required.");
        LoadedInputs inputs = Inputs.Load(selection);
        Recording recording = RecordingReader.Read(recordingPath);
        EvalsResult result = ReportPipeline.Build("report", inputs, recording, parseResult.GetValue(SharedOptions.Force));
        Publish(result, parseResult.GetValue(SharedOptions.Out), io);
        return ExitCodes.Success;
    }
}
