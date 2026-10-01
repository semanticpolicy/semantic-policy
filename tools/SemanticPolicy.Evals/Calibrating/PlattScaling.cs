namespace SemanticPolicy.Evals.Calibrating;

// Platt's sigmoid fit, p = σ(slope · x + intercept), in the sign convention of EvidenceCalibration: Platt writes
// 1 / (1 + exp(A·f + B)), so his A and B are the negated slope and intercept. The targets are smoothed as he
// proposed, (N₊ + 1) / (N₊ + 2) for a flagged row and 1 / (N₋ + 2) for the others, which keeps the slope finite
// even when the rows separate perfectly. The minimiser is Newton's method with a backtracking line search, after
// Lin, Lin and Weng's note on Platt's probabilistic outputs, and every log and sigmoid is written so that no large
// input overflows.
internal static class PlattScaling
{
    private const int _maxIterations = 100;
    private const double _minStep = 1e-10;
    private const double _regularizer = 1e-12;
    private const double _tolerance = 1e-5;

    public static (double Slope, double Intercept) Fit(IReadOnlyList<(double Input, bool Flagged)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int flagged = rows.Count(row => row.Flagged);
        int other = rows.Count - flagged;
        double high = (flagged + 1.0) / (flagged + 2.0);
        double low = 1.0 / (other + 2.0);
        double[] x = [.. rows.Select(row => row.Input)];
        double[] t = [.. rows.Select(row => row.Flagged ? high : low)];

        // The start is the prior: no slope, and the intercept that gives every row the smoothed flagged share.
        double a = 0;
        double b = Math.Log((flagged + 1.0) / (other + 1.0));
        double loss = Loss(x, t, a, b);
        for (int iteration = 0; iteration < _maxIterations; iteration++)
        {
            double ga = 0, gb = 0, haa = _regularizer, hab = 0, hbb = _regularizer;
            for (int i = 0; i < x.Length; i++)
            {
                double p = Sigmoid((a * x[i]) + b);
                double d = p - t[i];
                double w = p * (1 - p);
                ga += d * x[i];
                gb += d;
                haa += w * x[i] * x[i];
                hab += w * x[i];
                hbb += w;
            }

            if (Math.Abs(ga) < _tolerance && Math.Abs(gb) < _tolerance)
            {
                break;
            }

            double determinant = (haa * hbb) - (hab * hab);
            double da = -((hbb * ga) - (hab * gb)) / determinant;
            double db = -((haa * gb) - (hab * ga)) / determinant;
            double descent = (ga * da) + (gb * db);
            double step = 1;
            while (step >= _minStep)
            {
                double na = a + (step * da);
                double nb = b + (step * db);
                double next = Loss(x, t, na, nb);
                if (next < loss + (1e-4 * step * descent))
                {
                    (a, b, loss) = (na, nb, next);
                    break;
                }

                step /= 2;
            }

            if (step < _minStep)
            {
                // No step along the Newton direction lowers the loss: the fit is as close as doubles allow.
                break;
            }
        }

        return (a, b);
    }

    // The cross-entropy of the targets against σ(z), as softplus(z) − t·z, never taking the exp of a positive number.
    private static double Loss(double[] x, double[] t, double a, double b)
    {
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double z = (a * x[i]) + b;
            sum += z >= 0
                ? ((1 - t[i]) * z) + Math.Log(1 + Math.Exp(-z))
                : Math.Log(1 + Math.Exp(z)) - (t[i] * z);
        }

        return sum;
    }

    private static double Sigmoid(double z)
    {
        if (z >= 0)
        {
            return 1 / (1 + Math.Exp(-z));
        }

        double e = Math.Exp(z);
        return e / (1 + e);
    }
}
