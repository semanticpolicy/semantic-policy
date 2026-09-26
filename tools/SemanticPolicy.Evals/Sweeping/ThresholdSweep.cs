using SemanticPolicy.Evals.Curves;
using SemanticPolicy.Evals.Metrics;

namespace SemanticPolicy.Evals.Sweeping;

/// <summary>
/// The operating point recommended for one ladder rung, and why. A recommendation exists only against the
/// constraints it names; a rung nobody constrained keeps the policy file's number and says it was not swept.
/// </summary>
/// <param name="Rung">The ladder rung.</param>
/// <param name="Constraints">The constraints on this rung, in the order they were given.</param>
/// <param name="Swept">Whether any constraint was given for the rung.</param>
/// <param name="Feasible">
/// Whether some observed threshold satisfies every constraint; <see langword="true"/> for a rung that was not
/// swept, which has nothing to fail.
/// </param>
/// <param name="Threshold">
/// The recommended threshold; the policy file's for a rung that was not swept; <see langword="null"/> when the
/// constraints cannot be met.
/// </param>
/// <param name="Chosen">The curve point the recommendation stands on, when there is one.</param>
/// <param name="Nearest">
/// When the constraints cannot be met, the observed point that misses them by the least, summed over the
/// constraints; <see langword="null"/> otherwise, or when no point has every constrained rate defined.
/// </param>
public sealed record RungRecommendation(
    Verdict Rung,
    IReadOnlyList<RungConstraint> Constraints,
    bool Swept,
    bool Feasible,
    double? Threshold,
    CurvePoint? Chosen,
    CurvePoint? Nearest);

/// <summary>
/// Chooses a rung's threshold from its curve under the user's constraints. Only observed thresholds are
/// candidates: a grid point is the shape of the curve between measurements, not a number any attempt reported.
/// Nothing here picks by F1 or accuracy; without a constraint nothing is picked at all.
/// </summary>
public static class ThresholdSweep
{
    /// <summary>
    /// Intersects the feasible sets of the rung's constraints and takes the end the first-named constraint asks
    /// for: the highest threshold for <c>min-recall</c>, the lowest for <c>max-fpr</c> and <c>min-precision</c>.
    /// A rate that is undefined at a point — no labelled positives, nothing predicted — satisfies nothing there.
    /// </summary>
    /// <param name="curve">The rung's curve, as <see cref="ThresholdCurve"/> computed it on the rows to choose on.</param>
    /// <param name="constraints">The constraints; those for other rungs are ignored.</param>
    /// <param name="fileThreshold">The policy file's threshold for the rung, kept when the rung is not swept.</param>
    public static RungRecommendation Recommend(
        RungCurve curve,
        IReadOnlyList<RungConstraint> constraints,
        double? fileThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(constraints);
        RungConstraint[] own = [.. constraints.Where(constraint => constraint.Rung == curve.Rung)];
        if (own.Length == 0)
        {
            return new RungRecommendation(curve.Rung, own, Swept: false, Feasible: true, fileThreshold, null, null);
        }

        // Walking the candidates from the end the first constraint prefers makes the first feasible point the
        // recommendation, and makes that same end win a tie between two equally near infeasible points.
        IEnumerable<CurvePoint> observed = curve.Points.Where(point => point.Observed);
        CurvePoint[] preferred = own[0].Kind == ConstraintKind.MinRecall
            ? [.. observed.OrderByDescending(point => point.Threshold)]
            : [.. observed.OrderBy(point => point.Threshold)];

        CurvePoint? chosen = preferred.FirstOrDefault(point => Meets(point.Matrix, own));
        if (chosen is not null)
        {
            return new RungRecommendation(curve.Rung, own, Swept: true, Feasible: true, chosen.Threshold, chosen, null);
        }

        CurvePoint? nearest = null;
        double least = double.PositiveInfinity;
        foreach (CurvePoint point in preferred)
        {
            if (TotalViolation(point.Matrix, own) is { } violation && violation < least)
            {
                least = violation;
                nearest = point;
            }
        }

        return new RungRecommendation(curve.Rung, own, Swept: true, Feasible: false, null, null, nearest);
    }

    /// <summary>Whether every constraint holds at a point; a rate that is undefined there satisfies none.</summary>
    /// <param name="matrix">The point's confusion matrix.</param>
    /// <param name="constraints">The constraints, all on the rung the point belongs to.</param>
    internal static bool Meets(BinaryConfusion matrix, IEnumerable<RungConstraint> constraints) =>
        constraints.All(constraint => Satisfies(matrix, constraint));

    private static bool Satisfies(BinaryConfusion matrix, RungConstraint constraint) =>
        Rate(matrix, constraint.Kind) is { } rate
        && (constraint.Kind == ConstraintKind.MaxFpr ? rate <= constraint.Value : rate >= constraint.Value);

    private static double? TotalViolation(BinaryConfusion matrix, IEnumerable<RungConstraint> constraints)
    {
        double total = 0;
        foreach (RungConstraint constraint in constraints)
        {
            if (Rate(matrix, constraint.Kind) is not { } rate)
            {
                return null;
            }

            total += constraint.Kind == ConstraintKind.MaxFpr
                ? Math.Max(0, rate - constraint.Value)
                : Math.Max(0, constraint.Value - rate);
        }

        return total;
    }

    private static double? Rate(BinaryConfusion matrix, ConstraintKind kind) => kind switch
    {
        ConstraintKind.MinRecall => matrix.Recall,
        ConstraintKind.MaxFpr => matrix.FalsePositiveRate,
        _ => matrix.Precision,
    };
}
