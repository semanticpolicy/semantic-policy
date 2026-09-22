namespace SemanticPolicy.Evals.Datasets;

/// <summary>
/// A dataset file as read: its rows in file order and the digest a recording is later joined to it by.
/// </summary>
/// <param name="Path">The path the file was read from, as given.</param>
/// <param name="Sha256">The lowercase hex SHA-256 of the file's bytes.</param>
/// <param name="Rows">Every row, in file order.</param>
public sealed record Dataset(string Path, string Sha256, IReadOnlyList<DatasetRow> Rows)
{
    /// <summary>
    /// The rows, once every <see cref="RowLabelKind.Answer"/> label has been checked against the rule's
    /// vocabulary: <c>true</c>/<c>false</c> for a Boolean rule, an option key for a Choice rule, a level for
    /// a Score rule. Ambiguous and abstain labels belong to every rule.
    /// </summary>
    /// <param name="rule">The rule the dataset is about to be evaluated on.</param>
    /// <exception cref="EvalsException">A label is outside the vocabulary; the first one is named.</exception>
    public IReadOnlyList<DatasetRow> ForRule(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Func<string, bool> accepts;
        string vocabulary;
        switch (rule)
        {
            case BooleanRule:
                accepts = answer => answer is "true" or "false";
                vocabulary = "true or false, the answers of Boolean rule";
                break;
            case ChoiceRule choice:
                accepts = answer => choice.Options.Any(option => option.Key == answer);
                vocabulary = "an option key of rule";
                break;
            case ScoreRule score:
                accepts = answer => score.Levels.Contains(answer);
                vocabulary = "a level of rule";
                break;
            default:
                throw new ArgumentException("The rule is not a Boolean, Choice or Score rule.", nameof(rule));
        }

        foreach (DatasetRow row in Rows)
        {
            if (row.Label is { Kind: RowLabelKind.Answer, Answer: { } answer } && !accepts(answer))
            {
                throw new EvalsException(
                    $"Dataset '{Path}', line {row.Line}, id '{row.Id}': label '{answer}' is not {vocabulary} '{rule.Id}'.");
            }
        }

        return Rows;
    }
}
