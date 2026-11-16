# 0025. A policy's digest names exactly one policy, and every verdict and evaluation span carries it

**Status:** Accepted
**Date:** 2026-11-16

## Context

A verdict named its policy by id and nothing else. An application that stores verdicts, and later
moves a threshold, refits a calibration, adds a Choice option or switches a policy from Shadow to
Enforce under the same id, could not tell afterwards which version issued a stored verdict. Replaying
stored decisions through the evaluation tool ([0013](0013-evaluation-records-answers-and-replays-them-through-core.md))
has the same gap: a row judged under one version and replayed under another is not the same
measurement.

A policy already has one serialized form, the JSON `SemanticPolicyJson.Options` writes, but those
bytes are not a stable thing to hash. Members come in declaration order, so reordering a record's
properties in C# moves them. Numbers come in .NET's forms — `1E-05`, `1E+21`, `-0` — and the Web
encoder escapes non-ASCII characters and `< & ' +`. A hash over them could be recomputed only by .NET,
and only by the same declarations.

Two other constraints were real. Some applications build a policy per call, with options from a set
that changes, so any value that varies with the policy can grow without bound. And
[0008](0008-telemetry-and-content-logging.md) forbids "a hash or a prefix of the content computed by
the library" as a correlation key, which raised the question of whether a hash of a policy is one.

## Decision

1. **`Policy.Digest` covers the whole policy.** It digests the JSON `SemanticPolicyJson.Options`
   writes: the id, the mode, the failure behaviour, the budget, every rule with its question,
   criteria and option descriptions, every binding with its thresholds, gates and calibrations,
   provenance included. There are no exceptions: switching Shadow to Enforce, or changing the id,
   gives another digest.

2. **The digest is SHA-256 over the RFC 8785 (JCS) canonical form of that JSON**: members sorted by
   their names' UTF-16 code units at every depth, arrays in order, no whitespace, strings escaped and
   numbers written as ECMAScript writes them, `-0` as `0`, all of it UTF-8. The value is `sha256:`
   followed by 64 lowercase hex digits. Any JCS implementation in any language recomputes it from the
   policy's JSON.

3. **The digest of an unchanged policy is stable across library versions.** A test pins the
   canonical JSON and the digest of a fixed policy that uses every kind of rule, binding, gate and
   calibration. A property added to a policy later is optional and is omitted from the policy's JSON
   at its default value, so every existing policy serializes as before. A change that has to alter the
   canonical JSON of existing policies is a new scheme under a new prefix, documented beside
   `sha256:`, never a change under it.

4. **The digest is computed, not stored.** It is not part of the policy's JSON, which it would
   otherwise have to digest. It is computed on first read and kept for that instance, keyed by the
   instance and held outside the record's fields, so a `with` copy is another instance with the digest
   of its own content, and record equality compares no cached value. Reading it does not validate the
   policy; `Validate` does that. A policy that has no JSON form — a NaN or infinite number, a rule
   other than a Boolean, Choice or Score rule, an enum member holding a value its enum does not define
   — has no digest, and reading it throws `PolicyConfigurationException` with the policy's id and
   nothing the policy's author wrote.

5. **Every verdict carries it.** The step function ([0010](0010-core-decision-runtime-architecture.md))
   sets `PolicyVerdict.PolicyDigest` on every verdict it returns, live and in replay, and it
   serializes as `policyDigest`. It is an init-only property outside the positional parameters, so the
   constructor and `Deconstruct` are as before, and it is null on a verdict read from JSON written
   without it. No frontend computes a digest: the guards, the agent integrations, the validation rules
   and the MCP gateway already hand the verdict to the application, and the digest goes with it.

6. **Telemetry tags it on the evaluation span only**: `semanticpolicy.policy.digest` on the
   `semanticpolicy.evaluate` activity. An attempt span is a child of that activity and is read with
   it. No instrument gains the tag; metrics stay keyed by policy id.

7. **[0008](0008-telemetry-and-content-logging.md) reads this way: a policy is configuration, not
   judged content.** 0008's content is what a provider judges — the question's context, tool arguments
   and results, the provider's raw output, what a user typed into the judged input. A policy is
   written by the application, so its digest is tagged by default, with no switch. The documentation
   of `Policy.Digest`, `PolicyVerdict.PolicyDigest` and the tag says that the digest covers the
   policy's own text, questions and option descriptions included: an application that builds those
   from what a user typed sends a hash of that text to its traces and its stored verdicts.

8. **The canonicalizer is Core's own, and internal.** Core gains no package for it, and no public JCS
   API. It is tested on RFC 8785's own samples: the §3.2.2 sample with its canonical form, the §3.2.3
   sorting sample and the numbers of Appendix B, with the forms .NET writes otherwise.

## Consequences

- A stored verdict, a replayed one and a trace each name the exact policy behind them. Two verdicts
  with the same digest came from the same policy, and anyone holding a policy's JSON can check a
  digest without .NET.
- **Every later change to a policy's shape inherits point 3.** A new property on `Policy` or on
  anything a policy holds is optional and omitted at its default, or it moves the digest of every
  existing policy; the pinned test fails when it does. Reordering C# properties is free, because the
  canonical form sorts members. Renaming a property or an enum member is not: its JSON name comes
  from the C# name, so the rename moves the digest of every policy that writes it, unless
  `[JsonPropertyName]` or `[JsonStringEnumMemberName]` keeps the old name.
- The step function reads the digest on every evaluation. A registered policy pays for one
  canonicalization and one SHA-256 once; a policy built per call pays for them on every call.
- A policy can pass `Validate` with an enum member holding a value its enum does not define: the
  mode, the failure action, the verdict of a Choice option, a ladder rung or a Score rung, or the
  evidence kind of an uncalibrated threshold or gate. Such a policy has no digest, so every
  evaluation of it throws `PolicyConfigurationException`. A cast from an integer produces such a
  value, and so does a verdict or an evidence kind in a policy's JSON that joins member names with a
  comma, such as `"abstain, deny"`: `JsonStringEnumConverter` ORs the names even for an enum without
  `[Flags]`.
- The digest is a hash of the policy's text, not a secret. A short policy can be recovered from it
  by enumeration, and option descriptions built from user input reach traces as a hash. The
  documentation says so; nothing prevents it.
- Metrics cannot be split by policy version. A chart per version needs traces.
- Core owns a small piece of RFC 8785: the layout of a number, string escaping and member order, with
  the shortest round-trip digits from .NET itself. The RFC's samples are the conformance claim, and
  they cover only what a policy's JSON can hold. A lone surrogate never reaches it, because
  System.Text.Json writes one as U+FFFD.

## Alternatives considered

- **Everything but the mode and the id.** Lost because each exception needs documenting, and a switch
  from Shadow to Enforce would vanish from a policy's version history.
- **The rules' shape only.** Lost because it names neither the thresholds nor the calibration, which
  are what change between versions.
- **SHA-256 over the bytes the library already writes, guarded by a golden test.** Lost because the
  value is reproducible only in .NET, and a reordered declaration breaks it.
- **Bare hex.** Lost because the value would not say which scheme made it, and a later scheme could
  be confused with this one.
- **The first 16 hex digits.** Shorter tags, but lost for the weaker guarantee in an audit.
- **The tag on the evaluation span and on the two policy instruments.** Charts per version without
  traces, but lost for unbounded cardinality where a policy is built per call.
- **The tag on every attempt span as well.** Lost because it is redundant in a full trace.
- **The tag only on opt-in.** Lost because it adds a switch against a weak risk: the policy is
  configuration.
- **No tag.** Lost because a trace could not name the policy version behind its verdict.
- **A private lazy field.** Lost because a `with` copy copies private fields, so
  `shadow with { Mode = PolicyMode.Enforce }` would carry the Shadow policy's digest, and record
  equality would compare it.
- **A hand-written copy constructor that skips that field.** Lost because a property added later and
  missed there would drop out of every copy, silently.
- **Computing the digest on every read.** Lost because a registered policy would pay for it on every
  evaluation.
- **Validating on first read.** Lost because a digest names content, not validity, and a check added
  in a later release would make `Digest` throw for a policy that had one.
- **Null or an empty string for a policy with no JSON form.** Lost because a non-digest would travel
  silently into verdicts and traces.
- **Passing the serializer's own exception on.** Lost because an `ArgumentException` from a property
  that takes no argument, or a `JsonException` from a policy built in code, says nothing a caller can
  act on.
- **Rejecting `-0`.** Lost because `Validate` accepts `-0.0` wherever it accepts a number, so a valid
  policy would have no digest, and `-0` and `0` compare equal wherever evaluation reads them.
- **A sixth positional parameter on `PolicyVerdict`.** Lost because it moves a public constructor and
  `Deconstruct` for a value only the step function sets, and a verdict read from JSON written before
  this record has no digest, so the property is nullable either way.
- **A JCS package, or a vendored reference implementation.** Lost because the .NET packages were
  young or carried code under more than one licence, one rejected `-0`, and any of them would be a
  runtime dependency that every application referencing Core inherits; the reference
  implementation's number code is what .NET already provides.
- **A public canonicalizer.** Lost because it would be a JCS API nobody asked for, supported from
  then on.
