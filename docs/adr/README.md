# Architecture decision records

One record per decision that constrains code outside the change that made it. If someone working on
an unrelated part of the library six months from now would make a worse choice for not knowing it, it
belongs here.

## Format

`NNNN-short-slug.md`, four digits, numbered in the order they are accepted. A record is **immutable
once merged**: a decision that no longer holds is superseded by a new record that says so, not edited
in place. The history of what was believed and when is most of the value.

```markdown
# NNNN. Title in one line

**Status:** Accepted | Superseded by [NNNN](NNNN-slug.md)
**Date:** YYYY-MM-DD

## Context

What made this a question. The constraints that were real at the time — not a summary of the final
answer.

## Decision

What was decided, in the present tense. "Core does not reference any provider package."

## Consequences

What this makes easy, what it makes hard, and what it forecloses. Include the costs; a record with
only benefits was written to justify rather than to record.

## Alternatives considered

Each with the reason it lost. An alternative with no stated reason reads as one nobody thought about,
and it comes back every six months.
```

## What does not go here

Anything commercial: pricing, competitors, what gets built next quarter, who a feature is aimed at.
This directory is read by contributors deciding how to write code, and a record they cannot act on is
noise at best.
