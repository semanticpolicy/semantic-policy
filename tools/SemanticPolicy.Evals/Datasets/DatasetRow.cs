using System.Text.Json;

namespace SemanticPolicy.Evals.Datasets;

/// <summary>
/// One line of a dataset. <paramref name="Input"/> is the context a provider is asked about, and it is the
/// one thing here that never appears in a message or a log; <paramref name="Line"/> and <paramref name="Id"/>
/// are what an error names instead.
/// </summary>
/// <param name="Id">The row's id, unique within its file.</param>
/// <param name="Line">The 1-based line the row was read from.</param>
/// <param name="Input">The context: a single <c>text</c> part for a string, or one part per property.</param>
/// <param name="Label">The truth the row carries.</param>
/// <param name="Metadata">
/// Provenance, passed through untouched and reachable by a filter; every element is detached from the
/// document it was read from.
/// </param>
public sealed record DatasetRow(
    string Id,
    int Line,
    SemanticContext Input,
    RowLabel Label,
    IReadOnlyDictionary<string, JsonElement> Metadata);
