# Security

## Reporting a vulnerability

Report privately through GitHub's **Report a vulnerability** button under this repository's Security
tab, which opens a private advisory. Do not open a public issue for a vulnerability.

Include what an attacker can do, the smallest input that shows it, and the version or commit you
tested. You will get an acknowledgement, and a fix or an explanation of why it is not one.

## What this library is, in security terms

SemanticPolicy evaluates **probabilistic** decisions. A rule returns a verdict and a confidence, and
both can be wrong in either direction on an input nobody anticipated.

That has consequences worth stating plainly:

- **A semantic rule is not an authorization check.** Whether a caller *may* perform an action is
  decided by authorization, not by a model's opinion about intent. Keep the check.
- **A prompt-injection rule raises the cost of an attack; it does not close the attack.** Treat it as
  one layer among least-privilege tools, output handling, and a human in the loop for anything
  irreversible.
- **A denied verdict is not proof of an attack, and an allowed verdict is not proof of safety.** Both
  are inputs to a decision your application still owns.
- **Thresholds are a product decision.** The evaluation tooling exists so they are chosen from
  measured precision and recall on your own data, not from a default that looked reasonable.

## Content handling

The library does not log prompts, tool arguments, tool results or any customer content by default.
Debug content logging is opt-in and must stay off in production unless the data classification of
what passes through the policy allows it.

A provider sends the content it evaluates to whatever endpoint it is configured with. Which provider
runs a rule is therefore a data-residency decision as much as a cost decision, and the library will
not make it for you.
