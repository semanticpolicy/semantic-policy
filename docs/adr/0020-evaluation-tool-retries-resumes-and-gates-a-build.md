# 0020. The evaluation tool retries, resumes, bounds every rate and gates a build

**Status:** Accepted
**Date:** 2026-09-30

## Context

[0013](0013-evaluation-records-answers-and-replays-them-through-core.md) fixed what the evaluation
tool records and how it replays it, and [0014](0014-evaluation-tool-records-live-once-and-ci-replays.md)
how a committed recording is made and used. Four gaps showed once the tool ran outside this
repository:

- `run` calls its providers several at a time, and a hosted provider answers a burst of such calls
  with a rate limit. Each refused call was recorded as a provider failure, so the report measured
  the run's own parallelism as much as the provider.
- A run cut short kept its finished rows and often left a torn last line. Every read verb then
  failed on a file whose other rows were sound, and the only way to finish it was a new run that
  paid for every row again.
- A dataset of this kind holds forty to five hundred rows. On 33 flags, a precision of 0.970 is also
  consistent with a provider that is right 85% of the time, and a rate printed alone to three
  decimals said nothing about that.
- A pipeline could fail a build on a number only by parsing the JSON result itself, and every
  pipeline would have written the same parser.

Each fix touches the recording or the result, both `v0` formats that committed recordings and other
people's scripts already read.

## Decision

1. **The formats stay `v0`, and everything added is optional.** Every member this record adds to
   `semanticpolicy/evals-recording/v0` or `semanticpolicy/evals-result/v0` may be absent, no
   existing member changes its meaning, and the reader reads every earlier `v0` recording, the
   committed ones included. A member that does not apply is left out, as before.

2. **Only an `unavailable` answer is called again.** A rate limit, an overloaded server and one that
   cannot be reached all reach the tool as `unavailable`. `run --retries <n>`, 2 by default and 0 for
   none, calls such an attempt again up to n times. A timeout is never retried, since the call may
   have been slow rather than refused, and every other result is final.
   - The wait before retry k is drawn evenly from [d/2, d], d = min(1 s · 2^(k−1), 30 s). Half the
     range is always waited, so a retry does not go straight back into the limit that refused it,
     and the other half is spread, so calls refused together do not come back together. No adapter
     passes a `Retry-After` on, so the wait is the tool's own.
   - The attempt keeps its place among the `--parallel` calls while it waits, so a provider shedding
     load is not sent another row meanwhile. Each call gets its own `--timeout`.
   - The recorded attempt is the last call's result. One still `unavailable` after its last retry is
     recorded as that failure, for a resume to call again.

3. **A retry never disappears.** The header records the run's `retries`. A row whose attempt was
   retried carries a `retries` map beside `attempts`, keyed the same way by rule id and provider
   name, counting the calls made after the first; a row with no retried attempt has none. The
   attempt itself stays exactly what Core serializes (0013 §2). The report's providers table counts
   the attempts retried at least once. An attempt a resume calls again records the resume's count.

4. **A resume answers exactly the rows the first run selected, asked the same way.** A new run's
   header records `where`, its `--where` filters as `metadata.<key>=<value>`, an empty list when it
   had none. `run --resume <recording>` calls every binding for each selected row the recording
   lacks, calls again only the attempts recorded as `unavailable`, and keeps every other answer.
   - Before any call it checks that the policy, compared as the library serializes it, is the one
     in the header, that each dataset's digest matches, and that `--timeout` and `--where`, when
     given, are the header's; omitted, the header's are used, and filters compare as a set. A header
     without `where` or `retries` is refused, and so is `--record` beside `--resume`. Any mismatch is
     exit 1 with the file unchanged.
   - `--parallel` and `--retries` may differ. The header keeps the first run's values and lists every
     resume in `resumptions`, each with its `resumedAt`, `parallel`, `retries` and `toolVersion`.
   - With nothing to call, the resume builds no provider, leaves the file untouched and prints the
     report.

5. **A row already paid for is never at risk.** A resume writes to `<recording>.tmp` and that file
   replaces the recording only when it holds every row the recording held; otherwise it is deleted.
   A resume cut short writes the rows it finished and then the recording's remaining rows as they
   were, calling nothing, so a second resume carries on from there. A row's line is never cancelled
   halfway. After Ctrl+C the tool gives `run` 30 s to write out, not the command-line library's 2 s,
   because that last step grows with the recording. A hard kill leaves the recording as it was.

6. **Only a torn last line is skipped.** The last non-blank line of a recording, when it is a row
   line and not valid JSON, is skipped: `report`, `sweep` and `compare` name it, the JSON result's
   `rows.tornLine` holds its number, and its row counts as not recorded, so a resume calls it. A torn
   line anywhere else, a torn header, or a last line that is JSON but not a row still fails, naming
   its line.

7. **A committed recording is still one uninterrupted run.** This extends 0014 §2. A retried
   attempt is allowed in it: the run is still one, and in the end nothing failed. A recording with a
   resumption is recorded again before it is committed, and the test that replays the committed
   recordings checks that none lists one.

8. **Every proportion carries a 95% Wilson interval.** It is the Wilson score interval without
   continuity correction, z = 1.959963984540054, computed from the same integer counts as the rate.
   It ends at exactly 0 when no row counts and at exactly 1 when every row does, and a rate with
   nothing to divide by has none.
   - Accuracy, precision, recall and the false positive and negative rates of every matrix, curve
     points included, carry one; so do the failure and abstention rates, a Choice or Score rule's
     accuracy, each class's precision and recall, and a gate point's abstention rate and accuracy.
     A proportion added later carries one the same way.
   - F1, macro-F1, ROC-AUC, PR-AUC, ECE and the Brier score carry none: none is one count over
     another, and an interval for them would need a resampling method of its own.
   - The level is fixed; there is no option for it. In the JSON result the interval is a sibling
     member named after its rate, `<rate>Interval`, holding `lower` and `upper`, and it is left out
     with its rate.

9. **`--require` gates a build on the exact value, and the interval only warns.** `report` and `run`
   take `--require`, repeatable, and every requirement must pass: a rung's `min-precision`,
   `min-recall` or `max-fpr` on a Boolean rule, `min-accuracy` or `min-macro-f1` on a Choice or Score
   rule, `max-abstain` or `max-failure-rate` on any.
   - A requirement reads what the report measures: the rows after `--where`, the rule `--rule`
     selects, and the chain's verdicts at the policy file's own thresholds and gates. It chooses
     nothing, and the report reads the same with it and without it.
   - A `min-` requirement passes at or above its goal and a `max-` one at or below it, on the exact
     value, never the three-place number printed. A rate with nothing to divide by fails.
   - A passing requirement whose interval bound on the goal's side misses the goal warns. A warning
     never changes the exit code.
   - A malformed requirement is exit 1 before any file is opened, and one the rule cannot have is
     exit 1 once the policy is read, before `run` calls a provider. A failed requirement is exit 2,
     after `run` has recorded every row, `--out` is written and the text is printed. The JSON result
     carries a `requirements` array only when `--require` was given.

## Consequences

- A pipeline replays a recording committed next to its dataset and fails when a policy change makes
  the rule worse on those rows, with no key, no server and no cost. Every recording made before,
  the committed ones included, still replays.
- A burst of rate limits no longer reads as a provider's failures, and a run cut short, or one that
  still holds `unavailable` answers, is finished without paying for its other rows again.
- A server that is not running also answers `unavailable`, so a run against it takes longer to fail
  than before. `--retries 0` restores the old behaviour.
- A waiting retry holds its slot, so a rate-limited provider slows the whole run rather than letting
  the other calls race ahead into the same limit.
- A recording made before this record cannot be resumed, because its header does not say which rows
  it selected. The committed recordings are among them; 0014 never resumes them anyway.
- A resume cannot check where a registration name points now. A providers file pointed at another
  server or model mixes two providers' answers under one name, and only the header's first reported
  model hints at it.
- A gate passes on the rows it replays, not in general. A goal of 0 or 1 always warns when it
  passes, since a finite number of rows never rules out a small rate; the exit code ignores it.
- The formats cannot drop or rename a member without leaving `v0`, so every later addition is one
  more optional member.

## Alternatives considered

- **Retries only when asked for.** Lost because every rate limit a parallel run set off would be
  recorded as a provider failure unless the user knew to ask.
- **Retrying a timeout too.** Lost because a slow call was not refused, and calling it again doubles
  the cost and the wait of the slowest provider.
- **Full jitter, a wait anywhere from zero.** Lost because a wait near zero goes straight back into
  the limit that refused the call. Decorrelated jitter lost because it is hard to state in the
  documentation.
- **Recording the first failure rather than the last result.** Lost because the report would not read
  what the provider finally answered.
- **A `retries` member inside the attempt.** Lost because the attempt would no longer be Core's
  serialization, the tool would keep a converter for it, and a later Core member of that name would
  collide.
- **The retry setting in the header alone, or a count printed only after the run.** Lost because a
  provider that needed three calls a row would read like one that answered at once, and a count on
  the terminal is gone with the terminal.
- **A resume that fills missing rows only.** Lost because a finished run that caught a burst of rate
  limits could then be repaired only by a new run.
- **A resume that takes its filters from the command line alone.** Lost because it could send rows the
  first run left out, or never call rows it selected.
- **A resume into a new file named by `--record`, or rewriting the recording in place.** The first
  leaves two files for one run; the second loses rows already paid for when the rewrite is
  interrupted.
- **Skipping any line that does not parse.** Lost because a damaged file would replay as a smaller
  one without a word.
- **Allowing resumed recordings in the repository.** Lost because it would supersede 0014 §2 for a
  convenience.
- **`v1` formats.** Lost because every recording, the committed ones included, would need recording
  again or a migration.
- **An object in place of each rate, holding the value and its bounds.** Lost because it breaks every
  `v0` reader.
- **The normal approximation.** Lost because it leaves [0, 1] near the edges and collapses to a point
  at 0 of n and n of n, the rates a small, clean dataset produces most.
- **Judging a requirement on its bound.** Honest on small datasets. Lost because the shipped smoke
  example would fail, and a switch between value and bound is one more option to explain.
- **Judging a requirement on the printed value.** Lost because 0.9496 would pass `min-precision=0.95`.
- **`min-roc-auc` and `min-pr-auc`.** Lost because a threshold-free measure can hold while the
  operating point the policy acts at gets worse.
- **Requirements for Boolean rules only.** Lost because Choice and Score rules would have no gate.
- **No gate, with each pipeline reading the JSON result.** Lost because every pipeline would write the
  same parser, and the gate is how a pipeline uses the tool.
