using System.Text.Json.Serialization;
using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// A yes-or-no question. One answer is the flagged one, and the ladder lists what it may mean from the
/// least severe verdict up; the other answer means Allow. A binding thresholds every rung of the ladder
/// on the flagged answer's evidence, and evaluation takes the most severe rung reached — the provider's
/// own answer is never consulted.
/// </summary>
/// <param name="Id">The id a binding's operating point and an evaluation result refer to the rule by.</param>
/// <param name="Question">The question, as the provider reads it.</param>
/// <param name="FlaggedAnswer">The answer the ladder applies to; the other answer means Allow.</param>
/// <param name="Ladder">
/// What the flagged answer may mean, least severe first: Warn, Escalate or Deny, each at most once,
/// strictly increasing.
/// </param>
/// <param name="Criteria">What each answer looks like, for the provider; or <see langword="null"/>.</param>
public sealed record BooleanRule(
    string Id,
    string Question,
    [property: JsonRequired] bool FlaggedAnswer,
    IReadOnlyList<Verdict> Ladder,
    BooleanCriteria? Criteria = null) : Rule(Id, Question)
{
    /// <summary>
    /// What the flagged answer may mean, least severe first: Warn, Escalate or Deny, each at most once,
    /// strictly increasing.
    /// </summary>
    public IReadOnlyList<Verdict> Ladder { get; init; } = Ladder ?? throw new ArgumentNullException(nameof(Ladder));

    /// <inheritdoc/>
    [JsonIgnore]
    public override DecisionType Type => DecisionType.Boolean;

    /// <inheritdoc/>
    public override DecisionRequest CreateRequest(SemanticContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new DecisionRequest(DecisionType.Boolean, Question, context.ToJson(), Criteria: Criteria);
    }
}
