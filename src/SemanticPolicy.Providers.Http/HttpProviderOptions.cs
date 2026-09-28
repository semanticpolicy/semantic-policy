using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.Http;

/// <summary>
/// How an <see cref="HttpProvider"/> is configured: a mutable class a host fills in a delegate and the
/// provider copies at construction, so a later change to an instance changes nothing that runs. There
/// is no default address, because choosing one would choose where a user's content is sent, and no
/// default capability, because only the user knows what their server answers.
/// </summary>
public sealed class HttpProviderOptions
{
    // CancellationTokenSource.CancelAfter takes at most 0xFFFFFFFE ms, about 49.7 days; a longer
    // timeout would pass here and then throw from every call instead.
    private static readonly TimeSpan _maxTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// The id a provider built straight from these options reports, when nothing sets <see cref="Id"/>.
    /// A registration defaults the id to its own name instead, so the two never silently disagree.
    /// </summary>
    public const string DefaultId = "http";

    /// <summary>
    /// The server's address, such as <c>http://127.0.0.1:8765</c>. Required and absolute, without a
    /// query or a fragment; a path in it, such as a gateway's <c>/api</c>, is kept in front of
    /// <see cref="Path"/>. Plain <c>http</c> is accepted on a loopback host, and elsewhere only with
    /// <see cref="AllowInsecureHttp"/>.
    /// </summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>The path the decision endpoint answers on, appended to <see cref="BaseUrl"/>.</summary>
    public string Path { get; set; } = "/v0/decide";

    /// <summary>
    /// What the server runs, reported as <see cref="ProviderMetadata.Model"/> on every result whose
    /// answer names no model and on every result the provider builds itself, such as a failure.
    /// Required, with no default, and never sent: a protocol v0 request carries no model.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// The key sent as a Bearer token. Optional: without it, and without a variable that holds one, no
    /// <c>Authorization</c> header is sent. Set explicitly it wins over <see cref="ApiKeyVariable"/>.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The environment variable to read the key from when <see cref="ApiKey"/> is not set. Only a
    /// registration reads it, when the evaluator is first resolved; an unset variable then sends no
    /// header. A provider built by hand never reads the environment, so it rejects options that name a
    /// variable without a key.
    /// </summary>
    public string? ApiKeyVariable { get; set; }

    /// <summary>
    /// Whether plain <c>http</c> is accepted to a host that is not loopback. False by default, because
    /// content should cross a network in clear text only when someone decided so in code.
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// The decision types the server answers, reported as <see cref="ProviderCapabilities.Types"/>.
    /// Required and not empty: <see langword="null"/> means not declared, which a registration refuses.
    /// </summary>
    public IReadOnlyCollection<DecisionType>? Types { get; set; }

    /// <summary>
    /// The evidence kinds the server produces, reported as <see cref="ProviderCapabilities.Evidence"/>.
    /// Required, and empty for a server that sends no evidence. A kind the server sends that is not
    /// declared here is dropped from the result. Declaring <see cref="EvidenceKind.Probability"/> is the
    /// user's claim that the server's numbers are calibrated; the provider does not check it.
    /// </summary>
    public IReadOnlyCollection<EvidenceKind>? Evidence { get; set; }

    /// <summary>
    /// Whether the server reads an object or array context as such. Required. When
    /// <see langword="false"/> the provider sends the context as the text
    /// <see cref="SemanticContext.ToCanonicalText(System.Text.Json.JsonElement)"/> renders, so every
    /// text-only server reads the same text.
    /// </summary>
    public bool? StructuredContext { get; set; }

    /// <summary>
    /// The longest context, in characters of its canonical text, the provider sends. A longer one is
    /// not sent and comes back as a <see cref="FailureKind.RejectedInput"/> failure, which the policy's
    /// failure behaviour handles. <see langword="null"/>, the default, sends every context; set it for
    /// a server that truncates long input without saying so.
    /// </summary>
    public int? MaxContextLength { get; set; }

    /// <summary>
    /// How long one call may take, reading the body included, before the provider reports it as a
    /// <see cref="FailureKind.Timeout"/>. Ten seconds by default.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The id the provider reports on every result, as <see cref="ProviderMetadata.Id"/>, whatever id
    /// the server names. It is not what a policy binds to — a binding names the registration — so
    /// <c>AddHttpProvider</c> defaults this to the registration's name, and only a provider built
    /// directly falls back to <see cref="DefaultId"/>.
    /// </summary>
    public string Id { get; set; } = DefaultId;

    /// <summary>
    /// Checks the options before a provider is built from them. A violation is a mistake in the host's
    /// configuration, so it is an exception naming the property rather than a provider outcome.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="Types"/>, <see cref="Evidence"/> or <see cref="StructuredContext"/> is not declared,
    /// or <see cref="Types"/> is empty; <see cref="BaseUrl"/> is absent or relative, its scheme is
    /// neither <c>http</c> nor <c>https</c>, it is <c>http</c> to a host other than loopback while
    /// <see cref="AllowInsecureHttp"/> is false, or it carries a query or a fragment;
    /// <see cref="Path"/> does not start with <c>/</c>;
    /// <see cref="Model"/> is empty; <see cref="MaxContextLength"/> is not greater than zero;
    /// <see cref="Timeout"/> is not greater than zero, or longer than a timer can wait (about 49 days);
    /// or <see cref="Id"/> is empty.
    /// </exception>
    public void EnsureValid()
    {
        if (Types is null)
        {
            throw new ArgumentException("The decision types the server answers are not declared.", nameof(Types));
        }

        if (Types.Count == 0)
        {
            throw new ArgumentException("The declared decision types are empty.", nameof(Types));
        }

        // Empty is a declaration, of a server that sends no evidence; only null is missing one.
        if (Evidence is null)
        {
            throw new ArgumentException("The evidence kinds the server produces are not declared.", nameof(Evidence));
        }

        if (StructuredContext is null)
        {
            throw new ArgumentException(
                "Whether the server reads a structured context is not declared.",
                nameof(StructuredContext));
        }

        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The BaseUrl is not an absolute URI.", nameof(BaseUrl));
        }

        // Checked whatever the host and the flag say: HttpClient fails any other scheme with a
        // NotSupportedException, which no failure kind covers, so every call would throw.
        if (BaseUrl.Scheme != Uri.UriSchemeHttps && BaseUrl.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("The BaseUrl's scheme is neither http nor https.", nameof(BaseUrl));
        }

        if (BaseUrl.Scheme == Uri.UriSchemeHttp && !BaseUrl.IsLoopback && !AllowInsecureHttp)
        {
            throw new ArgumentException(
                "The BaseUrl must use https unless its host is loopback or AllowInsecureHttp is set.",
                nameof(BaseUrl));
        }

        // The path is appended to the base as text, so a query or a fragment would swallow it.
        if (BaseUrl.Query.Length > 0 || BaseUrl.Fragment.Length > 0)
        {
            throw new ArgumentException("The BaseUrl carries a query or a fragment.", nameof(BaseUrl));
        }

        if (string.IsNullOrEmpty(Path) || Path[0] != '/')
        {
            throw new ArgumentException("The Path does not start with '/'.", nameof(Path));
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new ArgumentException("The model is empty.", nameof(Model));
        }

        if (MaxContextLength <= 0)
        {
            throw new ArgumentException("The MaxContextLength is not greater than zero.", nameof(MaxContextLength));
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("The timeout is not greater than zero.", nameof(Timeout));
        }

        if (Timeout > _maxTimeout)
        {
            throw new ArgumentException("The timeout is longer than a timer can wait, about 49 days.", nameof(Timeout));
        }

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new ArgumentException("The id is empty.", nameof(Id));
        }
    }
}
