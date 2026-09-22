using SemanticPolicy.Evals.Metrics;

namespace SemanticPolicy.Evals.Curves;

/// <summary>
/// One candidate threshold and what the rule would have concluded there. The counts come from replaying
/// every row at a policy carrying that number, so a point is what the runtime would do rather than what
/// arithmetic over evidence values predicts it would do.
/// </summary>
/// <param name="Threshold">The candidate value on the binding's declared evidence kind.</param>
/// <param name="Observed">
/// Whether some attempt actually reported this value. A point that was only added to fill out the grid is
/// a shape of the curve, not a measurement, and a reader choosing an operating point should know which.
/// </param>
/// <param name="Matrix">The rung's counts at this threshold.</param>
/// <param name="Outcomes">What became of every row at this threshold, failures and abstentions included.</param>
public sealed record CurvePoint(double Threshold, bool Observed, BinaryConfusion Matrix, OutcomeCounts Outcomes);

/// <summary>
/// The whole trade-off curve of one ladder rung. Warn and Deny are two cuts of the same question under two
/// different constraints, so each rung gets its own curve and is chosen on its own.
/// </summary>
/// <param name="Rung">The ladder rung the curve is for.</param>
/// <param name="Points">The candidates, by ascending threshold.</param>
public sealed record RungCurve(Verdict Rung, IReadOnlyList<CurvePoint> Points);
