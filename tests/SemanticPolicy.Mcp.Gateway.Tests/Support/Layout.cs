namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// The four files a gateway reads, written side by side under the names the valid gateway file gives them. A null
// gateway file is left unwritten.
internal sealed record Layout(string? Gateway, string Providers, string ResultsPolicy, string DefinitionsPolicy)
{
    public static Layout Valid { get; } = new(
        Samples.Gateway(Samples.Results, Samples.Definitions),
        Samples.ProvidersFile($$"""{ "von": {{Samples.Von}} }"""),
        Samples.Policy("results-guard"),
        Samples.Policy("definitions-guard"));

    // Returns the gateway file's path.
    public string WriteTo(Workspace workspace)
    {
        workspace.Write("providers.json", Providers);
        workspace.Write("results.policy.json", ResultsPolicy);
        workspace.Write("definitions.policy.json", DefinitionsPolicy);
        return Gateway is null ? workspace.PathOf("gateway.json") : workspace.Write("gateway.json", Gateway);
    }
}
