using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticPolicy.Protocol;

/// <summary>
/// One context and one typed question, as it goes to a provider. What comes back is the provider's
/// estimate about the context, never a ruling on it.
/// </summary>
/// <param name="Type">The shape of the answer asked for.</param>
/// <param name="Question">The question, as the provider reads it.</param>
/// <param name="Context">
/// The thing being judged, as the application has it: a string, an object or an array.
/// </param>
/// <param name="Criteria">For a Boolean question, what each answer looks like; otherwise absent.</param>
/// <param name="Options">
/// For a Choice question, the answers a provider may pick — the key is what the result carries, the
/// text is what it means; otherwise absent.
/// </param>
/// <param name="Levels">For a Score question, the answers ordered from lowest to highest; otherwise absent.</param>
public sealed record DecisionRequest(
    DecisionType Type,
    string Question,
    JsonElement Context,
    BooleanCriteria? Criteria = null,
    IReadOnlyDictionary<string, string>? Options = null,
    IReadOnlyList<string>? Levels = null)
{
    /// <summary>The protocol version this request is written in.</summary>
    [JsonPropertyOrder(-1)]
    public string Protocol { get; init; } = ProtocolVersion.V0;
}
