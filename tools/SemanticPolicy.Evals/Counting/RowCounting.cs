using System.Diagnostics;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Counting;

/// <summary>
/// Sorts an evaluated row into the table it is counted in. Everything is read off the verdict the step
/// function produced — its source, its trace and the answer of the attempt that decided — so the tool never
/// forms a second opinion about what a provider's evidence meant.
/// </summary>
public static class RowCounting
{
    /// <summary>Buckets one row.</summary>
    /// <param name="row">The row and the verdict its rule reached.</param>
    /// <param name="rule">The rule the verdict is about.</param>
    /// <exception cref="ArgumentException">The verdict is about another rule.</exception>
    public static RowOutcome Classify(EvaluatedRow row, Rule rule)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rule);
        RuleVerdict verdict = row.Verdict;
        if (!string.Equals(verdict.RuleId, rule.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The row's verdict is about rule '{verdict.RuleId}', not '{rule.Id}'.",
                nameof(rule));
        }

        // The label is read before the verdict: a row the annotators could not agree on, or one whose right
        // outcome is for nobody to decide, says nothing about a classifier however the verdict was reached.
        if (row.Row.Label.Kind != RowLabelKind.Answer)
        {
            return new RowOutcome(row, RowBucket.Ambiguous, FailureKind: null, PredictedAnswer: null);
        }

        return verdict.Source switch
        {
            VerdictSource.Threshold or VerdictSource.OptionMap or VerdictSource.LevelMap =>
                new RowOutcome(row, RowBucket.Classified, null, AnswerOf(verdict)),
            VerdictSource.FailureBehavior =>
                new RowOutcome(row, RowBucket.Failed, TerminatingKind(verdict), null),
            VerdictSource.UncertaintyExhausted =>
                new RowOutcome(row, RowBucket.Abstained, null, null),
            _ => throw new UnreachableException(),
        };
    }

    private static string? AnswerOf(RuleVerdict verdict) =>
        Deciding(verdict)?.Result.Value switch
        {
            BooleanValue boolean => boolean.Value ? "true" : "false",
            ChoiceValue choice => choice.Option,
            ScoreValue score => score.Level,
            _ => null,
        };

    private static Attempt? Deciding(RuleVerdict verdict) =>
        verdict.DecidingBinding is { } index
            ? verdict.Attempts.FirstOrDefault(attempt => attempt.BindingIndex == index)
            : null;

    // A provider that abstained rather than failed goes through the same failure behaviour and carries no
    // failure kind, so the row is a failure with nothing to name.
    private static FailureKind? TerminatingKind(RuleVerdict verdict) =>
        verdict.Attempts
            .LastOrDefault(attempt => attempt.Disposition == AttemptDisposition.TerminatedByFailure)
            ?.EffectiveOutcome.Kind;
}
