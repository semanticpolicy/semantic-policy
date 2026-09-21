namespace SemanticPolicy;

/// <summary>
/// Whether a policy's verdict is returned as its decision or only recorded. The mode belongs to the
/// policy, never to a provider, and switching it is a configuration change.
/// </summary>
public enum PolicyMode
{
    /// <summary>
    /// The policy is evaluated in full and everything behind the verdict is recorded, but the effective
    /// verdict is always Allow. Where a new probabilistic policy on a sensitive path starts.
    /// </summary>
    Shadow,

    /// <summary>The evaluated verdict is the policy's decision.</summary>
    Enforce,
}
