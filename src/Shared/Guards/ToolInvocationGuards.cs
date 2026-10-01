using Microsoft.Extensions.AI;

namespace SemanticPolicy.Guards;

/// <summary>
/// The two tool points around one function invocation: the guard reads the call, or the result, and
/// the handler's outcome decides what the loop sees. Every call the loop invokes passes through,
/// including several proposed in one iteration; there is no filter and no allow-list, so a call the
/// application wants to let by is one its handler proceeds on. An exception the evaluator or the
/// handler throws is not caught here.
/// </summary>
/// <remarks>
/// Compiled into each integration package as a linked file rather than shipped as a public type, so the
/// integrations share one implementation without an API of their own to version.
/// </remarks>
internal static class ToolInvocationGuards
{
    /// <summary>
    /// Guards one call before the tool runs. <see cref="PreToolOutcomeKind.Refuse"/> returns the
    /// outcome's message as the call's result without invoking <paramref name="next"/>,
    /// <see cref="PreToolOutcomeKind.Stop"/> does the same and ends the loop, and anything else invokes
    /// <paramref name="next"/> and returns what it returns.
    /// </summary>
    /// <param name="context">The invocation, as the function-calling loop hands it over.</param>
    /// <param name="next">The rest of the invocation: the next stage, or the function itself.</param>
    /// <param name="guard">The policy, the handler and the context construction for this point.</param>
    /// <param name="cancellationToken">The caller's token, passed to the guard and to <paramref name="next"/>.</param>
    /// <returns>What the model sees as the call's result.</returns>
    public static async ValueTask<object?> BeforeToolAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        PolicyGuard<ToolCall, PreToolOutcome> guard,
        CancellationToken cancellationToken)
    {
        (_, PreToolOutcome outcome) = await guard.GuardAsync(context.ToToolCall(), cancellationToken).ConfigureAwait(false);
        return outcome.Kind switch
        {
            // A refusal is the tool's result and nothing more: the model sees it and is free to
            // try something else, which is the difference from a stop.
            PreToolOutcomeKind.Refuse => outcome.Message,
            PreToolOutcomeKind.Stop => Terminate(context, outcome.Message),
            _ => await next(context, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Guards one tool's result before the model sees it. <paramref name="next"/> runs first; then
    /// <see cref="PostToolOutcomeKind.Replace"/> returns the outcome's result in its place,
    /// <see cref="PostToolOutcomeKind.Stop"/> returns the outcome's message and ends the loop, and
    /// anything else returns the value as the tool produced it.
    /// </summary>
    /// <param name="context">The invocation, as the function-calling loop hands it over.</param>
    /// <param name="next">The rest of the invocation: the next stage, or the function itself.</param>
    /// <param name="guard">The policy, the handler and the context construction for this point.</param>
    /// <param name="cancellationToken">The caller's token, passed to <paramref name="next"/> and to the guard.</param>
    /// <returns>What the model sees as the call's result.</returns>
    public static async ValueTask<object?> AfterToolAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        PolicyGuard<ToolResult, PostToolOutcome> guard,
        CancellationToken cancellationToken)
    {
        object? value = await next(context, cancellationToken).ConfigureAwait(false);
        ToolResult result = new(context.ToToolCall(), value);
        (_, PostToolOutcome outcome) = await guard.GuardAsync(result, cancellationToken).ConfigureAwait(false);
        return outcome.Kind switch
        {
            PostToolOutcomeKind.Replace => outcome.Result,
            PostToolOutcomeKind.Stop => Terminate(context, outcome.Message),
            _ => value,
        };
    }

    // Ending the loop is a property on the context, not a return value, so the message still has to be
    // returned as the call's result for the model to see it in the response.
    private static object? Terminate(FunctionInvocationContext context, string? message)
    {
        context.Terminate = true;
        return message;
    }
}
