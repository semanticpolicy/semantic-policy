# 0014. The evaluation tool calls a provider once, and CI replays what it recorded

**Status:** Accepted
**Date:** 2026-09-24

## Context

[0013](0013-evaluation-records-answers-and-replays-them-through-core.md) fixed what a recording holds
and how a replay checks it. The tool shipped with no provider registered, so no recording could be
made, and its documentation could only show output nobody else could reproduce. Registering its
first provider and committing its first recording raised four questions, and each answer binds code
outside the change that made it:

1. Which bytes is a dataset's digest taken over, when Git may convert line endings at checkout?
2. How is a committed recording made, kept and quoted, when making one costs a key, a network and a
   paid call to a third party?
3. What does the recording of the committed smoke set measure?
4. How does the tool register a provider, so that a recording's header tells the truth and a
   missing setting is not a crash?

## Decision

1. **Datasets are LF in every checkout, and the digest stays over raw bytes.** `.gitattributes`
   carries `*.jsonl text eol=lf`. A recording made on one platform replays on every other, and a
   byte that really changed still fails the replay.

2. **A committed recording is made live once, sits beside its inputs, and is only ever replaced.**
   - It lives in the directory of the dataset and the policy it was made from.
   - It is one `run`, made after the dataset, the policy and the recording format have settled.
     Every row has an answer and none failed.
   - It is never edited by hand. When a change to the dataset, the rule or the format stops it
     fitting, the fix is a new run.
   - A test replays it with `report`, without a key and without `--force`, and checks that it
     answers every row. CI never calls a live endpoint.
   - The tool's documentation quotes committed recordings only, so every sample output can be
     reproduced on a fresh clone.

3. **The smoke set is not a benchmark.** Its hundred hand-written rows check that the tool, a
   provider and a policy fit together. No figure read from its recording is presented as a measure
   of a provider or a rule, and the thresholds in the committed policies are illustrative.

4. **The tool registers providers as an application does, and leaves them nothing to decide about
   a run.**
   - `Providers.Register` is the tool's registration, passed to `EvalsCli.Build` as its hook. It
     registers TypeSafe Jev as `jev` on the OpenRouter route, the name and route the examples use,
     so one policy file runs in the tool, the examples and an application.
   - `run --timeout` is the only per-call limit. Each registration sets its adapter's own timeout
     past any limit a run would use, so the timeout in a recording's header is the one that
     applied.
   - An adapter that cannot be built from its configuration, such as a key missing from the
     environment, is a usage error: `run` exits with 1, prints the adapter's own message and calls
     nothing. The tool does not check an adapter's settings itself.

## Consequences

- Every `.jsonl` in the repository is LF on every checkout, a dataset or not. A dataset kept
  outside this repository gets no such protection, and the tool's README says how to set it up.
- A change to the smoke dataset, its rule or the recording format costs a live run, a key and a
  small charge, and the README samples are regenerated from the new recording. The replay test
  fails until then, which is its purpose. A change to thresholds or gates needs no new run,
  because a replay accepts different numbers.
- The replay test proves that the recording still fits, not that the provider still answers the
  same way. A provider that drifts shows only in a new run.
- A provider added to the tool has to push its adapter's timeout out of the way as well, or a long
  `--timeout` cuts calls short while the header names the longer limit.
- An adapter that reports a missing setting with anything but `PolicyConfigurationException` still
  reaches the user as a stack trace.

## Alternatives considered

- **Digest normalized text inside the tool.** Lost because a recorded digest would stop meaning
  "these bytes", and a real change could hide behind a line-ending one.
- **Replay with `--force` in the test.** Lost because it skips the digest check the test exists
  for.
- **A live smoke run in CI.** Lost because every pull request would need a secret, pay a third
  party and depend on its uptime.
- **No replay test.** Lost because a line-ending slip or a file committed under the wrong path
  would ship a recording that no longer replays, with no other symptom.
- **A directory of recordings of their own.** Lost because a recording is only useful next to the
  inputs it replays against.
- **No committed recording.** Lost because the commands that read a recording could then be
  documented only on output nobody can reproduce.
- **Quote a trial run in the README.** Lost because it came from a local build and a policy nobody
  else has.
- **Present the smoke recording's numbers as a measure of Jev.** Lost because a hundred
  hand-written rows cannot support a published precision.
- **Check the adapter's environment variable in the tool before resolving it.** Lost because the
  tool would duplicate the adapter's rule and its variable name, and the two would drift apart.
- **Leave a missing key on the path for unexpected exceptions.** Lost because a new user would get
  a stack trace for a setup step the README documents.
- **Keep the adapter's ten-second default.** Lost because a run with a longer `--timeout` would cut
  calls at ten seconds while its header named the longer limit.
- **Pass `--timeout` into the registration.** Lost because the hook would have to carry run
  options, and two clocks set to one value race.
