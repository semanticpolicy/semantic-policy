namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// What one invocation of the command left behind: its exit code, both streams, and what it handed over to serving,
// which stays null when it never got that far.
internal sealed record GatewayRun(int ExitCode, string Output, string Error, GatewayComposition? Composition, UpstreamCommand? Upstream)
{
    public bool Served => Composition is not null;

    public static async Task<GatewayRun> InvokeAsync(params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        GatewayComposition? composed = null;
        UpstreamCommand? upstream = null;
        int exit = await GatewayCli.RunAsync(
            args,
            output,
            error,
            (composition, command, _) =>
            {
                composed = composition;
                upstream = command;
                return Task.FromResult(ExitCodes.Success);
            },
            TestContext.Current.CancellationToken);
        return new GatewayRun(exit, output.ToString(), error.ToString(), composed, upstream);
    }
}
