using Microsoft.Extensions.DependencyInjection.Extensions;
using SemanticPolicy;
using SemanticPolicy.Providers;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the SemanticPolicy runtime in a service collection. What it registers evaluates policies
/// and returns verdicts; a verdict is a semantic signal for the application's own code to act on,
/// never an authorization.
/// </summary>
public static class SemanticPolicyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPolicyEvaluator"/> as a singleton over every provider and policy added
    /// through the returned builder. Calling it again registers nothing more and returns a builder over
    /// the same collection, so libraries can each call it without knowing about one another.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The builder the providers and policies are added through.</returns>
    public static ISemanticPolicyBuilder AddSemanticPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPolicyEvaluator, PolicyEvaluator>();
        return new Builder(services);
    }

    // Providers register as ProviderRegistration singletons and policies as Policy singletons, which is
    // exactly the pair of enumerables the evaluator's constructor takes: the container gathers them and
    // the evaluator never sees the container.
    private sealed class Builder(IServiceCollection services) : ISemanticPolicyBuilder
    {
        public IServiceCollection Services { get; } = services;

        public ISemanticPolicyBuilder AddProvider(string name, Func<IServiceProvider, IDecisionProvider> factory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(factory);
            Services.AddSingleton(container => new ProviderRegistration(name, factory(container)));
            return this;
        }

        public ISemanticPolicyBuilder AddProvider(IDecisionProvider provider, string? name = null)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (name is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
            }

            Services.AddSingleton(new ProviderRegistration(name ?? provider.Id, provider));
            return this;
        }

        public ISemanticPolicyBuilder AddPolicy(Policy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);
            Services.AddSingleton(policy);
            return this;
        }
    }
}
