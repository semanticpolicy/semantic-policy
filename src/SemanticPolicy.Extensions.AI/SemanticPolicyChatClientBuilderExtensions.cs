using Microsoft.Extensions.DependencyInjection;
using SemanticPolicy;
using SemanticPolicy.Guards;

namespace Microsoft.Extensions.AI;

/// <summary>
/// Hangs a policy on a chat client's function-calling loop at two points: before a tool the model
/// proposed runs, and after that tool returns. Every method takes the application's handler, which
/// reads the verdict and decides what happens; the frontend applies what the handler returns and
/// decides nothing of its own, in Shadow and in Enforce alike. A verdict is a probabilistic signal
/// about content and not an authorization, so a guard here is one layer among several rather than the
/// one that holds — <c>SECURITY.md</c> and <c>docs/THREAT_MODEL.md</c> say what that means for what
/// you build on it.
/// </summary>
public static class SemanticPolicyChatClientBuilderExtensions
{
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
    /// <para>
    /// Write the call before <c>UseFunctionInvocation()</c>. At <see cref="ChatClientBuilder.Build(IServiceProvider)"/>
    /// the guard finds the <see cref="FunctionInvokingChatClient"/> below it and wraps its
    /// <see cref="FunctionInvokingChatClient.FunctionInvoker"/>; with no such client below it, the build
    /// fails. The evaluator and the policy are looked up in the container given to the build as well, so
    /// a missing registration fails there and not on the first call.
    /// </para>
    /// <para>
    /// Guards run in the order their calls are written, all of them outside the application's own
    /// invoker. Set that invoker in <c>UseFunctionInvocation(configure: …)</c>, never on the built
    /// client: one assigned after the build replaces the guards, and every call then runs unchecked,
    /// with no error.
    /// </para>
    /// <para>
    /// The handler decides on a verdict that is a probabilistic signal, not an authorization. The guard
    /// awaits it in every mode and applies the outcome, for every call the loop invokes, several in one
    /// response included.
    /// </para>
    /// </remarks>
    public static ChatClientBuilder UseSemanticPolicyBeforeTool(
        this ChatClientBuilder builder,
        string policyId,
        PreToolHandler handler,
        Func<ToolCall, SemanticContext>? context = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentNullException.ThrowIfNull(handler);
        return builder.Use((inner, services) => Wrap(
            inner,
            nameof(UseSemanticPolicyBeforeTool),
            () => new PolicyGuard<ToolCall, PreToolOutcome>(
                GuardSubject.PreTool, Evaluator(services, policyId), policyId, handler.Invoke, context),
            ToolInvocationGuards.BeforeToolAsync));
    }

    /// <summary>
    /// Asks the policy about every tool call the model proposes, before the tool runs, then applies
    /// what the handler returns. The policy and the evaluator are given here, so the client needs no
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
    /// <remarks>
    /// <para>
    /// Write the call before <c>UseFunctionInvocation()</c>. At <see cref="ChatClientBuilder.Build(IServiceProvider)"/>
    /// the guard finds the <see cref="FunctionInvokingChatClient"/> below it and wraps its
    /// <see cref="FunctionInvokingChatClient.FunctionInvoker"/>; with no such client below it, the build
    /// fails.
    /// </para>
    /// <para>
    /// Guards run in the order their calls are written, all of them outside the application's own
    /// invoker. Set that invoker in <c>UseFunctionInvocation(configure: …)</c>, never on the built
    /// client: one assigned after the build replaces the guards, and every call then runs unchecked,
    /// with no error.
    /// </para>
    /// <para>
    /// The handler decides on a verdict that is a probabilistic signal, not an authorization. The guard
    /// awaits it in every mode and applies the outcome, for every call the loop invokes, several in one
    /// response included.
    /// </para>
    /// </remarks>
    public static ChatClientBuilder UseSemanticPolicyBeforeTool(
        this ChatClientBuilder builder,
        Policy policy,
        IPolicyEvaluator evaluator,
        PreToolHandler handler,
        Func<ToolCall, SemanticContext>? context = null)
    {
        Require(builder, policy, evaluator, handler);
        return builder.Use((inner, services) => Wrap(
            inner,
            nameof(UseSemanticPolicyBeforeTool),
            () => new PolicyGuard<ToolCall, PreToolOutcome>(
                GuardSubject.PreTool, evaluator, policy, handler.Invoke, context),
            ToolInvocationGuards.BeforeToolAsync));
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
    /// <remarks>
    /// <para>
    /// Write the call before <c>UseFunctionInvocation()</c>. At <see cref="ChatClientBuilder.Build(IServiceProvider)"/>
    /// the guard finds the <see cref="FunctionInvokingChatClient"/> below it and wraps its
    /// <see cref="FunctionInvokingChatClient.FunctionInvoker"/>; with no such client below it, the build
    /// fails. The evaluator and the policy are looked up in the container given to the build as well, so
    /// a missing registration fails there and not on the first call.
    /// </para>
    /// <para>
    /// Guards run in the order their calls are written, all of them outside the application's own
    /// invoker, so this one checks what that invoker returned. Set that invoker in
    /// <c>UseFunctionInvocation(configure: …)</c>, never on the built client: one assigned after the
    /// build replaces the guards, and every call then runs unchecked, with no error.
    /// </para>
    /// <para>
    /// The handler decides on a verdict that is a probabilistic signal, not an authorization. The guard
    /// awaits it in every mode and applies the outcome, for every result the loop receives.
    /// </para>
    /// </remarks>
    public static ChatClientBuilder UseSemanticPolicyAfterTool(
        this ChatClientBuilder builder,
        string policyId,
        PostToolHandler handler,
        Func<ToolResult, SemanticContext>? context = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        ArgumentNullException.ThrowIfNull(handler);
        return builder.Use((inner, services) => Wrap(
            inner,
            nameof(UseSemanticPolicyAfterTool),
            () => new PolicyGuard<ToolResult, PostToolOutcome>(
                GuardSubject.PostTool, Evaluator(services, policyId), policyId, handler.Invoke, context),
            ToolInvocationGuards.AfterToolAsync));
    }

    /// <summary>
    /// Asks the policy about every tool's result, before the model sees it, then applies what the
    /// handler returns. The policy and the evaluator are given here, so the client needs no container.
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
    /// <remarks>
    /// <para>
    /// Write the call before <c>UseFunctionInvocation()</c>. At <see cref="ChatClientBuilder.Build(IServiceProvider)"/>
    /// the guard finds the <see cref="FunctionInvokingChatClient"/> below it and wraps its
    /// <see cref="FunctionInvokingChatClient.FunctionInvoker"/>; with no such client below it, the build
    /// fails.
    /// </para>
    /// <para>
    /// Guards run in the order their calls are written, all of them outside the application's own
    /// invoker, so this one checks what that invoker returned. Set that invoker in
    /// <c>UseFunctionInvocation(configure: …)</c>, never on the built client: one assigned after the
    /// build replaces the guards, and every call then runs unchecked, with no error.
    /// </para>
    /// <para>
    /// The handler decides on a verdict that is a probabilistic signal, not an authorization. The guard
    /// awaits it in every mode and applies the outcome, for every result the loop receives.
    /// </para>
    /// </remarks>
    public static ChatClientBuilder UseSemanticPolicyAfterTool(
        this ChatClientBuilder builder,
        Policy policy,
        IPolicyEvaluator evaluator,
        PostToolHandler handler,
        Func<ToolResult, SemanticContext>? context = null)
    {
        Require(builder, policy, evaluator, handler);
        return builder.Use((inner, services) => Wrap(
            inner,
            nameof(UseSemanticPolicyAfterTool),
            () => new PolicyGuard<ToolResult, PostToolOutcome>(
                GuardSubject.PostTool, evaluator, policy, handler.Invoke, context),
            ToolInvocationGuards.AfterToolAsync));
    }

    private static void Require(ChatClientBuilder builder, Policy policy, IPolicyEvaluator evaluator, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(handler);
    }

    // Runs inside the builder's factory, so at Build. Factories run from the last added to the first,
    // which means the function-invoking client below has been built and configured by now, and every
    // guard written after this one has already wrapped its invoker: wrapping what is there keeps the
    // guards in the order written and the application's own invoker innermost.
    private static IChatClient Wrap<TSubject, TOutcome>(
        IChatClient inner,
        string method,
        Func<PolicyGuard<TSubject, TOutcome>> createGuard,
        Func<FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, PolicyGuard<TSubject, TOutcome>, CancellationToken, ValueTask<object?>> stage)
    {
        FunctionInvokingChatClient loop = inner.GetService<FunctionInvokingChatClient>()
            ?? throw new InvalidOperationException(
                $"{method} found no function-invoking client below it: call it before UseFunctionInvocation() "
                + "on the same builder, so the loop it guards is already there.");

        PolicyGuard<TSubject, TOutcome> guard = createGuard();
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next = loop.FunctionInvoker ?? InvokeFunction;
        loop.FunctionInvoker = (invocation, cancellationToken) => stage(invocation, next, guard, cancellationToken);
        return inner;
    }

    // What the loop runs when no invoker is set.
    private static ValueTask<object?> InvokeFunction(FunctionInvocationContext invocation, CancellationToken cancellationToken) =>
        invocation.Function.InvokeAsync(invocation.Arguments, cancellationToken);

    // The by-id road's whole lookup, run inside the builder's factory and therefore at Build: the
    // evaluator has to be in the container the client was built with, and the id has to name a policy
    // registered beside it. Both are configuration mistakes, and a configuration mistake surfaces
    // better at startup than inside the function-calling loop, which turns an exception into a result
    // the model reads.
    private static IPolicyEvaluator Evaluator(IServiceProvider services, string policyId)
    {
        IPolicyEvaluator evaluator = services.GetService<IPolicyEvaluator>()
            ?? throw new InvalidOperationException(
                "A policy named by its id needs an IPolicyEvaluator in the container: register one with "
                + "AddSemanticPolicy() and build the client with Build(services).");

        bool registered = services.GetServices<Policy>()
            .Any(policy => string.Equals(policy.Id, policyId, StringComparison.Ordinal));

        return registered
            ? evaluator
            : throw new ArgumentException($"No policy in the container carries the id '{policyId}'.", nameof(policyId));
    }
}
