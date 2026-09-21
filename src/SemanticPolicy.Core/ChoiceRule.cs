using System.Text.Json.Serialization;
using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// A question answered by picking one of the rule's options. Every option maps to a verdict, and the
/// verdict comes from the option the provider picked; a binding's operating point for a Choice rule
/// carries only the margin gate.
/// </summary>
/// <param name="Id">The id a binding's operating point and an evaluation result refer to the rule by.</param>
/// <param name="Question">The question, as the provider reads it.</param>
/// <param name="Options">The answers a provider may pick: at least two, with distinct keys.</param>
public sealed record ChoiceRule(string Id, string Question, IReadOnlyList<ChoiceOption> Options) : Rule(Id, Question)
{
    /// <summary>The answers a provider may pick: at least two, with distinct keys.</summary>
    public IReadOnlyList<ChoiceOption> Options { get; init; } =
        Options ?? throw new ArgumentNullException(nameof(Options));

    /// <inheritdoc/>
    [JsonIgnore]
    public override DecisionType Type => DecisionType.Choice;

    /// <inheritdoc/>
    public override DecisionRequest CreateRequest(SemanticContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Dictionary<string, string> options = new(Options.Count, StringComparer.Ordinal);
        foreach (ChoiceOption option in Options)
        {
            options[option.Key] = option.Description;
        }

        return new DecisionRequest(DecisionType.Choice, Question, context.ToJson(), Options: options);
    }
}
