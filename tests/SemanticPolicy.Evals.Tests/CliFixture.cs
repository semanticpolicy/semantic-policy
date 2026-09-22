using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Evals.Datasets;
using SemanticPolicy.Evals.Recordings;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Evals.Tests;

// One dataset row of a CLI fixture: the label, the split or none, and what each provider answered by name.
internal sealed record FixtureRow(string Label, string? Split, IReadOnlyDictionary<string, ProviderResult> Attempts)
{
    public static FixtureRow Of(string label, string? split, params (string Provider, ProviderResult Result)[] attempts) =>
        new(label, split, attempts.ToDictionary(attempt => attempt.Provider, attempt => attempt.Result, StringComparer.Ordinal));
}

internal sealed record CliRun(int ExitCode, string Output, string Error);

// A policy file, a dataset and a recording of it in a directory of their own, so a verb can be run end to end
// without a provider: the results are written inline, the dataset is placeholder text, and the recording's
// digest is the dataset file's own. Rows are r00, r01, ... in the order given.
internal sealed class CliFixture : IDisposable
{
    private readonly string _directory;

    private CliFixture(string directory) => _directory = directory;

    public string PolicyPath => Path.Combine(_directory, "policy.json");

    public string DatasetPath => Path.Combine(_directory, "rows.jsonl");

    public string RecordingPath => Path.Combine(_directory, "rows.recording.jsonl");

    public string OutPath => Path.Combine(_directory, "result.json");

    public static async Task<CliFixture> CreateAsync(Policy policy, IReadOnlyList<FixtureRow> rows)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"semanticpolicy-evals-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        CliFixture fixture = new(directory);
        await File.WriteAllTextAsync(
            fixture.PolicyPath,
            JsonSerializer.Serialize(policy, SemanticPolicyJson.Options),
            TestContext.Current.CancellationToken);

        StringBuilder dataset = new();
        for (int index = 0; index < rows.Count; index++)
        {
            JsonObject row = new()
            {
                ["id"] = Id(index),
                ["input"] = "context",
                ["label"] = rows[index].Label is "true" or "false"
                    ? JsonValue.Create(rows[index].Label == "true")
                    : JsonValue.Create(rows[index].Label),
            };
            if (rows[index].Split is { } split)
            {
                row["metadata"] = new JsonObject { ["split"] = split };
            }

            dataset.Append(row.ToJsonString()).Append('\n');
        }

        await File.WriteAllTextAsync(fixture.DatasetPath, dataset.ToString(), TestContext.Current.CancellationToken);
        string ruleId = policy.Rules[0].Id;
        await Samples.RecordAsync(
            fixture.RecordingPath,
            Samples.Header(policy, DatasetReader.Read(fixture.DatasetPath).Sha256),
            [
                .. rows.Select((row, index) => Samples.Recorded(
                    Id(index),
                    [.. row.Attempts.Select(attempt => (ruleId, attempt.Key, attempt.Value))])),
            ]);
        return fixture;
    }

    public static string Id(int index) => $"r{index:00}";

    // The verb with the fixture's policy, dataset, recording and --out, then whatever the test adds.
    public Task<CliRun> RunAsync(string verb, params string[] options) =>
        InvokeAsync(
        [
            verb,
            "--policy", PolicyPath,
            "--dataset", DatasetPath,
            "--recording", RecordingPath,
            "--out", OutPath,
            .. options,
        ]);

    public JsonElement ReadOut()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(OutPath));
        return document.RootElement.Clone();
    }

    public static async Task<CliRun> InvokeAsync(string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        RootCommand root = EvalsCli.Build(new CliIo(output, error));
        InvocationConfiguration configuration = new()
        {
            Output = output,
            Error = error,
            EnableDefaultExceptionHandler = false,
        };
        int exitCode = await root.Parse(args).InvokeAsync(configuration, TestContext.Current.CancellationToken);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file the OS still holds is left for the temp directory's own cleanup.
        }
    }
}
