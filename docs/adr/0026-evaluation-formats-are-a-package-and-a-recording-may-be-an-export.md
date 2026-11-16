# 0026. The evaluation formats ship as a package, a row may carry its Choice options, and a recording may be an export

**Status:** Accepted
**Date:** 2026-11-16

## Context

[0013](0013-evaluation-records-answers-and-replays-them-through-core.md) fixed the evaluation tool's
two formats — a dataset of labelled rows, and the recording `semanticpolicy/evals-recording/v0` — and
a replay that accepts the same question with different numbers.
[0020](0020-evaluation-tool-retries-resumes-and-gates-a-build.md) kept both `v0` and made every
addition optional. The formats existed only as the tool's README and the tool's internal types, and
the tool is a dotnet tool, which no application can reference.

An application that already decides in production wants to measure those decisions offline: write
the inputs it judged as a dataset and its stored verdicts as a recording, then let `report` replay
them under the policy that made them, or under a candidate. Three things stood in the way:

1. Both formats had to be written by hand from the README, and nothing said a file was wrong until
   `report` read it.
2. A Choice rule's options were fixed by the policy file. An application that builds its options per
   request — a router over a set of targets that changes — could not put calls with different options
   into one dataset, and 0013 §3 required the replayed rule's options to match the recorded rule's.
3. A recording assumed a `run`: every binding asked on every row, `parallel` and `timeout` filled in,
   and a header `--resume` can finish. A stored verdict holds only the attempts its evaluator made,
   since a cascade stops at the binding that decides, and nothing on disk said which version of a
   policy decided a row. [0025](0025-a-policy-digest-names-exactly-one-policy.md) gave every verdict
   the digest of its policy.

## Decision

1. **The formats are a package, `SemanticPolicy.Evals.Format`, and the tool keeps no second copy.**
   It sits under `src/`, references Core, and ships with the other packages at one version. Its
   public surface has four parts and no more:
   - the records of a dataset row and of a recording;
   - the writers: `DatasetWriter`, which appends rows as a stream and returns the SHA-256 of the
     bytes it wrote, equal to the digest the reader computes, and `RecordingWriter`, which writes the
     header and then each row;
   - the readers, with the tool's validation and its messages;
   - the conversion of a `PolicyVerdict` into a recorded row, and `RecordingHeader.ForExport`, the
     header of a recording made that way.

   Metrics, replay, sweeps and calibration stay in the tool. The package throws its own
   `EvalsFormatException`, which carries a message only, and the tool maps it to its usage-or-data
   exit code. The tool carries the library inside its own package, as it carries Core, so it still
   installs as one package with no NuGet dependency
   ([0016](0016-evaluation-tool-ships-as-a-dotnet-tool.md)).

2. **From the package's first release both formats are public API.** The dataset schema and
   `semanticpolicy/evals-recording/v0` change only by optional additions, or under a new format
   version. Everything this record adds is optional, so both stay `v0` (0020 §1), and every dataset
   and recording written before it, the committed ones included, reads and replays unchanged.

3. **A dataset row may carry the Choice options it was shown.** `options` maps a Choice rule's id to
   a list of options in the policy file's own shape: `key`, `description`, `verdict`. A row without
   options for a rule uses the policy's; a row with them replaces them for that row only. A rule built
   from a row's options is an ordinary `ChoiceRule`, Core does not change, and the policy file stays a
   valid policy.
   - A row's options are checked by building the policy with that rule's options replaced and calling
     `Policy.Validate`, so a row and a policy share one definition of a valid Choice rule. `options`
     naming no rule of the policy, or a rule that is not a Choice rule, is an error, and the row's
     label must be one of the row's keys.
   - An option key is an identifier, like a rule id or a label, and a message may name it. An
     option's description is text a provider reads, and no message quotes it.

4. **`run`, replay and the shape check use a row's own options.** `run` asks each row with its own
   options, and replay evaluates each row under a policy carrying them, built once per distinct option
   set. 0013 §3's comparison of a Choice rule's options with the recorded rule's applies only to rows
   that carry none; for a row that carries its own, the dataset's digest binds the recording to them.
   What is replayed is still what the provider was shown.

5. **A Choice report over varying options counts the keys the rows used.** When the option sets
   differ between rows, accuracy, per-class precision and recall and macro-F1 are computed over the
   union of keys seen as a label or a prediction, in ordinal order, and the JSON result says how many
   distinct sets there were in `optionSets`. The text then prints the confusion matrix up to 10
   classes and the per-class table always. With one option set the classes are its keys in declared
   order, and the report is byte for byte what it was before this record, whatever the number of
   classes.

6. **A recording names its source.** The header gains an optional `source`:
   `{"kind": "run" | "export", "name": …, "version": …}`. Absent means a run, and `run` writes none.
   An export names the program that wrote it and that program's version, and its `toolVersion` is the
   format package's version. An export's header carries nothing that describes a run — no `parallel`,
   `timeout`, `retries`, `where` or `resumptions` — and the reader refuses an export that carries
   `parallel` or `timeout`.

7. **`--resume` never calls a provider for an export.** It refuses a `source` of `export` before any
   other check, and it refuses a header without `timeout`, as 0020 §4 refuses one without `where` or
   `retries`. Because an export writes neither `where` nor `retries`, every earlier release that has
   `--resume` refuses it too.

8. **Digests are recorded, never compared.** The header carries `policyDigest`, its policy's digest
   (0025). A row may carry `policyDigest`, the digest of the policy whose evaluation produced its
   results; `run` never writes it, and an export does. The tool stores both as written and never
   recomputes or verifies either. A verb counts the distinct row digests among the rows it scores and
   reports the number as `rows.distinctPolicies`, and that is all. A replayed verdict carries the
   digest of the policy the replay built, which differs from the recorded one by construction once a
   replay narrows the policy.

9. **An export keeps only the attempts its evaluator made, and a replay fails only on an attempt Core
   asks for.** `RecordedRow.FromVerdict` keys each rule's attempts by `Attempt.ProviderId`, the
   binding's provider — the name a replay matches against the chain — and leaves out a rule with no
   attempt. A replay hands Core the attempts a row has, and fails only when the step function requires
   one the row lacks; the message names the rule, the provider and the row. This is what 0013 §3's
   "an attempt the replay needs" now means. An export replays under its own policy and gates;
   `compare`, which measures every binding on every row, cannot run on it, and neither can a `sweep`
   whose gates send rows on to a binding production never asked.

10. **A recording stays header-first.** `RecordingWriter` writes the header before any row, so the
    torn-line reader and `--resume` work as before. An export writes its dataset first, takes the
    digest `DatasetWriter` returns, and then writes the recording with that digest in its header.

## Consequences

- An application that references `SemanticPolicy.Evals.Format` and no other evaluation code can
  write a dataset and a recording of its production decisions that `semantic-policy report` replays
  and gates, with no provider and no key.
- The formats can no longer change freely. A member added to either is optional, a member removed or
  renamed is a new format version, and that holds for the package's records as much as for the files.
- A run recording that lacks a binding's attempts now fails at the first row whose cascade reaches
  that binding, not at the first row. A run asks every binding on every row, so a sound run recording
  replays as before.
- A recording without `source` or `policyDigest` — every one written before this record — reads as a
  run whose policy is the header's.
- A tool from before this record rejects a dataset row with `options`, because its reader rejects
  unknown members. It reads an export's header, taking the absent `parallel` and `timeout` as zero,
  but replays it under the every-binding rule this record narrows, so an export is read with the
  release that introduced it or a later one.
- A Choice report whose option sets vary may hold hundreds of classes. Its text drops the matrix
  above 10 classes; the JSON result keeps everything.
- `ambiguous` and `abstain` stay reserved label words while Core accepts them as option keys, so a
  row whose right answer is an option keyed that way cannot carry it as its label.
- A replay validates and digests one policy per distinct option set, so a dataset whose every row has
  its own options pays for both on every row of every sweep point.
- The library ships twice, on its own and inside the tool's package, so the two are released
  together at one version.

## Alternatives considered

- **A specification and a JSON Schema only.** They work in any language. Lost because an application
  would still write JSON by hand and learn that a file is wrong only at `report`.
- **The format's types in Core.** Lost because Core would carry types it never uses at run time.
- **Types and serialization only.** Lost because an application would find out its file is wrong only
  at `report`.
- **Replay and metrics in the package as well.** Lost because it is a large public API nobody asked
  for, all of which would have to stay compatible.
- **The names `SemanticPolicy.Evaluation` and `SemanticPolicy.Evals.Recording`.** Lost because the
  first promises more than the package holds, and the second leaves out the dataset.
- **Copying the types into the package and converting at the tool's boundary.** Lost because each
  format would have two implementations.
- **The tool's own exception in the package.** Lost because a process exit code would become part of
  a library's API.
- **The package's version as a run's `toolVersion`.** Lost because a run would report the library's
  version rather than the tool's.
- **Options as key and description, with the verdict taken from the policy's option of the same
  key.** Lost because a key new to the policy has no verdict, so it would be either an error or a
  default verdict added to Core.
- **Keys only, as a subset of a catalogue in the policy.** Lost because a description that changed
  over time cannot be written down, and the policy would have to hold every key ever shown.
- **Several policies in one recording, each row naming its own.** Lost because the header grows with
  every version of the policy, and a replay under a candidate would need a mapping from each recorded
  one.
- **Options supplied by Core at evaluation time.** Lost because building a policy per call already
  meets the need, and it would be a Core change.
- **All rows or none per rule.** It would catch an export that lost its options in some rows. Lost
  because the policy's options would be dead for that rule.
- **An empty option list allowed in a dataset's policy.** Lost because the policy file would not be a
  valid policy outside the tool.
- **A checker of the package's own for a row's options.** Lost because it would drift from Core's.
- **Always the union of keys.** Lost because it reorders every existing Choice report.
- **A class cap on every Choice report.** Lost because a policy with 11 or more options would lose the
  matrix it prints today. A cap set by a flag lost because it is one more option for a number nobody
  asked to change, and no cap lost because the text is unreadable over hundreds of keys.
- **No per-class metrics when the options vary.** Lost because nothing would show which keys are
  confused with which.
- **A policy built per row in replay.** Lost because Core validates and digests every instance, so a
  sweep would pay both per row per point even when the rows share their options.
- **An export that poses as a run.** No format change. Lost because `--resume` would call providers
  for rows production never had, and `parallel` and `timeout` would be invented.
- **A separate export format, `semanticpolicy/evals-export/v0`.** A cleaner meaning. Lost because it
  is two formats to keep.
- **`where` and `retries` in an export's header, as in a run's.** Lost because an older release's
  `--resume` would then accept it.
- **`run` writing `source` too.** Lost because it is a new shape for no reader. A string `source`
  beside separate name and version members lost because it is three members for one fact.
- **The policy digest in the header only.** Lost because an export that spans several versions of a
  policy could not say which one decided which row. A `metadata.policyDigest` convention in the
  dataset lost because the provenance of the results would sit in the question file.
- **Verifying the header's digest on read, or setting a replayed verdict's digest beside the recorded
  one.** Lost because the two differ by construction once a replay narrows the policy, and the
  comparison would report differences that mean nothing.
- **Keeping 0013's requirement that a replay have every binding's attempt.** Lost because an export
  that stopped at its first binding would fail replay even under its own policy.
- **Keying an exported attempt by the adapter's id, `ProviderResult.Provider.Id`.** Lost because it
  can differ from the binding's provider, and a replay would not find it in the chain.
- **A late header written through `RecordingWriter.ReplaceHeaderAsync`.** Lost because every export
  would rewrite its file, and a torn export would have a header without the digest. A paired writer
  for both files lost because it is a larger API for an order the application keeps by itself.
- **`v1` formats.** Lost because every recording, the committed ones included, would need recording
  again or a migration.
