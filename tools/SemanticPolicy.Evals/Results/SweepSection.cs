using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Sweeping;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Results;

/// <summary>
/// One binding's operating point, swept: every rung's curve and recommendation, the gate's, and how the
/// chosen numbers did on the rows they were not chosen on. Nothing here is repaired or reordered; a
/// recommendation that would not make a valid policy is flagged, not fixed.
/// </summary>
/// <param name="Provider">The binding that was swept, by provider name.</param>
/// <param name="Split">Which rows the numbers were chosen on and which they are reported on.</param>
/// <param name="Rungs">One entry per ladder rung of a Boolean rule, in ladder order; empty for any other rule.</param>
/// <param name="Conflict">
/// Whether the thresholds, as recommended or kept, fail to increase with severity. Such a set is reported as
/// it is: the tool does not move one to make room for another.
/// </param>
/// <param name="Gate">
/// The gate's curve and recommendation, or <see langword="null"/> when the binding declares no evidence kind a
/// margin could be read on and no gate constraint asked for one.
/// </param>
/// <param name="Passes">
/// How many passes the sweep took and how they ended. The curves, recommendations and test rows above are the last
/// pass's.
/// </param>
public sealed record SweepSection(
    string Provider,
    SplitWording Split,
    IReadOnlyList<SweptRung> Rungs,
    bool Conflict,
    SweptGate? Gate,
    SweepPasses Passes)
{
    /// <summary>Whether every constraint on every rung and on the gate could be met.</summary>
    public bool Feasible =>
        Rungs.All(rung => rung.Recommendation.Feasible) && (Gate is null || Gate.Recommendation.Feasible);
}

/// <summary>One rung of the swept binding: its curve on the tune rows, the recommendation, and the test rows at it.</summary>
/// <param name="Rung">The ladder rung.</param>
/// <param name="Curve">The rung's curve on the rows it was chosen on; every point the table prints.</param>
/// <param name="Recommendation">The threshold recommended, kept from the file, or missed.</param>
/// <param name="Test">
/// The rung at that threshold on the rows it is reported on, from the same single-rung variant the curve uses;
/// <see langword="null"/> when no threshold was recommended.
/// </param>
/// <param name="ChosenOn">The rows the threshold was chosen on.</param>
/// <param name="ReportedOn">The rows <paramref name="Test"/> was measured on.</param>
public sealed record SweptRung(
    Verdict Rung,
    RungCurve Curve,
    RungRecommendation Recommendation,
    CurvePoint? Test,
    string ChosenOn,
    string ReportedOn);

/// <summary>The swept binding's margin gate: its curve on the tune rows, the recommendation, and the test rows at it.</summary>
/// <param name="Kind">The evidence kind the margin is read on.</param>
/// <param name="Curve">The gate curve on the rows it was chosen on.</param>
/// <param name="Recommendation">The gate recommended, kept from the file, or missed.</param>
/// <param name="Test">The gate on the rows it is reported on; <see langword="null"/> when nothing was recommended.</param>
/// <param name="ChosenOn">The rows the gate was chosen on.</param>
/// <param name="ReportedOn">The rows <paramref name="Test"/> was measured on.</param>
public sealed record SweptGate(
    EvidenceKind Kind,
    GateCurve Curve,
    GateRecommendation Recommendation,
    GatePoint? Test,
    string ChosenOn,
    string ReportedOn);

/// <summary>
/// The passes of a sweep. Each pass is swept at the picks of the one before, the first at the policy file's numbers,
/// because a threshold's curve moves with the gate and the gate's with the lowest threshold.
/// </summary>
/// <param name="Count">How many passes were swept.</param>
/// <param name="End">Why there was no next one.</param>
/// <param name="SweptAt">
/// With no fixed point, the binding's operating point the last pass was swept at; <see langword="null"/> otherwise.
/// </param>
/// <param name="Picked">
/// With no fixed point, the operating point the last pass picked there; <see langword="null"/> otherwise.
/// </param>
public sealed record SweepPasses(
    int Count,
    PassesEnd End,
    RuleOperatingPoint? SweptAt = null,
    RuleOperatingPoint? Picked = null);

/// <summary>Why a sweep made no further pass.</summary>
public enum PassesEnd
{
    /// <summary>The last pass picked what it was swept at, so every curve is replayed at the recommendation.</summary>
    Settled,

    /// <summary>
    /// The last pass picked thresholds that do not increase with severity. No policy holds them, so there is
    /// nothing to sweep a next pass at, and the gate was swept at the thresholds the pass started from.
    /// </summary>
    Conflict,

    /// <summary>
    /// The last pass picked what the one before it was swept at: the passes alternate between two sets of picks
    /// and never settle.
    /// </summary>
    Alternating,

    /// <summary>The picks were still changing when <see cref="OperatingPointSweep.MaxPasses"/> passes had run.</summary>
    OutOfPasses,
}

/// <summary>
/// How a result names its two halves. A number chosen and measured on the same rows says less than one measured
/// on rows it never saw, and every line that reports one says which it is.
/// </summary>
/// <param name="ChosenOn">The rows a threshold or gate is chosen on, with their count.</param>
/// <param name="ReportedOn">The rows it is then reported on, with their count.</param>
/// <param name="Sentence">Both, as the clause a recommendation line ends with.</param>
public sealed record SplitWording(string ChosenOn, string ReportedOn, string Sentence);
