namespace SemanticPolicy.Evals.Replay;

/// <summary>
/// The Choice options a row was shown: its own for the rule when it carries them, the rule's otherwise. Two sets are
/// the same set when they hold equal options — key, description and verdict — in the same order, which is how
/// <see cref="Comparer"/> compares them; a list compares by reference, so it cannot key a cache on its own.
/// </summary>
internal static class OptionSets
{
    /// <summary>Compares option sets by value and order.</summary>
    public static IEqualityComparer<IReadOnlyList<ChoiceOption>> Comparer { get; } = new ByValue();

    /// <summary>The options <paramref name="row"/> was shown for <paramref name="rule"/>.</summary>
    public static IReadOnlyList<ChoiceOption> Of(DatasetRow row, ChoiceRule rule) =>
        row.Options?.GetValueOrDefault(rule.Id) ?? rule.Options;

    private sealed class ByValue : IEqualityComparer<IReadOnlyList<ChoiceOption>>
    {
        public bool Equals(IReadOnlyList<ChoiceOption>? x, IReadOnlyList<ChoiceOption>? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y));

        public int GetHashCode(IReadOnlyList<ChoiceOption> obj)
        {
            HashCode hash = new();
            foreach (ChoiceOption option in obj)
            {
                hash.Add(option);
            }

            return hash.ToHashCode();
        }
    }
}
