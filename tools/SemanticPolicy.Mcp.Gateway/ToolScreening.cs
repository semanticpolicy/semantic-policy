using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SemanticPolicy.Evaluation;

namespace SemanticPolicy.Mcp.Gateway;

// The two screening points over one session's tools: each result a tools/call returns, and each definition a tools/list
// returns. A point acts on the verdict's Effective and never reads the mode, so a Shadow policy is still awaited and
// logged but changes nothing. Every evaluation writes one line of metadata to the log, and no line holds content.
//
// A definition's verdict is kept apart from which definition is current. The verdicts are cached by the whole definition
// and shared while in flight, so two lists of one new definition make one provider call, and an evaluation belongs to
// no request: it runs until it ends, under the policy's budget, or until the gateway stops. Which definition is current
// is set by name as each page arrives, never by an evaluation finishing, so an older verdict landing last cannot undo a
// newer definition. A page is not the whole catalogue: a name missing from one keeps its state.
internal sealed class ToolScreening(GatewayComposition composition, TextWriter log, Action<string>? callWaiting, CancellationToken lifetime)
{
    // A ToolCall needs its arguments, but neither context the gateway builds carries them.
    private static readonly JsonElement _noArguments = JsonSerializer.SerializeToElement(new Dictionary<string, object>());

    // Guards both maps, so a call's admission and a page's publication happen in one order.
    private readonly Lock _lock = new();
    private readonly Lock _logLock = new();
    private readonly Dictionary<Definition, Task<MappedAction>> _verdicts = [];

    // The definition each name was most recently listed with: what names a tool in a result's context, and what admits a
    // call to it.
    private readonly Dictionary<string, Listed> _current = new(StringComparer.Ordinal);

    public McpRequestHandler<ListToolsRequestParams, ListToolsResult> List(McpRequestHandler<ListToolsRequestParams, ListToolsResult> forward) =>
        async (request, cancellationToken) =>
        {
            ListToolsResult page = await forward(request, cancellationToken).ConfigureAwait(false);
            Task<MappedAction>?[] verdicts = Publish(page.Tools, CorrelationId(request));
            if (composition.Definitions is null)
            {
                return page;
            }

            // Cancelling the list ends only this wait; the evaluations go on and decide later calls.
            MappedAction[] actions = await Task.WhenAll(verdicts.Select(verdict => verdict!)).WaitAsync(cancellationToken).ConfigureAwait(false);
            page.Tools = [.. page.Tools.Where((_, index) => !Hides(actions[index]))];
            return page;
        };

    public McpRequestHandler<CallToolRequestParams, CallToolResult> Call(McpRequestHandler<CallToolRequestParams, CallToolResult> forward) =>
        async (request, cancellationToken) =>
        {
            string? name = request.Params?.Name;
            if (name is not null && await AdmitAsync(name, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            CallToolResult result = await forward(request, cancellationToken).ConfigureAwait(false);
            return composition.Results is { } point && name is not null
                ? await ScreenResultAsync(point, name, result, CorrelationId(request), cancellationToken).ConfigureAwait(false)
                : result;
        };

    // The host's JSON-RPC id, so a line and a span join the host's own log of the request.
    private static string CorrelationId<TParams>(RequestContext<TParams> request) => request.JsonRpcRequest.Id.ToString();

    // Makes each tool on the page its name's current definition, and returns each one's verdict, started now unless an
    // earlier list already started it. Without a definition point there are no verdicts.
    private Task<MappedAction>?[] Publish(IList<Tool> tools, string correlationId)
    {
        GatewayPoint? point = composition.Definitions;
        Task<MappedAction>?[] verdicts = new Task<MappedAction>?[tools.Count];
        lock (_lock)
        {
            for (int index = 0; index < tools.Count; index++)
            {
                Tool tool = tools[index];
                if (point is not null)
                {
                    Definition definition = new(tool.Name, tool.Description, tool.InputSchema.GetRawText());
                    if (!_verdicts.TryGetValue(definition, out Task<MappedAction>? verdict))
                    {
                        verdict = Task.Run(() => ScreenDefinitionAsync(point, tool, correlationId), CancellationToken.None);
                        _verdicts.Add(definition, verdict);
                    }

                    verdicts[index] = verdict;
                }

                _current[tool.Name] = new Listed(tool.Description, verdicts[index]);
            }
        }

        return verdicts;
    }

    // Null admits the call. A call waits for the verdict of its name's current definition, and is admitted only on the
    // verdict of a definition that is still current once the wait ends; when a newer one was published meanwhile, it
    // waits for that one's instead. A name never listed, or listed with no definition point, is admitted at once.
    private async Task<CallToolResult?> AdmitAsync(string name, CancellationToken cancellationToken)
    {
        while (true)
        {
            Listed? listed;
            lock (_lock)
            {
                listed = _current.GetValueOrDefault(name);
            }

            if (listed?.Verdict is not { } verdict)
            {
                return null;
            }

            if (!verdict.IsCompleted)
            {
                callWaiting?.Invoke(name);
            }

            MappedAction action = await verdict.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (ReferenceEquals(_current[name], listed))
                {
                    return Hides(action) ? new CallToolResult { Content = [new TextContentBlock { Text = action.Message! }], IsError = true } : null;
                }
            }
        }
    }

    // Runs on the gateway's lifetime, never on a list's token, so a list the host cancels leaves it to finish.
    private async Task<MappedAction> ScreenDefinitionAsync(GatewayPoint point, Tool tool, string correlationId)
    {
        SemanticContext call = new ToolCall(tool.Name, tool.Description, _noArguments, [], correlationId).ToSemanticContext();
        SemanticContext context = new(
            [call.Parts.Single(part => part.Name == "tool"), ContextPart.Json("input_schema", tool.InputSchema)],
            correlationId);
        (PolicyVerdict verdict, TimeSpan latency) = await EvaluateAsync(point, context, lifetime).ConfigureAwait(false);
        MappedAction action = Act(point, verdict.Effective);
        Log("definition", point, tool.Name, verdict, action.Action, latency, correlationId, unscreened: null);
        return action;
    }

    private static bool Hides(MappedAction action) => action.Action switch
    {
        GatewayAction.Pass => false,
        GatewayAction.Hide => true,
        _ => throw new UnreachableException($"The gateway file gave a definition the action {action.Action}."),
    };

    private async Task<CallToolResult> ScreenResultAsync(
        GatewayPoint point,
        string name,
        CallToolResult result,
        string correlationId,
        CancellationToken cancellationToken)
    {
        (object? value, bool unscreened) = Screenable(result);
        if (value is null)
        {
            Log("result", point, name, verdict: null, GatewayAction.Pass, latency: null, correlationId, unscreened);
            return result;
        }

        string? description;
        lock (_lock)
        {
            description = _current.GetValueOrDefault(name)?.Description;
        }

        SemanticContext full = new ToolResult(new ToolCall(name, description, _noArguments, [], correlationId), value).ToSemanticContext();
        SemanticContext context = new([.. full.Parts.Where(part => part.Name != "user_request")], full.CorrelationId);
        (PolicyVerdict verdict, TimeSpan latency) = await EvaluateAsync(point, context, cancellationToken).ConfigureAwait(false);
        MappedAction action = Act(point, verdict.Effective);
        Log("result", point, name, verdict, action.Action, latency, correlationId, unscreened);
        switch (action.Action)
        {
            case GatewayAction.Pass:
                return result;
            case GatewayAction.Annotate:
                result.Content = [new TextContentBlock { Text = action.Message! }, .. result.Content];
                return result;
            case GatewayAction.Withhold:
                result.Content = [new TextContentBlock { Text = action.Message! }];
                result.StructuredContent = null;
                result.IsError = true;
                return result;
            default:
                throw new UnreachableException($"The gateway file gave a result the action {action.Action}.");
        }
    }

    // Every text the model may read, in order: the text blocks and the text of embedded resources. Structured content
    // stands in only when there is no text, because a result with both carries the same data twice. Anything else is not
    // screened, and the line says the result held it.
    private static (object? Value, bool Unscreened) Screenable(CallToolResult result)
    {
        List<string> texts = [];
        bool unscreened = false;
        foreach (ContentBlock block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text:
                    texts.Add(text.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    texts.Add(resource.Text);
                    break;
                default:
                    unscreened = true;
                    break;
            }
        }

        if (texts.Count > 0)
        {
            return (string.Join('\n', texts), unscreened);
        }

        return result.StructuredContent is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } structured
            ? (structured, unscreened)
            : (null, unscreened);
    }

    private async Task<(PolicyVerdict Verdict, TimeSpan Latency)> EvaluateAsync(
        GatewayPoint point,
        SemanticContext context,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        PolicyVerdict verdict = await composition.Evaluator.EvaluateAsync(point.Policy.Id, context, cancellationToken).ConfigureAwait(false);
        return (verdict, Stopwatch.GetElapsedTime(started));
    }

    // Allow always passes; every other verdict takes the action the operator mapped it to.
    private static MappedAction Act(GatewayPoint point, Verdict effective) => effective switch
    {
        Verdict.Allow => new MappedAction(GatewayAction.Pass, null),
        Verdict.Warn => point.Mapping.Warn,
        Verdict.Escalate => point.Mapping.Escalate,
        Verdict.Deny => point.Mapping.Deny,
        Verdict.Abstain => point.Mapping.Abstain,
        _ => throw new UnreachableException($"No action is mapped for the verdict {effective}."),
    };

    // One JSON object per line. The tool's name is the only thing in it the upstream wrote: never a result's text, a
    // description, a schema or the rule's question.
    private void Log(
        string at,
        GatewayPoint point,
        string tool,
        PolicyVerdict? verdict,
        GatewayAction action,
        TimeSpan? latency,
        string correlationId,
        bool? unscreened)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter line = new(buffer))
        {
            line.WriteStartObject();
            line.WriteString("point", at);
            line.WriteString("policy", point.Policy.Id);
            line.WriteString("tool", tool);
            if (verdict is not null)
            {
                line.WriteString("effective", Name(verdict.Effective));
                line.WriteString("evaluated", Name(verdict.Evaluated));
            }

            line.WriteString("action", Name(action));
            if (latency is { } elapsed)
            {
                line.WriteNumber("latencyMs", (long)Math.Round(elapsed.TotalMilliseconds));
            }

            line.WriteString("correlationId", correlationId);
            if (unscreened is { } flag)
            {
                line.WriteBoolean("unscreened", flag);
            }

            line.WriteEndObject();
        }

        string text = Encoding.UTF8.GetString(buffer.WrittenSpan);
        lock (_logLock)
        {
            log.WriteLine(text);
        }
    }

    // The names the gateway file and the policy files use.
    private static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    // A whole definition, the cache's key. The schema is compared as the upstream sent it.
    private sealed record Definition(string Name, string? Description, string InputSchema);

    // One publication of a name: compared by reference, so a call can tell whether a newer one replaced it, even one of
    // the same definition.
    private sealed class Listed(string? description, Task<MappedAction>? verdict)
    {
        public string? Description { get; } = description;

        public Task<MappedAction>? Verdict { get; } = verdict;
    }
}
