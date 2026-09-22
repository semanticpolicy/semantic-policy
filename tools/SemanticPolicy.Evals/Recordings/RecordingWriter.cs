using System.Text;
using System.Text.Json;

namespace SemanticPolicy.Evals.Recordings;

/// <summary>
/// Writes a recording: the header, then one line per row. Each row is flushed as it is written, so a run
/// killed halfway leaves every row before it readable and the file is a partial recording rather than a
/// broken one. Serialisation goes through the library's own options, which is why
/// <see cref="Protocol.ProviderResult.Raw"/> never reaches the file.
/// </summary>
public sealed class RecordingWriter : IAsyncDisposable
{
    // No byte-order mark, and the same newline on every platform: a recording may be committed, and a file
    // whose bytes depend on the machine that wrote it cannot be compared between two runs.
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly StreamWriter _writer;
    private readonly string _path;

    private RecordingWriter(StreamWriter writer, string path)
    {
        _writer = writer;
        _path = path;
    }

    /// <summary>Creates the file, replacing one that is there, and writes the header line.</summary>
    /// <param name="path">Where the recording goes.</param>
    /// <param name="header">The header line.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="EvalsException">The file cannot be written; the message names the path.</exception>
    public static async Task<RecordingWriter> CreateAsync(
        string path,
        RecordingHeader header,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(header);
        StreamWriter stream;
        try
        {
            stream = new StreamWriter(path, append: false, _utf8) { AutoFlush = false, NewLine = "\n" };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new EvalsException($"Recording '{path}' cannot be written: {e.Message}");
        }

        RecordingWriter writer = new(stream, path);
        try
        {
            await writer.WriteLineAsync(header, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The file is already created and truncated at this point, so a writer nobody can dispose would
            // hold the handle open until the finalizer runs and a retry would not be able to replace the file.
            await writer.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return writer;
    }

    /// <summary>Appends one row and flushes it.</summary>
    /// <param name="row">The row's results, already keyed by rule and provider.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="EvalsException">The row cannot be written; the message names the path and the row.</exception>
    public Task WriteAsync(RecordedRow row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        return WriteLineAsync(row, cancellationToken, row.Id);
    }

    /// <summary>Closes the file; every line was already flushed as it was written.</summary>
    public ValueTask DisposeAsync() => _writer.DisposeAsync();

    private async Task WriteLineAsync<T>(T value, CancellationToken cancellationToken, string? rowId = null)
    {
        // Serialisation first: a value that cannot be written must not leave a half-line in the file.
        string line = JsonSerializer.Serialize(value, SemanticPolicyJson.Options);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            string what = rowId is null ? "the header" : $"row '{rowId}'";
            throw new EvalsException($"Recording '{_path}': {what} cannot be written: {e.Message}");
        }
    }
}
