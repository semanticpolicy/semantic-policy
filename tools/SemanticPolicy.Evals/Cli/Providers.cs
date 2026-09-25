using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Providers;
using SemanticPolicy.Providers.SystemOne;
using SemanticPolicy.Providers.TypeSafe;

namespace SemanticPolicy.Evals.Cli;

/// <summary>
/// The providers <c>run</c> may call, registered the way an application registers them. Whatever the hook adds
/// through the builder's <c>AddProvider</c> is the whole set; <see cref="Register"/> is the hook the tool itself
/// passes.
/// </summary>
public static class Providers
{
    private const string _localUrlVariable = "SEMANTICPOLICY_EVALS_LOCAL_URL";

    // Where Von listens when served on 127.0.0.1 with its default port, so a local run needs no setting.
    private static readonly Uri _defaultLocalUrl = new("http://127.0.0.1:8000");

    // `run --timeout` is the only per-call limit: the runner cancels each attempt at it, records that as a timeout
    // and stamps the limit in the recording's header. An adapter's own timer, ten seconds by default, would cut
    // calls sooner under a longer --timeout while the header still named the longer one, so each adapter's timer
    // is set past any limit a run would use.
    private static readonly TimeSpan _pastAnyRunTimeout = TimeSpan.FromDays(1);

    /// <summary>
    /// Registers the providers the tool runs with. <c>local</c> is a Von server at the address in
    /// <c>SEMANTICPOLICY_EVALS_LOCAL_URL</c>, <c>http://127.0.0.1:8000</c> when unset: it needs no key and sends
    /// content to that address only. <c>jev</c> is TypeSafe's Jev model reached through OpenRouter: it needs
    /// <c>OPENROUTER_API_KEY</c> and sends every input it evaluates to that third party. <see cref="Resolve"/>
    /// builds only the ones a policy binds.
    /// </summary>
    /// <param name="builder">The builder <see cref="Resolve"/> hands its hook.</param>
    /// <exception cref="EvalsException">
    /// The address is not an absolute <c>http</c> or <c>https</c> URL, or is plain <c>http</c> to a host that is not
    /// loopback.
    /// </exception>
    public static void Register(ISemanticPolicyBuilder builder)
    {
        Uri local = LocalUrl();
        try
        {
            builder.AddSystemOne("local", options =>
            {
                options.BaseUrl = local;
                options.Model = "von-1.2.2";
                options.Timeout = _pastAnyRunTimeout;
            });
        }
        catch (ArgumentException failure) when (failure.ParamName == nameof(SystemOneOptions.BaseUrl))
        {
            // The scheme and loopback rules are the adapter's; the tool only says which setting broke them.
            throw InvalidLocalUrl();
        }

        builder.AddTypeSafeJev("jev", options =>
        {
            options.Route = TypeSafeJevRoute.OpenRouter;
            options.Timeout = _pastAnyRunTimeout;
        });
    }

    /// <summary>
    /// Applies the hook to a builder over <c>AddSemanticPolicy()</c>, then builds the providers the policy binds and
    /// no other, so a registration the policy leaves unbound cannot stop the run with a missing key or server.
    /// </summary>
    /// <param name="configureProviders">Adds providers to the builder; <see langword="null"/> registers none.</param>
    /// <param name="policy">The policy whose bindings name the providers to build.</param>
    /// <returns>Each provider the policy binds, keyed by the name its bindings refer to.</returns>
    /// <exception cref="EvalsException">
    /// Two registrations share a name, a binding names a provider nothing registered, or a bound provider cannot be
    /// built from its configuration, such as a key missing from the environment.
    /// </exception>
    public static IReadOnlyDictionary<string, IDecisionProvider> Resolve(
        Action<ISemanticPolicyBuilder>? configureProviders,
        Policy policy)
    {
        ServiceCollection services = new();
        CapturingBuilder builder = new(services.AddSemanticPolicy());
        configureProviders?.Invoke(builder);

        Dictionary<string, Func<IServiceProvider, IDecisionProvider>> factories = new(StringComparer.Ordinal);
        foreach ((string name, Func<IServiceProvider, IDecisionProvider> factory) in builder.Captured)
        {
            if (!factories.TryAdd(name, factory))
            {
                throw new EvalsException($"Provider '{name}' is registered twice; each name must be registered once.");
            }
        }

        ProviderBinding? missing = policy.Bindings.FirstOrDefault(binding => !factories.ContainsKey(binding.ProviderId));
        if (missing is not null)
        {
            string registered = factories.Count == 0 ? "none" : string.Join(", ", factories.Keys.Order(StringComparer.Ordinal));
            throw new EvalsException(
                $"Policy '{policy.Id}' binds provider '{missing.ProviderId}', which is not registered; registered providers: {registered}.");
        }

        // The container is deliberately not disposed. The caller goes on to call the adapters built over it; the
        // process ends with the verb.
        ServiceProvider container = services.BuildServiceProvider();
        Dictionary<string, IDecisionProvider> providers = new(StringComparer.Ordinal);
        foreach (string name in policy.Bindings.Select(binding => binding.ProviderId).Distinct(StringComparer.Ordinal))
        {
            try
            {
                providers.Add(name, factories[name](container));
            }
            catch (PolicyConfigurationException failure)
            {
                // Each adapter checks its own configuration, its key above all, when it is built. A missing key is a
                // setup step the user fixes, not a bug, so it gets the adapter's message and no stack trace.
                throw new EvalsException(failure.Message);
            }
        }

        return providers;
    }

    private static Uri LocalUrl()
    {
        string? value = Environment.GetEnvironmentVariable(_localUrlVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return _defaultLocalUrl;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? url) ? url : throw InvalidLocalUrl();
    }

    // The value is not echoed: an address can carry credentials, and this message lands in terminals and CI logs.
    private static EvalsException InvalidLocalUrl() =>
        new($"{_localUrlVariable} must be an absolute https URL, or an http URL to a loopback host such as 127.0.0.1.");

    // Core's ProviderRegistration is built with its provider, so reading the names back from the container would
    // build every adapter and fail on any one that cannot be built. The names and factories are captured here, on
    // the way in, and every call is still forwarded, so Core validates the arguments as it would for an application.
    private sealed class CapturingBuilder(ISemanticPolicyBuilder inner) : ISemanticPolicyBuilder
    {
        public List<(string Name, Func<IServiceProvider, IDecisionProvider> Factory)> Captured { get; } = [];

        public IServiceCollection Services => inner.Services;

        public ISemanticPolicyBuilder AddProvider(string name, Func<IServiceProvider, IDecisionProvider> factory)
        {
            inner.AddProvider(name, factory);
            Captured.Add((name, factory));
            return this;
        }

        public ISemanticPolicyBuilder AddProvider(IDecisionProvider provider, string? name = null)
        {
            inner.AddProvider(provider, name);
            Captured.Add((name ?? provider.Id, _ => provider));
            return this;
        }

        public ISemanticPolicyBuilder AddPolicy(Policy policy)
        {
            inner.AddPolicy(policy);
            return this;
        }
    }
}
