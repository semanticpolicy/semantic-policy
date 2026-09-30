using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Evals.Cli;
using SemanticPolicy.Evals.Tests.Support;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Evals.Tests;

public sealed class ProvidersFileTests
{
    // Written into every value the tool refuses; no message may carry it.
    private const string _marker = "VALUE-MARKER-7c41";

    private const string _von = """{ "kind": "systemone", "options": { "baseUrl": "http://127.0.0.1:8000", "model": "von-1.2.2" } }""";

    [Fact]
    public async Task Run_With_A_Providers_File_Knows_Only_Its_Providers()
    {
        // The hook the tool is built with registers local and jev; with a file, the file's entries are all there is.
        using Files files = Files.Create(Holding($$"""{ "von": {{_von}} }"""), bound: "local");
        ScriptedProvider scripted = new();

        CliRun run = await InvokeAsync(
            files.RunArgs(),
            builder => builder.AddProvider(scripted, "local").AddProvider(scripted, "jev"));

        run.ExitCode.Should().Be(ExitCodes.UsageOrData);
        run.Error.TrimEnd().Should().Be("Policy 'guard' binds provider 'local', which is not registered; registered providers: von.");
        scripted.Calls.Should().Be(0);
        File.Exists(files.RecordingPath).Should().BeFalse();

        // An unbound Jev entry is registered but not built, so its unset key stops nothing. Resolved without a run,
        // because a run would call the System One server.
        using TempFile both = TempFile.Write(
            Holding($$"""{ "von": {{_von}}, "jev": { "kind": "typesafe-jev", "options": { "route": {{OpenRouterRoute("", TestVariable())}} } } }"""),
            ".json");
        IReadOnlyDictionary<string, IDecisionProvider> resolved = Cli.Providers.Resolve(
            ProvidersFile.Read(both.Path),
            Samples.Guard(FailureBehavior.Fallback(Verdict.Escalate), ["von"], [Samples.Flagged()]));

        resolved.Keys.Should().Equal("von");
    }

    // What the tool owns is refused wherever the entry is bound, so the policy binds a name the file lacks: an entry
    // that got through would end the run as an unregistered binding, never as a call.
    [Theory]
    [InlineData("systemone", "", "apiKey")]
    [InlineData("systemone", "", "ApiKey")]
    [InlineData("systemone", "", "APIKEY")]
    [InlineData("systemone", "", "timeout")]
    [InlineData("systemone", "", "id")]
    [InlineData("typesafe-jev", "", "apiKey")]
    [InlineData("typesafe-jev", ".route", "apiKey")]
    [InlineData("typesafe-jev", ".route", "Timeout")]
    public async Task Providers_File_Refuses_What_The_Tool_Owns_Naming_Only_The_Property(string kind, string within, string property)
    {
        string owned = $"\"{property}\": \"{_marker}\"";
        string options = kind == "systemone"
            ? $$"""{ "baseUrl": "http://127.0.0.1:8000", "model": "von-1.2.2", {{owned}} }"""
            : within == ""
                ? $$"""{ "route": {{OpenRouterRoute("")}}, {{owned}} }"""
                : $$"""{ "route": {{OpenRouterRoute(owned + ",")}} }""";
        using Files files = Files.Create($$"""{ "providers": { "entry": { "kind": "{{kind}}", "options": {{options}} } } }""", bound: "absent");

        CliRun run = await InvokeAsync(files.RunArgs());

        run.ExitCode.Should().Be(ExitCodes.UsageOrData);
        run.Error.Should().Contain($"Providers file '{files.ProvidersPath}': provider 'entry': options{within}.{property} ");
        (run.Output + run.Error).Should().NotContain(_marker);
        File.Exists(files.RecordingPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("an option the class lacks", "options.baseUrll is not a property of SystemOneOptions.")]
    [InlineData("a route property the route lacks", "options.route.region is not a property of TypeSafeJevRoute.")]
    [InlineData("an option given twice", "options.MODEL is given twice.")]
    [InlineData("a value of the wrong type", "options.maxContextLength holds a value")]
    [InlineData("entry property kinds", "provider 'von': 'kinds' is not an entry property")]
    [InlineData("no kind", "provider 'von': the entry has no 'kind'")]
    [InlineData("no options", "provider 'von': the entry has no 'options'")]
    [InlineData("no providers", "has no top-level 'providers' object.")]
    [InlineData("a second top-level property", "top-level property 'version' is not known")]
    [InlineData("an unknown kind", "provider 'von': 'kind' names a kind that is not known; known kinds: systemone, typesafe-jev.")]
    [InlineData("a name given twice", "provider 'von' is given twice.")]
    [InlineData("truncated JSON", "is not valid JSON at line 2, byte ")]
    [InlineData("no such file", "cannot be read")]
    [InlineData("a Jev route naming no key variable", "provider 'jev': options.route.apiKeyVariable is missing")]
    public async Task Providers_File_Refuses_A_Shape_It_Does_Not_Know(string defect, string expected)
    {
        string file = defect switch
        {
            "an option the class lacks" => Holding($$"""{ "von": { "kind": "systemone", "options": { "baseUrll": "{{_marker}}", "model": "von-1.2.2" } } }"""),
            "a route property the route lacks" => Holding($$"""{ "jev": { "kind": "typesafe-jev", "options": { "route": {{OpenRouterRoute($"\"region\": \"{_marker}\",")}} } } }"""),
            "an option given twice" => Holding($$"""{ "von": { "kind": "systemone", "options": { "baseUrl": "http://127.0.0.1:8000", "model": "{{_marker}}", "MODEL": "von-1.2.2" } } }"""),
            "a value of the wrong type" => Holding($$"""{ "von": { "kind": "systemone", "options": { "baseUrl": "http://127.0.0.1:8000", "model": "von-1.2.2", "maxContextLength": "{{_marker}}" } } }"""),
            "entry property kinds" => Holding($$"""{ "von": { "kinds": "{{_marker}}", "kind": "systemone", "options": { "model": "von-1.2.2" } } }"""),
            "no kind" => Holding("""{ "von": { "options": { "model": "von-1.2.2" } } }"""),
            "no options" => Holding("""{ "von": { "kind": "systemone" } }"""),
            "no providers" => "{ }",
            "a second top-level property" => $$"""{ "providers": { "von": {{_von}} }, "version": "{{_marker}}" }""",
            "an unknown kind" => Holding($$"""{ "von": { "kind": "{{_marker}}", "options": { "model": "von-1.2.2" } } }"""),
            "a name given twice" => Holding($$"""{ "von": {{_von}}, "von": {{_von}} }"""),
            "truncated JSON" => $$"""{ "providers":{{"\n"}}{ "von": { "kind": "systemone", "options": { "model": "{{_marker}}""",
            "no such file" => Holding($$"""{ "von": {{_von}} }"""),
            "a Jev route naming no key variable" => Holding($$"""{ "jev": { "kind": "typesafe-jev", "options": { "route": { "baseUrl": "https://openrouter.ai/api", "path": "/v1/systemone", "model": "{{_marker}}" } } } }"""),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        using Files files = Files.Create(file, bound: "absent");
        if (defect == "no such file")
        {
            File.Delete(files.ProvidersPath);
        }

        CliRun run = await InvokeAsync(files.RunArgs());

        run.ExitCode.Should().Be(ExitCodes.UsageOrData);
        run.Error.Should().StartWith($"Providers file '{files.ProvidersPath}'").And.Contain(expected);
        (run.Output + run.Error).Should().NotContain(_marker);
        File.Exists(files.RecordingPath).Should().BeFalse();
    }

    // Every entry is registered, bound or not: the first three are left unbound, so a registration skipped for them
    // would end the run as an unregistered binding instead. The last is bound, because only a bound Jev provider reads
    // its key.
    [Theory]
    [InlineData("a relative baseUrl", "provider 'von': The BaseUrl is not an absolute URI.")]
    [InlineData("plain http to a remote host", "provider 'von': The BaseUrl must use https")]
    [InlineData("a Jev entry with no route", "provider 'jev': The route is absent.")]
    [InlineData("a bound Jev entry whose key variable is unset", "provider 'jev': the environment variable ")]
    public async Task Providers_File_Adapter_Failure_Is_A_Usage_Error_Naming_The_Provider(string defect, string expected)
    {
        string unset = TestVariable();
        (string providers, string bound) = defect switch
        {
            "a relative baseUrl" => ("""{ "von": { "kind": "systemone", "options": { "baseUrl": "/v1", "model": "von-1.2.2" } } }""", "absent"),
            "plain http to a remote host" => ("""{ "von": { "kind": "systemone", "options": { "baseUrl": "http://10.0.0.5", "model": "von-1.2.2" } } }""", "absent"),
            "a Jev entry with no route" => ("""{ "jev": { "kind": "typesafe-jev", "options": { "model": "typesafe/jev-1.13", "apiKeyVariable": "SEMANTICPOLICY_TEST_UNSET_KEY" } } }""", "absent"),
            "a bound Jev entry whose key variable is unset" => ($$"""{ "jev": { "kind": "typesafe-jev", "options": { "route": {{OpenRouterRoute("", unset)}} } } }""", "jev"),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        using Files files = Files.Create(Holding(providers), bound);

        CliRun run = await InvokeAsync(files.RunArgs());

        run.ExitCode.Should().Be(ExitCodes.UsageOrData);
        run.Error.Should().Contain(expected);
        run.Output.Should().BeEmpty();
        File.Exists(files.RecordingPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("systemone", true)]
    [InlineData("systemone", false)]
    [InlineData("typesafe-jev", true)]
    public async Task Providers_File_Entry_Reaches_The_Adapter_Request(string kind, bool keySet)
    {
        string variable = TestVariable();
        try
        {
            if (keySet)
            {
                Environment.SetEnvironmentVariable(variable, "test-key-not-a-credential");
            }

            // Jev on OpenRouter's route with the model overridden; System One on a path of its own, keyed optionally.
            string options = kind == "systemone"
                ? $$"""{ "baseUrl": "https://von.example", "path": "/v2/decide", "model": "von-1.2.2", "apiKeyVariable": "{{variable}}" }"""
                : $$"""{ "route": {{OpenRouterRoute("", variable)}}, "model": "typesafe/jev-override" }""";
            using TempFile file = TempFile.Write(Holding($$"""{ "entry": { "kind": "{{kind}}", "options": {{options}} } }"""), ".json");
            Action<ISemanticPolicyBuilder> fromFile = ProvidersFile.Read(file.Path);
            AnsweringHandler handler = new();

            IReadOnlyDictionary<string, IDecisionProvider> providers = Cli.Providers.Resolve(
                builder =>
                {
                    fromFile(builder);
                    builder.Services.AddHttpClient("entry").ConfigurePrimaryHttpMessageHandler(() => handler);
                },
                Samples.Guard(FailureBehavior.Fallback(Verdict.Escalate), ["entry"], [Samples.Flagged()]));
            ProviderResult result = await providers["entry"].DecideAsync(
                Samples.Flagged().CreateRequest(SemanticContext.FromText("synthetic context")),
                TestContext.Current.CancellationToken);

            result.Provider.Id.Should().Be("entry");
            result.Outcome.Should().Be(ProviderOutcome.Success);
            handler.Uri.Should().Be(kind == "systemone" ? "https://von.example/v2/decide" : "https://openrouter.ai/api/v1/systemone");
            handler.Model.Should().Be(kind == "systemone" ? "von-1.2.2" : "typesafe/jev-override");
            handler.Authorization.Should().Be(keySet ? "Bearer test-key-not-a-credential" : null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // A key pasted where its variable's name belongs. A bound Jev entry would otherwise reach the adapter, whose message
    // for an unset variable names the variable; the System One entry is left unbound, so no run calls a server.
    [Theory]
    [InlineData("typesafe-jev", "")]
    [InlineData("typesafe-jev", ".route")]
    [InlineData("systemone", "")]
    public async Task Providers_File_Refuses_A_Key_Variable_That_Is_Not_A_Name(string kind, string within)
    {
        string pasted = $"sk-or-v1-{_marker}";
        string options = kind == "systemone"
            ? $$"""{ "baseUrl": "http://127.0.0.1:8000", "model": "von-1.2.2", "apiKeyVariable": "{{pasted}}" }"""
            : within == ""
                ? $$"""{ "route": {{OpenRouterRoute("")}}, "apiKeyVariable": "{{pasted}}" }"""
                : $$"""{ "route": {{OpenRouterRoute("", pasted)}} }""";
        using Files files = Files.Create(
            Holding($$"""{ "entry": { "kind": "{{kind}}", "options": {{options}} } }"""),
            bound: kind == "systemone" ? "absent" : "entry");

        CliRun run = await InvokeAsync(files.RunArgs());

        run.ExitCode.Should().Be(ExitCodes.UsageOrData);
        run.Error.Should().Contain(
            $"Providers file '{files.ProvidersPath}': provider 'entry': options{within}.apiKeyVariable is not the name of an environment variable");
        (run.Output + run.Error).Should().NotContain(_marker);
        File.Exists(files.RecordingPath).Should().BeFalse();
    }

    // Visual Studio and Windows PowerShell 5.1 write UTF-8 with a byte-order mark; the dataset and policy readers accept it.
    [Fact]
    public void Providers_File_Written_With_A_Byte_Order_Mark_Is_Read()
    {
        using TempFile file = TempFile.Write("﻿" + Holding($$"""{ "von": {{_von}} }"""), ".json");

        IReadOnlyDictionary<string, IDecisionProvider> resolved = Cli.Providers.Resolve(
            ProvidersFile.Read(file.Path),
            Samples.Guard(FailureBehavior.Fallback(Verdict.Escalate), ["von"], [Samples.Flagged()]));

        resolved.Keys.Should().Equal("von");
    }

    [Theory]
    [InlineData("report")]
    [InlineData("sweep")]
    [InlineData("compare")]
    public void Providers_Option_Belongs_To_Run_Only(string verb)
    {
        Command root = EvalsCli.Build();

        root.Parse([verb, "--providers", "providers.json"]).Errors
            .Should().Contain(error => error.Message.Contains("--providers"));
        root.Parse(["run", "--providers", "providers.json"]).Errors
            .Should().NotContain(error => error.Message.Contains("--providers"));
    }

    private static string Holding(string providers) => $$"""{ "providers": {{providers}} }""";

    // OpenRouter's route spelled out, keyed by a variable no test sets unless it says so; `extra` opens the object.
    private static string OpenRouterRoute(string extra, string keyVariable = "SEMANTICPOLICY_TEST_UNSET_KEY") =>
        $$"""{ {{extra}} "baseUrl": "https://openrouter.ai/api", "path": "/v1/systemone", "model": "typesafe/jev-1.13", "apiKeyVariable": "{{keyVariable}}" }""";

    // Variables are process-wide and test classes run in parallel, so a test names one no developer or other test uses.
    private static string TestVariable() => $"SEMANTICPOLICY_TEST_{Guid.NewGuid():N}";

    private static async Task<CliRun> InvokeAsync(string[] args, Action<ISemanticPolicyBuilder>? providers = null)
    {
        StringWriter output = new();
        StringWriter error = new();
        int exit = await EvalsCli.Build(new CliIo(output, error), providers).Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false },
            TestContext.Current.CancellationToken);
        return new CliRun(exit, output.ToString(), error.ToString());
    }

    // Stands in for the server behind a named client: answers every request with one Boolean answer on the System One
    // wire and keeps what the last request carried, so no test reaches a real endpoint.
    private sealed class AnsweringHandler : HttpMessageHandler
    {
        public string? Uri { get; private set; }

        public string? Model { get; private set; }

        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Model = body.RootElement.GetProperty("model").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"answers":{"decision":{"type":"noul","noul":0.91}}}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    // A policy binding the names given, a one-row dataset and a providers file, in a directory of their own.
    private sealed class Files : IDisposable
    {
        private readonly DirectoryInfo _directory;

        private Files(DirectoryInfo directory) => _directory = directory;

        public string PolicyPath => Path.Combine(_directory.FullName, "policy.json");

        public string DatasetPath => Path.Combine(_directory.FullName, "rows.jsonl");

        public string ProvidersPath => Path.Combine(_directory.FullName, "providers.json");

        public string RecordingPath => Path.Combine(_directory.FullName, "rows.recording.jsonl");

        public static Files Create(string providers, params string[] bound)
        {
            Files files = new(Directory.CreateTempSubdirectory("semanticpolicy-evals-"));
            Policy policy = Samples.Guard(FailureBehavior.Fallback(Verdict.Escalate), bound, [Samples.Flagged()]);
            File.WriteAllText(files.PolicyPath, JsonSerializer.Serialize(policy, SemanticPolicyJson.Options));
            File.WriteAllText(files.DatasetPath, """{"id":"r1","input":"synthetic context","label":true}""" + "\n");
            File.WriteAllText(files.ProvidersPath, providers);
            return files;
        }

        public string[] RunArgs() =>
        [
            "run",
            "--policy", PolicyPath,
            "--dataset", DatasetPath,
            "--record", RecordingPath,
            "--providers", ProvidersPath,
        ];

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A file the OS still holds is left for the temp directory's own cleanup.
            }
        }
    }
}
