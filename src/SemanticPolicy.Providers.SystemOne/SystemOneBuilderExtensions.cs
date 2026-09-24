using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Providers.SystemOne;

namespace SemanticPolicy;

/// <summary>
/// Registers a System One server on <see cref="ISemanticPolicyBuilder"/>. What it registers answers a
/// rule's question with the server's numbers, as a score unless the options declare them a
/// probability; whether that answer allows or denies anything is the policy's decision, and neither is
/// proof of safety or of an attack.
/// </summary>
public static class SystemOneBuilderExtensions
{
    /// <summary>
    /// Registers a <see cref="SystemOneProvider"/> under <paramref name="name"/>, the name a policy's
    /// bindings refer to. The options are configured, copied and validated here, at the call, so a
    /// mistake in them fails before any container exists and a later change to the configured instance
    /// changes nothing. There is no default address: <paramref name="configure"/> must set
    /// <see cref="SystemOneOptions.BaseUrl"/> and <see cref="SystemOneOptions.Model"/>, because a
    /// default would choose where content is sent.
    /// </summary>
    /// <param name="builder">The builder <c>services.AddSemanticPolicy()</c> returned.</param>
    /// <param name="name">
    /// The registration's name, distinct across registrations: what a policy's bindings refer to, the
    /// name of the <see cref="HttpClient"/> the factory creates, and the provider's
    /// <see cref="SystemOneOptions.Id"/> unless <paramref name="configure"/> sets another.
    /// </param>
    /// <param name="configure">Fills the options: the base URL and the model at least.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, or the configured options fail
    /// <see cref="SystemOneOptions.EnsureValid"/>.
    /// </exception>
    public static ISemanticPolicyBuilder AddSystemOne(
        this ISemanticPolicyBuilder builder,
        string name,
        Action<SystemOneOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        // The id starts as the registration's name, so the name a policy binds to, the name of the client
        // the factory creates and the id every verdict carries are one name. configure still wins.
        SystemOneOptions configured = new() { Id = name };
        configure(configured);
        SystemOneOptions snapshot = Copy(configured);
        snapshot.EnsureValid();

        // Every logger, not a redaction filter: whatever a host's redaction default is, a Trace level in
        // a developer's environment must never print the Authorization header. A host that wants the
        // factory's logging back calls AddDefaultLogger() on the same name, with its own redaction.
        // The client's own timeout is disabled because the provider keeps its own timer over the whole
        // call: a shorter client timeout would surface as a cancellation with no token cancelled.
        builder.Services.AddHttpClient(name)
            .RemoveAllLoggers()
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        return builder.AddProvider(name, container =>
        {
            IHttpClientFactory factory = container.GetRequiredService<IHttpClientFactory>();
            SystemOneOptions options = Copy(snapshot);

            // The variable has been read once it reaches the provider, so it is cleared: an unset one
            // means no key, not a key the provider would have to look for.
            options.ApiKey = ResolveKey(options);
            options.ApiKeyVariable = null;
            return new SystemOneProvider(() => factory.CreateClient(name), options);
        });
    }

    // Read when the evaluator is first resolved, never at the AddSystemOne call, so the variable may be
    // set after registration. The key is optional: a variable that is unset or blank sends no header.
    private static string? ResolveKey(SystemOneOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return options.ApiKey;
        }

        if (string.IsNullOrWhiteSpace(options.ApiKeyVariable))
        {
            return null;
        }

        string? key = Environment.GetEnvironmentVariable(options.ApiKeyVariable);
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    // A Uri is immutable and every other property is a value or a string, so a member-wise copy is a
    // full snapshot: nothing the host still holds is reachable from it.
    private static SystemOneOptions Copy(SystemOneOptions source) =>
        new()
        {
            BaseUrl = source.BaseUrl,
            Path = source.Path,
            Model = source.Model,
            ApiKey = source.ApiKey,
            ApiKeyVariable = source.ApiKeyVariable,
            AllowInsecureHttp = source.AllowInsecureHttp,
            Evidence = source.Evidence,
            MaxContextLength = source.MaxContextLength,
            Timeout = source.Timeout,
            Id = source.Id,
        };
}
