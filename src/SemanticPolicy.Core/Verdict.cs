namespace SemanticPolicy;

/// <summary>
/// What a policy or a rule concluded about a context. A verdict is a semantic signal, not an
/// authorization: it is an input to a decision the application still owns, and acting on it — refusing
/// a tool call, asking a person, doing nothing — is the application's code. Members are declared in
/// ascending severity, so the most severe verdict is the numeric maximum and a policy's verdict is the
/// maximum over its rules.
/// </summary>
public enum Verdict
{
    /// <summary>
    /// Nothing the policy looks for was found. The unflagged answer and an unreached ladder both mean this.
    /// </summary>
    Allow,

    /// <summary>Something worth recording was found; the application may proceed and should know.</summary>
    Warn,

    /// <summary>
    /// The provider's evidence was too close to call under the binding's margin gate and no later binding
    /// answered. Never authored in a rule: it is the runtime's outcome, and it outranks Warn because
    /// "could not decide" beside "fine" is not "fine".
    /// </summary>
    Abstain,

    /// <summary>The decision belongs to something outside the runtime: a person, a stronger model, a queue.</summary>
    Escalate,

    /// <summary>
    /// The policy's most severe conclusion. Whether anything is stopped is decided by the application.
    /// </summary>
    Deny,
}
