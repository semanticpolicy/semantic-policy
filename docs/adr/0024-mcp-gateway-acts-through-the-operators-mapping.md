# 0024. The MCP gateway screens one server's tools and acts only through its operator's mapping

**Status:** Accepted
**Date:** 2026-10-10

## Context

[0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) leaves every verdict to a
handler the application writes, and [0022](0022-guard-types-in-core-and-guards-on-the-function-invoking-loop.md)
puts that handler on any Microsoft.Extensions.AI chat client. Both assume code you own. An MCP host
such as Claude Desktop, Claude Code, Cursor or VS Code is code you do not own: it starts its MCP
servers itself and shows their tools to its model, and the only thing its user controls is the
command line of each server entry.

The tools an `McpClient` from the MCP C# SDK lists are already `AIFunction`s, so an application of
your own guards them with 0022's methods and needs nothing new. A host needs a process between itself
and the server. Building that process, `SemanticPolicy.Mcp.Gateway`, raised seven questions whose
answers bind whoever changes it next:

1. What does the gateway sit in front of, and what is it not?
2. Which protocol revision does it speak, and through which seams of the SDK?
3. What does it screen, and with which context parts?
4. Who decides what happens on a verdict, when there is no application to write a handler?
5. How does a tool's definition stay hidden across paging, refreshes and concurrent calls?
6. Which files configure it, and where do its limits and keys come from?
7. What may it write, and what does the server it starts get?

## Decision

1. **One stdio proxy in front of one stdio server, shipped as a dotnet tool.** The host starts
   `semantic-policy-mcp`, through `dnx` or installed, as the server entry's command, and the gateway
   starts the server from the command line after its `--`. The gateway does no aggregation, tool-name
   prefixing, authentication, routing, rate limiting or description pinning: those are access and
   traffic control, not a semantic signal, and [0001](0001-semantic-decision-runtime-boundary.md)
   leaves them to other systems. A host that wraps two servers has two entries and two gateways. The
   package is `SemanticPolicy.Mcp.Gateway`, under `tools/`, released with the other packages, and it
   does not declare the `McpServer` package type, whose arguments would be the user's own server
   command line.

2. **One MCP revision, `2025-06-18`, on both hops, through the SDK's typed handlers.** The host-side
   server accepts only `2025-06-18`, and the upstream client asks only for it, so "passes unchanged"
   means unchanged: nothing is translated between revisions. What a hop owns is never copied to the
   other: a forwarded request goes upstream under a request id of the gateway's own, without the
   host's revision and capabilities, and only `_meta` keys outside the reserved
   `io.modelcontextprotocol/` prefix pass. The gateway serves every forwarded method through
   `McpServerOptions.Handlers` and registers no `RequestHandlers` entry, because
   `ModelContextProtocol.Core` 2.2.0 throws at construction when one names a method it already
   handles. Of the MCP SDK it references `ModelContextProtocol.Core` only. The upstream client
   declares no client capabilities, so the server's requests to the host (sampling, elicitation,
   roots) are not forwarded.

3. **Two points, results and definitions, at most one policy each, with frozen part names.**
   - A `tools/call` result, or an error a server answers a call with, is screened with the context
     parts `tool` and `result`. `tool` is built as `ToolCall` builds it, from the upstream
     definition's name and description. `result` holds the text blocks and embedded resources' text,
     joined in order, or the structured content as JSON when there is no text. There is no
     `user_request`: the gateway never sees the person's request, and this narrows
     [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) § 3 for the gateway.
   - A definition from `tools/list` is screened with `tool` and `input_schema`, the input schema as
     JSON. The definition subject is internal to the gateway; it becomes a public type in Core when a
     second frontend needs it.
   - Contexts are built from Core's `ToolCall` and `ToolResult`, so a live context and a dataset row
     have one shape. The part names are frozen: renaming one invalidates every labelled set and
     recording.
   - A result is never cut. A context over a provider's limit fails as `RejectedInput`, and the
     policy's `onFailure` gives the verdict.
   - Images, audio, binary resources and resource links in a result pass unread, and the log line
     says so. Resources, resource templates, prompts, completions, the server's `instructions` and a
     call's arguments pass unscreened. A point with no policy is not screened.

4. **The operator's mapping decides, and there is no default.** The handler of
   [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) § 1 becomes a file,
   because a host's user has no code to write it in. On a point with a policy, each of `warn`,
   `escalate`, `deny` and `abstain` names an action, or the gateway does not start and names the
   missing key; `allow` always passes. The gateway acts on `Effective` and logs `Evaluated`, never
   reads the mode, and awaits every verdict, Shadow included. Every text the model or a person sees is
   the operator's.
   - On a result: `pass`; `annotate`, the operator's text as a block in front of the content;
     `withhold`, the content replaced by the operator's message with `isError` set, so the model
     knows at the protocol's level that the output did not reach it; and `ask`.
   - On a definition: `pass` and `hide`. A hidden tool leaves the list, and a call to it gets the
     operator's message as an error result without reaching the server. A definition is never
     rewritten.
   - `ask` is form elicitation (`elicitation/create` at `2025-06-18`), on results only, because a
     server may ask the host during `tools/call` and never during `tools/list`. The dialog shows the
     operator's message and the tool's name, only when the name is a plain identifier, and never the
     result. Only an explicit accept lets the result through; a decline, a dismissal or a failed
     request withholds it, and a call the host cancels meanwhile withdraws the question and ends
     cancelled. Every `ask` names a fallback action, which a host that declared no form elicitation
     gets instead.

5. **A definition is evaluated once per process, and a hidden tool's call never reaches the
   server.**
   - Verdicts are cached in memory, keyed by the name, the description and the input schema. A
     changed definition is evaluated again. The key is never logged and is never a correlation id
     ([0008](0008-telemetry-and-content-logging.md)).
   - Separately, each tool name has a current definition: the one the server listed most recently.
     A `tools/list` page publishes its definitions when it arrives, before their verdicts are in, and
     a finishing evaluation never publishes. Absence from a page changes nothing, because a page is
     not the catalogue.
   - A call waits for its name's current definition's verdict. If a newer definition was published
     while it waited, it waits for that one's instead, so no call is forwarded on a verdict a newer
     definition has replaced. A call already forwarded is not revoked. An evaluation belongs to no
     single request: a host that cancels its list ends only its own wait.

6. **The gateway file names Core's policy JSON and the evaluation tool's providers file.** The
   gateway file holds the path of the providers file and, per point, a policy path and the mapping.
   It is strict: an unknown or repeated property, a missing mapping or an action a point does not
   take stops the gateway before it starts the server, with a message that names the file, the point
   and the key and never a value. Paths resolve against the gateway file's directory, because a host
   chooses the directory a server starts in. Policies are Core's JSON, so a policy measured with the
   evaluation tool runs here unchanged. The providers file is read by one `internal` reader,
   `src/Shared/ProvidersFileReader.cs`, linked into both tools as
   [0017](0017-protocol-v0-servers-over-one-http-binding.md) links shared source, so the two cannot
   drift ([0018](0018-evaluation-tool-reads-providers-from-a-file.md)). Keys come only through the
   variables it names, and every provider the policies bind is built at start. The policy's `budget`
   is the only limit on a check: the providers file's `timeout` is refused, and the adapters keep
   their own timers.

7. **Nothing the gateway writes carries content, and the server gets no provider key.**
   - Each check writes one JSON line of metadata to stderr: the point, the policy, the tool's name,
     both verdicts, the action, the latency, the correlation id and, on results, whether anything
     went unread. The correlation id is the host's JSON-RPC request id, so a line joins the host's
     own log. Nothing else reaches stderr but the gateway's own start-up and failure messages.
   - The evaluator's activity source and meter are exported over OTLP only when an endpoint variable
     is set. The gateway declares no source or meter of its own.
   - The server's stderr is read and dropped, no `ILoggerFactory` reaches an SDK type, and a failure
     message is built only from what the gateway knows itself: the command's name, the step that
     failed and an exit code. A server can print a result, its arguments or a key there, and
     [0008](0008-telemetry-and-content-logging.md) holds for the gateway's whole stderr, not only its
     own lines.
   - The gateway records no content for an operator's dataset. An opt-in to do so is a decision of
     its own, under [0008](0008-telemetry-and-content-logging.md).
   - The server is started with the gateway's environment minus every variable the providers file
     names as holding a key, so a server whose output the gateway treats as untrusted never holds
     the decision provider's key.

## Consequences

- A host's user puts a policy in front of a server with no code, and a mistaken mapping is a
  start-up error, not a surprise on the first result.
- Every tool result waits for its verdict, in Shadow too: one decision-model call per result, and a
  latency the policy's `budget` bounds. On the samples, Jev through OpenRouter took 282 ms at the
  median.
- A host or a server that cannot speak `2025-06-18` cannot use the gateway. Moving to a later
  revision means translating between hops or a revision per hop, and needs a record of its own.
- A server that relies on sampling, elicitation or roots loses them behind the gateway.
- What passes unscreened is no worse off than with no gateway, but it is not screened, and the
  package README has to keep saying so.
- A definition's verdict lasts as long as the process. `hide` on `escalate` would hide a tool until a
  restart after one provider failure, which is why the sample maps `escalate` and `abstain` on
  definitions to `pass`.
- An operator debugging a server runs its command alone; the gateway shows none of its messages.
- `ask` works only where the host declares form elicitation; everywhere else the fallback is what
  happens, so it is mandatory.
- A change to the providers file's format changes both tools at once, and both test projects run
  against it.
- The samples' thresholds are illustrations, so the samples run in Shadow and change nothing for a
  person who only tries them.
- Like every rule here, the gateway is a signal, not a security boundary
  ([0001](0001-semantic-decision-runtime-boundary.md)).

## Alternatives considered

- **A library of MCP server filters instead of a proxy.** Lost because it serves only servers written
  in .NET, and a host's servers are not.
- **Streamable HTTP towards the host.** Lost because HTTP is stateless from `2026-07-28`, so asking
  would need multi-round-trip requests, and the proxy itself would need authentication.
- **An HTTP upstream, with or without OAuth.** Lost for now: with OAuth it is authentication, which
  [0001](0001-semantic-decision-runtime-boundary.md) leaves to other systems, and without it it waits
  for remote servers someone asks for.
- **Aggregating several servers behind one gateway.** Lost because it needs prefixed tool names and a
  routing layer, and a host already wraps each server entry on its own.
- **Pinning descriptions on first use.** Lost because it is a deterministic check, not a semantic
  signal.
- **A fully transparent proxy that also forwards the server's requests to the host.** Lost because
  it doubles the protocol work and collides with the gateway's own `ask`.
- **Screening a call's arguments, `resources/read`, `prompts/get` or `instructions` in the first
  version.** Lost because a call's arguments have nothing to be compared against without the person's
  request, `instructions` are meant to instruct, so the injection question would flag every one, and
  resources need a part and data of their own.
- **`ToolResult`'s default context with an empty `user_request`.** Lost because an empty request may
  push a model towards "nothing here matches the request".
- **The whole `CallToolResult` as JSON in `result`.** Lost because the model would read the
  protocol's structure instead of the content.
- **Cutting a long result into chunks, or keeping its head.** Lost because chunks multiply cost and
  latency on long pages, and an instruction at the end of a page would pass a head.
- **A public `ToolDefinition` in Core.** Lost because it is more API to freeze before a second
  frontend asks for it.
- **Evaluating definitions on every `tools/list`.** Lost because a refresh would cost one call per
  tool, and a verdict near a threshold would flip between lists.
- **A hidden set that follows the latest page, or is replaced when a refresh completes.** Lost
  because a page is not the catalogue: a tool hidden on page one would be forgotten when page two
  arrives, and a call to it would reach the server.
- **Several policies per point.** Lost because the actions would need an order, and every event
  would cost one call per policy.
- **A fixed mapping inside the gateway, or only `deny` mandatory.** Lost because the product would
  ship an enforcement choice, the default handler
  [0012](0012-agent-integrations-leave-the-verdict-to-the-application.md) rejected.
- **Handlers in code, with the gateway as a package and a project template.** Lost because nobody on
  a desktop host would run it.
- **Taking Shadow off the critical path.** Lost because the gateway would read the mode.
- **`annotate` on a definition, or switching off the whole server on one flag.** Lost because the
  model would still read the poisoned text, and one false positive would take every tool away.
- **`withhold` as a plain result.** Lost because the model would read the operator's message as the
  tool's data.
- **An excerpt of the result in the `ask` dialog.** Lost because a server's text in a dialog the
  person trusts as the host's is the server arguing its own case.
- **A cancelled or dismissed `ask` that falls back to the mapping.** Lost because only an explicit
  yes may let a flagged result through.
- **`2025-11-25` on both hops.** Lost because a server that knows only `2025-06-18` answers with it,
  and the pinned client refuses the handshake.
- **A revision negotiated per hop, with or without translation.** Lost because translation is a
  wider scope, and without it a `2026-07-28` server can return what an older host cannot take
  unchanged.
- **`RequestHandlers` entries for the standard methods, or a raw JSON-RPC relay.** Lost because the
  first throws at construction, and the second is a second protocol implementation beside the SDK's.
- **A `timeout` in the gateway file, or `timeout` accepted for the gateway only.** Lost because Core's
  `Policy` already carries a limit, and the evaluation tool would refuse the same providers file.
- **The gateway referencing the evaluation tool's project, a copy of its reader, or a new package for
  it.** Lost because a tool would depend on another tool's executable, two readers drift, and a
  package is public API to version for one internal reader.
- **Forwarding the server's stderr, plainly or behind a debug setting.** Lost because a server can
  print content or a key there, and a content setting is a decision of its own.
- **Full environment inheritance, or an empty environment with an allowlist.** Lost because the first
  hands the decision provider's key to an untrusted server, and the second breaks servers configured
  through the host's `env` block, which reaches the gateway first.
- **Free-text log lines, or `Microsoft.Extensions.Logging`.** Lost because an operator's tools cannot
  parse the first, and the second is a second configuration surface.
- **Always registering the OTLP exporter.** Lost because it defaults to `localhost:4317` and retries
  against nothing.
- **A fresh GUID per evaluation as the correlation id.** Lost because nothing would join it to the
  host's own log.
- **Self-contained binaries per runtime, or a container image.** Lost because the release gets
  heavier, and with the server inside a container stdio gets awkward.
