# Changelog

Notable changes to the packages `SemanticPolicy.Core`, `SemanticPolicy.Providers.SystemOne` (from
0.1.0-alpha.2), `SemanticPolicy.Providers.Http` (from 0.1.0-alpha.2),
`SemanticPolicy.Providers.TypeSafe`, `SemanticPolicy.Extensions.AI` (from 0.1.0-alpha.3),
`SemanticPolicy.AgentFramework`, `SemanticPolicy.FluentValidation` (from 0.1.0-alpha.2),
`SemanticPolicy.Evals` (from 0.1.0-alpha.2) and `SemanticPolicy.Mcp.Gateway`, which share one
version. Versions follow [Semantic Versioning](https://semver.org/); while the major version is 0,
any release can change the API.

## Unreleased

A new dotnet tool, `SemanticPolicy.Mcp.Gateway`, runs one MCP server behind SemanticPolicy for a
host you do not write, such as Claude Desktop, Claude Code, Cursor or VS Code. In your own code, the
tool guards already cover an MCP client's tools, and now have tests that show it.

- **`SemanticPolicy.Mcp.Gateway`.** New. The `semantic-policy-mcp` command, which a host starts as
  `dnx SemanticPolicy.Mcp.Gateway --prerelease -- --gateway <file> -- <server command>`, sits
  between the host and one stdio MCP server, on MCP revision 2025-06-18 on both sides. It asks a
  policy about each tool result, each error a server answers a call with, and each tool definition,
  and its gateway file maps every verdict but allow to an action: `pass`, `annotate`, `withhold` or
  `ask` for a result, `pass` or `hide` for a definition. The gateway is not a security boundary.
- **`SemanticPolicy.Mcp.Gateway`.** `ask` asks the person, in a dialog the host shows with the
  operator's text and the tool's name and none of the result, whether the result may pass. Only an
  accept lets it through; anything else withholds it, and a host that cannot show the dialog gets
  the entry's fallback action.
- **`SemanticPolicy.Mcp.Gateway`.** It reads the evaluation CLI's providers file with the same code,
  builds every provider its policies bind when it starts, and refuses `timeout` there, because a
  policy's `budget` limits each check. It starts the server without the variables that hold the
  providers' keys and drops the server's stderr. It writes one line of metadata per check to its
  own stderr, never content, and exports the evaluator's spans and metrics over OTLP when an
  endpoint variable is set. Images, audio, binary resources and resource links in a result pass
  unread, and so do resources, prompts, completions, the server's instructions and a call's
  arguments.
- **`SemanticPolicy.Mcp.Gateway`.** The package carries its own README and no samples. The samples,
  in the repository, run in Shadow, and come with two labelled synthetic sets and a recording of
  each through Jev, which CI replays. The package carries its dependencies inside it, among them
  `ModelContextProtocol.Core` (Apache-2.0), OpenTelemetry and its OTLP exporter (Apache-2.0) and
  `System.CommandLine` (MIT), and declares no NuGet dependency.
- **`SemanticPolicy.Extensions.AI`.** The tools an `McpClient` from the MCP C# SDK lists are
  `AIFunction`s, so `UseSemanticPolicyBeforeTool` and `UseSemanticPolicyAfterTool` guard an MCP
  server's tools in process, with no extra package. Tests now run a real MCP server and client in
  the test process: a refused call never reaches the server, and a replaced result is what the model
  receives. The package's README says what `ToolResult.Value` holds for a plain, a structured and an
  error result. Nothing in the API changes.

## 0.1.0-alpha.3 - 2026-10-03

A new package, `SemanticPolicy.Extensions.AI`, puts the tool guards on any Microsoft.Extensions.AI
chat client, and the guard layer's types move into `SemanticPolicy.Core`, where both integrations
share them. A Choice rule's verdict names the option the provider picked, and the evaluation CLI can
call any protocol v0 server and fit a calibration into a new policy.

- **`SemanticPolicy.Core`.** `RuleVerdict` gains `ChosenOption`, the key of the option the deciding
  answer picked on a Choice rule, and `DecidingAttempt`, the attempt at `DecidingBinding`, whose
  result holds that answer and the evidence the provider returned with it. Both are null when no
  answer decided the rule, because the gate abstained or the failure behaviour set the verdict, and
  `ChosenOption` is null on a Boolean or Score rule. Both are read from `Attempts`, so the
  constructor, `Deconstruct` and the serialized verdict are as before.
- **`SemanticPolicy.Core`.** The guard layer's subjects (`ConversationMessage`, `ModelInput`,
  `ToolCall`, `ToolResult`), outcomes (`PreModelOutcome`, `PreToolOutcome`, `PostToolOutcome` and
  their `…Kind` enums) and handlers (`PreModelHandler`, `PreToolHandler`, `PostToolHandler`) move here
  from `SemanticPolicy.AgentFramework`, unchanged and in the same namespace. Core gains no
  dependency. `ModelInput`, `ToolCall` and `ToolResult` gain `ToSemanticContext()`, the default
  context the integrations send, so an application that calls `IPolicyEvaluator` itself can send the
  same parts.
- **`SemanticPolicy.Extensions.AI`.** New. `UseSemanticPolicyBeforeTool` and
  `UseSemanticPolicyAfterTool` on `ChatClientBuilder` put the two tool points on any `IChatClient`
  with a function-calling loop, with the same handlers and outcomes as the Agent Framework package.
  They go before `UseFunctionInvocation()`. A missing function-invoking client, an unknown policy id
  or a container without an evaluator fails at `Build`, and so does a second `Build` over a
  function-invoking client you constructed yourself. `FunctionInvocationContext.ToToolCall()` maps a
  call for an application that evaluates it on its own. There is no pre-model point. The package
  depends on `Microsoft.Extensions.AI` (MIT).
- **`SemanticPolicy.AgentFramework`.** An assembly compiled against 0.1.0-alpha.2 that uses one of
  the types that moved to Core has to be rebuilt: there are no type forwards. Source compiles
  unchanged, because the package still brings Core. The package now depends on
  `SemanticPolicy.Extensions.AI` and ships its own README on nuget.org. Its own API is unchanged.
- **`SemanticPolicy.Evals`.** The providers file `run --providers` reads gains the `http` kind: an
  entry whose `options` are `SemanticPolicy.Providers.Http`'s `HttpProviderOptions` registers that
  provider, so a protocol v0 server can be evaluated and compared beside System One servers and Jev.
  Its key is optional and named by an environment variable, as a System One key is. The tool package
  now carries the Http provider's assembly.
- **`SemanticPolicy.Evals`.** `calibrate` fits Platt scaling to one binding of a Boolean rule on the
  tune rows of a recording and writes a new policy that carries the calibration, each threshold
  moved so that every recorded row keeps its verdict; it prints ECE and the Brier score on the test
  rows before and after. The calibrated probability is an estimate and can be wrong on inputs unlike
  those rows. `report`, `sweep` and `compare` read a calibrated policy as the library evaluates it,
  and `report` and `run` count the rows another model answered as `calibrationModelMismatchRows`.
  The smoke set gains `prompt-injection.calibrated.policy.json`, an illustration `calibrate` wrote.

## 0.1.0-alpha.2 - 2026-09-30

The System One provider, for a decision model you run yourself, a provider for any server that
speaks protocol v0, a guide to writing a provider of your own, semantic rules on a FluentValidation
validator, outside any agent, and the evaluation CLI as a package that can fail a build. A Boolean
rule's operating point can calibrate the provider's evidence before the thresholds read it.

- **`SemanticPolicy.Core`.** `Calibrate` on an operating point takes an `EvidenceCalibration`: Platt's
  map p = σ(`Slope` · x + `Intercept`), where x is the log-odds of a score or probability, or the raw
  value of a logit or an unbounded score. The thresholds then read the calibrated probability, and a
  margin gate still reads the provider's own margin. A pair fitted as P = 1 / (1 + exp(A · f + B)), as
  Platt and scikit-learn write it, enters negated: `Slope` = −A and `Intercept` = −B. The calibrated
  probability is an estimate fitted on labelled data and can be wrong on inputs unlike that data.
  `Validate()` refuses a calibration on a Choice or Score rule, a source kind other than score, logit
  or probability, a logit read through log-odds, a slope that is not finite and above zero, an
  intercept that is not finite, and a threshold or gate of the wrong kind at a calibrated point, and
  the evaluator needs the provider to produce only the kind the calibration reads. Each attempt
  read at a calibrated point carries the `CalibratedEvidence` beside its untouched result, and is
  marked when the result names a model other than the one the calibration was fitted on, which spans
  tag as `semanticpolicy.calibration.method` and `semanticpolicy.calibration.model_mismatch`. The
  arithmetic is public: `Apply`, `Invert` and `Input` turn a threshold between the two scales. A
  policy without a calibration evaluates, serializes and traces as before. `RuleOperatingPoint` gains
  `Calibration`, and `Attempt` gains `CalibratedEvidence` and `CalibrationModelMismatch`, as optional
  last parameters, so each record's constructor and `Deconstruct` change: a call to the constructor
  compiles as before, a positional deconstruction or pattern needs a place for each new member, and
  an assembly built against 0.1.0-alpha.1 that constructs or deconstructs either record must be
  rebuilt. A success whose evidence of the kind a rule reads carries NaN or an infinity is
  `Malformed`, so the policy's `OnFailure` decides; until now NaN and −∞ read as `Allow` and +∞
  crossed every threshold.
- **`SemanticPolicy.Providers.SystemOne`.** New. Any server that answers TypeSafe's System One API at
  `/v1/systemone`, registered with `AddSystemOne`. It reports the server's numbers as a score unless
  you declare them a probability, refuses plain `http` to anything but a loopback host unless you
  allow it, and can refuse a context longer than a limit you set.
- **`SemanticPolicy.Providers.Http`.** New. Any server that speaks [protocol v0](docs/protocol-v0.md)
  over HTTP, registered with `AddHttpProvider`. You declare the decision types the server answers,
  the evidence kinds it sends and whether it reads a structured context, and the provider reports
  nothing beyond that declaration. Like the System One provider, it refuses plain `http` to anything
  but a loopback host unless you allow it, and can refuse a context longer than a limit you set.
  Nothing the server writes reaches a failure's message. The answer is read strictly, member names
  in the protocol's case and numbers as JSON numbers, and evidence keyed outside the request's
  answers, a value that is not finite, a probability outside [0, 1] or a second entry of a declared
  kind reads as malformed. `docs/protocol-v0.md` gains the
  [HTTP binding](docs/protocol-v0.md#http-binding): what such a server answers, how the provider
  reads it, and a minimal server to start from.
- **`SemanticPolicy.Providers.TypeSafe`.** Built on the System One provider, which it now brings
  along at exactly its own version.
- **Every HTTP provider.** An exception from a handler the host added, such as a circuit breaker or
  a rate limiter, is an `Unknown` failure for the policy's `OnFailure`, no longer an exception out
  of `EvaluateAsync`. A body over 1 MiB is not read, and the client a registration sets up follows
  no redirect. A model, a request id or an error code the server writes is reported only when it is
  an identifier, printable ASCII without a space. A base URL with a query or a fragment is refused
  at registration, since the path would land inside it. A body that starts with a UTF-8 byte order
  mark is read as the JSON after the mark: JSON sent over a network should carry no such mark, but a
  parser may ignore one. For `SemanticPolicy.Providers.TypeSafe` each of these is a change from
  0.1.0-alpha.1.
- **`SemanticPolicy.FluentValidation`.** New. `RuleFor(...).Semantic(evaluator, "policy-id")` asks a
  policy about a property of a FluentValidation validator and reports a flagged verdict as a
  validation failure, beside the validator's other rules. A rule names a registered policy or takes a
  `Policy`, and sends the property's text or, through a context delegate, the parts the delegate
  builds; a null or whitespace text passes without a call. By default it reads the effective verdict:
  `Deny` fails as an `Error`, `Escalate` as a `Warning`, and `Warn`, `Abstain` and `Allow` do not
  fail, so a policy in Shadow mode never fails a validation. A `severity:` delegate replaces that
  mapping. A failure carries the `PolicyVerdict` as its `CustomState` and `SemanticPolicyValidator`
  as its error code, and its default message says the value was flagged by the policy, never that it
  is invalid. The rule runs under `ValidateAsync` only. The package depends on FluentValidation and
  `SemanticPolicy.Core`, not on ASP.NET Core, so a web endpoint calls `ValidateAsync` itself.
- **`SemanticPolicy.Evals`.** New. The evaluation CLI, until now run from a clone, as a dotnet tool
  whose command is `semantic-policy`. Its new `samples` command writes out the datasets and
  recordings the package carries, and `report`, `sweep` and `compare` replay those without a key.
  `run` gains a `local` binding for a Von server beside `jev`, and the recordings of the smoke and
  router sets are answered by both. It builds a provider only when a policy binds it, and a curve
  replays at most 101 candidates. `report --diagram <file>` draws the calibration section as an SVG
  reliability diagram. `run --providers <file>` calls the providers a file names, System One servers
  and Jev routes, in place of `local` and `jev`; a key is named by its environment variable, never
  written in the file. A call answered `unavailable` is made again up to `--retries` times, 2 by
  default, and `run --resume <recording>` finishes a recording a run left short; it needs a
  recording made by this version, whose header keeps the run's retries and filters. Every rate that
  is one count over another comes with its 95% Wilson interval, in the text and in the JSON result.
  `report` and `run` take `--require`, such as `deny.min-precision=0.95` or `max-failure-rate=0.02`,
  and exit with code 2 when a requirement fails, so a build that replays a committed recording can
  gate on it.

[Local setup](README.md#local-setup) starts Von, and [docs/local-models.md](docs/local-models.md)
has what a probe measured on it and on two other local servers.

[docs/custom-providers.md](docs/custom-providers.md) says which provider fits a model of your own
and, when none does, the rules an `IDecisionProvider` you write has to keep;
[examples/CustomProvider](examples/CustomProvider/README.md) is one, over a prompt-injection
classifier behind a Text Embeddings Inference server, with its own tests.

[examples/SupportTicketForm](examples/SupportTicketForm) is a minimal API whose validator asks two
policies about a support ticket, and the evaluation CLI carries `support-ticket.jsonl`, fifty
synthetic tickets for the example's category rule, to measure that rule with `compare`.
[docs/classification.md](docs/classification.md) shows a Choice rule picking a label outside any
agent, on Core alone.

## 0.1.0-alpha.1 - 2026-09-24

The first release: prerelease packages on NuGet, for .NET 10.

- **`SemanticPolicy.Core`.** Policies of Boolean, Choice and Score rules, each bound to one or more
  providers with thresholds on the evidence they return. `IPolicyEvaluator` turns an input into one
  of five verdicts: Allow, Warn, Abstain, Escalate or Deny. A policy runs in Shadow or Enforce and
  declares what happens when a provider fails; a margin gate moves a close call to the next binding
  and ends in Abstain after the last. Also the provider contract of
  [protocol v0](docs/protocol-v0.md), telemetry that carries no content, and `AddSemanticPolicy()`
  for dependency injection.
- **`SemanticPolicy.Providers.TypeSafe`.** The TypeSafe Jev decision model, at TypeSafe's own
  endpoint or through OpenRouter, registered with `AddTypeSafeJev`.
- **`SemanticPolicy.AgentFramework`.** `UseSemanticPolicyBeforeModel`, `UseSemanticPolicyBeforeTool`
  and `UseSemanticPolicyAfterTool` on `AIAgentBuilder`: each evaluates a policy and hands the verdict
  to your handler.

The repository also holds the evaluation CLI, `tools/SemanticPolicy.Evals`, and four examples;
neither is published as a package.
