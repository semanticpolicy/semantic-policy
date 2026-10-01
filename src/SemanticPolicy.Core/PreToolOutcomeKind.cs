namespace SemanticPolicy;

/// <summary>The kinds of <see cref="PreToolOutcome"/>.</summary>
public enum PreToolOutcomeKind
{
    /// <summary>Run the tool with the arguments as they are.</summary>
    Proceed,

    /// <summary>Do not run the tool; the outcome's message is its result, and the loop continues.</summary>
    Refuse,

    /// <summary>Do not run the tool; the outcome's message is its result, and the loop ends.</summary>
    Stop,
}
