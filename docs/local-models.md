# Local decision models

What a probe measured on three System One servers you can run on your own machine: Von, Laya and
kev. [Local setup](../README.md#local-setup) starts Von and registers it; this page holds the
numbers behind its advice.

## Where they come from

Von, Laya and kev are third-party projects, a few days old when they were measured. Pin the version
you run and read its code before you send it anything; nothing here endorses one of them.

| Server | Source | Licence |
|---|---|---|
| Von | [`von-sdk`](https://pypi.org/project/von-sdk/) on PyPI, code at [wfzyx/von](https://github.com/wfzyx/von) | Apache-2.0 |
| Laya | [`laya`](https://pypi.org/project/laya/) on PyPI, by Convai Innovations, model at [convaiinnovations/laya](https://huggingface.co/convaiinnovations/laya) | Apache-2.0 |
| kev | [jaredpalmer/kev](https://github.com/jaredpalmer/kev) on GitHub, run from a clone at commit `557598f` | Apache-2.0 |

Von 1.2.2 and Laya 0.3.10 download their weights from the latest revision of a Hugging Face
repository, so pinning the package does not pin the weights. Von listens on every network interface
(`0.0.0.0`) unless it is started with `--host 127.0.0.1`, and Laya does unless `LAYA_HOST=127.0.0.1`
is set.

## Speed and ranking

Every number on this page comes from a probe on one machine, a Ryzen 5 1600 with a GTX 1660 (6 GB),
on loopback and with synthetic content, on the date given with it.

| Server | Version | Date | Device | Smoke AUC | Median call | First call |
|---|---|---|---|---|---|---|
| Von | 1.2.2 | 25 Sep 2026 | GPU | 0.81 | 63 ms | 6.4 s |
| Laya, `english` checkpoint | 0.3.10 | 23 Sep 2026 | GPU | 0.81 | 242 ms | 2.1 s |
| Laya, `typed-decisions` checkpoint | 0.3.10 | 23 Sep 2026 | GPU | 0.89 | 228 ms | 755 ms |
| kev, 0.8B model | 0.1.0, commit `557598f` | 23 Sep 2026 | CPU | 0.93 | 4.2 s | 16.7 s |

- **Smoke AUC** is the ROC-AUC of the boolean score over the smoke set that ships with the
  evaluation CLI: how well it ranks the 46 synthetic attacks above the 48 benign rows, with the 6
  `ambiguous` rows left out, as the CLI leaves them out. It is not an accuracy, and it says nothing
  about your data.
- **Median call** is over ten short calls to a warm server. **First call** is the first after the
  server answered its health check. Von's is with its weights already on disk; a first run downloads
  them and takes longer.
- **The model a verdict reports** is the one the server names: `von-1.2.0` from Von 1.2.2, whatever
  it is sent; `laya-rl-agent` from Laya 0.3.10 for either checkpoint, which it picks by `o.Model`
  (checked again on 25 September); kev repeats what it is sent.

## Long contexts

A server reads a bounded context and does not say when it stops. The probe put a synthetic
instruction at the end of a synthetic English text and made the text longer. The limit below is the
length at which the text with the instruction first scored within 0.05 of the same text without it.

| Server | Date | What it reads | Limit, in characters | `o.MaxContextLength` |
|---|---|---|---|---|
| Von 1.2.2 | 25 Sep 2026 | all of it, up to the 40,000 characters measured, diluted | 1,800 | 1,700 |
| Laya 0.3.10, `english` | 25 Sep 2026 | the head only | 1,712 | 1,600 |
| Laya 0.3.10, `typed-decisions` | 25 Sep 2026 | the head only | 3,773 | 3,600 |
| kev, 0.8B model | 23 Sep 2026 | all of it, diluted | not reached at 12,000, the one length tried | none measured |

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

The `o.MaxContextLength` column is a round number below the limit. The limits are characters of one
English text, and code, numbers or another language spend more tokens per character, so set a lower
limit for such content. What the option does, and the failure behaviour to pair it with, is under
[Any System One server](../README.md#any-system-one-server).

## Agreement with Jev

The probe also asked each server 30 questions and compared its answers with Jev's. On 23 September,
Laya's two checkpoints and kev agreed with Jev on 8 or 9 of 9 routing questions and on 8 to 10 of 21
guard questions; on 25 September, Von 1.2.2 agreed on 7 of 9 and 11 of 21. Agreeing with Jev is not
being right, and 30 questions prove little, but the gap between routing and guarding is the one to
plan around: [What a local model is for](../README.md#what-a-local-model-is-for) says how.
