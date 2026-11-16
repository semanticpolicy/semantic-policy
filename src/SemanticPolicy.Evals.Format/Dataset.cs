namespace SemanticPolicy.Evals.Format;

/// <summary>
/// A dataset file as read: its rows in file order and the digest a recording is later joined to it by.
/// </summary>
/// <param name="Path">The path the file was read from, as given.</param>
/// <param name="Sha256">The lowercase hex SHA-256 of the file's bytes.</param>
/// <param name="Rows">Every row, in file order.</param>
public sealed record Dataset(string Path, string Sha256, IReadOnlyList<DatasetRow> Rows)
{
    private static readonly Dictionary<string, IReadOnlyList<ChoiceOption>> _noOptions = [];

    /// <summary>
    /// The rows, once each row's <see cref="DatasetRow.Options"/> have been checked against the policy and every
    /// <see cref="RowLabelKind.Answer"/> label against the selected rule's vocabulary: <c>true</c>/<c>false</c> for a
    /// Boolean rule, an option key for a Choice rule — the row's own keys when it carries options for the rule, the
    /// policy's otherwise — and a level for a Score rule. Ambiguous and abstain labels belong to every rule.
    /// </summary>
    /// <remarks>
    /// A row's options must name a Choice rule of <paramref name="policy"/>, and the policy with that rule's options
    /// replaced by the row's must pass <see cref="Policy.Validate"/>: a row's options are held to exactly what a
    /// policy file's are, by the same code.
    /// </remarks>
    /// <param name="policy">The validated policy the dataset is about to be evaluated under.</param>
    /// <param name="rule">The rule of <paramref name="policy"/> the labels answer.</param>
    /// <exception cref="EvalsFormatException">
    /// A row's options name a rule the policy lacks or one that is not a Choice rule, Core rejects them, or a label is
    /// outside the vocabulary. The first such row is named by its line, its id and the rule's id, never by an
    /// option's description.
    /// </exception>
    public IReadOnlyList<DatasetRow> ForPolicy(Policy policy, Rule rule)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rule);
        if (rule is not (BooleanRule or ChoiceRule or ScoreRule))
        {
            throw new ArgumentException("The rule is not a Boolean, Choice or Score rule.", nameof(rule));
        }

        // Most rows share a few sets, so each distinct set of a rule is validated once rather than once per row.
        HashSet<(string Rule, IReadOnlyList<ChoiceOption> Options)> valid = new(ValidatedSet.Comparer);
        foreach (DatasetRow row in Rows)
        {
            foreach ((string ruleId, IReadOnlyList<ChoiceOption>? options) in row.Options ?? _noOptions)
            {
                if (options is null || !valid.Contains((ruleId, options)))
                {
                    Validate(policy, row, ruleId, options);
                    valid.Add((ruleId, options!));
                }
            }

            if (row.Label is { Kind: RowLabelKind.Answer, Answer: { } answer })
            {
                CheckLabel(row, rule, answer);
            }
        }

        return Rows;
    }

    private void Validate(Policy policy, DatasetRow row, string ruleId, IReadOnlyList<ChoiceOption>? options)
    {
        Rule? named = policy.Rules.FirstOrDefault(candidate => string.Equals(candidate.Id, ruleId, StringComparison.Ordinal));
        if (named is null)
        {
            throw Fail(row, ruleId, $"the row carries options for it, and policy '{policy.Id}' has no rule by that id.");
        }

        if (named is not ChoiceRule choice)
        {
            throw Fail(row, ruleId, "the row carries options for it, and it is not a Choice rule.");
        }

        if (options is null)
        {
            throw Fail(row, ruleId, "the row names the rule in 'options' with no options list.");
        }

        Policy replaced = policy with
        {
            Rules = [.. policy.Rules.Select(candidate => ReferenceEquals(candidate, named) ? choice with { Options = options } : candidate)],
        };
        try
        {
            replaced.Validate();
        }
        catch (PolicyConfigurationException e)
        {
            // Core's message names ids and the error, never a question or an option's text.
            throw Fail(row, ruleId, $"the row's options are not a valid Choice rule: {e.Message}");
        }
    }

    private void CheckLabel(DatasetRow row, Rule rule, string answer)
    {
        (bool accepted, string vocabulary) = rule switch
        {
            BooleanRule => (answer is "true" or "false", "true or false, the answers of Boolean rule"),
            ChoiceRule choice when row.Options is not null && row.Options.TryGetValue(choice.Id, out IReadOnlyList<ChoiceOption>? own) =>
                (own.Any(option => option.Key == answer), "an option key the row carries for rule"),
            ChoiceRule choice => (choice.Options.Any(option => option.Key == answer), "an option key of rule"),
            ScoreRule score => (score.Levels.Contains(answer), "a level of rule"),
            _ => (false, string.Empty),
        };
        if (!accepted)
        {
            throw new EvalsFormatException(
                $"Dataset '{Path}', line {row.Line}, id '{row.Id}': label '{answer}' is not {vocabulary} '{rule.Id}'.");
        }
    }

    private EvalsFormatException Fail(DatasetRow row, string ruleId, string error) =>
        new($"Dataset '{Path}', line {row.Line}, id '{row.Id}', rule '{ruleId}': {error}");

    // A rule id and an option set, the set compared by value and order: a list compares by reference, and two rows
    // read from a file never share one.
    private sealed class ValidatedSet : IEqualityComparer<(string Rule, IReadOnlyList<ChoiceOption> Options)>
    {
        public static ValidatedSet Comparer { get; } = new();

        public bool Equals((string Rule, IReadOnlyList<ChoiceOption> Options) x, (string Rule, IReadOnlyList<ChoiceOption> Options) y) =>
            string.Equals(x.Rule, y.Rule, StringComparison.Ordinal) && x.Options.SequenceEqual(y.Options);

        public int GetHashCode((string Rule, IReadOnlyList<ChoiceOption> Options) obj)
        {
            HashCode hash = new();
            hash.Add(obj.Rule, StringComparer.Ordinal);
            foreach (ChoiceOption option in obj.Options)
            {
                hash.Add(option);
            }

            return hash.ToHashCode();
        }
    }
}
