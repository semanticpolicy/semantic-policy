namespace SemanticPolicy;

/// <summary>
/// What the application decided to do with a tool call after reading the policy's verdict about it.
/// The verdict a handler reads is probabilistic — a provider's estimate about the call, thresholded
/// by the policy — and not an authorization, so the decision is the handler's: the adapter applies
/// what it returns and decides nothing itself. A refused call is one the application declined on that
/// estimate, not one shown to be harmful. Made with <see cref="Proceed"/>, <see cref="Refuse"/> or
/// <see cref="Stop"/> and never changed afterwards.
/// </summary>
public sealed record PreToolOutcome
{
    private PreToolOutcome(PreToolOutcomeKind kind, string? message)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>Run the tool with the arguments as they are.</summary>
    public static PreToolOutcome Proceed { get; } = new(PreToolOutcomeKind.Proceed, message: null);

    /// <summary>What was decided.</summary>
    public PreToolOutcomeKind Kind { get; }

    /// <summary>
    /// The message the model sees as the tool's result on <see cref="Refuse"/> and <see cref="Stop"/>;
    /// <see langword="null"/> on <see cref="Proceed"/>.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Do not run the tool: the message becomes the tool's result and the model carries on, free to
    /// try something else.
    /// </summary>
    /// <param name="message">What the model sees as the tool's result; non-blank.</param>
    /// <exception cref="ArgumentException">The message is null or white space.</exception>
    public static PreToolOutcome Refuse(string message) =>
        new(PreToolOutcomeKind.Refuse, RequireMessage(message));

    /// <summary>
    /// Do not run the tool and end the run: the message becomes the tool's result and the loop stops
    /// after it.
    /// </summary>
    /// <param name="message">What the model sees as the tool's result; non-blank.</param>
    /// <exception cref="ArgumentException">The message is null or white space.</exception>
    public static PreToolOutcome Stop(string message) =>
        new(PreToolOutcomeKind.Stop, RequireMessage(message));

    private static string RequireMessage(string message) =>
        string.IsNullOrWhiteSpace(message)
            ? throw new ArgumentException("A refusal or a stop needs a message.", nameof(message))
            : message;
}
