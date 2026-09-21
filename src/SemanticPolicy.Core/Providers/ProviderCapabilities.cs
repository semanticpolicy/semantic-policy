using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers;

/// <summary>
/// What an adapter declares about its provider, in-process and never on the wire. A policy asks before
/// it relies on a decision type or an evidence kind; an absent capability is absent, not zero, not
/// <see langword="false"/> and not an empty distribution.
/// </summary>
/// <param name="Types">The decision types the provider answers.</param>
/// <param name="Evidence">The evidence kinds the provider produces.</param>
/// <param name="RawOutput">Whether results carry the provider's response as received.</param>
/// <param name="StructuredContext">
/// Whether the provider reads an object or array context as such. When <see langword="false"/> the
/// adapter renders it to text with <see cref="SemanticContext.ToCanonicalText(System.Text.Json.JsonElement)"/>.
/// </param>
public sealed record ProviderCapabilities(
    IReadOnlySet<DecisionType> Types,
    IReadOnlySet<EvidenceKind> Evidence,
    bool RawOutput,
    bool StructuredContext);
