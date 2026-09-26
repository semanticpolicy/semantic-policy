# 0015. Self-hosted decision models are reached through one System One client

**Status:** Accepted
**Date:** 2026-09-26

## Context

System One is TypeSafe's HTTP API for decision models: a call to `/v1/systemone` sends the content to
judge and one or more typed questions, and gets a structured answer to each. In `0.1.0-alpha.1`, the
only code that spoke it sat inside the TypeSafe package, as the adapter for Jev. Open servers
that run a decision model on your own machine, such as Von, Laya and kev, answer the same API, and a
probe of all three found that the adapter's reading handled every answer they gave. They differ from
Jev in ways that bind code outside any one adapter:

- their numbers order answers, but nobody has checked that they are calibrated;
- some cut a long input without saying so, so an instruction past the cut scores like benign text;
- they run on loopback or on a private network, with a key or without one;
- they are not Jev, and a package named after TypeSafe is the wrong place to register them.

Reaching them raised seven questions:

1. Where does the wire code live, and what does the TypeSafe package become?
2. How do two packages share that code without new public API and without version drift?
3. What kind of evidence is a self-hosted server's number?
4. How does a Boolean answer read when it is not a probability?
5. Is a self-hosted server's answer read more loosely than Jev's?
6. When may content cross a network in clear text?
7. What happens to an input longer than a server reads?

Measuring them in the evaluation tool raised two more: what `run` does when a policy binds only one
of two registered providers, and how a curve stays small when the evidence is unrounded.

## Decision

1. **One client for the API, and TypeSafe's Jev is a preset on it.**
   `SemanticPolicy.Providers.SystemOne` holds the wire code (the request writer, the strict reader,
   the status table and the timed call) and registers any `/v1/systemone` server with
   `AddSystemOne`. `SemanticPolicy.Providers.TypeSafe` is built on it and keeps its public shape: its
   routes, a mandatory key, and `Probability` evidence on the `calibrated` scale. `AddTypeSafeJev`
   stays Jev's, at TypeSafe's endpoint or a gateway in front of it, and a server that runs another
   model is registered with `AddSystemOne`. `SystemOneOptions` mirrors `TypeSafeJevOptions` wherever
   the rules agree.

2. **The wire code is internal, and TypeSafe pins SystemOne exactly.** SystemOne's public surface is
   `AddSystemOne`, `SystemOneOptions` and `SystemOneProvider`. TypeSafe reaches the rest through
   `InternalsVisibleTo`, so its package depends on SystemOne at exactly its own version, `[X]`, and
   not at the minimum that `dotnet pack` writes for a project reference. A target in TypeSafe's
   project file rewrites the range after NuGet computes it. TypeSafe's dependency on Core stays a
   minimum, because it reaches no internals of Core.

3. **A server's number is a score unless the user declares it a probability.** `AddSystemOne`
   reports `Score` evidence on the `systemone` scale by default. `Probability` is accepted only as a
   declaration in the options, and is reported on the `calibrated` scale. It is the user's claim, and
   the provider checks nothing. No other kind is accepted. A policy written with probability
   thresholds then fails with a configuration error before any call, rather than reading a score as
   a probability ([0003](0003-evidence-semantics.md)).

4. **A Boolean answer under `Score` carries both ends.** The API gives one number for *yes*, on a
   scale with two ends. Under `Score` the evidence is `true` at that number and `false` at one minus
   it. Under `Probability` it is `true` alone, as for Jev. The answer is `true` from 0.5 up. The
   contract suite requires both keys from any provider whose Boolean evidence is not a probability.

5. **The reading is as strict for every server as for Jev.** A 200 body is read strictly, and every
   other status maps to a failure kind through one table. A distribution with a key missing is
   `Malformed`, never a partial success ([0006](0006-failure-and-abstention-model.md)).

6. **Content crosses a network in clear text only when code says so.** `https` goes anywhere, and
   plain `http` goes to a loopback host. Plain `http` to any other host needs
   `AllowInsecureHttp = true`, and any other scheme is refused. The options are checked at
   registration. The TypeSafe preset keeps its stricter rule: `https` unless the host is loopback.

7. **An optional context limit, counted on the canonical text, enforced before the call.**
   `MaxContextLength` is off by default. When a context's canonical text
   (`SemanticContext.ToCanonicalText`) is longer, it is not sent: the provider returns
   `Failure(RejectedInput)`, with both lengths in the message and no content. The count uses the
   canonical text although the API sends the structure, so that every client counts the same input
   the same way. What the failure means is the policy's failure behaviour
   ([0007](0007-per-policy-failure-behaviour.md)).

8. **The evaluation tool builds only the providers a policy binds.** `run` resolves the
   registrations its policy names. A registration that cannot be built, such as one whose key is
   missing, stops only the policies that bind it. The "not registered" message and the duplicate-name
   check still read every registered name. The tool does this through Core's public builder, and Core
   does not change.

9. **A curve replays at most 101 candidates, and a sweep still recommends only numbers the recorded
   answers produced.** Over the bound, a rung's curve gives half its places to values reported on
   rows labelled with the flagged answer and half to values on the other rows. Each half is spread
   evenly by rank through its label's values, lowest and highest included, and a label with fewer
   values than its half keeps them all. For `Probability` evidence the 0.05 grid stays whole and
   counts toward the 101, and a sweep never recommends a grid point. A gate curve keeps, after its
   no-gate point, at most 101 of the margins the rows produced, spread evenly by rank, the narrowest
   and widest included. At or under the bound, both curves are exactly what they were.

## Consequences

- A new server or vendor that speaks the API is a registration, not a package. The wire code has one
  copy, so the status table and the strict reading cannot drift apart between two packages.
- SystemOne and TypeSafe ship together, always at one version. A consumer who references another
  SystemOne version directly gets NuGet's NU1608 warning at restore rather than a
  `MissingMethodException` at run time. The exact range rests on a target that runs after one of
  NuGet's internal targets, so a NuGet update could quietly turn it back into a minimum. Nothing
  checks the packed nuspec yet, so each release reads it by hand.
- A policy moves between Jev and a self-hosted server only with new thresholds, measured on data.
  That is the cost of refusing to read a score as a probability, and it is intended.
- A self-hosted server that sends a partial distribution fails every call instead of degrading.
- The context limit counts characters, which only approximate tokens, so code, numbers and other
  languages need a lower limit than English text. The limit is only as safe as the policy's failure
  behaviour. Under `Allow`, an input padded past the limit skips the rule. Under `Fallback`, with a
  second binding, it goes to the next binding.
- A registration that no policy binds can stay broken, unnoticed, until a policy binds it.
- Over 101 distinct values, a curve skips some thresholds, so a recommendation can sit near the best
  one the data allows rather than on it.

## Alternatives considered

- **A self-hosted route in the TypeSafe package, such as `TypeSafeJevRoute.SelfHosted(url, model)`.**
  Lost because `AddTypeSafeJev` would register models that are not Jev, under Jev's calibration
  claim.
- **An `AddSystemOne` alias inside the TypeSafe package.** Lost because it is one adapter under two
  names, in a package named after a vendor these servers are not.
- **Renaming the TypeSafe package to SystemOne.** Lost because the namespace would move in the
  examples, the tools and the README, and a package published in `0.1.0-alpha.1` would be deprecated
  by the next release.
- **TypeSafe built on SystemOne's public API.** Lost because it would need public members that only
  TypeSafe uses, such as its own User-Agent product and a per-call client source.
- **A copy of the wire code in each package.** Lost because two copies of the status table and the
  strict reader drift apart.
- **A minimum-version dependency, and documentation saying that both packages share a version.** Lost
  because nothing stops a consumer who references a newer SystemOne directly, and the mismatch would
  show at run time.
- **`Probability` by default, as for Jev.** Lost because it is a calibration claim nobody checked.
- **A mandatory evidence declaration with no default.** Lost because every user would have to
  understand [0003](0003-evidence-semantics.md) before running anything.
- **An `uncalibrated` scale label, or a separate `declared` label for a user's `Probability`.** Lost
  because the kind already says the first, and the provider id already says whose claim the second
  is.
- **One key for a Boolean answer under `Score`.** Lost because Core rejects a Boolean reading that is
  not a probability and has one key, as malformed.
- **A Core change that completes a score with its complement.** Lost because Core's rule that a score
  has no complement is deliberate.
- **A looser reading for self-hosted servers.** Lost because none of them needed it, and a partial
  distribution read as a success is as wrong for them as for Jev.
- **Plain `http` to loopback only, as for TypeSafe.** Lost because a sidecar in a container network
  would then need TLS.
- **Plain `http` to any host.** Lost because content would cross a network in clear text on nobody's
  decision.
- **Long contexts documented, with no limit in the client.** Lost because the client would send
  everything and the miss would stay silent.
- **A mandatory limit.** Lost because characters only approximate tokens, and every registration
  would have to pick one.
- **Counting the serialised JSON.** Lost because it counts quotes, keys and escapes, and would differ
  from another client's count for the same input.
- **Resolving every registration up front.** Lost because a missing key would stop every policy's
  `run`.
- **A Core change that lists registration names without building them.** Lost because Core's public
  surface already allows lazy resolution.
- **A fixed grid for score and logit evidence.** Lost because it is an arbitrary set of numbers on a
  provider's own scale.
- **Rounding the observed values.** Lost because it changes the thresholds a user copies into a
  policy.
- **Computing curves outside Core's replay.** Lost to
  [0013](0013-evaluation-records-answers-and-replays-them-through-core.md).
