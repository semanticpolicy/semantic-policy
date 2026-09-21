namespace SemanticPolicy.Evaluation;

/// <summary>
/// What a policy concluded about a context, with everything behind it. The verdict is a semantic
/// signal, not an authorization: acting on it — refusing a tool call, asking a person, doing
/// nothing — is the application's code. <see cref="Effective"/> is what the application acts on and
/// <see cref="Evaluated"/> what the policy concluded; in Shadow mode the first is always Allow while
/// the second is still recorded, so a policy under observation cannot be enforced by accident. Every
/// rule's verdict and every attempt's provider result are kept, and a result's raw response never
/// serializes, so the verdict can go into a log and still replay against another policy.
/// </summary>
/// <param name="PolicyId">The policy.</param>
/// <param name="Mode">Whether the evaluated verdict is the policy's decision or only recorded.</param>
/// <param name="Effective">
/// The verdict the application acts on: <paramref name="Evaluated"/> in Enforce, Allow in Shadow.
/// </param>
/// <param name="Evaluated">The most severe of the rules' verdicts.</param>
/// <param name="Rules">Every rule's verdict, in policy order.</param>
public sealed record PolicyVerdict(
    string PolicyId,
    PolicyMode Mode,
    Verdict Effective,
    Verdict Evaluated,
    IReadOnlyList<RuleVerdict> Rules);
