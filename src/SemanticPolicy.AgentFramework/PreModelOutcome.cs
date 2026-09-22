namespace SemanticPolicy;

/// <summary>
/// What the application decided to do with a run after reading the policy's verdict about its input.
/// The verdict a handler reads is probabilistic — a provider's estimate about the text, thresholded by
/// the policy — and not an authorization, so the decision is the handler's: the adapter applies what
/// it returns and decides nothing itself. Made with <see cref="Proceed"/> or <see cref="Stop"/> and
/// never changed afterwards.
/// </summary>
public sealed record PreModelOutcome
{
    private PreModelOutcome(PreModelOutcomeKind kind, string? message)
    {
        Kind = kind;
        Message = message;
    }

    /// <summary>Run the model on the input as it is.</summary>
    public static PreModelOutcome Proceed { get; } = new(PreModelOutcomeKind.Proceed, message: null);

    /// <summary>What was decided.</summary>
    public PreModelOutcomeKind Kind { get; }

    /// <summary>The message a stopped run responds with; <see langword="null"/> on <see cref="Proceed"/>.</summary>
    public string? Message { get; }

    /// <summary>
    /// Do not run the model: the run responds with the message instead, and the inner agent never sees
    /// the input.
    /// </summary>
    /// <param name="message">What the run responds with; non-blank.</param>
    /// <exception cref="ArgumentException">The message is null or white space.</exception>
    public static PreModelOutcome Stop(string message) =>
        new(PreModelOutcomeKind.Stop, RequireMessage(message));

    private static string RequireMessage(string message) =>
        string.IsNullOrWhiteSpace(message)
            ? throw new ArgumentException("A stop needs a message.", nameof(message))
            : message;
}
