namespace SemanticPolicy;

/// <summary>
/// What a tool returned, as the post-tool guard sees it before the model does: the call it answers
/// and the value as the tool produced it. Nothing in it is a framework type, so a handler written
/// against it moves to another frontend unchanged.
/// </summary>
/// <param name="Call">The call the value answers.</param>
/// <param name="Value">
/// The tool's result as the frontend has it: a string, a <see cref="System.Text.Json.JsonElement"/>,
/// any object, or <see langword="null"/>.
/// </param>
public sealed record ToolResult(ToolCall Call, object? Value)
{
    /// <summary>The call the value answers.</summary>
    public ToolCall Call { get; } = Call ?? throw new ArgumentNullException(nameof(Call));
}
