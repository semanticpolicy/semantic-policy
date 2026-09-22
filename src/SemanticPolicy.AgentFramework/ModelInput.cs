namespace SemanticPolicy;

/// <summary>
/// What a run is about to send to the model, as the pre-model guard sees it: the operation's messages
/// and an id for telemetry. Nothing in it is a framework type, so a handler written against it moves
/// to another frontend unchanged.
/// </summary>
/// <param name="Messages">The messages of the operation, in order; the list may be empty.</param>
/// <param name="CorrelationId">
/// The id the verdict is tied to in the application's traces, one per run; non-blank and never derived
/// from the content.
/// </param>
public sealed record ModelInput(IReadOnlyList<ConversationMessage> Messages, string CorrelationId)
{
    /// <summary>The messages of the operation, in order.</summary>
    public IReadOnlyList<ConversationMessage> Messages { get; } = ConversationMessage.Copy(Messages, nameof(Messages));

    /// <summary>The id the verdict is tied to in the application's traces.</summary>
    public string CorrelationId { get; } = string.IsNullOrWhiteSpace(CorrelationId)
        ? throw new ArgumentException("A model input needs a correlation id.", nameof(CorrelationId))
        : CorrelationId;

    /// <summary>
    /// The text of every message that says something, in order, one blank line between them: what the
    /// default pre-model context asks the policy about.
    /// </summary>
    public string Text => ConversationMessage.Join(Messages, role: null);
}
