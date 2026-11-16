using System.Reflection;

namespace SemanticPolicy.Evals.Format;

/// <summary>
/// The first line of a recording: everything a later replay needs to know what produced the results, and
/// nothing a row said. The policy is stored whole because a replay is only allowed against a structurally
/// identical rule, and the digests are what a dataset is joined back to.
/// </summary>
/// <param name="Format">The format marker; <see cref="FormatV0"/> is the only one this package reads.</param>
/// <param name="Policy">The policy the run was made with, as the library serializes it.</param>
/// <param name="Datasets">The dataset files read, each with the digest of its bytes.</param>
/// <param name="Providers">The providers called, by the registration name the attempts are keyed by.</param>
/// <param name="ToolVersion">
/// The version of what wrote the file: the tool for a run, this package for a header made by
/// <see cref="ForExport"/>.
/// </param>
/// <param name="RecordedAt">When the run started, or when the export was made.</param>
/// <param name="Parallel">
/// How many attempts the run dispatched at once; <see langword="null"/> in an export, which dispatched nothing.
/// </param>
/// <param name="Timeout">
/// The per-call timeout the run applied; <see langword="null"/> in an export, which applied none.
/// </param>
/// <param name="Retries">
/// How many times the run called an unavailable answer again; <see langword="null"/> in a recording written
/// before the tool retried, and in an export.
/// </param>
/// <param name="Where">
/// The run's <c>--where</c> filters as <c>metadata.&lt;key&gt;=&lt;value&gt;</c>, empty when it had none;
/// <see langword="null"/> in a recording written before the tool recorded them, and in an export.
/// </param>
/// <param name="Resumptions">
/// Every <c>--resume</c> that rewrote the recording, oldest first; <see langword="null"/> when none did. The
/// other values stay the first run's.
/// </param>
public sealed record RecordingHeader(
    string Format,
    Policy Policy,
    IReadOnlyList<RecordedDataset> Datasets,
    IReadOnlyList<RecordedProvider> Providers,
    string ToolVersion,
    DateTimeOffset RecordedAt,
    int? Parallel,
    TimeSpan? Timeout,
    int? Retries = null,
    IReadOnlyList<string>? Where = null,
    IReadOnlyList<RecordedResumption>? Resumptions = null)
{
    /// <summary>The format of a recording this package writes and reads.</summary>
    public const string FormatV0 = "semanticpolicy/evals-recording/v0";

    /// <summary>The format marker, which a reader checks before anything else.</summary>
    public string Format { get; init; } = Format ?? throw new ArgumentNullException(nameof(Format));

    /// <summary>The policy of the run.</summary>
    public Policy Policy { get; init; } = Policy ?? throw new ArgumentNullException(nameof(Policy));

    /// <summary>The datasets of the run.</summary>
    public IReadOnlyList<RecordedDataset> Datasets { get; init; } =
        Datasets ?? throw new ArgumentNullException(nameof(Datasets));

    /// <summary>The providers of the run.</summary>
    public IReadOnlyList<RecordedProvider> Providers { get; init; } =
        Providers ?? throw new ArgumentNullException(nameof(Providers));

    /// <summary>The tool version of the run.</summary>
    public string ToolVersion { get; init; } = ToolVersion ?? throw new ArgumentNullException(nameof(ToolVersion));

    /// <summary>
    /// The <see cref="SemanticPolicy.Policy.Digest"/> of <see cref="Policy"/> as the writer computed it, stored as
    /// written and never recomputed on read; <see langword="null"/> in a recording written before headers carried
    /// it. A row's own <see cref="RecordedRow.PolicyDigest"/> names the policy that produced that row's results.
    /// </summary>
    public string? PolicyDigest { get; init; }

    /// <summary>
    /// What wrote the rows: a run of the tool, or an export of stored verdicts. <see langword="null"/> means a run,
    /// and a run writes none.
    /// </summary>
    public RecordingSource? Source { get; init; }

    // This package's own assembly: an export's toolVersion names the format library that wrote it, never the
    // application that called it.
    private static readonly string _packageVersion =
        typeof(RecordingHeader).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(RecordingHeader).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>
    /// The header of a recording exported from stored verdicts rather than made by a run. It names the exporting
    /// program in <see cref="Source"/>, carries <paramref name="policy"/>'s digest and this package's version, and
    /// leaves out what describes a run — <see cref="Parallel"/>, <see cref="Timeout"/>, <see cref="Retries"/>,
    /// <see cref="Where"/> and <see cref="Resumptions"/> — because an export dispatched, timed, retried and
    /// selected nothing. Without <c>where</c> and <c>retries</c> every release of the tool refuses to
    /// <c>--resume</c> it, so none calls a provider for rows the exporting program's evaluator already decided.
    /// </summary>
    /// <param name="policy">The policy the verdicts are replayed under, written whole into the header.</param>
    /// <param name="datasets">The dataset file the rows come from, with the digest its writer returned.</param>
    /// <param name="providers">The providers the policy's bindings name, with the model each reported.</param>
    /// <param name="sourceName">The exporting program's name.</param>
    /// <param name="sourceVersion">The exporting program's version.</param>
    /// <param name="recordedAt">When the export was made.</param>
    /// <exception cref="ArgumentException">The source's name or version is empty or white space.</exception>
    public static RecordingHeader ForExport(
        Policy policy,
        IReadOnlyList<RecordedDataset> datasets,
        IReadOnlyList<RecordedProvider> providers,
        string sourceName,
        string sourceVersion,
        DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVersion);
        return new RecordingHeader(FormatV0, policy, datasets, providers, _packageVersion, recordedAt, Parallel: null, Timeout: null)
        {
            PolicyDigest = policy.Digest,
            Source = new RecordingSource(RecordingSourceKind.Export, sourceName, sourceVersion),
        };
    }
}

/// <summary>What wrote a recording's rows.</summary>
public enum RecordingSourceKind
{
    /// <summary>A run of the tool, which called the providers itself.</summary>
    Run,

    /// <summary>A program that converted verdicts it had stored, holding only the attempts its evaluator made.</summary>
    Export,
}

/// <summary>
/// The <c>source</c> of a recording's header. An export names the program that wrote it, because nothing else in
/// the file says whose stored verdicts these are.
/// </summary>
/// <param name="Kind">A run or an export.</param>
/// <param name="Name">The exporting program's name; required for an export.</param>
/// <param name="Version">The exporting program's version; required for an export.</param>
public sealed record RecordingSource(RecordingSourceKind Kind, string? Name = null, string? Version = null);

/// <summary>One dataset file of a run, as the header records it.</summary>
/// <param name="Path">The path the file was read from, as it was given.</param>
/// <param name="Sha256">The lowercase hex SHA-256 of the file's bytes, which a replay checks.</param>
/// <param name="Split">Which half the file was, in two-file mode; <see langword="null"/> for a single dataset.</param>
public sealed record RecordedDataset(string Path, string Sha256, string? Split);

/// <summary>One provider of a run: the name its attempts are keyed by, and what it said it ran.</summary>
/// <param name="Name">The registration name, which is also a binding's provider id.</param>
/// <param name="Model">The model the provider reported, when it reported one.</param>
public sealed record RecordedProvider(string Name, string? Model);

/// <summary>One <c>--resume</c> of a recording: when it ran, and the settings a resume may change.</summary>
/// <param name="ResumedAt">When the resume started.</param>
/// <param name="Parallel">How many attempts the resume dispatched at once.</param>
/// <param name="Retries">How many times the resume called an unavailable answer again.</param>
/// <param name="ToolVersion">The version of the tool that resumed.</param>
public sealed record RecordedResumption(DateTimeOffset ResumedAt, int Parallel, int Retries, string ToolVersion);
