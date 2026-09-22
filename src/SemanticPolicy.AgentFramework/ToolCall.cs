using System.Text.Json;

namespace SemanticPolicy;

/// <summary>
/// A tool call the model proposed, as the pre-tool guard sees it before the tool runs: the tool's
/// name and description, the arguments as JSON, the conversation the call came out of and an id for
/// telemetry. Nothing in it is a framework type, so a handler written against it moves to another
/// frontend unchanged.
/// </summary>
/// <param name="Name">The tool's name; non-blank.</param>
/// <param name="Description">The tool's description, or <see langword="null"/> when it has none.</param>
/// <param name="Arguments">The arguments as JSON, of any kind but undefined.</param>
/// <param name="Conversation">The messages of the operation up to the call, in order; the list may be empty.</param>
/// <param name="CorrelationId">
/// The id the verdict is tied to in the application's traces, one per call; non-blank and never derived
/// from the content.
/// </param>
public sealed record ToolCall(
    string Name,
    string? Description,
    JsonElement Arguments,
    IReadOnlyList<ConversationMessage> Conversation,
    string CorrelationId)
{
    /// <summary>The tool's name.</summary>
    public string Name { get; } = string.IsNullOrWhiteSpace(Name)
        ? throw new ArgumentException("A tool call needs a name.", nameof(Name))
        : Name;

    /// <summary>
    /// The arguments as JSON. Cloned on the way in, so the call stays readable after the
    /// <see cref="JsonDocument"/> they came from is disposed.
    /// </summary>
    public JsonElement Arguments { get; } = Arguments.ValueKind == JsonValueKind.Undefined
        ? throw new ArgumentException("A tool call needs its arguments.", nameof(Arguments))
        : Arguments.Clone();

    /// <summary>The messages of the operation up to the call, in order.</summary>
    public IReadOnlyList<ConversationMessage> Conversation { get; } =
        ConversationMessage.Copy(Conversation, nameof(Conversation));

    /// <summary>The id the verdict is tied to in the application's traces.</summary>
    public string CorrelationId { get; } = string.IsNullOrWhiteSpace(CorrelationId)
        ? throw new ArgumentException("A tool call needs a correlation id.", nameof(CorrelationId))
        : CorrelationId;

    /// <summary>
    /// What the user asked for: the text of every <c>user</c> message of the conversation, in order,
    /// one blank line between them, or the empty string when there is none. The role is the trust
    /// boundary — an assistant or tool message never counts, whatever it says — and intent accumulates
    /// over turns, which is why the last user message alone is not enough.
    /// </summary>
    public string UserRequest => ConversationMessage.Join(Conversation, "user");

    /// <summary>
    /// The call's shape and nothing it carries: the tool's name, the JSON kind of its arguments, how
    /// many messages came before it and the id it is judged under — never the arguments themselves,
    /// the conversation or the request read out of it.
    /// </summary>
    public override string ToString() =>
        $"ToolCall {{ Name = {Name}, Arguments = {Arguments.ValueKind}, Conversation = {Conversation.Count}, "
        + $"CorrelationId = {CorrelationId} }}";
}
