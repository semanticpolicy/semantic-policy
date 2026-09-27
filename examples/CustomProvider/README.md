# CustomProvider

A decision provider of your own: `TeiClassifierProvider` implements `IDecisionProvider` over a
[Text Embeddings Inference](https://github.com/huggingface/text-embeddings-inference) (TEI) server
that serves a prompt-injection classifier, and `Program.cs` registers it and evaluates a policy with
it. The example references `SemanticPolicy.Core` and nothing else, and its tests in
`CustomProvider.Tests` run on a fake transport of their own, so the pair is a pattern to copy.

The classifier is [`protectai/deberta-v3-base-prompt-injection-v2`](https://huggingface.co/protectai/deberta-v3-base-prompt-injection-v2),
Apache-2.0, with the labels `SAFE` and `INJECTION`.

The classifier answers one question, whether a text carries a prompt injection, whatever the rule
asks, so bind it only to a rule that asks that question. Its answer is a signal for a policy to
threshold, not a security boundary ([`SECURITY.md`](../../SECURITY.md)).

## Start the server

TEI's CPU image, on loopback only, with the model pinned to the revision the run below used and its
cache in a Docker volume, so the weights are downloaded once:

```bash
docker run -d --name tei -p 127.0.0.1:8080:80 -v tei-data:/data \
  ghcr.io/huggingface/text-embeddings-inference:cpu-1.9.4 \
  --model-id protectai/deberta-v3-base-prompt-injection-v2 \
  --revision 90c9989b1a342275dd0d1a95aad283c04e075671
```

`docker logs -f tei` ends with `Ready` once the server listens. Before sending traffic, warm it with
one call of your own, so a cold start does not land on the first real request as a `Timeout`:

```bash
curl -s http://127.0.0.1:8080/predict -H 'Content-Type: application/json' \
  -d '{"inputs": "A short synthetic sentence to warm the server.", "truncate": false}'
```

Stop it with `docker rm -f tei`; `docker volume rm tei-data` also removes the weights.

## Run it

```bash
dotnet run --project examples/CustomProvider/CustomProvider
dotnet test examples/CustomProvider/CustomProvider.Tests
```

The program asks the policy about two synthetic texts, release notes for a made-up product with and
without a planted instruction, and prints each verdict:

```
classifier: protectai/deberta-v3-base-prompt-injection-v2   server: TEI at http://127.0.0.1:8080/

input: release notes
policy: prompt-injection  mode: Enforce  evaluated: Allow  effective: Allow
  rule injection: Allow  evidence: Score 0.0001  source: Threshold
    tei: Success in 149 ms

input: release notes with a planted instruction
policy: prompt-injection  mode: Enforce  evaluated: Deny  effective: Deny
  rule injection: Deny  evidence: Score 1.0000  source: Threshold
    tei: Success in 83 ms
```

With no server listening, the call fails, and the verdict is the one the policy declares for a
failure, `OnFailure(FailureBehavior.Deny)`. That is "the check did not happen", not "the classifier
said yes":

```
input: release notes
policy: prompt-injection  mode: Enforce  evaluated: Deny  effective: Deny
  rule injection: Deny  evidence: none  source: FailureBehavior
    tei: Failure Unavailable (HttpRequestException: ConnectionError) in 2079 ms
```

The thresholds, 0.60 for `Warn` and 0.90 for `Deny` on the classifier's score, are illustrative.
Measure them on labelled examples of your own before you enforce anything.

## What the run showed

One run on 27 September 2026, with the image above on a Windows machine through Docker Desktop:

- TEI 1.9.4 served the classifier, at revision `90c9989b1a342275dd0d1a95aad283c04e075671` of its
  repository: on first start it downloaded the repository's `onnx/model.onnx` (about 0.7 GB, 73 s)
  and was ready 7 s after the download finished. `GET /info` reported a classifier with `SAFE` and
  `INJECTION` and a maximum input of 512 tokens.
- The two texts above scored 0.0001 and 1.0000 for `INJECTION`, as TEI answered them
  (`0.0000871343` and `0.99999934`), and the calls took 149 ms and 83 ms.
- An input of 702 tokens with `truncate: false` answered 422 with `error_type` `Validation`, which
  the provider reads as `RejectedInput`.
- The refused connection above took two seconds, which is why the provider's timer is 3 s
  ([Custom providers](../../docs/custom-providers.md#a-timer-of-your-own-separate-from-the-callers-token)).

## How it maps TEI onto the library

The provider declares Boolean decisions only, `Score` evidence, `StructuredContext: false` and
`RawOutput: true`. It sends `POST {base}/predict` with the context rendered by
`SemanticContext.ToCanonicalText` as `inputs` and never sends the rule's question. It sends
`truncate: false` because TEI otherwise cuts an over-long input silently, and an instruction past the
cut would read as benign text; with it, TEI refuses the input and the policy's failure handling
decides.

| TEI's answer | Result |
|---|---|
| `200`, two labels, one of them `INJECTION` | Boolean, `true` when `INJECTION` scores at least as high as the other label; `Score` evidence with both labels' scores as `true` and `false` and the scale `softmax`; the parsed body in `Raw` |
| `200` with anything else | `Malformed` |
| `401`, `403` | `Unauthorized` |
| `400`, `404`, `413`, `422` | `RejectedInput` |
| `408`, `429`, any `5xx` | `Unavailable` |
| `424`, TEI's backend error, and any other status, a redirect included | `Unknown` |
| a body over 1 MiB, never read whole | `Malformed` on a `200`, the status's kind otherwise |
| no connection | `Unavailable` |
| any other exception from the client or a handler the host added | `Unknown`, naming only the type |
| nothing within the provider's timer | `Timeout` |
| the caller's token cancelled | an `OperationCanceledException` |

Every result names the model the registration configured, because TEI's answer names none. A
failure's message carries the status, TEI's `error_type` when it is one of TEI's own, and the body's
length, and never TEI's `error` text, which can quote the input.
