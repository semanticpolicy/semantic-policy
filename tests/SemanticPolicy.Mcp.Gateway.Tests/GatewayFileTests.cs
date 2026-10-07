using SemanticPolicy.Mcp.Gateway.Tests.Support;
using static SemanticPolicy.Mcp.Gateway.Tests.Support.Samples;

namespace SemanticPolicy.Mcp.Gateway.Tests;

public sealed class GatewayFileTests
{
    // {gateway} is the gateway file as given, {providers} the providers file, {dir} the directory the gateway file
    // sits in and {unset} a key variable nobody sets.
    [Theory]
    [InlineData("a missing deny", "Gateway file '{gateway}': results.deny is missing")]
    [InlineData("an allow key", "Gateway file '{gateway}': results.allow is refused")]
    [InlineData("an unknown action", "Gateway file '{gateway}': results.warn.action is not an action the results point takes")]
    [InlineData("hide on results", "Gateway file '{gateway}': results.warn.action is not an action the results point takes")]
    [InlineData("ask on results", "Gateway file '{gateway}': results.escalate.action is not an action the results point takes")]
    [InlineData("annotate on definitions", "Gateway file '{gateway}': definitions.warn.action is not an action the definitions point takes")]
    [InlineData("withhold on definitions", "Gateway file '{gateway}': definitions.escalate.action is not an action the definitions point takes")]
    [InlineData("ask on definitions", "Gateway file '{gateway}': definitions.deny.action is not an action the definitions point takes")]
    [InlineData("annotate without a message", "Gateway file '{gateway}': results.warn.message is missing")]
    [InlineData("withhold without a message", "Gateway file '{gateway}': results.deny.message is missing")]
    [InlineData("hide without a message", "Gateway file '{gateway}': definitions.deny.message is missing")]
    [InlineData("pass with a message", "Gateway file '{gateway}': results.abstain.message is refused")]
    [InlineData("an unknown top-level property", "Gateway file '{gateway}': top-level property 'version' is not known")]
    [InlineData("an unknown point property", "Gateway file '{gateway}': results.mode is not a property of a point")]
    [InlineData("an unknown entry property", "Gateway file '{gateway}': results.warn.text is not a property of an action")]
    [InlineData("a repeated top-level property", "Gateway file '{gateway}': 'providers' is given twice")]
    [InlineData("a repeated mapping key", "Gateway file '{gateway}': results.warn is given twice")]
    [InlineData("a repeated entry property", "Gateway file '{gateway}': results.warn.message is given twice")]
    [InlineData("a gateway file that is not JSON", "Gateway file '{gateway}' is not valid JSON at line 2, byte ")]
    [InlineData("a gateway file that cannot be read", "Gateway file '{gateway}' cannot be read")]
    [InlineData("a point without a policy", "Gateway file '{gateway}': results.policy is missing")]
    [InlineData("a policy file that cannot be read", "Gateway file '{gateway}': results.policy '{dir}missing.policy.json' cannot be read")]
    [InlineData("a policy file that is not JSON", "Gateway file '{gateway}': results.policy '{dir}results.policy.json' is not Core's policy JSON at line 2, byte ")]
    [InlineData("a policy file that is not Core's JSON", "Gateway file '{gateway}': results.policy '{dir}results.policy.json' is not Core's policy JSON at line ")]
    [InlineData("a policy that fails validation", "Gateway file '{gateway}': results.policy '{dir}results.policy.json': Policy 'results-guard': the policy has no bindings.")]
    [InlineData("a point but no providers", "Gateway file '{gateway}': 'providers' is missing")]
    [InlineData("a providers file that cannot be read", "Providers file '{dir}absent.json' cannot be read")]
    [InlineData("a providers file the reader refuses", "Providers file '{providers}': provider 'von': options.baseUrll is not a property of SystemOneOptions.")]
    [InlineData("a policy bound to a provider the file does not register", "Gateway file '{gateway}': results.policy '{dir}results.policy.json': policy 'results-guard' binds provider 'absent', which providers file '{providers}' does not register")]
    [InlineData("an adapter that cannot be built", "Providers file '{providers}': provider 'jev': the environment variable {unset} is not set.")]
    [InlineData("a policy its provider cannot answer", "Gateway file '{gateway}': results.policy '{dir}results.policy.json': Policy 'results-guard', provider 'v0'")]
    public async Task Gateway_File_With_A_Problem_Never_Starts_And_Names_It(string defect, string expected)
    {
        string unset = UnsetVariable();
        using Workspace workspace = Workspace.Create();
        string gateway = Broken(defect, unset).WriteTo(workspace);

        GatewayRun run = await GatewayRun.InvokeAsync("--gateway", gateway, "--", "upstream-server");

        run.ExitCode.Should().Be(ExitCodes.Configuration, run.Error);
        run.Error.Should().Contain(expected
            .Replace("{gateway}", gateway, StringComparison.Ordinal)
            .Replace("{providers}", workspace.PathOf("providers.json"), StringComparison.Ordinal)
            .Replace("{dir}", workspace.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            .Replace("{unset}", unset, StringComparison.Ordinal));
        run.Error.TrimEnd().Should().NotContain("\n", "a refusal is one message");
        run.Error.Should().NotContain(Canary);
        run.Output.Should().BeEmpty();
        run.Served.Should().BeFalse();
    }

    // The working directory is the test host's, and the files are under the system's temporary directory.
    [Fact]
    public void Relative_Paths_In_The_Gateway_File_Resolve_Against_Its_Own_Directory()
    {
        using Workspace workspace = Workspace.Create();
        workspace.Write("config/providers/providers.json", Layout.Valid.Providers);
        workspace.Write("config/policies/results.policy.json", Policy("results-guard"));
        workspace.Write("shared/definitions.policy.json", Policy("definitions-guard"));
        string gateway = workspace.Write(
            "config/gateway.json",
            Object(
                "\"providers\": \"providers/providers.json\"",
                "\"results\": " + Object("\"policy\": \"policies/results.policy.json\"", ResultsWarn, ResultsEscalate, ResultsDeny, ResultsAbstain),
                "\"definitions\": " + Object("\"policy\": \"../shared/definitions.policy.json\"", DefinitionsWarn, DefinitionsEscalate, DefinitionsDeny, DefinitionsAbstain)));
        Directory.GetCurrentDirectory().Should().NotStartWith(workspace.Root);

        GatewayComposition composition = GatewayComposition.Compose(gateway);

        composition.Results!.Policy.Id.Should().Be("results-guard");
        composition.Definitions!.Policy.Id.Should().Be("definitions-guard");
    }

    [Fact]
    public void Valid_Gateway_File_Composes_An_Evaluator_And_Both_Points()
    {
        using Workspace workspace = Workspace.Create();
        string gateway = Layout.Valid.WriteTo(workspace);

        GatewayComposition composition = GatewayComposition.Compose(gateway);

        composition.Results!.Policy.Id.Should().Be("results-guard");
        composition.Results.Mapping.Should().Be(new VerdictMapping(
            Warn: new MappedAction(GatewayAction.Annotate, "message-a"),
            Escalate: new MappedAction(GatewayAction.Withhold, "message-b"),
            Deny: new MappedAction(GatewayAction.Withhold, "message-c"),
            Abstain: new MappedAction(GatewayAction.Pass, null)));
        composition.Definitions!.Policy.Id.Should().Be("definitions-guard");
        composition.Definitions.Mapping.Should().Be(new VerdictMapping(
            Warn: new MappedAction(GatewayAction.Pass, null),
            Escalate: new MappedAction(GatewayAction.Hide, "message-d"),
            Deny: new MappedAction(GatewayAction.Hide, "message-e"),
            Abstain: new MappedAction(GatewayAction.Pass, null)));
        composition.Evaluator.Should().NotBeNull();
    }

    [Fact]
    public void Gateway_File_With_No_Point_Composes_And_Screens_Nothing()
    {
        using Workspace workspace = Workspace.Create();
        string gateway = workspace.Write("gateway.json", "{ }");

        GatewayComposition composition = GatewayComposition.Compose(gateway);

        composition.Results.Should().BeNull();
        composition.Definitions.Should().BeNull();
        composition.KeyVariables.Should().BeEmpty();
        composition.Evaluator.Should().NotBeNull();
    }

    [Fact]
    public async Task Providers_File_Timeout_Is_Refused_With_The_Gateways_Own_Reason()
    {
        using Workspace workspace = Workspace.Create();
        string gateway = (Layout.Valid with
        {
            Providers = ProvidersFile($$"""{ "von": { "kind": "systemone", "options": { "baseUrl": "http://127.0.0.1:8000", "model": "von-1.2.2", "timeout": "{{Canary}}" } } }"""),
        }).WriteTo(workspace);

        GatewayRun run = await GatewayRun.InvokeAsync("--gateway", gateway, "--", "upstream-server");

        run.ExitCode.Should().Be(ExitCodes.Configuration);
        run.Error.Should().Contain($"Providers file '{workspace.PathOf("providers.json")}': provider 'von': options.timeout is refused: ")
            .And.Contain("budget")
            .And.NotContain("run --timeout")
            .And.NotContain(Canary);
        run.Served.Should().BeFalse();
    }

    private static string Entry(string key, string action, string? message = null) =>
        message is null
            ? $$"""
                "{{key}}": { "action": "{{action}}" }
                """
            : $$"""
                "{{key}}": { "action": "{{action}}", "message": "{{message}}" }
                """;

    // The valid layout with one defect, and the canary planted in the value the defect breaks or, where the defect
    // is a missing value, in a value beside it.
    private static Layout Broken(string defect, string unset)
    {
        Layout valid = Layout.Valid;
        string canaryWarn = Entry("warn", "annotate", Canary);
        string results = "\"results\": " + Results;
        string definitions = "\"definitions\": " + Definitions;
        return defect switch
        {
            "a missing deny" => Gateway(Object(ResultsPolicyPath, canaryWarn, ResultsEscalate, ResultsAbstain)),
            "an allow key" => Gateway(Object(ResultsPolicyPath, Entry("allow", "annotate", Canary), ResultsWarn, ResultsEscalate, ResultsDeny, ResultsAbstain)),
            "an unknown action" => Gateway(Object(ResultsPolicyPath, Entry("warn", Canary, "message-a"), ResultsEscalate, ResultsDeny, ResultsAbstain)),
            "hide on results" => Gateway(Object(ResultsPolicyPath, Entry("warn", "hide", Canary), ResultsEscalate, ResultsDeny, ResultsAbstain)),
            "ask on results" => Gateway(Object(ResultsPolicyPath, ResultsWarn, Entry("escalate", "ask", Canary), ResultsDeny, ResultsAbstain)),
            "annotate on definitions" => Gateway(Results, Object(DefinitionsPolicyPath, Entry("warn", "annotate", Canary), DefinitionsEscalate, DefinitionsDeny, DefinitionsAbstain)),
            "withhold on definitions" => Gateway(Results, Object(DefinitionsPolicyPath, DefinitionsWarn, Entry("escalate", "withhold", Canary), DefinitionsDeny, DefinitionsAbstain)),
            "ask on definitions" => Gateway(Results, Object(DefinitionsPolicyPath, DefinitionsWarn, DefinitionsEscalate, Entry("deny", "ask", Canary), DefinitionsAbstain)),
            "annotate without a message" => Gateway(Object(ResultsPolicyPath, Entry("warn", "annotate"), ResultsEscalate, Entry("deny", "withhold", Canary), ResultsAbstain)),
            "withhold without a message" => Gateway(Object(ResultsPolicyPath, canaryWarn, ResultsEscalate, Entry("deny", "withhold"), ResultsAbstain)),
            "hide without a message" => Gateway(Results, Object(DefinitionsPolicyPath, DefinitionsWarn, Entry("escalate", "hide", Canary), Entry("deny", "hide"), DefinitionsAbstain)),
            "pass with a message" => Gateway(Object(ResultsPolicyPath, ResultsWarn, ResultsEscalate, ResultsDeny, Entry("abstain", "pass", Canary))),
            "an unknown top-level property" => valid with { Gateway = Object(ProvidersPath, results, definitions, $"\"version\": \"{Canary}\"") },
            "an unknown point property" => Gateway(Object(ResultsPolicyPath, $"\"mode\": \"{Canary}\"", ResultsWarn, ResultsEscalate, ResultsDeny, ResultsAbstain)),
            "an unknown entry property" => Gateway(Object(ResultsPolicyPath, $"\"warn\": {{ \"action\": \"annotate\", \"message\": \"message-a\", \"text\": \"{Canary}\" }}", ResultsEscalate, ResultsDeny, ResultsAbstain)),
            "a repeated top-level property" => valid with { Gateway = Object(ProvidersPath, $"\"providers\": \"{Canary}\"", results, definitions) },
            "a repeated mapping key" => Gateway(Object(ResultsPolicyPath, ResultsWarn, Entry("warn", "withhold", Canary), ResultsEscalate, ResultsDeny, ResultsAbstain)),
            "a repeated entry property" => Gateway(Object(ResultsPolicyPath, $"\"warn\": {{ \"action\": \"annotate\", \"message\": \"message-a\", \"message\": \"{Canary}\" }}", ResultsEscalate, ResultsDeny, ResultsAbstain)),
            "a gateway file that is not JSON" => valid with { Gateway = $"{{ \"providers\": \"providers.json\",\n  \"results\": {{ \"policy\": \"{Canary}" },
            "a gateway file that cannot be read" => valid with { Gateway = null },
            "a point without a policy" => Gateway(Object(canaryWarn, ResultsEscalate, ResultsDeny, ResultsAbstain)),
            "a policy file that cannot be read" => Gateway(Object("\"policy\": \"missing.policy.json\"", canaryWarn, ResultsEscalate, ResultsDeny, ResultsAbstain)),
            "a policy file that is not JSON" => valid with { ResultsPolicy = $"{{ \"id\": \"results-guard\",\n  \"rules\": [ {{ \"question\": \"{Canary}" },
            "a policy file that is not Core's JSON" => valid with { ResultsPolicy = Policy("results-guard").Replace("\"mode\": \"shadow\"", $"\"mode\": \"{Canary}\"", StringComparison.Ordinal) },
            "a policy that fails validation" => valid with { ResultsPolicy = Policy("results-guard", question: Canary, bindings: "") },
            "a point but no providers" => valid with { Gateway = Object("\"results\": " + Object(ResultsPolicyPath, canaryWarn, ResultsEscalate, ResultsDeny, ResultsAbstain), definitions) },
            "a providers file that cannot be read" => valid with { Gateway = Object("\"providers\": \"absent.json\"", "\"results\": " + Object(ResultsPolicyPath, canaryWarn, ResultsEscalate, ResultsDeny, ResultsAbstain), definitions) },
            "a providers file the reader refuses" => valid with { Providers = ProvidersFile($$"""{ "von": { "kind": "systemone", "options": { "baseUrll": "{{Canary}}", "model": "von-1.2.2" } } }""") },
            "a policy bound to a provider the file does not register" => valid with { ResultsPolicy = Policy("results-guard", question: Canary, bindings: Binding("absent")) },
            "an adapter that cannot be built" => valid with
            {
                Providers = ProvidersFile($$"""{ "von": {{Von}}, "jev": { "kind": "typesafe-jev", "options": { "route": {{JevRoute(unset, model: Canary)}} } } }"""),
                ResultsPolicy = Policy("results-guard", bindings: Binding("jev", "probability")),
            },
            "a policy its provider cannot answer" => valid with
            {
                Providers = ProvidersFile($$"""{ "von": {{Von}}, "v0": {{V0}} }"""),
                ResultsPolicy = Policy("results-guard", question: Canary, bindings: Binding("v0", "probability")),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Layout Gateway(string resultsPoint, string? definitionsPoint = null) =>
            valid with { Gateway = Samples.Gateway(resultsPoint, definitionsPoint ?? Definitions) };
    }
}
