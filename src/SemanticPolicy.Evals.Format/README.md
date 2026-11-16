# SemanticPolicy.Evals.Format

The two file formats of the [SemanticPolicy](https://github.com/semanticpolicy/semantic-policy)
evaluation CLI, `semantic-policy`, as a library: the labelled dataset it measures a policy on, and
the recording of provider answers it replays. An application that writes either file with this
package writes one the CLI reads, because the CLI reads and writes them with this package too.

```sh
dotnet add package SemanticPolicy.Evals.Format --prerelease
```

## What it holds

- **The records.** `DatasetRow`, with its `RowLabel`, and `Dataset` for a dataset; `RecordingHeader`,
  `RecordedDataset`, `RecordedProvider`, `RecordedResumption`, `RecordedRow` and `Recording` for a
  recording.
- **Both readers.** `DatasetReader.Read` and `RecordingReader.Read` validate as the CLI does, and
  `Dataset.ForPolicy` checks every label against a rule's answers, options or levels, and every
  row's own options against the policy.
- **Both writers.** `DatasetWriter` writes a dataset one row at a time and returns the SHA-256 of
  the file, which a recording's header names the dataset by. `RecordingWriter` writes a recording's
  header and then its rows, each flushed as it is written.
- **The conversion.** `RecordedRow.FromVerdict` turns a `PolicyVerdict` an application stored into
  a recorded row, and `RecordingHeader.ForExport` makes the header of a recording exported that way.

Metrics, replay, threshold sweeps and calibration stay in the CLI; this package holds the files, not
what is computed from them.

## Writing a dataset and reading it back

```csharp
using System.Text.Json;
using SemanticPolicy;
using SemanticPolicy.Evals.Format;

string sha256;
await using (DatasetWriter writer = await DatasetWriter.CreateAsync("rows.jsonl", cancellationToken))
{
    await writer.WriteAsync(
        new DatasetRow("r-001", SemanticContext.FromText("placeholder: a request"), new RowLabel(RowLabelKind.Answer, "true")),
        cancellationToken);

    using JsonDocument context = JsonDocument.Parse("""{"channel": "chat", "turn": 3}""");
    await writer.WriteAsync(
        new DatasetRow(
            "r-002",
            new SemanticContext([
                ContextPart.Text("message", "placeholder: a request about a document"),
                ContextPart.Json("context", context.RootElement)]),
            new RowLabel(RowLabelKind.Ambiguous, null))
        {
            Metadata = new Dictionary<string, JsonElement> { ["source"] = JsonSerializer.SerializeToElement("synthetic") },
        },
        cancellationToken);

    sha256 = await writer.CompleteAsync(cancellationToken);
}

Dataset dataset = DatasetReader.Read("rows.jsonl");
// dataset.Sha256 == sha256, and dataset.Rows holds both rows, each with the Line it was read from.
```

The file is UTF-8 without a byte-order mark, with one `\n` per row, so its bytes, and therefore its
digest, are the same on every machine:

```jsonl
{"id":"r-001","input":"placeholder: a request","label":true}
{"id":"r-002","input":{"message":"placeholder: a request about a document","context":{"channel":"chat","turn":3}},"label":"ambiguous","metadata":{"source":"synthetic"}}
```

A row's `Metadata` is empty and its `Line` is 0 unless it was read from a file; the writer ignores
`Line`. The writer refuses, rather than writes, a row that would read back as a different row or not
at all: an id that is empty, white space or already written; an answer label that is empty,
`ambiguous` or `abstain`; an ambiguous or abstain label that carries an answer; and a JSON input part
whose value is a JSON string, which the reader reads back as text, so write it as a text part. A
refused row writes nothing, and the writer takes the next one.

## Options per row

A row of a Choice rule can carry the options it was shown, when they change from one request to the
next. `DatasetRow.Options` maps a rule id to its options, as `ChoiceOption` values, and the file
holds them under `options` in the policy file's own option shape:

```csharp
DatasetRow row = new("r-003", SemanticContext.FromText("placeholder: asks to change a plan"), new RowLabel(RowLabelKind.Answer, "plans"))
{
    Options = new Dictionary<string, IReadOnlyList<ChoiceOption>>
    {
        ["route"] =
        [
            new ChoiceOption("plans", "Changes to a subscription plan.", Verdict.Allow),
            new ChoiceOption("person", "A person should take over.", Verdict.Escalate),
        ],
    },
};
```

```jsonl
{"id":"r-003","input":"placeholder: asks to change a plan","label":"plans","options":{"route":[{"key":"plans","description":"Changes to a subscription plan.","verdict":"allow"},{"key":"person","description":"A person should take over.","verdict":"escalate"}]}}
```

A row without options for a rule uses the policy's. `Dataset.ForPolicy(policy, rule)` checks each
row's options as Core checks a Choice rule's, refuses options for a rule the policy lacks or that is
not a Choice rule, and checks the row's label against the row's own keys. The writer refuses an
options list, option, key or description that is null, and a verdict that is not a `Verdict` name.
`ambiguous` and `abstain` stay reserved label words: a row whose right answer is an option keyed so
cannot carry it as its label.

## Exporting stored verdicts

An application that keeps the `PolicyVerdict` of each decision can hand those verdicts to the CLI as
a recording, and measure, sweep or calibrate the policy on what production saw. Write the dataset
first, because the header names it by the digest its writer returns, then the recording:

```csharp
using SemanticPolicy;
using SemanticPolicy.Evals.Format;
using SemanticPolicy.Evaluation;

PolicyVerdict verdict = await evaluator.EvaluateAsync(policy, context, cancellationToken); // stored with the decision

string sha256;
await using (DatasetWriter dataset = await DatasetWriter.CreateAsync("decisions.jsonl", cancellationToken))
{
    await dataset.WriteAsync(new DatasetRow("d-0001", context, new RowLabel(RowLabelKind.Answer, "true")), cancellationToken);
    sha256 = await dataset.CompleteAsync(cancellationToken);
}

RecordingHeader header = RecordingHeader.ForExport(
    policy,
    [new RecordedDataset("decisions.jsonl", sha256, Split: null)],
    [.. policy.Bindings.Select(binding => new RecordedProvider(binding.ProviderId, Model: null))],
    sourceName: "my-exporter",
    sourceVersion: "1.4.0",
    recordedAt: DateTimeOffset.UtcNow);
await using (RecordingWriter recording = await RecordingWriter.CreateAsync("decisions.recording.jsonl", header, cancellationToken))
{
    await recording.WriteAsync(RecordedRow.FromVerdict("d-0001", verdict), cancellationToken);
}
```

`FromVerdict` keys each rule's attempts by `Attempt.ProviderId`, the binding's provider, to the
provider's `Result` as it was returned, and carries the verdict's `PolicyDigest`. It keeps only the
attempts the evaluator made and leaves out a rule with none, so a verdict that the first binding
decided gives a row with that binding's answer alone. A result's raw response never serializes. With
the policy above binding `local` and then `jev`, and `local` deciding, the file holds:

```jsonl
{"recordedAt":"2026-11-16T09:30:00+00:00","format":"semanticpolicy/evals-recording/v0","policy":{...},"datasets":[{"path":"decisions.jsonl","sha256":"<64 hex digits>"}],"providers":[{"name":"local"},{"name":"jev"}],"toolVersion":"<this package's version>","policyDigest":"sha256:<64 hex digits>","source":{"kind":"export","name":"my-exporter","version":"1.4.0"}}
{"id":"d-0001","attempts":{"prompt-injection":{"local":{"protocol":"semanticpolicy/v0","type":"boolean","outcome":{"status":"success"},"value":true,"evidence":[...],"provider":{...}}}},"policyDigest":"sha256:<64 hex digits>"}
```

The header carries `policyDigest`, the policy's `Digest`, and a `source` naming the exporting
program, and leaves out `parallel`, `timeout`, `where`, `retries` and `resumptions`, which describe a
run. Each row's `policyDigest` names the policy that decided it, so an export that spans several
versions of a policy says which one decided which row; the CLI counts them and compares them with
nothing. Because the export holds only the attempts production made, a replay that needs another
fails and names the rule, the provider and the row, and the CLI's `run --resume` refuses an export.
The CLI's README says what an export can be replayed with, in [Exporting production
decisions][export].

## Errors

Every error a reader or a writer reports is an `EvalsFormatException`. Its message names the file,
the line or the row's number, the row's id, a rule id or a property, and never a row's input, a
metadata value or a JSON parser's message, so it can be shown or logged as it is.

## Format versions

Both formats are `v0`: a recording's header says `semanticpolicy/evals-recording/v0`, and every
dataset and recording written for the CLI so far reads with this package unchanged. A change to
either format is an addition or a new format version. The CLI's README describes both formats field
by field, in [The dataset][dataset] and [The recording][recording].

[dataset]: https://github.com/semanticpolicy/semantic-policy/blob/main/tools/SemanticPolicy.Evals/README.md#the-dataset
[recording]: https://github.com/semanticpolicy/semantic-policy/blob/main/tools/SemanticPolicy.Evals/README.md#the-recording
[export]: https://github.com/semanticpolicy/semantic-policy/blob/main/tools/SemanticPolicy.Evals/README.md#exporting-production-decisions
