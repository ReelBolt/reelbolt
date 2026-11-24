# Platform documentation search (assistant RAG)

The platform assistant answers questions about what ReelBolt can do from **this documentation**, not
just from the step JSON Schemas. Before this, it only had the schemas and reasoned its way to confidently
wrong answers — for example that nothing renders Remotion, or that a tracked screen insert has no content
source — because a schema says what a field accepts, not what it is for or how steps combine.

## What gets indexed

`docs/user-guide/**` (audience `User`), the assistant knowledge base `docs/assistant-kb/**` (audience
`Developer`, INTERNAL BACKGROUND: understood by the assistant, never quoted to customers) and the top-level
developer docs `docs/*.md` (audience `Developer`). `archive/`, `design/`, `marketing/`, `screenshots/` and
`README.md` are excluded, so design specs are deliberately not retrievable.

Only the user guide is published as a website ([docs-site.md](docs-site.md)). The developer docs have **no
public page anywhere**: the assistant reads them and nothing else does, as internal background it can answer
from, not as documentation a customer can open.

The tree ships **inside the Inference API image** at `/app/platform-docs`
(`COPY --from=platform-docs`, a named build context pointing at `./docs`; see the Dockerfile). The
documentation site image gets a different, narrower context (`./docs/user-guide`), so this is the only image
that carries the developer docs. A plain `docker build` without that context ships an empty tree and search
reports itself unavailable instead of the build failing.

## Chunking

`PlatformDocsCorpus` splits each file at `##` headings (never inside a fenced code block), strips front
matter, and splits any section longer than `MaxChunkChars` at paragraph breaks. Each chunk carries the
page title, the section heading, its audience and, for a user-guide section, its docs-site URL
(`/docs/...#anchor`). A developer-audience section has **no URL** (it is empty), because it has no public
page; search results mark such a section `audience: "Internal"` and omit the URL, so there is nothing for
the assistant to link to. What is embedded is `"{title} — {section}\n\n{text}"`, so a short section
still lands near questions about its page. This is why the writing guideline is that every `##` section
must stand on its own.

### Chunk metadata (chunker version `"3"`)

Every chunk also carries retrieval metadata, stored as payload next to `docPath, audience, title, section,
url, content, embedHash`: `chunkId`, `kind` (`recipe`, `step-card`, `concept`, `how-to`, `reference`,
`user-guide`, `generated-fact`), `stepTypes`, `agentTypes`, `capabilities`, `templateKeys`, `goals`,
`costClass` (`free`, `credits`, `voice`, `generation`), `locale` (`en`), `headingPath`
(`# title > ## section`), `prevId` / `nextId` (neighbours inside one document) and `docVersion` (a content
hash of the file, diagnostics only).

A page may declare them in an optional `kb:` block of its front matter (scalars, `[a, b]` lists or `- item`
lists; unknown `kind`, `costClass` and enum names are dropped):

```yaml
---
title: Compile cards
kb:
  kind: step-card
  stepTypes: [VideoCompile]
  costClass: credits
  goals:
    - make a promo video
---
```

Declared lists apply to every section of that page and are **merged with** each section's own scan (never
replace it), so do not declare `stepTypes` or `templateKeys` at page level in a file with one `##` per step or
recipe: declare only what is true of every section and let the scan tag the rest. Trailing ` # comments` in the
block are ignored. Whatever is not declared is derived: the folder rule for `kind` (`user-guide/recipes/` = `recipe`, other `user-guide/` = `user-guide`,
`assistant-kb/` = `concept`, top-level docs = `reference`) and an exact, case-sensitive scan of the section
for `StepType`, `AgentType` and `InferenceProviderCapability` names and for workflow-template keys (whole
tokens only: `quick-win-promo` does not match inside `quick-win-promo-ds`). A compound name
(`VideoCompile`) matches anywhere; a single word that is also plain English (`Agent`, `Extract`, `Decision`,
`Chat`) matches only inside backticks or next to the word step / agent / capability / type. The metadata is part of the
corpus hash, so editing it re-indexes.

## Index

`QdrantPlatformDocsIndex` keeps one **global** collection per (embedding model, corpus version):

```
platformdocs_{model-slug}_{model-hash8}_{corpus-hash12}[d]
```

The trailing `d` marks the dense-only layout (`Hybrid:Enabled=false`). Because the layout is part of the name,
flipping the switch is an ordinary new-collection build: the serving collection keeps answering, its dense
vectors are reused, and it is dropped only once the new one is complete.

- The corpus hash covers every chunk, its metadata and a chunker version (now `3`), so any doc edit produces a
  new collection, and the move to chunker 3 rebuilt it once.
- The model is part of the name for the same reason project-file collections key on it
  ([embeddings.md](embeddings.md)): vectors from different models are not comparable.
- A new collection is built in full, then superseded `platformdocs_*` collections are dropped.
- "Already indexed" is a name lookup plus an exact point count; a partial collection from a crashed
  attempt is deleted and rebuilt.
- The `platformdocs_` prefix can never collide with a project collection (those start with a 32-hex
  project id).

With `PlatformDocs:Hybrid:Enabled` (default) the collection has two **named vectors**: `dense` (cosine, as
before) and `bm25` (sparse, `Modifier.Idf`, so the server applies IDF and the index sends term frequencies of
a Unicode-folded, lowercased token stream, no stemming; the page's declared `goals` are added to the sparse
text only). Keyword payload indexes exist on `kind`, `stepTypes`, `agentTypes`, `capabilities`,
`templateKeys`, `costClass` and `audience`. Naming, swap-after-complete and `embedHash` reuse of unchanged
chunks are unchanged (reuse reads the dense vector of an old unnamed or a new named collection). Switching
`Hybrid:Enabled` builds the other layout under its own name (see above); while a new collection builds,
searches keep using the previous one in whichever layout it has. A filtered `SearchPlatformDocs` call
answered while the serving collection predates chunker 3 (no metadata) says the index is being rebuilt
instead of returning zero hits. With it off the collection is the single unnamed cosine
vector and search is the one dense call, exactly as before.

The Qdrant server is pinned (`qdrant/qdrant:v1.19.1` in `docker-compose.yml`): hybrid needs the Query API
(sparse vectors, prefetch, RRF) which the 1.19 client uses, and retrieval behaviour must not change under an
unpinned `latest`. Bump the tag deliberately and run the opt-in container test below. The tag is not digest-pinned
(`@sha256:...`): a re-pushed tag would change the server under us; pin a digest if that risk matters.

`PlatformDocsIndexingService` (hosted) runs `EnsureIndexedAsync` 15 s after startup and then every
`RecheckIntervalSeconds` (default 600; at most 120 s after a failure). That makes adding or switching the
default **Embedding** provider at runtime take effect without a restart. With no Embedding provider
configured, nothing is indexed and both retrieval paths below degrade to "unavailable".

## Retrieval: injected and on demand

1. **Injected per turn.** `PlatformDocsContextBuilder` embeds the user's latest message, takes the top
   `InjectTopK` sections above `InjectMinScore` (user-guide sections get a small ranking boost), caps the
   block at `InjectMaxChars`, and appends it to the assistant's **system prompt** next to the session
   context block — never to the persisted thread, so it is recomputed every turn and never replayed
   stale. It is bounded by `InjectTimeoutSeconds` and degrades to nothing on any failure.
2. **On demand.** The `SearchPlatformDocs` tool lets the model search with its own query when the injected
   sections do not cover the question. The system prompt tells it never to declare something impossible
   without searching first. The tool is bounded by `SearchTimeoutSeconds` (see below).

### The hybrid pipeline

Both paths call the same search (`IPlatformDocsSearch`); with hybrid on it runs:

1. **Filters.** The explicit `SearchPlatformDocs` arguments `stepType`, `kind`, `capability` and `costClass`
   are exact `must` conditions on the payload indexes. For injection nothing is filtered: the exact enum names
   and template keys the user's message mentions (and any session hints) become ranking hints, so a hint can
   re-order but never hide a result.
2. **Dual prefetch + RRF.** Dense top 30 and sparse top 30 (same filter) fused by reciprocal-rank fusion
   (k = 60) to the top 20 in one `QueryAsync`-shaped request, returning only the payload fields used. A second
   dense-only request in the same batch returns the cosines the injection floor is judged on.
3. **Stage A rerank** (`PlatformDocsStageA`, deterministic, free, always on): score = normalised fused score
   + kind priority, only when the query has a build intent (words such as build, create, workflow, recipe,
   step; neutral otherwise): recipe 0.06, step-card 0.045, how-to 0.03, concept 0.015 + exact template match 0.10 /
   step match 0.08 / agent match 0.04 / capability match 0.03 with the hints + 0.05 for customer-facing
   audience; at most `MaxChunksPerDoc` (2) chunks per document; ties break on chunk id. For injection the
   0.35 floor is applied here: if no candidate has a dense cosine above it nothing is injected, and a candidate
   whose own cosine is known and below it is dropped (a candidate found only by the lexical side stays).
4. **Model rerank (stage B, optional)** (`IPlatformDocsRerankStage`). The top stage-A candidates
   (at most 20, 600 characters each) are re-ordered by a `Rerank`-capability model; see "Model rerank"
   below. Without a configured, priced Rerank provider, or on any failure, the order is stage A's, byte for
   byte. The final top `InjectTopK` (injection) or `limit` (tool) is taken from the stage-B order.
5. **Neighbour expansion.** When the top hit is one piece of a section split for length, the other pieces
   of the same section (`prevId` / `nextId`) are placed right behind it, and the tail is trimmed back to the
   requested limit, so the head of the list that injection keeps carries the whole section.

The query embedding is cached (LRU, 256, keyed on the calling organization, the embedding model and the
text, so one organization's cache hit never reveals another's query). `SearchPlatformDocs` truncates its query
to 2000 characters, like injection. Failure, timeouts and
the "unavailable" answers are as before. Search queries are written **in English** (the tool description says
so): the corpus is English and the sparse side is lexical; the dense side carries cross-lingual injection.

Retrieved text is documentation, not user instruction; the injected block says so explicitly, matching the
assistant's "tool output is data" rule.

## Configuration (`PlatformDocs:*`, Inference API)

| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | `true` | Master switch (indexing, tool, injection). Compose: `PLATFORM_DOCS_ENABLED`. |
| `Path` | `/app/platform-docs` | Root of the shipped Markdown tree. |
| `RecheckIntervalSeconds` | `600` | How often the indexer re-checks corpus/model. |
| `MaxChunkChars` | `2400` | Longest chunk before a section is split. |
| `InjectTopK` | `4` | Sections injected per turn (`0` disables injection only). |
| `InjectMaxChars` | `7000` | Character budget of the injected block. |
| `InjectTimeoutSeconds` | `5` | Hard bound on per-turn retrieval. |
| `InjectMinScore` | `0.35` | Cosine floor for injection (the tool ignores it). |
| `SearchTimeoutSeconds` | `15` | Hard bound on one `SearchPlatformDocs` tool call. |
| `Hybrid:Enabled` | `true` | Dense + sparse + RRF + stage A. `false` = today's dense-only collection and search. |
| `Hybrid:PrefetchLimit` | `30` | Candidates per prefetch (dense, sparse). |
| `Hybrid:FuseLimit` | `20` | Candidates kept after RRF, handed to the rerank stages. |
| `Hybrid:RrfK` | `60` | The RRF constant. |
| `Hybrid:MaxChunksPerDoc` | `2` | Stage A's per-document cap. |
| `Hybrid:QueryEmbeddingCacheSize` | `256` | Query-embedding LRU capacity. |
| `Rerank:Enabled` | `true` | Stage B switch. With no `Rerank` provider it does nothing. |
| `Rerank:Candidates` | `20` | Top stage-A candidates scored (hard cap 20). |
| `Rerank:TimeoutSeconds` | `3` | Hard bound on the rerank call; on expiry stage A's order is used. |

`InjectMinScore` stays a cosine floor in both modes. The opt-in container test
(`PlatformDocsQdrantIntegrationTests`) runs against a real server when `REELBOLT_QDRANT_TEST_HOST` is set
(for example `docker run -p 16334:6334 qdrant/qdrant:v1.19.1` and `REELBOLT_QDRANT_TEST_PORT=16334`).

## Model rerank (stage B)

`PlatformDocsRerankStage` takes the top stage-A candidates and the user's query and asks the default
`Rerank`-capability provider (`IInferenceProviderResolver.ResolveRerankAsync`: explicit row, then the
default row, else none) to score them. The provider is an `OpenAICompatible` row whose endpoint serves
`POST {endpoint}/rerank` with `{query, texts[], top_n}`; both the Hugging Face text-embeddings-inference
answer (`[{index, score}]`) and the hosted-reranker answer (`{results:[{index, relevance_score}]}`) are
accepted. The model name on the row is sent as `model` (a hosted reranker needs it; TEI ignores it; the Workers AI dialect below puts it in the URL instead).

**Dialects, chosen by exact host only** (never by tenant input): `api.cloudflare.com` is Cloudflare Workers AI;
`api.voyageai.com` sends `documents` + `top_k`; `api.cohere.com`, `api.cohere.ai` and `api.jina.ai` send
`documents` + `top_n`; every other host (including `*.workers.dev` and look-alikes such as
`api.cloudflare.com.evil.test`) is the TEI shape above.

**Cloudflare Workers AI (staging).** Register an `OpenAICompatible` `Rerank` row with endpoint
`https://api.cloudflare.com/client/v4/accounts/<account>/ai/v1` (the same row shape as the embedding row) and model
`@cf/baai/bge-reranker-base`. The client derives `POST .../accounts/<account>/ai/run/@cf/baai/bge-reranker-base`
from it (trailing `/ai/v1` replaced by `/ai/run/<model>`; the model is in the URL, not the body) and sends
`{"query": ..., "contexts": [{"text": ...}], "top_k": n}` with `Authorization: Bearer <token>`. An endpoint path
that is not exactly `/client/v4/accounts/<account>/ai/v1`, a missing model, or a model not matching
`@cf/<vendor>/<name>` is a fail-soft no-call. An empty candidate is sent as a single space (`contexts[].text` must
be non-empty) so indexes never shift. The answer is `{"result":{"response":[{"id","score"}]},"success":true}` (or the
bare `{"response":[...]}`); `id` is the context index. Schema:
https://developers.cloudflare.com/workers-ai/models/bge-reranker-base/. What leaves the host: the capped query and
at most 20 candidate chunks of at most 600 characters, to Cloudflare. Price: $0.00002 per search (upper bound
derived from $0.00311 per M input tokens, https://developers.cloudflare.com/workers-ai/platform/pricing/).

- **Bounds, per turn.** One call per assistant turn across the injection lookup and every `SearchPlatformDocs`
  call (the first search of the turn claims the slot, keyed on the turn's usage scope; later searches keep stage A), at most 20 candidates of at most 600 characters each, a query of at
  most 500 characters, 3 seconds. The client enforces the caps itself, whatever the caller passes.
- **Fail-soft.** A timeout, a transport or HTTP error, a malformed answer, fewer scores than candidates,
  a repeated or out-of-range index, no provider, a disabled stage, and an unpriced platform model all
  return stage A's order unchanged. Retrieval never fails because the reranker did.
- **Priced or refused.** A platform rerank row without a price is "rerank unavailable"
  (`ModelUnpricedException` is read as no provider), and `GET /api/v1/pricing/coverage` lists it with
  the missing unit `RerankSearches`. No hosted-vendor price is seeded: an admin enters the vendor's
  published price. For a self-hosted TEI reranker, apply the suggested `$0` (price book entry
  `BAAI/bge-reranker-base`, a real zero because the operator runs the container).
- **Platform rows only.** The corpus includes internal developer-audience documents, so the stage uses the
  rerank provider only when it is operator-configured (platform-managed). An organization's own `Rerank`
  row is never sent documentation: the stage skips it (stage A order, no call, no usage row). The admin
  boundary still accepts such a row at the kind level (the allowlist is per kind, not per owner); the stage
  is the enforcement.
- **Request dialect.** Self-hosted and unknown hosts get the TEI body (`texts`, `top_n`). Known hosted hosts
  get `documents` (Cohere `api.cohere.com`/`api.cohere.ai`, Jina `api.jina.ai`) or `documents` with `top_k`
  (Voyage `api.voyageai.com`). The dialect is derived from the host, never from tenant input. Responses
  are accepted as a bare array, `{results:[...]}` or `{data:[...]}`. A hosted reranker stays unavailable
  until an admin enters its published price.
- **Metering and charging.** One `usage_events` row per call whose response body arrived (`RerankSearches`,
  quantity 1, capability `Rerank`), including a malformed or short body (the vendor may have billed it); a
  timeout, transport error or non-success status writes no row. The source is the ambient usage scope's:
  `Assistant` in the assistant chat turn, `Mcp` (one row per call) for an MCP client searching the docs,
  recorded inside that scope. Its
  credits join what the turn metered and are settled by `NonWorkflowChargeService` inside the turn's
  existing hold and allowance: no extra line for the user, and with billing off only the usage row is
  written. See [`metering-and-billing.md`](metering-and-billing.md).
- **Admin.** Add a provider of capability `Rerank` (kind `OpenAICompatible` only). The provider test is
  configured-only (no ping, because a hosted ping would be a billed search that ships text to the vendor).
  `GET /api/v1/inference-providers/selectable?capability=Rerank` and `.../status?capability=Rerank` work
  like every other capability.
- **Optional self-hosted reranker.** Run a second text-embeddings-inference container beside `embeddings`
  with a reranker model (for example `BAAI/bge-reranker-base`), register it as a `Rerank` row with
  endpoint `http://reranker:80`. `docker-compose.yml` carries it as a commented, opt-in service with a
  pinned image tag; it is not auto-wired and nothing downloads a model during CI.

### Wiring

`PlatformDocsRerankStage` is a singleton registered as `IPlatformDocsRerankStage`; the singleton
`QdrantPlatformDocsIndex` takes it as its optional stage-B seam, and both the injection builder and the
tool reach it through `IPlatformDocsSearch` (one shared `RankAsync`: stage A, stage B, final top K). Its
usage row lands in the turn's ambient usage scope, so it settles inside the turn's hold. A private-network
address on an organisation-owned row is refused by the client (public addresses only), which is just
another fail-soft case.

## The search tool never holds a turn

`SearchPlatformDocs` embeds the model's query on the same embedding server that re-embeds the corpus
after a deploy and every uploaded project file. With no bound, a busy (often CPU-only) server left the
assistant on "Searching the documentation…" for minutes — reproduced twice with 60 s+ waits, and once
with no answer at all. Three things now keep the turn moving:

- **A bound, with the injection path's semantics.** One tool call gets `SearchTimeoutSeconds` (default
  15, a little more than `InjectTimeoutSeconds` because the model asked explicitly). On expiry — and
  whenever the index is not built yet or is unreachable — the tool returns
  `{ "available": false, "status": "timeout" | "still_indexing" | "unavailable", "reason", "guidance" }`,
  where `guidance` tells the model to answer from what it knows, say it could not check the
  documentation this time, and not retry in the same turn. Search itself never waits on the indexer's
  lock: it reads whichever collection was last confirmed complete.
- **A truthful label.** Every tool reports a caption when it starts (`data-toolStatus`) and another
  when it returns: "Thinking" normally, or "Docs index still building — answering without it" when the
  search came back unavailable. Before this the label kept naming the search for as long as the model
  then took.
- **Interactive priority.** Between embedding batches the docs indexer waits (at most 15 s per batch)
  while an assistant turn is in flight, as does per-file indexing (at most 10 s per chunk) and file
  summarization on the chat provider (at most `FileSummarization:InteractiveYieldSeconds`). The count
  of turns in flight is `AssistantActivityMonitor`, a process-wide singleton. See
  [platform-assistant-mcp.md](platform-assistant-mcp.md#background-work-never-blocks-an-upload-or-the-assistant).

## What leaves the host

The user's message (truncated to 2000 characters) is embedded through the default Embedding provider on
every assistant turn, and the docs chunks are embedded once per corpus version. With the local
`embeddings` container nothing leaves the host; with a hosted embedding provider, the user's message
does.

With a `Rerank` provider the user's query (truncated to 500 characters) and the text of the top
candidate chunks (each truncated to 600 characters) are also sent to that provider. With the
self-hosted reranker nothing leaves the host; with a hosted reranker they do. The corpus includes
internal background documents, so a deployment that cares should use the self-hosted option. See
[`provider-data-handling.md`](provider-data-handling.md).

## Re-indexing after a documentation change

A changed document produces a new corpus hash and therefore a new collection, but two things keep
the assistant useful while it builds:

- **Searches keep answering from the previous collection** of the same Embedding model until the
  new one is complete — slightly stale, never blind. Before this, every deploy that touched a doc
  left the assistant answering "the documentation is still being indexed" for as long as a full
  re-embed took on the CPU embedding server (several minutes for ~700 chunks), and it fell back to
  reading whatever it could find in the project's own files.
- **Unchanged chunks are not re-embedded.** Every point stores `embedHash`, a hash of exactly the
  text that was embedded; the new collection reuses the previous collection's vector for every
  chunk whose hash is unchanged, so a docs edit embeds only the edited sections. Collections
  written before `embedHash` existed are re-embedded in full once.


## Generated facts

At index time `PlatformDocsGeneratedFacts` adds one `generated-fact` chunk per step type and one per recipe, produced from `StepCatalog` (see `docs/platform-assistant-mcp.md`, "Step catalog and generated facts"). They are developer-audience (internal background, no URL), carry `stepTypes`, `templateKeys` and `costClass` (a class, never a price), and enter the corpus hash, so a code change to a card re-indexes the collection.

## Retrieval eval

Relevance is measured offline, never per PR and never against a paid provider. A gold set of about 150 queries
(`docs/design/workflow-apps/retrieval-eval.json`; gold is `docPath#section`) is scored by an opt-in xUnit harness
(`REELBOLT_RETRIEVAL_EVAL=1`, test class `RetrievalEvalRun`) against a local Qdrant, the local `embeddings` service and a
local TEI reranker, for three configurations: dense-only baseline (`PlatformDocs:Hybrid:Enabled=false`), hybrid stage A and
hybrid stage A plus B. It reports recall@5, MRR@10, nDCG@10, injection hit rate at the 0.35 floor and latency p50/p95 per
slice, and the ship gates (hybrid: recall@5 +10 points, non-English not worse, p95 at most 2x baseline; stage B: nDCG@10 not
below stage A). How to run it, and the gold-set rules (including pending-kb queries that wait for `assistant-kb/**`), are in
`docs/design/workflow-apps/retrieval-eval.md`; the recorded numbers are in `retrieval-eval-results.md` beside it. Rerun it
after any change to chunking, the sparse text, stage A, the floor or the rerank model, and after the knowledge base lands.
