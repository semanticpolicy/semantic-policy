using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Results;

/// <summary>
/// What a verb wrote out, whole: what was run, on which rows, and what it measured. The envelope is the
/// tool's contract with whatever reads its files — a dashboard, a diff between two runs, a later version of
/// the tool — so it names its format and carries the run's identity even when there is nothing to report.
/// </summary>
/// <param name="Format">The format marker; <see cref="FormatV0"/> is the only one this tool writes.</param>
/// <param name="Verb">The verb that produced the file.</param>
/// <param name="ToolVersion">The version of the tool that produced it.</param>
/// <param name="GeneratedAt">When the file was written.</param>
/// <param name="PolicyId">The policy the run was about.</param>
/// <param name="Mode">Whether that policy enforces its verdicts or only records them.</param>
/// <param name="RuleId">The rule the run measured; a run is about one rule.</param>
/// <param name="DecisionType">The kind of question that rule asks, which says which sections are filled.</param>
/// <param name="Rows">How the rows were arrived at, from the dataset down to what was scored.</param>
/// <param name="Report">The measurements, or <see langword="null"/> for a verb that takes none.</param>
/// <param name="RecordingPath">The recording the numbers were replayed from, as it was given.</param>
public sealed record EvalsResult(
    string Format,
    string Verb,
    string ToolVersion,
    DateTimeOffset GeneratedAt,
    string PolicyId,
    PolicyMode Mode,
    string RuleId,
    DecisionType DecisionType,
    RowSelection Rows,
    ReportSection? Report,
    string? RecordingPath = null)
{
    /// <summary>The format of a result file this tool writes.</summary>
    public const string FormatV0 = "semanticpolicy/evals-result/v0";
}

/// <summary>
/// How many rows the numbers are about and what became of the rest. A reader who sees a metric computed
/// over a fraction of the dataset needs to know it was a fraction, and the drop happens in stages.
/// </summary>
/// <param name="DatasetRows">The rows the dataset files hold.</param>
/// <param name="RecordedRows">Of those, the ones the recording has results for.</param>
/// <param name="AfterFilter">Of those, the ones left after the filters.</param>
/// <param name="Filters">The filters as they were given, so the count can be read back to a reason.</param>
/// <param name="SplitSource">How tune and test were told apart.</param>
/// <param name="TuneRows">The rows a threshold may be chosen on.</param>
/// <param name="TestRows">The rows it is then measured on.</param>
public sealed record RowSelection(
    int DatasetRows,
    int RecordedRows,
    int AfterFilter,
    IReadOnlyList<string> Filters,
    string SplitSource,
    int TuneRows,
    int TestRows);

/// <summary>
/// Everything measured about one rule on one selection. A section that does not apply to the rule's
/// decision type is absent rather than empty: a Boolean rule has no class table, and a Choice rule has no
/// ladder to sweep.
/// </summary>
/// <param name="Outcomes">What became of every row, decided or not.</param>
/// <param name="Verdicts">How often the rule reached each verdict, over every row.</param>
/// <param name="Rungs">The per-rung matrices of a Boolean rule.</param>
/// <param name="Classes">The confusion table of a Choice or Score rule.</param>
/// <param name="Discrimination">How well each rung's evidence separates the labels.</param>
/// <param name="Calibration">Whether the probabilities mean what they say.</param>
/// <param name="Providers">What each provider was asked and what it cost.</param>
/// <param name="Notes">What a reader should know before trusting the numbers above.</param>
/// <param name="SweptProvider">
/// The binding whose threshold the discrimination curves move, by provider name; every other binding stays
/// at its file thresholds. <see langword="null"/> when there is no discrimination section.
/// </param>
public sealed record ReportSection(
    OutcomeCounts Outcomes,
    IReadOnlyDictionary<string, int> Verdicts,
    IReadOnlyList<RungMetrics>? Rungs,
    MulticlassConfusion? Classes,
    IReadOnlyList<RungDiscrimination>? Discrimination,
    Calibration Calibration,
    IReadOnlyList<ProviderStats> Providers,
    IReadOnlyList<string> Notes,
    string? SweptProvider = null);

/// <summary>One rung's discrimination, paired with the rung it was measured on.</summary>
/// <param name="Rung">The ladder rung.</param>
/// <param name="Values">The areas under that rung's curves.</param>
public sealed record RungDiscrimination(Verdict Rung, Discrimination Values);
