namespace SemanticPolicy.Evals.Format.Tests;

// Datasets and recordings written before the formats moved into this package, read through it unchanged: v0 data
// stays readable for as long as the format is v0.
public sealed class CommittedDataTests
{
    [Theory]
    [InlineData("tools/SemanticPolicy.Evals/datasets/examples/agent-router.jsonl", null)]
    [InlineData("tools/SemanticPolicy.Evals/datasets/examples/harm-severity.jsonl", null)]
    [InlineData("tools/SemanticPolicy.Evals/datasets/examples/prompt-injection.jsonl", null)]
    [InlineData("tools/SemanticPolicy.Evals/datasets/examples/support-ticket.jsonl", null)]
    [InlineData("tools/SemanticPolicy.Evals/datasets/smoke/prompt-injection.smoke.jsonl", null)]
    [InlineData("tools/SemanticPolicy.Evals/datasets/smoke/support-router.smoke.jsonl", null)]
    [InlineData("tools/SemanticPolicy.Mcp.Gateway/samples/definitions.jsonl", null)]
    [InlineData("tools/SemanticPolicy.Mcp.Gateway/samples/results.jsonl", null)]
    [InlineData("tools/SemanticPolicy.Evals/datasets/smoke/prompt-injection.recording.jsonl", 100)]
    [InlineData("tools/SemanticPolicy.Evals/datasets/smoke/support-router.recording.jsonl", 80)]
    [InlineData("tools/SemanticPolicy.Mcp.Gateway/samples/definitions.recording.jsonl", 100)]
    [InlineData("tools/SemanticPolicy.Mcp.Gateway/samples/results.recording.jsonl", 100)]
    public void Committed_Datasets_And_Recordings_Read_Through_The_Package(string file, int? recordedRows)
    {
        string path = Path.Combine([RepositoryRoot(), .. file.Split('/')]);

        if (recordedRows is { } rows)
        {
            Recording recording = RecordingReader.Read(path);
            recording.Header.Format.Should().Be(RecordingHeader.FormatV0);
            recording.Rows.Should().HaveCount(rows);
            recording.TornLine.Should().BeNull();
        }
        else
        {
            Dataset dataset = DatasetReader.Read(path);
            dataset.Rows.Should().HaveCount(File.ReadAllLines(path).Count(line => !string.IsNullOrWhiteSpace(line)));
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SemanticPolicy.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("No SemanticPolicy.slnx above the test output directory.");
    }
}
