using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SemanticPolicy.AgentFramework;

/// <summary>
/// The two tool points, on the Agent Framework's function-calling middleware. Every call the loop
/// makes passes through, including several proposed in one iteration; there is no filter and no
/// allow-list, so a call the application wants to let by is one its handler proceeds on.
/// </summary>
internal static class FunctionInvocationMiddleware
{
    /// <summary>Wraps the agent so the guard reads each proposed call before the tool runs.</summary>
    /// <param name="inner">The agent whose function-calling loop is guarded.</param>
    /// <param name="services">The container the pipeline was built with, passed on to the inner stages.</param>
    /// <param name="guard">The policy, the handler and the context construction for this point.</param>
    public static AIAgent BeforeTool(AIAgent inner, IServiceProvider? services, PolicyGuard<ToolCall, PreToolOutcome> guard) =>
        new AIAgentBuilder(inner)
            .Use(async (agent, context, next, cancellationToken) =>
            {
                (_, PreToolOutcome outcome) = await guard.GuardAsync(Call(context), cancellationToken).ConfigureAwait(false);
                return outcome.Kind switch
                {
                    // A refusal is the tool's result and nothing more: the model sees it and is free to
                    // try something else, which is the difference from a stop.
                    PreToolOutcomeKind.Refuse => outcome.Message,
                    PreToolOutcomeKind.Stop => Terminate(context, outcome.Message),
                    _ => await next(context, cancellationToken).ConfigureAwait(false),
                };
            })
            .Build(services);

    /// <summary>Wraps the agent so the guard reads each tool's result before the model does.</summary>
    /// <param name="inner">The agent whose function-calling loop is guarded.</param>
    /// <param name="services">The container the pipeline was built with, passed on to the inner stages.</param>
    /// <param name="guard">The policy, the handler and the context construction for this point.</param>
    public static AIAgent AfterTool(AIAgent inner, IServiceProvider? services, PolicyGuard<ToolResult, PostToolOutcome> guard) =>
        new AIAgentBuilder(inner)
            .Use(async (agent, context, next, cancellationToken) =>
            {
                object? value = await next(context, cancellationToken).ConfigureAwait(false);
                ToolResult result = new(Call(context), value);
                (_, PostToolOutcome outcome) = await guard.GuardAsync(result, cancellationToken).ConfigureAwait(false);
                return outcome.Kind switch
                {
                    PostToolOutcomeKind.Replace => outcome.Result,
                    PostToolOutcomeKind.Stop => Terminate(context, outcome.Message),
                    _ => value,
                };
            })
            .Build(services);

    // The call as the model proposed it. The arguments are serialized with the same options the
    // function-calling loop binds them with, so a policy reads the JSON the model produced rather than
    // the CLR objects the loop happened to bind it to.
    private static ToolCall Call(FunctionInvocationContext context) =>
        new(
            context.Function.Name,
            context.Function.Description,
            JsonSerializer.SerializeToElement(context.Arguments, AIJsonUtilities.DefaultOptions),
            [.. context.Messages.Select(message => new ConversationMessage(message.Role.Value, message.Text))],
            context.CallContent.CallId);

    // Ending the loop is a property on the context, not a return value, so the message still has to be
    // returned as the call's result for the model to see it in the response.
    private static object? Terminate(FunctionInvocationContext context, string? message)
    {
        context.Terminate = true;
        return message;
    }
}
