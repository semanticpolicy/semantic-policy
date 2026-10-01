using System.Text.Json;
using SemanticPolicy;

namespace Microsoft.Extensions.AI;

/// <summary>
/// Reads a function invocation as the tool call the model proposed, in the shape a pre-tool handler
/// and a policy's default context use. The tool guards build their subject with it; an application
/// that evaluates a policy itself starts from it as well.
/// </summary>
public static class SemanticPolicyFunctionInvocationContextExtensions
{
    /// <summary>
    /// The tool call the model proposed: the function's name and description, the arguments as JSON,
    /// every message of the conversation as its role and its text, and the call's id as the
    /// correlation id.
    /// </summary>
    /// <param name="context">The invocation, as the function-calling loop hands it over.</param>
    /// <returns>The call, with no framework type left in it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The arguments are serialized with <see cref="AIJsonUtilities.DefaultOptions"/>, the options the
    /// function-calling loop binds them with, so a policy reads the JSON the model produced rather than
    /// the objects the loop happened to bind it to. A message carries only its text; content that is not
    /// text, such as an earlier call or an image, is left out.
    /// </para>
    /// <para>
    /// An application that wants a Shadow policy off the critical path, which the tool guards never are
    /// because they await the verdict in every mode, maps the call here, builds the context with
    /// <see cref="ToolCall.ToSemanticContext"/> and calls <see cref="IPolicyEvaluator"/> itself.
    /// </para>
    /// </remarks>
    public static ToolCall ToToolCall(this FunctionInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(
            context.Function.Name,
            context.Function.Description,
            JsonSerializer.SerializeToElement(context.Arguments, AIJsonUtilities.DefaultOptions),
            [.. context.Messages.Select(message => new ConversationMessage(message.Role.Value, message.Text))],
            context.CallContent.CallId);
    }
}
