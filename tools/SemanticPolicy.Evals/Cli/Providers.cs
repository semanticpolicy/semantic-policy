using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Providers;
using SemanticPolicy.Providers.TypeSafe;

namespace SemanticPolicy.Evals.Cli;

/// <summary>
/// The providers <c>run</c> may call, registered the way an application registers them. Whatever the hook adds is
/// the whole set; <see cref="Register"/> is the hook the tool itself passes.
/// </summary>
public static class Providers
{
    /// <summary>
    /// Registers the providers the tool runs with: TypeSafe Jev as <c>jev</c>, reached through OpenRouter, so
    /// <c>run</c> needs <c>OPENROUTER_API_KEY</c> and sends every input it evaluates to that third party.
    /// </summary>
    /// <param name="builder">The builder <see cref="Resolve"/> hands its hook.</param>
    public static void Register(ISemanticPolicyBuilder builder) =>
        builder.AddTypeSafeJev("jev", options =>
        {
            options.Route = TypeSafeJevRoute.OpenRouter;

            // `run --timeout` is the only per-call limit: the runner cancels each attempt at it, records that as a
            // timeout and stamps the limit in the recording's header. The adapter's own timer, ten seconds by
            // default, would cut calls sooner under a longer --timeout while the header still named the longer
            // one, so it is set past any limit a run would use.
            options.Timeout = TimeSpan.FromDays(1);
        });

    /// <summary>Builds a container over <c>AddSemanticPolicy()</c>, applies the hook and reads the registrations.</summary>
    /// <param name="configureProviders">Adds providers to the builder; <see langword="null"/> registers none.</param>
    /// <returns>Every registered provider, keyed by the name a binding's provider id refers to.</returns>
    /// <exception cref="EvalsException">
    /// Two registrations share a name, or a provider cannot be built from its configuration, such as a key missing
    /// from the environment.
    /// </exception>
    public static IReadOnlyDictionary<string, IDecisionProvider> Resolve(Action<ISemanticPolicyBuilder>? configureProviders)
    {
        ServiceCollection services = new();
        ISemanticPolicyBuilder builder = services.AddSemanticPolicy();
        configureProviders?.Invoke(builder);

        // The container is deliberately not disposed. Disposing it would dispose every adapter a factory
        // registration built, and the caller goes on to call those adapters; the process ends with the verb.
        ServiceProvider container = services.BuildServiceProvider();
        ProviderRegistration[] registrations;
        try
        {
            registrations = [.. container.GetServices<ProviderRegistration>()];
        }
        catch (PolicyConfigurationException failure)
        {
            // Each adapter is built here and checks its own configuration, its key above all. A missing key is a
            // setup step the user fixes, not a bug, so it gets the adapter's message and no stack trace.
            throw new EvalsException(failure.Message);
        }

        Dictionary<string, IDecisionProvider> providers = new(StringComparer.Ordinal);
        foreach (ProviderRegistration registration in registrations)
        {
            if (!providers.TryAdd(registration.Name, registration.Provider))
            {
                throw new EvalsException($"Provider '{registration.Name}' is registered twice; each name must be registered once.");
            }
        }

        return providers;
    }
}
