namespace SemanticPolicy.Evals.Metrics;

/// <summary>
/// One binary classifier's four counts and the rates read off them. A rate whose denominator is zero is
/// <see langword="null"/> rather than zero: "no row was labelled positive" and "the classifier found none
/// of them" are different facts, and a report that printed both as 0 would let a script confuse them.
/// </summary>
/// <param name="TruePositives">Rows predicted positive and labelled positive.</param>
/// <param name="FalsePositives">Rows predicted positive and labelled negative.</param>
/// <param name="TrueNegatives">Rows predicted negative and labelled negative.</param>
/// <param name="FalseNegatives">Rows predicted negative and labelled positive.</param>
public sealed record BinaryConfusion(int TruePositives, int FalsePositives, int TrueNegatives, int FalseNegatives)
{
    /// <summary>The share of counted rows the classifier got right.</summary>
    public double? Accuracy => Rate(
        TruePositives + TrueNegatives,
        TruePositives + FalsePositives + TrueNegatives + FalseNegatives);

    /// <summary>The share of predicted positives that were labelled positive.</summary>
    public double? Precision => Rate(TruePositives, TruePositives + FalsePositives);

    /// <summary>The share of labelled positives the classifier predicted.</summary>
    public double? Recall => Rate(TruePositives, TruePositives + FalseNegatives);

    /// <summary>
    /// The harmonic mean of precision and recall, computed as 2·TP / (2·TP + FP + FN) so that it is defined
    /// wherever the classifier predicted or missed anything, and undefined only where it did neither.
    /// </summary>
    public double? F1 => Rate(2 * TruePositives, (2 * TruePositives) + FalsePositives + FalseNegatives);

    /// <summary>The share of labelled negatives the classifier predicted positive.</summary>
    public double? FalsePositiveRate => Rate(FalsePositives, FalsePositives + TrueNegatives);

    /// <summary>The share of labelled positives the classifier predicted negative.</summary>
    public double? FalseNegativeRate => Rate(FalseNegatives, FalseNegatives + TruePositives);

    private static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;
}
