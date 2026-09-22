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
}
