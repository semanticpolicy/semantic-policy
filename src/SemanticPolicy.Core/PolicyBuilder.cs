namespace SemanticPolicy;

/// <summary>
/// Assembles a <see cref="Policy"/> from <see cref="Policy.Define"/>. The mode and the failure
/// behaviour are mandatory and have no default; <see cref="Build"/> expands binding-level shorthand,
/// constructs the record and validates it, so a policy that leaves the builder can be evaluated.
/// </summary>
public sealed class PolicyBuilder
{
    private readonly string _id;
    private readonly List<Rule> _rules = [];
    private readonly List<BindingBuilder> _bindings = [];
    private PolicyMode? _mode;
    private FailureBehavior? _onFailure;
    private TimeSpan? _budget;

    internal PolicyBuilder(string id)
    {
        _id = id;
    }

    /// <summary>Evaluate and record, but let everything through. Where a new policy starts.</summary>
    public PolicyBuilder Shadow()
    {
        _mode = PolicyMode.Shadow;
        return this;
    }

    /// <summary>The evaluated verdict is the policy's decision.</summary>
    public PolicyBuilder Enforce()
    {
        _mode = PolicyMode.Enforce;
        return this;
    }

    /// <summary>Adds a rule. Its id must be distinct within the policy.</summary>
    /// <param name="rule">A rule from <see cref="Policy.Rule"/> or constructed directly.</param>
    public PolicyBuilder Rule(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules.Add(rule);
        return this;
    }

    /// <summary>
    /// Adds the next provider in the chain. Bindings are tried in the order they are added; the first
    /// is the primary and each later one is where fallback and the margin gate send an attempt.
    /// </summary>
    /// <param name="providerId">The id the provider is registered under, distinct within the policy.</param>
    /// <param name="configure">Sets the thresholds and gates for this provider.</param>
    public PolicyBuilder Using(string providerId, Action<BindingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(configure);
        BindingBuilder binding = new(providerId);
        configure(binding);
        _bindings.Add(binding);
        return this;
    }

    /// <summary>What the policy does when a provider does not decide. Mandatory.</summary>
    /// <param name="behavior">One of the <see cref="FailureBehavior"/> members.</param>
    public PolicyBuilder OnFailure(FailureBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        _onFailure = behavior;
        return this;
    }

    /// <summary>
    /// The time the whole chain may take. Expiry is a provider failure and goes through the failure behaviour.
    /// </summary>
    /// <param name="budget">Greater than zero.</param>
    public PolicyBuilder Budget(TimeSpan budget)
    {
        _budget = budget;
        return this;
    }

    /// <summary>
    /// Expands binding-level shorthand, constructs the policy and validates it.
    /// </summary>
    /// <exception cref="PolicyConfigurationException">
    /// No mode or no failure behaviour was set; a binding mixes shorthand with <see cref="BindingBuilder.ForRule"/>;
    /// shorthand thresholds were given and the policy has no Boolean rule; or anything
    /// <see cref="Policy.Validate"/> rejects.
    /// </exception>
    public Policy Build()
    {
        if (_mode is null)
        {
            throw PolicyConfigurationException.For(_id, "no mode is set; call Shadow() or Enforce().");
        }

        if (_onFailure is null)
        {
            throw PolicyConfigurationException.For(_id, "no failure behaviour is set; call OnFailure(...).");
        }

        Rule[] rules = [.. _rules];
        ProviderBinding[] bindings = [.. _bindings.Select(binding => binding.Build(_id, rules))];
        Policy policy = new(_id, _mode.Value, rules, bindings, _onFailure, _budget);
        policy.Validate();
        return policy;
    }
}
