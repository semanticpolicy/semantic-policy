using System.Text.Json.Serialization;
using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// A question answered on an ordered scale of named levels. The rungs map the scale to verdicts —
/// "Deny at or above <c>serious</c>" — and the verdict is the most severe rung at or below the level the
/// provider picked, else Allow. A binding's operating point for a Score rule carries only the margin
/// gate.
/// </summary>
/// <param name="Id">The id a binding's operating point and an evaluation result refer to the rule by.</param>
/// <param name="Question">The question, as the provider reads it.</param>
/// <param name="Levels">The scale, lowest first: two to ten distinct names.</param>
/// <param name="Rungs">
/// Which level each verdict starts at: at least one, each naming a distinct level, with severity
/// increasing along the scale.
/// </param>
public sealed record ScoreRule(
    string Id,
    string Question,
    IReadOnlyList<string> Levels,
    IReadOnlyList<ScoreRung> Rungs) : Rule(Id, Question)
{
    /// <summary>The scale, lowest first: two to ten distinct names.</summary>
    public IReadOnlyList<string> Levels { get; init; } = Levels ?? throw new ArgumentNullException(nameof(Levels));

    /// <summary>
    /// Which level each verdict starts at: at least one, each naming a distinct level, with severity
    /// increasing along the scale.
    /// </summary>
    public IReadOnlyList<ScoreRung> Rungs { get; init; } = Rungs ?? throw new ArgumentNullException(nameof(Rungs));

    /// <inheritdoc/>
    [JsonIgnore]
    public override DecisionType Type => DecisionType.Score;

    /// <inheritdoc/>
    public override DecisionRequest CreateRequest(SemanticContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new DecisionRequest(DecisionType.Score, Question, context.ToJson(), Levels: Levels);
    }
}
