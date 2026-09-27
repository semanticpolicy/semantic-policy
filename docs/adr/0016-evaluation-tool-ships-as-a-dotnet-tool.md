# 0016. The evaluation tool ships as a dotnet tool that carries its samples, and CI installs what it packs

**Status:** Accepted
**Date:** 2026-09-27

## Context

The evaluation tool ran only from a clone of this repository, as
`dotnet run --project tools/SemanticPolicy.Evals`. Nobody outside the repository clones it to
measure a rule, so the tool had no users outside it. Shipping it as a package raised four questions,
and each answer binds code outside the change that made it:

1. What is the package, and what does a user type?
2. What does someone who has just installed it measure first, with no key and no dataset of their
   own?
3. How does CI know that the package it would ship installs and runs?
4. What can a README hold when it is also the package's page on nuget.org?

## Decision

1. **The tool is the `SemanticPolicy.Evals` dotnet tool, and its command is `semantic-policy`.**
   - It is one framework-dependent package with `RollForward` `Major`, so it runs on .NET 10 or,
     where that is missing, on a later major version.
   - As the SDK packs a tool, it carries Core, SystemOne and TypeSafe as assemblies beside its own
     and declares no NuGet dependency.
   - It is versioned and released with the libraries: one version, one tag. While that version is a
     prerelease, the documented install names `--prerelease`.
   - The verbs keep their names, options and exit codes: `semantic-policy run`, `report`, `sweep`
     and `compare`, with `samples` beside them. A command outside evaluation, when one comes, stands
     beside the verbs too.

2. **The package carries the shipped datasets, and `semantic-policy samples <dir>` writes them
   out.**
   - Every `.json` and `.jsonl` file under the tool's `datasets/` directory travels in the package
     as a file beside the assembly: the example datasets and the smoke sets, with their policies and
     the smoke sets' recordings.
   - `samples` copies them byte for byte under `<dir>`, at their paths below `datasets/`. A
     dataset's digest is taken over its bytes
     ([0014](0014-evaluation-tool-records-live-once-and-ci-replays.md)), so a shipped recording
     replays wherever `samples` writes it.
   - It checks every target before it writes the first. When one exists, it exits with 1, names that
     file and writes nothing. There is no option to overwrite.

3. **CI installs the package it packed, and runs it.** On every pull request into `main`, after the
   tests, CI packs the tool from the Release build it has just tested and installs it into a
   temporary tool path with `--source` set to that pack's output, which replaces every configured
   feed. Outside the checkout, it runs `samples`, then `report` on a smoke recording. Nothing
   calls a provider or needs a key.

4. **The tool's README is its package page.** nuget.org shows it, so every link in it is an absolute
   URL or one of its own headings, and it shows the tool only as the installed command. The
   from-source form, `dotnet run --project tools/SemanticPolicy.Evals -- <command>`, is in
   `AGENTS.md`, for contributors.

## Consequences

- From an install to numbers is three commands, `dotnet tool install`, `samples` and `report`, with
  no clone, no key and no cost.
- The tool runs the Core and adapters it was packed with. A fix in either reaches its users only in
  the next release, and a provider package the tool does not reference stays out of its reach, as
  [0013](0013-evaluation-records-answers-and-replays-them-through-core.md) says.
- Every `.json` and `.jsonl` file added under the tool's `datasets/` ships to everyone who installs
  the tool, and a file in another format there does not ship at all.
- A shipped recording must replay after `samples` writes it. The replay test covers the committed
  recordings in the repository, and CI's install step covers one of them as the package carries it.
- Moving or renaming the smoke files breaks the CI step until the step follows them, and every CI
  run pays for a pack and an install.
- `samples` into a directory that holds an earlier release's files fails, and the user picks another
  directory.
- A link from the tool's README to another file in the repository points at `main`, not at the tag
  the package was built from, so it can describe a newer version than the one installed.

## Alternatives considered

- **Keep the clone and `dotnet run`.** Lost because it leaves no way to run the tool short of
  cloning the repository.
- **Self-contained binaries in the GitHub release.** Lost because every release would build and ship
  one binary per platform, and what that adds, a tool for teams outside .NET, did not outweigh it.
- **`semantic-policy eval <verb>`.** Lost because every invocation grows longer, to reserve a
  namespace nothing needs yet.
- **A `semantic-policy-evals` command.** Lost because it is the longest spelling of the product's
  only command.
- **Links to the dataset files on GitHub in place of `samples`.** Lost because the quick start grows
  longer and depends on paths on `main`.
- **An `init` command that writes a skeleton policy and dataset.** Lost because with no recording,
  the quick start shows no number without a key.
- **The datasets as embedded resources.** Lost because they would not appear in the package's file
  list.
- **`samples` skipping the files that exist and writing the rest.** Lost because a directory would
  mix two releases' files, and a recording need not match another release's dataset.
- **`samples` overwriting.** Lost because it would replace a file the user may have changed.
- **A test that opens the package and checks the command name and the datasets.** Lost because it
  is fast and offline but never runs the tool.
- **A manual install noted in the pull request.** Lost because nothing would catch a later
  regression.
- **`--add-source` beside the configured feeds in CI.** Lost because feeds are read in parallel and
  the fastest wins for the same package and version, so once a version is on nuget.org, CI could
  install the published package instead of the one it built.
- **Relative links in the tool's README.** Lost because the README is also the package page, and
  nuget.org documents that relative image paths do not render and says nothing of relative links.
- **`dotnet run` beside `semantic-policy` in the tool's README.** Lost because every command would
  have two spellings in a README most readers see on nuget.org, where the second cannot run.
