using System.Collections;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using SemanticPolicy.Mcp.Gateway.Tests.Support;
using static SemanticPolicy.Mcp.Gateway.Tests.Support.GatewayHarness;

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed class UpstreamFailureTests
{
    private const string _advice = "Run its command on its own to see its messages.";

    [Theory]
    [InlineData("a command that does not exist")]
    [InlineData("an upstream pinned to 2025-11-25")]
    public async Task Upstream_That_Fails_To_Start_Stops_The_Gateway_Before_It_Serves(string upstream)
    {
        bool missing = upstream == "a command that does not exist";
        UpstreamCommand command = missing
            ? new UpstreamCommand("semantic-policy-missing-server-3e9d", ["--token", Samples.Canary])
            : new UpstreamCommand(Command, []);
        McpServerOptions options = new ToolsUpstream([ToolsUpstream.Lookup]).Options();
        if (!missing)
        {
            // It answers the handshake with the newer revision, which the gateway's pinned client refuses.
            options.ProtocolVersion = "2025-11-25";
        }

        await using GatewayHarness gateway = Start(
            options,
            missing
                ? harness => new StdioClientTransport(
                    UpstreamStart.Options(command, [], Environment.GetEnvironmentVariables(), harness.Log))
                : null,
            command.Command);

        (await gateway.Gateway.WaitAsync(gateway.Deadline)).Should().Be(ExitCodes.UpstreamFailure);
        (await gateway.HostReceivedAnythingAsync()).Should().BeFalse();
        string log = gateway.Log.ToString();
        log.Should().NotContain(Samples.Canary);

        // On Windows the transport starts every command through cmd.exe, which starts, finds no such command and exits
        // during the handshake.
        if (missing && !OperatingSystem.IsWindows())
        {
            log.Should().Be($"The upstream server '{command.Command}' could not be started. {_advice}{Environment.NewLine}");
        }
        else if (missing)
        {
            log.Should().StartWith($"The upstream server '{command.Command}' did not complete the MCP handshake")
                .And.EndWith($". {_advice}{Environment.NewLine}");
        }
        else
        {
            log.Should().Be($"The upstream server '{Command}' did not complete the MCP handshake. {_advice}{Environment.NewLine}");
        }
    }

    [Theory]
    [InlineData("a line on the child's stderr")]
    [InlineData("the start fails")]
    [InlineData("the handshake fails")]
    [InlineData("the upstream ends while the gateway serves")]
    [InlineData("the child exits while the gateway serves")]
    public async Task Upstream_Stderr_And_Failure_Details_Never_Reach_The_Gateways_Stderr(string failure)
    {
        if (failure == "a line on the child's stderr")
        {
            StringWriter log = new();
            StdioClientTransportOptions start = UpstreamStart.Options(new UpstreamCommand(Command, []), [], new Hashtable(), log);

            start.StandardErrorLines?.Invoke(Samples.Canary);

            log.ToString().Should().BeEmpty();
            return;
        }

        McpServerOptions options = new ToolsUpstream([ToolsUpstream.Lookup]).Options();
        string expected;
        GatewayHarness gateway;
        switch (failure)
        {
            case "the start fails":
                gateway = Start(options, harness =>
                {
                    harness.UpstreamSide.ConnectFailure = CarriesTheCanary();
                    return harness.UpstreamSide;
                });
                expected = "could not be started";
                break;
            case "the handshake fails":
                gateway = Start(options, harness =>
                {
                    harness.UpstreamSide.SendFailure = CarriesTheCanary();
                    return harness.UpstreamSide;
                });
                expected = "did not complete the MCP handshake";
                break;
            case "the upstream ends while the gateway serves":
                gateway = await ConnectAsync(options);
                gateway.UpstreamSide.Fail(CarriesTheCanary());
                expected = "ended while the gateway was serving";
                break;
            default:
                // As the stdio transport ends a session whose child exits: the exit code, the child's last stderr lines
                // and the exception that carries them.
                gateway = await ConnectAsync(options);
                gateway.UpstreamSide.Fail(new ClientTransportClosedException(new StdioClientCompletionDetails
                {
                    ExitCode = 7,
                    ProcessId = 4242,
                    StandardErrorTail = [Samples.Canary],
                    Exception = CarriesTheCanary(),
                }));
                expected = "ended while the gateway was serving (exit code 7)";
                break;
        }

        await using (gateway)
        {
            (await gateway.Gateway.WaitAsync(gateway.Deadline)).Should().Be(ExitCodes.UpstreamFailure);
            gateway.Log.ToString().Should().Be($"The upstream server '{Command}' {expected}. {_advice}{Environment.NewLine}");
            gateway.Log.ToString().Should().NotContain(Samples.Canary);
        }
    }

    // A variable spelled in another case is the same variable on Windows, and another one elsewhere.
    [Theory]
    [InlineData("OPENROUTER_API_KEY")]
    [InlineData("openrouter_api_key")]
    public void Upstream_Child_Starts_Without_Any_Provider_Key_Variable(string spelling)
    {
        Hashtable environment = new()
        {
            ["PATH"] = "path-a",
            ["HOME"] = "home-a",
            [spelling] = Samples.Canary,
            ["TYPESAFE_API_KEY"] = Samples.Canary,
        };
        UpstreamCommand command = new("upstream-server", ["--port", "7", "--token=a b"]);

        StdioClientTransportOptions start = UpstreamStart.Options(
            command,
            ["OPENROUTER_API_KEY", "TYPESAFE_API_KEY"],
            environment,
            TextWriter.Null);

        Dictionary<string, string?> expected = new() { ["PATH"] = "path-a", ["HOME"] = "home-a" };
        if (spelling != "OPENROUTER_API_KEY" && !OperatingSystem.IsWindows())
        {
            expected[spelling] = Samples.Canary;
        }

        start.InheritEnvironmentVariables.Should().BeFalse();
        start.EnvironmentVariables.Should().BeEquivalentTo(expected);
        start.Command.Should().Be("upstream-server");
        start.Arguments.Should().Equal("--port", "7", "--token=a b");
    }

    // The way the SDK's exception for a child that exited carries its last stderr lines: in its message, and in the
    // message of the exception inside it.
    private static IOException CarriesTheCanary() =>
        new($"The server exited: {Samples.Canary}", new InvalidOperationException(Samples.Canary));
}
