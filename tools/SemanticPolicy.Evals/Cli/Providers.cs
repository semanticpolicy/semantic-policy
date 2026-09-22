using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Evals.Cli;

/// <summary>
/// The providers <c>run</c> may call, registered the way an application registers them. The tool ships no
/// adapter: whatever the hook adds is the whole set, so a build without one can replay and report but not run.
/// </summary>
public static class Providers
{
    /// <summary>Builds a container over <c>AddSemanticPolicy()</c>, applies the hook and reads the registrations.</summary>
    /// <param name="configureProviders">Adds providers to the builder; <see langword="null"/> registers none.</param>
    /// <returns>Every registered provider, keyed by the name a binding's provider id refers to.</returns>
    /// <exception cref="EvalsException">Two registrations share a name.</exception>
    public static IReadOnlyDictionary<string, IDecisionProvider> Resolve(Action<ISemanticPolicyBuilder>? configureProviders)
    {
        ServiceCollection services = new();
        ISemanticPolicyBuilder builder = services.AddSemanticPolicy();
        configureProviders?.Invoke(builder);

        // The container is deliberately not disposed. Disposing it would dispose every adapter a factory
        // registration built, and the caller goes on to call those adapters; the process ends with the verb.
        ServiceProvider container = services.BuildServiceProvider();
        Dictionary<string, IDecisionProvider> providers = new(StringComparer.Ordinal);
        foreach (ProviderRegistration registration in container.GetServices<ProviderRegistration>())
        {
            if (!providers.TryAdd(registration.Name, registration.Provider))
            {
                throw new EvalsException($"Provider '{registration.Name}' is registered twice; each name must be registered once.");
            }
        }

        return providers;
    }
}
