using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Core.Tests.Support;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Core.Tests;

public sealed class SemanticPolicyServiceCollectionExtensionsTests
{
    private static readonly SemanticContext _context = SemanticContext.FromText("part-a");

    [Fact]
    public async Task AddSemanticPolicy_Resolves_An_Evaluator_Over_Registered_Providers_And_Policies()
    {
        ScriptedProvider provider = new ScriptedProvider("scripted").Returns(Flagged(0.95));
        ServiceCollection services = new();
        services.AddSingleton(provider);

        ISemanticPolicyBuilder builder = services.AddSemanticPolicy();
        builder.Services.Should().BeSameAs(services);
        builder
            .AddProvider("primary", sp => sp.GetRequiredService<ScriptedProvider>())
            .AddPolicy(Define("p", "primary"));

        await using ServiceProvider container = services.BuildServiceProvider();
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        PolicyVerdict verdict = await evaluator.EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        verdict.PolicyId.Should().Be("p");
        verdict.Evaluated.Should().Be(Verdict.Deny);
        verdict.Rules.Should().ContainSingle().Which.Attempts.Should().ContainSingle()
            .Which.ProviderId.Should().Be("primary");
        provider.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task AddSemanticPolicy_Is_Idempotent_And_Defaults_The_Provider_Name_To_Its_Id()
    {
        ScriptedProvider provider = new ScriptedProvider("scripted").Returns(Flagged(0.05));
        ServiceCollection services = new();

        services.AddSemanticPolicy();
        services.AddSemanticPolicy()
            .AddProvider(provider)
            .AddPolicy(Define("p", "scripted"));

        services.Where(descriptor => descriptor.ServiceType == typeof(IPolicyEvaluator))
            .Should().ContainSingle()
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
        await using ServiceProvider container = services.BuildServiceProvider();
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        evaluator.Should().BeOfType<PolicyEvaluator>()
            .And.BeSameAs(container.GetRequiredService<IPolicyEvaluator>());
        PolicyVerdict verdict = await evaluator.EvaluateAsync("p", _context, TestContext.Current.CancellationToken);
        verdict.Evaluated.Should().Be(Verdict.Allow);
        verdict.Rules.Should().ContainSingle().Which.Attempts.Should().ContainSingle()
            .Which.ProviderId.Should().Be("scripted");
        provider.Calls.Should().ContainSingle();
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("policy")]
    public void Duplicate_Registrations_Fail_When_The_Evaluator_Is_First_Resolved(string duplicated)
    {
        ServiceCollection services = new();
        ISemanticPolicyBuilder builder = services.AddSemanticPolicy()
            .AddProvider(new ScriptedProvider("scripted"))
            .AddPolicy(Define("p", "scripted"));
        if (duplicated == "provider")
        {
            builder.AddProvider(new ScriptedProvider("other"), name: "scripted");
        }
        else
        {
            builder.AddPolicy(Define("p", "scripted"));
        }

        using ServiceProvider container = services.BuildServiceProvider();
        Action resolve = () => container.GetRequiredService<IPolicyEvaluator>();

        PolicyConfigurationException error = resolve.Should().Throw<PolicyConfigurationException>().Which;
        error.ProviderId.Should().Be(duplicated == "provider" ? "scripted" : null);
        error.PolicyId.Should().Be(duplicated == "policy" ? "p" : null);
    }

    // What the container built, the container disposes; what it was handed, it does not — the rule
    // AddSingleton itself follows, seen through the two AddProvider overloads.
    [Fact]
    public async Task Disposing_The_Container_Disposes_A_Factory_Built_Provider_And_Leaves_An_Instance_Alone()
    {
        DisposableProvider? built = null;
        DisposableProvider given = new("given");
        ServiceCollection services = new();
        services.AddSemanticPolicy()
            .AddProvider("built", _ => built = new DisposableProvider("built"))
            .AddProvider(given)
            .AddPolicy(Define("p", "built"));
        ServiceProvider container = services.BuildServiceProvider();
        IPolicyEvaluator evaluator = container.GetRequiredService<IPolicyEvaluator>();
        PolicyVerdict verdict = await evaluator.EvaluateAsync("p", _context, TestContext.Current.CancellationToken);

        await container.DisposeAsync();

        verdict.Rules.Should().ContainSingle().Which.Attempts.Should().ContainSingle()
            .Which.ProviderId.Should().Be("built");
        built.Should().NotBeNull();
        built!.Disposed.Should().BeTrue();
        given.Disposed.Should().BeFalse();
    }

    private static ProviderResult Flagged(double probability) =>
        ScriptedProvider.Success(
            new BooleanValue(probability >= 0.5),
            ("true", probability),
            ("false", 1 - probability));

    // One Boolean rule, Warn at 0.6 / Deny at 0.9 on probability, on the one provider named.
    private static Policy Define(string id, string provider) =>
        Policy.Define(id)
            .Enforce()
            .Rule(Policy.Rule("r").Boolean("question-a").WhenTrue(Verdict.Warn, Verdict.Deny))
            .Using(provider, b => b.WarnAboveProbability(0.6).DenyAboveProbability(0.9))
            .OnFailure(FailureBehavior.Deny)
            .Build();

    // An adapter with something to release: answers as the scripted provider does, and records whether
    // it was disposed, by either route the container may take.
    private sealed class DisposableProvider(string id) : IDecisionProvider, IDisposable, IAsyncDisposable
    {
        private readonly ScriptedProvider _inner = new ScriptedProvider(id).Returns(Flagged(0.95));

        public string Id => id;

        public ProviderCapabilities Capabilities => _inner.Capabilities;

        public bool Disposed { get; private set; }

        public Task<ProviderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
            _inner.DecideAsync(request, cancellationToken);

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
