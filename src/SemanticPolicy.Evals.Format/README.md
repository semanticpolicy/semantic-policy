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
  `Dataset.ForRule` checks every label against a rule's answers, options or levels.
- **Both writers.** `DatasetWriter` writes a dataset one row at a time and returns the SHA-256 of
  the file, which a recording's header names the dataset by. `RecordingWriter` writes a recording's
  header and then its rows, each flushed as it is written.

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
