namespace SemanticPolicy;

/// <summary>
/// One provider in a policy's ordered chain, with the operating point of every rule it answers. A
/// binding is the provider's id and the numbers — the rules, the mode and the failure behaviour are the
/// policy's. Fallback and the margin gate both move an attempt to the next binding in the list.
/// </summary>
/// <param name="ProviderId">The id the provider is registered under.</param>
/// <param name="OperatingPoints">
/// At most one per rule; every Boolean rule of the policy needs one, a Choice or Score rule may go
/// without.
/// </param>
public sealed record ProviderBinding(string ProviderId, IReadOnlyList<RuleOperatingPoint> OperatingPoints)
{
    /// <summary>The id the provider is registered under.</summary>
    public string ProviderId { get; init; } = ProviderId ?? throw new ArgumentNullException(nameof(ProviderId));

    /// <summary>
    /// At most one per rule; every Boolean rule of the policy needs one, a Choice or Score rule may go
    /// without.
    /// </summary>
    public IReadOnlyList<RuleOperatingPoint> OperatingPoints { get; init; } =
        OperatingPoints ?? throw new ArgumentNullException(nameof(OperatingPoints));
}
