using System.Buffers;
using System.Text.Json;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.SystemOne;

/// <summary>
/// Writes a <see cref="DecisionRequest"/> as a System One body: the model, the context exactly as it is
/// under <c>state</c>, and one question under a fixed key. The property names are the wire's, spelled
/// here and nowhere else.
/// </summary>
internal static class SystemOneRequest
{
    /// <summary>The key the single question is sent under and its answer is read from.</summary>
    public const string QuestionKey = "decision";

    /// <summary>The body as UTF-8.</summary>
    public static byte[] Write(DecisionRequest request, string model)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WritePropertyName("state");
            request.Context.WriteTo(writer);
            writer.WriteStartObject("questions");
            writer.WriteStartObject(QuestionKey);
            writer.WriteString("type", WireType(request.Type));
            writer.WriteString("instructions", request.Question);
            WriteCriteria(writer, request);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The wire's name for a decision type. Its Boolean is spelled <c>noul</c>.</summary>
    public static string WireType(DecisionType type) =>
        type switch
        {
            DecisionType.Boolean => "noul",
            DecisionType.Choice => "choice",
            DecisionType.Score => "score",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    private static void WriteCriteria(Utf8JsonWriter writer, DecisionRequest request)
    {
        switch (request.Type)
        {
            case DecisionType.Boolean:
                if (request.Criteria is not { } criteria || (criteria.True is null && criteria.False is null))
                {
                    return;
                }

                writer.WriteStartObject("criteria");
                if (criteria.True is { } whenTrue)
                {
                    writer.WriteString("true", whenTrue);
                }

                if (criteria.False is { } whenFalse)
                {
                    writer.WriteString("false", whenFalse);
                }

                writer.WriteEndObject();
                break;
            case DecisionType.Choice:
                writer.WriteStartObject("criteria");
                foreach ((string key, string meaning) in request.Options!)
                {
                    writer.WriteString(key, meaning);
                }

                writer.WriteEndObject();
                break;
            case DecisionType.Score:
                writer.WriteStartArray("criteria");
                foreach (string level in request.Levels!)
                {
                    writer.WriteStringValue(level);
                }

                writer.WriteEndArray();
                break;
        }
    }
}
