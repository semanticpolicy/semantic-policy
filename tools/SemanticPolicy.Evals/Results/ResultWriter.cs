using System.Text;
using System.Text.Json;

namespace SemanticPolicy.Evals.Results;

/// <summary>
/// Writes a result file. It goes through the library's own serializer settings, so enums are the names the
/// protocol uses everywhere else and a section nothing filled is absent rather than null.
/// </summary>
public static class ResultWriter
{
    // No byte-order mark and the same newline on every platform: a result file is compared against the
    // previous run's, and a diff that turns on which machine wrote it is not a diff of the numbers.
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions _indented =
        new(SemanticPolicyJson.Options) { WriteIndented = true, NewLine = "\n" };

    /// <summary>Writes the result, replacing a file that is already there.</summary>
    /// <param name="path">Where the file goes.</param>
    /// <param name="result">The result to write.</param>
    /// <exception cref="EvalsException">The file cannot be written; the message names the path.</exception>
    public static void Write(string path, EvalsResult result)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(result);

        // Serialised first: a result that cannot be written must not leave a truncated file behind.
        string json = JsonSerializer.Serialize(result, _indented);
        try
        {
            File.WriteAllText(path, json + "\n", _utf8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new EvalsException($"Result '{path}' cannot be written: {e.Message}");
        }
    }
}
