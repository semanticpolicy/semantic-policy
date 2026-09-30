# Changelog

Notable changes to the packages `SemanticPolicy.Core`, `SemanticPolicy.Providers.SystemOne` (from
0.1.0-alpha.2), `SemanticPolicy.Providers.Http` (from 0.1.0-alpha.2),
`SemanticPolicy.Providers.TypeSafe`, `SemanticPolicy.AgentFramework` and `SemanticPolicy.Evals` (from
0.1.0-alpha.2), which share one version. Versions follow
[Semantic Versioning](https://semver.org/); while the major version is 0, any release can change the
API.

## Unreleased

A Boolean rule's operating point can calibrate the provider's evidence before the thresholds read it.
The evaluation CLI names its providers in a file, retries and resumes a run, gives each rate an
interval, and can fail a build.

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
  an assembly built against 0.1.0-alpha.2 that constructs or deconstructs either record must be
  rebuilt.
- **`SemanticPolicy.Providers.SystemOne`, `SemanticPolicy.Providers.Http` and
  `SemanticPolicy.Providers.TypeSafe`.** A body that starts with a UTF-8 byte order mark is read as
  the JSON after the mark. It used to read as a body that is not JSON: `malformed` on a 200, and on
  any other status a failure without the body's error code in its message, without the raw JSON
  and, from the Http provider, without the failure kind the server named. JSON sent over a network
  should carry no such mark, but a parser may ignore one.
- **`SemanticPolicy.Evals`.** `run --providers <file>` calls the providers a file names, System One
  servers and Jev routes, in place of `local` and `jev`; a key is named by its environment variable,
  never written in the file. A call answered `unavailable` is made again up to `--retries` times, 2
  by default, and `run --resume <recording>` finishes a recording a run left short; it needs a
  recording made by this version, whose header keeps the run's retries and filters. Every rate that
  is one count over another comes with its 95% Wilson interval, in the text and in the JSON result.
  `report` and `run` take `--require`, such as `deny.min-precision=0.95` or `max-failure-rate=0.02`,
  and exit with code 2 when a requirement fails, so a build that replays a committed recording can
  gate on it.

## 0.1.0-alpha.2

The System One provider, for a decision model you run yourself, a provider for any server that
speaks protocol v0, a guide to writing a provider of your own, and the evaluation CLI as a package.

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
- **Every HTTP provider.** An exception from a handler the host added, such as a circuit breaker or a
  rate limiter, is an `Unknown` failure for the policy's `OnFailure`, no longer an exception out of
  `EvaluateAsync`. A body over 1 MiB is not read, and the client a registration sets up follows no
  redirect. A model, a request id or an error code the server writes is reported only when it is an
  identifier, printable ASCII without a space. A base URL with a query or a fragment is refused at
  registration, since the path would land inside it. For `SemanticPolicy.Providers.TypeSafe` each of
  these is a change from 0.1.0-alpha.1.
- **`SemanticPolicy.Evals`.** New. The evaluation CLI, until now run from a clone, as a dotnet tool
  whose command is `semantic-policy`. Its new `samples` command writes out the datasets and
  recordings the package carries, and `report`, `sweep` and `compare` replay those without a key.
  `run` gains a `local` binding for a Von server beside `jev`, and the recordings of the smoke and
  router sets are answered by both. It builds a provider only when a policy binds it, and a curve
  replays at most 101 candidates. `report --diagram <file>` draws the calibration section as an SVG
  reliability diagram.

[Local setup](README.md#local-setup) starts Von, and [docs/local-models.md](docs/local-models.md)
has what a probe measured on it and on two other local servers.

[docs/custom-providers.md](docs/custom-providers.md) says which provider fits a model of your own
and, when none does, the rules an `IDecisionProvider` you write has to keep;
[examples/CustomProvider](examples/CustomProvider/README.md) is one, over a prompt-injection
classifier behind a Text Embeddings Inference server, with its own tests.

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
