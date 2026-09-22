using SemanticPolicy.Evals.Replay;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Counting;

/// <summary>
/// Which table a row belongs to. A provider failure and an abstention are never folded into a
/// classification error: an outage would otherwise read as poor recall, and a model that declined to
/// answer as a wrong answer.
/// </summary>
public enum RowBucket
{
    /// <summary>A verdict the rule's own mapping produced; the row enters the confusion matrix.</summary>
    Classified,

    /// <summary>The policy's failure behaviour decided; the row counts as a provider failure.</summary>
    Failed,

    /// <summary>The chain ran out of bindings under the margin gate; the row counts as an abstention.</summary>
    Abstained,

    /// <summary>The row is labelled <c>ambiguous</c> or <c>abstain</c> and is counted outside the matrix.</summary>
    Ambiguous,
}

/// <summary>
/// Where one evaluated row is counted, and what it predicted. <paramref name="PredictedAnswer"/> is in the
/// rule's answer vocabulary, the same vocabulary a label is written in, so a metric compares the two
/// directly and never re-reads evidence to decide what the provider meant.
/// </summary>
/// <param name="Row">The row and its verdict.</param>
/// <param name="Bucket">Which table the row belongs to.</param>
/// <param name="FailureKind">
/// Why the attempt that ended the chain failed, for a <see cref="RowBucket.Failed"/> row;
/// <see langword="null"/> for every other bucket and for a provider that abstained rather than failed.
/// </param>
/// <param name="PredictedAnswer">
/// The answer the deciding attempt gave — <c>true</c>/<c>false</c>, an option key or a level — for a
/// <see cref="RowBucket.Classified"/> row, and <see langword="null"/> otherwise.
/// </param>
public sealed record RowOutcome(EvaluatedRow Row, RowBucket Bucket, FailureKind? FailureKind, string? PredictedAnswer);
