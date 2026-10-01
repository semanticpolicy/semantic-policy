using Microsoft.Agents.AI;
using SemanticPolicy.Guards;

namespace SemanticPolicy.AgentFramework;

/// <summary>
/// The two tool points, on the Agent Framework's function-calling middleware. Its callback hands over
/// the same invocation context and the same <c>next</c> as any function invoker, so this class only
/// adapts that shape and leaves reading the call and applying the outcome to
/// <see cref="ToolInvocationGuards"/>.
/// </summary>
internal static class FunctionInvocationMiddleware
{
    /// <summary>Wraps the agent so the guard reads each proposed call before the tool runs.</summary>
    /// <param name="inner">The agent whose function-calling loop is guarded.</param>
    /// <param name="services">The container the pipeline was built with, passed on to the inner stages.</param>
    /// <param name="guard">The policy, the handler and the context construction for this point.</param>
    public static AIAgent BeforeTool(AIAgent inner, IServiceProvider? services, PolicyGuard<ToolCall, PreToolOutcome> guard) =>
        new AIAgentBuilder(inner)
            .Use((agent, context, next, cancellationToken) =>
                ToolInvocationGuards.BeforeToolAsync(context, next, guard, cancellationToken))
            .Build(services);

    /// <summary>Wraps the agent so the guard reads each tool's result before the model does.</summary>
    /// <param name="inner">The agent whose function-calling loop is guarded.</param>
    /// <param name="services">The container the pipeline was built with, passed on to the inner stages.</param>
    /// <param name="guard">The policy, the handler and the context construction for this point.</param>
    public static AIAgent AfterTool(AIAgent inner, IServiceProvider? services, PolicyGuard<ToolResult, PostToolOutcome> guard) =>
        new AIAgentBuilder(inner)
            .Use((agent, context, next, cancellationToken) =>
                ToolInvocationGuards.AfterToolAsync(context, next, guard, cancellationToken))
            .Build(services);
}
