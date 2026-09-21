using System.Text.Json;

namespace SemanticPolicy.Protocol;

/// <summary>Who answered: the adapter, the model it used and what the caller observed.</summary>
/// <param name="Id">The adapter's id.</param>
/// <param name="Model">
/// What the adapter used, as specific as the provider reports it. <see langword="null"/> only on a
/// result the runtime synthesized without a call; an adapter always fills it.
/// </param>
/// <param name="LatencyMs">The call's duration in milliseconds, as observed by the caller.</param>
/// <param name="RequestId">The provider's id for the call, when it gives one.</param>
/// <param name="Usage">Provider-shaped usage data, when reported. Nothing in it is read by a policy.</param>
/// <param name="Extra">
/// Anything else provider-shaped, such as a vendor's own confidence figure. Nothing in it is read by
/// a policy.
/// </param>
public sealed record ProviderMetadata(
    string Id,
    string? Model,
    double LatencyMs,
    string? RequestId = null,
    JsonElement? Usage = null,
    JsonElement? Extra = null);
