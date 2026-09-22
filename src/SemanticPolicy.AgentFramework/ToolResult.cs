using System.Text.Json;

namespace SemanticPolicy;

/// <summary>
/// What a tool returned, as the post-tool guard sees it before the model does: the call it answers
/// and the value as the tool produced it. Nothing in it is a framework type, so a handler written
/// against it moves to another frontend unchanged.
/// </summary>
/// <param name="Call">The call the value answers.</param>
/// <param name="Value">
/// The tool's result as the frontend has it: a string, a <see cref="JsonElement"/>, any object, or
/// <see langword="null"/>. A JSON element is of any kind but undefined.
/// </param>
public sealed record ToolResult(ToolCall Call, object? Value)
{
    /// <summary>The call the value answers.</summary>
    public ToolCall Call { get; } = Call ?? throw new ArgumentNullException(nameof(Call));

    /// <summary>
    /// The tool's result. A <see cref="JsonElement"/> is checked and cloned on the way in, the way a
    /// call's arguments are, so the result stays readable after the <see cref="JsonDocument"/> it came
    /// from is disposed and an element the guard could not read fails here rather than at evaluation.
    /// Any other value is kept as the tool produced it.
    /// </summary>
    public object? Value { get; } = Value is JsonElement element
        ? element.ValueKind == JsonValueKind.Undefined
            ? throw new ArgumentException("A tool result needs its value.", nameof(Value))
            : element.Clone()
        : Value;

    /// <summary>
    /// The result's shape and nothing it carries: the call it answers and the kind of value it holds,
    /// never the value. A result that lands in a log line, an exception message or an assertion
    /// failure is safe to print.
    /// </summary>
    public override string ToString() => $"ToolResult {{ Call = {Call}, Value = {Kind(Value)} }}";

    // The JSON kind for an element, "null" for nothing, the runtime type's name for anything else.
    private static string Kind(object? value) =>
        value switch
        {
            null => "null",
            JsonElement element => $"Json {element.ValueKind}",
            _ => value.GetType().Name,
        };
}
