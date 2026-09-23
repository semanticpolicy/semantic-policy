using System.CommandLine;
using SemanticPolicy.Evals.Cli;

namespace SemanticPolicy.Evals.Tests;

public sealed class EvalsCliTests
{
    [Fact]
    public async Task Cli_Without_A_Verb_Prints_Usage_To_Error_And_Exits_1()
    {
        StringWriter output = new();
        StringWriter error = new();
        RootCommand root = EvalsCli.Build(new CliIo(output, error));

        int exitCode = await root.Parse([]).InvokeAsync(
            new InvocationConfiguration { Output = output, Error = error },
            TestContext.Current.CancellationToken);

        exitCode.Should().Be(ExitCodes.UsageOrData);
        error.ToString().Should().Contain("Usage");
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Cli_Help_Writes_Usage_To_Output_And_Exits_0()
    {
        StringWriter output = new();
        StringWriter error = new();
        RootCommand root = EvalsCli.Build(new CliIo(output, error));

        int exitCode = await root.Parse(["--help"]).InvokeAsync(
            new InvocationConfiguration { Output = output, Error = error },
            TestContext.Current.CancellationToken);

        exitCode.Should().Be(ExitCodes.Success);
        output.ToString().Should().Contain("Usage");
        error.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task Cli_Help_Lists_The_Commands_In_The_Order_They_Are_Used()
    {
        string help = await HelpAsync();

        string[] lines = help.ReplaceLineEndings("\n").Split('\n');
        int start = Array.IndexOf(lines, "Commands:") + 1;
        int end = Array.FindIndex(lines, start, string.IsNullOrWhiteSpace);
        lines[start..(end < 0 ? lines.Length : end)].Select(line => line.Trim().Split(' ')[0]).Should()
            .Equal("run", "report", "sweep", "compare");
    }

    [Theory]
    [InlineData("report")]
    [InlineData("sweep")]
    [InlineData("compare")]
    public async Task Cli_Help_Marks_The_Recording_Required_On_Every_Command_That_Reads_One(string verb)
    {
        string help = await HelpAsync(verb);

        help.ReplaceLineEndings("\n").Split('\n').Should().ContainSingle(line => line.Contains("--recording", StringComparison.Ordinal))
            .Which.Should().Contain("(REQUIRED)");
    }

    [Theory]
    [InlineData(ExitCodes.UsageOrData)]
    [InlineData(ExitCodes.InfeasibleConstraint)]
    public async Task Cli_Maps_An_Evals_Error_To_Its_Exit_Code_And_Writes_The_Message_To_Error(int exitCode)
    {
        StringWriter output = new();
        StringWriter error = new();
        CliIo io = new(output, error);
        RootCommand root = EvalsCli.Build(io);
        Command probe = new("probe");
        probe.SetAction(_ => EvalsCli.Guard(io, () => throw new EvalsException("probe-failure", exitCode)));
        root.Subcommands.Add(probe);

        InvocationConfiguration configuration = new()
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
        };
        int actual = await root.Parse(["probe"]).InvokeAsync(configuration, TestContext.Current.CancellationToken);

        actual.Should().Be(exitCode);
        error.ToString().Should().Contain("probe-failure");
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Cli_Guard_Does_Not_Swallow_Other_Exceptions()
    {
        StringWriter error = new();
        CliIo io = new(new StringWriter(), error);

        Action act = () => EvalsCli.Guard(io, () => throw new InvalidOperationException("not-an-evals-error"));

        act.Should().Throw<InvalidOperationException>();
        error.ToString().Should().BeEmpty();
    }

    private static async Task<string> HelpAsync(params string[] verb)
    {
        StringWriter output = new();
        StringWriter error = new();
        RootCommand root = EvalsCli.Build(new CliIo(output, error));

        int exitCode = await root.Parse([.. verb, "--help"]).InvokeAsync(
            new InvocationConfiguration { Output = output, Error = error },
            TestContext.Current.CancellationToken);

        exitCode.Should().Be(ExitCodes.Success);
        return output.ToString();
    }
}
