using System.CommandLine;
using SemanticPolicy.Evals.Datasets;

namespace SemanticPolicy.Evals.Cli;

/// <summary>
/// The options every verb spells the same way, defined once so the README, the tests and the verbs cannot
/// disagree. A verb adds the members it reads; <see cref="InputOptions"/> are the ones
/// <see cref="InputSelection.From"/> reads.
/// </summary>
public static class SharedOptions
{
    /// <summary><c>--policy &lt;file&gt;</c>: the policy every verb starts from.</summary>
    public static Option<string> Policy { get; } = new("--policy")
    {
        Description = "The policy file, in the library's JSON format.",
        HelpName = "file",
        Required = true,
    };

    /// <summary><c>--dataset &lt;file&gt;</c>: one dataset, split by metadata.split when it carries one.</summary>
    public static Option<string?> Dataset { get; } = new("--dataset")
    {
        Description = "One dataset; tune and test halves come from metadata.split, when every row has one.",
        HelpName = "file",
    };

    /// <summary><c>--tune &lt;file&gt;</c>: the tune half as its own file; goes with <c>--test</c>.</summary>
    public static Option<string?> Tune { get; } = new("--tune")
    {
        Description = "The tune dataset; goes with --test and excludes --dataset.",
        HelpName = "file",
    };

    /// <summary><c>--test &lt;file&gt;</c>: the test half as its own file; goes with <c>--tune</c>.</summary>
    public static Option<string?> Test { get; } = new("--test")
    {
        Description = "The test dataset; goes with --tune and excludes --dataset.",
        HelpName = "file",
    };

    /// <summary><c>--tune-split &lt;name&gt;</c>: the metadata.split value of the tune rows.</summary>
    public static Option<string> TuneSplit { get; } = new("--tune-split")
    {
        Description = "The metadata.split value that marks a tune row.",
        HelpName = "name",
        DefaultValueFactory = _ => SplitNames.DefaultTune,
    };

    /// <summary><c>--test-split &lt;name&gt;</c>: the metadata.split value of the test rows.</summary>
    public static Option<string> TestSplit { get; } = new("--test-split")
    {
        Description = "The metadata.split value that marks a test row.",
        HelpName = "name",
        DefaultValueFactory = _ => SplitNames.DefaultTest,
    };

    /// <summary><c>--where metadata.&lt;key&gt;=&lt;value&gt;</c>: repeatable; every filter must match.</summary>
    public static Option<string[]> Where { get; } = new("--where")
    {
        Description = "Keep only rows whose metadata.<key> equals <value>; repeatable, and every filter must match.",
        HelpName = "metadata.<key>=<value>",
        AllowMultipleArgumentsPerToken = true,
    };

    /// <summary><c>--rule &lt;id&gt;</c>: which rule, when the policy has several.</summary>
    public static Option<string?> Rule { get; } = new("--rule")
    {
        Description = "The rule to evaluate; required when the policy has more than one.",
        HelpName = "id",
    };

    /// <summary><c>--out &lt;file&gt;</c>: where the JSON result goes.</summary>
    public static Option<string?> Out { get; } = new("--out")
    {
        Description = "Write the JSON result to this file.",
        HelpName = "file",
    };

    /// <summary><c>--recording &lt;file&gt;</c>: the recording a read verb works from.</summary>
    public static Option<string?> Recording { get; } = new("--recording")
    {
        Description = "The recording to read.",
        HelpName = "file",
    };

    /// <summary><c>--force</c>: accept a recording whose dataset digest no longer matches the file.</summary>
    public static Option<bool> Force { get; } = new("--force")
    {
        Description = "Read the recording even though the dataset's digest differs from the recorded one.",
    };

    /// <summary>The options that select the inputs, in the order help lists them.</summary>
    public static IReadOnlyList<Option> InputOptions { get; } =
    [
        Policy,
        Dataset,
        Tune,
        Test,
        TuneSplit,
        TestSplit,
        Where,
        Rule,
    ];
}
