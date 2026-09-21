namespace SemanticPolicy.Evaluation;

/// <summary>
/// What produced a rule's verdict: a provider's answer read through the rule's vocabulary, or the
/// policy's own declaration when no answer decided. A verdict that came from the failure behaviour
/// or from an exhausted gate says nothing about the context, and a reader that acts on it should know.
/// </summary>
public enum VerdictSource
{
    /// <summary>A Boolean rule: the most severe ladder rung the flagged answer's evidence reached.</summary>
    Threshold,

    /// <summary>A Choice rule: the verdict of the option the provider picked.</summary>
    OptionMap,

    /// <summary>A Score rule: the most severe rung at or below the level the provider picked.</summary>
    LevelMap,

    /// <summary>
    /// The evidence fell under the margin gate on the last binding, so the rule abstained. No provider
    /// decided.
    /// </summary>
    UncertaintyExhausted,

    /// <summary>
    /// A provider failed, abstained or broke the contract and the policy's failure behaviour named the
    /// verdict — or, for a fallback, the chain ran out and its terminal verdict applied. No provider
    /// decided.
    /// </summary>
    FailureBehavior,
}
