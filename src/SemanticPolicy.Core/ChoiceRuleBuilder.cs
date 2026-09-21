namespace SemanticPolicy;

/// <summary>Collects a Choice rule's options, each with the verdict picking it means.</summary>
public sealed class ChoiceRuleBuilder
{
    private readonly string _id;
    private readonly string _question;
    private readonly List<ChoiceOption> _options = [];

    internal ChoiceRuleBuilder(string id, string question)
    {
        _id = id;
        _question = question;
    }

    /// <summary>Adds an option.</summary>
    /// <param name="key">The option's key on the wire, distinct within the rule.</param>
    /// <param name="description">What the option means, as the provider reads it.</param>
    /// <param name="verdict">The verdict when the provider picks it: Allow, Warn, Escalate or Deny.</param>
    public ChoiceRuleBuilder Option(string key, string description, Verdict verdict)
    {
        _options.Add(new ChoiceOption(key, description, verdict));
        return this;
    }

    /// <summary>The rule with the options added so far.</summary>
    public ChoiceRule Build() => new(_id, _question, [.. _options]);
}
