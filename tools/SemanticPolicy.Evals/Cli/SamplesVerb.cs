using System.CommandLine;

namespace SemanticPolicy.Evals.Cli;

/// <summary>
/// <c>samples</c>: writes the datasets, policies and recordings the tool ships with under a directory, at their paths
/// below <c>datasets/</c> and byte for byte, so each recording still matches its dataset's digest where it lands. It
/// overwrites nothing: when a file it would write exists, it names that file and writes none.
/// </summary>
public static class SamplesVerb
{
    private static readonly Argument<string> _directory = new("dir")
    {
        Description = "The directory to write them under; created when it does not exist.",
    };

    /// <summary>Builds the verb.</summary>
    /// <param name="io">Where it writes.</param>
    public static Command Build(CliIo io)
    {
        ArgumentNullException.ThrowIfNull(io);
        Command command = new("samples", "Write the datasets, policies and recordings the tool ships with under a directory.");
        command.Arguments.Add(_directory);
        command.SetAction(parse => EvalsCli.Guard(io, () => Run(parse.GetRequiredValue(_directory), io)));
        return command;
    }

    private static int Run(string directory, CliIo io)
    {
        // The project file copies the datasets beside the assembly, and the tool package carries them there.
        string shipped = Path.Combine(AppContext.BaseDirectory, "datasets");
        string[] files =
        [
            .. Directory.GetFiles(shipped, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(shipped, file))
                .Order(StringComparer.Ordinal),
        ];

        // Every target is checked before the first is written. Skipping the files that exist would leave a mix of two
        // releases' files, and a recording need not match another release's dataset.
        foreach (string file in files)
        {
            string target = Path.Combine(directory, file);
            if (Path.Exists(target))
            {
                throw new EvalsException($"'{target}' exists, and samples overwrites nothing; write them to another directory.");
            }
        }

        foreach (string file in files)
        {
            string target = Path.Combine(directory, file);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(shipped, file), target, overwrite: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new EvalsException($"Sample '{target}' cannot be written: {e.Message}");
            }

            io.Output.WriteLine(target);
        }

        return ExitCodes.Success;
    }
}
