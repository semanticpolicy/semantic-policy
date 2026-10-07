namespace SemanticPolicy.Mcp.Gateway;

/// <summary>The upstream server's command line, exactly as it followed the first <c>--</c>.</summary>
/// <param name="Command">The program to start.</param>
/// <param name="Arguments">Its arguments, verbatim, including any that look like the gateway's own options.</param>
public sealed record UpstreamCommand(string Command, IReadOnlyList<string> Arguments);
