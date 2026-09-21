using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SemanticPolicy;

/// <summary>
/// The thing a policy is asked about: ordered, named parts of text or JSON, plus a correlation id for
/// telemetry. On the wire the context is one JSON object with a property per part; the correlation id
/// never leaves the process and is never derived from the content.
/// </summary>
/// <param name="Parts">The parts, in the order a provider reads them: at least one, each with its own name.</param>
/// <param name="CorrelationId">An id for telemetry, or <see langword="null"/>. Never a hash of the content.</param>
public sealed record SemanticContext(IReadOnlyList<ContextPart> Parts, string? CorrelationId = null)
{
    // Non-ASCII stays literal so that a text-only provider reads "ключ", not "\u043A\u043B\u044E\u0447".
    private static readonly JsonWriterOptions _compactUnescaped = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The parts, in the order a provider reads them.</summary>
    public IReadOnlyList<ContextPart> Parts { get; } = EnsureParts(Parts);

    /// <summary>A context of one text part named <c>text</c>.</summary>
    /// <param name="text">The text, rendered to a provider as it is.</param>
    /// <param name="correlationId">An id for telemetry, or <see langword="null"/>.</param>
    public static SemanticContext FromText(string text, string? correlationId = null) =>
        new([ContextPart.Text("text", text)], correlationId);

    /// <summary>
    /// The wire shape: one object with a property per part in declared order, a text part as a string
    /// and a JSON part embedded as it is. The correlation id is not in it.
    /// </summary>
    public JsonElement ToJson()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, _compactUnescaped))
        {
            writer.WriteStartObject();
            foreach (ContextPart part in Parts)
            {
                writer.WritePropertyName(part.Name);
                part.WriteValue(writer);
            }

            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// This context rendered to text, exactly as <see cref="ToCanonicalText(JsonElement)"/> renders its
    /// <see cref="ToJson"/>: there is one rendering, whichever side of the wire asks for it.
    /// </summary>
    public string ToCanonicalText() => ToCanonicalText(ToJson());

    /// <summary>
    /// Renders a wire context to text for a provider that reads text only, the same way everywhere, so
    /// that two providers compared on one input differ in the model and not in the rendering. A string
    /// renders as it is. An object with exactly one string-valued property renders as that string. Any
    /// other object renders one block per property in declared order — the name, a colon, a line
    /// break, then a string as it is or any other value as compact JSON — with a blank line between
    /// blocks. An array, or any other kind, renders as compact JSON. Non-ASCII stays literal, every line
    /// break is LF, and there is no trailing newline. The question and the correlation id are never
    /// part of it.
    /// </summary>
    /// <param name="context">The <c>context</c> of a decision request.</param>
    /// <exception cref="ArgumentException">The element is undefined.</exception>
    public static string ToCanonicalText(JsonElement context)
    {
        return context.ValueKind switch
        {
            JsonValueKind.Undefined => throw new ArgumentException("The context is absent.", nameof(context)),
            JsonValueKind.String => context.GetString()!,
            JsonValueKind.Object => RenderObject(context),
            _ => Compact(context),
        };
    }

    private static string RenderObject(JsonElement context)
    {
        JsonProperty[] properties = [.. context.EnumerateObject()];
        if (properties.Length == 1 && properties[0].Value.ValueKind == JsonValueKind.String)
        {
            return properties[0].Value.GetString()!;
        }

        StringBuilder text = new();
        for (int i = 0; i < properties.Length; i++)
        {
            if (i > 0)
            {
                text.Append("\n\n");
            }

            JsonElement value = properties[i].Value;
            text.Append(properties[i].Name)
                .Append(":\n")
                .Append(value.ValueKind == JsonValueKind.String ? value.GetString() : Compact(value));
        }

        return text.ToString();
    }

    private static string Compact(JsonElement element)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, _compactUnescaped))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static IReadOnlyList<ContextPart> EnsureParts(IReadOnlyList<ContextPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ContextPart[] copy = [.. parts];
        if (copy.Length == 0)
        {
            throw new ArgumentException("A context needs at least one part.", nameof(Parts));
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (ContextPart part in copy)
        {
            if (part is null)
            {
                throw new ArgumentException("A context part is null.", nameof(Parts));
            }

            if (!names.Add(part.Name))
            {
                throw new ArgumentException("Two context parts share a name.", nameof(Parts));
            }
        }

        return copy;
    }
}
