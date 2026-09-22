namespace SemanticPolicy;

/// <summary>
/// One message of the conversation a guarded operation runs in, reduced to what a policy can be
/// asked about: who said it and what was said. The role is the trust boundary the default contexts
/// read — <c>user</c> against <c>assistant</c>, <c>system</c> and <c>tool</c> — so it is kept as the
/// frontend named it and compared ordinally, never normalised.
/// </summary>
/// <param name="Role">The speaker's role, as the frontend names it; non-blank.</param>
/// <param name="Text">
/// The message's text, as the frontend has it. A message of white space only says nothing and is
/// skipped by every join.
/// </param>
public sealed record ConversationMessage(string Role, string Text)
{
    /// <summary>The speaker's role, compared ordinally.</summary>
    public string Role { get; } = string.IsNullOrWhiteSpace(Role)
        ? throw new ArgumentException("A message needs a role.", nameof(Role))
        : Role;

    /// <summary>The message's text.</summary>
    public string Text { get; } = Text ?? throw new ArgumentNullException(nameof(Text));

    /// <summary>
    /// Who spoke and nothing that was said. A message that lands in a log line, an exception message
    /// or an assertion failure names its role, never its text, so the printing a record would
    /// otherwise synthesize is replaced here.
    /// </summary>
    public override string ToString() => $"ConversationMessage {{ Role = {Role} }}";

    /// <summary>
    /// The messages' texts in order, one blank line between them, skipping any that is white space
    /// only: every message when <paramref name="role"/> is <see langword="null"/>, otherwise those of
    /// that role. The join is the layer's; a frontend hands over the messages and never joins them.
    /// </summary>
    internal static string Join(IReadOnlyList<ConversationMessage> messages, string? role) =>
        string.Join(
            "\n\n",
            messages
                .Where(message => role is null || string.Equals(message.Role, role, StringComparison.Ordinal))
                .Where(message => !string.IsNullOrWhiteSpace(message.Text))
                .Select(message => message.Text));

    /// <summary>A private copy of the list, with no null entry; the list may be empty.</summary>
    internal static IReadOnlyList<ConversationMessage> Copy(IReadOnlyList<ConversationMessage> messages, string paramName)
    {
        ArgumentNullException.ThrowIfNull(messages, paramName);
        ConversationMessage[] copy = [.. messages];
        if (Array.Exists(copy, message => message is null))
        {
            throw new ArgumentException("A message is null.", paramName);
        }

        return copy;
    }
}
