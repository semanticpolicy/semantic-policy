using System.CommandLine;
using SemanticPolicy.Evals.Datasets;

namespace SemanticPolicy.Evals.Cli;

/// <summary>
/// What the user asked to read, checked for shape but not yet opened: exactly one of a single dataset or
/// the tune/test pair, the split names, the filters and the rule.
/// </summary>
/// <param name="PolicyPath">The <c>--policy</c> file.</param>
/// <param name="RuleId">The <c>--rule</c> id, or <see langword="null"/> to take the policy's only rule.</param>
/// <param name="DatasetPath">The <c>--dataset</c> file, or <see langword="null"/> in two-file mode.</param>
/// <param name="TunePath">The <c>--tune</c> file, or <see langword="null"/> in single-file mode.</param>
/// <param name="TestPath">The <c>--test</c> file, or <see langword="null"/> in single-file mode.</param>
/// <param name="SplitNames">The metadata.split values that mark the two halves.</param>
/// <param name="Filters">The <c>--where</c> filters, already parsed.</param>
public sealed record InputSelection(
    string PolicyPath,
    string? RuleId,
    string? DatasetPath,
    string? TunePath,
    string? TestPath,
    SplitNames SplitNames,
    IReadOnlyList<MetadataFilter> Filters)
{
    /// <summary>Reads the shared input options off a parsed command line.</summary>
    /// <param name="parseResult">The parse result of a verb that carries <see cref="SharedOptions.InputOptions"/>.</param>
    /// <exception cref="EvalsException">
    /// Neither form of dataset was given, both were, <c>--tune</c> and <c>--test</c> do not come as a pair, or a
    /// <c>--where</c> token is malformed.
    /// </exception>
    public static InputSelection From(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        string policy = parseResult.GetValue(SharedOptions.Policy)
            ?? throw new EvalsException("--policy <file> is required.");
        string? dataset = parseResult.GetValue(SharedOptions.Dataset);
        string? tune = parseResult.GetValue(SharedOptions.Tune);
        string? test = parseResult.GetValue(SharedOptions.Test);
        if (dataset is not null && (tune is not null || test is not null))
        {
            throw new EvalsException("--dataset excludes --tune and --test: give one file, or the pair.");
        }

        if (dataset is null && (tune is null || test is null))
        {
            throw new EvalsException(tune is null && test is null
                ? "Give --dataset <file>, or --tune <file> with --test <file>."
                : "--tune and --test go together; give both.");
        }

        SplitNames names = new(
            parseResult.GetValue(SharedOptions.TuneSplit) ?? SplitNames.DefaultTune,
            parseResult.GetValue(SharedOptions.TestSplit) ?? SplitNames.DefaultTest);
        MetadataFilter[] filters = [.. (parseResult.GetValue(SharedOptions.Where) ?? []).Select(MetadataFilter.Parse)];
        return new InputSelection(policy, parseResult.GetValue(SharedOptions.Rule), dataset, tune, test, names, filters);
    }
}
