using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using SemanticPolicy.Mcp.Gateway;

// stdout carries the host's session from here on, and stderr only the gateway's own words: the runtime would print an
// unhandled exception's ToString() there, and the SDK's exceptions carry the upstream server's last stderr lines.
try
{
    return await GatewayCli.RunAsync(
        args,
        Console.Out,
        Console.Error,
        (composition, upstream, cancellationToken) => GatewayProxy.RunAsync(
            upstream.Command,
            new StdioServerTransport("semantic-policy-mcp"),
            new StdioClientTransport(UpstreamStart.Options(
                upstream,
                composition.KeyVariables,
                Environment.GetEnvironmentVariables(),
                Console.Error)),
            composition,
            Console.Error,
            Environment.GetEnvironmentVariables(),
            cancellationToken));
}
catch (Exception)
{
    await Console.Error.WriteLineAsync("The gateway stopped on an unexpected error.");
    return ExitCodes.Unexpected;
}
