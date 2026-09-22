namespace SemanticPolicy.Evals.Replay;

/// <summary>
/// Whether a rule is the same question a recording was made with. A stored result is an answer to what the
/// provider was asked, so a replay is allowed only against a rule that asks it identically: the id, the
/// type, the question and whatever else the provider was shown — the criteria, the flagged answer and the
/// ladder, the options, the levels and the rungs. Everything the policy decides for itself, the thresholds,
/// the gates, the chain, the failure behaviour, the mode and the budget, is outside the question and may
/// differ freely.
/// </summary>
public static class RuleShape
{
    /// <summary>Compares the two rules.</summary>
    /// <param name="recorded">The rule the recording was made with.</param>
    /// <param name="candidate">The rule the user wants to replay.</param>
    /// <param name="differingField">
    /// The name of the first field that differs, as the policy file spells it, or <see langword="null"/> when
    /// the rules match. It is a field name and never a value, so it is safe to print.
    /// </param>
    public static bool Matches(Rule recorded, Rule candidate, out string? differingField)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(candidate);
        differingField = FirstDifference(recorded, candidate);
        return differingField is null;
    }

    private static string? FirstDifference(Rule recorded, Rule candidate)
    {
        if (!string.Equals(recorded.Id, candidate.Id, StringComparison.Ordinal))
        {
            return "id";
        }

        if (recorded.Type != candidate.Type)
        {
            return "type";
        }

        if (!string.Equals(recorded.Question, candidate.Question, StringComparison.Ordinal))
        {
            return "question";
        }

        return (recorded, candidate) switch
        {
            (BooleanRule first, BooleanRule second) => BooleanDifference(first, second),
            // An option's description is part of what the provider was shown, and their order is the order it
            // was shown them in, so both are compared as written.
            (ChoiceRule first, ChoiceRule second) => first.Options.SequenceEqual(second.Options) ? null : "options",
            (ScoreRule first, ScoreRule second) => ScoreDifference(first, second),
            _ => "type",
        };
    }

    private static string? BooleanDifference(BooleanRule recorded, BooleanRule candidate)
    {
        if (recorded.FlaggedAnswer != candidate.FlaggedAnswer)
        {
            return "flaggedAnswer";
        }

        if (!recorded.Ladder.SequenceEqual(candidate.Ladder))
        {
            return "ladder";
        }

        return recorded.Criteria == candidate.Criteria ? null : "criteria";
    }

    private static string? ScoreDifference(ScoreRule recorded, ScoreRule candidate)
    {
        if (!recorded.Levels.SequenceEqual(candidate.Levels, StringComparer.Ordinal))
        {
            return "levels";
        }

        return recorded.Rungs.SequenceEqual(candidate.Rungs) ? null : "rungs";
    }
}
