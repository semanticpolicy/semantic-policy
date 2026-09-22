using System.CommandLine;
using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Evals.Datasets;

namespace SemanticPolicy.Evals.Tests;

public sealed class InputsTests
{
    private const string _policy = """
        {"id": "guard", "mode": "shadow",
         "rules": [{"id": "prompt-injection", "question": "question-b", "flaggedAnswer": true, "ladder": ["deny"], "type": "boolean"}],
         "bindings": [{"providerId": "local", "operatingPoints": [{"ruleId": "prompt-injection",
           "thresholds": [{"verdict": "deny", "kind": "score", "atOrAbove": 0.8}], "gate": {"kind": "score", "below": 0.1}}]}],
         "onFailure": {"action": "fallback", "then": "deny"}}
        """;

    [Theory]
    [InlineData("--dataset d.jsonl --tune t.jsonl")]
    [InlineData("--dataset d.jsonl --test t.jsonl")]
    [InlineData("--tune t.jsonl")]
    public async Task Input_Selection_Rejects_Dataset_With_Tune_Or_Test_And_Tune_Without_Test(string files)
    {
        StringWriter output = new();
        StringWriter error = new();
        CliIo io = new(output, error);
        RootCommand root = EvalsCli.Build(io);
        Command probe = new("probe");
        foreach (Option option in SharedOptions.InputOptions)
        {
            probe.Options.Add(option);
        }

        probe.SetAction(parseResult => EvalsCli.Guard(io, () =>
        {
            InputSelection.From(parseResult);
            return ExitCodes.Success;
        }));
        root.Subcommands.Add(probe);
        InvocationConfiguration configuration = new()
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
        };

        int exitCode = await root.Parse(["probe", "--policy", "p.json", .. files.Split(' ')])
            .InvokeAsync(configuration, TestContext.Current.CancellationToken);

        exitCode.Should().Be(ExitCodes.UsageOrData);
        error.ToString().Should().NotBeEmpty();
        output.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Filter_Applies_Before_Splits_So_Both_Halves_Stay_Disjoint()
    {
        using TempFile policy = TempFile.Write(_policy, ".json");
        using TempFile dataset = TempFile.Write(
            """{"id": "a", "input": "x", "label": true, "metadata": {"split": "tune", "source": "synthetic"}}""" + "\n" +
            """{"id": "b", "input": "x", "label": false, "metadata": {"split": "test", "source": "synthetic"}}""" + "\n" +
            """{"id": "c", "input": "x", "label": true, "metadata": {"split": "tune", "source": "collected"}}""" + "\n" +
            """{"id": "d", "input": "x", "label": false, "metadata": {"split": "test", "source": "collected"}}""" + "\n");
        InputSelection selection = new(
            policy.Path,
            RuleId: null,
            dataset.Path,
            TunePath: null,
            TestPath: null,
            new SplitNames(),
            [MetadataFilter.Parse("metadata.source=synthetic")]);

        LoadedInputs loaded = Inputs.Load(selection);

        loaded.RowsBeforeFilter.Should().Be(4);
        loaded.Selected.Select(row => row.Id).Should().Equal("a", "b");
        loaded.Splits.Source.Should().Be(SplitSource.Metadata);
        loaded.Splits.Tune.Select(row => row.Id).Should().Equal("a");
        loaded.Splits.Test.Select(row => row.Id).Should().Equal("b");
        loaded.Rule.Id.Should().Be("prompt-injection");
        loaded.Datasets.Should().ContainSingle().Which.Path.Should().Be(dataset.Path);
    }
}
