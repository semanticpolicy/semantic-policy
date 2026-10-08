using SemanticPolicy.Mcp.Gateway.Tests.Support;
using static SemanticPolicy.Mcp.Gateway.Tests.Support.Samples;

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed class ProvidersCompositionTests
{
    // The Jev adapter reads its key only when a bound provider is built, so this proves the composition builds what
    // the policies bind before anything is served.
    [Fact]
    public async Task Unset_Key_Variable_Stops_The_Gateway_And_Names_The_Variable()
    {
        string unset = UnsetVariable();
        using Workspace workspace = Workspace.Create();
        string gateway = (Layout.Valid with
        {
            Providers = ProvidersFile($$"""{ "von": {{Von}}, "jev": { "kind": "typesafe-jev", "options": { "route": {{JevRoute(unset)}} } } }"""),
            DefinitionsPolicy = Policy("definitions-guard", bindings: Binding("jev", "probability")),
        }).WriteTo(workspace);

        GatewayRun run = await GatewayRun.InvokeAsync("--gateway", gateway, "--", "upstream-server");

        run.ExitCode.Should().Be(ExitCodes.Configuration);
        run.Error.TrimEnd().Should().Be(
            $"Providers file '{workspace.PathOf("providers.json")}': provider 'jev': the environment variable {unset} is not set.");
        run.Served.Should().BeFalse();
    }

    // Every entry is left unbound, so the list comes from the file and no adapter is built.
    [Theory]
    [InlineData("a top-level apiKeyVariable")]
    [InlineData("a Jev route's apiKeyVariable")]
    [InlineData("a Jev entry naming both")]
    [InlineData("an http entry with no key")]
    public void Composition_Lists_Every_Key_Variable_The_Providers_File_Names(string entry)
    {
        string top = UnsetVariable();
        string route = UnsetVariable();
        (string Entries, string[] Expected) named = entry switch
        {
            "a top-level apiKeyVariable" => (
                $$"""{ "von": { "kind": "systemone", "options": { "baseUrl": "http://127.0.0.1:8000", "model": "von-1.2.2", "apiKeyVariable": "{{top}}" } } }""",
                [top]),
            "a Jev route's apiKeyVariable" => (
                $$"""{ "jev": { "kind": "typesafe-jev", "options": { "route": {{JevRoute(route)}} } } }""",
                [route]),
            "a Jev entry naming both" => (
                $$"""{ "jev": { "kind": "typesafe-jev", "options": { "route": {{JevRoute(route)}}, "apiKeyVariable": "{{top}}" } } }""",
                [top, route]),
            "an http entry with no key" => ($$"""{ "v0": {{V0}} }""", []),
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        };
        using Workspace workspace = Workspace.Create();
        workspace.Write("providers.json", ProvidersFile(named.Entries));
        string gateway = workspace.Write("gateway.json", Object(ProvidersPath));

        GatewayComposition composition = GatewayComposition.Compose(gateway);

        composition.KeyVariables.Should().BeEquivalentTo(named.Expected);
    }

    // The shapes the evaluation tool's own tests accept, in one file written with the byte-order mark Windows
    // PowerShell 5.1 writes. The Jev entry stays unbound, because binding it would need its key in the process.
    [Fact]
    public void Shared_Reader_Accepts_What_The_Evaluation_Tool_Accepts()
    {
        string key = UnsetVariable();
        string providers = ProvidersFile($$"""
            {
              "von": { "kind": "systemone", "options": { "baseUrl": "https://von.example", "path": "/v2/decide", "model": "von-1.2.2", "apiKeyVariable": "{{key}}" } },
              "jev": { "kind": "typesafe-jev", "options": { "route": {{JevRoute(key)}}, "model": "typesafe/jev-override" } },
              "v0": { "kind": "http", "options": { "baseUrl": "https://decide.example", "model": "stub-decider-1", "types": ["boolean"], "evidence": ["score"], "structuredContext": false, "apiKeyVariable": "{{key}}" } }
            }
            """);
        using Workspace workspace = Workspace.Create();
        string gateway = (Layout.Valid with
        {
            Providers = "﻿" + providers,
            ResultsPolicy = Policy("results-guard", bindings: Binding("von") + ", " + Binding("v0")),
        }).WriteTo(workspace);

        GatewayComposition composition = GatewayComposition.Compose(gateway);

        composition.Results!.Policy.Bindings.Select(binding => binding.ProviderId).Should().Equal("von", "v0");
        composition.KeyVariables.Should().Equal(key);
    }
}
