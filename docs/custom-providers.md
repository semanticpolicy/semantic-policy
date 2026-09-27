# Custom providers

A provider answers a rule's question and reports what it observed: a value with evidence of a
declared kind, or a failure. It decides nothing. Thresholds, verdicts and what a failure means belong
to the policy, and the verdict a policy reaches is probabilistic: a denied verdict is not proof of an
attack, and an allowed verdict is not proof of safety. No provider turns a rule into a security
boundary. A prompt-injection rule raises the cost of an attack; it does not close the attack
([`SECURITY.md`](../SECURITY.md)).

This guide says which way to plug a model in and, when that way is code of your own, the rules the
code has to keep. [`examples/CustomProvider/`](../examples/CustomProvider/README.md) is a compiled
provider that keeps all of them, over a prompt-injection classifier behind a Text Embeddings
Inference (TEI) server, with its own tests.

## Which path to take

| Your model is behind | Use | Code to write |
|---|---|---|
| a server that speaks [protocol v0 over HTTP](protocol-v0.md#http-binding), at `/v0/decide` | `AddHttpProvider`, in the `SemanticPolicy.Providers.Http` package | none: declare what the server answers ([README](../README.md#any-protocol-v0-server)) |
| a server that answers the System One API at `/v1/systemone`, such as Von | `AddSystemOne`, in the `SemanticPolicy.Providers.SystemOne` package | none ([README](../README.md#any-system-one-server)) |
| anything else: another vendor's API, a server you cannot change, a model in your own process | an `IDecisionProvider` of your own, registered with `AddProvider` | the provider, following this guide |

If the server is yours to change, the first row is usually less work: the HTTP binding ends with a
minimal server on Python's standard library, and the Http client already keeps every rule below. The
third row is for an API that is not yours to change, such as TEI's `/predict` in the example.

## The contract

[`IDecisionProvider`](../src/SemanticPolicy.Core/Providers/IDecisionProvider.cs) has three members:

```csharp
public interface IDecisionProvider
{
    string Id { get; }
    ProviderCapabilities Capabilities { get; }
    Task<ProviderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default);
}
```

`DecideAsync` gets one question, its decision type and the context to judge, and returns one
`ProviderResult`. The rules below are what the evaluator relies on. Each names the code that keeps it
in the example and in the Http client, and the tests that hold it there.

### A failure is a result, never an exception

A server that refuses the key, rejects the input, is down, answers something the provider cannot read
or does not answer in time has not broken anything in your code. Return
`ProviderResult.Failed(type, kind, message, metadata)` with the kind that says which, and the policy's
`OnFailure` decides what it means. Throw only for a programming error, such as a request of a type the
provider does not declare, and let the token's cancellation propagate as an
`OperationCanceledException`. The token carries the caller's cancellation and the policy's budget; the
evaluator turns the budget's expiry into a `Timeout` itself. Any other exception propagates out of
`EvaluateAsync` unchanged, with no verdict.

For an HTTP server, both clients and the example map what came back the same way:

| What came back | Failure kind |
|---|---|
| 401, 403 | `Unauthorized` |
| 400, 404, 413, 422 | `RejectedInput` |
| 408, 429, any 5xx | `Unavailable` |
| a 200 the provider cannot read, any other 2xx | `Malformed` |
| any other status | `Unknown` |
| no connection: `HttpRequestException`, `HttpIOException` | `Unavailable` |
| nothing within the provider's own timer | `Timeout` |

The Http client reads one thing first: a status outside 2xx whose body is a protocol v0 failure takes
the `kind` that body names, which the status only approximates, so a 504 naming `timeout` reads
`Timeout` ([HTTP binding](protocol-v0.md#response)). The System One client and the example have no
such body to read and go by the status alone.

A failed call is reported once, never retried inside the provider; a host that wants retries
configures them on the `HttpClient`.

- Code: the `catch` blocks and `KindOf` in the example's
  [`TeiClassifierProvider`](../examples/CustomProvider/CustomProvider/TeiClassifierProvider.cs); the
  clients' shared [`ProviderHttpCall`](../src/Shared/ProviderHttpCall.cs) and `KindOf` in
  [`ProviderHttp`](../src/Shared/ProviderHttp.cs); `FailureKindOf` in the Http client's
  [`HttpProviderResponse`](../src/SemanticPolicy.Providers.Http/HttpProviderResponse.cs).
- Tests: the example's `Status_Maps_To_A_Failure_Kind`, `Connection_Failure_Reads_As_Unavailable`,
  `Answer_Outside_The_Expected_Shape_Reads_As_Malformed` and
  `Caller_Cancellation_Throws_OperationCanceledException`; the contract suite's
  `Provider_Reports_Every_Failure_Kind_As_A_Failure_Result_Without_Throwing`, which also checks that a
  failed call is not retried; the Http client's `Failure_Body_Kind_Wins_Over_The_Status`; Core's
  `Provider_Exception_Propagates_Unchanged` and
  `Budget_Expiry_Becomes_Failure_Timeout_Under_The_Failure_Behaviour`.

### A timer of your own, separate from the caller's token

Link a `CancellationTokenSource` to the token you are given, start its timer with `CancelAfter`, and
send the request and read the whole body under it. When the timer fired and the given token did not,
the call is a `Timeout` failure whose message says `no response within N ms`. When the given token was
cancelled, the `OperationCanceledException` goes on.

Leave the `HttpClient`'s own `Timeout` infinite. A client timeout that fires first throws a
`TaskCanceledException` with neither token cancelled, which is neither a `Timeout` the provider
reports nor a cancellation anyone asked for, so the evaluator treats it as a programming error and it
propagates. `AddHttpProvider`, `AddSystemOne` and the example's registration all set
`Timeout.InfiniteTimeSpan` on the named client.

On Windows, a connection to a loopback port nothing listens on takes about two seconds to be refused,
so a timer shorter than that reports a missing local server as a `Timeout` rather than as
`Unavailable`. The example's timer is three seconds for that reason.

- Code: `DecideAsync` in the example's provider, and the `ConfigureHttpClient` line in its
  [`Program.cs`](../examples/CustomProvider/CustomProvider/Program.cs); the clients' shared
  `ProviderHttpCall`, and the same line in `AddClient` in
  [`ProviderHttp`](../src/Shared/ProviderHttp.cs).
- Tests: the example's `Silent_Server_Ends_In_A_Timeout_Failure` and
  `Caller_Cancellation_Throws_OperationCanceledException`; the Http client's
  `Registered_Provider_Reports_The_Declared_Capabilities_Under_The_Registration_Name`, which checks
  that the named client's timeout is infinite.

### The honest evidence kind

Every number a result carries is evidence of a declared kind
([ADR 0003](adr/0003-evidence-semantics.md)):

- `Probability` only when the number is calibrated — 0.8 is right about four times in five — and the
  provider, or a calibration step, claims so;
- `Score` for a number that orders answers without being calibrated, which is what a classifier's
  softmax or sigmoid is;
- `Logit` for an unbounded log-odds value, and `Margin` for the gap between the top two options;
- `Unknown` for a number whose meaning the provider does not define.

Never report a score as a probability because a probability threshold reads better. A provider with no
numbers returns an empty evidence list, never a placeholder entry
([ADR 0011](adr/0011-absence-of-evidence-is-an-empty-list.md)).

A Boolean answer's evidence is keyed `true` and `false`. `Probability` evidence may carry one side,
and the evaluator completes it as 1 − p. Any other kind has no complement, so it carries both keys, or
a Boolean rule reads the result as `Malformed` and the policy's `OnFailure` decides.
`Evidence.Scale` names the scale for a reader, such as `softmax`, and claims nothing.

- Code: `Read` in the example's provider, which reports TEI's two labels as `Score` evidence with both
  keys on the `softmax` scale; the Boolean check in Core's
  [`PolicyEvaluation`](../src/SemanticPolicy.Core/Evaluation/PolicyEvaluation.cs); the Http client's
  `Relay` in [`HttpProvider`](../src/SemanticPolicy.Providers.Http/HttpProvider.cs), which passes on
  only the kinds the registration declares.
- Tests: the example's `Classifier_Answer_Becomes_A_Boolean_With_Score_Evidence`; Core's
  `Success_That_Breaks_The_Contract_Is_Malformed_For_That_Attempt`, whose "one-key score evidence"
  case is a `Score` with only `true`; the Http client's `Server_Answer_Is_Relayed_Under_The_Client_Id`,
  which drops a `logit` the registration did not declare.

### Declared capabilities, checked before any call

[`Capabilities`](../src/SemanticPolicy.Core/Providers/ProviderCapabilities.cs) says, in-process, what
the provider answers: the decision types, the evidence kinds, whether results carry the response in
`Raw`, and whether it reads a structured context as such. When the evaluator is created it checks
every registered policy against the providers its bindings name, and it checks a policy passed to
`EvaluateAsync` the same way before running it. Each provider must be registered, answer every rule's
decision type, and produce every evidence kind a threshold or a margin gate reads. Otherwise the check
throws a `PolicyConfigurationException` before any provider is called, so a probability threshold
bound to a provider that declares only `Score` fails at start-up rather than reading a score as a
probability.

Declare what the provider produces and nothing more: an absent capability is absent, not zero. With
`StructuredContext` false, render the context with `SemanticContext.ToCanonicalText`, so every
text-only provider reads the same text.

- Code: `Capabilities` in the example's provider — Boolean, `Score`, `RawOutput`, no structured
  context; the check in Core's [`PolicyEvaluator`](../src/SemanticPolicy.Core/PolicyEvaluator.cs); the
  Http client's [`HttpProviderOptions`](../src/SemanticPolicy.Providers.Http/HttpProviderOptions.cs),
  which refuse a registration that leaves a capability undeclared.
- Tests: Core's `Registered_Policies_Are_Validated_When_The_Evaluator_Is_Constructed` and
  `Ad_Hoc_Policy_Is_Validated_Before_Its_First_Provider_Call`; the Http client's
  `Registration_With_An_Undeclared_Or_Invalid_Option_Throws_At_The_Call`.

### No content in messages or logs, the body in `Raw`

A failure's message says what happened in terms that quote nothing: the status, a code or error type
the server returned, the body's length, `no response within N ms`, a transport failure's
`HttpRequestError`. Never the question, the context, or any text the server wrote, since an error text
can quote the input. The same holds for exception messages and for anything the provider logs, and the
simplest provider logs nothing. Keep the parsed body in the result's `Raw`, which stays in memory and
is never serialized, so a caller can read it without it travelling wherever a result is written.
Register the named `HttpClient` with `RemoveAllLoggers()`, so that no log line can print an
`Authorization` header.

- Code: `DescribeError` in the example's provider; `Describe` in the Http client's
  `HttpProviderResponse`.
- Tests: the example's `Failure_Message_Names_The_Error_Type_But_Never_The_Server_Text`; the contract
  suite's `Failure_Message_And_ToString_Never_Contain_The_Marker`; the Http client's
  `Server_Text_Never_Reaches_The_Outcome_Message`.

### Refuse an input the model would cut

A model with a context limit may cut its input without saying so, and an injection past the cut then
reads as the benign text before it. Refuse such an input as `RejectedInput` instead of sending it:
check its length before the call, or ask the server to refuse rather than cut. The policy's
`OnFailure` then decides, so pair the refusal with a second binding and
`FailureBehavior.Fallback(Verdict.Deny)`. Under `FailureBehavior.Allow`, anyone who pads an input past
the limit skips the rule.

- `MaxContextLength` on both clients,
  [`SystemOneOptions`](../src/SemanticPolicy.Providers.SystemOne/SystemOneOptions.cs) and
  `HttpProviderOptions`, refuses a context whose canonical text is longer, without calling the server.
  [Local decision models](local-models.md#long-contexts) gives the limits a probe measured on Von and
  Laya. They are characters of English text; code, numbers or another language need a lower limit.
- The example sends TEI `truncate: false`, which overrides TEI's default of cutting silently: TEI then
  answers an over-long input with a 422, which the provider reads as `RejectedInput`.
- Tests: the Http client's `Context_Length_Against_MaxContextLength_Decides_Whether_The_Server_Is_Called`;
  the example's `Request_Carries_The_Canonical_Text_Of_The_Context_And_Not_The_Question`, which checks
  `truncate: false`, and `Status_Maps_To_A_Failure_Kind`, whose 422 case reads as `RejectedInput`.

### Every result names a model

`Provider.Model` names what answered, on every result the provider returns, a failure included. When
the server's answer names a model, report that one. When it names none, as TEI's does not, or when the
provider builds the result itself, report the model the registration configured. A threshold is
measured against one model, so a verdict that cannot say which model answered cannot be checked
against that measurement.

- Code: `Metadata` in the example's provider, from `TeiClassifierOptions.Model`; `Relay` and `Failed`
  in the Http client's `HttpProvider`, from `HttpProviderOptions.Model`, which is required.
- Tests: the example's `Status_Maps_To_A_Failure_Kind`, `Connection_Failure_Reads_As_Unavailable`,
  `Answer_Outside_The_Expected_Shape_Reads_As_Malformed` and `Silent_Server_Ends_In_A_Timeout_Failure`,
  each of which checks the configured model on a failure; the Http client's
  `Result_Without_A_Server_Model_Reports_The_Configured_Model`.

### One name for the binding, the client and the id

Register the provider on the builder `AddSemanticPolicy()` returns, with `AddProvider(name, factory)`,
and use that one name three times: a policy's bindings refer to it, the `HttpClient` the factory
creates is named after it, and the provider reports it as its `Id`. A verdict's provider id, a binding
and a client's configuration then never disagree. From the example's `Program.cs`:

```csharp
services.AddHttpClient("tei")
    .RemoveAllLoggers()
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

services.AddSemanticPolicy()
    .AddProvider("tei", container =>
    {
        IHttpClientFactory factory = container.GetRequiredService<IHttpClientFactory>();
        return new TeiClassifierProvider("tei", () => factory.CreateClient("tei"), options);
    })
    .AddPolicy(policy);
```

The provider is a singleton, so it asks the factory for a client on every call rather than keeping
one: a kept client would hold its first connections, and the address they resolved, until the process
restarts.

A provider you hand to others deserves an `Add<Provider>` extension on `ISemanticPolicyBuilder` that
does the same work. It takes the name and an options delegate, validates the options at the call so a
mistake fails before a container exists, registers the named client with no loggers and no timeout of
its own, and defaults the provider's id to the name. `AddHttpProvider` in
`HttpProviderBuilderExtensions` is one to copy; `AddSystemOne` and `AddTypeSafeJev` have the same
shape.

- Tests: the example's `Every_Call_Asks_The_Source_For_A_Client`; the Http client's
  `Registered_Provider_Reports_The_Declared_Capabilities_Under_The_Registration_Name` and
  `Registration_With_An_Undeclared_Or_Invalid_Option_Throws_At_The_Call`.

## Warm a cold server

A model server that answers its health check has not necessarily loaded its model; many load it on
the first real call, and calls wait meanwhile. A cold server's first calls can therefore outlast the
provider's timer and end as `Timeout`, which the policy's `OnFailure` handles. Warm the server with one
real call of your own before any traffic, and again after every restart. The example's
[README](../examples/CustomProvider/README.md#start-the-server) has that call for TEI, and
[Warming a server](../README.md#warming-a-server) says what a cold start cost on Von.

## A fixed-task classifier

A classifier trained for one task, such as the example's prompt-injection model, answers that one
question whatever the rule asks. It never sees the rule's question — the example sends TEI the context
alone — so nothing stops a policy from binding it to a rule that asks something else, and the answer
would then be to a question nobody asked. Declare only the decision type it answers, say in the
provider's documentation which question that is, and bind it only to a rule that asks exactly that.

- Test: the example's `Request_Carries_The_Canonical_Text_Of_The_Context_And_Not_The_Question`, which
  checks that the question never reaches the server.

## Test on a fake transport

Test the provider through an `HttpMessageHandler` of your own that stands in for the server, never
against a live one. The example's
[`FakeTeiHandler`](../examples/CustomProvider/CustomProvider.Tests/FakeTeiHandler.cs) answers the way
a test scripted it, throws as a transport does when nothing listens, or hangs until the token it was
given is cancelled, and keeps the last request's method, URI and body for the test to check.
[`TeiClassifierProviderTests`](../examples/CustomProvider/CustomProvider.Tests/TeiClassifierProviderTests.cs)
then covers the rules above: an answer read into a value and evidence, the request the server
receives, the status table, a refused connection, an answer it cannot read, its own timer, the
caller's cancellation, a failure message without the server's text, and a client asked for on every
call. Every fixture is synthetic: no real content, no key, no live endpoint.

```bash
dotnet test examples/CustomProvider/CustomProvider.Tests
```

The library's own contract suite,
[`tests/SemanticPolicy.Providers.ContractTests`](../tests/SemanticPolicy.Providers.ContractTests/),
runs the same kind of cases against every provider in this repository. It is not published as a
package, so copy the cases your provider needs, as the example does.
