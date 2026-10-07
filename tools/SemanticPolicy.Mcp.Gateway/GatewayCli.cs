using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace SemanticPolicy.Mcp.Gateway;

/// <summary>The command line of the tool, and the error boundary its work runs inside.</summary>
public static class GatewayCli
{
    private const string _name = "semantic-policy-mcp";
    private const string _separator = "--";
    private const string _synopsis = $"{_name} --gateway <path> -- <command> [<arg>...]";
    private const string _usage = $"Usage: {_synopsis}";

    /// <summary>
    /// Parses the command line, composes what the gateway file configures, and hands the composition and the upstream
    /// command to <paramref name="serve"/>. Nothing is handed over unless the whole configuration is sound.
    /// </summary>
    /// <param name="args">The gateway's own options, then <c>--</c> and the upstream server's command line.</param>
    /// <param name="output">Where help and the version go.</param>
    /// <param name="error">Where a refusal goes, as one message.</param>
    /// <param name="serve">Serves the composition in front of the upstream command and returns the exit code.</param>
    /// <param name="cancellationToken">Passed to <paramref name="serve"/>.</param>
    /// <returns>One of <see cref="ExitCodes"/>, or what <paramref name="serve"/> returns.</returns>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        Func<GatewayComposition, UpstreamCommand, CancellationToken, Task<int>> serve,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(serve);

        // Split here rather than by the parser, so nothing after the first separator is the gateway's: not an option
        // such as --help, not a second separator, not an @file the parser would expand.
        int separator = args.ToList().IndexOf(_separator);
        string[] own = separator < 0 ? [.. args] : [.. args.Take(separator)];
        string[] upstream = separator < 0 ? [] : [.. args.Skip(separator + 1)];

        Option<string> gateway = new("--gateway")
        {
            Description = "The gateway file: the providers file, and each screening point's policy and actions.",
            HelpName = "path",
            Required = true,
        };
        Command root = new(
            _name,
            $"Runs an MCP server behind SemanticPolicy: screens its tool results and tool definitions with the gateway file's policies. The server's command line follows --: {_synopsis}");
        root.Options.Add(gateway);
        root.Options.Add(new HelpOption());
        root.Options.Add(new VersionOption());

        // The root has no action of its own, so any action left after parsing is help or the version.
        ParseResult parsed = root.Parse(own);
        if (parsed.Action is not null and not ParseErrorAction)
        {
            return await parsed
                .InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken)
                .ConfigureAwait(false);
        }

        List<string> problems = [.. parsed.Errors.Select(problem => problem.Message)];
        if (parsed.Errors.Count == 0 && string.IsNullOrWhiteSpace(parsed.GetValue(gateway)))
        {
            problems.Add("Option '--gateway' is empty; give the gateway file's path.");
        }

        if (upstream.Length == 0)
        {
            problems.Add($"The upstream command is missing; give the MCP server's command line after {_separator}.");
        }

        if (problems.Count > 0)
        {
            problems.Add(_usage);
            await error.WriteLineAsync(string.Join(Environment.NewLine, problems)).ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        try
        {
            GatewayComposition composition = GatewayComposition.Compose(parsed.GetRequiredValue(gateway));
            return await serve(composition, new UpstreamCommand(upstream[0], upstream[1..]), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GatewayException failure)
        {
            // What the operator can fix: the message alone, which names no value from a file. Any other exception is
            // a bug and passes through with its stack.
            await error.WriteLineAsync(failure.Message).ConfigureAwait(false);
            return failure.ExitCode;
        }
    }
}
