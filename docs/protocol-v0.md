# Protocol v0

The language-neutral shape of a semantic decision request and its result. The .NET types in
`SemanticPolicy.Core` mirror it; a provider written in any language that speaks this shape over HTTP
is a provider. Frozen with [ADR 0002](adr/0002-provider-contract-and-capabilities.md) after the same
request passed through a hosted decision model and a local zero-shot classifier. A change to the
shape is a new version, not an edit.

JSON, camel case, enums as lower-case strings. Absent means absent: a field a provider cannot fill is
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

`context` is the thing being judged, as the application has it. A provider that reads text flattens
an object or array to text and declares `structuredContext: false` in its capabilities.

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

The provider response as received, for evaluation and replay. Optional on the wire; excluded from
telemetry by default ([ADR 0008](adr/0008-telemetry-and-content-logging.md)).

## Capabilities

Declared in-process by every adapter, not sent on the wire:

```jsonc
{ "types": ["boolean", "choice", "score"],
  "evidence": ["probability"],             // kinds this provider produces
  "rawOutput": true,
  "structuredContext": true }              // false: the provider flattens objects and arrays to text
```

A policy or an evaluation run asks before it relies on a kind. An absent capability is absent — not
zero, not `false`, not an empty distribution.

## Not in v0

- **Several decisions on one context in one request.** Each decision is one request. A provider that
  can batch may do so behind its adapter; the shape does not expose it.
- **A continuous score value.** The expected level under a distribution, or any other scalar, is
  derived from the evidence by whoever wants it.
- **Calibration.** A calibration layer that turns `score`, `logit` or `margin` evidence into
  `probability` evidence produces a result in this same shape; it is not part of the protocol.
- **Provider-side abstention in practice.** `abstain` is in the outcome for providers that do it; no
  provider in the alpha does, and the abstention that matters is the policy's.
