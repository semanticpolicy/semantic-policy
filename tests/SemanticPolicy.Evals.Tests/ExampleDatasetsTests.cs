using System.Text.RegularExpressions;
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

    // A structural guard: the README's examples run on this file, and a set that stopped parsing, or lost its
    // test rows so that every sweep example chose and reported on the same data, would show no other symptom.
    [Fact]
    public void Shipped_Smoke_Set_Parses_Has_About_A_Hundred_Rows_Unique_Ids_Both_Splits_And_Both_Labels()
    {
        string smoke = Path.Combine(RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets", "smoke");
        InputSelection selection = new(
            Path.Combine(smoke, "prompt-injection.policy.json"),
            RuleId: null,
            Path.Combine(smoke, "prompt-injection.smoke.jsonl"),
            TunePath: null,
            TestPath: null,
            new SplitNames(),
            []);

        LoadedInputs loaded = Inputs.Load(selection);

        loaded.Selected.Count.Should().BeInRange(90, 110);
        loaded.Selected.Select(row => row.Id).Should().OnlyHaveUniqueItems();
        loaded.Splits.Source.Should().Be(SplitSource.Metadata);
        foreach (IReadOnlyList<DatasetRow> split in new[] { loaded.Splits.Tune, loaded.Splits.Test })
        {
            split.Select(row => row.Label.Answer).Should().Contain(["true", "false"]);
        }

        loaded.Selected.Should().OnlyContain(row =>
            row.Metadata.ContainsKey("set") && row.Metadata["set"].GetString() == "smoke, not a benchmark");
        loaded.Policy.Bindings.Select(binding => binding.ProviderId).Should().Equal("local", "jev");
    }

    // A structural guard: a real address, key or endpoint committed in a dataset is a content-policy breach with
    // no runtime symptom, so every file under datasets/ is scanned for the shapes one would take.
    [Fact]
    public void Shipped_Datasets_Contain_No_Url_Email_Or_Key_Shaped_Token()
    {
        string datasets = Path.Combine(RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets");
        Regex[] forbidden =
        [
            new(@"\b[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase),
            new(@"\bwww\.", RegexOptions.IgnoreCase),
            new(@"\b[a-z0-9-]+\.(?:com|net|org|io|dev|ai|app|co|uk|pl|de|eu)\b", RegexOptions.IgnoreCase),
            new(@"[a-z0-9._%+-]+@[a-z0-9-]+(?:\.[a-z0-9-]+)+", RegexOptions.IgnoreCase),
            new(@"\b(?:sk|pk|rk)[-_][A-Za-z0-9_-]{16,}"),
            new(@"\bAKIA[0-9A-Z]{16}\b"),
            new(@"\bgh[pousr]_[A-Za-z0-9]{20,}"),
            new(@"\bxox[abpr]-"),
            new(@"\beyJ[A-Za-z0-9_-]{10,}\."),
            new("-----BEGIN"),
            new(@"(?<![A-Za-z0-9+/_-])[A-Za-z0-9+/_-]{40,}"),
        ];

        // By extension: on a case-insensitive file system this folder is also the one that holds the dataset
        // reader's source files.
        string[] files =
        [
            .. Directory.GetFiles(datasets, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetExtension(file) is ".jsonl" or ".json"),
        ];

        files.Should().NotBeEmpty();
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (Regex pattern in forbidden)
            {
                Match match = pattern.Match(text);
                match.Success.Should().BeFalse(
                    $"'{Path.GetRelativePath(datasets, file)}' holds '{match.Value}', which matches {pattern}");
            }
        }
    }

    internal static string RepositoryRoot()
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
