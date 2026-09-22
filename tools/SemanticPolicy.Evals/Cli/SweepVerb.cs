using System.CommandLine;
using SemanticPolicy.Evals.Output;
using SemanticPolicy.Evals.Results;
using SemanticPolicy.Evals.Sweeping;

namespace SemanticPolicy.Evals.Cli;

/// <summary>
/// <c>sweep</c>: one binding's thresholds and gate, swept on a recording. Every curve is printed; a threshold or a
/// gate is recommended only under a constraint the user gave, and a constraint nothing satisfies ends the run with
/// <see cref="ExitCodes.InfeasibleConstraint"/> after everything else has been printed and written.
/// </summary>
public static class SweepVerb
{
    private static readonly Option<string?> _provider = new("--provider")
    {
        Description = "The binding to sweep, by provider name; required when the policy has more than one.",
        HelpName = "name",
    };

    /// <summary>Builds the verb.</summary>
    /// <param name="io">Where it writes.</param>
    public static Command Build(CliIo io)
    {
        ArgumentNullException.ThrowIfNull(io);
        Command command = new(
            "sweep",
            "Sweep one binding's thresholds and margin gate on a recording, and recommend them under constraints.");
        foreach (Option option in SharedOptions.InputOptions)
        {
            command.Options.Add(option);
        }

        command.Options.Add(SharedOptions.Recording);
        command.Options.Add(SharedOptions.Force);
        command.Options.Add(SharedOptions.Out);
        command.Options.Add(_provider);
        foreach (Option option in SharedOptions.ConstraintOptions)
        {
            command.Options.Add(option);
        }

        command.SetAction(parse => EvalsCli.Guard(io, () => Run(parse, io)));
        return command;
    }

    private static int Run(ParseResult parse, CliIo io)
    {
        // Constraints are read first, so a mistyped one is reported before any file is opened.
        IReadOnlyList<RungConstraint> rungConstraints = ReplayedInputs.RungConstraints(parse);
        IReadOnlyList<GateConstraint> gateConstraints = ReplayedInputs.GateConstraints(parse);
        ReplayedInputs replayed = ReplayedInputs.Load(parse);
        int bindingIndex = replayed.BindingIndex(parse.GetValue(_provider));
        SweepSection section = OperatingPointSweep.Run(
            replayed.Set,
            replayed.Inputs.Policy,
            bindingIndex,
            replayed.Inputs.Splits,
            replayed.Wording,
            rungConstraints,
            gateConstraints);

        // Everything is computed before anything is printed, so a run that fails prints only the error.
        EvalsResult result = replayed.Result("sweep", section);
        if (parse.GetValue(SharedOptions.Out) is { } path)
        {
            ResultWriter.Write(path, result);
        }

        SweepRenderer.WriteSweep(io.Output, result, section);
        return section.Feasible ? ExitCodes.Success : ExitCodes.InfeasibleConstraint;
    }
}
