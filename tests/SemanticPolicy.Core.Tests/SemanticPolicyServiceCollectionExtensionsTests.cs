using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Core.Tests.Support;
using SemanticPolicy.Evaluation;
using SemanticPolicy.Protocol;

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
}
