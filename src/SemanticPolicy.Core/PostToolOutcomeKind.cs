namespace SemanticPolicy;

/// <summary>The kinds of <see cref="PostToolOutcome"/>.</summary>
public enum PostToolOutcomeKind
{
    /// <summary>Hand the tool's result to the model as it is.</summary>
    Proceed,

    /// <summary>Hand the outcome's result to the model instead of the tool's.</summary>
    Replace,

    /// <summary>Hand the outcome's message to the model as the result, and end the loop.</summary>
    Stop,
}
