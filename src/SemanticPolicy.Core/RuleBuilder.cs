using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// The first step of <see cref="Policy.Rule"/>: picks the decision type. Each step returns the builder
/// for that type, and the last step returns the <see cref="Rule"/> record.
/// </summary>
public sealed class RuleBuilder
{
    private readonly string _id;

    internal RuleBuilder(string id)
    {
        _id = id;
    }

    /// <summary>
    /// A yes-or-no question; <see cref="BooleanRuleBuilder.WhenTrue"/> or <see cref="BooleanRuleBuilder.WhenFalse"/>
    /// finishes it.
    /// </summary>
    /// <param name="question">The question, as the provider reads it.</param>
    /// <param name="criteria">What each answer looks like, for the provider; or <see langword="null"/>.</param>
    public BooleanRuleBuilder Boolean(string question, BooleanCriteria? criteria = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        return new BooleanRuleBuilder(_id, question, criteria);
    }

    /// <summary>
    /// A question answered by picking one option; <see cref="ChoiceRuleBuilder.Option"/> adds each and
    /// <see cref="ChoiceRuleBuilder.Build"/> finishes it.
    /// </summary>
    /// <param name="question">The question, as the provider reads it.</param>
    public ChoiceRuleBuilder Choice(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return new ChoiceRuleBuilder(_id, question);
    }

    /// <summary>
    /// A question answered on an ordered scale; the rung methods map it and <see cref="ScoreRuleBuilder.Build"/>
    /// finishes it.
    /// </summary>
    /// <param name="question">The question, as the provider reads it.</param>
    /// <param name="levels">The scale, lowest first: two to ten distinct names.</param>
    public ScoreRuleBuilder Score(string question, params string[] levels)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(levels);
        return new ScoreRuleBuilder(_id, question, levels);
    }
}
