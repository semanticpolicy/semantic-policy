using System.CommandLine;
using System.CommandLine.Help;

namespace SemanticPolicy.Evals.Cli;

/// <summary>The command tree of the tool, and the error boundary every verb action runs inside.</summary>
public static class EvalsCli
{
    /// <summary>Builds the root command with every verb attached.</summary>
    /// <param name="io">Where output goes; the console when omitted.</param>
    /// <param name="configureProviders">
    /// Registers the providers <c>run</c> may call, as an application would on its own builder; none when omitted.
    /// </param>
    public static Command Build(CliIo? io = null, Action<ISemanticPolicyBuilder>? configureProviders = null)
    {
        CliIo writers = io ?? CliIo.ForConsole();

        // Not a RootCommand: that one is named after the running executable, which is the assembly or a test host
        // rather than the command a user types, and every help page's usage line starts with the root's name. What a
        // RootCommand would add is added here: help, which reaches every verb, and version, which stays on the root.
        Command root = new("semantic-policy", "Evaluates a SemanticPolicy policy against a labelled JSONL dataset.");
        root.Options.Add(new HelpOption());
        root.Options.Add(new VersionOption());

        // In the order they are used, which is the order help lists them: write out the shipped datasets, record once,
        // then read the recording.
        root.Subcommands.Add(SamplesVerb.Build(writers));
        root.Subcommands.Add(RunCommand.Create(writers, configureProviders));
        root.Subcommands.Add(ReportCommand.Create(writers));
        root.Subcommands.Add(SweepVerb.Build(writers));
        root.Subcommands.Add(CompareVerb.Build(writers));
        root.SetAction(_ => WithoutVerb(root, writers));
        return root;
    }

    /// <summary>
    /// Runs a verb's body. An <see cref="EvalsException"/> is the tool telling the user what to fix: its
    /// message goes to <see cref="CliIo.Error"/> and its exit code is returned. Any other exception is a bug
    /// and passes through untouched, so it is reported with its stack rather than as a usage error.
    /// </summary>
    /// <param name="io">Where the message goes.</param>
    /// <param name="body">The verb's work, returning its exit code.</param>
    public static int Guard(CliIo io, Func<int> body)
    {
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            return body();
        }
        catch (EvalsException failure)
        {
            io.Error.WriteLine(failure.Message);
            return failure.ExitCode;
        }
    }

    /// <summary>The asynchronous form of <see cref="Guard"/>, for a verb that awaits.</summary>
    /// <param name="io">Where the message goes.</param>
    /// <param name="body">The verb's work, returning its exit code.</param>
    public static async Task<int> GuardAsync(CliIo io, Func<Task<int>> body)
    {
        ArgumentNullException.ThrowIfNull(io);
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (EvalsException failure)
        {
            await io.Error.WriteLineAsync(failure.Message).ConfigureAwait(false);
            return failure.ExitCode;
        }
    }

    // With no verb there is nothing to do, so the usage text is the error message: the help renderer is
    // pointed at the error stream and the exit code says the invocation was wrong.
    private static int WithoutVerb(Command root, CliIo io)
    {
        root.Parse(["--help"]).Invoke(new InvocationConfiguration { Output = io.Error, Error = io.Error });
        return ExitCodes.UsageOrData;
    }
}
