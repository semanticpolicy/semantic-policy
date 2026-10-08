using System.Collections;
using ModelContextProtocol.Client;

namespace SemanticPolicy.Mcp.Gateway;

/// <summary>
/// How the upstream server's process is started: its command line as given, and the gateway's environment without
/// the variables that hold the providers' keys.
/// </summary>
public static class UpstreamStart
{
    /// <summary>Builds the stdio transport's options for the upstream server.</summary>
    /// <param name="upstream">The command line after <c>--</c>, passed on verbatim.</param>
    /// <param name="keyVariables">The variables the providers file names as holding a key; the child gets none of them.</param>
    /// <param name="environment">The gateway's environment, as <see cref="Environment.GetEnvironmentVariables()"/> returns it.</param>
    /// <param name="log">
    /// The gateway's stderr, which the child's stderr never reaches: each line the child writes there is read and
    /// dropped, because a server can print a result, its arguments or a key there.
    /// </param>
    public static StdioClientTransportOptions Options(
        UpstreamCommand upstream,
        IReadOnlyList<string> keyVariables,
        IDictionary environment,
        TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(keyVariables);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(log);

        // Names compare as the operating system compares them, so a key spelled in another case on Windows, which is
        // the same variable there, is left out as well.
        HashSet<string> keys = new(keyVariables, VariableComparer);
        Dictionary<string, string?> inherited = new(VariableComparer);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string name && !keys.Contains(name))
            {
                inherited[name] = entry.Value as string;
            }
        }

        // The child gets this dictionary and nothing else: an entry that removes a variable from an inherited
        // environment is not something to rely on.
        return new StdioClientTransportOptions
        {
            Command = upstream.Command,
            Arguments = [.. upstream.Arguments],
            InheritEnvironmentVariables = false,
            EnvironmentVariables = inherited,
            StandardErrorLines = static _ => { },
        };
    }

    private static StringComparer VariableComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
