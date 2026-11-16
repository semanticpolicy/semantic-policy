using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SemanticPolicy.Evals.Format;

/// <summary>
/// Writes a dataset one row at a time, in the shape <see cref="DatasetReader"/> reads: <c>id</c>, <c>input</c>,
/// <c>label</c>, <c>options</c> when the row carries them and <c>metadata</c> when it has any. The bytes do not depend
/// on the machine — UTF-8 without a byte-order mark, one <c>\n</c> per row — and <see cref="CompleteAsync"/> returns
/// their SHA-256, the digest a recording's header names the file by.
/// </summary>
/// <remarks>
/// <para>
/// The writer refuses a row that would read back as a different row, or not at all, rather than write it:
/// </para>
/// <list type="bullet">
/// <item><description>an id that is empty, white space, or already written;</description></item>
/// <item><description>
/// a <see cref="RowLabelKind.Answer"/> label whose answer is <see langword="null"/>, empty, <c>ambiguous</c> or
/// <c>abstain</c>, the last two of which read back as those kinds;
/// </description></item>
/// <item><description>an <see cref="RowLabelKind.Ambiguous"/> or <see cref="RowLabelKind.Abstain"/> label carrying an answer;</description></item>
/// <item><description>a JSON input part whose value is a JSON string, which reads back as a text part;</description></item>
/// <item><description>a metadata entry with no value;</description></item>
/// <item><description>
/// a rule in <see cref="DatasetRow.Options"/> with no list, or an option that is null, has a null key or description,
/// or has a verdict that is not a <see cref="Verdict"/> name. Whether the options make a valid rule is not the
/// writer's to say: <see cref="Dataset.ForPolicy"/> checks that when the file is read for a policy.
/// </description></item>
/// </list>
/// <para>
/// A refused row writes nothing, and the writer takes the next one. A row that fails while it is written, or whose
/// write is cancelled, may have left part of itself in the file, so the writer is faulted: it takes no more rows and
/// does not complete, and the file is to be discarded. <see cref="DatasetRow.Line"/> is ignored: the
/// line a row lands on is decided by the rows before it.
/// </para>
/// </remarks>
public sealed class DatasetWriter : IAsyncDisposable
{
    private static readonly byte[] _newline = "\n"u8.ToArray();

    // The default encoder escapes for embedding in HTML, which turns '+', '<' or a non-ASCII letter into \uXXXX;
    // a dataset is a file, never a page. Control characters are still escaped, so a row never spans two lines.
    private static readonly JsonWriterOptions _options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly FileStream _stream;
    private readonly string _path;
    private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Dictionary<string, int> _rowOfId = new(StringComparer.Ordinal);
    private int _rows;
    private string? _digest;
    private bool _faulted;

    private DatasetWriter(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    /// <summary>Creates the file, replacing one that is there.</summary>
    /// <param name="path">Where the dataset goes.</param>
    /// <param name="cancellationToken">Cancels the call before the file is created.</param>
    /// <returns>A writer positioned at the start of the empty file.</returns>
    /// <exception cref="EvalsFormatException">The file cannot be created; the message names the path.</exception>
    public static Task<DatasetWriter> CreateAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new EvalsFormatException($"Dataset '{path}' cannot be written: {e.Message}");
        }

        return Task.FromResult(new DatasetWriter(stream, path));
    }

    /// <summary>Appends one row, or refuses it and leaves the file as it was.</summary>
    /// <param name="row">The row. Its <see cref="DatasetRow.Line"/> is ignored.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="EvalsFormatException">
    /// The row would not read back as itself, or it cannot be written. The message names the row's id, or its
    /// number in the file when the id is at fault, and never its input or metadata.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The write was cancelled. The file may hold part of the row, so the writer is faulted.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="CompleteAsync"/> was already called, or an earlier write failed or was cancelled.
    /// </exception>
    public async Task WriteAsync(DatasetRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_digest is not null)
        {
            throw new InvalidOperationException("The dataset is complete; no row can be added to it.");
        }

        ThrowIfFaulted();

        int number = _rows + 1;
        JsonElement input = row.Input.ToJson();
        Validate(row, input, number);

        // Serialised in full first: a row that cannot be written must not leave a half-line in the file.
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter json = new(buffer, _options))
        {
            WriteRow(json, row, input);
        }

        buffer.Write(_newline);

        try
        {
            await _stream.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Part of the row may already sit in the stream's buffer while the digest and the row count have not
            // moved, so the next row would be flushed behind bytes the digest never saw.
            _faulted = true;
            if (e is IOException)
            {
                throw new EvalsFormatException($"Dataset '{_path}', id '{row.Id}': the row cannot be written: {e.Message}");
            }

            throw;
        }

        _sha256.AppendData(buffer.WrittenSpan);
        _rowOfId.Add(row.Id, number);
        _rows = number;
    }

    /// <summary>
    /// Flushes and closes the file, and returns the lowercase hex SHA-256 of every byte written: the value
    /// <see cref="DatasetReader.Read"/> reports as <see cref="Dataset.Sha256"/> for the file.
    /// </summary>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>The digest of the file's bytes.</returns>
    /// <exception cref="EvalsFormatException">The file cannot be flushed; the message names the path.</exception>
    /// <exception cref="InvalidOperationException">An earlier write failed or was cancelled, so no digest would match the file.</exception>
    public async Task<string> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_digest is not null)
        {
            return _digest;
        }

        ThrowIfFaulted();

        try
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw new EvalsFormatException($"Dataset '{_path}' cannot be written: {e.Message}");
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _digest = Convert.ToHexStringLower(_sha256.GetHashAndReset());
        _sha256.Dispose();
        return _digest;
    }

    /// <summary>
    /// Closes the file. A writer that was not completed leaves every row it wrote, though the last may not have
    /// reached the disk.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _sha256.Dispose();
    }

    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException(
                $"An earlier write to dataset '{_path}' failed, so the file may not match the rows written; discard it.");
        }
    }

    private void Validate(DatasetRow row, JsonElement input, int number)
    {
        if (string.IsNullOrWhiteSpace(row.Id))
        {
            throw Refuse(number, "'id' is a non-empty string that is not white space.");
        }

        if (_rowOfId.TryGetValue(row.Id, out int first))
        {
            throw Refuse(
                number,
                string.Create(CultureInfo.InvariantCulture, $"the id '{row.Id}' was already written as row {first}."));
        }

        switch (row.Label)
        {
            case { Kind: RowLabelKind.Answer, Answer: null or "" }:
                throw Refuse(row.Id, "an answer label is a non-empty string.");
            case { Kind: RowLabelKind.Answer, Answer: "ambiguous" or "abstain" }:
                throw Refuse(row.Id, $"an answer label '{row.Label.Answer}' reads back as the {row.Label.Answer} kind; write that kind instead.");
            case { Kind: RowLabelKind.Ambiguous or RowLabelKind.Abstain, Answer: not null }:
                throw Refuse(row.Id, "an ambiguous or abstain label carries no answer.");
        }

        int index = 0;
        foreach (JsonProperty part in input.EnumerateObject())
        {
            // A text part is written as a JSON string, and the reader reads every JSON string back as text, so a JSON
            // part holding a string would come back as a text part. A part is a text part when it equals one.
            if (part.Value.ValueKind == JsonValueKind.String
                && !row.Input.Parts[index].Equals(ContextPart.Text(part.Name, part.Value.GetString()!)))
            {
                throw Refuse(row.Id, $"input part '{part.Name}' is a JSON string, which reads back as text; write it as a text part.");
            }

            index++;
        }

        foreach ((string key, JsonElement value) in row.Metadata)
        {
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                throw Refuse(row.Id, $"metadata '{key}' has no value.");
            }
        }

        foreach ((string rule, IReadOnlyList<ChoiceOption> options) in row.Options ?? ReadOnlyDictionary<string, IReadOnlyList<ChoiceOption>>.Empty)
        {
            if (options is null)
            {
                throw Refuse(row.Id, $"rule '{rule}' has no options list.");
            }

            for (int at = 0; at < options.Count; at++)
            {
                string? error = options[at] switch
                {
                    null => "the option is null.",
                    { Key: null } => "the key is null.",
                    { Description: null } => "the description is null.",
                    { Verdict: var verdict } when !Enum.IsDefined(verdict) => "the verdict is not a Verdict name.",
                    _ => null,
                };
                if (error is not null)
                {
                    throw Refuse(row.Id, string.Create(CultureInfo.InvariantCulture, $"rule '{rule}', option at index {at}: {error}"));
                }
            }
        }
    }

    // Called only on a validated row, whose string-valued parts are all text parts.
    private static void WriteRow(Utf8JsonWriter json, DatasetRow row, JsonElement input)
    {
        json.WriteStartObject();
        json.WriteString("id", row.Id);
        json.WritePropertyName("input");

        // A context of one text part named "text" is what the reader makes of a string input, so it is written as one.
        if (row.Input.Parts is [{ Name: "text" }] && input.GetProperty("text") is { ValueKind: JsonValueKind.String } text)
        {
            text.WriteTo(json);
        }
        else
        {
            input.WriteTo(json);
        }

        json.WritePropertyName("label");
        switch (row.Label)
        {
            case { Kind: RowLabelKind.Ambiguous }:
                json.WriteStringValue("ambiguous");
                break;
            case { Kind: RowLabelKind.Abstain }:
                json.WriteStringValue("abstain");
                break;
            case { Answer: "true" }:
                json.WriteBooleanValue(true);
                break;
            case { Answer: "false" }:
                json.WriteBooleanValue(false);
                break;
            default:
                json.WriteStringValue(row.Label.Answer);
                break;
        }

        if (row.Options is { } options)
        {
            json.WriteStartObject("options");
            foreach ((string rule, IReadOnlyList<ChoiceOption> list) in options)
            {
                json.WriteStartArray(rule);
                foreach (ChoiceOption option in list)
                {
                    json.WriteStartObject();
                    json.WriteString("key", option.Key);
                    json.WriteString("description", option.Description);
                    json.WritePropertyName("verdict");
                    JsonSerializer.Serialize(json, option.Verdict, SemanticPolicyJson.Options);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        if (row.Metadata.Count > 0)
        {
            json.WriteStartObject("metadata");
            foreach ((string key, JsonElement value) in row.Metadata)
            {
                json.WritePropertyName(key);
                value.WriteTo(json);
            }

            json.WriteEndObject();
        }

        json.WriteEndObject();
    }

    private EvalsFormatException Refuse(int number, string error) =>
        new(string.Create(CultureInfo.InvariantCulture, $"Dataset '{_path}', row {number}: {error}"));

    private EvalsFormatException Refuse(string id, string error) => new($"Dataset '{_path}', id '{id}': {error}");
}
