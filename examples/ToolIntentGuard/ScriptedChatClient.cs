using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ToolIntentGuard;

/// <summary>
/// The "model" this demo runs on: for each request it knows, one fixed tool call and one fixed answer
/// once that call has a result. It reaches no network and holds no model. A real model cannot be made
/// to propose a destructive call on a benign request often enough to demonstrate anything, so the model
/// is the scripted half here and the policy is the real one.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    // The whole script: the request, the call proposed for it, and what is said once the call has a
    // result. Data, so that adding a scenario is a row rather than a branch.
    private static readonly (string Request, string CallId, string Tool, string? Name, string Answer)[] _script =
    [
        (
            "Check the repository status.",
            "call-status",
            "delete_repository",
            null,
            "I was not able to report the repository's status."),
        (
            "Delete branch test-old.",
            "call-branch",
            "delete_branch",
            "test-old",
            "Branch test-old is deleted."),
    ];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatMessage[] conversation = [.. messages];
        (string _, string callId, string tool, string? name, string answer) = Scripted(conversation);

        // The loop calls back with the function's result appended, and that is the only thing that tells
        // the second turn from the first: the last user message is the same in both.
        bool called = conversation
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Any();

        IList<AIContent> contents = called
            ? [new TextContent(answer)]
            : [new FunctionCallContent(callId, tool, Arguments(name))];

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatMessage message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
    }

    private static (string Request, string CallId, string Tool, string? Name, string Answer) Scripted(
        IReadOnlyList<ChatMessage> messages)
    {
        string request = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        foreach ((string Request, string CallId, string Tool, string? Name, string Answer) turn in _script)
        {
            if (string.Equals(turn.Request, request, StringComparison.Ordinal))
            {
                return turn;
            }
        }

        throw new InvalidOperationException("The scripted client has no turn for that request.");
    }

    private static IDictionary<string, object?>? Arguments(string? name) =>
        name is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal) { ["name"] = name };
}
