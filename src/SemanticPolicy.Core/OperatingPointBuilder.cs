using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// The numbers for one rule on one provider, inside <see cref="BindingBuilder.ForRule"/>. A threshold
/// is inclusive and reads the flagged answer's evidence of the named kind; the gate reads the margin on
/// the same kind. Every number is a measured operating point for this provider on this policy's data,
/// not a recommendation, and it does not carry over to another provider.
/// </summary>
public sealed class OperatingPointBuilder
{
    private readonly List<Threshold> _thresholds = [];
    private MarginGate? _gate;

    internal OperatingPointBuilder()
    {
    }

    internal bool HasThresholds => _thresholds.Count > 0;

    internal bool HasGate => _gate is not null;

    /// <summary>Warn when the flagged answer's probability is at or above the value.</summary>
    /// <param name="value">In [0, 1].</param>
    public OperatingPointBuilder WarnAboveProbability(double value) =>
        Above(Verdict.Warn, EvidenceKind.Probability, value);

    /// <summary>Escalate when the flagged answer's probability is at or above the value.</summary>
    /// <param name="value">In [0, 1].</param>
    public OperatingPointBuilder EscalateAboveProbability(double value) =>
        Above(Verdict.Escalate, EvidenceKind.Probability, value);

    /// <summary>Deny when the flagged answer's probability is at or above the value.</summary>
    /// <param name="value">In [0, 1].</param>
    public OperatingPointBuilder DenyAboveProbability(double value) =>
        Above(Verdict.Deny, EvidenceKind.Probability, value);

    /// <summary>Warn when the flagged answer's score is at or above the value, on the provider's scale.</summary>
    /// <param name="value">On the provider's scale.</param>
    public OperatingPointBuilder WarnAboveScore(double value) => Above(Verdict.Warn, EvidenceKind.Score, value);

    /// <summary>Escalate when the flagged answer's score is at or above the value, on the provider's scale.</summary>
    /// <param name="value">On the provider's scale.</param>
    public OperatingPointBuilder EscalateAboveScore(double value) => Above(Verdict.Escalate, EvidenceKind.Score, value);

    /// <summary>Deny when the flagged answer's score is at or above the value, on the provider's scale.</summary>
    /// <param name="value">On the provider's scale.</param>
    public OperatingPointBuilder DenyAboveScore(double value) => Above(Verdict.Deny, EvidenceKind.Score, value);

    /// <summary>
    /// Move to the next binding when the probability margin between the top answer and the runner-up is
    /// below the value.
    /// </summary>
    /// <param name="value">Greater than zero. A later call replaces an earlier one.</param>
    public OperatingPointBuilder WhenProbabilityMarginBelow(double value) => Gate(EvidenceKind.Probability, value);

    /// <summary>
    /// Move to the next binding when the score margin between the top answer and the runner-up is below
    /// the value.
    /// </summary>
    /// <param name="value">Greater than zero, on the provider's scale. A later call replaces an earlier one.</param>
    public OperatingPointBuilder WhenScoreMarginBelow(double value) => Gate(EvidenceKind.Score, value);

    internal RuleOperatingPoint Build(string ruleId) => new(ruleId, [.. _thresholds], _gate);

    internal RuleOperatingPoint BuildGateOnly(string ruleId) => new(ruleId, [], _gate);

    private OperatingPointBuilder Above(Verdict verdict, EvidenceKind kind, double value)
    {
        _thresholds.Add(new Threshold(verdict, kind, value));
        return this;
    }

    private OperatingPointBuilder Gate(EvidenceKind kind, double value)
    {
        _gate = new MarginGate(kind, value);
        return this;
    }
}
