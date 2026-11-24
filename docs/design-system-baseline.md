# Design-system baseline measurements (Phase 1, P1-05)

Status: **DONE** — the isolated baseline ran on 2026-10-16 on this machine.
Source of record: the local `reelbolt-pds` stack only; **never** staging or production.

## Caveats: what this baseline does not establish

Read the numbers below with these limits. The baseline measures the cost of the existing analyzer steps; it does not show that the design-system templates save anything.

- **3 of the 8 runs were cancelled** at the 40-minute poll cap (R1-tailwind, R1-mantine and the ablation run A1-tailwind), and a fourth (R2-tailwind) failed on a sandbox refusal. Only 4 runs passed.
- **Billing was off** (`BILLING_ENABLED=false`), so `credits_charged_millis` is 0 for every step and there is no real credit data; credits are derived from `usage_events`.
- **Cache hit rate was 0%** in every run, so no R2-vs-R1 reuse was measured (see the cache section).
- **One provider only**: every agent resolved to a single DeepSeek `Chat` provider row.
- **No `-ds` template run was ever executed.** The saving from the `-ds` templates is therefore **not established**: steps 1-5 are 3.2% of tokens and 7.3% of duration in this baseline, which is the most a template could skip, and the rest of the run was not compared.
- **Key provenance is not recorded in this repository.** Which key the run used and where it came from is not written down here, and the run itself cannot be re-verified from the repository. No key material belongs in this file.

## What was measured

Steps 1–5 of the unchanged `quick-win-promo` template (the five analyzers) plus
whole-run totals, and what one hand-written palette/font style summary does
downstream. Eight runs: R1 ×3 (first run per fixture), R2 ×3 (identical repeat,
for cache hits), R3 ×1 (changed brief), A1 ×1 (base brief + style summary). All
agents resolved to one DeepSeek `Chat` provider row
(`deepseek-v4-pro`, `https://api.deepseek.com/`).

## Safety

- No host other than `localhost` and the configured provider endpoint was contacted.
- **8 of the maximum 12** workflow runs used.
- Measured spend **$18.0750**, under the $20 abort threshold (hard cap $25).
- No key value was printed, logged or committed.

## Runs

| Run | Fixture | Status | Wall (s) | input tok | output tok | tokens_used | steps 1–5 tok | 1–5 share | spend (USD) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| R1-tailwind | tailwind-app | Cancelled (40 min cap) | 2402 | 6,845,410 | 129,827 | 7,180,338 | 114,902 | 1.6% | 2.894399 |
| R1-mantine | mantine-app | Cancelled (40 min cap) | 2400 | 3,918,914 | 104,691 | 4,257,446 | 121,802 | 2.9% | 2.719661 |
| R1-plain | plain-css-app | Passed | 2161 | 3,340,593 | 119,978 | 3,848,475 | 129,132 | 3.4% | 2.138855 |
| R2-tailwind | tailwind-app | Failed | 1756 | 2,374,963 | 74,513 | 2,725,030 | 152,197 | 5.6% | 1.898872 |
| R2-mantine | mantine-app | Passed | 1648 | 4,437,319 | 119,559 | 5,020,085 | 163,866 | 3.3% | 1.648800 |
| R2-plain | plain-css-app | Passed | 1529 | 4,477,882 | 106,154 | 4,972,293 | 156,876 | 3.2% | 1.564989 |
| R3-tailwind | tailwind-app | Passed | 1723 | 4,631,338 | 145,646 | 5,207,232 | 181,528 | 3.5% | 1.910437 |
| A1-tailwind | tailwind-app | Cancelled (40 min cap) | 2404 | 3,419,464 | 106,803 | 3,768,115 | 156,353 | 4.2% | 3.083599 |

- **R2-tailwind Failed** on a real sandbox refusal, not the cap: HTTP 409
  "sandbox containers have no network egress, so packages cannot be installed at
  runtime" from the Author step (the generated composition wanted a runtime
  package install).
- R1-tailwind and A1-tailwind hit the 40-minute poll cap and were stopped.
- `credits_charged_millis` is **0** for every step because `BILLING_ENABLED=false`;
  the credit-equivalent is taken from `usage_events.credits_millis` (below).

## Whole-run totals (8 runs)

| Metric | Value |
|---|---:|
| `input_tokens` | 33,445,883 |
| `output_tokens` | 907,171 |
| `tokens_used` | 36,979,014 |
| duration | 12,243,613 ms (~3 h 24 m summed) |
| `credits_charged_millis` | 0 (billing off) |
| `usage_events.credits_millis` | 2,976,631 (≈2,976.6 credits) |
| execution `cost_usd` | $17.8596 |
| other `usage_events` (file summarization) | $0.2154 |
| **total measured spend** | **$18.0750** |

## Steps 1–5: per-analyzer aggregate (8 runs each)

| Step | Agent | avg in | avg out | avg tokens | avg ms | total in | total out |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | CodeStructureAnalyzer | 25,505 | 1,044 | 26,549 | 13,555 | 204,039 | 8,350 |
| 2 | DependencyAnalyzer | 22,507 | 1,025 | 23,532 | 14,180 | 180,052 | 8,203 |
| 3 | ComponentInventoryAnalyzer | 26,549 | 2,520 | 29,069 | 25,187 | 212,393 | 20,158 |
| 4 | RouteAndApiAnalyzer | 32,233 | 3,197 | 35,430 | 37,486 | 257,860 | 25,576 |
| 5 | StyleAndThemeExtractor | 30,726 | 1,777 | 32,503 | 20,947 | 245,810 | 14,215 |
| | **steps 1–5 total** | | | | | **1,100,154** | **76,502** |

## Steps 1–5 share of the whole run

| Measure | steps 1–5 | whole run | share |
|---|---:|---:|---:|
| `input_tokens` | 1,100,154 | 33,445,883 | 3.3% |
| `output_tokens` | 76,502 | 907,171 | 8.4% |
| `tokens_used` | 1,176,656 | 36,979,014 | 3.2% |
| duration | 890,836 ms | 12,243,613 ms | 7.3% |

## Cache hit rate: R2 vs R1

**0.0%** — every one of the 8 runs had 0 cached step results
(`workflow_step_results.from_cache = false`), and all 40
`workflow_step_cache_entries` rows ended with `hit_count = 0`:

| Run | cached steps / step rows | hit rate |
|---|---:|---:|
| R1 (all three) | 0 / 12, 0 / 13, 0 / 13 | 0.0% |
| R2 (all three) | 0 / 12, 0 / 13, 0 / 13 | 0.0% |
| R3 / A1 | 0 / 13, 0 / 12 | 0.0% |

**Why:** the cache is enabled and entries were written, but the key changed
between R1 and R2. `ProjectFileFingerprintProvider` hashes each file's
`Id|StorageKey|SizeBytes|UploadedAt|SummaryStatus|IndexingStatus`. The runner
started R1 immediately after upload, while the background file summarizer still
had the files `Pending`; by R2 the summaries were `Completed`, so the fingerprint
differed and every key missed. A meaningful R2-vs-R1 hit rate needs the
summarizer to settle **before** R1 (and the same run order otherwise).

## Ablation A1 vs R1 (tailwind-app)

| Metric | R1-tailwind | A1-tailwind (+ style summary) |
|---|---:|---:|
| Translator (step 6) input tokens | 755,759 | 1,401,395 |
| Translator output tokens | 31,881 | 33,106 |
| Author (step 10) input tokens | 1,998,613 | 987,285 |
| Author output tokens | 37,817 | 43,902 |
| Review score, iteration 1 | 6 | 6 |
| Run spend (USD) | 2.894399 | 3.083599 |

Reading: the style summary roughly doubled the Translator's input (more context
to read) and halved the Author's input (explicit palette/font guidance replaced
rediscovery), while the review score did not move (6 in both). One sample only.

## Caveats

- Numbers are from a local single-host stack with `BILLING_ENABLED=false` and
  one `deepseek-v4-pro` provider; they are not production numbers.
- `input_tokens`/`output_tokens` are NULL by design for the review steps;
  `tokens_used` is the headline token metric.
- Build-time DNS on this host needed `build.network: host` (see
  an operator tool); runtime networking was unchanged.
- Measurement queries: an operator tool (read-only), raw output
  in `H/evidence/p1-teardown.txt` and `/tmp/baseline-numbers.txt`.

## D-8 gate

This is the baseline evidence for the D-8 gate (lifted on 2026-10-16,
see `SIGNOFF.md` D.1). It shows that the analyzer steps are a small part of a run
(steps 1–5 are about 3.2% of tokens and 7.3% of duration) and that the
translator/author steps dominate. It does **not** show a saving from the
design-system templates; see the caveats at the top of this page. The cache
finding is an operational fix (let summaries settle), not a design-system result.
