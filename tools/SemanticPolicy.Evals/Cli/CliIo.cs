namespace SemanticPolicy.Evals.Cli;

/// <summary>
/// Where the tool writes: reports to <paramref name="Output"/>, errors and progress to
/// <paramref name="Error"/>. Every write in the tool goes through one of these so a test can capture both;
/// <see cref="ForConsole"/> is the only place the console is named.
/// </summary>
/// <param name="Output">Standard output: the reports a script reads.</param>
/// <param name="Error">Standard error: usage, error messages and progress.</param>
public sealed record CliIo(TextWriter Output, TextWriter Error)
{
    /// <summary>The process's own console streams.</summary>
    public static CliIo ForConsole() => new(Console.Out, Console.Error);
}
