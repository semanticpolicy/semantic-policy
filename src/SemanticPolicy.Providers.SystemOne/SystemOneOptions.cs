using SemanticPolicy.Protocol;

namespace SemanticPolicy.Providers.SystemOne;

/// <summary>
/// How a <see cref="SystemOneProvider"/> is configured: a mutable class a host fills in a delegate and
/// the provider copies at construction, so a later change to an instance changes nothing that runs.
/// There is no default address, because choosing one would choose where a user's content is sent.
/// </summary>
public sealed class SystemOneOptions
{
    // CancellationTokenSource.CancelAfter takes at most 0xFFFFFFFE ms, about 49.7 days; a longer
    // timeout would pass here and then throw from every call instead.
    private static readonly TimeSpan _maxTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// The id a provider built straight from these options reports, when nothing sets <see cref="Id"/>.
    /// A registration defaults the id to its own name instead, so the two never silently disagree.
    /// </summary>
    public const string DefaultId = "systemone";

    /// <summary>
    /// The server's address, such as <c>http://127.0.0.1:8000</c>. Required and absolute. Plain
    /// <c>http</c> is accepted on a loopback host, and elsewhere only with
    /// <see cref="AllowInsecureHttp"/>.
    /// </summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>The path the decision endpoint answers on, appended to <see cref="BaseUrl"/>.</summary>
    public string Path { get; set; } = "/v1/systemone";

    /// <summary>
    /// The model sent with every request. Required, with no default: some servers pick a checkpoint by
    /// this name and others ignore it, so only the user knows what theirs expects.
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
    /// The kind of evidence the provider reports: <see cref="EvidenceKind.Score"/> by default, on the
    /// <c>systemone</c> scale, because a server's number in [0, 1] is not known to be a probability.
    /// Declaring <see cref="EvidenceKind.Probability"/> reports the same numbers on the
    /// <c>calibrated</c> scale; that is the user's claim that the server is calibrated, and the
    /// provider does not check it. No other kind is accepted.
    /// </summary>
    public EvidenceKind Evidence { get; set; } = EvidenceKind.Score;

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
    /// The id the provider reports on every verdict, as <c>ProviderMetadata.Id</c>. It is not what a
    /// policy binds to — a binding names the registration — so <c>AddSystemOne</c> defaults this to the
    /// registration's name, and only a provider built directly falls back to <see cref="DefaultId"/>.
    /// </summary>
    public string Id { get; set; } = DefaultId;

    /// <summary>
    /// Checks the options before a provider is built from them. A violation is a mistake in the host's
    /// configuration, so it is an exception naming the property rather than a provider outcome.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="BaseUrl"/> is absent or relative, its scheme is neither <c>http</c> nor <c>https</c>,
    /// or it is <c>http</c> to a host other than loopback while <see cref="AllowInsecureHttp"/> is false;
    /// <see cref="Path"/> does not start with <c>/</c>; <see cref="Model"/> is empty;
    /// <see cref="Evidence"/> is neither <see cref="EvidenceKind.Score"/> nor
    /// <see cref="EvidenceKind.Probability"/>; <see cref="MaxContextLength"/> is not greater than
    /// zero; <see cref="Timeout"/> is not greater than zero, or longer than a timer can wait (about 49
    /// days); or <see cref="Id"/> is empty.
    /// </exception>
    public void EnsureValid()
    {
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

        if (string.IsNullOrEmpty(Path) || Path[0] != '/')
        {
            throw new ArgumentException("The Path does not start with '/'.", nameof(Path));
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new ArgumentException("The model is empty.", nameof(Model));
        }

        if (Evidence is not (EvidenceKind.Score or EvidenceKind.Probability))
        {
            throw new ArgumentException("The evidence kind is neither Score nor Probability.", nameof(Evidence));
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
