using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Recordings;

/// <summary>
/// One row of a run: the dataset row's id and every result it produced, keyed by rule id and then by the
/// provider's registration name. Provider names rather than binding indices are what makes a recording
/// replayable under a reordered chain, and the id is the only thing here that comes from the dataset —
/// there is no input, no label and no metadata in a recording.
/// </summary>
/// <param name="Id">The dataset row's id.</param>
/// <param name="Attempts">Every result of the row: rule id, then provider name.</param>
/// <param name="Retries">
/// How many times an attempt was called again after an unavailable answer, by rule id and provider name, for
/// the attempts that were; <see langword="null"/> when none of the row's was. The attempt itself is the last
/// call's result.
/// </param>
public sealed record RecordedRow(
    string Id,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, ProviderResult>> Attempts,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? Retries = null)
{
    /// <summary>The dataset row's id.</summary>
    public string Id { get; init; } = Id ?? throw new ArgumentNullException(nameof(Id));

    /// <summary>The row's results, by rule id and provider name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ProviderResult>> Attempts { get; init; } =
        Attempts ?? throw new ArgumentNullException(nameof(Attempts));

    /// <summary>
    /// The retry counts of the row's retried attempts. Beside the attempts rather than inside them, so an
    /// attempt stays exactly what the library serializes.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? Retries { get; init; } = Retries;
}
