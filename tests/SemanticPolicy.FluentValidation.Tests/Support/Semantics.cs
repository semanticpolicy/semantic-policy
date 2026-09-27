using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.FluentValidation.Tests.Support;

/// <summary>
/// The policy the tests ask, the scripted answers that reach each of its verdicts, and the runtime an
/// application would register around them.
/// </summary>
internal static class Semantics
{
    public const string PolicyId = "ticket-description";

    /// <summary>
    /// One Boolean rule on the scripted provider: a true answer is Warn from 0.5, Escalate from 0.7 and
    /// Deny from 0.9, and an answer whose probability margin is below 0.2 is too close to call.
    /// </summary>
    public static Policy Ladder(PolicyMode mode = PolicyMode.Enforce, string id = PolicyId)
    {
        PolicyBuilder builder = mode == PolicyMode.Enforce
            ? Policy.Define(id).Enforce()
            : Policy.Define(id).Shadow();
        return builder
            .Rule(Policy.Rule("flagged").Boolean("Is this flagged?").WhenTrue(Verdict.Warn, Verdict.Escalate, Verdict.Deny))
            .Using("scripted", binding => binding
                .WarnAboveProbability(0.5)
                .EscalateAboveProbability(0.7)
                .DenyAboveProbability(0.9)
                .WhenProbabilityMarginBelow(0.2))
            .OnFailure(FailureBehavior.Deny)
            .Build();
    }

    /// <summary>The answer <see cref="Ladder"/> reads as the verdict.</summary>
    public static ProviderResult Answer(Verdict verdict) => verdict switch
    {
        Verdict.Allow => ScriptedDecisionProvider.Boolean(false, 0.05),
        Verdict.Warn => ScriptedDecisionProvider.Boolean(true, 0.65),
        Verdict.Abstain => ScriptedDecisionProvider.Boolean(true, 0.55),
        Verdict.Escalate => ScriptedDecisionProvider.Boolean(true, 0.8),
        Verdict.Deny => ScriptedDecisionProvider.Boolean(true, 0.95),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "No scripted answer reaches it."),
    };

    /// <summary>
    /// The runtime as an application registers it: <c>AddSemanticPolicy()</c> with the provider and the
    /// policies the evaluator serves by id.
    /// </summary>
    public static ServiceProvider Container(ScriptedDecisionProvider provider, params Policy[] policies)
    {
        ServiceCollection services = new();
        ISemanticPolicyBuilder builder = services.AddSemanticPolicy().AddProvider(provider);
        foreach (Policy policy in policies)
        {
            builder.AddPolicy(policy);
        }

        return services.BuildServiceProvider();
    }
}
