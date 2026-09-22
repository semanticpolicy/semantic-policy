using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.TypeSafe;

/// <summary>
/// How a <see cref="TypeSafeJevProvider"/> is configured: a mutable class a host fills in a delegate
/// and the provider copies at construction, so a later change to an instance changes nothing that
/// runs. There is no default route, because choosing one would choose where a user's content is sent.
/// </summary>
public sealed class TypeSafeJevOptions
{
    /// <summary>
    /// Where calls go. Required; <see cref="TypeSafeJevRoute.TypeSafe"/> and
    /// <see cref="TypeSafeJevRoute.OpenRouter"/> are the presets.
    /// </summary>
    public TypeSafeJevRoute? Route { get; set; }

    /// <summary>The model to ask for instead of the route's pinned one; <see langword="null"/> keeps the pin.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// The key sent as a Bearer token. Set explicitly it wins over any environment variable, and the
    /// provider's constructor requires it: only a registration reads the environment.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The environment variable to read the key from instead of the route's. Read by a registration,
    /// never by the provider itself.
    /// </summary>
    public string? ApiKeyVariable { get; set; }

    /// <summary>
    /// How long one call may take, reading the body included, before the adapter reports it as a
    /// <see cref="FailureKind.Timeout"/>. Ten seconds by default: an outage, not a slow answer.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The provider's id, the name a policy binds to. <c>typesafe-jev</c> by default.</summary>
    public string Id { get; set; } = "typesafe-jev";

    /// <summary>
    /// Checks the options before a provider is built from them. A violation is a mistake in the host's
    /// configuration, so it is an exception naming the property rather than a provider outcome.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="Route"/> is absent; <see cref="Timeout"/> is not greater than zero; the model,
    /// <see cref="Model"/> or else the route's, is empty; the route's base URL is not absolute, or is
    /// not <c>https</c> on a host other than loopback; the route's path does not start with <c>/</c>;
    /// or <see cref="Id"/> is empty.
    /// </exception>
    public void EnsureValid()
    {
        if (Route is null)
        {
            throw new ArgumentException("The route is absent.", nameof(Route));
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("The timeout is not greater than zero.", nameof(Timeout));
        }

        if (string.IsNullOrWhiteSpace(Model ?? Route.Model))
        {
            throw new ArgumentException("The model is empty.", nameof(Model));
        }

        if (Route.BaseUrl is null || !Route.BaseUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The route's BaseUrl is not an absolute URI.", nameof(Route));
        }

        // A Bearer key over plain http to a remote host is the simplest key leak there is; loopback is
        // where a local proxy or a hand-run fake server lives, and the only place http is allowed.
        if (Route.BaseUrl.Scheme != Uri.UriSchemeHttps && !Route.BaseUrl.IsLoopback)
        {
            throw new ArgumentException("The route's BaseUrl must use https unless its host is loopback.", nameof(Route));
        }

        if (string.IsNullOrEmpty(Route.Path) || Route.Path[0] != '/')
        {
            throw new ArgumentException("The route's Path does not start with '/'.", nameof(Route));
        }

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("The id is empty.", nameof(Id));
        }
    }
}
