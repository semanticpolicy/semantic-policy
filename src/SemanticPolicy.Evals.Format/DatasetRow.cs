using System.Collections.ObjectModel;
using System.Text.Json;

namespace SemanticPolicy.Evals.Format;

/// <summary>
/// One line of a dataset. <paramref name="Input"/> is the context a provider is asked about, and it is the
/// one thing here that never appears in a message or a log; <see cref="Line"/> and <paramref name="Id"/>
/// are what an error names instead.
/// </summary>
/// <param name="Id">The row's id, unique within its file.</param>
/// <param name="Input">The context: a single <c>text</c> part for a string, or one part per property.</param>
/// <param name="Label">The truth the row carries.</param>
public sealed record DatasetRow(string Id, SemanticContext Input, RowLabel Label)
{
    private readonly IReadOnlyDictionary<string, JsonElement> _metadata = ReadOnlyDictionary<string, JsonElement>.Empty;

    /// <summary>The row's id, unique within its file.</summary>
    public string Id { get; init; } = Id ?? throw new ArgumentNullException(nameof(Id));

    /// <summary>The context a provider is asked about.</summary>
    public SemanticContext Input { get; init; } = Input ?? throw new ArgumentNullException(nameof(Input));

    /// <summary>The truth the row carries.</summary>
    public RowLabel Label { get; init; } = Label ?? throw new ArgumentNullException(nameof(Label));

    /// <summary>
    /// Provenance, passed through untouched and reachable by a filter; empty unless set. A row read from a file
    /// holds elements detached from the document they were parsed from.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Metadata
    {
        get => _metadata;
        init => _metadata = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The Choice options the row was shown, by rule id, or <see langword="null"/> when the row carries none. For a
    /// rule named here they replace the rule's own options, for this row only: what a provider is asked, what its
    /// answer maps to, and which keys a label may be. A rule the row does not name keeps the policy's options.
    /// <see cref="Dataset.ForPolicy"/> checks them as Core checks a policy's.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ChoiceOption>>? Options { get; init; }

    /// <summary>
    /// The 1-based line the row was read from, or 0 for a row that was not read from a file. A writer ignores it:
    /// the line a row lands on is decided by the rows written before it.
    /// </summary>
    public int Line { get; init; }
}
