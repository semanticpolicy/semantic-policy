using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// An upstream a test scripts request by request: each tools/list is answered with the next list the test queued, as one
// page, and each tools/call with the result the test set. It signals every list it served and every call that reached
// it, and can hold the calls on a barrier, so a test orders the gateway's work without sleeping.
internal sealed class ScriptedUpstream
{
    private readonly Lock _lock = new();
    private readonly Queue<Tool[]> _lists = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _listWaiters = [];
    private readonly List<(int Count, TaskCompletionSource Signal)> _callWaiters = [];
    private readonly List<string> _calls = [];
    private int _served;
    private TaskCompletionSource? _barrier;

    // What a call returns, by the tool's name; a new result for every call, so the gateway's changes never carry over.
    public Func<string, CallToolResult> Result { get; set; } = _ => new CallToolResult { Content = [new TextContentBlock { Text = "result-a" }] };

    // The name of each call that reached the upstream, in order.
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public ScriptedUpstream Lists(params Tool[] tools)
    {
        lock (_lock)
        {
            _lists.Enqueue(tools);
        }

        return this;
    }

    // Every call waits here until Open.
    public void HoldCalls()
    {
        lock (_lock)
        {
            _barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void OpenCalls()
    {
        lock (_lock)
        {
            _barrier?.TrySetResult();
            _barrier = null;
        }
    }

    public Task WaitForListsAsync(int count, CancellationToken cancellationToken) => Wait(_listWaiters, () => _served, count, cancellationToken);

    public Task WaitForCallsAsync(int count, CancellationToken cancellationToken) => Wait(_callWaiters, () => _calls.Count, count, cancellationToken);

    public McpServerOptions Options() => new()
    {
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (_, _) =>
            {
                Tool[] tools;
                lock (_lock)
                {
                    tools = _lists.Dequeue();
                    _served++;
                    Signal(_listWaiters, _served);
                }

                return ValueTask.FromResult(new ListToolsResult { Tools = [.. tools] });
            },
            CallToolHandler = async (request, cancellationToken) =>
            {
                string name = request.Params!.Name;
                Task barrier;
                lock (_lock)
                {
                    _calls.Add(name);
                    Signal(_callWaiters, _calls.Count);
                    barrier = _barrier?.Task ?? Task.CompletedTask;
                }

                await barrier.WaitAsync(cancellationToken);
                return Result(name);
            },
        },
    };

    // A tool with the given description and input schema.
    public static Tool Tool(string name, string description, string schema = """{ "type": "object" }""") => new()
    {
        Name = name,
        Description = description,
        InputSchema = ToolsUpstream.Json(schema),
    };

    private Task Wait(List<(int Count, TaskCompletionSource Signal)> waiters, Func<int> reached, int count, CancellationToken cancellationToken)
    {
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (reached() >= count)
            {
                return Task.CompletedTask;
            }

            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((count, signal));
        }

        return signal.Task.WaitAsync(cancellationToken);
    }

    private static void Signal(List<(int Count, TaskCompletionSource Signal)> waiters, int reached) =>
        waiters.RemoveAll(waiter =>
        {
            if (reached < waiter.Count)
            {
                return false;
            }

            waiter.Signal.TrySetResult();
            return true;
        });
}
