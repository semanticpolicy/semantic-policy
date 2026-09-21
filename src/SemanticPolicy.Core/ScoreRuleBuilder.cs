namespace SemanticPolicy;

/// <summary>
/// Maps a Score rule's scale to verdicts: each rung names the level from which its verdict applies,
/// and severity must increase along the scale. The level below the lowest rung means Allow.
/// </summary>
public sealed class ScoreRuleBuilder
{
    private readonly string _id;
    private readonly string _question;
    private readonly string[] _levels;
    private readonly List<ScoreRung> _rungs = [];

    internal ScoreRuleBuilder(string id, string question, string[] levels)
    {
        _id = id;
        _question = question;
        _levels = levels;
    }

    /// <summary>Warn from this level up.</summary>
    /// <param name="level">One of the rule's levels.</param>
    public ScoreRuleBuilder WarnAtOrAbove(string level) => Rung(level, Verdict.Warn);

    /// <summary>Escalate from this level up.</summary>
    /// <param name="level">One of the rule's levels.</param>
    public ScoreRuleBuilder EscalateAtOrAbove(string level) => Rung(level, Verdict.Escalate);

    /// <summary>Deny from this level up.</summary>
    /// <param name="level">One of the rule's levels.</param>
    public ScoreRuleBuilder DenyAtOrAbove(string level) => Rung(level, Verdict.Deny);

    /// <summary>The rule with the rungs added so far.</summary>
    public ScoreRule Build() => new(_id, _question, [.. _levels], [.. _rungs]);

    private ScoreRuleBuilder Rung(string level, Verdict verdict)
    {
        _rungs.Add(new ScoreRung(level, verdict));
        return this;
    }
}
