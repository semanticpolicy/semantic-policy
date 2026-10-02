namespace SemanticPolicy.Evals.Cli;

internal static class CliFiles
{
    // A verb never replaces a file it reads, and each output needs a path of its own. Check this before the verb
    // opens any of the named files.
    public static void RefuseSharedFiles(
        string verb,
        IEnumerable<(string Option, string? Path)> inputs,
        IEnumerable<(string Option, string? Path)> outputs)
    {
        List<(string Option, string Path, bool Read)> taken =
        [
            .. inputs
                .Where(input => !string.IsNullOrWhiteSpace(input.Path))
                .Select(input => (input.Option, input.Path!, true)),
        ];
        foreach ((string option, string? path) in outputs)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            foreach ((string other, string otherPath, bool read) in taken)
            {
                if (SamePath(path, otherPath))
                {
                    throw new EvalsException(read
                        ? $"{option} '{path}' is the {other} file; {verb} writes new files and never rewrites one "
                            + "it reads. Name another file."
                        : $"{option} '{path}' is also the {other} file; each file {verb} writes needs a name of "
                            + "its own. Name another file.");
                }
            }

            taken.Add((option, path, false));
        }
    }

    // Windows and macOS file systems ignore case by default, so two spellings of one file must compare equal there.
    private static bool SamePath(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
