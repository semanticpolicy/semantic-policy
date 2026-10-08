namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// A directory of real files under the system's temporary directory, never the working directory, so a path that
// resolved against the working directory would not find them.
internal sealed class Workspace : IDisposable
{
    private Workspace(DirectoryInfo root) => Root = root.FullName;

    public string Root { get; }

    public static Workspace Create() => new(Directory.CreateTempSubdirectory("semanticpolicy-gateway-"));

    // Returns the absolute path written, creating the folders on the way.
    public string Write(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string PathOf(string relativePath) => Path.Combine(Root, relativePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A file the OS still holds is left for the temp directory's own cleanup.
        }
    }
}
