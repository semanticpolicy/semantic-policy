using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Providers.TypeSafe;

namespace SemanticPolicy;

/// <summary>
/// Registers the TypeSafe Jev provider on <see cref="ISemanticPolicyBuilder"/>. What it registers
/// answers a rule's question with a probability; whether that answer allows or denies anything is the
/// policy's decision, and neither is proof of safety or of an attack.
/// </summary>
public static class TypeSafeJevBuilderExtensions
{
    /// <summary>
    /// Registers a <see cref="TypeSafeJevProvider"/> under <paramref name="name"/>, the name a policy's
    /// bindings refer to. The options are configured, copied and validated here, at the call, so a
    /// mistake in them fails before any container exists and a later change to the configured instance
    /// changes nothing. There is no default route: <paramref name="configure"/> must set
    /// <see cref="TypeSafeJevOptions.Route"/>, because a default would choose where content is sent.
    /// </summary>
    /// <param name="builder">The builder <c>services.AddSemanticPolicy()</c> returned.</param>
    /// <param name="name">
    /// The registration's name, distinct across registrations: what a policy's bindings refer to, the
    /// name of the <see cref="HttpClient"/> the factory creates, and the provider's
    /// <see cref="TypeSafeJevOptions.Id"/> unless <paramref name="configure"/> sets another.
    /// </param>
    /// <param name="configure">Fills the options: the route at least.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty, or the configured options fail
    /// <see cref="TypeSafeJevOptions.EnsureValid"/>.
    /// </exception>
    public static ISemanticPolicyBuilder AddTypeSafeJev(
        this ISemanticPolicyBuilder builder,
        string name,
        Action<TypeSafeJevOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        // The id starts as the registration's name, so the name a policy binds to, the name of the client
        // the factory creates and the id every verdict carries are one name. Two registrations that both
        // kept TypeSafeJevOptions.DefaultId would be indistinguishable on a verdict. configure still wins.
        TypeSafeJevOptions configured = new() { Id = name };
        configure(configured);
        TypeSafeJevOptions snapshot = Copy(configured);
        snapshot.EnsureValid();

        // Every logger, not a redaction filter: whatever a host's redaction default is, a Trace level in
        // a developer's environment must never print the Authorization header. A host that wants the
        // factory's logging back calls AddDefaultLogger() on the same name, with its own redaction.
        // The client's own timeout is disabled because the adapter keeps its own timer over the whole
        // call: a shorter client timeout would surface as a cancellation with no token cancelled.
        builder.Services.AddHttpClient(name)
            .RemoveAllLoggers()
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        return builder.AddProvider(name, container =>
        {
            IHttpClientFactory factory = container.GetRequiredService<IHttpClientFactory>();
            TypeSafeJevOptions options = Copy(snapshot);
            options.ApiKey = ResolveKey(options, name);
            return new TypeSafeJevProvider(() => factory.CreateClient(name), options);
        });
    }

    // Read when the evaluator is first resolved, so a missing key fails at start-up rather than on the
    // first request, and never at the AddTypeSafeJev call, where a test that forgot a key would pick up
    // a developer's real one.
    private static string ResolveKey(TypeSafeJevOptions options, string name)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return options.ApiKey;
        }

        string variable = options.ApiKeyVariable ?? options.Route!.ApiKeyVariable;
        string? key = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new PolicyConfigurationException(
                $"provider '{name}': the environment variable {variable} is not set.",
                policyId: null,
                providerId: name);
        }

        return key;
    }

    // The route is a record and every other property is a value or a string, so a member-wise copy is a
    // full snapshot: nothing the host still holds is reachable from it.
    private static TypeSafeJevOptions Copy(TypeSafeJevOptions source) =>
        new()
        {
            Route = source.Route,
            Model = source.Model,
            ApiKey = source.ApiKey,
            ApiKeyVariable = source.ApiKeyVariable,
            Timeout = source.Timeout,
            Id = source.Id,
        };
}
