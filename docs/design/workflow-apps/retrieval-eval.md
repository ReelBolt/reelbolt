# Retrieval eval (docs/design/workflow-apps.md 7.5)

Offline, opt-in, free: it never calls a paid provider and never runs in the default test pass or per PR.

## Files

| File | What |
|---|---|
| `retrieval-eval.json` | the gold set: queries with `gold` (`docPath#section`, the `##` heading text) |
| `check-retrieval-eval.mjs` | validator: shape, unique ids, gold files and sections exist, pending-kb handling |
| `retrieval-eval.compose.yml` | scratch services: Qdrant v1.19.1, TEI embeddings (`Qwen/Qwen3-Embedding-0.6B`), TEI reranker (`BAAI/bge-reranker-v2-m3`, multilingual; `bge-reranker-base` fails the non-English gate) |
| `retrieval-eval-results.md` | the recorded baseline and results |
| `inference/tests/ReelBolt.WorkflowEngine.Tests/PlatformDocs/RetrievalEval/` | the harness (xUnit, skipped unless `REELBOLT_RETRIEVAL_EVAL=1`) plus pure unit tests of the metrics |

## The gold set

Query fields: `id`, `locale` (`en`, `it`, `es`, `de`, `fr`, `pt`), `type` (`goal`, `step`, `template`, `recipe`, `howto`,
`paraphrase`), `query`, `gold`, optional `pendingGold` and `source` (the eval-goals id). `gold` lists acceptable sections
(any of them is a correct answer for recall and MRR; every listed section is relevant for nDCG). Gold was chosen by reading
the corpus, never by running the retriever.

`pendingGold` names sections of the assistant knowledge base (`assistant-kb/**`, lane 3b-2) that do not exist yet. A query
with empty `gold` is **pending-kb**: it is left out of every metric. When the document lands, its `pendingGold` entries start
counting automatically (the harness checks the corpus it indexes), so the set is re-runnable with no edit. After the KB
lands, fix any `pendingGold` heading that differs from the real one (`check-retrieval-eval.mjs` warns, `--strict` fails) and
move confirmed entries into `gold`.

## Running it

```bash
docker compose -p retrieval-eval -f docs/design/workflow-apps/retrieval-eval.compose.yml up -d   # one stack, then wait for /health
cd inference
REELBOLT_RETRIEVAL_EVAL=1 REELBOLT_RETRIEVAL_EVAL_OUT=/tmp/eval-out \
  dotnet test tests/ReelBolt.WorkflowEngine.Tests --filter "FullyQualifiedName~RetrievalEvalRun"
docker compose -p retrieval-eval -f ../docs/design/workflow-apps/retrieval-eval.compose.yml down -v
```

Output: `eval-results.json` (every query's ranked sections and latency) and `eval-report.md` in the output directory.
Safety: the eval sends the whole corpus and every query to the embeddings and reranker URLs, so the harness refuses any
non-loopback `REELBOLT_EVAL_*` URL unless `REELBOLT_EVAL_ALLOW_REMOTE=1`. A search that answers anything but Ok (index not
built, service down) fails the run instead of counting as a miss. Image tags are not digest-pinned and model revisions are
not pinned; see the caveats in `retrieval-eval-results.md`.

Environment knobs: `REELBOLT_EVAL_QDRANT_URL` / `_QDRANT_GRPC_PORT`, `REELBOLT_EVAL_EMBEDDINGS_URL` / `_MODEL`,
`REELBOLT_EVAL_RERANKER_URL` / `_MODEL`, `REELBOLT_EVAL_RERANK_CANDIDATES` (default 20), `REELBOLT_RETRIEVAL_EVAL_REPO`.

Plan the resources: the full corpus is about 2700 chunks and the CPU embedding service needs hours for it; the harness therefore indexes a subset by default (`REELBOLT_EVAL_MAX_CHUNKS`, default 800: every gold document plus seeded random others, `REELBOLT_EVAL_SEED`), caches batch embeddings on disk (`REELBOLT_EVAL_EMBED_CACHE`) and stops after `REELBOLT_EVAL_BUDGET_MINUTES` (default 45), reporting unfinished configurations as NOT MEASURED. Raise the cap for a full run. Run the compiled test directly (`dotnet test --no-build`) rather than inside any shared build slot, since it waits on the services.

## What is measured

Three configurations over the same index code the product runs: `baseline` (`Hybrid:Enabled=false`), `hybrid-a` (hybrid
dense + sparse RRF, deterministic stage A) and `hybrid-a+b` (stage A then the TEI rerank). Results are collapsed to
sections (a split section counts once). Per slice (all, english, non-english, step-question, goal-question): recall@5
(a gold section in the top 5), MRR@10, nDCG@10, injection hit rate (a gold section among the injected top 4 above the 0.35
floor), injection any rate (anything injected) and latency p50/p95 of the first search per query (limit 10, uncached query embedding, plus the rerank call for A+B). Gate verdicts follow 7.5.
