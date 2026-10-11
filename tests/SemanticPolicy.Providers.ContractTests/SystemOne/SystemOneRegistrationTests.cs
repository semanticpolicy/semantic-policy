using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.SystemOne;

namespace SemanticPolicy.Providers.ContractTests.SystemOne;

public sealed class SystemOneRegistrationTests
{
    // OpenRouter's public error when no endpoint of the model is on its zero data retention list.
    private const string _zeroDataRetentionRefusal =
        """{"error":{"message":"No endpoints found matching your data policy (Zero data retention). Configure: https://openrouter.ai/settings/privacy","code":404,"metadata":{"routing_funnel":[{"step":"Initial Endpoints","endpoint_count":1}],"failed_routing_step":"Filter by Data Policy"}}}""";

    private static readonly SemanticContext _context = SemanticContext.FromText("a synthetic context");

    [Theory]
    [InlineData("BaseUrl missing", "BaseUrl")]
    [InlineData("BaseUrl relative", "BaseUrl")]
    [InlineData("ftp://127.0.0.1", "BaseUrl")]
    [InlineData("http://von.internal without the flag", "BaseUrl")]
    [InlineData("BaseUrl with a query", "BaseUrl")]
    [InlineData("BaseUrl with a fragment", "BaseUrl")]
    [InlineData("Path without /", "Path")]
    [InlineData("Model blank", "Model")]
    [InlineData("Timeout zero", "Timeout")]
    [InlineData("Evidence Logit", "Evidence")]
    [InlineData("Evidence Margin", "Evidence")]
    [InlineData("Evidence Unknown", "Evidence")]
    [InlineData("MaxContextLength 0", "MaxContextLength")]
    [InlineData("MaxContextLength -1", "MaxContextLength")]
    [InlineData("Id blank", "Id")]
    public void AddSystemOne_Rejects_An_Invalid_Option_At_The_Call(string defect, string property)
    {
        ISemanticPolicyBuilder builder = new ServiceCollection().AddSemanticPolicy();

        Action add = () => builder.AddSystemOne("local", options =>
        {
            Loopback(options);
            switch (defect)
            {
                case "BaseUrl missing":
                    options.BaseUrl = null;
                    break;
                case "BaseUrl relative":
                    options.BaseUrl = new Uri("/v1", UriKind.Relative);
                    break;
                case "ftp://127.0.0.1":
                    options.BaseUrl = new Uri("ftp://127.0.0.1");
                    break;
                case "http://von.internal without the flag":
                    options.BaseUrl = new Uri("http://von.internal");
                    break;
                case "BaseUrl with a query":
                    options.BaseUrl = new Uri("http://127.0.0.1:8000/?token=abc");
                    break;
                case "BaseUrl with a fragment":
                    options.BaseUrl = new Uri("http://127.0.0.1:8000/#v1");
                    break;
                case "Path without /":
                    options.Path = "v1/systemone";
                    break;
                case "Model blank":
                    options.Model = " ";
                    break;
                case "Timeout zero":
                    options.Timeout = TimeSpan.Zero;
                    break;
                case "Evidence Logit":
                    options.Evidence = EvidenceKind.Logit;
                    break;
                case "Evidence Margin":
                    options.Evidence = EvidenceKind.Margin;
                    break;
                case "Evidence Unknown":
                    options.Evidence = EvidenceKind.Unknown;
                    break;
                case "MaxContextLength 0":
                    options.MaxContextLength = 0;
                    break;
                case "MaxContextLength -1":
                    options.MaxContextLength = -1;
                    break;
                case "Id blank":
                    options.Id = "";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(defect));
            }
        });

        add.Should().Throw<ArgumentException>().Which.ParamName.Should().Be(property);
    }

    [Theory]
    [InlineData("https://von.internal", false)]
    [InlineData("http://127.0.0.1:8000", false)]
    [InlineData("http://localhost:8000", false)]
    [InlineData("http://[::1]:8000", false)]
    [InlineData("http://von.internal", true)]
    public async Task AddSystemOne_Accepts_Https_Loopback_Http_And_Opted_In_Http(string baseUrl, bool allowInsecureHttp)
    {
        ScriptedHttpMessageHandler handler = Answering(0.91);
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddSystemOne("local", options =>
            {
                options.BaseUrl = new Uri(baseUrl);
                options.Model = SystemOneHarness.Model;
                options.AllowInsecureHttp = allowInsecureHttp;
            })
            .AddPolicy(Define("p", "local"));
        services.AddHttpClient("local").ConfigurePrimaryHttpMessageHandler(() => handler);

        await using ServiceProvider container = services.BuildServiceProvider();
        await container.GetRequiredService<IPolicyEvaluator>()
            .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        handler.RequestCount.Should().Be(1);
        handler.LastRequest.Uri.Should().Be(new Uri(baseUrl.TrimEnd('/') + "/v1/systemone"));
    }

    // The registration's name, the named client's name and the id on the verdict are one name unless
    // configure sets another: the scripted handler sits only under the registration's name, so the
    // call reaching it proves the factory created the client under that name.
    [Fact]
    public async Task AddSystemOne_Registers_Under_Its_Name_As_Provider_Id_And_Client_Name()
    {
        ScriptedHttpMessageHandler handler = Answering(0.91);
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddSystemOne("von-local", Loopback)
            .AddPolicy(Define("p", "von-local"));
        services.AddHttpClient("von-local").ConfigurePrimaryHttpMessageHandler(() => handler);

        await using ServiceProvider container = services.BuildServiceProvider();
        PolicyVerdict verdict = await container.GetRequiredService<IPolicyEvaluator>()
            .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);
        using HttpClient client = container.GetRequiredService<IHttpClientFactory>().CreateClient("von-local");

        ProviderIdOf(verdict).Should().Be("von-local");
        handler.RequestCount.Should().Be(1);
        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Theory]
    [InlineData("a literal ApiKey", "Bearer test-key-not-a-credential")]
    [InlineData("a set variable", "Bearer value-of-the-variable")]
    [InlineData("a named but unset variable", null)]
    [InlineData("neither", null)]
    public async Task Request_Carries_A_Bearer_Only_When_A_Key_Resolves(string key, string? authorization)
    {
        string variable = TestVariable();
        try
        {
            if (key == "a set variable")
            {
                Environment.SetEnvironmentVariable(variable, "value-of-the-variable");
            }

            ScriptedHttpMessageHandler handler = Answering(0.91);
            ServiceCollection services = new();
            services.AddSemanticPolicy()
                .AddSystemOne("local", options =>
                {
                    Loopback(options);
                    switch (key)
                    {
                        case "a literal ApiKey":
                            options.ApiKey = "test-key-not-a-credential";
                            break;
                        case "a set variable":
                        case "a named but unset variable":
                            options.ApiKeyVariable = variable;
                            break;
                    }
                })
                .AddPolicy(Define("p", "local"));
            services.AddHttpClient("local").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using ServiceProvider container = services.BuildServiceProvider();
            await container.GetRequiredService<IPolicyEvaluator>()
                .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);
            using HttpClient client = container.GetRequiredService<IHttpClientFactory>().CreateClient("local");

            handler.RequestCount.Should().Be(1);
            handler.LastRequest.Header("Authorization").Should().Be(authorization);
            client.DefaultRequestHeaders.Authorization.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // The margin of the two Boolean ends is |2·noul − 1|: 0.52 leaves 0.04, under the 0.1 gate.
    [Theory]
    [InlineData(0.95, Verdict.Deny)]
    [InlineData(0.7, Verdict.Warn)]
    [InlineData(0.3, Verdict.Allow)]
    [InlineData(0.52, Verdict.Abstain)]
    public async Task Score_Policy_Evaluates_A_SystemOne_Boolean_Answer_To_A_Verdict(double noul, Verdict expected)
    {
        ScriptedHttpMessageHandler handler = Answering(noul);
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddSystemOne("local", Loopback)
            .AddPolicy(Define("p", "local"));
        services.AddHttpClient("local").ConfigurePrimaryHttpMessageHandler(() => handler);

        await using ServiceProvider container = services.BuildServiceProvider();
        PolicyVerdict verdict = await container.GetRequiredService<IPolicyEvaluator>()
            .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        verdict.Evaluated.Should().Be(expected);
    }

    // A refusal is an ordinary 404 to the adapter, read through the same status table as every server's,
    // with nothing in the body singled out: the reason stays on Raw, and the policy's failure behaviour
    // moves on to the next binding as it would for any other failure.
    [Fact]
    public async Task Zero_Data_Retention_Refusal_Is_Rejected_Input_And_The_Next_Binding_Decides()
    {
        ScriptedHttpMessageHandler refusing = new();
        refusing.Respond(HttpStatusCode.NotFound, _zeroDataRetentionRefusal);
        ScriptedHttpMessageHandler answering = Answering(0.3);
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddSystemOne("zero-retention", options =>
            {
                Loopback(options);
                options.RequestZeroDataRetention = true;
            })
            .AddSystemOne("next", Loopback)
            .AddPolicy(Policy.Define("p")
                .Enforce()
                .Rule(Policy.Rule("r").Boolean("Does the context mention a synthetic marker?").WhenTrue(Verdict.Warn, Verdict.Deny))
                .Using("zero-retention", b => b.WarnAboveScore(0.6).DenyAboveScore(0.9))
                .Using("next", b => b.WarnAboveScore(0.6).DenyAboveScore(0.9))
                .OnFailure(FailureBehavior.Fallback(Verdict.Deny))
                .Build());
        services.AddHttpClient("zero-retention").ConfigurePrimaryHttpMessageHandler(() => refusing);
        services.AddHttpClient("next").ConfigurePrimaryHttpMessageHandler(() => answering);

        await using ServiceProvider container = services.BuildServiceProvider();
        PolicyVerdict verdict = await container.GetRequiredService<IPolicyEvaluator>()
            .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        RuleVerdict rule = verdict.Rules.Should().ContainSingle().Which;
        rule.DecidingBinding.Should().Be(1);
        rule.Verdict.Should().Be(Verdict.Allow);
        ProviderResult refused = rule.Attempts.Should().Contain(attempt => attempt.BindingIndex == 0).Which.Result;
        refused.Outcome.Status.Should().Be(OutcomeStatus.Failure);
        refused.Outcome.Kind.Should().Be(FailureKind.RejectedInput);
        JsonElement.DeepEquals(refused.Raw!.Value, JsonSerializer.Deserialize<JsonElement>(_zeroDataRetentionRefusal))
            .Should().BeTrue();
        refusing.RequestCount.Should().Be(1);
        JsonSerializer.Deserialize<JsonElement>(refusing.LastRequest.Body!).GetProperty("provider").GetRawText()
            .Should().Be("""{"zdr":true}""");
        answering.RequestCount.Should().Be(1);
        JsonSerializer.Deserialize<JsonElement>(answering.LastRequest.Body!).TryGetProperty("provider", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Probability_Thresholds_On_A_Score_Registration_Fail_At_Configuration()
    {
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddSystemOne("local", Loopback)
            .AddPolicy(Policy.Define("p")
                .Enforce()
                .Rule(Policy.Rule("r").Boolean("Does the context mention a synthetic marker?").WhenTrue(Verdict.Warn, Verdict.Deny))
                .Using("local", b => b.WarnAboveProbability(0.6).DenyAboveProbability(0.9))
                .OnFailure(FailureBehavior.Deny)
                .Build());

        using ServiceProvider container = services.BuildServiceProvider();
        Action resolve = () => container.GetRequiredService<IPolicyEvaluator>();

        PolicyConfigurationException error = resolve.Should().Throw<PolicyConfigurationException>().Which;
        error.ProviderId.Should().Be("local");
        error.Message.Should().Contain(nameof(EvidenceKind.Probability));
    }

    // A Boolean rule on the score scale: Warn at 0.6, Deny at 0.9, and a gate that moves on when the
    // two ends of the answer are closer than 0.1. There is no next binding, so a gated answer abstains.
    // A failure escalates, a verdict no answer here maps to, so no test passes through the failure path.
    private static Policy Define(string id, string provider) =>
        Policy.Define(id)
            .Enforce()
            .Rule(Policy.Rule("r").Boolean("Does the context mention a synthetic marker?").WhenTrue(Verdict.Warn, Verdict.Deny))
            .Using(provider, b => b.WarnAboveScore(0.6).DenyAboveScore(0.9).WhenScoreMarginBelow(0.1))
            .OnFailure(FailureBehavior.Escalate)
            .Build();

    // Loopback http with no key: the registration reads no environment variable, and no call leaves
    // the scripted transport.
    private static void Loopback(SystemOneOptions options)
    {
        options.BaseUrl = SystemOneHarness.BaseUrl;
        options.Model = SystemOneHarness.Model;
    }

    // Environment variables are process-wide and xUnit runs classes in parallel, so a test never names
    // a variable a developer may have set: a name unique to the run cannot collide with a real key.
    private static string TestVariable() =>
        $"SEMANTICPOLICY_TEST_{Guid.NewGuid():N}";

    private static ScriptedHttpMessageHandler Answering(double noul)
    {
        ScriptedHttpMessageHandler handler = new();
        handler.Respond(HttpStatusCode.OK, SystemOneFixtures.BooleanAnswer(noul));
        return handler;
    }

    private static string ProviderIdOf(PolicyVerdict verdict) =>
        verdict.Rules.Should().ContainSingle().Which.Attempts.Should().ContainSingle().Which.Result.Provider.Id;
}
