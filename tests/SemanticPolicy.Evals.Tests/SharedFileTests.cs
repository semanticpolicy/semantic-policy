using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

public sealed class SharedFileTests
{
    [Theory]
    [InlineData("report")]
    [InlineData("sweep")]
    [InlineData("compare")]
    public async Task Read_Verbs_Refuse_To_Write_Over_An_Input_And_Leave_It_Unchanged(string verb)
    {
        using CliFixture fixture = await CreateFixtureAsync();
        byte[] before = await File.ReadAllBytesAsync(fixture.PolicyPath, TestContext.Current.CancellationToken);

        CliRun run = await CliFixture.InvokeAsync(
        [
            verb,
            "--policy", fixture.PolicyPath,
            "--dataset", fixture.DatasetPath,
            "--recording", fixture.RecordingPath,
            "--out", fixture.PolicyPath,
        ]);

        run.ExitCode.Should().Be(ExitCodes.UsageOrData, run.Output);
        run.Error.Should().Contain("--out").And.Contain("the --policy file");
        (await File.ReadAllBytesAsync(fixture.PolicyPath, TestContext.Current.CancellationToken)).Should().Equal(before);
    }

    [Fact]
    public async Task Report_Refuses_Two_Outputs_With_One_Path_Before_Writing_Either()
    {
        using CliFixture fixture = await CreateFixtureAsync();

        CliRun run = await CliFixture.InvokeAsync(
        [
            "report",
            "--policy", fixture.PolicyPath,
            "--dataset", fixture.DatasetPath,
            "--recording", fixture.RecordingPath,
            "--out", fixture.OutPath,
            "--diagram", fixture.OutPath,
        ]);

        run.ExitCode.Should().Be(ExitCodes.UsageOrData, run.Output);
        run.Error.Should().Contain("--diagram").And.Contain("the --out file");
        File.Exists(fixture.OutPath).Should().BeFalse();
    }

    [Fact]
    public async Task Run_Refuses_To_Record_Over_An_Input_And_Leaves_It_Unchanged()
    {
        using CliFixture fixture = await CreateFixtureAsync();
        byte[] before = await File.ReadAllBytesAsync(fixture.PolicyPath, TestContext.Current.CancellationToken);

        CliRun run = await CliFixture.InvokeAsync(
        [
            "run",
            "--policy", fixture.PolicyPath,
            "--dataset", fixture.DatasetPath,
            "--record", fixture.PolicyPath,
        ]);

        run.ExitCode.Should().Be(ExitCodes.UsageOrData, run.Output);
        run.Error.Should().Contain("--record").And.Contain("the --policy file");
        (await File.ReadAllBytesAsync(fixture.PolicyPath, TestContext.Current.CancellationToken)).Should().Equal(before);
    }

    private static Task<CliFixture> CreateFixtureAsync() =>
        CliFixture.CreateAsync(
            Samples.Guard(FailureBehavior.Deny, ["local"], [Samples.Flagged()]),
            [FixtureRow.Of("true", null, ("local", Samples.BooleanAnswer(0.9)))]);
}
