namespace SemanticPolicy.Protocol;

/// <summary>
/// What a piece of evidence means. Every kind is a signal on its own scale; none is read as a calibrated
/// probability unless it says it is one, and none is a guarantee.
/// </summary>
public enum EvidenceKind
{
    /// <summary>
    /// In [0, 1], and the provider or a calibration layer claims it is calibrated. Over all options and
    /// summing to one it is a distribution.
    /// </summary>
    Probability,

    /// <summary>Monotonic and provider-scaled: ordering is meaningful, the value is not a probability.</summary>
    Score,

    /// <summary>An unbounded log-odds value.</summary>
    Logit,

    /// <summary>The gap between the top option and the runner-up, on the provider's scale.</summary>
    Margin,

    /// <summary>A number the provider returned whose meaning it does not define.</summary>
    Unknown,
}
