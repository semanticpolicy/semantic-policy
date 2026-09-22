using System.Collections.ObjectModel;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Replay;

/// <summary>One dataset row joined to what the providers answered about it for the selected rule.</summary>
/// <param name="Row">The dataset row, with its label.</param>
/// <param name="AttemptsByProvider">
/// The recorded results for the selected rule, by provider registration name; empty when the recording holds
/// the row but nothing for this rule.
/// </param>
public sealed record ReplayRow(DatasetRow Row, IReadOnlyDictionary<string, ProviderResult> AttemptsByProvider);

/// <summary>
/// A recording joined to the rows a verb selected, ready to be replayed at any policy that asks the
/// recorded question. The join is by row id, so a run that was interrupted replays on the rows it reached
/// and the verb reports how many of how many that was.
/// </summary>
public sealed class ReplaySet
{
    private ReplaySet(RecordingHeader header, Policy policy, Rule rule, IReadOnlyList<ReplayRow> rows, int datasetRowCount)
    {
        Header = header;
        Policy = policy;
        Rule = rule;
        Rows = rows;
        DatasetRowCount = datasetRowCount;
    }

    /// <summary>The header of the recording that was read.</summary>
    public RecordingHeader Header { get; }

    /// <summary>The policy the user loaded, which <see cref="Evaluate()"/> replays at.</summary>
    public Policy Policy { get; }

    /// <summary>The rule every row is replayed on.</summary>
    public Rule Rule { get; }

    /// <summary>The rows that were selected and are in the recording, in dataset order.</summary>
    public IReadOnlyList<ReplayRow> Rows { get; }

    /// <summary>How many selected rows the recording held: the N of "N of M rows".</summary>
    public int RecordedRowCount => Rows.Count;

    /// <summary>How many rows were selected: the M of "N of M rows".</summary>
    public int DatasetRowCount { get; }

    /// <summary>Joins a recording to the loaded inputs.</summary>
    /// <param name="recording">The recording, as read.</param>
    /// <param name="inputs">The policy, rule and rows the verb works on.</param>
    /// <param name="force">Replay even though the datasets no longer have the recorded digests.</param>
    /// <exception cref="EvalsException">
    /// The datasets changed and <paramref name="force"/> is <see langword="false"/>, the recording's policy
    /// has no such rule, or its rule asks a different question; the message names the rule and the field.
    /// Two selected rows share an id, which no recorded result can be matched to; the message names the id.
    /// </exception>
    public static ReplaySet Load(Recording recording, LoadedInputs inputs, bool force)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(inputs);
        CheckDatasets(recording, inputs, force);
        CheckRule(recording, inputs.Rule);

        Dictionary<string, RecordedRow> byId = new(recording.Rows.Count, StringComparer.Ordinal);
        foreach (RecordedRow row in recording.Rows)
        {
            if (!byId.TryAdd(row.Id, row))
            {
                throw new EvalsException($"Recording '{recording.Path}' holds row '{row.Id}' more than once.");
            }
        }

        List<ReplayRow> rows = [];
        HashSet<string> selectedIds = new(inputs.Selected.Count, StringComparer.Ordinal);
        foreach (DatasetRow row in inputs.Selected)
        {
            // A row id is unique within its file, not across the pair --tune and --test read, and the join is
            // by id alone. Two selected rows sharing one id would both take the same recorded result and be
            // scored against their own labels, which is a wrong number rather than an error.
            if (!selectedIds.Add(row.Id))
            {
                throw new EvalsException(
                    $"Row '{row.Id}' is among the selected rows twice, so a recorded result cannot be matched "
                    + "to one of them. Give the rows distinct ids across the files being read.");
            }

            if (byId.TryGetValue(row.Id, out RecordedRow? recorded))
            {
                rows.Add(new ReplayRow(row, AttemptsFor(recorded, inputs.Rule.Id)));
            }
        }

        return new ReplaySet(recording.Header, inputs.Policy, inputs.Rule, rows, inputs.Selected.Count);
    }

    private static void CheckDatasets(Recording recording, LoadedInputs inputs, bool force)
    {
        HashSet<string> recorded = new(
            recording.Header.Datasets.Select(dataset => dataset.Sha256),
            StringComparer.OrdinalIgnoreCase);
        if (force || recorded.SetEquals(inputs.Datasets.Select(dataset => dataset.Sha256)))
        {
            return;
        }

        string then = Paths(recording.Header.Datasets.Select(dataset => dataset.Path));
        string now = Paths(inputs.Datasets.Select(dataset => dataset.Path));
        throw new EvalsException(
            $"Recording '{recording.Path}' was made from {then}; {now} no longer has the same digest, so the "
            + "results are about other rows. Pass --force to replay against it anyway.");
    }

    private static void CheckRule(Recording recording, Rule rule)
    {
        Rule? recorded = recording.Header.Policy.Rules
            .FirstOrDefault(candidate => string.Equals(candidate.Id, rule.Id, StringComparison.Ordinal));
        if (recorded is null)
        {
            string ids = string.Join(", ", recording.Header.Policy.Rules.Select(candidate => $"'{candidate.Id}'"));
            throw new EvalsException($"Recording '{recording.Path}' has no rule '{rule.Id}'; its rules are {ids}.");
        }

        if (!RuleShape.Matches(recorded, rule, out string? field))
        {
            throw new EvalsException(
                $"Recording '{recording.Path}': rule '{rule.Id}' differs from the recorded one in '{field}'. "
                + "A result answers the rule it was asked about, so a rule that changed there needs a new run.");
        }
    }

    private static IReadOnlyDictionary<string, ProviderResult> AttemptsFor(RecordedRow row, string ruleId) =>
        row.Attempts.TryGetValue(ruleId, out IReadOnlyDictionary<string, ProviderResult>? byProvider)
        && byProvider is not null
            ? byProvider
            : ReadOnlyDictionary<string, ProviderResult>.Empty;

    private static string Paths(IEnumerable<string> paths) => string.Join(" and ", paths.Select(path => $"'{path}'"));

    /// <summary>Evaluates every row at the policy the inputs were loaded with.</summary>
    public IReadOnlyList<EvaluatedRow> Evaluate() => Evaluate(Policy);

    /// <summary>
    /// Evaluates every row at a variant of the policy. The variant is narrowed to the selected rule — the
    /// rule alone, every binding kept with only that rule's operating points — so a recording of one rule
    /// replays without the others, and the recorded results are keyed to the variant's own chain by matching
    /// provider names. Only the rule's id and type have to match the loaded rule: a variant may reorder or
    /// drop bindings and change thresholds, gates, the failure behaviour, the mode and the budget, which is
    /// what makes a sweep possible without calling a provider again. The question itself was checked at
    /// <see cref="Load"/>.
    /// </summary>
    /// <param name="variant">The policy to evaluate at; it must carry the selected rule.</param>
    /// <exception cref="EvalsException">
    /// A binding's provider has no recorded attempt for the rule on some row; the message names the rule, the
    /// provider and the row. A missing result is never turned into a failure.
    /// </exception>
    public IReadOnlyList<EvaluatedRow> Evaluate(Policy variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        Policy narrowed = Narrow(variant);
        List<EvaluatedRow> evaluated = new(Rows.Count);
        foreach (ReplayRow row in Rows)
        {
            evaluated.Add(new EvaluatedRow(row.Row, Decide(narrowed, row)));
        }

        return evaluated;
    }

    private Policy Narrow(Policy variant)
    {
        Rule rule = variant.Rules.FirstOrDefault(candidate => string.Equals(candidate.Id, Rule.Id, StringComparison.Ordinal))
            ?? throw new ArgumentException($"The policy variant carries no rule '{Rule.Id}'.", nameof(variant));
        return variant with
        {
            Rules = [rule],
            Bindings =
            [
                .. variant.Bindings.Select(binding => binding with
                {
                    OperatingPoints =
                    [
                        .. binding.OperatingPoints.Where(point =>
                            string.Equals(point.RuleId, Rule.Id, StringComparison.Ordinal)),
                    ],
                }),
            ],
        };
    }

    private RuleVerdict Decide(Policy narrowed, ReplayRow row)
    {
        Dictionary<AttemptKey, ProviderResult> attempts = new(narrowed.Bindings.Count);
        for (int index = 0; index < narrowed.Bindings.Count; index++)
        {
            string providerId = narrowed.Bindings[index].ProviderId;
            if (!row.AttemptsByProvider.TryGetValue(providerId, out ProviderResult? result) || result is null)
            {
                throw new EvalsException(
                    $"Rule '{Rule.Id}' has no recorded attempt from provider '{providerId}' on row '{row.Row.Id}'. "
                    + "Record the run again with that provider in the chain: what a provider never answered "
                    + "cannot be counted as a failure.");
            }

            attempts[new AttemptKey(Rule.Id, index)] = result;
        }

        EvaluationStep step = PolicyEvaluation.Evaluate(narrowed, attempts);

        // Every binding of the one rule was just supplied, so the step is complete unless the step function
        // stopped asking along a chain it should have walked; that would be a bug, not a verdict.
        return step.Verdict?.Rules.Single()
            ?? throw new InvalidOperationException(
                $"The step function asked for more attempts on rule '{Rule.Id}' although every binding was supplied.");
    }
}
