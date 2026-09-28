# 0018. The evaluation tool reads its providers from a file of the adapters' own options

**Status:** Accepted
**Date:** 2026-09-28

## Context

This record narrows §6 of [0013](0013-evaluation-records-answers-and-replays-them-through-core.md):
the evaluation tool gets a file that names the providers `run` calls. The rest of 0013 and all of
[0014](0014-evaluation-tool-records-live-once-and-ci-replays.md) stay in force.

0013 §6 gave the tool no provider configuration of its own: its entry point adds providers to the
builder as an application does, and an adapter's settings and secrets come from wherever the adapter
documents them. 0014 §4 made `Providers.Register` that registration, and it registers two built-ins:
`local`, one System One server at the address an environment variable gives, and `jev`, TypeSafe's
Jev on the OpenRouter route. Since [0016](0016-evaluation-tool-ships-as-a-dotnet-tool.md) the tool
is an installed command, and an installed command has no entry point a user can edit. It reaches
those two registrations and nothing else — not two System One servers side by side, not Jev on the
vendor's own route — and any other provider needs a clone of this repository.

0013 also rejected a `providers.json` mapped to adapters, as a second configuration format per
adapter. The question was whether a file can name the providers without becoming that format.

## Decision

**`run --providers <file>` reads the providers from a file whose entries are the adapters' own
options classes.**

- The file maps a registration name to an adapter kind and that adapter's options:
  `{ "providers": { "<name>": { "kind": "<kind>", "options": { … } } } }`. `options` is deserialized
  into the adapter's own public options class — `SystemOneOptions` for `systemone`,
  `TypeSafeJevOptions` for `typesafe-jev` — with the settings `SemanticPolicyJson` uses, so its
  properties are the ones the adapter documents. A Jev route is written out as the route's own
  properties; the file has no names of its own for presets.
- With the file, only its providers exist. Without it, the built-ins apply as 0014 §4 describes
  them. The file never adds to the built-ins and never overrides one.
- A key is only ever the name of an environment variable, an `apiKeyVariable` in the options or,
  for Jev, in the route. The file sets no key, no timeout and no identity: the registration name is
  the provider's identity in a recording, and `run --timeout` stays the only per-call limit, as
  0014 §4 decided. The tool sets each adapter's timer past any run's limit and its id to its
  registration name, as it does for the built-ins.
- An entry that sets `apiKey`, `timeout` or `id`, or a property the file's shape or the options
  class does not know, is refused before any provider is built. The message names the file, the
  provider and the property, and never quotes a setting's value. The settings inside `options` are
  still the adapter's to check when it is built: a failure there is exit 1 with the adapter's own
  message, as 0014 §4 decided.
- Only `run` reads the file. `report`, `sweep` and `compare` call no provider.
- Kinds are compiled into the tool. A kind is an adapter the tool references, never a package it
  finds and loads at run time.

This is not the second configuration format 0013 rejected. An entry holds the adapter's own public
options, so there is nothing per adapter to learn beyond what the adapter already documents, and
nothing for the tool to translate. The rest of 0013 §6 holds: providers reach the tool through their
DI registration, a binding's provider id is a registration name, a policy that names an unregistered
provider fails before the first call, and latency and usage are read without knowing the adapter.
Loading adapters by reflection stays rejected, and 0013's consequence stands: a provider package the
tool does not reference is out of its reach.

## Consequences

- Any System One server and both Jev routes can be named in one file, several at once. A policy that
  binds them is recorded in one run and compared on the same rows.
- The file alone shows where a dataset's content is sent, because nothing outside it is registered.
- An adapter's public options become a file schema. An option renamed or removed breaks every file
  that sets it: the file is refused on the unknown property until it is updated, so a change to an
  options class is a breaking change for the tool's users as well as the adapter's.
- A new kind is a change to the tool and a release, as a new built-in was. A custom
  `IDecisionProvider` still needs an entry point of its own that registers it; the installed command
  cannot reach it.
- No key can travel in the file. Every key, a self-hosted server's included, is set in the
  environment of the process that runs the tool, and a file can be shared without leaking one.
- A policy that binds a built-in name, run with a file that does not declare it, fails before the
  first call, as any unregistered provider does.

## Alternatives considered

- **Configure the built-ins from environment variables alone.** It would leave 0013 as written.
  Lost because it reaches one System One server at a time, so two servers cannot be compared in one
  run.
- **A library package holding the tool's entry point, beside a thin tool.** Any `IDecisionProvider`
  would run from a program of a few lines. Lost because it costs two projects and a public API to
  document and keep.
- **Let the file add to the built-ins, or override one by name.** More convenient. Lost because
  where a dataset's content goes would depend on both the file and the defaults.
- **Jev presets by name in the file.** Lost because they are a vocabulary of the tool's own, in a
  file whose schema is otherwise the adapter's.
- **Pass a timeout or an id through to the adapter.** Lost because a timeout in the file is a second
  clock beside `run --timeout`, and an id other than the registration name disagrees with the name
  the recording keys every attempt by.
- **Write this record once the file ships.** Quicker. Lost because the tool's code would contradict
  an accepted record until then.
