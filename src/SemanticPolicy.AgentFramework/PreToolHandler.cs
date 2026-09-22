using SemanticPolicy.Evaluation;

namespace SemanticPolicy;

/// <summary>
/// The application's decision about a tool call, made after the policy has judged it and before the
/// tool runs. The verdict is a semantic signal, probabilistic by nature, and not an authorization:
/// what to do on a Deny, a Warn or an Abstain is this delegate's, in every mode. The adapter applies
/// the outcome as returned and never reads the verdict itself, so a handler that acts on
/// <see cref="PolicyVerdict.Effective"/> and reports <see cref="PolicyVerdict.Evaluated"/> behaves the
/// same in Shadow and in Enforce.
/// </summary>
/// <param name="call">The call the model proposed.</param>
/// <param name="verdict">What the policy concluded, with every attempt behind it.</param>
/// <param name="cancellationToken">The caller's token, as the run received it.</param>
/// <returns>What happens to the call.</returns>
public delegate ValueTask<PreToolOutcome> PreToolHandler(
    ToolCall call,
    PolicyVerdict verdict,
    CancellationToken cancellationToken);
