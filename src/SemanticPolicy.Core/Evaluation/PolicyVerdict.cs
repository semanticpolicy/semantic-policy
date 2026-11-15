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
    IReadOnlyList<RuleVerdict> Rules)
{
    /// <summary>
    /// The <see cref="Policy.Digest"/> of the policy that produced the verdict, so a stored verdict names the
    /// exact policy behind it, not only its id: <c>sha256:</c> followed by 64 lowercase hex digits, stable for
    /// an unchanged policy across processes, machines and library versions. It covers the whole policy, its
    /// questions, criteria and option descriptions included, so an application that builds options from what a
    /// user typed stores a hash of that text with the verdict. Every verdict evaluation returns carries it,
    /// live and in replay alike; it is <see langword="null"/> on a verdict read from JSON written without it.
    /// It serializes as <c>policyDigest</c>, and not at all when null.
    /// </summary>
    public string? PolicyDigest { get; init; }
}
