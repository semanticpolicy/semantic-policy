using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Providers;

namespace SemanticPolicy;

/// <summary>
/// What <c>services.AddSemanticPolicy()</c> hands back: the place to register the providers a policy
/// binds to and the policies the evaluator serves by id. Provider packages extend it with their own
/// <c>Add…</c> methods over <see cref="Services"/>. Everything registered here reaches the evaluator
/// when it is first resolved, which is also when a duplicate name or a policy no provider can serve
/// fails — the evaluator's constructor rule, seen through the container.
/// </summary>
public interface ISemanticPolicyBuilder
{
    /// <summary>The service collection the runtime is registered in.</summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Registers a provider under the name a policy's bindings refer to it by, built from the container
    /// when the evaluator is first resolved. The container owns what the factory builds: an adapter
    /// that is <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/> is disposed with the
    /// container, as any service it built is. One adapter class registered twice under two names is
    /// two providers — the way one adapter serves two endpoints.
    /// </summary>
    /// <param name="name">The name a binding's provider id refers to; distinct across registrations.</param>
    /// <param name="factory">Builds the adapter from the container.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The name is empty.</exception>
    ISemanticPolicyBuilder AddProvider(string name, Func<IServiceProvider, IDecisionProvider> factory);

    /// <summary>
    /// Registers an adapter instance, under <paramref name="name"/> when one is given and otherwise
    /// under the adapter's own <see cref="IDecisionProvider.Id"/>. The instance stays the caller's:
    /// the container does not dispose an instance it was handed, so an adapter that needs disposing
    /// is disposed by whoever built it, after the container is gone.
    /// </summary>
    /// <param name="provider">The adapter.</param>
    /// <param name="name">
    /// The name a binding's provider id refers to, or <see langword="null"/> to use the adapter's id.
    /// </param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The name is given and empty.</exception>
    ISemanticPolicyBuilder AddProvider(IDecisionProvider provider, string? name = null);

    /// <summary>
    /// Registers a policy so that
    /// <see cref="IPolicyEvaluator.EvaluateAsync(string, SemanticContext, CancellationToken)"/>
    /// finds it by id. It is validated, and checked against the registered providers, when the evaluator
    /// is first resolved.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <returns>This builder.</returns>
    ISemanticPolicyBuilder AddPolicy(Policy policy);
}
