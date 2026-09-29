namespace SemanticPolicy.Evals.Metrics;

/// <summary>
/// A 95% confidence interval around a proportion. On the forty to five hundred rows a dataset of this kind
/// holds, a rate read alone overstates what it shows: 32 of 33 reads as 0.970 and supports anything from 0.847.
/// </summary>
/// <param name="Lower">The lower bound, between 0 and the proportion.</param>
/// <param name="Upper">The upper bound, between the proportion and 1.</param>
public sealed record Interval(double Lower, double Upper)
{
    // The 0.975 quantile of the standard normal distribution, to double precision.
    private const double _z = 1.959963984540054;

    /// <summary>
    /// The Wilson score interval, without continuity correction, of <paramref name="successes"/> out of
    /// <paramref name="trials"/>. Unlike the normal approximation it stays inside [0, 1] and does not collapse to a
    /// point at 0 of n or n of n, which are the rates a small, clean dataset produces most.
    /// </summary>
    /// <param name="successes">The numerator's count.</param>
    /// <param name="trials">The denominator's count.</param>
    /// <returns>The interval, or <see langword="null"/> when <paramref name="trials"/> is 0 and there is no rate.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="successes"/> is negative or greater than <paramref name="trials"/>.
    /// </exception>
    public static Interval? Wilson(int successes, int trials)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(successes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(successes, trials);
        if (trials == 0)
        {
            return null;
        }

        double n = trials;
        double p = successes / n;
        double zSquared = _z * _z;
        double scale = 1 + (zSquared / n);
        double centre = (p + (zSquared / (2 * n))) / scale;
        double halfWidth = _z * Math.Sqrt((p * (1 - p) / n) + (zSquared / (4 * n * n))) / scale;

        // At 0 of n the centre and the half-width are equal, and at n of n they sum to 1, but only up to rounding:
        // the bound is set rather than computed, so it is never a hair outside [0, 1].
        return new Interval(
            successes == 0 ? 0 : centre - halfWidth,
            successes == trials ? 1 : centre + halfWidth);
    }
}
