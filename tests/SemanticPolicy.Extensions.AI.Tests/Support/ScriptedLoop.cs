using Microsoft.Extensions.AI;
using SemanticPolicy.Providers;

namespace SemanticPolicy.Extensions.AI.Tests.Support;

/// <summary>What a stub tool returns when a test wants a structured result rather than a string.</summary>
/// <param name="BranchName">The branch the call named.</param>
/// <param name="Deleted">Whether the stub says it deleted it.</param>
internal sealed record StubResult(string BranchName, bool Deleted);

/// <summary>
/// A tool the tests hand to the loop: one <c>name</c> argument, a fixed return value, and the
/// argument of every invocation it received. It runs no process and touches nothing, so a test that
/// asserts it was never invoked is asserting about the loop, not about a side effect.
/// </summary>
internal sealed class StubTool
{
    private readonly Lock _lock = new();
    private readonly List<string?> _arguments = [];

    /// <summary>A tool of that name and description, answering every call with the value.</summary>
    /// <param name="tool">The name the model calls it by.</param>
    /// <param name="description">The description the model and the policy see.</param>
    /// <param name="returns">What the function returns; the loop marshals it the way it marshals any function's.</param>
    public StubTool(string tool, string description, object? returns)
    {
        Function = AIFunctionFactory.Create(
            (string? name) =>
            {
                lock (_lock)
                {
                    _arguments.Add(name);
                }

                return returns;
            },
            tool,
            description);
    }

    /// <summary>The function to give the loop.</summary>
    public AIFunction Function { get; }

    /// <summary>The <c>name</c> argument of every invocation, in order.</summary>
    public IReadOnlyList<string?> Arguments
    {
        get
        {
            lock (_lock)
            {
                return [.. _arguments];
            }
        }
    }
}

/// <summary>Builds the request options and the policy the frontend tests run over.</summary>
internal static class ScriptedLoop
{
    /// <summary>The id every scripted policy is registered under.</summary>
    public const string PolicyId = "guard";

    /// <summary>The options of one request, offering the stubs as its tools.</summary>
    public static ChatOptions With(params StubTool[] tools) =>
        new() { Tools = [.. tools.Select(tool => (AITool)tool.Function)] };

    /// <summary>The arguments of a call that names one branch or tag.</summary>
    public static Dictionary<string, object?> Named(string name) =>
        new(StringComparer.Ordinal) { ["name"] = name };

    /// <summary>A provider that answers every question with a true at 0.95, over the Deny rung.</summary>
    public static ScriptedDecisionProvider Flagging() =>
        new ScriptedDecisionProvider().Returns(ScriptedDecisionProvider.Boolean(true, 0.95));

    /// <summary>One Boolean rule on the scripted provider: Deny above 0.9, Deny on a provider failure.</summary>
    public static Policy DenyPolicy(PolicyMode mode = PolicyMode.Enforce)
    {
        PolicyBuilder builder = mode == PolicyMode.Enforce
            ? Policy.Define(PolicyId).Enforce()
            : Policy.Define(PolicyId).Shadow();
        return builder
            .Rule(Policy.Rule("flagged").Boolean("Is this flagged?").WhenTrue(Verdict.Deny))
            .Using("scripted", binding => binding.DenyAboveProbability(0.9))
            .OnFailure(FailureBehavior.Deny)
            .Build();
    }

    /// <summary>An evaluator over the provider and the policy, the explicit road's half.</summary>
    public static PolicyEvaluator Evaluator(ScriptedDecisionProvider provider, Policy? policy = null) =>
        new([new ProviderRegistration("scripted", provider)], [policy ?? DenyPolicy()]);
}
