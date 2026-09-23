# Contributing

Thanks for looking. The project is pre-alpha and the API is still moving, so the most useful
contributions right now are use cases, evaluation datasets, and reports of where the abstraction does
not fit what you are building.

## Before a large change

Open an issue first and describe the problem rather than the patch. A pull request that changes the
shape of `Core`, adds a provider, or moves a decision across the Core/provider boundary is a design
question, and design questions are cheaper to settle before the code exists. Small fixes need no
preamble.

## Working on it

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
```

All three must pass before a pull request is ready. CI runs them on pull requests into `main`.

## House rules

- **Core knows nothing about any provider.** If a change needs a provider type in `Core`, the design
  is wrong somewhere — say so in the issue instead of adding the reference.
- **A provider decides; it does not enforce.** Verdict thresholds, modes and what happens on a `Deny`
  belong to the policy, not to the thing that answered the question.
- **Public API carries XML documentation.** The build generates the documentation file and warnings
  are errors, so this is enforced rather than requested.
- **Tests cover behaviour, not implementation.** A provider change proves itself against the contract
  test suite every provider shares.
- **Nothing logs content by default.** Prompts, tool arguments, tool results and anything a user
  typed stay out of logs, error messages and exception text unless debug content logging is
  explicitly enabled.

## Commits and pull requests

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/): `feat`, `fix`,
`refactor`, `perf`, `docs`, `test`, `build`, `ci`, `chore`, with an optional scope. The type must
match the change — a `feat:` that only moves code is a lie that outlives the pull request.

A pull request says what changed and why, names the commands it was verified with, and targets
`main`. Keep it to one concern; two concerns are two pull requests.

## Code of conduct

Everyone taking part in the project follows the [Code of Conduct](CODE_OF_CONDUCT.md). Report
behaviour that breaks it to admin@semanticpolicy.dev.

## Licence

By contributing you agree that your contribution is licensed under Apache-2.0, the licence in
`LICENSE`.
