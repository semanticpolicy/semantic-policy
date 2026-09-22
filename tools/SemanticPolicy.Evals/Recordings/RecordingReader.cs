using System.Text.Json;

namespace SemanticPolicy.Evals.Recordings;

/// <summary>
/// A recording as read: the header and every row that survived in the file. A run killed halfway leaves
/// fewer rows than the dataset has, which is a partial recording and not an error — the verbs say how many
/// of how many rows they read.
/// </summary>
/// <param name="Path">The path the recording was read from, as it was given.</param>
/// <param name="Header">The header line.</param>
/// <param name="Rows">The rows, in the order they were written.</param>
public sealed record Recording(string Path, RecordingHeader Header, IReadOnlyList<RecordedRow> Rows);

/// <summary>
/// Reads a recording written by <see cref="RecordingWriter"/>. The format marker is checked before anything
/// else is believed, and every error names the line it was found on, because a torn last line is the normal
/// shape of an interrupted run.
/// </summary>
public static class RecordingReader
{
    /// <summary>Reads the whole file. Blank lines are skipped but counted, so line numbers match an editor.</summary>
    /// <param name="path">The recording to read.</param>
    /// <exception cref="EvalsException">
    /// The file cannot be read, holds no header, carries another format, or has a line that cannot be parsed;
    /// the message names the line.
    /// </exception>
    public static Recording Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new EvalsException($"Recording '{path}' cannot be read: {e.Message}");
        }

        RecordingHeader? header = null;
        List<RecordedRow> rows = [];
        for (int index = 0; index < lines.Length; index++)
        {
            string text = lines[index];
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            int line = index + 1;
            if (header is null)
            {
                header = ReadHeader(path, line, text);
            }
            else
            {
                rows.Add(Parse<RecordedRow>(path, line, text));
            }
        }

        return header is null
            ? throw new EvalsException($"Recording '{path}' holds no header line; it is empty.")
            : new Recording(path, header, rows);
    }

    // The format is read off the raw document rather than off a deserialized header: a file written by a
    // later version may not fit this version's shape at all, and "the format is v1" is the useful error.
    private static RecordingHeader ReadHeader(string path, int line, string text)
    {
        string? format = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("format", out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                format = value.GetString();
            }
        }
        catch (JsonException e)
        {
            throw Torn(path, line, e);
        }

        if (format is null)
        {
            throw Fail(path, line, $"the first line is a header with a 'format' of '{RecordingHeader.FormatV0}'.");
        }

        if (!string.Equals(format, RecordingHeader.FormatV0, StringComparison.Ordinal))
        {
            throw Fail(path, line, $"the format is '{format}'; this tool reads '{RecordingHeader.FormatV0}'.");
        }

        return Parse<RecordingHeader>(path, line, text);
    }

    private static T Parse<T>(string path, int line, string text)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, SemanticPolicyJson.Options)
                ?? throw Fail(path, line, "the line holds null.");
        }
        catch (JsonException e)
        {
            throw Torn(path, line, e);
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            // A record constructor rejecting a null member surfaces as an ArgumentException, so a line that
            // parses as JSON but is missing a required property arrives here rather than as a JsonException.
            throw Fail(path, line, $"the line is not a recording line: {e.Message}");
        }
    }

    // Only the position is passed on: the parser's own message can quote the token it stopped at.
    private static EvalsException Torn(string path, int line, JsonException error) =>
        Fail(path, line, $"the line is not valid JSON (byte {error.BytePositionInLine ?? 0}); an interrupted run leaves a torn last line.");

    private static EvalsException Fail(string path, int line, string error) =>
        new($"Recording '{path}', line {line}: {error}");
}
