using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticPolicy.Protocol;

/// <summary>
/// What a provider call produced: on one axis what happened to the call, on the other — for a success —
/// the provider's answer with its evidence. The answer is an estimate; what it means for the
/// application is the policy's to decide.
/// </summary>
/// <param name="Type">The decision type, echoed from the request.</param>
/// <param name="Outcome">What happened to the call.</param>
/// <param name="Value">The provider's answer; present exactly when the outcome is a success.</param>
/// <param name="Evidence">
/// The numbers behind the answer, each with its kind. Empty when the provider returned none.
/// </param>
/// <param name="Provider">Who answered.</param>
/// <param name="Raw">
/// The provider's response as received, for evaluation and replay. Held in memory only: it is neither
/// written to nor read from the wire, so a result that lands in a log carries no content by accident.
/// </param>
public sealed record ProviderResult(
    DecisionType Type,
    ProviderOutcome Outcome,
    DecisionValue? Value,
    IReadOnlyList<Evidence> Evidence,
    ProviderMetadata Provider,
    [property: JsonIgnore] JsonElement? Raw = null)
{
    /// <summary>The protocol version this result is written in.</summary>
    [JsonPropertyOrder(-1)]
    public string Protocol { get; init; } = ProtocolVersion.V0;

    /// <summary>
    /// A result for a call that failed: the outcome carries the kind and the message, and there is no
    /// answer and no evidence.
    /// </summary>
    /// <param name="type">The decision type of the request.</param>
    /// <param name="kind">Why the call failed.</param>
    /// <param name="message">A note for the log. It is never content from the context.</param>
    /// <param name="provider">Who was asked.</param>
    public static ProviderResult Failed(DecisionType type, FailureKind kind, string message, ProviderMetadata provider) =>
        new(type, ProviderOutcome.Failure(kind, message), Value: null, Evidence: [], provider);

    /// <summary>
    /// The result's shape and its answer, nothing the provider sent back. A result that lands in a log
    /// line names its type, what happened to the call, the answer, how much evidence came with it, who
    /// answered and the kind of the raw response — never the raw response itself.
    /// </summary>
    public override string ToString()
    {
        StringBuilder text = new StringBuilder("ProviderResult { Protocol = ")
            .Append(Protocol)
            .Append(", Type = ")
            .Append(Type)
            .Append(", Outcome = ")
            .Append(Outcome.Status);

        if (Outcome.Kind is not null)
        {
            text.Append(", Kind = ").Append(Outcome.Kind);
        }

        if (Value is not null)
        {
            text.Append(", Value = ").Append(Value);
        }

        text.Append(", Evidence = ").Append(Evidence.Count).Append(", Provider = ").Append(Provider.Id);

        if (Raw is not null)
        {
            text.Append(", Raw = ").Append(Raw.Value.ValueKind);
        }

        return text.Append(" }").ToString();
    }
}
