namespace SemanticPolicy.Providers.TypeSafe;

/// <summary>
/// Where a Jev call goes and how it is named there: the base URL, the path that follows it, the model
/// the route pins and the environment variable its key is read from. Two presets cover the vendor's
/// own endpoint and OpenRouter's gateway; the public constructor leaves room for any other gateway
/// that speaks the same wire. Nothing in the adapter switches on which route it was given.
/// </summary>
/// <param name="BaseUrl">
/// An absolute URL. It is joined to <paramref name="Path"/> as text, with one trailing <c>/</c> removed,
/// so a base that carries a path of its own keeps it.
/// </param>
/// <param name="Path">The endpoint path, starting with <c>/</c>.</param>
/// <param name="Model">
/// The model id the route pins. Thresholds are measured per model, so a route names an exact version
/// rather than an alias that moves without a trace in configuration.
/// </param>
/// <param name="ApiKeyVariable">
/// The environment variable a registration reads the key from when the options set none explicitly.
/// </param>
public sealed record TypeSafeJevRoute(Uri BaseUrl, string Path, string Model, string ApiKeyVariable)
{
    /// <summary>The vendor's own endpoint, pinned to <c>jev-1.13.0</c>, keyed by <c>TYPESAFE_API_KEY</c>.</summary>
    public static TypeSafeJevRoute TypeSafe { get; } =
        new(new Uri("https://api.typesafe.ai"), "/v1/systemone", "jev-1.13.0", "TYPESAFE_API_KEY");

    /// <summary>
    /// OpenRouter's gateway to the same endpoint, pinned to <c>typesafe/jev-1.13</c>, keyed by
    /// <c>OPENROUTER_API_KEY</c>.
    /// </summary>
    public static TypeSafeJevRoute OpenRouter { get; } =
        new(new Uri("https://openrouter.ai/api"), "/v1/systemone", "typesafe/jev-1.13", "OPENROUTER_API_KEY");
}
