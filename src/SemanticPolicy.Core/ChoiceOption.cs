namespace SemanticPolicy;

/// <summary>
/// One answer a Choice rule offers, and what picking it means. The key is what the provider's result
/// carries; the description is what the provider reads.
/// </summary>
/// <param name="Key">The option's key on the wire, unique within the rule.</param>
/// <param name="Description">What the option means, as the provider reads it.</param>
/// <param name="Verdict">The verdict when the provider picks this option: Allow, Warn, Escalate or Deny.</param>
public sealed record ChoiceOption(string Key, string Description, Verdict Verdict)
{
    /// <summary>The option's key on the wire, unique within the rule.</summary>
    public string Key { get; init; } = Key ?? throw new ArgumentNullException(nameof(Key));

    /// <summary>What the option means, as the provider reads it.</summary>
    public string Description { get; init; } = Description ?? throw new ArgumentNullException(nameof(Description));
}
