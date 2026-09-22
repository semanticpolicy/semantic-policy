using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy;
using SemanticPolicy.AgentFramework;

namespace Microsoft.Agents.AI;

/// <summary>
/// Hangs a policy on an agent at one of three points: before the model runs, before a tool the model
/// proposed runs, and after that tool returns. Every method takes the application's handler, which
/// reads the verdict and decides what happens; the adapter applies what the handler returns and
/// decides nothing of its own, in Shadow and in Enforce alike. A verdict is a probabilistic signal
/// about content and not an authorization, so a guard here is one layer among several rather than the
/// one that holds — <c>SECURITY.md</c> and <c>docs/THREAT_MODEL.md</c> say what that means for what
/// you build on it.
/// </summary>
public static class SemanticPolicyAIAgentBuilderExtensions
{
    /// <summary>
    /// Asks the policy registered under <paramref name="policyId"/> about the run's input before the
    /// model sees it, then applies what the handler returns.
    /// </summary>
    /// <param name="builder">The pipeline the guard is added to.</param>
    /// <param name="policyId">The id the policy was registered under with <c>AddPolicy</c>; non-blank.</param>
    /// <param name="handler">What the application does with the verdict.</param>
    /// <param name="context">
    /// Builds the context the policy is asked about, in place of the default: one <c>input</c> part
    /// carrying the run's messages as one text.
    /// </param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="handler"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="policyId"/> is blank.</exception>
    /// <remarks>
    /// The evaluator and the policy are looked up in the container given to
    /// <see cref="AIAgentBuilder.Build(IServiceProvider)"/>, so a missing registration fails there and
    /// not on the first run.
    /// </remarks>
    public static AIAgentBuilder UseSemanticPolicyBeforeModel(
        this AIAgentBuilder builder,
        string policyId,
        PreModelHandler handler,
        Func<ModelInput, SemanticContext>? context = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentNullException.ThrowIfNull(handler);
        return builder.Use((inner, services) => PreModelMiddleware.Decorate(
            inner,
            services,
            new PolicyGuard<ModelInput, PreModelOutcome>(
                GuardSubject.PreModel, Evaluator(services, policyId), policyId, handler.Invoke, context)));
    }

    /// <summary>
    /// Asks the policy about the run's input before the model sees it, then applies what the handler
    /// returns. The policy and the evaluator are given here, so the agent needs no container.
    /// </summary>
    /// <param name="builder">The pipeline the guard is added to.</param>
    /// <param name="policy">The policy to evaluate.</param>
    /// <param name="evaluator">The runtime that evaluates it.</param>
    /// <param name="handler">What the application does with the verdict.</param>
    /// <param name="context">
    /// Builds the context the policy is asked about, in place of the default: one <c>input</c> part
    /// carrying the run's messages as one text.
    /// </param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="context"/> is null.</exception>
    public static AIAgentBuilder UseSemanticPolicyBeforeModel(
        this AIAgentBuilder builder,
        Policy policy,
        IPolicyEvaluator evaluator,
        PreModelHandler handler,
        Func<ModelInput, SemanticContext>? context = null)
    {
        Require(builder, policy, evaluator, handler);
        return builder.Use((inner, services) => PreModelMiddleware.Decorate(
            inner,
            services,
            new PolicyGuard<ModelInput, PreModelOutcome>(
                GuardSubject.PreModel, evaluator, policy, handler.Invoke, context)));
    }

    /// <summary>
    /// Asks the policy registered under <paramref name="policyId"/> about every tool call the model
    /// proposes, before the tool runs, then applies what the handler returns.
    /// </summary>
    /// <param name="builder">The pipeline the guard is added to.</param>
    /// <param name="policyId">The id the policy was registered under with <c>AddPolicy</c>; non-blank.</param>
    /// <param name="handler">What the application does with the verdict.</param>
    /// <param name="context">
    /// Builds the context the policy is asked about, in place of the default: <c>user_request</c>,
    /// <c>tool</c> and <c>arguments</c>.
    /// </param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="handler"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="policyId"/> is blank.</exception>
    /// <remarks>
    /// The agent must run a function-calling loop; the framework says so at once when it does not.
    /// Every call goes to the policy, several in one iteration included, so a call the application
    /// wants to let by is one its handler proceeds on.
    /// </remarks>
    public static AIAgentBuilder UseSemanticPolicyBeforeTool(
        this AIAgentBuilder builder,
        string policyId,
        PreToolHandler handler,
        Func<ToolCall, SemanticContext>? context = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentNullException.ThrowIfNull(handler);
        return builder.Use((inner, services) => FunctionInvocationMiddleware.BeforeTool(
            inner,
            services,
            new PolicyGuard<ToolCall, PreToolOutcome>(
                GuardSubject.PreTool, Evaluator(services, policyId), policyId, handler.Invoke, context)));
    }

    /// <summary>
    /// Asks the policy about every tool call the model proposes, before the tool runs, then applies
    /// what the handler returns. The policy and the evaluator are given here, so the agent needs no
    /// container.
    /// </summary>
    /// <param name="builder">The pipeline the guard is added to.</param>
    /// <param name="policy">The policy to evaluate.</param>
    /// <param name="evaluator">The runtime that evaluates it.</param>
    /// <param name="handler">What the application does with the verdict.</param>
    /// <param name="context">
    /// Builds the context the policy is asked about, in place of the default: <c>user_request</c>,
    /// <c>tool</c> and <c>arguments</c>.
    /// </param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="context"/> is null.</exception>
    public static AIAgentBuilder UseSemanticPolicyBeforeTool(
        this AIAgentBuilder builder,
        Policy policy,
        IPolicyEvaluator evaluator,
        PreToolHandler handler,
        Func<ToolCall, SemanticContext>? context = null)
    {
        Require(builder, policy, evaluator, handler);
        return builder.Use((inner, services) => FunctionInvocationMiddleware.BeforeTool(
            inner,
            services,
            new PolicyGuard<ToolCall, PreToolOutcome>(
                GuardSubject.PreTool, evaluator, policy, handler.Invoke, context)));
    }

    /// <summary>
    /// Asks the policy registered under <paramref name="policyId"/> about every tool's result, before
    /// the model sees it, then applies what the handler returns.
    /// </summary>
    /// <param name="builder">The pipeline the guard is added to.</param>
    /// <param name="policyId">The id the policy was registered under with <c>AddPolicy</c>; non-blank.</param>
    /// <param name="handler">What the application does with the verdict.</param>
    /// <param name="context">
    /// Builds the context the policy is asked about, in place of the default: <c>user_request</c>,
    /// <c>tool</c> and <c>result</c>.
    /// </param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="handler"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="policyId"/> is blank.</exception>
    public static AIAgentBuilder UseSemanticPolicyAfterTool(
        this AIAgentBuilder builder,
        string policyId,
        PostToolHandler handler,
        Func<ToolResult, SemanticContext>? context = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentNullException.ThrowIfNull(handler);
        return builder.Use((inner, services) => FunctionInvocationMiddleware.AfterTool(
            inner,
            services,
            new PolicyGuard<ToolResult, PostToolOutcome>(
                GuardSubject.PostTool, Evaluator(services, policyId), policyId, handler.Invoke, context)));
    }

    /// <summary>
    /// Asks the policy about every tool's result, before the model sees it, then applies what the
    /// handler returns. The policy and the evaluator are given here, so the agent needs no container.
    /// </summary>
    /// <param name="builder">The pipeline the guard is added to.</param>
    /// <param name="policy">The policy to evaluate.</param>
    /// <param name="evaluator">The runtime that evaluates it.</param>
    /// <param name="handler">What the application does with the verdict.</param>
    /// <param name="context">
    /// Builds the context the policy is asked about, in place of the default: <c>user_request</c>,
    /// <c>tool</c> and <c>result</c>.
    /// </param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="context"/> is null.</exception>
    public static AIAgentBuilder UseSemanticPolicyAfterTool(
        this AIAgentBuilder builder,
        Policy policy,
        IPolicyEvaluator evaluator,
        PostToolHandler handler,
        Func<ToolResult, SemanticContext>? context = null)
    {
        Require(builder, policy, evaluator, handler);
        return builder.Use((inner, services) => FunctionInvocationMiddleware.AfterTool(
            inner,
            services,
            new PolicyGuard<ToolResult, PostToolOutcome>(
                GuardSubject.PostTool, evaluator, policy, handler.Invoke, context)));
    }

    private static void Require(AIAgentBuilder builder, Policy policy, IPolicyEvaluator evaluator, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(handler);
    }

    // The by-id road's whole lookup, run inside the pipeline factory and therefore at Build: the
    // evaluator has to be in the container the agent was built with, and the id has to name a policy
    // registered beside it. Both are configuration mistakes, and a configuration mistake surfaces
    // better at startup than inside a function-calling loop, where the framework turns an exception
    // into a result the model reads.
    private static IPolicyEvaluator Evaluator(IServiceProvider? services, string policyId)
    {
        IPolicyEvaluator evaluator = services?.GetService<IPolicyEvaluator>()
            ?? throw new InvalidOperationException(
                "A policy named by its id needs an IPolicyEvaluator in the container: register one with "
                + "AddSemanticPolicy() and build the agent with Build(services).");

        bool registered = services.GetServices<Policy>()
            .Any(policy => string.Equals(policy.Id, policyId, StringComparison.Ordinal));

        return registered
            ? evaluator
            : throw new ArgumentException($"No policy in the container carries the id '{policyId}'.", nameof(policyId));
    }
}
