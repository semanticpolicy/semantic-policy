using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evaluation;

/// <summary>
/// One provider call in a rule's trace: the provider's result as it was returned, what evaluation
/// made of it, and what happened next. The result is never rewritten — a success that broke the
/// contract keeps its result and gets a synthesized <see cref="EffectiveOutcome"/> beside it — so a
/// stored trace replays against another policy exactly as the provider answered.
/// </summary>
/// <param name="BindingIndex">The binding's position in the policy's chain, zero-based.</param>
/// <param name="ProviderId">The binding's provider.</param>
/// <param name="Result">The provider's result, untouched.</param>
/// <param name="EffectiveOutcome">
/// The outcome evaluation acted on: the result's own, or <c>Failure(Malformed)</c> when a success did
/// not honour the contract.
/// </param>
/// <param name="Margin">
/// The gap between the top answer and the runner-up on the gate's evidence kind, when a gate applied
/// to this attempt; otherwise <see langword="null"/>.
/// </param>
/// <param name="Disposition">Whether the attempt decided the rule, and if not, what moved the chain.</param>
public sealed record Attempt(
    int BindingIndex,
    string ProviderId,
    ProviderResult Result,
    ProviderOutcome EffectiveOutcome,
    double? Margin,
    AttemptDisposition Disposition);
