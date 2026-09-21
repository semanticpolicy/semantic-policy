namespace SemanticPolicy;

/// <summary>
/// One provider in the policy's chain, inside <see cref="PolicyBuilder.Using"/>. The numbers go either
/// per rule through <see cref="ForRule"/>, or once at binding level, which
/// <see cref="PolicyBuilder.Build"/> expands to every rule they fit: thresholds to every Boolean rule,
/// the gate to every rule. The two spellings do not mix in one binding, and the built policy always
/// holds the expanded form.
/// </summary>
public sealed class BindingBuilder
{
    private readonly string _providerId;
    private readonly OperatingPointBuilder _shorthand = new();
    private readonly List<RuleOperatingPoint> _perRule = [];

    internal BindingBuilder(string providerId)
    {
        _providerId = providerId;
    }

    /// <summary>The numbers for one rule.</summary>
    /// <param name="ruleId">The rule's id in the policy.</param>
    /// <param name="configure">Sets the rule's thresholds and gate on this provider.</param>
    public BindingBuilder ForRule(string ruleId, Action<OperatingPointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        ArgumentNullException.ThrowIfNull(configure);
        OperatingPointBuilder point = new();
        configure(point);
        _perRule.Add(point.Build(ruleId));
        return this;
    }

    /// <summary>For every Boolean rule: Warn when the flagged answer's probability is at or above the value.</summary>
    /// <param name="value">In [0, 1].</param>
    public BindingBuilder WarnAboveProbability(double value)
    {
        _shorthand.WarnAboveProbability(value);
        return this;
    }

    /// <summary>
    /// For every Boolean rule: Escalate when the flagged answer's probability is at or above the value.
    /// </summary>
    /// <param name="value">In [0, 1].</param>
    public BindingBuilder EscalateAboveProbability(double value)
    {
        _shorthand.EscalateAboveProbability(value);
        return this;
    }

    /// <summary>For every Boolean rule: Deny when the flagged answer's probability is at or above the value.</summary>
    /// <param name="value">In [0, 1].</param>
    public BindingBuilder DenyAboveProbability(double value)
    {
        _shorthand.DenyAboveProbability(value);
        return this;
    }

    /// <summary>For every Boolean rule: Warn when the flagged answer's score is at or above the value.</summary>
    /// <param name="value">On the provider's scale.</param>
    public BindingBuilder WarnAboveScore(double value)
    {
        _shorthand.WarnAboveScore(value);
        return this;
    }

    /// <summary>For every Boolean rule: Escalate when the flagged answer's score is at or above the value.</summary>
    /// <param name="value">On the provider's scale.</param>
    public BindingBuilder EscalateAboveScore(double value)
    {
        _shorthand.EscalateAboveScore(value);
        return this;
    }

    /// <summary>For every Boolean rule: Deny when the flagged answer's score is at or above the value.</summary>
    /// <param name="value">On the provider's scale.</param>
    public BindingBuilder DenyAboveScore(double value)
    {
        _shorthand.DenyAboveScore(value);
        return this;
    }

    /// <summary>For every rule: move to the next binding when the probability margin is below the value.</summary>
    /// <param name="value">Greater than zero. A later call replaces an earlier one.</param>
    public BindingBuilder WhenProbabilityMarginBelow(double value)
    {
        _shorthand.WhenProbabilityMarginBelow(value);
        return this;
    }

    /// <summary>For every rule: move to the next binding when the score margin is below the value.</summary>
    /// <param name="value">Greater than zero, on the provider's scale. A later call replaces an earlier one.</param>
    public BindingBuilder WhenScoreMarginBelow(double value)
    {
        _shorthand.WhenScoreMarginBelow(value);
        return this;
    }

    internal ProviderBinding Build(string policyId, IReadOnlyList<Rule> rules)
    {
        bool hasShorthand = _shorthand.HasThresholds || _shorthand.HasGate;
        if (hasShorthand && _perRule.Count > 0)
        {
            throw PolicyConfigurationException.For(
                policyId,
                "binding-level thresholds or gate are mixed with ForRule(...); use one spelling per binding.",
                providerId: _providerId);
        }

        if (_shorthand.HasThresholds && !rules.Any(rule => rule is BooleanRule))
        {
            throw PolicyConfigurationException.For(
                policyId,
                "binding-level thresholds apply to Boolean rules and the policy has none.",
                providerId: _providerId);
        }

        if (!hasShorthand)
        {
            return new ProviderBinding(_providerId, [.. _perRule]);
        }

        List<RuleOperatingPoint> points = [];
        foreach (Rule rule in rules)
        {
            if (rule is BooleanRule)
            {
                points.Add(_shorthand.Build(rule.Id));
            }
            else if (_shorthand.HasGate)
            {
                points.Add(_shorthand.BuildGateOnly(rule.Id));
            }
        }

        return new ProviderBinding(_providerId, points);
    }
}
