using System.Text;
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

    /// <summary>
    /// Checks the request against the protocol before any provider sees it. An invalid request is a
    /// mistake in the caller's process, so it is an exception here rather than a provider outcome.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The question is empty; the context is absent or <see langword="null"/>; a Choice request has
    /// fewer than two options; a Score request has fewer than two or more than ten levels, or a level
    /// that is empty or repeated; or a field belongs to another decision type. The message names the
    /// field and never quotes its value.
    /// </exception>
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(Question))
        {
            throw new ArgumentException("The question is empty.", nameof(Question));
        }

        // An absent `context` deserializes to a default element, whose kind is Undefined.
        if (Context.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("The context is absent.", nameof(Context));
        }

        if (Criteria is not null && Type != DecisionType.Boolean)
        {
            throw new ArgumentException("Criteria belong to a Boolean request only.", nameof(Criteria));
        }

        if (Options is not null && Type != DecisionType.Choice)
        {
            throw new ArgumentException("Options belong to a Choice request only.", nameof(Options));
        }

        if (Levels is not null && Type != DecisionType.Score)
        {
            throw new ArgumentException("Levels belong to a Score request only.", nameof(Levels));
        }

        switch (Type)
        {
            case DecisionType.Choice when Options is null || Options.Count < 2:
                throw new ArgumentException("A Choice request needs at least two options.", nameof(Options));
            case DecisionType.Score:
                EnsureLevels();
                break;
        }
    }

    /// <summary>
    /// The request's shape and nothing it carries. A request that lands in a log line, an exception
    /// message or an assertion failure names its type, the kind of its context and how many criteria
    /// sides, options or levels it has — never the question, the context or their text.
    /// </summary>
    public override string ToString()
    {
        StringBuilder text = new StringBuilder("DecisionRequest { Protocol = ")
            .Append(Protocol)
            .Append(", Type = ")
            .Append(Type)
            .Append(", Context = ")
            .Append(Context.ValueKind);

        if (Criteria is not null)
        {
            int sides = (Criteria.True is null ? 0 : 1) + (Criteria.False is null ? 0 : 1);
            text.Append(", Criteria = ").Append(sides);
        }

        if (Options is not null)
        {
            text.Append(", Options = ").Append(Options.Count);
        }

        if (Levels is not null)
        {
            text.Append(", Levels = ").Append(Levels.Count);
        }

        return text.Append(" }").ToString();
    }

    private void EnsureLevels()
    {
        if (Levels is null || Levels.Count is < 2 or > 10)
        {
            throw new ArgumentException("A Score request needs two to ten levels.", nameof(Levels));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string level in Levels)
        {
            if (string.IsNullOrWhiteSpace(level))
            {
                throw new ArgumentException("A level is empty.", nameof(Levels));
            }

            if (!seen.Add(level))
            {
                throw new ArgumentException("A level is repeated.", nameof(Levels));
            }
        }
    }
}
