namespace SemanticPolicy.Evals;

/// <summary>
/// A failure the user can act on: an option, a file, a row, a policy or a constraint. The message names
/// paths, ids and line numbers and never a row's input, because it is printed as it is. Anything else that
/// escapes a verb is a bug and is left to surface as one.
/// </summary>
/// <param name="message">What went wrong, without any content from a dataset row.</param>
/// <param name="exitCode">The exit code the process ends with; one of <see cref="ExitCodes"/>.</param>
public sealed class EvalsException(string message, int exitCode = ExitCodes.UsageOrData) : Exception(message)
{
    /// <summary>The exit code the process ends with.</summary>
    public int ExitCode { get; } = exitCode;
}
