using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// Finishes a Boolean rule by naming the flagged answer and its ladder. The ladder is what the answer
/// may mean, least severe first; the binding decides at what evidence each rung is reached.
/// </summary>
public sealed class BooleanRuleBuilder
{
    private readonly string _id;
    private readonly string _question;
    private readonly BooleanCriteria? _criteria;

    internal BooleanRuleBuilder(string id, string question, BooleanCriteria? criteria)
    {
        _id = id;
        _question = question;
        _criteria = criteria;
    }

    /// <summary>
    /// A <c>true</c> answer means one of these verdicts, least severe first; <c>false</c> means Allow.
    /// </summary>
    /// <param name="ladder">Warn, Escalate or Deny, each at most once, strictly increasing.</param>
    public BooleanRule WhenTrue(params Verdict[] ladder) => Build(flaggedAnswer: true, ladder);

    /// <summary>
    /// A <c>false</c> answer means one of these verdicts, least severe first; <c>true</c> means Allow.
    /// </summary>
    /// <param name="ladder">Warn, Escalate or Deny, each at most once, strictly increasing.</param>
    public BooleanRule WhenFalse(params Verdict[] ladder) => Build(flaggedAnswer: false, ladder);

    private BooleanRule Build(bool flaggedAnswer, Verdict[] ladder)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        return new BooleanRule(_id, _question, flaggedAnswer, [.. ladder], _criteria);
    }
}
