namespace SemanticPolicy.Evaluation;

/// <summary>
/// Which provider call an attempt is: one rule of the policy on one binding of its chain. The step
/// function asks for attempts by this key and reads the results it is handed by it. The binding is
/// named by its position because the chain's order is what fallback and the margin gate walk; the
/// provider's id is on the binding.
/// </summary>
/// <param name="RuleId">The rule the attempt answers.</param>
/// <param name="BindingIndex">The binding's position in the policy's chain, zero-based.</param>
public readonly record struct AttemptKey(string RuleId, int BindingIndex);
