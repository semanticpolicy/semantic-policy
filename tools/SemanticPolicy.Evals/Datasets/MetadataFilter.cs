using System.Text.Json;

namespace SemanticPolicy.Evals.Datasets;

/// <summary>
/// One <c>--where metadata.&lt;key&gt;=&lt;value&gt;</c> filter: equality on a single metadata key. A string
/// value is compared as text; any other JSON value is compared by the text it was written as, so
/// <c>metadata.turn=1</c> matches <c>"turn": 1</c> and <c>"turn": "1"</c> alike. Filtering here rather than
/// by editing the file keeps the file's digest, which is what a recording is joined to.
/// </summary>
/// <param name="Key">The metadata key, exactly as written.</param>
/// <param name="Value">The text the value has to equal.</param>
public sealed record MetadataFilter(string Key, string Value)
{
    private const string _prefix = "metadata.";

    /// <summary>Parses one option token.</summary>
    /// <param name="token">The text after <c>--where</c>.</param>
    /// <exception cref="EvalsException">The token has no <c>metadata.</c> prefix, no <c>=</c> or an empty key.</exception>
    public static MetadataFilter Parse(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        int equals = token.IndexOf('=');
        if (!token.StartsWith(_prefix, StringComparison.Ordinal) || equals < _prefix.Length)
        {
            throw Malformed(token);
        }

        string key = token[_prefix.Length..equals];
        return key.Length == 0 ? throw Malformed(token) : new MetadataFilter(key, token[(equals + 1)..]);
    }

    /// <summary>The rows that satisfy every filter, in their original order; every row when there is none.</summary>
    /// <param name="filters">The filters, all of which must match.</param>
    /// <param name="rows">The rows to filter.</param>
    public static IReadOnlyList<DatasetRow> Apply(IReadOnlyList<MetadataFilter> filters, IReadOnlyList<DatasetRow> rows)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(rows);
        return filters.Count == 0 ? rows : [.. rows.Where(row => filters.All(filter => filter.Matches(row)))];
    }

    /// <summary>Whether the row carries the key with exactly this value. A row without the key never matches.</summary>
    /// <param name="row">The row to test.</param>
    public bool Matches(DatasetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.Metadata.TryGetValue(Key, out JsonElement value))
        {
            return false;
        }

        string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        return string.Equals(text, Value, StringComparison.Ordinal);
    }

    private static EvalsException Malformed(string token) =>
        new($"--where expects metadata.<key>=<value>, not '{token}'.");
}
