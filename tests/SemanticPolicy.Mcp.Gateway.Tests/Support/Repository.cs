namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// The checkout the tests run from, found by walking up from the test output to the solution file, for the files the
// repository ships beside the gateway: its samples and its README.
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);

    public static string Sample(string name) => PathOf("tools", "SemanticPolicy.Mcp.Gateway", "samples", name);

    private static string FindRoot()
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
