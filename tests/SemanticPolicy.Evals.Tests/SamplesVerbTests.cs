namespace SemanticPolicy.Evals.Tests;

public sealed class SamplesVerbTests : IDisposable
{
    private static readonly string _datasets =
        Path.Combine(ExampleDatasetsTests.RepositoryRoot(), "tools", "SemanticPolicy.Evals", "datasets");

    // A directory of this test's own that does not exist yet: samples creates it, and Dispose removes it.
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"semanticpolicy-evals-{Guid.NewGuid():N}");

    [Fact]
    public async Task Samples_Writes_Every_Shipped_Dataset_File_Byte_For_Byte()
    {
        CliRun run = await CliFixture.InvokeAsync(["samples", _directory]);

        run.ExitCode.Should().Be(ExitCodes.Success);
        string[] shipped = ShippedFiles();
        Files(_directory).Should().BeEquivalentTo(shipped);
        foreach (string file in shipped)
        {
            File.ReadAllBytes(Path.Combine(_directory, file)).AsSpan()
                .SequenceEqual(File.ReadAllBytes(Path.Combine(_datasets, file)))
                .Should().BeTrue("'{0}' is written with the bytes the repository holds", file);
            run.Output.Should().Contain(Path.Combine(_directory, file));
        }
    }

    // The first and the last file in ordinal order of their paths. A samples that checks each file only just before
    // writing it passes the case of whichever file it writes first, and no order writes both first.
    [Theory]
    [InlineData("examples/agent-router.jsonl")]
    [InlineData("smoke/support-router.smoke.jsonl")]
    public async Task Samples_Into_A_Directory_Holding_One_Of_Its_Files_Exits_1_And_Writes_Nothing(string shipped)
    {
        string relative = shipped.Replace('/', Path.DirectorySeparatorChar);
        string existing = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "not a shipped file\n", TestContext.Current.CancellationToken);

        CliRun run = await CliFixture.InvokeAsync(["samples", _directory]);

        run.ExitCode.Should().Be(ExitCodes.UsageOrData);
        run.Error.Should().Contain(existing);
        (await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken)).Should().Be("not a shipped file\n");
        Files(_directory).Should().Equal(relative);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Never created, or a file the OS still holds, which is left for the temp directory's own cleanup.
        }
    }

    // By extension: on a case-insensitive file system the datasets folder is also the one that holds the dataset
    // reader's source files.
    private static string[] ShippedFiles() =>
        [.. Files(_datasets).Where(file => Path.GetExtension(file) is ".json" or ".jsonl")];

    private static string[] Files(string directory) =>
        [.. Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(directory, file))];
}
