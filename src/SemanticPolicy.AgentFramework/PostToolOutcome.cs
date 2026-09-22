namespace SemanticPolicy;

/// <summary>
/// What the application decided to do with a tool's result after reading the policy's verdict about
/// it. The verdict a handler reads is probabilistic — a provider's estimate about the result,
/// thresholded by the policy — and not an authorization, so the decision is the handler's: the
/// adapter applies what it returns and decides nothing itself. Made with <see cref="Proceed"/>,
/// <see cref="Replace"/> or <see cref="Stop"/> and never changed afterwards.
/// </summary>
public sealed record PostToolOutcome
{
    private PostToolOutcome(PostToolOutcomeKind kind, string? message, object? result)
    {
        Kind = kind;
        Message = message;
        Result = result;
    }

    /// <summary>Hand the tool's result to the model as it is.</summary>
    public static PostToolOutcome Proceed { get; } = new(PostToolOutcomeKind.Proceed, message: null, result: null);

    /// <summary>What was decided.</summary>
    public PostToolOutcomeKind Kind { get; }

    /// <summary>The message the model sees as the result on <see cref="Stop"/>; <see langword="null"/> otherwise.</summary>
    public string? Message { get; }

    /// <summary>The value the model sees as the result on <see cref="Replace"/>; <see langword="null"/> otherwise.</summary>
    public object? Result { get; }

    /// <summary>Hand the model this value as the tool's result instead of what the tool returned.</summary>
    /// <param name="result">What the model sees as the tool's result; may be <see langword="null"/>.</param>
    public static PostToolOutcome Replace(object? result) =>
        new(PostToolOutcomeKind.Replace, message: null, result);

    /// <summary>
    /// Hand the model the message as the tool's result and end the run: the loop stops after it.
    /// </summary>
    /// <param name="message">What the model sees as the tool's result; non-blank.</param>
    /// <exception cref="ArgumentException">The message is null or white space.</exception>
    public static PostToolOutcome Stop(string message) =>
        new(PostToolOutcomeKind.Stop, RequireMessage(message), result: null);

    private static string RequireMessage(string message) =>
        string.IsNullOrWhiteSpace(message)
            ? throw new ArgumentException("A stop needs a message.", nameof(message))
            : message;
}
