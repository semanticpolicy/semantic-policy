namespace SemanticPolicy;

/// <summary>
/// What a policy does when a provider does not decide — a failure or an abstention. There is no
/// library default; every policy declares one.
/// </summary>
public enum FailureAction
{
    /// <summary>Proceed as if the check had passed; the failure is recorded.</summary>
    Allow,

    /// <summary>Conclude Deny; the failure is recorded as the reason.</summary>
    Deny,

    /// <summary>Hand the decision to something outside the runtime.</summary>
    Escalate,

    /// <summary>
    /// Evaluate the failed rules again on the next binding in the chain. When the chain is exhausted the
    /// behaviour's <see cref="FailureBehavior.Then"/> verdict is the outcome.
    /// </summary>
    Fallback,
}
