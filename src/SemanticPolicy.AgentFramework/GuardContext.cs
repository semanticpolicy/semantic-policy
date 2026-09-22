using System.Text.Json;
using System.Text.Json.Nodes;

namespace SemanticPolicy.AgentFramework;

/// <summary>
/// The context the layer asks a policy about when the application supplies none: named parts, under
/// the keys a dataset row for the same point carries. Only what the point is about goes in — the run's
/// input, or the user's request with the tool and its arguments or result — never the whole
/// conversation, because a provider's accuracy drops with context that has nothing to do with the
/// question.
/// </summary>
internal static class GuardContext
{
    /// <summary>One text part, <c>input</c>: the run's input as one text, under the run's id.</summary>
    public static SemanticContext PreModel(ModelInput input) =>
        new([ContextPart.Text("input", input.Text)], input.CorrelationId);

    /// <summary><c>user_request</c>, <c>tool</c> and <c>arguments</c>, in that order, under the call's id.</summary>
    public static SemanticContext PreTool(ToolCall call) =>
        new([UserRequest(call), Tool(call), ContextPart.Json("arguments", call.Arguments)], call.CorrelationId);

    /// <summary><c>user_request</c>, <c>tool</c> and <c>result</c>, in that order, under the call's id.</summary>
    public static SemanticContext PostTool(ToolResult result) =>
        new([UserRequest(result.Call), Tool(result.Call), Result(result.Value)], result.Call.CorrelationId);

    private static ContextPart UserRequest(ToolCall call) => ContextPart.Text("user_request", call.UserRequest);

    // An object with the tool's name and, only when it has one, its description.
    private static ContextPart Tool(ToolCall call)
    {
        JsonObject tool = new() { ["name"] = call.Name };
        if (!string.IsNullOrEmpty(call.Description))
        {
            tool["description"] = call.Description;
        }

        return ContextPart.Json("tool", JsonSerializer.SerializeToElement(tool));
    }

    // A string result stays text, so a text-only provider reads it as it is; JSON stays JSON; null and
    // anything else serialize the way the web defaults do, camel-cased, so a dataset row can carry it.
    private static ContextPart Result(object? value) =>
        value switch
        {
            string text => ContextPart.Text("result", text),
            JsonElement element => ContextPart.Json("result", element),
            _ => ContextPart.Json("result", JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web)),
        };
}
