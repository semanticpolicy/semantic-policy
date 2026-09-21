namespace SemanticPolicy.Providers;

/// <summary>
/// One provider as the evaluator knows it: an adapter under the name a policy's bindings refer to it
/// by. The name is what a binding's provider id must match; the adapter's own <see cref="IDecisionProvider.Id"/>
/// is only the name to use when nothing else is chosen. One adapter registered twice under two names
/// is two providers — the way one adapter class serves two endpoints.
/// </summary>
/// <param name="Name">
/// The name a binding's provider id refers to; distinct across the evaluator's registrations.
/// </param>
/// <param name="Provider">The adapter that answers under the name.</param>
public sealed record ProviderRegistration(string Name, IDecisionProvider Provider)
{
    /// <summary>The name a binding's provider id refers to; distinct across the evaluator's registrations.</summary>
    public string Name { get; init; } = Name ?? throw new ArgumentNullException(nameof(Name));

    /// <summary>The adapter that answers under the name.</summary>
    public IDecisionProvider Provider { get; init; } = Provider ?? throw new ArgumentNullException(nameof(Provider));
}
