namespace SemanticPolicy.Mcp.Gateway;

/// <summary>
/// The process exit codes, so a host's log or a script can tell a wrong command line from a wrong configuration
/// without reading the text.
/// </summary>
public static class ExitCodes
{
    /// <summary>The gateway did what was asked.</summary>
    public const int Success = 0;

    /// <summary>The command line was wrong: no gateway file, or no upstream command after <c>--</c>.</summary>
    public const int Usage = 1;

    /// <summary>
    /// The gateway file, a policy file or the providers file was wrong, or a provider could not be built from it; the
    /// message says which. Nothing was started.
    /// </summary>
    public const int Configuration = 2;

    /// <summary>
    /// The upstream server could not be started, did not complete the MCP handshake, or ended while the gateway was
    /// serving; the message names the step, never the server's own messages.
    /// </summary>
    public const int UpstreamFailure = 3;

    /// <summary>
    /// The gateway stopped on an error of its own. The message says no more, because an error's text can carry what
    /// passed through the gateway.
    /// </summary>
    public const int Unexpected = 4;
}
