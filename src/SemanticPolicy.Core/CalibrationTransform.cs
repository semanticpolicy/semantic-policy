namespace SemanticPolicy;

/// <summary>How an <see cref="EvidenceCalibration"/> turns the provider's value into the input x of its map.</summary>
public enum CalibrationTransform
{
    /// <summary>
    /// The log-odds of the value, ln(v / (1 − v)), after it is held to
    /// [<see cref="EvidenceCalibration.Epsilon"/>, 1 − <see cref="EvidenceCalibration.Epsilon"/>]. For a
    /// score or a probability in [0, 1]; with a slope of one and an intercept of zero the map leaves a value
    /// inside that range as it is.
    /// </summary>
    LogOdds,

    /// <summary>The value itself. For a logit, and for a score that is not bounded to [0, 1].</summary>
    Identity,
}
