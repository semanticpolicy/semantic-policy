using SemanticPolicy.Evaluation;

namespace SemanticPolicy;

/// <summary>
/// The application's decision about a tool's result, made after the policy has judged it and before
/// the model sees it. The verdict is a semantic signal, probabilistic by nature, and not an
/// authorization: what to do on a Deny, a Warn or an Abstain is this delegate's, in every mode. The
/// adapter applies the outcome as returned and never reads the verdict itself, so a handler that acts
/// on <see cref="PolicyVerdict.Effective"/> and reports <see cref="PolicyVerdict.Evaluated"/> behaves
/// the same in Shadow and in Enforce.
/// </summary>
/// <param name="result">What the tool returned, with the call it answers.</param>
/// <param name="verdict">What the policy concluded, with every attempt behind it.</param>
/// <param name="cancellationToken">The caller's token, as the run received it.</param>
/// <returns>What the model sees as the result.</returns>
public delegate ValueTask<PostToolOutcome> PostToolHandler(
    ToolResult result,
    PolicyVerdict verdict,
    CancellationToken cancellationToken);
