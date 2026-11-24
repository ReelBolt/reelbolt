# Assistant eval results

Harness for the 50-goal set ([`eval-goals.md`](eval-goals.md), spec section 8): `inference/tests/ReelBolt.WorkflowEngine.Tests/AssistantEval/`. It drives the real `AssistantAgent` (system prompt, the registered tools, the per-turn session block and documentation block) against an in-memory stub project built from each goal's `project` block, records tool calls, replies and token counts, scores the table in `eval-goals.md` (3 samples, 2-of-3 majority) and writes `results.json`, `results.md` and `samples.jsonl`. The offline tests (`AssistantEvalHarnessTests`, scripted model, no provider) run in the normal test pass; the real run is env-gated.

## Status

Baseline of the current assistant (master with run brief, scriptwriter fix, manifest, hybrid retrieval, rerank, pre-run linter): **NOT MEASURED (no model key).** Every provider key in the repository `.env` is empty, so no model could be called. No numbers below are estimates.

| Item | Value |
|---|---|
| Provider / model | NOT MEASURED (no model key); planned: DeepSeek, `deepseek-flash` |
| Samples run | NOT MEASURED |
| Tokens in / out | NOT MEASURED |
| Estimated cost | NOT MEASURED (list price in `ModelPriceCatalog`: USD 0.30 / 1.20 per million tokens, peak hours) |
| Route accuracy (target >= 85 %) | NOT MEASURED |
| Template accuracy (>= 80 %) | NOT MEASURED |
| Brief language / format (>= 95 %) | NOT MEASURED |
| Lint recall (>= 90 %) | NOT MEASURED (the real `WorkflowLinter` is run on the first accepted proposal over the stub project's facts) |
| Unneeded questions (<= 2 goals) | NOT MEASURED |
| Paid disclosure (100 %) | NOT MEASURED |
| Zero tolerance (0) | NOT MEASURED |

## Run it once (needs a key)

Export the key into the shell without echoing it, then from `inference/`:

```
REELBOLT_ASSISTANT_EVAL=1 REELBOLT_EVAL_PROVIDER=DeepSeek REELBOLT_EVAL_MODEL=deepseek-flash \
REELBOLT_EVAL_STOP_USD=13.5 REELBOLT_EVAL_STOP_TOKENS=25000000 REELBOLT_EVAL_LABEL=baseline \
dotnet test tests/ReelBolt.WorkflowEngine.Tests --no-restore --filter "FullyQualifiedName~Run_the_baseline_against_a_real_model"
```

Settings (all environment): `REELBOLT_EVAL_PROVIDER` (an `InferenceProviderKind`), `REELBOLT_EVAL_MODEL`, `REELBOLT_EVAL_ENDPOINT`, `REELBOLT_EVAL_API_KEY_ENV` (name of the variable holding the key), `REELBOLT_EVAL_SAMPLES` (3, at most 5), `REELBOLT_EVAL_PARALLEL` (at most 4), `REELBOLT_EVAL_GOAL_IDS`, `REELBOLT_EVAL_PRICE_IN_PER_MTOK` / `_OUT_` (price override), `REELBOLT_EVAL_OUT` (must be under the test output or the system temp directory), `REELBOLT_EVAL_LABEL` (`[a-z0-9-]`).

**Spend guard (fails closed).** The run refuses to start when the model has no price in `ModelPriceCatalog` and neither a price override nor `REELBOLT_EVAL_STOP_TOKENS` is set. Every model request first reserves its worst case (estimated input plus a 4096-token output cap, which is also sent to the provider) and is refused when that would cross `REELBOLT_EVAL_STOP_TOKENS` (default 25 000 000) or, when a price is known, `REELBOLT_EVAL_STOP_USD` (default 13.5). A failed or timed-out request keeps its reserved estimate. The run then stops and the report says so. The key is read from the environment only and never written to a report; provider error text is reduced to the exception type and HTTP status in `samples.jsonl`.

**Modes.** `REELBOLT_EVAL_LABEL=baseline` is the assistant as merged today. After lane 3a lands, run the same command with `REELBOLT_EVAL_LABEL=after`; results go to `<out>/<label>/` so the two runs sit side by side.

## Scoring notes and limits of the stub (read before comparing to production)

- A question counts as a clarifying question only when the reply proposes nothing; approval prompts and offers ("Shall I apply it?", "Want me to ...?") are dropped before counting. A reply with no accepted proposal and no clarifying question is a decline.
- A proposal counts only when the tool accepted it (its answer carries a confirmation token). The route and template are inferred from the last accepted proposal by (agent type, step type) per step, then by how many steps carry the template's own configuration; ties are all accepted when the fixture lists any of them. A changed setting makes it `template_with_patch`.
- Hallucinated template key: a key passed to a template tool that is not in the catalog, or a code-font key in the reply that looks like a catalog key and is not one. Plain prose such as "green-screen" is not flagged.
- An errored or timed-out sample earns nothing on every metric (never a decline pass); the per-goal error count is reported.
- An invented brief field is one miss in the goal-level brief rate. Formats are four buckets (Landscape 16:9, Vertical 9:16, Portrait 4:5, Square 1:1); the fixture's `project-voice` needs a narrated brief with a project voice id.
- Lint recall: the real `WorkflowLinter` runs on the FIRST accepted proposal (steps and the observed brief) with facts built from the goal's `project` block (providers by capability, plan entitlements, files by role, design status, the consented voice), plus any `WLnnn` ids the assistant saw in tool results or its reply.
- Documentation retrieval is BM25 over `docs/`, not the production dense plus BM25 plus rerank.
- Provider capabilities are stubbed at the linter's facts only; the assistant's own tools do not see provider rows (no non-admin tool lists them), and auto-provisioned template workflows are not seeded.
- `Apply*` and `ExecuteWorkflow` run against the in-memory database with no approval gate and no real execution.
- The reply sanitizer is not applied, so the "id shown" check scores the raw model reply (stricter than production). The paid-disclosure check is a wording heuristic; clarification questions are captured for hand review, not judged.

## Q14 assumptions (recorded in each goal's `assumptions`)

- G29: verified, the Trial plan does not allow video generation (`plans.catalog.json`).
- G32: verified, photos are cut as still clips and no slideshow template exists (`video-editing.md`, "Still images").
- G26: unverifiable from code, whether the voice supports Japanese is vendor-dependent.
- G13, G44: not verifiable from code in this ticket; confirm on the first measured run.

## Recipe-first assistant (T-009): not run, blocked on the eval-spend decision

**Status: not run. Blocked on the eval-spend decision: eval not run.**

A product decision approves no paid eval spend and no model key exists in this environment, so the 50-goal eval
(docs/design/workflow-apps.md section 8) was not run against the recipe-first assistant. No before/after numbers exist.

| Gate (decision Q17) | Target | Measured |
|---|---|---|
| Route accuracy | >= 85 | not measured |
| Template accuracy | >= 80 | not measured |
| Language and format | >= 95 | not measured |
| Lint recall | >= 90 | not measured |
| Unneeded questions | <= 5% of goals | not measured |
| Zero-tolerance rows | 0 | not measured |

What is done: the prompt section, the tools, the closed patch catalog and the consistency tests. `Assistant:RecipeFirst`
stays **off** by default; turning it on is gated on a measured run that meets every row above. Spend: none.
Known harness finding to fix after the eval harness PR merges: QA-WAB-AEVAL-11 (a template answer that fills declared
inputs scores as a patch).
