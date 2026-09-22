namespace SemanticPolicy.Evals.Datasets;

/// <summary>What a row's <c>label</c> says about it.</summary>
public enum RowLabelKind
{
    /// <summary>An answer in the rule's vocabulary: <c>true</c>/<c>false</c>, an option key or a level.</summary>
    Answer,

    /// <summary>The annotators could not agree; the row is counted outside the confusion matrix.</summary>
    Ambiguous,

    /// <summary>The right outcome is for the provider not to decide; counted outside the matrix too.</summary>
    Abstain,
}

/// <summary>
/// The truth a row carries. <paramref name="Answer"/> is the label text for <see cref="RowLabelKind.Answer"/>
/// and <see langword="null"/> otherwise; a JSON boolean label arrives as <c>"true"</c> or <c>"false"</c>.
/// </summary>
/// <param name="Kind">Whether the label is an answer or one of the two reserved words.</param>
/// <param name="Answer">The answer as written, before it is checked against a rule.</param>
public sealed record RowLabel(RowLabelKind Kind, string? Answer);
