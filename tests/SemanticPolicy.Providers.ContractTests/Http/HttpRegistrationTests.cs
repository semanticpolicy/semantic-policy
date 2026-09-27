using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.Http;

namespace SemanticPolicy.Providers.ContractTests.Http;

public sealed class HttpRegistrationTests
{
    private static readonly SemanticContext _context = SemanticContext.FromText("a synthetic context");

    [Theory]
    [InlineData("Types null", "Types")]
    [InlineData("Types empty", "Types")]
    [InlineData("Evidence null", "Evidence")]
    [InlineData("StructuredContext null", "StructuredContext")]
    [InlineData("BaseUrl null", "BaseUrl")]
    [InlineData("BaseUrl relative", "BaseUrl")]
    [InlineData("ftp://127.0.0.1", "BaseUrl")]
    [InlineData("http://10.0.0.5 without the flag", "BaseUrl")]
    [InlineData("Path v0/decide", "Path")]
    [InlineData("MaxContextLength 0", "MaxContextLength")]
    [InlineData("Timeout zero", "Timeout")]
    [InlineData("Id empty", "Id")]
    [InlineData("Model unset", "Model")]
    [InlineData("Model blank", "Model")]
    public void Registration_With_An_Undeclared_Or_Invalid_Option_Throws_At_The_Call(string defect, string property)
    {
        ISemanticPolicyBuilder builder = new ServiceCollection().AddSemanticPolicy();

        Action add = () => builder.AddHttpProvider("v0", options =>
        {
            Declared(options);
            switch (defect)
            {
                case "Types null":
                    options.Types = null;
                    break;
                case "Types empty":
                    options.Types = [];
                    break;
                case "Evidence null":
                    options.Evidence = null;
                    break;
                case "StructuredContext null":
                    options.StructuredContext = null;
                    break;
                case "BaseUrl null":
                    options.BaseUrl = null;
                    break;
                case "BaseUrl relative":
                    options.BaseUrl = new Uri("/v0", UriKind.Relative);
                    break;
                case "ftp://127.0.0.1":
                    options.BaseUrl = new Uri("ftp://127.0.0.1");
                    break;
                case "http://10.0.0.5 without the flag":
                    options.BaseUrl = new Uri("http://10.0.0.5");
                    break;
                case "Path v0/decide":
                    options.Path = "v0/decide";
                    break;
                case "MaxContextLength 0":
                    options.MaxContextLength = 0;
                    break;
                case "Timeout zero":
                    options.Timeout = TimeSpan.Zero;
                    break;
                case "Id empty":
                    options.Id = "";
                    break;
                case "Model unset":
                    options.Model = null;
                    break;
                case "Model blank":
                    options.Model = " ";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(defect));
            }
        });

        add.Should().Throw<ArgumentException>().Which.ParamName.Should().Be(property);
    }

    [Theory]
    [InlineData("https://10.0.0.5", false)]
    [InlineData("http://127.0.0.1:8080", false)]
    [InlineData("http://localhost:8080", false)]
    [InlineData("http://[::1]:8080", false)]
    [InlineData("http://10.0.0.5", true)]
    public async Task Registration_Accepts_Https_Loopback_Http_And_Opted_In_Http(string baseUrl, bool allowInsecureHttp)
    {
        ScriptedHttpMessageHandler handler = Answering();
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddHttpProvider("v0", options =>
            {
                Declared(options);
                options.BaseUrl = new Uri(baseUrl);
                options.AllowInsecureHttp = allowInsecureHttp;
            })
            .AddPolicy(Define("p", "v0"));
        services.AddHttpClient("v0").ConfigurePrimaryHttpMessageHandler(() => handler);

        await using ServiceProvider container = services.BuildServiceProvider();
        await container.GetRequiredService<IPolicyEvaluator>()
            .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        handler.RequestCount.Should().Be(1);
        handler.LastRequest.Uri.Should().Be(new Uri(baseUrl.TrimEnd('/') + "/v0/decide"));
    }

    // The registration's name, the named client's name and the id on every result are one name unless
    // configure sets another: the scripted handler sits only under the registration's name, so the
    // call reaching it proves the factory created the client under that name. A policy cannot bind a
    // threshold to a provider that declares no evidence, so the provider is called directly.
    [Fact]
    public async Task Registered_Provider_Reports_The_Declared_Capabilities_Under_The_Registration_Name()
    {
        ScriptedHttpMessageHandler handler = Answering();
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddHttpProvider("v0-local", options =>
            {
                Declared(options);
                options.Types = [DecisionType.Boolean, DecisionType.Score];
                options.Evidence = [];
                options.StructuredContext = true;
            });
        services.AddHttpClient("v0-local").ConfigurePrimaryHttpMessageHandler(() => handler);

        await using ServiceProvider container = services.BuildServiceProvider();
        IDecisionProvider provider = container.GetServices<ProviderRegistration>()
            .Should().ContainSingle(registration => registration.Name == "v0-local").Which.Provider;
        ProviderResult result = await provider.DecideAsync(
            new DecisionRequest(DecisionType.Boolean, "Is the synthetic context benign?", _context.ToJson()),
            TestContext.Current.CancellationToken);
        using HttpClient client = container.GetRequiredService<IHttpClientFactory>().CreateClient("v0-local");

        provider.Id.Should().Be("v0-local");
        provider.Capabilities.Types.Should().BeEquivalentTo([DecisionType.Boolean, DecisionType.Score]);
        provider.Capabilities.Evidence.Should().BeEmpty();
        provider.Capabilities.StructuredContext.Should().BeTrue();
        provider.Capabilities.RawOutput.Should().BeTrue();
        result.Provider.Id.Should().Be("v0-local");
        handler.RequestCount.Should().Be(1);
        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Theory]
    [InlineData("ApiKey set", "Bearer test-key-not-a-credential")]
    [InlineData("the variable set", "Bearer value-of-the-variable")]
    [InlineData("the variable named but unset", null)]
    [InlineData("neither", null)]
    public async Task Bearer_Header_Follows_The_Key(string key, string? authorization)
    {
        string variable = TestVariable();
        try
        {
            if (key == "the variable set")
            {
                Environment.SetEnvironmentVariable(variable, "value-of-the-variable");
            }

            ScriptedHttpMessageHandler handler = Answering();
            ServiceCollection services = new();
            services.AddSemanticPolicy()
                .AddHttpProvider("v0", options =>
                {
                    Declared(options);
                    switch (key)
                    {
                        case "ApiKey set":
                            options.ApiKey = "test-key-not-a-credential";
                            break;
                        case "the variable set":
                        case "the variable named but unset":
                            options.ApiKeyVariable = variable;
                            break;
                    }
                })
                .AddPolicy(Define("p", "v0"));
            services.AddHttpClient("v0").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using ServiceProvider container = services.BuildServiceProvider();
            await container.GetRequiredService<IPolicyEvaluator>()
                .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);
            using HttpClient client = container.GetRequiredService<IHttpClientFactory>().CreateClient("v0");

            handler.RequestCount.Should().Be(1);
            handler.LastRequest.Header("Authorization").Should().Be(authorization);
            client.DefaultRequestHeaders.Authorization.Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // A Boolean rule on the score scale; a failure escalates, a verdict no answer here maps to.
    private static Policy Define(string id, string provider) =>
        Policy.Define(id)
            .Enforce()
            .Rule(Policy.Rule("r").Boolean("Does the context mention a synthetic marker?").WhenTrue(Verdict.Warn, Verdict.Deny))
            .Using(provider, b => b.WarnAboveScore(0.6).DenyAboveScore(0.95))
            .OnFailure(FailureBehavior.Escalate)
            .Build();

    // Everything a registration must declare, on loopback http with no key: the registration reads no
    // environment variable, and no call leaves the scripted transport.
    private static void Declared(HttpProviderOptions options)
    {
        options.BaseUrl = HttpHarness.BaseUrl;
        options.Model = HttpHarness.Model;
        options.Types = [DecisionType.Boolean];
        options.Evidence = [EvidenceKind.Score];
        options.StructuredContext = false;
    }

    // Environment variables are process-wide and xUnit runs classes in parallel, so a test never names
    // a variable a developer may have set: a name unique to the run cannot collide with a real key.
    private static string TestVariable() =>
        $"SEMANTICPOLICY_TEST_{Guid.NewGuid():N}";

    private static ScriptedHttpMessageHandler Answering()
    {
        ScriptedHttpMessageHandler handler = new();
        handler.Respond(HttpStatusCode.OK, HttpHarness.Success(DecisionType.Boolean));
        return handler;
    }
}
