using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SemanticPolicy.AgentFramework.Tests.Support;

/// <summary>
/// One response the client is scripted to give: the contents it carries, in order. A content with a
/// gate is not streamed until the test completes that gate, which is how a test observes that an
/// update reached the consumer while the rest of the response was still outstanding.
/// </summary>
internal sealed class ScriptedTurn
{
    internal List<(AIContent Content, TaskCompletionSource? Gate)> Updates { get; } = [];

    /// <summary>A turn that answers with text.</summary>
    public static ScriptedTurn Says(string text) => new ScriptedTurn().Then(new TextContent(text));

    /// <summary>A turn that proposes one tool call.</summary>
    public static ScriptedTurn Calls(string callId, string name, IDictionary<string, object?>? arguments = null) =>
        new ScriptedTurn().ThenCalls(callId, name, arguments);

    /// <summary>Adds a content to the turn, optionally behind a gate the streaming path awaits.</summary>
    public ScriptedTurn Then(AIContent content, TaskCompletionSource? gate = null)
    {
        Updates.Add((content, gate));
        return this;
    }

    /// <summary>Adds text to the turn, optionally behind a gate the streaming path awaits.</summary>
    public ScriptedTurn ThenSays(string text, TaskCompletionSource? gate = null) => Then(new TextContent(text), gate);

    /// <summary>Adds another tool call to the turn, so one response proposes several.</summary>
    public ScriptedTurn ThenCalls(string callId, string name, IDictionary<string, object?>? arguments = null) =>
        Then(new FunctionCallContent(callId, name, arguments));
}

/// <summary>Reads what the loop put in front of the model as a function's result.</summary>
internal static class ToolResults
{
    /// <summary>Every function result the messages carry, in order.</summary>
    public static IReadOnlyList<FunctionResultContent> In(IEnumerable<ChatMessage> messages) =>
        [.. messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()];

    /// <summary>
    /// A result as text, whether the loop marshalled it to a JSON element or passed the object through.
    /// </summary>
    public static string? TextOf(object? result) =>
        result switch
        {
            null => null,
            System.Text.Json.JsonElement element =>
                element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString() : element.ToString(),
            _ => result.ToString(),
        };
}

/// <summary>What one call to the client carried, snapshotted at the call.</summary>
/// <param name="Messages">The messages the caller sent, as they were at the call.</param>
/// <param name="Options">The options the caller sent.</param>
internal sealed record ScriptedRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

/// <summary>
/// A chat client double that answers from a queue of scripted turns and keeps every request it
/// received, so the real <see cref="FunctionInvokingChatClient"/> loop runs over it and a test reads
/// what the model was sent. It reaches no network and holds no model.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Lock _lock = new();
    private readonly Queue<ScriptedTurn> _turns = new();
    private readonly List<ScriptedRequest> _requests = [];

    /// <summary>Every request received so far, in arrival order.</summary>
    public IReadOnlyList<ScriptedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Queues the turns, each answering one request in order.</summary>
    public ScriptedChatClient Responds(params ScriptedTurn[] turns)
    {
        lock (_lock)
        {
            foreach (ScriptedTurn turn in turns)
            {
                _turns.Enqueue(turn);
            }
        }

        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ScriptedTurn turn = Next(messages, options);
        List<AIContent> contents = [.. turn.Updates.Select(update => update.Content)];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ScriptedTurn turn = Next(messages, options);
        foreach ((AIContent content, TaskCompletionSource? gate) in turn.Updates)
        {
            if (gate is not null)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            List<AIContent> contents = [content];
            yield return new ChatResponseUpdate(ChatRole.Assistant, contents);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
    }

    // The loop hands the same list over again with the turn and the function results appended, so the
    // record is a copy: a test reading an earlier request must see it as the model did.
    private ScriptedTurn Next(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        lock (_lock)
        {
            _requests.Add(new ScriptedRequest([.. messages], options));
            return _turns.Count > 0
                ? _turns.Dequeue()
                : throw new InvalidOperationException($"The scripted client has no turn left for request {_requests.Count}.");
        }
    }
}
