using System.Reflection;
using SemanticPolicy.Evals.Cli;

namespace SemanticPolicy.Evals.Recordings;

/// <summary>
/// The first line of a recording: everything a later replay needs to know what produced the results, and
/// nothing a row said. The policy is stored whole because a replay is only allowed against a structurally
/// identical rule, and the digests are what a dataset is joined back to.
/// </summary>
/// <param name="Format">The format marker; <see cref="FormatV0"/> is the only one this tool reads.</param>
/// <param name="Policy">The policy the run was made with, as the library serializes it.</param>
/// <param name="Datasets">The dataset files read, each with the digest of its bytes.</param>
/// <param name="Providers">The providers called, by the registration name the attempts are keyed by.</param>
/// <param name="ToolVersion">The version of the tool that wrote the file.</param>
/// <param name="RecordedAt">When the run started.</param>
/// <param name="Parallel">How many attempts the run dispatched at once.</param>
/// <param name="Timeout">The per-attempt timeout the run applied.</param>
public sealed record RecordingHeader(
    string Format,
    Policy Policy,
    IReadOnlyList<RecordedDataset> Datasets,
    IReadOnlyList<RecordedProvider> Providers,
    string ToolVersion,
    DateTimeOffset RecordedAt,
    int Parallel,
    TimeSpan Timeout)
{
    /// <summary>The format of a recording this tool writes and reads.</summary>
    public const string FormatV0 = "semanticpolicy/evals-recording/v0";

    /// <summary>The version a header written now carries.</summary>
    public static string CurrentToolVersion { get; } = ReadToolVersion();

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

    private static string ReadToolVersion()
    {
        // The tool's own assembly, never the entry assembly: under `dotnet test` the entry assembly is the
        // test runner, so a recording written from a test would carry the runner's version.
        Assembly assembly = typeof(EvalsCli).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}

/// <summary>One dataset file of a run, as the header records it.</summary>
/// <param name="Path">The path the file was read from, as it was given.</param>
/// <param name="Sha256">The lowercase hex SHA-256 of the file's bytes, which a replay checks.</param>
/// <param name="Split">Which half the file was, in two-file mode; <see langword="null"/> for a single dataset.</param>
public sealed record RecordedDataset(string Path, string Sha256, string? Split);

/// <summary>One provider of a run: the name its attempts are keyed by, and what it said it ran.</summary>
/// <param name="Name">The registration name, which is also a binding's provider id.</param>
/// <param name="Model">The model the provider reported, when it reported one.</param>
public sealed record RecordedProvider(string Name, string? Model);
