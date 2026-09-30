using System.CommandLine;
using System.Text.Json;
using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Evals.Tests.Support;

namespace SemanticPolicy.Evals.Tests;

public sealed class RequirementTests
{
    private static readonly string[] _verbs = ["report", "run"];

    // Every file named does not exist: a requirement is read before any file is opened, so a wrong one is the only
    // error, and a right one lets the verb go on to find the policy missing.
    [Theory]
    [InlineData("deny.min-precision=0.95", null)]
    [InlineData("deny.min-precison=0.95", "is not a requirement")]
    [InlineData("deny.min-precision=1.5", "a number from 0 to 1")]
    [InlineData("deny.min-precision=x", "a number from 0 to 1")]
    [InlineData("min-precision=0.9", "min-precision needs a rung")]
    [InlineData("deny.max-abstain=0.1", "max-abstain takes no rung")]
    [InlineData("block.min-recall=0.5", "'block' is not a rung")]
    [InlineData("abstain.min-recall=0.5", "'abstain' is not a rung")]
    public async Task Requirement_Parses_Or_Names_What_Is_Wrong(string requirement, string? wrong)
    {
        string missing = Path.Combine(Path.GetTempPath(), $"semanticpolicy-evals-{Guid.NewGuid():N}");
        string policy = missing + ".policy.json";

        foreach (string verb in _verbs)
        {
            string[] source = verb == "run" ? ["--record", missing + ".recording.jsonl"] : ["--recording", missing + ".recording.jsonl"];
            CliRun run = await CliFixture.InvokeAsync(
                [verb, "--policy", policy, "--dataset", missing + ".jsonl", .. source, "--require", requirement]);

            run.ExitCode.Should().Be(ExitCodes.UsageOrData, verb);
            run.Output.Should().BeEmpty(verb);
            if (wrong is null)
            {
                run.Error.Should().Contain(policy, verb).And.NotContain(requirement, verb);
            }
            else
            {
                run.Error.TrimEnd().Should().StartWith($"--require '{requirement}'", verb).And.Contain(wrong, verb)
                    .And.NotContain(missing, verb);
            }
        }

        File.Exists(missing + ".recording.jsonl").Should().BeFalse();
    }

    // The smoke policy's rule is Boolean with the ladder warn, deny; the router's is a Choice rule.
    [Theory]
    [InlineData("prompt-injection", "escalate.min-recall=0.5", "rule 'prompt-injection' has no escalate rung; its ladder is warn, deny")]
    [InlineData("prompt-injection", "min-accuracy=0.5", "min-accuracy is for a Choice or Score rule, and rule 'prompt-injection' is boolean")]
    [InlineData("support-router", "deny.min-precision=0.5", "min-precision is for a Boolean rule's rung, and rule 'route' is choice")]
    public async Task Requirement_That_Does_Not_Fit_The_Rule_Is_A_Usage_Error(string set, string requirement, string wrong)
    {
        string policy = Path.Combine(Smoke, $"{set}.policy.json");
        string dataset = Path.Combine(Smoke, $"{set}.smoke.jsonl");
        string record = Path.Combine(Path.GetTempPath(), $"semanticpolicy-evals-{Guid.NewGuid():N}.recording.jsonl");
        ScriptedProvider scripted = new ScriptedProvider().Returns(_ => Samples.BooleanAnswer(0.5));

        CliRun report = await InvokeAsync(
            ["report", "--policy", policy, "--dataset", dataset, "--recording", Path.Combine(Smoke, $"{set}.recording.jsonl"), "--require", requirement]);
        CliRun run = await InvokeAsync(
            ["run", "--policy", policy, "--dataset", dataset, "--record", record, "--require", requirement],
            builder => builder.AddProvider(scripted, "local").AddProvider(scripted, "jev"));

        foreach (CliRun refused in new[] { report, run })
        {
            refused.ExitCode.Should().Be(ExitCodes.UsageOrData);
            refused.Output.Should().BeEmpty();
            refused.Error.TrimEnd().Should().Be($"--require '{requirement}': {wrong}.");
        }

        scripted.Calls.Should().Be(0);
        File.Exists(record).Should().BeFalse();
    }

    [Fact]
    public async Task Uncomputable_Metric_Fails_Its_Requirement()
    {
        // Every row is labelled flagged, so no row can be a false positive and the rate has nothing to divide by.
        Policy policy = Samples.Guard(FailureBehavior.Escalate, ["local"], [Samples.Flagged()]);
        using CliFixture fixture = await CliFixture.CreateAsync(
            policy,
            [.. new[] { 0.95, 0.7, 0.3 }.Select(p => FixtureRow.Of("true", null, ("local", Samples.BooleanAnswer(p))))]);

        CliRun run = await fixture.RunAsync("report", "--require", "deny.max-fpr=0.1");

        run.ExitCode.Should().Be(ExitCodes.InfeasibleConstraint, run.Error);
        Lines(run.Output).Should().EndWith("deny.max-fpr=0.1: failed at n/a (0/0)");
        JsonElement entry = fixture.ReadOut().GetProperty("requirements").EnumerateArray().Should().ContainSingle().Subject;
        entry.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
        entry.TryGetProperty("interval", out _).Should().BeFalse();
        entry.GetProperty("passed").GetBoolean().Should().BeFalse();
        entry.GetProperty("warned").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Every_Requirement_Must_Pass()
    {
        (CliRun Result, JsonElement[] Requirements) run = await SmokeReportAsync("prompt-injection", "deny.min-precision=0.95", "deny.min-recall=0.8");

        run.Result.ExitCode.Should().Be(ExitCodes.InfeasibleConstraint, run.Result.Error);
        Lines(run.Result.Output).TakeLast(2).Should().Equal(
            "deny.min-precision=0.95: passed at 0.970 (32/33) [0.847, 0.995]; warning: lower bound 0.847 is below the goal",
            "deny.min-recall=0.8: failed at 0.711 (32/45) [0.566, 0.823]");
        run.Requirements.Select(entry => (entry.GetProperty("requirement").GetString(), entry.GetProperty("passed").GetBoolean()))
            .Should().Equal(("deny.min-precision=0.95", true), ("deny.min-recall=0.8", false));
    }

    [Fact]
    public async Task Choice_Rule_Gates_On_Accuracy_And_Macro_F1()
    {
        // 66 of the router's 72 classified rows are routed as labelled, and its macro-F1 is 0.919. Macro-F1 is not one
        // count over another, so it has no interval and nothing to warn about, passed or failed.
        (CliRun Result, JsonElement[] Requirements) run = await SmokeReportAsync("support-router", "min-accuracy=0.9", "min-macro-f1=0.9", "min-macro-f1=0.95");

        run.Result.ExitCode.Should().Be(ExitCodes.InfeasibleConstraint, run.Result.Error);
        Lines(run.Result.Output).TakeLast(3).Should().Equal(
            "min-accuracy=0.9: passed at 0.917 (66/72) [0.830, 0.961]; warning: lower bound 0.830 is below the goal",
            "min-macro-f1=0.9: passed at 0.919",
            "min-macro-f1=0.95: failed at 0.919");
        run.Requirements.Select(entry => (entry.GetProperty("passed").GetBoolean(), entry.GetProperty("warned").GetBoolean()))
            .Should().Equal((true, true), (true, false), (false, false));
        run.Requirements.Skip(1).Should().AllSatisfy(entry => entry.TryGetProperty("interval", out _).Should().BeFalse());
    }

    private static async Task<(CliRun Result, JsonElement[] Requirements)> SmokeReportAsync(string set, params string[] requirements)
    {
        using TempFile json = TempFile.Write("", ".json");
        CliRun run = await InvokeAsync(
        [
            "report",
            "--policy", Path.Combine(Smoke, $"{set}.policy.json"),
            "--dataset", Path.Combine(Smoke, $"{set}.smoke.jsonl"),
            "--recording", Path.Combine(Smoke, $"{set}.recording.jsonl"),
            "--out", json.Path,
            .. requirements.SelectMany(requirement => new[] { "--require", requirement }),
        ]);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(json.Path));
        return (run, [.. document.RootElement.GetProperty("requirements").EnumerateArray().Select(entry => entry.Clone())]);
    }

    private static string[] Lines(string output) =>
        output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string Smoke => Path.Combine(ExampleDatasetsTests.RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "smoke");

    private static async Task<CliRun> InvokeAsync(string[] args, Action<ISemanticPolicyBuilder>? providers = null)
    {
        StringWriter output = new();
        StringWriter error = new();
        Command root = EvalsCli.Build(new CliIo(output, error), providers);
        int exit = await root.Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false },
            TestContext.Current.CancellationToken);
        return new CliRun(exit, output.ToString(), error.ToString());
    }
}
