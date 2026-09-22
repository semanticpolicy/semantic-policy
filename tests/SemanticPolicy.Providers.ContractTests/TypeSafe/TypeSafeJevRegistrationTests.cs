using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Providers.ContractTests.Support;
using SemanticPolicy.Providers.TypeSafe;

namespace SemanticPolicy.Providers.ContractTests.TypeSafe;

public sealed class TypeSafeJevRegistrationTests
{
    private static readonly SemanticContext _context = SemanticContext.FromText("a synthetic context");

    [Theory]
    [InlineData("route absent")]
    [InlineData("timeout zero")]
    [InlineData("http to a remote host")]
    public void AddTypeSafeJev_Rejects_Invalid_Options_At_The_Call(string defect)
    {
        ServiceCollection services = new();
        ISemanticPolicyBuilder builder = services.AddSemanticPolicy();

        Action add = () => builder.AddTypeSafeJev("jev", options =>
        {
            options.ApiKey = TypeSafeJevHarness.ApiKey;
            switch (defect)
            {
                case "route absent":
                    break;
                case "timeout zero":
                    options.Route = TypeSafeJevRoute.TypeSafe;
                    options.Timeout = TimeSpan.Zero;
                    break;
                case "http to a remote host":
                    options.Route = TypeSafeJevRoute.TypeSafe with { BaseUrl = new Uri("http://api.example") };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(defect));
            }
        });

        add.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Two_Registrations_Under_Two_Names_Are_Two_Providers_The_Evaluator_Serves()
    {
        ScriptedHttpMessageHandler direct = Answering(0.91);
        ScriptedHttpMessageHandler gateway = Answering(0.91);
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddTypeSafeJev("jev", options =>
            {
                options.Route = TypeSafeJevRoute.TypeSafe;
                options.ApiKey = TypeSafeJevHarness.ApiKey;
                options.Id = "jev-direct";
            })
            .AddTypeSafeJev("jev-openrouter", options =>
            {
                options.Route = TypeSafeJevRoute.OpenRouter;
                options.ApiKey = TypeSafeJevHarness.ApiKey;
                options.Id = "jev-gateway";
            })
            .AddPolicy(Define("direct", "jev"))
            .AddPolicy(Define("gateway", "jev-openrouter"));
        services.AddHttpClient("jev").ConfigurePrimaryHttpMessageHandler(() => direct);
        services.AddHttpClient("jev-openrouter").ConfigurePrimaryHttpMessageHandler(() => gateway);

        await using ServiceProvider container = services.BuildServiceProvider();
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        PolicyVerdict viaDirect = await evaluator.EvaluateAsync("direct", _context, TestContext.Current.CancellationToken);
        PolicyVerdict viaGateway = await evaluator.EvaluateAsync("gateway", _context, TestContext.Current.CancellationToken);

        viaDirect.Evaluated.Should().Be(Verdict.Deny);
        viaGateway.Evaluated.Should().Be(Verdict.Deny);
        ProviderIdOf(viaDirect).Should().Be("jev-direct");
        ProviderIdOf(viaGateway).Should().Be("jev-gateway");
        direct.LastRequest.Uri.Should().Be(new Uri("https://api.typesafe.ai/v1/systemone"));
        gateway.LastRequest.Uri.Should().Be(new Uri("https://openrouter.ai/api/v1/systemone"));
        direct.RequestCount.Should().Be(1);
        gateway.RequestCount.Should().Be(1);
    }

    // The registration's name, the named client's name and the id on the verdict are one name unless
    // configure sets another. Two registrations that both kept the type's own default would report the
    // same provider on every verdict, and nothing downstream could tell which route answered.
    [Fact]
    public async Task Registration_Name_Is_The_Provider_Id_By_Default()
    {
        ScriptedHttpMessageHandler handler = Answering(0.91);
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddTypeSafeJev("jev-direct", Preset())
            .AddPolicy(Define("p", "jev-direct"));
        services.AddHttpClient("jev-direct").ConfigurePrimaryHttpMessageHandler(() => handler);

        await using ServiceProvider container = services.BuildServiceProvider();
        PolicyVerdict verdict = await container.GetRequiredService<IPolicyEvaluator>()
            .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        ProviderIdOf(verdict).Should().Be("jev-direct");
    }

    [Fact]
    public void Missing_Key_Variable_Throws_PolicyConfigurationException_Naming_The_Variable_At_First_Resolve()
    {
        string variable = TestVariable();
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddTypeSafeJev("jev", options =>
            {
                options.Route = TypeSafeJevRoute.TypeSafe;
                options.ApiKeyVariable = variable;
            })
            .AddPolicy(Define("p", "jev"));

        using ServiceProvider container = services.BuildServiceProvider();
        Action resolve = () => container.GetRequiredService<IPolicyEvaluator>();

        PolicyConfigurationException error = resolve.Should().Throw<PolicyConfigurationException>().Which;
        error.ProviderId.Should().Be("jev");
        error.PolicyId.Should().BeNull();
        error.Message.Should().Contain(variable);
    }

    [Theory]
    [InlineData("the options' key")]
    [InlineData("the named variable")]
    [InlineData("the route's variable")]
    public async Task Key_Resolution_Prefers_ApiKey_Then_The_Named_Variable_Then_The_Route_Variable(string winner)
    {
        string namedVariable = TestVariable();
        string routeVariable = TestVariable();
        try
        {
            Environment.SetEnvironmentVariable(namedVariable, "value-of-the-named-variable");
            Environment.SetEnvironmentVariable(routeVariable, "value-of-the-route-variable");
            string expected = winner switch
            {
                "the options' key" => TypeSafeJevHarness.ApiKey,
                "the named variable" => "value-of-the-named-variable",
                _ => "value-of-the-route-variable",
            };
            ScriptedHttpMessageHandler handler = Answering(0.91);
            ServiceCollection services = new();
            services.AddSemanticPolicy()
                .AddTypeSafeJev("jev", options =>
                {
                    options.Route = new TypeSafeJevRoute(
                        new Uri("https://gateway.example"),
                        "/v1/systemone",
                        "jev-1.13.0",
                        routeVariable);
                    if (winner == "the options' key")
                    {
                        options.ApiKey = TypeSafeJevHarness.ApiKey;
                    }

                    if (winner != "the route's variable")
                    {
                        options.ApiKeyVariable = namedVariable;
                    }
                })
                .AddPolicy(Define("p", "jev"));
            services.AddHttpClient("jev").ConfigurePrimaryHttpMessageHandler(() => handler);

            await using ServiceProvider container = services.BuildServiceProvider();
            await container.GetRequiredService<IPolicyEvaluator>()
                .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

            handler.LastRequest.Header("Authorization").Should().Be($"Bearer {expected}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(namedVariable, null);
            Environment.SetEnvironmentVariable(routeVariable, null);
        }
    }

    [Fact]
    public void Same_Name_Registered_Twice_Fails_With_Core_Duplicate_Error_At_First_Resolve()
    {
        ServiceCollection services = new();
        ISemanticPolicyBuilder builder = services.AddSemanticPolicy();

        // Both carry a key, because every factory runs before Core compares names.
        Action register = () => builder.AddTypeSafeJev("jev", Preset()).AddTypeSafeJev("jev", Preset());

        register.Should().NotThrow();

        using ServiceProvider container = services.BuildServiceProvider();
        Action resolve = () => container.GetRequiredService<IPolicyEvaluator>();

        resolve.Should().Throw<PolicyConfigurationException>()
            .Which.Message.Should().Contain("registered twice");
    }

    [Fact]
    public async Task Host_Configuration_Of_The_Named_Client_Applies_To_The_Adapter()
    {
        RecordingHandler recording = new();
        ScriptedHttpMessageHandler handler = Answering(0.91);
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddTypeSafeJev("jev", Preset())
            .AddPolicy(Define("p", "jev"));

        // After the registration, on the same name: whichever runs second still applies.
        services.AddHttpClient("jev")
            .AddHttpMessageHandler(() => recording)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        await using ServiceProvider container = services.BuildServiceProvider();
        PolicyVerdict verdict = await container.GetRequiredService<IPolicyEvaluator>()
            .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        verdict.Evaluated.Should().Be(Verdict.Deny);
        recording.Seen.Should().ContainSingle()
            .Which.Should().Be(new Uri("https://api.typesafe.ai/v1/systemone"));
    }

    [Fact]
    public async Task Options_Captured_In_Configure_Change_Nothing_After_The_Call()
    {
        TypeSafeJevOptions? captured = null;
        ScriptedHttpMessageHandler handler = Answering(0.91);
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddTypeSafeJev("jev", options =>
            {
                options.Route = TypeSafeJevRoute.TypeSafe;
                options.ApiKey = TypeSafeJevHarness.ApiKey;
                options.Model = "jev-at-the-call";
                captured = options;
            })
            .AddPolicy(Define("p", "jev"));
        services.AddHttpClient("jev").ConfigurePrimaryHttpMessageHandler(() => handler);
        captured!.Model = "jev-after-the-call";

        await using ServiceProvider container = services.BuildServiceProvider();
        await container.GetRequiredService<IPolicyEvaluator>()
            .EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        JsonElement body = JsonSerializer.Deserialize<JsonElement>(handler.LastRequest.Body!);
        body.GetProperty("model").GetString().Should().Be("jev-at-the-call");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adapter_Traffic_Writes_No_HttpClient_Log_Category(bool hostAddsDefaultLogger)
    {
        CapturingLoggerProvider logs = new();
        ScriptedHttpMessageHandler handler = Answering(0.91);
        ServiceCollection services = new();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSemanticPolicy()
            .AddTypeSafeJev("jev", Preset())
            .AddPolicy(Define("p", "jev"));
        IHttpClientBuilder client = services.AddHttpClient("jev").ConfigurePrimaryHttpMessageHandler(() => handler);
        if (hostAddsDefaultLogger)
        {
            client.AddDefaultLogger();
        }

        await using ServiceProvider container = services.BuildServiceProvider();
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        await evaluator.EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        IEnumerable<string> written = logs.Categories
            .Where(category => category.StartsWith("System.Net.Http.HttpClient.jev", StringComparison.Ordinal));
        handler.RequestCount.Should().Be(1);
        if (hostAddsDefaultLogger)
        {
            written.Should().NotBeEmpty();
        }
        else
        {
            written.Should().BeEmpty();
        }
    }

    // The observable outcome of obtaining a client per call: a provider that kept its first client would
    // never see a second handler, so the factory's rotation, DNS refresh and lifetime would never reach
    // the adapter's traffic. SetHandlerLifetime rejects anything under a second, so this is the one slow
    // test here; it polls for the second handler rather than sleeping a fixed span.
    [Fact]
    public async Task Host_Handler_Lifetime_Rotates_The_Adapter_Primary_Handler()
    {
        int built = 0;
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddTypeSafeJev("jev", Preset())
            .AddPolicy(Define("p", "jev"));
        services.AddHttpClient("jev")
            .SetHandlerLifetime(TimeSpan.FromSeconds(1))
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                Interlocked.Increment(ref built);
                return Answering(0.91);
            });

        await using ServiceProvider container = services.BuildServiceProvider();
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        PolicyVerdict first = await evaluator.EvaluateAsync("p", _context, TestContext.Current.CancellationToken);
        long started = Stopwatch.GetTimestamp();
        int evaluations = 1;
        while (Volatile.Read(ref built) < 2 && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            PolicyVerdict next = await evaluator.EvaluateAsync("p", _context, TestContext.Current.CancellationToken);
            next.Evaluated.Should().Be(Verdict.Deny);
            evaluations++;
        }

        first.Evaluated.Should().Be(Verdict.Deny);
        Volatile.Read(ref built).Should().BeGreaterThanOrEqualTo(2);
        evaluations.Should().BeGreaterThan(1);
    }

    // A 100 s default would surface as a cancellation with no token cancelled, which the evaluator
    // treats as a programming error; no fast test can observe that, so the setting is asserted directly.
    [Fact]
    public void Named_Client_Timeout_Is_Infinite()
    {
        ServiceCollection services = new();
        services.AddSemanticPolicy().AddTypeSafeJev("jev", Preset());

        using ServiceProvider container = services.BuildServiceProvider();
        using HttpClient client = container.GetRequiredService<IHttpClientFactory>().CreateClient("jev");

        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    // The README's own shape: one Boolean rule, Warn at 0.6 / Deny at 0.9 on probability, on the one
    // provider named. A scripted 0.91 therefore evaluates to Deny.
    private static Policy Define(string id, string provider) =>
        Policy.Define(id)
            .Enforce()
            .Rule(Policy.Rule("r").Boolean("Does the context mention a synthetic marker?").WhenTrue(Verdict.Warn, Verdict.Deny))
            .Using(provider, b => b.WarnAboveProbability(0.6).DenyAboveProbability(0.9))
            .OnFailure(FailureBehavior.Deny)
            .Build();

    // Environment variables are process-wide and xUnit runs classes in parallel, so a test never names
    // a variable a developer may have set: a name unique to the run cannot collide with a real key.
    private static string TestVariable() =>
        $"SEMANTICPOLICY_TEST_{Guid.NewGuid():N}";

    private static ScriptedHttpMessageHandler Answering(double noul)
    {
        ScriptedHttpMessageHandler handler = new();
        handler.Respond(HttpStatusCode.OK, JevFixtures.BooleanAnswer(noul));
        return handler;
    }

    // A preset registration with the key set here: one that read the environment would pick up a
    // developer's real key and reach a live endpoint.
    private static Action<TypeSafeJevOptions> Preset(TypeSafeJevRoute? route = null) =>
        options =>
        {
            options.Route = route ?? TypeSafeJevRoute.TypeSafe;
            options.ApiKey = TypeSafeJevHarness.ApiKey;
        };

    private static string ProviderIdOf(PolicyVerdict verdict) =>
        verdict.Rules.Should().ContainSingle().Which.Attempts.Should().ContainSingle().Which.Result.Provider.Id;

    // A host's own handler in the chain: it records what passed through it and forwards unchanged.
    private sealed class RecordingHandler : DelegatingHandler
    {
        private readonly Lock _lock = new();
        private readonly List<Uri> _seen = [];

        public IReadOnlyList<Uri> Seen
        {
            get
            {
                lock (_lock)
                {
                    return [.. _seen];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri)
            {
                lock (_lock)
                {
                    _seen.Add(uri);
                }
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    // Records the category of every entry written, which is all the logger tests read: what the entries
    // say is the factory's business, and the adapter writes none of them.
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _lock = new();
        private readonly List<string> _categories = [];

        public IReadOnlyList<string> Categories
        {
            get
            {
                lock (_lock)
                {
                    return [.. _categories];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

        public void Dispose()
        {
        }

        private void Record(string category)
        {
            lock (_lock)
            {
                _categories.Add(category);
            }
        }

        private sealed class Capturing(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                provider.Record(category);
        }
    }
}
