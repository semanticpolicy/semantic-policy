# 0008. Telemetry carries metadata only; logging the judged content is an explicit opt-in

**Status:** Accepted
**Date:** 2026-09-20

## Context

Everything this library evaluates is, by construction, the thing an application least wants in a
log: the user's prompt, a document retrieved on their behalf, the arguments of a tool call, what a
tool returned. A prompt-injection guard sees every prompt. A tool-result guard sees every document.
Debug logging that includes "the input" is the most natural line to write in a provider adapter and
the most expensive one to have written when the log store is read by the wrong person or subpoenaed.

The threat model lists telemetry leakage and exposure to remote providers as threats in their own
right, and the mitigation for the first is the same in every source: log identifiers and numbers,
never content. The question was how far "never" goes and how content logging is allowed back in.

## Decision

**Every telemetry event the library emits carries metadata and no content.** The default event
from a policy evaluation contains:

- policy identifier and rule identifier;
- provider identifier and model or version identifier;
- decision type;
- provider outcome and, on failure, the failure kind ([0006](0006-failure-and-abstention-model.md));
- evidence kind and value — a number and its kind, never the text it was computed from
  ([0003](0003-evidence-semantics.md));
- verdict, mode, and in Shadow the verdict that would have applied;
- which threshold was crossed, when one was;
- whether a fallback ran and to which provider;
- latency.

**Never in a default event, log line, trace attribute or exception message:**

- the question's context: prompts, conversation, documents, retrieved passages;
- tool arguments and tool results;
- the provider's raw output;
- anything a user typed or a tool returned, in whole or in part.

**Content logging is opt-in, off by default, and scoped.** It is enabled per policy or per provider
by a setting whose name says what it does; it is not a log level, and no global "debug" or
"verbose" switch turns it on. Where it is introduced, the documentation says what it captures and
that it must stay off in production unless the data classification of the content allows it.

**The rule applies to the code, not only the runtime.** Tests, fixtures and example data carry no
real customer content, no real keys and no live endpoints. A provider adapter does not log request
or response bodies, including on error: a `Malformed` failure records that the response did not
parse and how long it was, not what it contained.

**Correlation without content.** Where a deployment needs to tie a verdict back to the input it
judged, it does so with an identifier the application supplies, not with a hash or a prefix of the
content computed by the library.

The same events are the source for any later OpenTelemetry integration; that integration exports
the fields above and adds none.

## Consequences

- A default deployment can be pointed at any log store without a data-classification review of the
  library, because the library's events contain nothing to classify.
- Debugging a wrong verdict needs the input, and the library will not have kept it. The
  evaluation CLI, which runs on a dataset the user already holds, is the place to reproduce a
  verdict; production telemetry is not.
- Provider adapters need a deliberate error path that describes a failure without quoting the
  response. This is slightly more code on every adapter and is enforced in review.
- Opt-in content logging, when it exists, is a per-policy decision with a name in configuration
  that a reviewer can search for.
- A redaction layer — regex out card numbers, mask names — is not part of the default and is not
  offered as one: a heuristic that misses one pattern turns a privacy default into a privacy
  incident. If one is ever added it is an opt-in on top of the opt-in.

This forecloses a `LogLevel.Debug` that includes content, and it forecloses a provider adapter that
logs the body of a failed response.

## Alternatives considered

- **Log everything at Debug, nothing at Information.** Lost because log levels are set per
  environment by people who do not know what this library's Debug contains, and "we turned on Debug
  to chase a bug" is how content reaches a log store.
- **Log content by default with redaction.** Lost because redaction is a heuristic over
  unstructured text and the failure mode is silent. A default has to be right when nobody is
  looking at it.
- **Log a hash of the content for correlation.** Lost as a default because a hash of a short input
  is reversible by enumeration and because the application already has an identifier for the
  request. It may be an opt-in later; it is not free.
- **Leave it to the host's logging configuration.** Lost because the host cannot filter what it
  did not know the library would emit. The library has to be safe before configuration.
