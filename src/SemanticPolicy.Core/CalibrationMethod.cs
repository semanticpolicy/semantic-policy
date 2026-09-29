namespace SemanticPolicy;

/// <summary>How an <see cref="EvidenceCalibration"/> was fitted.</summary>
public enum CalibrationMethod
{
    /// <summary>
    /// Platt scaling: a logistic function of one input, p = σ(slope · x + intercept), fitted on labelled
    /// data.
    /// </summary>
    Platt,
}
