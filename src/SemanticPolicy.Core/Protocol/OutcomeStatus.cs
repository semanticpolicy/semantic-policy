namespace SemanticPolicy.Protocol;

/// <summary>
/// What happened to a provider call. This is one axis of a result; the provider's answer is the other,
/// and neither is ever expressed as a value of the other.
/// </summary>
public enum OutcomeStatus
{
    /// <summary>The provider returned an answer, with whatever evidence it supports.</summary>
    Success,

    /// <summary>The provider declined to answer this input. Evidence may still be present.</summary>
    Abstain,

    /// <summary>The call produced no answer; <see cref="ProviderOutcome.Kind"/> says why.</summary>
    Failure,
}
