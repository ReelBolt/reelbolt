# Retrieval eval results

Recorded with the harness in `retrieval-eval.md`, over `retrieval-eval.json` (158 queries when this was run; 153 scored, 5 pending-kb; 4 more pending-kb step queries (ThumbnailGenerate, Revise, DesignGraphic, DesignSystem) were added afterwards and change none of the numbers below, since a pending-kb query is excluded from every metric because
their `assistant-kb/**` documents do not exist yet; the knowledge base has not landed, so these numbers are the
**pre-knowledge-base** baseline and must be re-run when lane 3b-2 lands).

## Conditions (read before quoting any number)

- Date 2026-10-25. Machine: a 32-logical-core workstation, CPU only, shared with other lanes while it ran (latency is noisy).
- **Subset corpus.** 799 of the 2686 chunks (52 documents): every document that is gold for a query plus seeded random other
  documents (seed 13, whole documents) up to 800 chunks. Embedding the full corpus on CPU was not feasible inside the time
  box. A subset has fewer distractors than production, so absolute numbers are optimistic; the comparison between
  configurations uses the same subset. Re-run on the full corpus for the final figures.
- Embeddings `Qwen/Qwen3-Embedding-0.6B` (TEI cpu-1.8), Qdrant v1.19.1, the real `QdrantPlatformDocsIndex` code
  (hybrid RRF k=60, prefetch 30, fuse 20, stage A, stage B through `OpenAICompatibleRerankClient`). No paid provider call.
- Latency is the first search per query (limit 10, uncached query embedding), plus the rerank call for A+B. An earlier run
  measured the second (embedding-cached) call and understated latency; it was fixed and everything below is from the fixed run.
- Injection floor 0.35: every query injected something in every configuration ("injection any" = 1.000), so the floor does not
  filter anything with this embedding model; it is not protecting against off-topic injection. Worth a look in 3b-2.

## Results (reranker `BAAI/bge-reranker-v2-m3`, 10 candidates, stage B timeout raised to 30 s)

| Slice | Configuration | n | recall@5 | MRR@10 | nDCG@10 | injection hit | p50 ms | p95 ms |
|---|---|---|---|---|---|---|---|---|
| all | baseline | 153 | 0.739 | 0.601 | 0.607 | 0.725 | 276 | 1079 |
| all | hybrid-a | 153 | 0.824 | 0.633 | 0.639 | 0.758 | 313 | 770 |
| all | hybrid-a+b | 153 | 0.843 | 0.688 | 0.684 | 0.797 | 6961 | 9949 |
| english | baseline | 110 | 0.736 | 0.596 | 0.611 | 0.727 | 269 | 925 |
| english | hybrid-a | 110 | 0.855 | 0.666 | 0.678 | 0.800 | 312 | 777 |
| english | hybrid-a+b | 110 | 0.873 | 0.745 | 0.739 | 0.836 | 6901 | 9847 |
| non-english | baseline | 43 | 0.744 | 0.614 | 0.596 | 0.721 | 306 | 1280 |
| non-english | hybrid-a | 43 | 0.744 | 0.548 | 0.542 | 0.651 | 313 | 392 |
| non-english | hybrid-a+b | 43 | 0.767 | 0.544 | 0.543 | 0.698 | 7159 | 10617 |
| step-question | baseline | 24 | 0.917 | 0.722 | 0.769 | 0.917 | 250 | 308 |
| step-question | hybrid-a | 24 | 0.958 | 0.706 | 0.737 | 0.875 | 290 | 781 |
| step-question | hybrid-a+b | 24 | 1.000 | 0.931 | 0.899 | 1.000 | 6326 | 7698 |
| goal-question | baseline | 50 | 0.480 | 0.391 | 0.370 | 0.460 | 265 | 312 |
| goal-question | hybrid-a | 50 | 0.580 | 0.435 | 0.413 | 0.520 | 321 | 482 |
| goal-question | hybrid-a+b | 50 | 0.620 | 0.385 | 0.388 | 0.520 | 7671 | 13844 |

## Gate verdicts

- **Hybrid recall@5 up at least 10 points: FAIL.** 0.739 to 0.824 on the whole set is +8.5 points (English +11.9, non-English 0.0).
- **Hybrid non-English not worse on all four of recall@5, MRR@10, nDCG@10 and injection hit: FAIL.** Recall@5 is equal
  (0.744 to 0.744) but MRR@10 falls 0.614 to 0.548, nDCG@10 0.596 to 0.542 and injection hit 0.721 to 0.651. Hybrid hurts
  non-English ranking: the sparse side adds no lexical signal for non-English queries against an English corpus and stage A
  reorders the dense order for the worse. The earlier "PASS" on recall@5 alone was too generous.
- Hybrid p95 at most 2x baseline (a latency gate, measured on the first search per query, not the rerank): PASS (770 ms vs 1079 ms; both noisy on a shared box, single-digit-ms cost over a ~300 ms
  query embedding).
- Stage B nDCG@10 not below stage A, whole set: PASS (0.639 to 0.684). Non-English: +0.001 (0.542 to 0.543). That is inside the run-to-run noise reported below (stage A alone moved by about 0.005 between rebuilds), so it is no evidence of harm, not a strict PASS.
- The hybrid recall gate is not met on this subset. Per spec 7.5 hybrid ships only when it is; the lead decides whether the
  full-corpus rerun (more distractors, where lexical match usually helps more) or the knowledge base, which adds the
  `goals` phrases the sparse side matches best, changes that. Goal questions (recall@5 0.480 to 0.580) are the weak slice
  everywhere: with no knowledge-base recipe pages the corpus has no document that states the goal in the user's words.

## Stage B model search (what was tried)

| Reranker | Candidates | Whole nDCG@10 A to A+B | Non-English nDCG@10 A to A+B | Verdict | Latency p50 |
|---|---|---|---|---|---|
| `BAAI/bge-reranker-base` (the compose default) | 20 | 0.634 to 0.641 | 0.522 to 0.431 | FAIL non-English | about 1.8 s |
| `BAAI/bge-reranker-v2-m3` | 20 | not measured | not measured | NOT MEASURED, the 45 minute budget ran out (about 8 s per call on this CPU) | n/a |
| `BAAI/bge-reranker-v2-m3` | 10 | 0.639 to 0.684 | 0.542 to 0.543 | PASS | about 7 s |

`bge-reranker-base` is English and Chinese only, which explains the non-English drop; the multilingual `v2-m3` fixes it.
Caveats: (1) the base-model row is from the first run, whose latency column was invalid (see conditions) but whose quality
columns are unaffected; the stage-A figure differs slightly between runs (0.634, 0.637, 0.639) because ties between equal
RRF scores are not ordered deterministically by Qdrant across rebuilds; (2) at about 7 s per call on CPU, `v2-m3` exceeds the
product's default 3 s `PlatformDocs:Rerank:TimeoutSeconds`, so with defaults and a CPU reranker stage B would time out and fall
back to stage A. A GPU, a smaller multilingual reranker or a longer timeout is needed before enabling it; measured cost per
turn with the self-hosted reranker is zero credits (price 0), only latency.

## Not measured

Full-corpus numbers (2686 chunks), `v2-m3` with 20 candidates, and anything involving the assistant knowledge base.
Raw per-query output was written to a scratch directory and not committed; rerun with the command in `retrieval-eval.md`
(the embedding cache directory, `REELBOLT_EVAL_EMBED_CACHE`, makes reruns cheap).

## Caveats and notes

- Gold sections are named by `##` heading text, and several documents repeat the same heading (for example "Related pages"
  or "Pitfalls ..."); the key is `docPath#section` so repeats across documents do not collide, but a heading repeated inside
  one document would conflate its sections. The validator reports no such gold today.
- The hybrid p95 gate is a latency gate on the retrieval call; it says nothing about the rerank cost, which is reported in the
  A+B row and is far above 2x baseline on CPU.
- The metric unit tests pin the formulas on small hand-made rankings (including a multi-gold nDCG); they do not validate gold
  quality, which was chosen by reading the corpus.
- Supply chain: the container tags (`qdrant/qdrant:v1.19.1`, TEI `cpu-1.8`) are tags, not digests, and the Hugging Face models
  (`Qwen/Qwen3-Embedding-0.6B`, `BAAI/bge-reranker-v2-m3`) are pulled at their latest revision with no pinned commit, so a
  rerun months later may differ. Pin digests and model revisions if exact reproducibility matters.
