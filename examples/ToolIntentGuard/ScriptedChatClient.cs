using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ToolIntentGuard;

/// <summary>
/// The "model" this demo runs on: it proposes the one tool call it was built with, then answers with
/// whatever that call returned. It reaches no network and holds no model. A real model asked to delete
/// branch test-old proposes test-old, so the wrong branch has to be scripted: the model is the scripted
/// half here and the policy is the real one.
/// </summary>
internal sealed class ScriptedChatClient(string tool, IReadOnlyDictionary<string, object?> arguments) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The loop calls back with the function's result appended, and that is the only thing that tells
        // the second turn from the first: the user's message is the same in both.
        FunctionResultContent? result = messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .LastOrDefault();

        AIContent content = result is null
            ? new FunctionCallContent("call-1", tool, new Dictionary<string, object?>(arguments))
            : new TextContent(Text(result.Result));

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [content])));
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

    // What the tool returned arrives as a JSON string element; what a handler put in its place arrives as
    // the handler's own string. Either way the answer repeats it word for word.
    private static string Text(object? result) => result switch
    {
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        null => string.Empty,
        _ => result.ToString() ?? string.Empty,
    };
}
