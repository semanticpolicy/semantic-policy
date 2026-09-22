using SemanticPolicy.Evals.Metrics;

namespace SemanticPolicy.Evals.Curves;

/// <summary>
/// How well a rung's evidence separates the two labels, independent of where its threshold is set. Two
/// providers with the same accuracy at their own cuts can rank rows very differently, and that difference
/// is what survives moving the threshold.
/// </summary>
/// <param name="RocAuc">The area under the ROC curve, or <see langword="null"/> when it is undefined.</param>
/// <param name="PrAuc">
/// The area under the precision-recall curve, or <see langword="null"/> when it is undefined. It is the
/// more honest of the two where the positives are rare.
/// </param>
/// <param name="Rows">How many classified rows the areas were computed over.</param>
public sealed record Discrimination(double? RocAuc, double? PrAuc, int Rows)
{
    /// <summary>Integrates a rung's curve by the trapezoid rule.</summary>
    /// <param name="curve">The rung's curve, one point per candidate threshold.</param>
    public static Discrimination FromCurve(RungCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (curve.Points.Count == 0)
        {
            return new Discrimination(null, null, 0);
        }

        // The labels are the data's, not the threshold's, so any point of the curve counts them.
        BinaryConfusion counts = curve.Points[0].Matrix;
        int positives = counts.TruePositives + counts.FalseNegatives;
        int negatives = counts.FalsePositives + counts.TrueNegatives;
        int rows = curve.Points[0].Outcomes.Classified;

        // With one label missing every threshold scores alike, and an area under that would read as a
        // measurement of a separation the data never asked the classifier to make.
        return positives == 0 || negatives == 0
            ? new Discrimination(null, null, rows)
            : new Discrimination(Roc(curve.Points), PrecisionRecall(curve.Points), rows);
    }

    private static double Roc(IReadOnlyList<CurvePoint> points)
    {
        // The corners: no threshold is guaranteed to predict everything or nothing, and without them the
        // area would be measured over whatever stretch of the curve the candidates happened to cover.
        List<(double X, double Y)> curve = [(0, 0), (1, 1)];
        foreach (CurvePoint point in points)
        {
            if (point.Matrix is { FalsePositiveRate: { } fpr, Recall: { } recall })
            {
                curve.Add((fpr, recall));
            }
        }

        curve.Sort((left, right) =>
        {
            int byFalsePositiveRate = left.X.CompareTo(right.X);
            return byFalsePositiveRate != 0 ? byFalsePositiveRate : left.Y.CompareTo(right.Y);
        });
        return Area(curve);
    }

    private static double? PrecisionRecall(IReadOnlyList<CurvePoint> points)
    {
        List<(double X, double Y)> curve = [];
        (double Threshold, double Precision)? strictest = null;
        foreach (CurvePoint point in points)
        {
            // A threshold that predicts nothing has no precision to plot: dividing by what was predicted
            // asks a question about an empty set.
            if (point.Matrix is not { Recall: { } recall, Precision: { } precision })
            {
                continue;
            }

            curve.Add((recall, precision));
            if (strictest is null || point.Threshold > strictest.Value.Threshold)
            {
                strictest = (point.Threshold, precision);
            }
        }

        if (strictest is not { } top)
        {
            return null;
        }

        // Recall 0 is where the curve starts and no threshold reaches it, so it is carried over from the
        // strictest threshold that still predicted something.
        curve.Add((0, top.Precision));
        curve.Sort((left, right) =>
        {
            int byRecall = left.X.CompareTo(right.X);

            // Where one recall was reached at several precisions, the best of them is the curve and the
            // others are points below it; taking them in descending order integrates the curve itself.
            return byRecall != 0 ? byRecall : right.Y.CompareTo(left.Y);
        });
        return Area(curve);
    }

    private static double Area(List<(double X, double Y)> curve)
    {
        double area = 0;
        for (int index = 1; index < curve.Count; index++)
        {
            (double x, double y) = curve[index];
            (double previousX, double previousY) = curve[index - 1];
            area += (x - previousX) * (y + previousY) / 2;
        }

        return area;
    }
}
