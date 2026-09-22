using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SemanticPolicy.AgentFramework;

/// <summary>
/// The pre-model point, on the Agent Framework's agent-run middleware. Both delegates are supplied:
/// given only the non-streaming one the framework derives the streaming run from it, which would hold
/// every update back until the whole response was in.
/// </summary>
internal static class PreModelMiddleware
{
    /// <summary>Wraps the agent so the guard reads each run's input before the model does.</summary>
    /// <param name="inner">The agent the run reaches when the handler proceeds.</param>
    /// <param name="services">The container the pipeline was built with, passed on to the inner stages.</param>
    /// <param name="guard">The policy, the handler and the context construction for this point.</param>
    public static AIAgent Decorate(AIAgent inner, IServiceProvider? services, PolicyGuard<ModelInput, PreModelOutcome> guard) =>
        new AIAgentBuilder(inner)
            .Use(
                (messages, session, options, innerAgent, cancellationToken) =>
                    RunAsync(guard, messages, session, options, innerAgent, cancellationToken),
                (messages, session, options, innerAgent, cancellationToken) =>
                    RunStreamingAsync(guard, messages, session, options, innerAgent, cancellationToken))
            .Build(services);

    private static async Task<AgentResponse> RunAsync(
        PolicyGuard<ModelInput, PreModelOutcome> guard,
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent inner,
        CancellationToken cancellationToken)
    {
        ChatMessage[] run = [.. messages];
        (_, PreModelOutcome outcome) = await guard.GuardAsync(Input(run), cancellationToken).ConfigureAwait(false);
        return outcome.Kind == PreModelOutcomeKind.Stop
            ? new AgentResponse(new ChatMessage(ChatRole.Assistant, Message(outcome)))
            : await inner.RunAsync(run, session, options, cancellationToken).ConfigureAwait(false);
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        PolicyGuard<ModelInput, PreModelOutcome> guard,
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent inner,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ChatMessage[] run = [.. messages];
        (_, PreModelOutcome outcome) = await guard.GuardAsync(Input(run), cancellationToken).ConfigureAwait(false);
        if (outcome.Kind == PreModelOutcomeKind.Stop)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, Message(outcome));
            yield break;
        }

        await foreach (AgentResponseUpdate update in inner.RunStreamingAsync(run, session, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    // The messages as the neutral layer reads them, under an id of this run's own. The id is not
    // derived from anything the run carries, so it identifies the evaluation and says nothing about it.
    private static ModelInput Input(IReadOnlyList<ChatMessage> messages) =>
        new(
            [.. messages.Select(message => new ConversationMessage(message.Role.Value, message.Text))],
            Guid.NewGuid().ToString("N"));

    // A stop carries a non-blank message: the outcome's factory is the only way to make one.
    private static string Message(PreModelOutcome outcome) => outcome.Message!;
}
