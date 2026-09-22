using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Evals.Datasets;

namespace SemanticPolicy.Evals.Tests;

public sealed class ExampleDatasetsTests
{
    // A structural guard: the README's examples would otherwise stop parsing without any runtime symptom.
    [Theory]
    [InlineData("prompt-injection", SplitSource.Metadata, 5, 5, 1)]
    [InlineData("agent-router", SplitSource.None, 10, 10, 0)]
    [InlineData("harm-severity", SplitSource.None, 10, 10, 0)]
    public void Shipped_Example_Datasets_Parse_Against_Their_Policies(
        string name,
        SplitSource source,
        int tuneRows,
        int testRows,
        int ambiguousRows)
    {
        string examples = Path.Combine(RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "examples");
        InputSelection selection = new(
            Path.Combine(examples, $"{name}.policy.json"),
            RuleId: null,
            Path.Combine(examples, $"{name}.jsonl"),
            TunePath: null,
            TestPath: null,
            new SplitNames(),
            []);

        LoadedInputs loaded = Inputs.Load(selection);

        loaded.Selected.Should().HaveCount(10);
        loaded.Splits.Source.Should().Be(source);
        loaded.Splits.Tune.Should().HaveCount(tuneRows);
        loaded.Splits.Test.Should().HaveCount(testRows);
        loaded.Selected.Count(row => row.Label.Kind == RowLabelKind.Ambiguous).Should().Be(ambiguousRows);
        loaded.Policy.Bindings.Select(binding => binding.ProviderId).Should().Equal("local", "jev");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SemanticPolicy.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("No SemanticPolicy.slnx above the test output directory.");
    }
}
