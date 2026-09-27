using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers;

/// <summary>
/// What every HTTP provider does the same way: the named client a registration sets up, the endpoint
/// and the user agent a request carries, the key a registration reads, and a status and a body read
/// before a provider's own interpretation.
/// </summary>
/// <remarks>
/// Compiled into every HTTP provider package as a linked file rather than shipped as a public type, so
/// the packages share one behaviour without a shared API to version.
/// </remarks>
internal static class ProviderHttp
{
    /// <summary>The longest string <see cref="Identifier"/> accepts.</summary>
    public const int MaxIdentifierLength = 200;

    /// <summary>
    /// Every status other than 200 as a failure kind. 400 and 422 are the server's verdict on the
    /// request, so they are <see cref="FailureKind.RejectedInput"/>; a 2xx that is not 200 carries no
    /// answer a provider reads, so it is <see cref="FailureKind.Malformed"/>.
    /// </summary>
    public static FailureKind KindOf(HttpStatusCode status) =>
        (int)status switch
        {
            401 or 403 => FailureKind.Unauthorized,
            400 or 404 or 413 or 422 => FailureKind.RejectedInput,
            408 or 429 => FailureKind.Unavailable,
            >= 500 and <= 599 => FailureKind.Unavailable,
            >= 200 and <= 299 => FailureKind.Malformed,
            _ => FailureKind.Unknown,
        };

    /// <summary>
    /// <paramref name="value"/> when it reads as an identifier, such as a model name, a request id, a
    /// scale or an error code: 1 to 200 characters of printable ASCII with no space. Otherwise
    /// <see langword="null"/>. The server wrote it and a result carries it wherever the result is
    /// written, so a string that could hold a sentence, a line break or a quoted input is dropped.
    /// </summary>
    public static string? Identifier(string? value) =>
        value is { Length: > 0 and <= MaxIdentifierLength } && !value.AsSpan().ContainsAnyExceptInRange('!', '~')
            ? value
            : null;

    /// <summary>The body parsed and detached from its document, or <see langword="null"/> when it is not JSON.</summary>
    public static JsonElement? TryParse(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The endpoint, joined as text: <see cref="Uri"/>'s own combination would drop a base path such as
    /// a gateway's <c>/api</c>.
    /// </summary>
    public static Uri Endpoint(Uri baseUrl, string path) => new(baseUrl.AbsoluteUri.TrimEnd('/') + path);

    /// <summary>
    /// The package's name and version, such as <c>SemanticPolicy.Providers.Http/0.1.0-alpha.2</c>. The
    /// informational version is cut at the first <c>+</c>, where the SDK appends the commit hash; the
    /// assembly version's three components stand in when the attribute is absent.
    /// </summary>
    public static ProductInfoHeaderValue UserAgent(Assembly assembly)
    {
        string version = (assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            version = plus < 0 ? informational : informational[..plus];
        }

        return new ProductInfoHeaderValue(assembly.GetName().Name!, version);
    }

    /// <summary>
    /// Sets up the named client a registration's provider sends through. Every logger is removed, not
    /// filtered: whatever a host's redaction default is, a Trace level in a developer's environment
    /// must never print the <c>Authorization</c> header, and a host that wants the factory's logging
    /// back calls <c>AddDefaultLogger()</c> on the same name, with its own redaction. The client's own
    /// timeout is disabled because the provider keeps its own timer over the whole call: a shorter
    /// client timeout would surface as a cancellation with no token cancelled. Redirects are not
    /// followed: a 307 or 308 would resend the context to wherever the server points, so a redirect
    /// reads as a failure instead.
    /// </summary>
    public static void AddClient(IServiceCollection services, string name) =>
        services.AddHttpClient(name)
            .RemoveAllLoggers()
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler((handler, _) => DisableRedirects(handler));

    // The factory's default primary handler is one of these two; a handler the host sets on the same
    // name after the registration replaces this one, and its redirects are the host's to decide.
    private static void DisableRedirects(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.AllowAutoRedirect = false;
                break;
            case HttpClientHandler client:
                client.AllowAutoRedirect = false;
                break;
        }
    }

    /// <summary>
    /// An optional key: <paramref name="apiKey"/> when set, else the value of
    /// <paramref name="variable"/>, else <see langword="null"/>, which sends no header. A registration
    /// reads it when the evaluator is first resolved, never at the registration call, so the variable
    /// may be set after registration.
    /// </summary>
    public static string? ResolveKey(string? apiKey, string? variable)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        if (string.IsNullOrWhiteSpace(variable))
        {
            return null;
        }

        string? key = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }
}
