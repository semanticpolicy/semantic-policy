using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Providers;
using SemanticPolicy.Providers.Http;

namespace SemanticPolicy;

/// <summary>
/// Registers a protocol v0 server on <see cref="ISemanticPolicyBuilder"/>. What it registers answers a
/// rule's question with the server's value and the evidence the options declare; whether that answer
/// allows or denies anything is the policy's decision, and neither is proof of safety or of an attack.
/// </summary>
public static class HttpProviderBuilderExtensions
{
    /// <summary>
    /// Registers an <see cref="HttpProvider"/> under <paramref name="name"/>, the name a policy's
    /// bindings refer to. The options are configured, copied and validated here, at the call, so a
    /// mistake in them fails before any container exists and a later change to the configured instance
    /// changes nothing. There are no defaults for where content goes or what the server answers:
    /// <paramref name="configure"/> must set <see cref="HttpProviderOptions.BaseUrl"/>,
    /// <see cref="HttpProviderOptions.Model"/>, <see cref="HttpProviderOptions.Types"/>,
    /// <see cref="HttpProviderOptions.Evidence"/> and <see cref="HttpProviderOptions.StructuredContext"/>.
    /// </summary>
    /// <param name="builder">The builder <c>services.AddSemanticPolicy()</c> returned.</param>
    /// <param name="name">
    /// The registration's name, distinct across registrations: what a policy's bindings refer to, the
    /// name of the <see cref="HttpClient"/> the factory creates, and the provider's
    /// <see cref="HttpProviderOptions.Id"/> unless <paramref name="configure"/> sets another.
    /// </param>
    /// <param name="configure">Fills the options: the base URL, the model and the capabilities at least.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, or the configured options fail
    /// <see cref="HttpProviderOptions.EnsureValid"/>.
    /// </exception>
    public static ISemanticPolicyBuilder AddHttpProvider(
        this ISemanticPolicyBuilder builder,
        string name,
        Action<HttpProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        // The id starts as the registration's name, so the name a policy binds to, the name of the client
        // the factory creates and the id every verdict carries are one name. configure still wins.
        HttpProviderOptions configured = new() { Id = name };
        configure(configured);
        HttpProviderOptions snapshot = Copy(configured);
        snapshot.EnsureValid();

        ProviderHttp.AddClient(builder.Services, name);
        return builder.AddProvider(name, container =>
        {
            IHttpClientFactory factory = container.GetRequiredService<IHttpClientFactory>();
            HttpProviderOptions options = Copy(snapshot);

            // The variable has been read once it reaches the provider, so it is cleared: an unset one
            // means no key, not a key the provider would have to look for.
            options.ApiKey = ProviderHttp.ResolveKey(options.ApiKey, options.ApiKeyVariable);
            options.ApiKeyVariable = null;
            return new HttpProvider(() => factory.CreateClient(name), options);
        });
    }

    // The collections are copied into arrays of their own, so a list the host keeps and changes later
    // is not reachable from the snapshot; a Uri is immutable and the rest are values or strings.
    private static HttpProviderOptions Copy(HttpProviderOptions source) =>
        new()
        {
            BaseUrl = source.BaseUrl,
            Path = source.Path,
            Model = source.Model,
            ApiKey = source.ApiKey,
            ApiKeyVariable = source.ApiKeyVariable,
            AllowInsecureHttp = source.AllowInsecureHttp,
            Types = source.Types is null ? null : [.. source.Types],
            Evidence = source.Evidence is null ? null : [.. source.Evidence],
            StructuredContext = source.StructuredContext,
            MaxContextLength = source.MaxContextLength,
            Timeout = source.Timeout,
            Id = source.Id,
        };
}
