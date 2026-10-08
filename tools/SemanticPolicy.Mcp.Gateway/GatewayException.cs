namespace SemanticPolicy.Mcp.Gateway;

/// <summary>
/// A failure the operator can act on. The message names paths, points, keys and provider names and never a value
/// from a file, because it is printed as it is and lands in the host's log. Anything else that escapes is a bug and
/// is left to surface as one.
/// </summary>
/// <param name="message">What went wrong, without any value from a file.</param>
/// <param name="exitCode">The exit code the process ends with; one of <see cref="ExitCodes"/>.</param>
public sealed class GatewayException(string message, int exitCode = ExitCodes.Configuration) : Exception(message)
{
    /// <summary>The exit code the process ends with.</summary>
    public int ExitCode { get; } = exitCode;
}
