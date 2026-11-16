using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Format;

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

    /// <summary>
    /// The <see cref="Policy.Digest"/> of the policy whose evaluation produced the row's results, as a stored
    /// verdict names it; <see langword="null"/> on a row a run of the tool wrote, whose policy is the header's.
    /// Written only when set.
    /// </summary>
    public string? PolicyDigest { get; init; }

    /// <summary>
    /// The row of a verdict an application stored: each rule's attempts keyed by
    /// <see cref="Attempt.ProviderId"/>, the binding's provider, to the provider's untouched
    /// <see cref="Attempt.Result"/>, and the verdict's <see cref="PolicyVerdict.PolicyDigest"/>. Only the attempts
    /// the evaluator made are kept — a cascade that stopped at its first binding leaves the later providers out —
    /// and a rule that holds none is left out. A result's raw response never serializes, so the row written holds
    /// no provider output beyond what the library keeps.
    /// </summary>
    /// <param name="id">The dataset row's id, which the application chooses and writes to the dataset too.</param>
    /// <param name="verdict">The verdict as the application's evaluator returned it.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is empty or white space, or a rule's attempts name one provider twice; the message names
    /// the rule and the provider.
    /// </exception>
    public static RecordedRow FromVerdict(string id, PolicyVerdict verdict)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(verdict);
        Dictionary<string, IReadOnlyDictionary<string, ProviderResult>> byRule = new(StringComparer.Ordinal);
        foreach (RuleVerdict rule in verdict.Rules)
        {
            if (rule.Attempts.Count == 0)
            {
                continue;
            }

            Dictionary<string, ProviderResult> byProvider = new(rule.Attempts.Count, StringComparer.Ordinal);
            foreach (Attempt attempt in rule.Attempts)
            {
                // A row keys one answer per provider, so a chain that binds one provider twice has no row shape.
                if (!byProvider.TryAdd(attempt.ProviderId, attempt.Result))
                {
                    throw new ArgumentException(
                        $"Rule '{rule.RuleId}' holds two attempts from provider '{attempt.ProviderId}', which a recorded row "
                        + "cannot tell apart.",
                        nameof(verdict));
                }
            }

            byRule[rule.RuleId] = byProvider;
        }

        return new RecordedRow(id, byRule) { PolicyDigest = verdict.PolicyDigest };
    }
}
