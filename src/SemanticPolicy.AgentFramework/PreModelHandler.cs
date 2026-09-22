using SemanticPolicy.Evaluation;

namespace SemanticPolicy;

/// <summary>
/// The application's decision about a run, made after the policy has judged its input. The verdict is
/// a semantic signal, probabilistic by nature, and not an authorization: what to do on a Deny, a Warn
/// or an Abstain is this delegate's, in every mode. The adapter applies the outcome as returned and
/// never reads the verdict itself, so a handler that acts on <see cref="PolicyVerdict.Effective"/> and
/// reports <see cref="PolicyVerdict.Evaluated"/> behaves the same in Shadow and in Enforce.
/// </summary>
/// <param name="input">What the run is about to send to the model.</param>
/// <param name="verdict">What the policy concluded, with every attempt behind it.</param>
/// <param name="cancellationToken">The caller's token, as the run received it.</param>
/// <returns>What the run does next.</returns>
public delegate ValueTask<PreModelOutcome> PreModelHandler(
    ModelInput input,
    PolicyVerdict verdict,
    CancellationToken cancellationToken);
