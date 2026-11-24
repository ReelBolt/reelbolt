# Embeddings (semantic file search)

Semantic search over project files (`POST /api/v1/projects/{id}/files/search`, the engine's
`search_project_files` tool) embeds file chunks and queries into Qdrant. The embedding backend is an
inference-provider capability, `Embedding`.

## What gets indexed

Only human-readable text is summarized and embedded. `ProjectFileContentClassifier`
(`Services/Indexing/`) decides from the MIME type, the file name and the first 8 KiB of content, and
both processors (`FileSummarizationService`, `ProjectFileIndexingConsumer`) apply it before any model
call, so every way a file arrives is covered.

- **Text:** under 10% control characters, in UTF-8, UTF-16/32 (byte-order mark, or BOM-less
  mostly-ASCII UTF-16), or Windows-1252 when not valid UTF-8. The encoding is picked from the first
  8 KiB and the file is transcoded to Unicode before summarizing and embedding. The content sniff is
  the authority; a `.txt` with binary bytes (NULs outside UTF-16/32, noise) is not text.
- **Images** (PNG, JPEG, GIF, WebP, by magic bytes; at most 20 MiB): the summarizer sends the image
  to the resolved `Vision` provider (`ResolveVisionAsync`: explicit -> default `Vision` capability -> none),
  metered as `UsageSource.Indexing`, and stores the description as the file's summary. It then
  republishes `ProjectFileIndexingRequested`; the consumer embeds the stored description, never the
  image bytes. An indexing message that arrives before the description exists leaves the file
  `Pending`. No Vision provider: the image is skipped (no summary, `NotIndexed`), not failed. The
  summarizer only sweeps `userFiles`, so an image an agent writes (`agentFiles`) is `NotIndexed`.
- **PDFs** (`application/pdf`, or a `.pdf` name on a generic MIME type; at most 20 MiB, body must start
  with `%PDF-`): the API never parses them. `ProjectFilePdfText` sends the bytes to the import-worker
  (`POST /v1/pdf-text`, the same client and `Import:Worker:*` settings as the brand-PDF import; the worker
  reads at most `IMPORT_WORKER_PDF_MAX_PAGES` pages, default 50, and 200 KiB of text; it also reports the page count it
  found and how many pages it read, and readers say "only the first N of M pages were read" only when pages were really left out) and the extracted text
  is treated as the file's text: control characters dropped, capped by `FileSummarization:MaxTextBytes` for
  the summary (with `[truncated]`) and `FileIndexing:MaxTextBytes` for the index, chunked as
  `<name>.pdf.txt`. The text is cached in process per PDF version (file id, size, upload time; 32 MiB of
  text, 30 minutes), so the summarizer, the indexer and the assistant share one extraction. Free, like any
  summary or indexing. A PDF with no text layer (a scan; there is no OCR), an unreadable one, or one over
  20 MiB is settled as `NotIndexed` and its summary is the plain reason (what to do about it) instead of a model summary; a worker that is down or busy leaves the summary
  `Failed` and the index `Failed` (code `PDF_READER_UNAVAILABLE` / `PDF_READER_BUSY`), so reindex retries
  them. `Import:Pdf:ProjectText:Enabled=false` switches this path off (the worker is never called): the assistant answers a plain "switched off on this workspace's server" message, the indexer settles the PDF as `NotIndexed`, and the summarizer stores no summary and leaves it `Failed`; a later reindex that reads the PDF puts a `Failed` summary with nothing stored back to `Pending` so the summarization sweep produces it. Two extractions run at once: the assistant's reads wait at most 30 s for a slot, then answer busy; the summarizer and indexer wait for a slot without that limit (nothing re-queues them after a busy). Engine agents cannot reach the worker (no `import-net`), so they see a PDF only through its
  summary (`agentSummary` in `ListProjectFiles`).
- **Everything else** (video, audio, SVG, HEIC/TIFF/BMP, archives, fonts, other binaries): no
  model call, no summary, no vectors. `SummaryStatus = Done` with a null summary and
  `IndexingStatus = NotIndexed`.

Adding a Vision provider later does not describe images already skipped: reindex re-embeds the stored
summary but does not re-run the summarizer, so such an image needs a fresh upload (or a content update)
to get a description.

## Resolution

`ResolveEmbeddingAsync`: explicit id -> the single enabled default `Embedding` capability -> legacy
`AzureOpenAI:Endpoint` / `AzureOpenAI:ApiKey` (+ `VectorSearch:EmbeddingDeployment`, default
`text-embedding-3-large`) -> none. With none, search answers `indexNotReady: true` (no 500).

Kinds: `AzureOpenAI`, `OpenAICompatible` (text-embeddings-inference, vLLM, Ollama, LiteLLM).
Others are rejected with a 400. **DeepSeek** documents no embeddings endpoint (only
`deepseek-flash`/`deepseek-v4-pro` chat models; api-docs.deepseek.com, 2026-10). **Anthropic** has none either.

## Local model

`docker compose up -d embeddings` starts text-embeddings-inference (CPU) serving
`Qwen/Qwen3-Embedding-0.6B` (1024 dimensions) on `127.0.0.1:${EMBEDDINGS_PORT:-9003}`. First start
downloads the weights into the `embeddings-hf-cache` volume. In `/app/admin/inference-providers` add:
Kind `OpenAICompatible`, Capability `Embedding`, endpoint `http://embeddings:80/v1`, model
`Qwen/Qwen3-Embedding-0.6B`, default on, then Test connection (expects `vector length 1024`).

## Index layout and switching models

Collections are named `{projectId:N}_{model-slug}_{hash8}_{dimension}`, one per project, model and
dimension, so models/dimensions never mix. Switching the default provider leaves the old collection
untouched and unsearched; the new one is empty until files are reindexed
(`POST /api/v1/projects/{id}/files/reindex`, or `POST .../files/{fileId}/reindex`). Per-file deletes
clear the file from every collection of the project, including the pre-capability bare
`{projectId:N}` collection. Orphaned collections can be dropped from Qdrant manually.

## Qdrant API key and TLS

The Inference API can talk to a hosted Qdrant (for example Qdrant Cloud) as well as the bundled
compose Qdrant. Set `VectorSearch__QdrantApiKey` (compose: `VECTOR_QDRANT_API_KEY`) together with an
`https://` `VectorSearch__QdrantUrl` (compose: `VECTOR_QDRANT_URL`); the gRPC port is
`VectorSearch__QdrantGrpcPort`. The key is passed to every Qdrant client, for both file search and the
platform docs index. A key with a non-https URL is refused: the Inference API fails at startup with a
message naming both settings, so the key is never sent unencrypted. With no key (the default) behaviour
is unchanged and plain `http://qdrant:6333` keeps working. The index can always be rebuilt from the
database by reindexing, so a new Qdrant cluster starts empty and fills via
`POST /api/v1/projects/{id}/files/reindex`.

## Hosted ASR and embeddings

A cloud deployment does not run the `whisper` and `embeddings` containers. Instead the Inference API
seeds the platform catalog (no organization, so every organization can use it) from the
`PlatformProviders` configuration array at startup. Each entry is one model capability. Entries
sharing one account (same kind, endpoint and key) land under one provider; with no such provider, the
provider that already holds the entry's model (same kind and endpoint) is reused, and re-keyed when the
entry carries a key, but only while that provider serves this one model (every re-key is logged, without
key material). A provider that also serves other models keeps its key, and the entry gets a new provider
of its own. Otherwise a provider named after the entry is created. The model and capability
are added when missing, and otherwise switched on or off and promoted to default as the entry says. A
new platform capability is pre-priced from the price book. The seeder never deletes anything, never
replaces a stored key with an empty value, and does nothing when the section is empty (self-host is
unchanged). Each entry passes through the same validation as the
platform admin API, so an unsupported kind/capability pair or a private endpoint is logged and
skipped without blocking the other entries or startup.

Each entry takes `Name`, `Kind`, `Capability`, `Endpoint`, `Model`, `ApiKey`, `IsDefault`,
`Enabled` (default `true`) and `TimeoutSeconds`. As environment variables:
`PlatformProviders__0__Name`, `PlatformProviders__0__Endpoint`, and so on. The cloud control compose
file wires two slots from `PLATFORM_ASR_*` and `PLATFORM_EMBEDDING_*` (see
an operator file); a blank `*_NAME` leaves the slot unconfigured.

- **ASR:** `Kind=OpenAICompatible`, `Capability=Transcription`, an endpoint speaking the OpenAI
  `/v1/audio/transcriptions` API, and the hosted model name.
- **Embeddings:** `Kind=OpenAICompatible` or `AzureOpenAI` only (the Embedding allowlist),
  `Capability=Embedding`.
- **Keys:** the `env:` sentinel is accepted only for kinds that support ambient credentials
  (Anthropic, Gemini, DeepSeek); for ASR and embeddings supply the key itself from a secret
  (`docs/secrets.md`). The seeder refuses `env:` for any other kind.
- **Dimension warning:** collections are keyed per project, model and dimension. The existing
  collections are Qwen3-Embedding-0.6B at 1024 dimensions. Pick a hosted model that outputs 1024
  dimensions to keep them, but note that the collection name also contains the model name, so a
  different model always starts an empty collection. Either way, reindex after switching
  (`POST /api/v1/projects/{id}/files/reindex`), or file search reports `indexNotReady`. The seeder
  does not reindex.
