using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Evals.Tests.Support;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

// The default recording path is relative to the process's current directory, which one test here moves;
// nothing else may run while it does.
[CollectionDefinition(nameof(CurrentDirectoryCollection), DisableParallelization = true)]
public sealed class CurrentDirectoryCollection;

[Collection(nameof(CurrentDirectoryCollection))]
public sealed partial class RunVerbTests
{
    private const string _marker = "INPUT-MARKER-5b2e";

    [Fact]
    public async Task Run_Fails_Before_Any_Call_When_A_Binding_Names_An_Unregistered_Provider_Listing_The_Names()
    {
        using Fixture fixture = Fixture.Create(Guard());
        ScriptedProvider scripted = Answering();

        (int partly, _, string partlyError) =
            await InvokeAsync(fixture.RunArgs(), builder => builder.AddProvider(scripted, "local"));
        (int none, _, string noneError) = await InvokeAsync(fixture.RunArgs(), providers: null);

        partly.Should().Be(ExitCodes.UsageOrData);
        partlyError.Should().Contain("'jev'").And.Contain("registered providers: local");
        none.Should().Be(ExitCodes.UsageOrData);
        noneError.Should().Contain("registered providers: none");
        scripted.Calls.Should().Be(0);
        File.Exists(fixture.RecordingPath).Should().BeFalse();
    }

    [Fact]
    public async Task Run_Writes_A_Recording_Then_Prints_The_Same_Report_As_Report_Does_On_It()
    {
        using Fixture fixture = Fixture.Create(Guard());
        ScriptedProvider scripted = Answering();

        (int runExit, string runOutput, string runError) = await InvokeAsync(
            fixture.RunArgs(),
            builder => builder.AddProvider(scripted, "local").AddProvider(scripted, "jev"));
        (int reportExit, string reportOutput, string reportError) = await InvokeAsync(
            ["report", .. fixture.InputArgs(), "--recording", fixture.RecordingPath],
            providers: null);

        runExit.Should().Be(ExitCodes.Success, runError);
        reportExit.Should().Be(ExitCodes.Success, reportError);
        scripted.Calls.Should().Be(12);
        runOutput.Should().Contain("rung deny:").And.Be(reportOutput);
        runError.Should().Contain("row 6 of 6");
    }

    [Fact]
    public async Task Run_Says_The_Policy_Budget_Is_Ignored_When_The_Policy_Has_One()
    {
        using Fixture fixture = Fixture.Create(Guard(budget: TimeSpan.FromSeconds(5)));
        ScriptedProvider scripted = Answering();

        (int exit, string output, string error) = await InvokeAsync(
            fixture.RunArgs(),
            builder => builder.AddProvider(scripted, "local").AddProvider(scripted, "jev"));

        exit.Should().Be(ExitCodes.Success, error);
        error.Should().Contain("policy budget ignored: run is eager");
        output.Should().NotContain("policy budget ignored");
    }

    public static TheoryData<string> Layouts => ["one dataset", "two files"];

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task Run_Without_Record_Writes_The_Recording_At_The_Default_Path_Named_After_The_Dataset_And_Policy(string layout)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("semanticpolicy-evals-");
        string previous = Environment.CurrentDirectory;
        try
        {
            string policy = Path.Combine(directory.FullName, "policy.json");
            File.WriteAllText(policy, JsonSerializer.Serialize(Guard(), SemanticPolicyJson.Options));
            string[] inputs;
            string expected;
            if (layout == "one dataset")
            {
                string rows = Path.Combine(directory.FullName, "rows.jsonl");
                File.WriteAllText(rows, Dataset(1, 2, 3));
                inputs = ["--dataset", rows];
                expected = "rows.guard.recording.jsonl";
            }
            else
            {
                string tune = Path.Combine(directory.FullName, "tune-half.jsonl");
                string test = Path.Combine(directory.FullName, "test-half.jsonl");
                File.WriteAllText(tune, Dataset(1, 2));
                File.WriteAllText(test, Dataset(3));
                inputs = ["--tune", tune, "--test", test];
                expected = "tune-half.guard.recording.jsonl";
            }

            Environment.CurrentDirectory = directory.FullName;
            ScriptedProvider scripted = Answering();
            (int exit, string output, string error) = await InvokeAsync(
                ["run", "--policy", policy, .. inputs],
                builder => builder.AddProvider(scripted, "local").AddProvider(scripted, "jev"));

            exit.Should().Be(ExitCodes.Success, error);
            File.Exists(Path.Combine(directory.FullName, expected)).Should().BeTrue();
            output.Should().Contain($"recording ./{expected}");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Nothing_The_Cli_Prints_Or_Records_Contains_A_Row_Input()
    {
        // Content leaking into a terminal, a CI log or a committed recording has no runtime symptom, so the
        // marker in every row's input is looked for everywhere the tool writes. The provider even hands the
        // request back as its raw output, which must stay in memory.
        using Fixture fixture = Fixture.Create(Guard());
        using TempFile runJson = TempFile.Write("", ".json");
        using TempFile reportJson = TempFile.Write("", ".json");
        ScriptedProvider scripted = new ScriptedProvider().Returns(request =>
            Samples.BooleanAnswer(PTrue(request)) with { Raw = request.Context });

        (int runExit, string runOutput, string runError) = await InvokeAsync(
            [.. fixture.RunArgs(), "--out", runJson.Path],
            builder => builder.AddProvider(scripted, "local").AddProvider(scripted, "jev"));
        (int reportExit, string reportOutput, string reportError) = await InvokeAsync(
            ["report", .. fixture.InputArgs(), "--recording", fixture.RecordingPath, "--out", reportJson.Path],
            providers: null);

        runExit.Should().Be(ExitCodes.Success, runError);
        reportExit.Should().Be(ExitCodes.Success, reportError);
        File.ReadAllText(fixture.DatasetPath).Should().Contain(_marker, "the guard is only as good as its marker");
        string[] everything =
        [
            runOutput, runError, reportOutput, reportError,
            File.ReadAllText(fixture.RecordingPath), File.ReadAllText(runJson.Path), File.ReadAllText(reportJson.Path),
        ];
        everything.Should().AllSatisfy(written => written.Should().NotContain(_marker));
    }

    // Six rows whose inputs carry the marker and the probability the scripted provider answers with.
    private static readonly (string Label, double PTrue)[] _rows =
        [("true", 0.95), ("true", 0.7), ("true", 0.3), ("false", 0.1), ("false", 0.65), ("false", 0.92)];

    private static Policy Guard(TimeSpan? budget = null) =>
        Samples.Guard(FailureBehavior.Fallback(Verdict.Escalate), ["local", "jev"], [Samples.Flagged()], budget: budget);

    private static ScriptedProvider Answering() =>
        new ScriptedProvider().Returns(request => Samples.BooleanAnswer(PTrue(request)));

    private static double PTrue(DecisionRequest request) =>
        double.Parse(Probability().Match(ScriptedProvider.TextOf(request)).Groups["p"].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"p=(?<p>[0-9.]+)")]
    private static partial Regex Probability();

    private static string Dataset(params int[] rows) =>
        string.Concat(rows.Select(row => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = $"r{row}",
            ["input"] = FormattableString.Invariant($"{_marker} p={_rows[row - 1].PTrue}"),
            ["label"] = bool.Parse(_rows[row - 1].Label),
        }) + "\n"));

    private static async Task<(int Exit, string Output, string Error)> InvokeAsync(
        string[] args,
        Action<ISemanticPolicyBuilder>? providers)
    {
        StringWriter output = new();
        StringWriter error = new();
        RootCommand root = EvalsCli.Build(new CliIo(output, error), providers);
        int exit = await root.Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false },
            TestContext.Current.CancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempFile _policy;
        private readonly TempFile _dataset;

        private Fixture(TempFile policy, TempFile dataset)
        {
            _policy = policy;
            _dataset = dataset;
            RecordingPath = Path.Combine(Path.GetTempPath(), $"semanticpolicy-evals-{Guid.NewGuid():N}.recording.jsonl");
        }

        public string DatasetPath => _dataset.Path;

        public string RecordingPath { get; }

        public static Fixture Create(Policy policy) =>
            new(
                TempFile.Write(JsonSerializer.Serialize(policy, SemanticPolicyJson.Options), ".json"),
                TempFile.Write(Dataset(1, 2, 3, 4, 5, 6)));

        public string[] InputArgs() => ["--policy", _policy.Path, "--dataset", _dataset.Path];

        public string[] RunArgs() => ["run", .. InputArgs(), "--record", RecordingPath];

        public void Dispose()
        {
            _policy.Dispose();
            _dataset.Dispose();
            File.Delete(RecordingPath);
        }
    }
}
