namespace SemanticPolicy;

/// <summary>The kinds of <see cref="PreModelOutcome"/>.</summary>
public enum PreModelOutcomeKind
{
    /// <summary>Run the model on the input as it is.</summary>
    Proceed,

    /// <summary>Do not run the model; the run responds with the outcome's message.</summary>
    Stop,
}
