using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SemanticPolicy.Evals.Datasets;

/// <summary>
/// Reads dataset schema v0: one JSON object per line with <c>id</c>, <c>input</c>, <c>label</c> and an
/// optional <c>metadata</c> object. Every error names the path, the line and, once it is known, the id, and
/// never the input, because the message is printed as it is.
/// </summary>
public static class DatasetReader
{
    private static readonly HashSet<string> _rowProperties = new(StringComparer.Ordinal)
    {
        "id",
        "input",
        "label",
        "metadata",
    };

    /// <summary>Reads the whole file. Blank lines are skipped but counted, so line numbers match an editor.</summary>
    /// <param name="path">The file to read.</param>
    /// <exception cref="EvalsException">The file cannot be read, or a row is malformed; the message says where.</exception>
    public static Dataset Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new EvalsException($"Dataset '{path}' cannot be read: {e.Message}");
        }

        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        List<DatasetRow> rows = [];
        Dictionary<string, int> lineOfId = new(StringComparer.Ordinal);
        using StreamReader reader = new(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        int line = 0;
        while (reader.ReadLine() is { } text)
        {
            line++;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            DatasetRow row = ParseRow(path, line, text);
            if (!lineOfId.TryAdd(row.Id, line))
            {
                throw Fail(path, line, row.Id, $"the id was already used on line {lineOfId[row.Id]}.");
            }

            rows.Add(row);
        }

        return new Dataset(path, sha256, rows);
    }

    private static DatasetRow ParseRow(string path, int line, string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            // The parser's own message can quote the offending token, so only its position is passed on.
            throw Fail(path, line, null, $"the line is not valid JSON (byte {e.BytePositionInLine ?? 0}).");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Fail(path, line, null, "a row is a JSON object.");
            }

            string id = ReadId(path, line, root);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!_rowProperties.Contains(property.Name))
                {
                    throw Fail(path, line, id, $"unknown property '{property.Name}'; a row has id, input, label and metadata.");
                }
            }

            return new DatasetRow(
                id,
                line,
                ReadInput(path, line, id, root),
                ReadLabel(path, line, id, root),
                ReadMetadata(path, line, id, root));
        }
    }

    private static string ReadId(string path, int line, JsonElement root)
    {
        if (!root.TryGetProperty("id", out JsonElement id))
        {
            throw Fail(path, line, null, "the row has no 'id'.");
        }

        if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
        {
            throw Fail(path, line, null, "'id' is a non-empty string.");
        }

        return id.GetString()!;
    }

    private static SemanticContext ReadInput(string path, int line, string id, JsonElement root)
    {
        if (!root.TryGetProperty("input", out JsonElement input))
        {
            throw Fail(path, line, id, "the row has no 'input'.");
        }

        try
        {
            return input.ValueKind switch
            {
                JsonValueKind.String => SemanticContext.FromText(input.GetString()!),
                JsonValueKind.Object => new SemanticContext([.. input.EnumerateObject().Select(ToPart)]),
                _ => throw Fail(path, line, id, "'input' is a string or an object."),
            };
        }
        catch (ArgumentException e)
        {
            // An empty object, an empty part name or a repeated one; the context's own messages carry no content.
            throw Fail(path, line, id, $"'input' is not a valid context: {e.Message}");
        }
    }

    // A string property is text a provider reads as it is; anything else stays JSON, the way the runtime's
    // wire shape keeps it.
    private static ContextPart ToPart(JsonProperty property) =>
        property.Value.ValueKind == JsonValueKind.String
            ? ContextPart.Text(property.Name, property.Value.GetString()!)
            : ContextPart.Json(property.Name, property.Value);

    private static RowLabel ReadLabel(string path, int line, string id, JsonElement root)
    {
        if (!root.TryGetProperty("label", out JsonElement label))
        {
            throw Fail(path, line, id, "the row has no 'label'.");
        }

        switch (label.ValueKind)
        {
            case JsonValueKind.True:
                return new RowLabel(RowLabelKind.Answer, "true");
            case JsonValueKind.False:
                return new RowLabel(RowLabelKind.Answer, "false");
            case JsonValueKind.String:
                string text = label.GetString()!;
                if (text.Length == 0)
                {
                    throw Fail(path, line, id, "'label' is empty.");
                }

                return text switch
                {
                    "ambiguous" => new RowLabel(RowLabelKind.Ambiguous, null),
                    "abstain" => new RowLabel(RowLabelKind.Abstain, null),
                    _ => new RowLabel(RowLabelKind.Answer, text),
                };
            default:
                throw Fail(path, line, id, "'label' is a string or a JSON boolean.");
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadMetadata(string path, int line, string id, JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out JsonElement metadata))
        {
            return ReadOnlyDictionary<string, JsonElement>.Empty;
        }

        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw Fail(path, line, id, "'metadata' is an object.");
        }

        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in metadata.EnumerateObject())
        {
            // Cloned: the row outlives the document its line was parsed from.
            if (!values.TryAdd(property.Name, property.Value.Clone()))
            {
                throw Fail(path, line, id, $"metadata repeats the key '{property.Name}'.");
            }
        }

        return values;
    }

    private static EvalsException Fail(string path, int line, string? id, string error)
    {
        string where = id is null
            ? $"Dataset '{path}', line {line}"
            : $"Dataset '{path}', line {line}, id '{id}'";
        return new EvalsException($"{where}: {error}");
    }
}
