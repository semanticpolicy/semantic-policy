using System.Text.Json.Serialization;

namespace SemanticPolicy.Protocol;

/// <summary>
/// The provider's best answer, in the shape the decision type asks for. It is the top of the provider's
/// evidence, not a verdict: a policy thresholds the evidence rather than acting on the value alone.
/// </summary>
[JsonConverter(typeof(DecisionValueJsonConverter))]
public abstract record DecisionValue
{
    private protected DecisionValue()
    {
    }
}

/// <summary>The answer to a Boolean question.</summary>
/// <param name="Value">The answer.</param>
public sealed record BooleanValue(bool Value) : DecisionValue;

/// <summary>The answer to a Choice question.</summary>
/// <param name="Option">The key of the option picked, one of the request's options.</param>
public sealed record ChoiceValue(string Option) : DecisionValue;

/// <summary>The answer to a Score question.</summary>
/// <param name="Level">The level picked, one of the request's levels.</param>
/// <param name="Index">The level's position in the request's list, zero-based.</param>
public sealed record ScoreValue(string Level, int Index) : DecisionValue;
