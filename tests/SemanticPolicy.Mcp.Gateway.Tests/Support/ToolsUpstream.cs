using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SemanticPolicy.Mcp.Gateway.Tests.Support;

// An upstream that lists its tools in fixed pages and answers each call from a fixed table, keeping every cursor and
// every call that reached it. Page n+1 is reached with the cursor page n returned.
internal sealed class ToolsUpstream(params Tool[][] pages)
{
    public const string TraceKey = "com.example/trace";

    // A tool whose result is an object that matches its output schema.
    public static Tool Lookup { get; } = new()
    {
        Name = "lookup_order",
        Title = "Order lookup",
        Description = "description-a",
        InputSchema = Json("""{ "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }"""),
        OutputSchema = Json("""{ "type": "object", "properties": { "state": { "type": "string" } }, "required": ["state"] }"""),
    };

    // A tool whose result is an error.
    public static Tool Archive { get; } = new()
    {
        Name = "archive_order",
        Description = "description-b",
        InputSchema = Json("""{ "type": "object", "properties": { "id": { "type": "string" } } }"""),
    };

    private readonly Lock _lock = new();
    private readonly List<string?> _cursors = [];
    private readonly List<CallToolRequestParams> _calls = [];

    public IReadOnlyList<Tool[]> Pages { get; } = pages;

    public IReadOnlyList<string?> Cursors
    {
        get
        {
            lock (_lock)
            {
                return [.. _cursors];
            }
        }
    }

    public IReadOnlyList<CallToolRequestParams> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    public static string CursorOf(int page) => $"cursor-page-{page}";

    public static CallToolResult ResultOf(string tool) => tool switch
    {
        "lookup_order" => new CallToolResult
        {
            Content = [new TextContentBlock { Text = "state-a" }],
            StructuredContent = Json("""{ "state": "state-a" }"""),
            IsError = false,
            Meta = new JsonObject { [TraceKey] = "trace-result-a" },
        },
        "archive_order" => new CallToolResult
        {
            Content = [new TextContentBlock { Text = "error-a" }],
            IsError = true,
            Meta = new JsonObject { [TraceKey] = "trace-result-b" },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    public McpServerOptions Options() => new()
    {
        Handlers = new McpServerHandlers
        {
            ListToolsHandler = (request, _) =>
            {
                string? cursor = request.Params?.Cursor;
                lock (_lock)
                {
                    _cursors.Add(cursor);
                }

                int page = cursor is null ? 0 : int.Parse(cursor["cursor-page-".Length..], CultureInfo.InvariantCulture);
                return ValueTask.FromResult(new ListToolsResult
                {
                    Tools = [.. Pages[page]],
                    NextCursor = page + 1 < Pages.Count ? CursorOf(page + 1) : null,
                });
            },
            CallToolHandler = (request, _) =>
            {
                lock (_lock)
                {
                    _calls.Add(request.Params!);
                }

                return ValueTask.FromResult(ResultOf(request.Params!.Name));
            },
        },
    };

    public static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
