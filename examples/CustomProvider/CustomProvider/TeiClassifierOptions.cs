namespace CustomProvider;

/// <summary>Where a TEI server is and what it serves.</summary>
/// <param name="BaseUrl">The server's root; the provider posts to <c>{BaseUrl}/predict</c>.</param>
public sealed record TeiClassifierOptions(Uri BaseUrl)
{
    /// <summary>The classifier this example runs against.</summary>
    public const string DefaultModel = "protectai/deberta-v3-base-prompt-injection-v2";

    /// <summary>
    /// Reported as the model on every result, failures included. TEI's answer names no model, so only
    /// the registration knows what the server was started with.
    /// </summary>
    public string Model { get; init; } = DefaultModel;

    /// <summary>The label whose score is the <c>true</c> side of the Boolean answer.</summary>
    public string PositiveLabel { get; init; } = "INJECTION";

    /// <summary>
    /// How long one call may take before it ends as a <c>Timeout</c> failure. The provider keeps this
    /// timer itself, because a <c>Timeout</c> is the provider's own reading, while the caller's token
    /// stays the caller's to cancel. Windows takes about two seconds to refuse a connection to a
    /// loopback port nothing listens on, so a shorter timer would report a missing server as a
    /// <c>Timeout</c> there rather than as <c>Unavailable</c>.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);
}
