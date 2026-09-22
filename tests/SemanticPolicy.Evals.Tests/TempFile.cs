namespace SemanticPolicy.Evals.Tests;

// A file that exists for one test: the content is written inline at the test and the file goes with it.
internal sealed class TempFile : IDisposable
{
    private TempFile(string path) => Path = path;

    public string Path { get; }

    public static TempFile Write(string content, string extension = ".jsonl")
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"semanticpolicy-evals-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return new TempFile(path);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // A file the OS still holds is left for the temp directory's own cleanup.
        }
    }
}
