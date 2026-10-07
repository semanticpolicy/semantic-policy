using SemanticPolicy.Mcp.Gateway.Tests.Support;

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("no --gateway", "--gateway")]
    [InlineData("nothing after --", "upstream command")]
    [InlineData("no -- at all", "upstream command")]
    public async Task Command_Line_Without_A_Gateway_File_Or_An_Upstream_Command_Is_Refused(string defect, string named)
    {
        using Workspace workspace = Workspace.Create();
        string gateway = workspace.Write("gateway.json", "{ }");
        string[] args = defect switch
        {
            "no --gateway" => ["--", "upstream-server", "--stdio"],
            "nothing after --" => ["--gateway", gateway, "--"],
            "no -- at all" => ["--gateway", gateway, "upstream-server"],
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        GatewayRun run = await GatewayRun.InvokeAsync(args);

        run.ExitCode.Should().Be(ExitCodes.Usage);
        run.Error.Should().Contain(named);
        run.Output.Should().BeEmpty();
        run.Served.Should().BeFalse();
    }

    // Options of the gateway's own, a second separator and help: none of them is the gateway's once past the first --.
    [Fact]
    public async Task Upstream_Command_Line_After_The_Separator_Is_Kept_Verbatim()
    {
        using Workspace workspace = Workspace.Create();
        string gateway = workspace.Write("gateway.json", "{ }");
        string[] upstream = ["node", "server.js", "--port", "3", "-v", "--", "--gateway", "other.json", "--help"];

        GatewayRun run = await GatewayRun.InvokeAsync(["--gateway", gateway, "--", .. upstream]);

        run.ExitCode.Should().Be(ExitCodes.Success, run.Error);
        run.Upstream!.Command.Should().Be("node");
        run.Upstream.Arguments.Should().Equal(upstream[1..]);
        run.Output.Should().BeEmpty();
    }
}
