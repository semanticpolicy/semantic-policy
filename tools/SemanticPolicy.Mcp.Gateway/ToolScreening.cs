using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
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
    // What the dialog shows in place of a tool name that is not a plain identifier.
    private const string _unshownName = "(not shown: the server gave it a name that is not a plain identifier)";

    // Guards both maps, so a call's admission and a page's publication happen in one order.
    private readonly Lock _lock = new();
    private readonly Lock _logLock = new();

    // One upstream list at a time, from sending it to publishing its page, so pages publish in the order the upstream
    // answered them. Two answers read back to back would otherwise publish in whichever order their handlers resume.
    private readonly SemaphoreSlim _listing = new(1, 1);
    private readonly Dictionary<Definition, Task<MappedAction>> _verdicts = [];

    // The definition each name was most recently listed with: what names a tool in a result's context, and what admits a
    // call to it.
    private readonly Dictionary<string, Listed> _current = new(StringComparer.Ordinal);

    private long _lastAskId;

    public McpRequestHandler<ListToolsRequestParams, ListToolsResult> List(McpRequestHandler<ListToolsRequestParams, ListToolsResult> forward) =>
        async (request, cancellationToken) =>
        {
            ListToolsResult page;
            Task<MappedAction>?[] verdicts;
            await _listing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                page = await forward(request, cancellationToken).ConfigureAwait(false);
                verdicts = Publish(page.Tools, CorrelationId(request));
            }
            finally
            {
                _listing.Release();
            }

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

            if (composition.Results is not { } point || name is null)
            {
                return await forward(request, cancellationToken).ConfigureAwait(false);
            }

            CallToolResult result;
            try
            {
                result = await forward(request, cancellationToken).ConfigureAwait(false);
            }
            catch (McpProtocolException error)
            {
                // An error answering the call carries text a host may hand the model as it would a result's.
                if (await ScreenErrorAsync(request.Server, point, name, error, CorrelationId(request), cancellationToken).ConfigureAwait(false) is { } replaced)
                {
                    throw replaced;
                }

                throw;
            }

            return await ScreenResultAsync(request.Server, point, name, result, CorrelationId(request), cancellationToken).ConfigureAwait(false);
        };

    // The host's JSON-RPC id, so a line and a span join the host's own log of the request. A blank one, which the
    // protocol allows, is kept as it is.
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
                        verdict = Task.Run(() => ScreenDefinitionAsync(point, definition, tool, correlationId), CancellationToken.None);
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

    // Runs on the gateway's lifetime, never on a list's token, so a list the host cancels leaves it to finish. An
    // evaluation that throws leaves the cache, so the next list that holds the definition evaluates it again instead of
    // failing on the same exception. The removal waits for the lock Publish holds, so the entry is in the cache by then.
    private async Task<MappedAction> ScreenDefinitionAsync(GatewayPoint point, Definition definition, Tool tool, string correlationId)
    {
        try
        {
            SemanticContext context = new([ToolPart(tool.Name, tool.Description), ContextPart.Json("input_schema", tool.InputSchema)], correlationId);
            (PolicyVerdict verdict, TimeSpan latency) = await EvaluateAsync(point, context, lifetime).ConfigureAwait(false);
            MappedAction action = Act(point, verdict.Effective);
            Log("definition", point, tool.Name, verdict, Name(action.Action), latency, correlationId, unscreened: null);
            return action;
        }
        catch (Exception)
        {
            lock (_lock)
            {
                _verdicts.Remove(definition);
            }

            throw;
        }
    }

    private static bool Hides(MappedAction action) => action.Action switch
    {
        GatewayAction.Pass => false,
        GatewayAction.Hide => true,
        _ => throw new UnreachableException($"The gateway file gave a definition the action {action.Action}."),
    };

    private async Task<CallToolResult> ScreenResultAsync(
        McpServer host,
        GatewayPoint point,
        string name,
        CallToolResult result,
        string correlationId,
        CancellationToken cancellationToken)
    {
        (ContextPart? part, bool unscreened) = Screenable(result);
        if (part is null)
        {
            Log("result", point, name, verdict: null, Name(GatewayAction.Pass), latency: null, correlationId, unscreened);
            return result;
        }

        MappedAction action = await ScreenAsync(host, point, name, part, correlationId, unscreened, cancellationToken).ConfigureAwait(false);
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

    // Null lets the error go on as it is. Otherwise the error the host receives instead: it stays an error with the
    // upstream's code, and an action changes only its text, as it does a result's. Withheld, nothing of the upstream's
    // error goes on but its code.
    private async Task<McpProtocolException?> ScreenErrorAsync(
        McpServer host,
        GatewayPoint point,
        string name,
        McpProtocolException error,
        string correlationId,
        CancellationToken cancellationToken)
    {
        MappedAction action = await ScreenAsync(host, point, name, Screenable(error), correlationId, unscreened: false, cancellationToken).ConfigureAwait(false);
        switch (action.Action)
        {
            case GatewayAction.Pass:
                return null;
            case GatewayAction.Annotate:
                McpProtocolException annotated = new($"{action.Message}\n{error.Message}", error, error.ErrorCode);
                foreach (DictionaryEntry entry in error.Data)
                {
                    annotated.Data[entry.Key] = entry.Value;
                }

                return annotated;
            case GatewayAction.Withhold:
                return new McpProtocolException(action.Message!, error.ErrorCode);
            default:
                throw new UnreachableException($"The gateway file gave a result the action {action.Action}.");
        }
    }

    // Evaluates one result on the result point, under the tool its name's current definition describes, settles an ask
    // with the person, and writes the line. What it returns is never an ask.
    private async Task<MappedAction> ScreenAsync(
        McpServer host,
        GatewayPoint point,
        string name,
        ContextPart result,
        string correlationId,
        bool unscreened,
        CancellationToken cancellationToken)
    {
        string? description;
        lock (_lock)
        {
            description = _current.GetValueOrDefault(name)?.Description;
        }

        SemanticContext context = new([ToolPart(name, description), result], correlationId);
        (PolicyVerdict verdict, TimeSpan latency) = await EvaluateAsync(point, context, cancellationToken).ConfigureAwait(false);
        MappedAction action = Act(point, verdict.Effective);
        void Settle(string done) => Log("result", point, name, verdict, done, latency, correlationId, unscreened);
        if (action.Action != GatewayAction.Ask)
        {
            Settle(Name(action.Action));
            return action;
        }

        try
        {
            (MappedAction taken, string done) = await AskAsync(host, name, action, cancellationToken).ConfigureAwait(false);
            Settle(done);
            return taken;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The call was cancelled while the person was being asked. The verdict was reached, so it has its line.
            Settle("ask:abandoned");
            throw;
        }
    }

    // Only the person's explicit accept lets a flagged result through. A decline, a cancel, an answer the protocol does
    // not define or a failed request withholds it, and none of them falls back to the mapping. The dialog carries the
    // operator's text and the tool's name and never the result: a server's text in a dialog the person trusts as the
    // host's would be the server talking the person into accepting it. The name is the server's text too, so it is shown
    // only when it is a plain identifier. Of the answer only its action is read; whatever the person entered is left
    // unread.
    //
    // A host that cannot show a form gets the fallback and is never sent a request. The SDK reads an elicitation
    // capability that names no mode, as a host on this revision declares it, as form.
    //
    // A call the host cancels while the person is being asked withdraws the question. The SDK stops waiting for a
    // cancelled request without telling the host, which would keep the dialog up for an answer nobody reads, so the
    // request goes under an id of the gateway's own and the gateway tells the host; were the SDK to start telling it
    // too, the protocol has the host ignore the second notice.
    private async Task<(MappedAction Taken, string Done)> AskAsync(
        McpServer host,
        string name,
        MappedAction ask,
        CancellationToken cancellationToken)
    {
        if (host.ClientCapabilities?.Elicitation?.Form is null)
        {
            MappedAction fallback = ask.Fallback!;
            return (fallback, $"ask:fallback:{Name(fallback.Action)}");
        }

        RequestId id = new($"gateway-ask-{Interlocked.Increment(ref _lastAskId)}");
        string answer;
        try
        {
            ElicitResult result = await host.SendRequestAsync<ElicitRequestParams, ElicitResult>(
                RequestMethods.ElicitationCreate,
                new ElicitRequestParams
                {
                    Message = $"{ask.Message}\n\nTool: {(IsPlainName(name) ? name : _unshownName)}",

                    // A yes or no needs no field, and Claude Code shows a form with none as a plain accept or decline.
                    RequestedSchema = new ElicitRequestParams.RequestSchema(),
                },
                requestId: id,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            answer = result.Action is "accept" or "decline" or "cancel" ? result.Action : "failed";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await host.SendNotificationAsync(
                    NotificationMethods.CancelledNotification,
                    new CancelledNotificationParams { RequestId = id },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A host that has gone cannot be told, and has no dialog left to close.
            }

            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            answer = "failed";
        }

        MappedAction taken = answer == "accept"
            ? new MappedAction(GatewayAction.Pass, null)
            : new MappedAction(GatewayAction.Withhold, ask.Withheld);
        return (taken, $"ask:{answer}");
    }

    // A tool name as the protocol's 2025-11-25 revision recommends one: 1 to 128 ASCII letters, digits, '_', '-' and '.'.
    // No line break, no space and no length to carry a sentence.
    private static bool IsPlainName(string name) =>
        name.Length is >= 1 and <= 128 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    // The tool part a ToolCall's context carries: the name and, only when there is one, the description. It is built
    // here because a ToolCall refuses a blank name, which an upstream can list and a host can call.
    private static ContextPart ToolPart(string name, string? description)
    {
        JsonObject tool = new() { ["name"] = name };
        if (!string.IsNullOrEmpty(description))
        {
            tool["description"] = description;
        }

        return ContextPart.Json("tool", JsonSerializer.SerializeToElement(tool));
    }

    // What the host receives of an error besides its code: the message alone, or, when the error carries data, the
    // message and the data's string-keyed entries, the only ones the server sends on, as one JSON object.
    private static ContextPart Screenable(McpProtocolException error)
    {
        Dictionary<string, object?> data = [];
        foreach (DictionaryEntry entry in error.Data)
        {
            if (entry.Key is string key)
            {
                data[key] = entry.Value;
            }
        }

        return data.Count == 0
            ? ContextPart.Text("result", error.Message)
            : ContextPart.Json("result", JsonSerializer.SerializeToElement(new { message = error.Message, data }, JsonSerializerOptions.Web));
    }

    // Every text the model may read, in order: the text blocks and the text of embedded resources. Structured content
    // stands in only when there is no text, because a result with both carries the same data twice. Anything else is not
    // screened, and the line says the result held it.
    private static (ContextPart? Part, bool Unscreened) Screenable(CallToolResult result)
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
            return (ContextPart.Text("result", string.Join('\n', texts)), unscreened);
        }

        return result.StructuredContent is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } structured
            ? (ContextPart.Json("result", structured), unscreened)
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
    // description, a schema, the rule's question or anything a person entered in answer to an ask.
    private void Log(
        string at,
        GatewayPoint point,
        string tool,
        PolicyVerdict? verdict,
        string action,
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

            line.WriteString("action", action);
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
