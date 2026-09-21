using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticPolicy.Protocol;

// The wire carries no discriminator: a Boolean answer is a JSON boolean, a Choice answer a string and a
// Score answer an object, so the shape is read from the token and written back the same way.
internal sealed class DecisionValueJsonConverter : JsonConverter<DecisionValue>
{
    public override DecisionValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => new BooleanValue(true),
            JsonTokenType.False => new BooleanValue(false),
            JsonTokenType.String => new ChoiceValue(reader.GetString()!),
            JsonTokenType.StartObject => ReadScore(ref reader, options),
            _ => throw new JsonException(
                $"A decision value is a boolean, a string or an object, not a {reader.TokenType} token."),
        };
    }

    public override void Write(Utf8JsonWriter writer, DecisionValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case BooleanValue boolean:
                writer.WriteBooleanValue(boolean.Value);
                break;
            case ChoiceValue choice:
                writer.WriteStringValue(choice.Option);
                break;
            case ScoreValue score:
                writer.WriteStartObject();
                writer.WriteString("level", score.Level);
                writer.WriteNumber("index", score.Index);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"{value.GetType()} is not a decision value the protocol defines.");
        }
    }

    private static ScoreValue ReadScore(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        string? level = null;
        int? index = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString()!;
            reader.Read();

            if (Matches(name, "level", options))
            {
                level = reader.TokenType == JsonTokenType.String
                    ? reader.GetString()
                    : throw new JsonException("A score value's level is a string.");
            }
            else if (Matches(name, "index", options))
            {
                index = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int parsed)
                    ? parsed
                    : throw new JsonException("A score value's index is an integer.");
            }
            else
            {
                reader.Skip();
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("A score value is an object.");
        }

        return level is not null && index is not null
            ? new ScoreValue(level, index.Value)
            : throw new JsonException("A score value carries both a level and an index.");
    }

    private static bool Matches(string name, string expected, JsonSerializerOptions options) =>
        string.Equals(
            name,
            expected,
            options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
