using System.Text.Json;

namespace SemanticPolicy;

/// <summary>
/// One named piece of a <see cref="SemanticContext"/>: text as the application has it, or JSON that
/// stays structured for a provider that reads structure. A part is made with <see cref="Text"/> or
/// <see cref="Json"/> and never changes afterwards.
/// </summary>
public abstract record ContextPart
{
    private ContextPart(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A context part needs a name.", nameof(name));
        }

        Name = name;
    }

    /// <summary>The part's name, which is its property name on the wire.</summary>
    public string Name { get; }

    /// <summary>A text part.</summary>
    /// <param name="name">The part's name; non-empty.</param>
    /// <param name="text">The text, rendered to a provider as it is.</param>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    public static ContextPart Text(string name, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new TextPart(name, text);
    }

    /// <summary>
    /// A JSON part. The element is cloned on the way in, so the part stays readable after the
    /// <see cref="JsonDocument"/> it came from is disposed.
    /// </summary>
    /// <param name="name">The part's name; non-empty.</param>
    /// <param name="json">The value, of any JSON kind.</param>
    /// <exception cref="ArgumentException">The name is empty, or the element is undefined.</exception>
    public static ContextPart Json(string name, JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A JSON part needs a value.", nameof(json));
        }

        return new JsonPart(name, json.Clone());
    }

    internal abstract void WriteValue(Utf8JsonWriter writer);

    private sealed record TextPart : ContextPart
    {
        public TextPart(string name, string text)
            : base(name)
        {
            Value = text;
        }

        public string Value { get; }

        internal override void WriteValue(Utf8JsonWriter writer) => writer.WriteStringValue(Value);
    }

    private sealed record JsonPart : ContextPart
    {
        public JsonPart(string name, JsonElement json)
            : base(name)
        {
            Value = json;
        }

        public JsonElement Value { get; }

        internal override void WriteValue(Utf8JsonWriter writer) => Value.WriteTo(writer);
    }
}
