# Protocol v0

The language-neutral shape of a semantic decision request and its result. The .NET types in
`SemanticPolicy.Core` mirror it: every provider adapter, an `IDecisionProvider`, takes the request
and returns the result, whatever its provider's own API looks like. `SemanticPolicy.Providers.Http`
speaks the shape itself over HTTP, so a provider written in another language can be a server that
answers it, as the [HTTP binding](#http-binding) describes, rather than a .NET adapter. Frozen with
[ADR 0002](adr/0002-provider-contract-and-capabilities.md) after the same request passed through a
hosted decision model and a local zero-shot classifier. A change to the shape is a new version, not
an edit.

JSON, camel case, enums as camel-case strings. Absent means absent: a field a provider cannot fill is
omitted, never `null`, `0`, `false` or `""`.

## Request

One context, one typed decision.

```jsonc
{
  "protocol": "semanticpolicy/v0",
  "type": "boolean" | "choice" | "score",
  "question": "Is this tool call destructive or does it run untrusted remote code?",
  "context": <string | object | array>,

  // boolean — optional; what a true and a false answer look like
  "criteria": { "true": "deletes data, runs remote scripts, or exfiltrates", "false": "read-only or reversible" },

  // choice — required; the key is the value the result carries, the text is what it means
  "options": { "allow": "safe to proceed as-is", "human_review": "a person must look before it proceeds", "block": "must not proceed" },

  // score — required; ordered from lowest to highest
  "levels": [ "harmless", "minor", "moderate", "serious", "critical" ]
}
```

`Core` validates before any provider is called: `question` non-empty; `context` present; `options`
at least two; `levels` two to ten, distinct. Providers accept different things — one answers a
one-level score with a degenerate distribution — so validation is not delegated to them. An invalid
request is an argument error in the caller's process, not a provider outcome.

`context` is the thing being judged, as the application has it. A provider that reads text declares
`structuredContext: false` in its capabilities, and its adapter renders an object or array to text
with `Core`'s canonical rendering (`SemanticContext.ToCanonicalText`) before the call, so the provider
receives a string and every text-only provider reads the same text.

## Result

```jsonc
{
  "protocol": "semanticpolicy/v0",
  "type": "score",                                   // echoed from the request
  "outcome": { "status": "success" },
  "value": { "level": "critical", "index": 4 },      // present iff status is success
  "evidence": [
    { "kind": "probability", "scale": "calibrated",
      "values": { "harmless": 0, "minor": 0, "moderate": 0, "serious": 0.04, "critical": 0.96 } }
  ],
  "raw": { ... },                                    // the provider response as received; optional
  "provider": {
    "id": "typesafe-jev",
    "model": "typesafe/jev-1.13",
    "latencyMs": 349,
    "requestId": "...",                              // optional
    "usage": { ... },                                // optional, provider-shaped
    "extra": { ... }                                 // optional, provider-shaped
  }
}
```

### `outcome` — what happened to the call

```jsonc
{ "status": "success" }
{ "status": "abstain", "message": "..." }            // the provider declined this input; evidence may still be present
{ "status": "failure", "kind": "timeout" | "unavailable" | "malformed" | "rejectedInput" | "unauthorized" | "unknown", "message": "..." }
```

The outcome is one axis; the value is another ([ADR 0006](adr/0006-failure-and-abstention-model.md)).
A failure carries no value. `malformed` is the adapter's verdict on the response body;
`rejectedInput` is the provider's verdict on the request. `message` is for the log and is never
content from the context.

### `value` — the provider's answer

| `type` | `value` |
|---|---|
| `boolean` | `true` or `false` |
| `choice` | one key of `options` |
| `score` | `{ "level": <one of levels>, "index": <its position, 0-based> }` |

The value is the provider's best answer. A policy does not act on the value alone; it thresholds
the evidence ([ADR 0003](adr/0003-evidence-semantics.md)). A provider that has only a probability
for a boolean cuts it at 0.5 to produce the value, and says so in its documentation.

### `evidence` — the numbers, each with its kind

Zero or more entries. Every entry has a `kind` and `values` keyed by option, level, or `true` /
`false` for a boolean. Optional `scale` names the provider's scale (`calibrated`, `sigmoid`, …).

| `kind` | Meaning |
|---|---|
| `probability` | in [0, 1], and the provider or a calibration layer claims it is calibrated. Over all options and summing to one it is a distribution |
| `score` | monotonic and provider-scaled; ordering is meaningful, the value is not a probability |
| `logit` | an unbounded log-odds value |
| `margin` | the gap between the top option and the runner-up, on the provider's scale |
| `unknown` | a number the provider returned whose meaning it does not define |

A bare number is not evidence. There is no `confidence` field: a vendor's confidence is a projection
of its own distribution and belongs in `provider.extra`; a margin is computed by the runtime from
per-option evidence when a policy asks for it. A distribution is a property of `probability`
evidence, not a kind or a capability of its own.

Two providers, the same request, as they actually answered:

```jsonc
// hosted decision model
"evidence": [ { "kind": "probability", "scale": "calibrated",
                "values": { "harmless": 0, "minor": 0, "moderate": 0, "serious": 0.04, "critical": 0.96 } } ]

// local zero-shot classifier — per-label sigmoid, does not sum to one, and the logits it came from
"evidence": [ { "kind": "score", "scale": "sigmoid",
                "values": { "harmless": 0.64, "minor": 0.70, "moderate": 0.85, "serious": 0.77, "critical": 0.43 } },
              { "kind": "logit",
                "values": { "harmless": 0.60, "minor": 0.83, "moderate": 1.73, "serious": 1.22, "critical": -0.30 } } ]
```

A probability threshold applied to the second is a configuration error the runtime reports, not a
coercion.

### `provider` — who answered

`id` (the adapter), `model` (what the adapter used, as specific as the provider reports it) and
`latencyMs` (observed by the caller) are mandatory. `requestId`, `usage` and `extra` are optional and
provider-shaped; nothing in them is read by a policy.

### `raw`

The provider response as received. Optional on the wire. The .NET types hold it in memory only: they
never write or read it, so it reaches no log, no telemetry and no evaluation recording
([ADR 0008](adr/0008-telemetry-and-content-logging.md),
[ADR 0013](adr/0013-evaluation-records-answers-and-replays-them-through-core.md)).

## Capabilities

Declared in-process by every adapter, not sent on the wire:

```jsonc
{ "types": ["boolean", "choice", "score"],
  "evidence": ["probability"],             // kinds this provider produces
  "rawOutput": true,
  "structuredContext": true }              // false: the adapter renders objects and arrays to text
```

A policy or an evaluation run asks before it relies on a kind. An absent capability is absent — not
zero, not `false`, not an empty distribution.

## HTTP binding

How a server speaks v0 over HTTP, and how `SemanticPolicy.Providers.Http` reads it. The request and
the result are the shapes above, unchanged; the binding adds the transport and nothing else.

### Request

`POST {base}/v0/decide`, where the client's `Path` option can move the path, with the request as the
body, `Content-Type: application/json`, and `Authorization: Bearer <key>` only when a key is
configured. With `structuredContext: false` the client puts `Core`'s canonical text of the context in
`context`, as a JSON string; with `true`, the context as the application passed it. A server that
reads text therefore never flattens anything itself. The request carries no model: the registration
names what the server runs, and a result reports that name whenever the server's answer names none.

### Response

`200` is for an answer only: a `success` or an `abstain`, with the result as the body. A failure is a
status outside 2xx, and a server uses these:

| Failure `kind` | Status |
|---|---|
| `unauthorized` | 401 or 403 |
| `rejectedInput` | 400, 413 or 422 |
| `unavailable` | 429 or 503 |
| `timeout` | 504 |

A failure may carry, as its body, a result whose outcome is `failure`; its `kind` then wins over the
status, which only approximates it. Without one the client maps the status itself: 401 and 403
`unauthorized`; 400, 404, 413 and 422 `rejectedInput`; 408, 429 and every 5xx `unavailable`, so a
bare 504 reads `unavailable`, not `timeout`; any other status `unknown`. The client's own timer gives
`timeout`, a connection that fails gives `unavailable`, and any other exception from the client's
handlers, such as a circuit breaker's, gives `unknown`. The client reads at most 1 MiB of a body: a
longer one is `malformed` on a `200` and the status's kind otherwise. A registered client follows no
redirect, so a 3xx reads `unknown` rather than sending the request on.

### What the client reads as v0

A body is a v0 result when the JSON object itself carries `"protocol": "semanticpolicy/v0"`, a
`type`, an `outcome` object with a `status` and a `provider` object; when every entry of an
`evidence` array is an object with `kind` and `values`; and when the whole deserializes into the
result's types, which a string no enum names, such as an unknown failure `kind`, does not. Each member
is checked on the JSON rather than on a default the client would fill in, so an evidence entry
without a `kind` never becomes a probability the server did not claim. Missing or empty `evidence` is
no evidence.

- On a `200`, anything but a v0 `success` or `abstain` of the request's `type` is `malformed`: a
  failure, a body that is not JSON, JSON that is not a v0 result, a result of another type, a
  `success` whose `value` is missing or is not one of the request's `options` or `levels`. So is
  evidence of a declared kind keyed by anything but the request's answers (`true` and `false`, an
  option, a level), and a `probability` outside [0, 1].
- On any other 2xx the call is `malformed`, whatever the body says.
- On a status outside 2xx the body is a failure body only when it is a v0 result whose outcome is
  `failure` with a `kind` the outcome's list names. HTML, `{"error": …}`, an object without
  `protocol`, a v0 `success` on an error status: each leaves the status to decide.

### What the client relays

From a `success` or an `abstain`: a success's `value`, the outcome's status, the evidence whose kind
the registration declares, with any other kind dropped, and `provider.model` and `provider.requestId`.
An evidence `scale`, the model and the request id are relayed only as identifiers, 1 to 200 printable
ASCII characters without a space; a model that is not one gives way to the configured model, and the
others are dropped. The result names the client's own id and the latency the client measured. The
outcome's `message`, `provider.usage`, `provider.extra` and any `value` on an abstain are not
relayed: text and JSON the library cannot vouch for would otherwise travel wherever a result is
written. A failure's message carries the status, the `kind` when it came from a failure body, and the
body's length, never anything the server wrote. The whole body stays in the result's `raw`, in memory
only.

The server's answer is an estimate for a policy to threshold, not a verdict.

### A minimal server

A Boolean-only server on Python's standard library, around a classifier of your own:

```python
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MODEL = "my-classifier-1"


def classify(question: str, text: str) -> float:
    """Your model: how strongly `text` answers `question` with yes, in [0, 1]."""
    raise NotImplementedError


def result(type_, outcome, started, **answer):
    latency = round((time.perf_counter() - started) * 1000)
    return {"protocol": "semanticpolicy/v0", "type": type_, "outcome": outcome, **answer,
            "provider": {"id": "my-server", "model": MODEL, "latencyMs": latency}}


class Decide(BaseHTTPRequestHandler):
    def do_POST(self):
        started = time.perf_counter()
        if self.path != "/v0/decide":
            return self.reply(404, None)
        try:
            request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            type_, question, text = request["type"], request["question"], request["context"]
        except (KeyError, TypeError, ValueError):
            return self.reply(400, None)
        if type_ != "boolean" or not isinstance(text, str):
            return self.reply(422, result(type_, {"status": "failure", "kind": "rejectedInput"}, started))
        try:
            p = classify(question, text)
        except Exception:
            return self.reply(503, result(type_, {"status": "failure", "kind": "unavailable"}, started))
        evidence = [{"kind": "score", "values": {"true": p, "false": 1 - p}}]
        self.reply(200, result(type_, {"status": "success"}, started, value=p >= 0.5, evidence=evidence))

    def reply(self, status, body):
        data = b"" if body is None else json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


ThreadingHTTPServer(("127.0.0.1", 8765), Decide).serve_forever()
```

It reports `score` evidence with both ends, not `probability`, because a classifier's number orders
its answers without being calibrated ([ADR 0003](adr/0003-evidence-semantics.md)), and it cuts the
number at 0.5 for the value. Register it with `Types` Boolean, `Evidence` Score and
`StructuredContext` false. It listens on loopback and checks no key; before it serves anything beyond
loopback, put it behind TLS and check the `Authorization` header.

## Not in v0

- **Several decisions on one context in one request.** Each decision is one request. A provider that
  can batch may do so behind its adapter; the shape does not expose it.
- **A continuous score value.** The expected level under a distribution, or any other scalar, is
  derived from the evidence by whoever wants it.
- **Calibration.** A calibration layer that turns `score`, `logit` or `margin` evidence into
  `probability` evidence produces a result in this same shape; it is not part of the protocol.
- **Provider-side abstention as a requirement.** `abstain` is in the outcome for providers that do
  it, and a v0 server's `abstain` reaches a policy through the Http client; no provider has to
  abstain, and the abstention that matters is the policy's.
