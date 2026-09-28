# 0017. Any protocol v0 server is reached over one HTTP binding

**Status:** Accepted
**Date:** 2026-09-28

## Context

[Protocol v0](../protocol-v0.md) defines a provider's request and result as JSON, and until
`0.1.0-alpha.2` said nothing about how they travel. A model that is neither Jev nor a System One
server, such as a classifier behind a small service of its own, had no client: its owner wrote an
`IDecisionProvider`, and nothing documented how.
[0015](0015-self-hosted-models-through-one-system-one-client.md) reached System One servers. This
record reaches the rest, in `SemanticPolicy.Providers.Http`, and fixes what every HTTP provider in
the library now shares.

A v0 server is someone else's code. The client cannot check that it keeps the protocol's rules, and
the ways it can break them bind code outside this package:

- a proxy or a framework in front of the server answers errors in a shape of its own;
- the result record fills an absent member with a default, so an evidence entry without a kind
  would read as a probability;
- the server's text, in a message, an evidence key or a model name, travels wherever a result is
  written;
- a text-only server flattens a structured context its own way.

Reaching such servers raised seven questions:

1. How do a v0 request and result travel over HTTP?
2. When is a body a v0 result, and how strictly is it read?
3. Who says what a server answers?
4. What of the server's answer does the client relay?
5. Who turns a structured context into text?
6. Where does the transport code live, now that three packages speak HTTP?
7. What does someone get who writes a provider of their own?

## Decision

1. **The binding is `POST {base}/v0/decide`, and `200` is for an answer only.** The request is the
   body, sent as `application/json`, with `Authorization: Bearer` only when a key is configured;
   the path is an option. A `success` or an `abstain` comes back on `200` with the result as the
   body. A failure is a status outside 2xx and may carry a v0 result whose outcome is `failure`,
   whose `kind` then wins over the status. Servers are told 401 or 403 for `unauthorized`, 400, 413
   or 422 for `rejectedInput`, 429 or 503 for `unavailable` and 504 for `timeout`. Without a failure
   body the client maps the status through the table every HTTP provider uses, in which a bare 504
   is `unavailable`. A failure on `200` is `malformed`, and so is any other 2xx. The binding is a
   section of `docs/protocol-v0.md`. The shapes do not change, so v0 stays v0.

2. **A body is a v0 result only when its JSON says so, and it is read strictly.** The JSON itself
   must carry `"protocol": "semanticpolicy/v0"`, a `type`, an `outcome` object with a `status`, a
   `provider` object, and evidence entries that each have a `kind` and `values`. These are checked
   before the record is trusted. Member names match only in the protocol's case, numbers only as
   JSON numbers, and an enum value only when the protocol names it. On a `200`, a result of another
   type is `malformed`, and so are a value the request did not offer, evidence keyed outside the
   request's answers, a value that is not finite, a probability outside [0, 1] and a second entry
   of a declared kind. Reading never throws. On a status outside 2xx, only a whole v0 failure body
   names the kind.

3. **The user declares what the server answers, and the result stays inside the declaration.** The
   decision types, the evidence kinds and whether the server reads a structured context have no
   default, and a registration that leaves one out fails. An empty evidence list declares a server
   with no evidence ([0011](0011-absence-of-evidence-is-an-empty-list.md)). `Probability` may be
   declared, as the user's claim that the numbers are calibrated, which the client does not check
   ([0003](0003-evidence-semantics.md)). Evidence of a kind nobody declared is dropped. The
   registration also names the model. It is never sent, since the request has no model, and it
   names every result whose answer names no model and every result the client builds itself.

4. **Nothing the server wrote leaves `Raw` except identifiers.** From a `success` or an `abstain`
   the client relays a success's value, the outcome's status and the declared evidence. The
   evidence's scale, the model and the request id are relayed only as identifiers, 1 to 200
   printable ASCII characters without a space. The outcome's message, `usage`, `extra` and a value
   on an abstain are not relayed. A failure's message names the status, the `kind` from a failure
   body and the body's length. The whole body stays in `Raw`, which is never serialized. The System
   One and TypeSafe providers apply the same identifier rule to the model, the request id and the
   error code they report.

5. **A text-only server receives Core's canonical text.** With `structuredContext: false` the
   client sends the text `SemanticContext.ToCanonicalText` renders, as a JSON string, and with
   `true` the structure as the application passed it. A server never flattens anything itself, so
   every text-only server reads the same text, and a change to the rendering is a change to Core.
   The rules of [0015](0015-self-hosted-models-through-one-system-one-client.md) for plain `http`
   (decision 6) and for an optional context limit counted on the canonical text (decision 7) hold
   for this client unchanged.

6. **Every HTTP provider shares one transport, compiled in as internal source.** `src/Shared` holds
   the status table, the timed call, the named client's setup, the endpoint, the user agent and the
   key's resolution. The System One and Http packages compile these files in as internal types, and
   neither references the other. TypeSafe uses SystemOne's copy through `InternalsVisibleTo`, as
   [0015](0015-self-hosted-models-through-one-system-one-client.md) decision 2 describes. This
   narrows its decision 1: the status table and the timed call are no longer SystemOne's own. Every
   HTTP provider keeps these rules:
   - the provider's own timer is the only source of `Timeout`, and the caller's cancellation
     propagates;
   - a transport failure is `Unavailable`, named by its error code, and any other exception from
     the client's handlers, such as a circuit breaker's, is `Unknown`, named by its type;
   - a body is read no further than 1 MiB, and a longer one is `Malformed` on a `200` and the
     status's kind otherwise;
   - the named client a registration sets up logs nothing, has no timeout of its own and follows no
     redirect;
   - a base URL may carry a path, which the endpoint keeps, but not a query or a fragment, which
     the options' validation refuses before any call;
   - the key of an Http or a System One registration is optional, is sent as a Bearer token, and
     is read from its variable when the evaluator is first resolved.

7. **A provider of your own gets a guide and a compiled example, not a package.**
   `docs/custom-providers.md` gives the rules an `IDecisionProvider` has to keep, and
   `examples/CustomProvider` keeps them over a Text Embeddings Inference classifier. The example
   references Core only and has tests on a fake transport of its own. The contract suite stays
   unpublished, Core gains no public HTTP helpers, and the repository carries no reference server
   and no image.

## Consequences

- A server that speaks v0 is a registration, not a package, and its author writes against one
  document, the shapes and the binding together.
- A proxy's error page still fails with the kind its status maps to. Only a v0 failure body
  refines it.
- A server whose JSON is slightly off, with a member in another case, a number as a string or a
  probability of 1.0001, fails every call as `malformed` instead of degrading. That is intended,
  and its author finds out on the first call.
- The declaration can be wrong. Evidence of a kind nobody declared leaves the result without a
  word, and a `Probability` declared on numbers that are not calibrated misleads every threshold.
  The client checks the range, not the calibration.
- When a server names its model in something other than an identifier, the result names the
  configured model instead.
- Text-only servers see one rendering. A model that reads Core's text badly needs a Core change or
  `structuredContext: true` with a server that renders the structure itself, not a client option.
- The transport rules changed for the System One and TypeSafe providers too. A handler's exception,
  a body over 1 MiB, a redirect and a base URL with a query behave differently than in
  `0.1.0-alpha.1`.
- The shared source is one copy, but each package ships its own compiled copy. The packages share
  one version, so they behave alike; a consumer who mixes versions gets two behaviours, with no
  clash at run time, because the types are internal.
- A host that replaces a registration's primary handler decides its redirects itself.
- A provider of your own copies its transport from the example rather than referencing it, so a fix
  to the library's transport does not reach it.
- A cold server's first calls end in `Timeout`. Warming it is the user's step, and the
  documentation says how.

## Alternatives considered

- **Always `200`, with a failure in the body.** Lost because a proxy, a load balancer or a dashboard
  between the two would see every failure as a success.
- **The status alone, with the body ignored.** Lost because a status only approximates the kind,
  and the server knows the exact one.
- **The binding in a document of its own.** Lost because one protocol would have two documents.
- **Taking `outcome.kind` from any JSON object that has one.** Lost because a proxy's or a
  framework's error object would choose the failure kind.
- **Reading `protocol` from the JSON and trusting the record for the rest.** Lost because an absent
  `type`, `status` or evidence `kind` reads as its enum's first value, so an entry without a kind
  would reach a probability threshold as a probability the server never claimed.
- **The serializer's web defaults, with names in any case and numbers from strings.** Lost because
  a member named `Evidence` would skip the checks made on `evidence` and still be read as the
  evidence, and the string `"NaN"` would read as a number.
- **Capabilities fetched from the server at start-up, such as from `GET /v0/capabilities`.** Lost
  because it is a protocol change, a network call at registration and a new way to fail.
- **A default declaration, such as every decision type with `Score`.** Lost because it assumes what
  someone else's server returns.
- **Collections that default to empty.** Lost because an undeclared evidence list would read as a
  server with no evidence instead of failing.
- **Failing a call on an undeclared evidence kind.** Lost because a server that adds `logit` beside
  `score` would fail every call unless the user declared kinds no policy reads.
- **Passing undeclared kinds through.** Lost because a result would carry kinds its provider says
  it never produces ([0002](0002-provider-contract-and-capabilities.md)).
- **Relaying the server's message, `usage` or `extra`, or an error code from a body that is not
  v0.** Lost because text and JSON the library cannot vouch for would reach logs, and a body that
  is not v0 has no known shape.
- **No model option, or an optional one.** Lost because a result the client builds would name no
  model, while Core documents that every provider fills it.
- **Each server flattening the context itself.** Lost because every server would render it
  differently.
- **An option choosing who flattens.** Lost because it is two behaviours to test and document.
- **A copy of the transport in each package.** Lost because two copies drift apart, as
  [0015](0015-self-hosted-models-through-one-system-one-client.md) found for the wire code.
- **Http referencing SystemOne and reaching its internals.** Lost because it ties the v0 client's
  package to an unrelated wire.
- **Public HTTP helpers in Core, or a base provider class.** Lost because they are public API to
  version for code that only the library's own providers use.
- **The contract suite as a package, such as `SemanticPolicy.Providers.Testing`.** Lost for now
  because it is more public API to maintain, bound to one test framework, before anyone asked for
  it.
- **A reference server or a maintained image in the repository.** Lost because CI would neither
  build nor test the server, and an image adds publishing, scanning and versioning to every
  release.
- **A warm-up hosted service, or a health check that makes a real call.** Lost because each is a
  new hosting element or a new dependency for a start-up cost the user controls.
- **A model runtime inside the library, such as ONNX in .NET.** Lost for now because it puts a
  model runtime inside the library, which a server outside it avoids.
