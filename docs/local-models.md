# Local decision models

What a probe measured on four System One servers you can run on your own machine: Von, Laya, kev
and Ollaya, which serves several models behind one endpoint. [Local setup](../README.md#local-setup)
starts Von and registers it; this page holds the numbers behind its advice.

## Where they come from

Von, Laya, kev and Ollaya are third-party projects, days or weeks old when they were measured. Pin
the version you run and read its code before you send it anything; nothing here endorses one of
them.

| Server | Source | Licence |
|---|---|---|
| Von | [`von-sdk`](https://pypi.org/project/von-sdk/) on PyPI, code at [wfzyx/von](https://github.com/wfzyx/von) | Apache-2.0 |
| Laya | [`laya`](https://pypi.org/project/laya/) on PyPI, by Convai Innovations, model at [convaiinnovations/laya](https://huggingface.co/convaiinnovations/laya) | Apache-2.0 |
| kev | [jaredpalmer/kev](https://github.com/jaredpalmer/kev) on GitHub, run from a clone at commit `557598f` | Apache-2.0 |
| Ollaya | [ollaya-dev/ollaya](https://github.com/ollaya-dev/ollaya) on GitHub, release v0.11.0. Models come from its own registry, which points at their weights on Hugging Face: `decider:0.8b` at [Mapika/decider-0.8b](https://huggingface.co/Mapika/decider-0.8b), `laya:multilingual` at [convaiinnovations/laya](https://huggingface.co/convaiinnovations/laya) | Apache-2.0, and so is each model measured here |

Von 1.2.2 and Laya 0.3.10 download their weights from the latest revision of a Hugging Face
repository, so pinning the package does not pin the weights. Von listens on every network interface
(`0.0.0.0`) unless it is started with `--host 127.0.0.1`, and Laya does unless `LAYA_HOST=127.0.0.1`
is set. Ollaya 0.11.0 keeps a manifest for each model it pulls, naming every file by its SHA-256
digest and the weights by a fixed Hugging Face revision, and listens on `127.0.0.1:11435` unless
`OLLAYA_HOST` says otherwise.

## Speed and ranking

Every number on this page comes from a probe on one machine, a Ryzen 5 1600 with a GTX 1660 (6 GB),
on loopback and with synthetic content, on the date given with it.

| Server | Version | Date | Device | Smoke AUC | Median call | First call |
|---|---|---|---|---|---|---|
| Von | 1.2.2 | 25 Sep 2026 | GPU | 0.81 | 63 ms | 6.4 s |
| Laya, `english` checkpoint | 0.3.10 | 23 Sep 2026 | GPU | 0.81 | 242 ms | 2.1 s |
| Laya, `typed-decisions` checkpoint | 0.3.10 | 23 Sep 2026 | GPU | 0.89 | 228 ms | 755 ms |
| kev, 0.8B model | 0.1.0, commit `557598f` | 23 Sep 2026 | CPU | 0.93 | 4.2 s | 16.7 s |
| Ollaya, `decider:0.8b` | 0.11.0 | 7 Oct 2026 | CPU | 0.91 | 1.6 s | 2.8 min |
| Ollaya, `laya:multilingual` | 0.11.0 | 7 Oct 2026 | CPU | 0.84 | 221 ms | 26 s |

- **Smoke AUC** is the ROC-AUC of the boolean score over the smoke set that ships with the
  evaluation CLI: how well it ranks the 46 synthetic attacks above the 48 benign rows, with the 6
  `ambiguous` rows left out, as the CLI leaves them out. It is not an accuracy, and it says nothing
  about your data.
- **Median call** is over ten short calls to a warm server. **First call** is the first after the
  server answered its health check. Von's is with its weights already on disk; a first run downloads
  them and takes longer. Ollaya loads a model on its first call to it: `decider:0.8b` took 2.8
  minutes on the first load after the server started, and 13 seconds when loaded again a few minutes
  later.
- **The model a verdict reports** is the one the server names: `von-1.2.0` from Von 1.2.2, whatever
  it is sent; `laya-rl-agent` from Laya 0.3.10 for either checkpoint, which it picks by `o.Model`
  (checked again on 25 September); kev repeats what it is sent. Ollaya picks the model by `o.Model`
  and names it in the answer, and answers a model it has not pulled with 404 `MODEL_NOT_FOUND`,
  which the provider reports as a rejected input.
- **Ollaya's `decima:small` and `decima:base`** are not in the table: they answered exactly 0.5 to
  every row of the smoke set, whose criteria run to 22 and 27 words. On another rule, on 6 October,
  cutting one criterion below 20 words was enough for `decima:small` to answer something else. A
  model that gives every row the same number ranks nothing, so check that its answers vary before
  you read a threshold off them.

## Long contexts

A server reads a bounded context, and most do not say when they stop. The probe put a synthetic
instruction at the end of a synthetic English text and made the text longer. The limit below is the
length at which the text with the instruction first scored within 0.05 of the same text without it.

| Server | Date | What it reads | Limit, in characters | `o.MaxContextLength` |
|---|---|---|---|---|
| Von 1.2.2 | 25 Sep 2026 | all of it, up to the 40,000 characters measured, diluted | 1,800 | 1,700 |
| Laya 0.3.10, `english` | 25 Sep 2026 | the head only | 1,712 | 1,600 |
| Laya 0.3.10, `typed-decisions` | 25 Sep 2026 | the head only | 3,773 | 3,600 |
| kev, 0.8B model | 23 Sep 2026 | all of it, diluted | not reached at 12,000, the one length tried | none measured |
| Ollaya 0.11.0, `laya:multilingual` | 7 Oct 2026 | up to its context, and refuses a longer one | 3,460, where it starts refusing | none needed |
| Ollaya 0.11.0, `decider:0.8b` | 7 Oct 2026 | all of it, diluted | not reached at 12,000, the one length tried | none measured |

- **Von 1.2.2** cuts nothing, but a longer text dilutes the instruction, and unevenly. Alone, the
  instruction scored 0.88. At the end of a text of 400 to 1,700 characters it still scored 0.18 to
  0.39 above the text without it; from 1,800 to 3,400 characters, anywhere from level with it to
  0.19 above. At the start of the text it held out until 6,000 to 8,000 characters.
- **Laya** reads a fixed number of tokens and drops the rest without saying so. The question and its
  criteria share those tokens with the context, so a longer question leaves less room for the
  context. Past the limit, the text with the instruction and the text without it score exactly
  alike.
- **kev** reads the whole context, diluted: an instruction that scored 0.77 alone scored 0.27 at the
  end of a 12,000-character text, against 0.13 without it.
- **Ollaya** refuses a context it would have to cut: past the model's context, `/v1/systemone`
  answers 422 `STATE_TRUNCATED`, and the provider reports a rejected input, the same failure
  `o.MaxContextLength` gives, so the option would only save the call. Ollaya's documentation ties
  the refusal to the model's context, not to a number of characters, so another language or code
  should meet it sooner rather than slip past it. Under `laya:multilingual` the text without the
  instruction already scored 0.41 to 0.99, so the 0.05 test says nothing here, and the table gives
  the length at which the refusals start.
- **`decider:0.8b`** reads the whole context, diluted: an instruction that scored 0.99 alone scored
  0.16 at the end of a 12,000-character text, against 0.002 without it, and 0.76 at its start.

The `o.MaxContextLength` column is a round number below the limit. The limits are characters of one
English text, and code, numbers or another language spend more tokens per character, so set a lower
limit for such content. What the option does, and the failure behaviour to pair it with, is under
[Any System One server](../README.md#any-system-one-server).

## Agreement with Jev

The probe also asked each server 30 questions and compared its answers with Jev's. On 23 September,
Laya's two checkpoints and kev agreed with Jev on 8 or 9 of 9 routing questions and on 8 to 10 of 21
guard questions; on 25 September, Von 1.2.2 agreed on 7 of 9 and 11 of 21; on 7 October, through
Ollaya, `decider:0.8b` agreed on 8 of 9 and 9 of 21, and `laya:multilingual` on 6 of 9 and 7 of 21.
Agreeing with Jev is not being right, and 30 questions prove little, but the gap between routing
and guarding is the one to plan around:
[What a local model is for](../README.md#what-a-local-model-is-for) says how.
