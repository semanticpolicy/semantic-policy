namespace SemanticPolicy.Protocol;

/// <summary>
/// Why a provider call failed. A failure is a fact about the call, never about the input: a timeout is
/// neither <c>false</c> nor a verdict.
/// </summary>
public enum FailureKind
{
    /// <summary>The call did not finish in time.</summary>
    Timeout,

    /// <summary>The provider could not be reached or did not serve the call.</summary>
    Unavailable,

    /// <summary>The response did not match the contract — the adapter's verdict on the body.</summary>
    Malformed,

    /// <summary>The provider refused the request as invalid — the provider's verdict on the request.</summary>
    RejectedInput,

    /// <summary>The provider rejected the caller's credentials.</summary>
    Unauthorized,

    /// <summary>A failure the adapter could not classify.</summary>
    Unknown,
}
