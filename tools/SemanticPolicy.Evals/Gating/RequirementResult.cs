using System.Text.Json.Serialization;
using SemanticPolicy.Evals.Metrics;

namespace SemanticPolicy.Evals.Gating;

/// <summary>
/// One requirement, judged on the report it follows. The value decides and the interval only warns: a replay of a
/// committed recording measures the same value every time, so the gate catches a change that makes the result on
/// those rows worse, while how far the rows themselves can be trusted is the interval's to say.
/// </summary>
/// <param name="Requirement">The requirement as it was typed.</param>
/// <param name="Goal">The bound it sets.</param>
/// <param name="Value">
/// The exact value it was judged on, never the three-place one a report prints; <see langword="null"/> when there was
/// nothing to divide by, which fails. Written as <c>null</c> rather than left out, so an entry always says what was read.
/// </param>
/// <param name="Interval">The value's 95% Wilson interval; <see langword="null"/> for macro-F1 and for an undefined value.</param>
/// <param name="Passed">Whether the value meets the goal: at or above it for a <c>min-</c> requirement, at or below for <c>max-</c>.</param>
/// <param name="Warned">
/// Whether it passed while the interval's bound on the goal's side misses the goal: the lower bound for <c>min-</c>,
/// the upper for <c>max-</c>. Never true for a requirement that failed or has no interval.
/// </param>
public sealed record RequirementResult(
    string Requirement,
    double Goal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? Value,
    Interval? Interval,
    bool Passed,
    bool Warned)
{
    // What the text line prints beside the value, and nothing a reader of the JSON result lacks: the counts behind a
    // proportion are in the report section, and which bound warns follows from the requirement's name.
    internal (int Successes, int Trials)? Counts { get; init; }

    internal bool Minimum { get; init; }
}
