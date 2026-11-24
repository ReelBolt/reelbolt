# Metering and billing (usage, credits, plans and the provider boundary)

ReelBolt meters every unit of work it buys from a provider and charges the customer for the
platform-supplied ones in **credits**. This document is the developer reference for that machinery:
the usage kinds and how one billable unit becomes a `usage_events` row, the two numbers every
row carries (our USD cost and the credits charged), model-capability prices, the markup and rate
multipliers (including what happens to a platform unit nobody has priced yet), how an existing install
is upgraded to them, the charging policy for work outside workflows, the credit ledger and its reserve → settle →
refund lifecycle with the failure classes that refund, the overdraft tolerance that lets a step finish
just over its estimate, entitlements and the plan catalog that is mirrored into the marketing site and
drift-checked in CI, the `IBillingProvider` boundary and the webhook pipeline with its idempotency
keys, and the subscription state machine.

The design record behind it is `docs/design/saas-metering.md` (usage taxonomy and failure
classification, WP B1), the credit unit and top-up design notes (including the Paddle ADR, WP B0) and [docs/design/model-catalog.md](design/model-catalog.md)
(the provider → model → capability → price catalog and the non-workflow charging policy), with the
the product decisions on credits and pricing. The code is `inference/src/ReelBolt.Shared/Metering/` (recorder,
price lookup, price book, the metering client decorators), `inference/src/ReelBolt.Shared/Billing/` (ledger, plans,
entitlement mapping), `inference/src/ReelBolt.WorkflowEngine/Services/Billing/CreditGate.cs` (the
per-step gate) and `inference/src/ReelBolt.Inference.Api/Services/Billing/` (webhooks, the provider
boundary, provisioning, the price book and the non-workflow charge path).

**Provider configuration is not in this page.** The provider selection (`Billing:Provider`), the Paddle
and Stripe environment variables, the plan price ids, the checkout/portal/seats endpoints, the operator
setup checklists and the Paddle and Stripe wire formats are all in [docs/billing.md](billing.md). This
page links to it rather than repeating it.

**Billing is off by default.** `Billing:Enabled=false` (the self-host default) makes every entitlement
unlimited, leaves the credit gate a no-op, and 404s the billing endpoints — so an install with no
billing configured records usage but charges nobody.

## Usage kinds: the billable units

ReelBolt's usage taxonomy is the `UsageKind` enum in
`inference/src/ReelBolt.Shared/Data/Models/Enums.cs`: append-only members, one per billable
physical unit. The enum is persisted by member name (EF string conversion) in `usage_events.kind`, so
appending a member needs no migration and renaming one breaks stored rows. `usage_events.quantity` is
a `decimal(28,6)` in the unit named below. The same enum is the closed list of price units: a
`model_capability_prices.unit` is one of these names.

| Member | Unit | Produced by in ReelBolt |
|---|---|---|
| `LlmInputTokens` | tokens | every chat call: workflow agents, ReviewLoop, room steps, the assistant, the file summarizer, decision and guardrail calls on logprob clients, vision captioning. The quantity is input **net of** the cached portion, so the three LLM kinds never overlap. |
| `LlmOutputTokens` | tokens | the same calls; the model's output tokens. |
| `LlmCachedInputTokens` | tokens | the part of the input the provider served from its prompt cache, billed at its own (lower) rate. Not to be confused with the engine's step-result cache (`FromCache`), which costs nothing and is a separate concept. |
| `EmbeddingTokens` | tokens | file indexing, semantic file search queries, platform-docs indexing and per-turn doc retrieval. The provider's reported count, or `ceil(chars / 4)` when a backend reports none. |
| `AsrAudioSeconds` | seconds of audio sent | `VideoAnalyze` transcription and voiceover word alignment. |
| `TtsInputBytes` | UTF-8 bytes of text sent | the `Voiceover` step, through `ISpeechSynthesisClient` (Fish Audio and the OpenAI-compatible speech endpoint both bill per byte). |

Voiceover hold (narration fit): a `Voiceover` step under the default `NarrationFitPolicy: Enforce` may shorten a line that cannot fit its scene (ONE NarrationWriter chat call, metered by `MeteringChatClient` against the step's own `workflow-step` usage scope) and re-synthesize only the changed lines. Its cold-start hold therefore carries +30% `TtsInputBytes` and one small chat call (`StepUsageProfiles` `VoiceoverEnforce`, priced as the NarrationWriter); a `Report` step holds exactly the old profile. The hold is an upper bound: settlement charges the recorded usage and the unused excess is released. A final `NARRATION_DOES_NOT_FIT` is refunded in full (shortener call included) only when the platform wrote the narration (`ScriptScenes`, `PlatformInternal`); for the user's own `Inline` text it is `UserInput` and the spend is kept, because the paid audio would otherwise stay in the object cache for a free re-run. A `CONTENT_BLOCKED` on text our shortener wrote is `PlatformInternal` (refunded); on the user's own text it is `UserInput`.
| `VideoGenSeconds` | seconds of generated video | the `VideoGenerate` step, from the take's **requested** seconds (the platform-side job spec), not the vendor's reported usage. A talking-head step (avatar audio) records it against the `LipSync` capability; the unit is unchanged. |
| `RenderOutputSeconds` | seconds of rendered Remotion output | the sandbox Remotion render. The engine fetches the stored object and probes it; the sandbox is not asked. |
| `EncodeOutputSeconds` | seconds of ffmpeg-encoded output | `VideoCompile`, from the probe of the file it just encoded. |
| `AnalyzeMediaSeconds` | seconds of source media analysed | the `VideoAnalyze` extraction phase: probe, silence and shot detection, frame grid, audio, keyframes. One row per analysed source. |
| `StorageGbDay` | gigabyte-days stored | the daily storage sampler, one row per organization. |
| `ImageGenImages` | images generated | `InferenceProviderCapability.ImageGeneration`, one unit per image delivered (see "Image generation and editing"). |
| `ImageEditImages` | images edited | `InferenceProviderCapability.ImageEdit` and the six per-tool picture capabilities, one unit per image delivered. |
| `SegmentationImages` | images segmented | `InferenceProviderCapability.Segmentation`: one unit per **call**, however many masks it returns (see "Segmentation and depth"). |
| `DepthImages` | depth maps | `InferenceProviderCapability.Depth`: one unit per call. |
| `VideoSegmentationFrames` | frames of video input sent | `InferenceProviderCapability.VideoSegmentation` on `VideoAnalyze`'s segmentation targets: one row per call, the quantity being the frames sent; priced in 16-frame units (see "Prompted video segmentation"). A content-cache hit makes no call and writes no row. |
| `ModerationCalls` | calls to a moderation model | `InferenceProviderCapability.ContentModeration`: one unit per call, on all four surfaces (generation prompt, ingest, generated text, publish). The price is a real `$0` row, not an unpriced gap, so the integration is visible in the ledger at zero cost. See [`docs/content-moderation.md`](content-moderation.md). |
| `RerankSearches` | rerank searches | `InferenceProviderCapability.Rerank`: one query plus up to 20 candidate texts is one unit, recorded under the ambient usage source: `Assistant` inside the assistant turn's hold and allowance (no new line for the user), or `Mcp` per call for an MCP client searching the docs (charged by the MCP source's own policy); billing off writes the row and charges nothing. A row is written when a response body arrived, including a malformed one, never on a timeout. A self-hosted reranker is a real `$0` price; a hosted one stays unpriced (rerank unavailable, listed in `GET /api/v1/pricing/coverage`) until an admin enters its published price. See [`platform-docs-search.md`](platform-docs-search.md). |
| `AudioCleanupMinutes` | billed minutes of source audio | `InferenceProviderCapability.AudioIsolation` (hosted voice isolation on fal, `fal-ai/elevenlabs/audio-isolation`, listed at $0.10 per minute): the source's audio SECONDS are measured and rounded UP to whole minutes (a started minute is a unit; 60 s is 1, 60.001 s is 2), capped at `Cleanup:Hosted:MaxSourceMinutes` (default 30) per request. Priced through the price book at the usual markup; a model with no price row is unofferable (fail closed, never a default price). `AudioCleanupCreditPricing` sizes the estimate and the hold. Local (ffmpeg) cleanup filters are free and write no row. Whether fal rounds or bills input vs output length is unconfirmed: must-measure, see `docs/fal-ai-models.md` "Audio isolation". |
| `VideoEnhanceSeconds` | billed seconds of OUTPUT video | `InferenceProviderCapability.VideoOutpaint` (fal Wan VACE / Luma reframe, see "Video enhancement: the VideoEnhance step") also meters here; `InferenceProviderCapability.VideoUpscale` (fal Topaz / bytedance): whole output seconds, rounded up, priced per output tier by price variant (`720p`, `1080p`, `uhd`, and the doubled `-hfr` twin when the output is above 30 fps); an unpriced tier is unofferable. See `docs/fal-ai-models.md` section 12. |
| `VideoEnhanceComputeSeconds` | vendor GPU compute seconds | `InferenceProviderCapability.FrameInterpolation` (fal RIFE / FILM, "$0.0013 per compute second"): the vendor returns no compute figure, so the hold is `ceil(outputFrames x ComputeSecondsPerOutputFrame x pixels / 1080p)` (setting `VideoEnhance:Hosted:ComputeSecondsPerOutputFrame`, default 0.05, UNMEASURED) and settlement is the hold. |

Two columns beside the kind carry attribution that is deliberately *not* part of it:

- `usage_events.source` (`UsageSource`) is `Workflow`, `Assistant`, `Indexing`, `DocsIndex`,
  `Storage`, `Unattributed`, `Editor`, `Mcp`, `FileSummary`, `TimelineAssist` or `Moderation`, and says which subsystem
  produced the call. The enum is append-only and stored by name. The source decides how a call is
  charged (see "Charging policy: work outside workflows" below). The last four:
  - `Mcp`: a model call made by an assistant tool run through the MCP bridge (`POST /api/v1/assistant/tool-call`), for the caller's org, the page's project when the caller may view it, and the caller.
  - `FileSummary`: the background file summarizer (and an image's vision description, which is its summary), for the file's org and project, with no user.
  - `TimelineAssist`: a timeline AI-assist proposal (`POST .../edit-timelines/{id}/proposals`), for the project's org, the project and the caller. `POST .../proposals/estimate` prices it first (balance read without the org ledger lock); an unparseable answer is charged what it metered (`424 PROPOSAL_FAILED` with `creditsUsed`; unpriced usage floors at the hold), and only a call that metered nothing (`PlatformProviderError` before any tokens) releases the hold; a call that times out (`424 PROPOSAL_TIMEOUT`), is abandoned (background job not polled for 45 s) or whose client disconnects is cancelled and its hold released, never charged; `EditorProposals:MaxPerHourPerUserProject` (default 20) caps asks per user+project per hour (`429 PROPOSAL_RATE_LIMITED`).
  - `Moderation`: a content-moderation call on any of its four surfaces, for the org, the project, the user and — when one exists — the execution and step result. It writes `UsageKind.ModerationCalls` at `$0`.
- `usage_events.billing_mode` (`UsageBillingMode`) is `Platform` (a platform-managed provider),
  `Byo` (the organization's own provider) or `Runner` (compute on the organization's own runner).
  The same `LlmInputTokens` event is `Platform` or `Byo` depending on who owns the resolved provider
  model capability — `MeteringChatClient.BillingModeOf` reads exactly that: a null `OrganizationId`
  means `Platform`, anything else means `Byo`.
- `usage_events.provider_id` holds the **model capability id** that served the call (for a migrated
  capability, the same value the old flat provider row had), `price_id` the price row that priced it
  (null for an oracle-priced or unpriced call) and `variant` the price variant it ran at. `rate_card_id`
  is kept for history and is never written again.

BYO and runner work is still **recorded** — with the quantity and with `credits_millis = 0` and
`cost_usd = 0` — so a usage dashboard stays complete and an organization can see what its own key did,
even though none of it is charged.

## How one billable unit becomes a usage_events row

Every metered call in ReelBolt goes through one interface,
`IUsageRecorder.RecordAsync(UsageRecordRequest)` (`inference/src/ReelBolt.Shared/Metering/`), and one
implementation, `UsageRecorder`. `UsageRecorder` is registered by `AddReelBoltUsageRecorder<TContext>()`
as a singleton **and** a hosted service: callers get the charged amount back synchronously while the
row itself is buffered and written in batches (100 rows or two seconds, whichever comes first; four
write attempts with a one-second delay between them).

A row is built in four steps:

1. **Attribution is ambient, not passed in.** `UsageRecordRequest` carries only the kind, quantity,
   model capability id (`ProviderId`), provider kind, model name and billing mode, plus the optional
   capability, price variant, provider (account) id and, for an oracle-priced call, the quoted USD. Organization, project, execution, step result,
   user and source come from `UsageScope.Current`, an `AsyncLocal` opened by the caller that owns the
   unit of work: `WorkflowExecutorService` opens one per step iteration (after the
   `workflow_step_results` row exists, so the scope carries its `StepResultId`), and
   `AssistantChatController` opens one per assistant turn (`Assistant`), while file indexing and the
   platform-docs index open their own. A request may override the scope explicitly. A call that runs
   **outside** any scope is not lost: it is written against the `Platform` organization with
   `source = Unattributed` and logged as a warning, so an unmetered code path shows up in the ledger
   and in the logs instead of silently costing money. With `Metering:FailOnUnattributed` set to `true`
   (`MeteringOptions`, default `false`) such a call throws `UnattributedUsageException` instead, and the
   metering wrappers let it through, so a model call that forgot its scope fails loudly in tests.
2. **The quantity is what the provider or the job spec says**, never an estimate of elapsed work. A
   provider that reports no usage gets the platform's own count (the UTF-8 byte length of the request
   text, or a character-based token estimate) rather than a silent zero.
3. **Pricing happens at write time** through `IModelPriceService.PriceAsync`, and the resulting
   `PriceQuote` gives the row its `credits_millis`, `cost_usd` and `price_id`. Pricing a row when it
   is created — rather than when it is read — is what makes a later price change apply to new usage
   only.
4. **The row is queued** on an unbounded channel, and the amount is added to the scope's running
   credits, which is what a mid-step check can read without touching the database.

**Idempotency is the row's identity.** Every row carries a unique `idempotency_key` (a unique index on
the column), so a retried write is a no-op. The default key is
`{stepResultId or scopeId}:{callSeq}:{kindCode}`, where `callSeq` is a per-scope counter and
`kindCode` is `in`, `out` or `cin` for the three LLM kinds and the enum name otherwise — one model
call therefore writes one row per kind under one shared call number. A unit with a natural identity of
its own overrides the key: a video-generation take is `videogen:{jobId:N}`, so a replayed settle never
double-charges, and the daily storage sample is `storage:{org}:{yyyy-MM-dd}`. The sink
(`EfUsageEventSink<TContext>`) checks the keys before inserting, and on a unique-index race falls back
to inserting row by row so one duplicate never loses its neighbours. If all four write attempts fail,
the batch's keys and amounts are written to the log at `Critical` level, explicitly so they can be
replayed by hand.

**One call writes one row per kind, and the LLM split is exact.** `MeteringChatClient` wraps every
client the chat factory hands out and, on each response, records `LlmInputTokens = input - cached`,
`LlmOutputTokens = output` and `LlmCachedInputTokens = min(cached, input)`, using the same call number
for all three. Embedding, ASR, TTS and decision clients are wrapped the same way by their own
factories, which is the point of the design: a call site never has to remember to meter, because the
factory is the choke point. A streaming chat call reports its usage as the sum of the streamed
updates, and the record runs in a `finally` block, so a consumer that stops early still pays for the
tokens it caused.

## Cost and credits: two numbers on every row

Every `usage_events` row carries **both** numbers (decision D10):

- `cost_usd` — **our** provider cost (COGS) in USD for that quantity. It is the one that decides
  whether the business is making money, and it is the number the pricing work package B13 is meant to
  replace the placeholder estimates with. It is `0` for `Byo` and `Runner` rows, because that work is
  not our cost.
- `credits_millis` — the credits charged to the organization, in **integer millicredits**
  (1 credit = 1,000 millicredits, decision D8). Integers rather than a float because embedding and ASR
  charges are fractions of a credit (a 500-token embedding is well under one millicredit) and must
  accumulate without rounding to zero, and because a `bigint` cannot drift.

The link between the two is the price row. A `model_capability_prices` row holds our cost
(`cost_usd_per_unit`), and the credits are derived from it at pricing time:

```text
credits_millis_per_unit = credits_millis_per_unit_override
                          ?? cost_usd_per_unit × markup × 1000 ÷ credit_usd_value
```

`markup` (default **1.2**) and `credit_usd_value` (**$0.006**, D9) live in the one-row
`pricing_settings` table. So with no override, one credit charges $0.005 of provider cost, and changing
the credit price of every unit is one markup edit rather than a row-by-row change.

Two rounding rules matter, and both are in `ModelPriceService.Price`: the charge is
`quantity × credits_millis_per_unit × multiplier` rounded half away from zero — **once per event**,
never per unit — and a non-zero platform-billed event is floored at **one millicredit**, so metering
never reads as free. `cost_usd` is not rounded at all: it is stored at `numeric(18,8)`.

## Model-capability prices and rate multipliers

The full design is [docs/design/model-catalog.md](design/model-catalog.md). Pricing hangs off the
provider catalog, which the Inference API owns and the WorkflowEngine maps read-only:

| Table | What one row is |
|---|---|
| `inference_providers` | An **account**: kind, endpoint, encrypted key, headers, timeout, OAuth mode, organization. `is_system` marks the platform compute provider. |
| `inference_models` | A model under an account: the vendor's `model_code` and the `display_name` customers see (unique per organization). |
| `model_capabilities` | One thing a model does (`Chat`, `Transcription`, `ImageEdit`…): `is_default`, `is_enabled`, last test, and `price_source` (`Table`, or `Oracle` for Higgsfield). Its id is what every `*ProviderId` field stores. |
| `model_capability_prices` | One price of one unit of one capability: `unit` (a `UsageKind` name), optional `variant`, `cost_usd_per_unit`, optional `credits_millis_per_unit_override`, `effective_from` / `effective_to`, `notes`. |
| `pricing_settings` | One row (id 1): `markup` (editable by a platform admin), `credit_usd_value` (changed only by migration), `catalog_upgraded_at`. |
| `rate_multipliers` | One row per `(billing_mode, usage_kind)`; unchanged. |

`rate_cards` is **frozen**: no seeder, endpoint or runtime reader touches it. The upgrade reads it once
(see "Upgrading an existing install" below), and it stays as history.

**A price is superseded by dating, never by editing in place.** Adding a price for the same unit and
variant closes the open row at the new row's `effective_from`, so a historical `usage_events` row keeps
the price that was actually charged. Two rows of one unit and variant cannot start at the same instant
(`409`), and retiring a row sets its `effective_to`. Prices exist on **platform** capabilities only: a
BYO capability is never priced (BYO is ×0) and the organization endpoints never return or accept prices.

**The override is for a price that is not cost-derived.** Leave it empty and the credits follow the
cost and the markup. Set it and the row charges exactly that many millicredits per unit, whatever the
markup.

**Variants distinguish prices of one unit inside one capability.** The lookup picks the exact variant,
else the row with no variant, else the dearest variant. Variants in use:

- video generation: the resolution the vendor quotes (`768P`, `2K`, `720p`, `1080p`…), so a 2K
  MiniMax second costs the vendor's 2K rate;
- fal.ai image tiers: the `#tier` suffix of `IBillingModelKeyed.BillingModelKey` (`low_quality`, `1K`,
  `max`…);
- compute: the tier `720p`, `1080p` or `uhd` on the system compute capability.

**Prompt-length tiers (`gt{N}k`) price long-context LLM calls.** A token unit (`LlmInputTokens`,
`LlmOutputTokens`, `LlmCachedInputTokens`) may carry rows whose variant is `gt{N}k` (case-insensitive):
the price for a call whose whole prompt is longer than N thousand tokens. The chat and decision meters
pass that length as `PromptTokens` on every unit of the call (cached input included: the Anthropic
adapter's `InputTokenCount` already sums uncached input, cache reads and cache writes, and OpenAI's and
Gemini's prompt count already includes cached tokens, which is what each vendor counts toward its
threshold). With no variant requested, the lookup takes the largest tier with `N × 1000 < PromptTokens`
before the variant-less row, so all three units of a 250k-token prompt bill at `gt200k` and the event
records `gt200k` as its variant; an explicit variant always wins, and no tier rows (or no length) leaves
the lookup as above. The assistant's round-trip and look claims and the timeline proposal hold pass the
length they already estimate; the cold-start profile estimates do not (an under-sized hold, not a mis-bill).

**Which price row applies** (`ModelPriceService`, 60-second snapshot cache): first the capability that
served the call, when its model code is the model that actually ran; then a capability of the same
`Capability` under the same provider whose model code is the model that ran; then, on the platform
only, any platform capability of the same kind, `Capability` and model code (this covers the legacy
`AzureOpenAI:*` configuration fallback, which has no row); and last, the dearest effective row for that
unit among the provider's capabilities of that `Capability`, so an explicitly requested model never
prices below the configured one. (The picture editor no longer relies on that last step for a Platform row: a
requested model outside the priced catalog is refused before the call.)

**Oracle-priced capabilities** (Higgsfield) are recorded from the USD the vendor's oracle quoted,
converted at the same markup, with `price_id` null. That is the only billed usage without a price row.

A **rate multiplier** scales the credits of a priced event by billing mode: one row per
`(billing_mode, usage_kind)` pair, where a null `usage_kind` is the default for the mode and a
kind-specific row beats it. No row at all means `Platform` is charged in full (`1.0`) and `Byo` and
`Runner` are free (`0`) — which is how decision D8's "BYO inference costs 0 credits" and "a desktop
runner costs 0 compute credits" are expressed as data rather than code. The seeder writes exactly those
three rows (`Platform = 1`, `Byo = 0`, `Runner = 0`) into an empty table only. Multipliers are kept and
stay editable at `GET`/`POST /api/v1/pricing/multipliers`.

**Compute is priced by the job spec, and its resolution tier is the variant.** The three compute kinds
are priced from what the platform itself measured — the probed duration of the file it rendered or
encoded, or of the source it analysed — and never from wall-clock time or a runner-reported figure,
because a runner is untrusted (decisions D13–D16). `ComputeMetering` prices them against the **system
compute capability** ("Platform compute" → "Rendering and encoding" → `Compute`, fixed ids in
`WellKnownModelCatalog`), which the upgrader creates and no admin can edit or delete.
`ComputeMetering.TierFor(width, height)` takes the **short** side of the probed frame, so `<= 720` is
`720p`, `<= 1080` is `1080p` and anything larger is `uhd` (a vertical 1080x1920 reel is `1080p`, and an
unknown size such as audio-only prices as `720p`).

**The price book pre-fills; it is never read while metering.** `ModelPriceCatalog` (in
`ReelBolt.Shared/Metering/`) names the models the platform supports, each with its capability and its
**published vendor list price**, every one carrying its own citation and read date. `PriceBook`
(`Services/Billing/PriceBook.cs`) turns it into suggested rows: when a platform capability is created
(by the admin API, the assistant's admin tools, `PlatformProviderSeeder` or the image-tools setup), it
gets the book's prices for its units, tiers as variants, MiniMax and fal.ai video resolutions as
variants (the fal.ai talking-head models are price rows under `LipSync`, not `VideoGeneration`, with the same resolution variants), and `PricingDefaults.ComputePrices` for the compute capability. `GET
/api/v1/pricing/price-book?kind=&capability=&model=` returns the same suggestions for the add-price
form. Every pre-filled note still begins with the marker word `PLACEHOLDER`, because none of these
figures has been reconciled against a provider invoice. an operator file reads the price rows
in effect now from `GET /api/v1/inference-providers` and keeps its invoice-reconciliation lane BLOCKED
while any of their notes starts with that marker.

| Price book source | Cost per unit | Basis |
|---|---|---|
| the models in `ModelPriceCatalog` | the vendor's own published price, per token / second / byte / image | Anthropic, DeepSeek and Fish Audio from each vendor's own pricing page, read 2026-10-09; MiniMax from `MiniMaxVideoGenerationClient.Describe`; fal.ai from each model page; the OpenAI audio and embedding rows from this repository's own recorded vendor figures (docs/design/cloud-provider-adr.md). **List prices, not invoices.** |
| compute (`PricingDefaults.ComputePrices`, nine rows) | $0.0000362 per 720p media second, scaled by the tier's pixel count for 1080p and UHD, with encode at 0.5 and analyse at 0.25 of render | The catalog seed's 720p render price ($0.0000181: a dedicated 8 vCPU Hetzner server at about $0.26/hour, one output second per run-second) doubled on 2026-11-12 so the price also pays for idle pool-server hours (Hetzner bills every started hour). Markup and credit value are unchanged. The run-second-to-output-second conversion is **estimated** (read at one-to-one, the conservative end). `PricingDefaults` mirrors the seed's compute block; the seeder rolls live rows on boot, ending the old row at the boot time (never back-dated). |

**A model the book cannot price is named, never guessed.** `ModelPriceCatalog.Unpriced` carries the
models the platform supports that no retrievable first-party page priced (the `AzureOpenAI` and
`OpenAICompatible` chat models, the two self-hosted decision kinds, the fal.ai segmentation and depth
endpoints and the OpenAI image models, among others), each with a reason string. A capability on one of
them gets no pre-filled row and shows up as a coverage gap until an admin prices it; there is no
provider-kind wildcard any more.

**An unpriced platform unit is recorded at zero credits and logged, not thrown.** Pricing itself fails
closed: `ModelPriceService.Price` throws `UnpricedUsageException` when `billing_mode = Platform` and no
row applies. `UsageRecorder` is where that becomes survivable: it catches the exception, logs it at
`Error` level ("has no price; recorded unpriced"), and **still writes the row** — with
`credits_millis = 0` and no `price_id` — so the gap is visible in the ledger and loud enough to page
on, while a model call the customer has already paid for is never turned into a workflow failure. For
`Byo` and `Runner` a missing price is not an error at all: those return zero credits and zero cost.

Two known gaps in the same area, stated rather than implied: the daily storage sampler writes its
`StorageGbDay` row **directly**, with `credits_millis = 0` and `cost_usd = 0`, so storage is metered
for visibility but charged nothing (storage is a plan quota; see "Charging policy" below); and compute
is recorded on success only, so a failed render or encode is not metered at all until the failure
classes are wired (see "What is designed but not built yet" below).

## Output resolution and credits

A run can choose its output resolution (`outputResolution`: `Auto`, `720p`, `1080p`, `2160p`, short
side; see [editing-styles.md](editing-styles.md) "Output resolution of a run"). Its credits scale with
the output pixels through the existing compute pricing, with no new price row: render and encode rows
are priced by `ComputeMetering.TierFor`, whose short-side tiers `720p` / `1080p` / `uhd` are the compute
capability's price variants, seeded (`PricingDefaults.ComputePrices`) at 1 : 2.25 : 9, which is about
**0.44× / 1× / 4×** of 1080p. For 16:9 and 9:16 outputs that equals output
pixels ÷ 1920×1080 (decision 3) exactly. For other shapes it does not: the tier comes from the short side
alone, so a square or 4:5 output is priced like the 16:9 output with the same short side (2160×2160 and
2160×2700 are 4×, against a pixel ratio of 2.25× and 2.81×; 1080×1080 is 1×, as before this work). This is
deliberate: the short-side tiers were kept over exact pixel billing (decided 2026-10-13). Model and token costs do not
change with resolution.

**The pre-run estimate** (`GET /api/v1/projects/{projectId}/workflows/{id}/estimate?outputResolution=`
and the execute preflight) multiplies the compile and render parts by
`OutputResolution.PixelFactor = (target / 1080)²` (720 → 0.444, 1080 → 1, 2160 → 4). Each step's
displayed distribution is scaled before the run-level range is combined (see "The pre-run estimate"
below): `VideoCompile` and `TimelineRender` steps scale their whole distribution (typical, p25 and
p75); an `Agent` step whose agent is granted `ToolGroup.SandboxRender` (the Author, which renders the
Remotion film) and a `Revise` step scale only their p75, and only when the factor is above 1, because
their bottom is token work a resolution does not change. The profiles themselves price compute at the
1080p reference tier, so the factor is applied once (pricing at the run's own tier as well would count
4K twice, 16×). A past-runs range only counts runs that chose the same resolution. A render step's
per-step history is counted per resolution too (QA-15): at a resolution other than Auto/1080p, a render
step with at least 3 charged samples from runs at exactly that resolution (exact agent, then agent type)
shows that distribution as it is, unscaled; otherwise its Auto/1080p samples (else its profile) are
scaled as above. Every other step pools its history across all resolutions. Auto (null) is factor 1,
but a render step at Auto reads only its Auto/1080p samples, so its figures change once the org has
render history at another resolution (4K or 720p samples no longer count toward Auto); for an org with
no explicit-resolution history, Auto leaves every figure as it was. The
execute endpoint resolves the choice **before** `RefuseIfUnaffordable`, so an unknown resolution is a
400 before any credit check and the affordability preflight already includes the factor; the
assistant's `ExecuteWorkflow`, its REST twin, the approval card and composition revisions price at
the run's resolution the same way.

**Holds.** The hold of every step that produces the delivered picture (a `VideoCompile` step, a
`TimelineRender` step, a `Revise` step, and an `Agent` step whose agent is granted `ToolGroup.SandboxRender`) is its
[cold-start plan](#the-cold-start-hold) hold (or its history p90) multiplied by the pixel factor when
the factor is above 1, and is never shrunk for a smaller choice. History samples carry their execution's
`OutputResolution` (a LEFT join to `workflow_executions`; a step result whose execution row is gone counts
as Auto, never dropped). A render step's plan hold reads only its **reference** samples, Auto and 1080p
(`ResolutionHoldScaling.IsReference`: pixel factor 1), so 4K runs never inflate a 1080p hold; with none,
the step is cold. Its final hold is `ResolutionHoldScaling.HoldAt` = **max(reference hold × factor above 1,
p90 of its own samples at exactly that resolution)**, the latter only with at least `MinSamples` of them
(exact agent, then step type). That native p90 can only raise a hold, never lower it: cheaper 4K history
leaves the scaled reference, and a 720p run never holds less than the reference. Every other step pools
its history across all resolutions. A 4K run whose every step renders and has no history therefore
requires exactly the cold-start plan × 4; a non-rendering step keeps its plan hold. No hold is below the
plan's, nor below the old per-step default × the factor. The rule lives once in `RunCreditHolds.Plan(...,
outputResolution)` (`ReelBolt.Shared/Metering`), which returns the FINAL holds and a per-step
`RendersOutput` flag, and runs in two places so they never disagree about a run: the API preflight
(`WorkflowCostEstimator`, which sums the plan's holds into `RequiredMillis` and scales the display by the
plan's flag) and the engine's per-step hold (`ICreditHoldEstimator.RunHoldMillisAsync(..., outputResolution,
...)`, which returns the plan's entry); `ResolutionHoldParityTests` pins both to the same figures, also over
the same mixed-resolution history. A composition revision is priced and held
at its source run's resolution, which it inherits.

**The plan ceiling.** A choice above the plan's `MaxOutputHeight` is refused with 402
`PLAN_NOT_ENTITLED`, feature `resolution` (see "Entitlements and the plan catalog"), so a run is never
priced at a resolution the plan does not include.

## Source footage and credits: the hold follows the source

A `VideoAnalyze` step is priced cold at a fixed 138.7 s of analyse compute at the 1080p tier plus model tokens, and
no ASR. Its **settle** meters the probed source duration at the source's own resolution tier (`AnalyzeMediaSeconds`;
the UHD price is 4x the 1080p one) and, when the step transcribes (`Transcription` not `Off`, the default is
`Optional`), the audio seconds (`AsrAudioSeconds`, priced like any other transcription). A long 4K source therefore
settled far above what was held and shown.

`SourceFootageCredits` (`ReelBolt.Shared/Metering`) closes that gap as an **addition**, never a replacement, in the
shared `RunCreditHolds.Plan`: when every source of the step is a **project file** (`VideoSourceKind.ProjectFile`), the
plan adds `max(0, analyse(real seconds at the source's tier) - analyse(138.7 s at 1080p)) + asr(audio seconds)` to
the step's hold, priced through the same `IProfileQuantityPricer` as the profile (no price is restated). The real seconds
and tier come from the file's **stored probe** (`media_derivatives.source_probe_json`, the newest readable one); a file
with no probe is known by its **size** only (`size / 2 MB/s`, capped at `VideoSourceLimits.DefaultMaxSeconds`, priced at
the 1080p reference and assumed to have sound). The author's `InSec`/`OutSec` trim window is what is priced, and a
source without audio adds no ASR. An unknown source (a step output, a brief file, no probe and no size), a resolver
failure, or a source small enough to sit inside the 138.7 s profile all give **today's hold**: holds only grow, and a
resolver failure never fails a run (it is logged and the step holds its default). Prices are unchanged.

The Inference API (402 preflight `RequiredMillis`, the shown estimate and the approval card, which add the same figure
to the step's low, typical and high) and the engine (`ICreditHoldEstimator.RunHoldMillisAsync`) both resolve the footage
with the same `SourceFootageCredits.ResolveAsync` over their own `ProjectFiles`/`MediaDerivatives` and hand it to
`RunCreditHolds.Plan`, so they cannot disagree (`SourceFootageHoldTests`, with the `ResolutionHoldParityTests` pattern).
The figure is a hold, not the bill: the settle still charges the metered actual. A settle above the hold is charged
inside the overdraft tolerance above and otherwise follows "When a step settle fails"; it can never end a run free.
The step cache key is untouched.

## Run rate limit buckets and `run_rate_events`

Runs that an assistant or MCP client starts are limited per user and project over a rolling hour, in two buckets so a free deterministic run never uses up the allowance of a run that holds credits. This is an abuse and spend guard, separate from the credit ledger; a rate-limited start writes no hold and no usage row.

| Bucket | Default | Config key | Runs |
|---|---|---|---|
| `paid` | 10 per hour | `Assistant:MaxPaidExecutionsPerHour` (falls back to the legacy `Assistant:MaxExecutionsPerHour` when only that is set) | Anything that may call a model or vendor, and every revision |
| `free` | 60 per hour | `Assistant:MaxFreeExecutionsPerHour` | Workflows in which every step is deterministic |

`RunRateClassifier` (pure, in `ReelBolt.Shared`) picks the bucket from the workflow's steps and denies by default: a workflow is free only when every step provably calls no model or vendor. `Conditional` (expression mode only), `Extract`, `DesignSystem`, `VideoCompile`, `TimelineRender`, `ThumbnailGenerate` and a `VideoAnalyze` whose config calls no provider (`VideoAnalyzeStepConfig.CallsAnyProvider` is false) are free-eligible; `Agent`, `ReviewLoop`, rooms, `ForEach`/`Parallel`, `Revise`, `DesignGraphic`, `VideoGenerate`, `Voiceover`, any unknown or future step type, an unparsable config and an empty workflow are paid. A reflection test makes a new `StepType` without a decision fail.

Counting is done from `run_rate_events` (API-owned; `RunRateEvent`: `UserId`, `ProjectId`, `Bucket`, `Source` of `execute` or `revision`, `ExecutionId`, `CreatedAt`; indexed on user, project, bucket, time). `IAssistantExecutionGuard.TryAcquireAsync` classifies, counts the last 3600 seconds, runs the start callback and inserts the event under one lock (an in-process semaphore per user and project plus a PostgreSQL advisory lock), so concurrent starts cannot overshoot. The table is not tenant-filtered (it is never addressed by a caller-supplied id) and `AccountPurgeService` deletes a user's rows.

The classification is read under the lock, but the engine reads the workflow's steps again when the run executes. The admitted bucket is therefore stamped on the run (`workflow_executions.admitted_run_rate_bucket`, engine-owned, mapped by the API without migrating it) and on `WorkflowExecutionRequested.AdmittedRunRateBucket`. Before the first step, a run admitted as `free` whose loaded steps now classify as paid fails with `RUN_CLASS_CHANGED` (UserInput; nothing has run or been charged) and the message "This workflow changed after the run started; start it again." A missing or `paid` stamp performs no check. The stamp lives on the row so Retry and the recovery sweeper's re-publish keep it. The rate-limited routes are the assistant `ExecuteWorkflow`, the MCP bridge (the same tool), `POST .../assistant/execute` and `ApplyReviseComposition`; a human running a workflow from the UI is not counted.

A refusal states the bucket, how many were used, the limit and when to retry on every surface; the field list is in [platform-assistant-mcp.md](platform-assistant-mcp.md#assistant-execution-rate-limiting). The revision route's own `REVISION_RATE_LIMITED` (`CompositionRevisions:MaxPerHourPerUserProject`, 10) and the graphics route's `GRAPHIC_RATE_LIMITED` (20) carry the same `used`, `limit`, `windowMinutes` and `retryAfterSeconds`.

---

## Price coverage: making a missing price visible

**The failure this section exists to prevent is silent, not loud.** An unpriced Platform event is
written with `credits_millis = 0`, `cost_usd = 0` and no `price_id` (now also flagged `unpriced` and charged at
least its hold, see "Unpriced models fail closed"); before that rule the only trace was a log line and a zero in a
column nobody sums. `PriceCoverage` makes it mechanical instead. A gap is an **enabled
platform** capability (capability, model and provider all enabled) with no effective price for a unit
its capability meters (`ProviderCapabilityUsage.ByCapability`; `Compute` needs the three compute
units). Oracle-priced capabilities are exempt, and BYO capabilities are never gaps.

**What went wrong before, found on the staging deployment on 2026-10-09.** Fish Audio serves
`Transcription` as well as `SpeechSynthesis`, and the staging default transcription provider was a
Fish Audio `transcribe-1-pro` row. The old rate-card seed priced `AsrAudioSeconds` for two
hand-written provider kinds only, so that pair had no card and the ledger held one unpriced row
(`FishAudio | AsrAudioSeconds | transcribe-1-pro | 0.300 s | 0 credits`). The shape of the bug — a
hand-written price matrix drifting away from the capability table — is what the checks below address.

| Where | What it sees | What it does |
|---|---|---|
| `PriceCoverageTests` | the capability table, the price book and an in-memory catalog | fails the build when `ProviderCapabilityUsage` and `InferenceProvidersController.IsUnsupportedCombination` disagree for any (kind, capability) pair, when a capability meters no unit or nothing serves it, when a catalogued price is for a unit its capability does not meter, or when an unpriced entry gives no reason |
| `ModelCatalogUpgradeRealPostgresTests` (only when `REELBOLT_TEST_PG` is set) | a real PostgreSQL holding a realistic pre-catalog install and its rate cards | migrates and upgrades it, then checks that shared accounts became one provider and every old id still resolves to the same kind, endpoint, model and key |
| `ModelCatalogUpgrader` at every boot | **a live deployment's own data** | logs `Error` ("Price coverage gap: …") naming every provider, model, capability and missing unit |
| `GET /api/v1/pricing/coverage` | the same data, for a person | returns the gaps (capability id, provider, model, capability, missing units), rendered on the admin billing page |

## Unpriced models fail closed

With `Billing:Enabled` (SaaS), a **Platform** model capability priced from its table (`price_source = Table`) with
no effective `model_capability_prices` row (`effective_from <= now < effective_to`, or open-ended) is never free.
BYO and Runner usage, oracle-priced capabilities (Higgsfield) and `Billing:Enabled = false`
(self-host) are untouched.

| Where | Rule |
|---|---|
| `InferenceProviderResolver` | treats it as absent: an explicit id falls to the capability default; nothing left throws `ModelUnpricedException` (code `MODEL_UNPRICED`, user-readable). The engine's `FailureClassifier` maps it to `PlatformInternal`: the step persists `failure_code = MODEL_UNPRICED` and is refunded. Chat and embeddings do not fall to the `AzureOpenAI:*` config fallback then. A pinned `ResolveChatAsync` id answers null. |
| Pickers | `GET /api/v1/inference-providers/selectable` (step editors) omits it, by the resolver's rule: a table-priced row needs a price in force for every unit it meters, not just one; the assistant's picture tools resolve through the resolver. |
| Admin API, assistant provider tools | "unpriced" here is the resolver's rule: not every unit the capability meters has a price in force (one price row is not enough). Enabling it, or keeping it enabled through any other change, is refused: REST `400 {message, errors}`, the assistant a plain refusal that writes nothing. So is switching on its **model** (`PUT …/models/{id}` under an enabled account) or its **provider account** (`PUT /{id}`, over the account's enabled models), and the assistant's update tool, which switches the row's model and account on too. Exception: oracle-priced and its oracle answers a probe quote. A new platform capability the price book cannot price is created switched **off** (the assistant says so in `note`). |
| API callers | a resolver refusal is never a 5xx or a broken tool. REST answers `409 {code: "MODEL_UNPRICED", message}` (picture-editor operations and their capabilities route, after the ImageEdit fallback; subject cut-outs; timeline proposals; voice cloning). Assistant chat answers the message as an ordinary reply, before any hold. Picture tools: `ListPictureTools` lists the tool as unavailable with the reason, the others refuse with `{error, code: "MODEL_UNPRICED"}`; the MCP bridge turns that refusal (or a thrown `ModelUnpricedException`) into an `isError` result with the plain sentence, logged as `model_unpriced`. Vector search reports the embedding model unavailable; the docs index loop reports not indexed and retries. |
| Metering (`UsageRecorder`) | a Platform unit whose lookup fails is still written (quantity, model), `credits_millis = 0`, flagged `usage_events.unpriced = true`, counted on `reelbolt.billing.unpriced_usage` (`source`, `capability`; meter `ReelBolt.Inference.Billing`, exported by both the Inference API and the Workflow Engine), logged at Error. |
| Settle | any unit of work with unpriced usage is charged `max(metered, hold)` (an assistant/MCP turn: `max(metered + its unpriced steps' claims, hold)`): engine `CreditGate` (step settle and non-refunded release; it also checks the step's flushed `usage_events.unpriced`), picture-editor AI operations, subject cut-outs, timeline proposals, assistant/MCP turns. The one in-process signal is the usage scope's `SawUnpricedPlatformUsage`, set by `UsageRecorder` for Platform units only. |
| Estimates | `NonWorkflowChargeService.EstimateAsync`/`EstimateOneUnitAsync` return `Billing:NonWorkflow:UnpricedEstimateCredits` (default 10) for an unpriced or unquotable Platform call, never 0. |
| Daily spend report | lists every gap (`PriceCoverage.FindAsync`) and the window's unpriced events at Error. |

The cold-start seed fallback (`StepUsageProfiles.SeedFallbackMillis`) applies the markup (live `pricing_settings`
markup where a price service is available, else 1.2), so estimates and holds built on it match what settle charges.
So do `StepCreditDefaults`' per-step-type rows (the at-cost B0 figures, including the `VideoGenerate` p50/p90, times
the default markup 1.2).

## Upgrading an existing install

The schema change is the EF migration `AddModelCatalog`. It turns every old `inference_providers` row
into one provider, one model and one capability whose **id is the old row's id**, so every reference
— the three foreign keys, step-config JSON ids, `usage_events.provider_id`, step-cache fingerprints —
keeps its value. No step config is rewritten and no step-cache key changes.

`ModelCatalogUpgrader` then runs from `DatabaseSeeder.SeedAsync` right after the API migration, on every
boot, and is idempotent:

1. **System compute provider.** It creates the compute provider, model and capability when missing.
2. **Price port, once** (while `pricing_settings.catalog_upgraded_at` is null). For every platform
   capability and every unit it meters, it finds the effective rate card with the old rule (same kind
   and unit; exact model name > longest `prefix*` > `*`; later `effective_from` wins) for the model
   code and writes its cost as a price row. `code#tier` cards become variant rows, compute cards become
   the compute capability's tier variants, and a retired card is not ported. A future-dated card (an
   announced price change) is ported too, with its own `effective_from`, wherever the old rule would
   have switched to it; one that cannot be ported because the capability already has its own rows for
   that unit is logged. The card's credits become
   an override **only** when an admin had set them away from `cost × 1000 ÷ 0.006`; seeded cards are
   cost-derived, so they now pick up the 1.2 markup. With no rate cards at all (a fresh install) it
   ports from the price book. Then it stamps `catalog_upgraded_at`.
3. **Video resolution variants.** For platform MiniMax and fal.ai `VideoGeneration` capabilities it adds
   one row per resolution from the client's static price table, where that variant has no row yet.
4. **Consolidation**, set by `ModelCatalog:Consolidation` (env `ModelCatalog__Consolidation`):
   `Apply` (the default when unset), `DryRun` (log what would merge, write nothing) or `Off`. Any other
   value falls back to `DryRun` with a warning, so a typo never merges accounts. `Off` is not needed for a
   normal upgrade. Providers are one account only when the organization (never across organizations), kind,
   trimmed endpoint, OAuth mode, timeout and extra headers match and both carry a present, decryptable, equal
   key (compared by SHA-256). A provider with no key, with the `env:` sentinel, or with a key that fails to
   decrypt is never grouped (the 1:1 split already resolves every id). One exception to the header match: a
   provider without extra headers whose key ciphertext is byte-identical to that of providers with
   headers joins them (the old image-tool rows copied the fal row's key but not its headers), and the
   survivor keeps the headers. In each group the
   oldest provider survives, the others' models move to it, and the emptied providers are deleted.
   Inside the survivor, models with the same `model_code` merge when their capabilities do not collide
   and their enabled flags match. Capability rows never
   change, so **resolution is identical before and after**. `GET
   /api/v1/inference-providers/consolidation-report` returns what a run would merge now.

It ends by logging any price coverage gap. On PostgreSQL the whole run is one transaction holding a
`pg_advisory_xact_lock` (`ModelCatalogUpgrader.AdvisoryLockKey`), so when several API replicas boot at
once the others wait and then find the stamp and the merged accounts, so the price port and the
consolidation each run once. A step that fails is rolled back to its savepoint, logged as an error and
counted (`reelbolt.model_catalog.consolidation_failed`, `reelbolt.model_catalog.upgrade_step_failed`); it
never aborts boot, and the next boot retries it.

**This upgrade changes billing on purpose.** Credits rise by the 20 % markup, and a video second is
priced at its own resolution instead of 768P.

**Rollback.** Run the new image's `Down` migration before deploying the previous image; the steps are in
[docs/design/model-catalog.md](design/model-catalog.md) §4.3 "Rollback". `rate_cards` is intact, so the
old metering resumes, and usage recorded in between keeps its credits and its `price_id` (the `Down`
keeps `usage_events.price_id` and `variant`; the previous code ignores them). The `Down` refuses, with an
error naming the first offending row, if any model's organization differs from its provider account's or
any capability's from its model's, rather than copying one scope's endpoint and key into another's row.

## Image generation and editing: two usage kinds

**The image arm bills per image RETURNED, and it is two kinds rather than one.**
`UsageKind.ImageGenImages` counts an image produced by `InferenceProviderCapability.ImageGeneration`
and `UsageKind.ImageEditImages` counts one produced by `InferenceProviderCapability.ImageEdit` — or by any
of the picture editor's six per-tool capabilities split out of it (`ImageErase`, `ImageFill`, `ImageExpand`,
`ImageBackgroundReplace`, `ImageRelight`, `ImageUpscale`): they are all edits of a picture the customer
already has, go through the same editing arm of `MeteringImageClient`, and are priced per endpoint, so the
split changes which ROW serves a job, never what it is billed as. A call
that returns `n` images meters `n` units, not one — recording one unit per call would under-charge a
batch by a factor of `n`, and recording before the response arrives would charge for images that were
never produced. `MeteringImageClient` is the decorator that does it, applied in `ImageClientFactory`
outside the client cache, exactly as `SpeechSynthesisClientFactory` applies its own.

**Why two kinds and not one.** The vendors price the two operations differently, and the gap is not
small: BFL lists FLUX.2 [pro] at "from $0.03" for text-to-image against "from $0.045" for image
editing. One shared image unit would under-charge every edit by a third. A price row prices one unit
of one capability, so the two operations meter two units.

**Three provider kinds serve images, and the pair that was impossible.** `MiniMax` (the
`POST /v1/image_generation` endpoint, model `image-01`), `OpenAICompatible` (the OpenAI Images shape,
`POST {base}/images/generations` and `POST {base}/images/edits`) and `BlackForestLabs` (the FLUX
endpoints, `POST https://api.bfl.ai/v1/{model}`, asynchronous submit-then-poll with an `x-key`
header). MiniMax is the case worth naming: its admin rule used to read "MiniMax providers can only
serve the VideoGeneration capability", so a MiniMax IMAGE row could not be created at all even though
MiniMax publishes an image endpoint on the same host under the same Bearer credential. That rule is
now one negative test against the three capabilities the kind does serve, which is the shape this
file's other self-hosted kinds use.

**Image prices are published figures.**
`ModelPriceCatalog` carries an entry per model with the vendor's own list price and, in its `Basis`,
where that price was read: MiniMax `image-01` at **$0.0035 per image** from
`platform.minimax.io/docs/pricing/overview` (section `#models/image`), and BFL's FLUX.2 and FLUX.1
figures from `docs.bfl.ml/quick_start/pricing`, quoted there as "from" because FLUX.2 is
megapixel-priced and a per-image price cannot express a resolution.

**There is no image wildcard any more.** The old rate-card seed priced an uncatalogued image model
with a wildcard: the highest published per-image price in the catalog for that unit. On an upgraded
install that figure survives only where the one-time price port copied it onto a capability. A
capability created since then on a model the price book does not price gets no price and shows up as
a coverage gap.

**OpenAI is a named gap, not a guess.** OpenAI bills its image models per 1M tokens rather than per
image, and neither image usage kind can express a token price. Its models are therefore listed in
`ModelPriceCatalog` with an empty price map and a `Basis` that says why, so the price book pre-fills
nothing for them and an admin prices them by hand.

**The coverage test is what enforces the capability table.** `PriceCoverageTests` fails when
`ProviderCapabilityUsage.ServableByKind` and `InferenceProvidersController.IsUnsupportedCombination`
disagree for any `(kind, capability)` pair.
That last assertion is what caught a real hole during this work rather than a reviewer: `BlackForestLabs`
is a new kind, and Chat, Vision, Transcription and Decision have no per-kind rule of their own because
that method falls through to ALLOW — so a BlackForestLabs CHAT row was accepted at the API boundary
while the capability table said the kind serves images only.
## Segmentation and depth: what fal.ai costs

**Two more capabilities, two more usage kinds, one provider kind.** `UsageKind.SegmentationImages`
counts an image returned by `InferenceProviderCapability.Segmentation` (a subject cut-out or a matte)
and `UsageKind.DepthImages` one returned by `InferenceProviderCapability.Depth` (a depth map). Both
are served by exactly one kind today, `InferenceProviderKind.FalAi`, which serves those two
capabilities and nothing else.

**The endpoints were read, not guessed.** Every one is behind fal's single queue contract
(`POST https://queue.fal.run/{model-id}`, `Authorization: Key <FAL_KEY>`, then poll the returned
`status_url` and fetch the returned `response_url`), and each endpoint's own request and response
schema was fetched from
`fal.ai/api/openapi/queue/openapi.json?endpoint_id={id}`: `fal-ai/evf-sam` (segments from a text
prompt, `prompt` required), `fal-ai/sam2/image` (promptable, box or point prompts, no text prompt),
`fal-ai/birefnet/v2` (background removal and matting, with a `model` enum whose `Matting` member is
tuned for soft alpha edges, and the only one of the three with a separate `mask_image` output),
`fal-ai/imageutils/depth` and `fal-ai/imageutils/marigold-depth`. The client refuses an endpoint it
has no schema for, because a field an endpoint does not define is silently ignored by the vendor — the
caller would get a plausible result for a request nobody made.

**There is no per-output price to cite.** None of those five endpoints appears in fal's per-unit price
table. What fal does publish, on `fal.ai/pricing`, is its GPU list prices, and its Model APIs pricing
page states that a model with no fixed per-output price "fall[s] back to per-second billing based on
the GPU machine type used to run the request". The old rate-card seed turned that into a wildcard:
fal's **highest** published hourly rate divided by 3600, times a stated **4 seconds per output**. An
upgraded install keeps that figure as ported price rows whose notes say which half is published and
which half is the assumption. A fresh install pre-fills nothing for these endpoints.

**The catalogue names all five endpoints as unpriced, with the reason.** That is the honest state, not
an omission: `ModelPriceCatalog` is for published list prices, and there is no per-image list price
here to record. A capability on one of them is a coverage gap until an admin prices it.

## Prompted video segmentation: 16-frame units, a content cache and a plan feature

`InferenceProviderCapability.VideoSegmentation` is the seventh fal.ai capability and the only one
whose billable unit is a **frame of video input** (D-01). It is served by
`IVideoSegmentationClient`; the shipping endpoint is `fal-ai/sam-3-1/video-rle`, one call per source
clip and prompt.

**The unit arithmetic is the vendor's, not an average.** The model page states, verbatim, "Your
request will cost $0.01 per 16 frames of video input" (read 2026-10-18), so a call costs

```text
units = ceil(frames sent / 16)
USD   = units × $0.01
```

A row records the **frames sent** as its quantity and is priced per 16-frame unit, rounding up; the
card's basis quotes the vendor's sentence and its read date, the way every other `ModelPriceCatalog`
row does. A 5-second clip at 24 fps is 120 frames, 8 units, $0.08; a 25 s clip at 24 fps is 600
frames, 38 units, $0.38; the 60-second per-target cap at 30 fps is 1,800 frames, 113 units, $1.13. The
three real calls the fixture pack replays cost $0.08 (120 frames), $0.11 (158 frames) and $0.08 (120
frames) at this arithmetic. Because a call is per clip **and** prompt, three prompts on one clip is
three calls.

**The masks are cached by content, and a hit is free.** The key is a SHA-256 over the source clip's
own content hash, the sanitized prompt, the model, `detection_threshold`, `max_num_objects`, the
point/box seed and the resolved provider fingerprint. A hit downloads the stored mask and makes **no
provider call and no `VideoSegmentationFrames` row** — the run really is free, not merely
unmetered — and a test asserts the second identical run issues no HTTP request. Changing the prompt,
the threshold or the provider is a new key, hence a new call; changing anything that does not affect
the mask is not.

**The hold is the worst case at the customer's own cap, and the displayed band is the same
figure.** `SegmentationCreditPricing.AddOnMillis` prices `ceil(capSeconds × 30 / 16)` frame-units
per segmentation target, at the catalogued per-frame price, rounded **up** to whole millicredits —
30 fps is the assumed ceiling and `capSeconds` is the step's own `MaxSegmentationSeconds`, so the
hold is what a run at the cap *could* buy rather than what this clip will. `RunCreditHolds` (via
`ColdStartHoldPlan`) adds it after the history/default ladder, so it is part of the floor a cold step
may never drop below, and an addition only. The **displayed** band adds the very same call —
`WorkflowCostEstimator.SegmentationAddOn` adds it to the low, typical and high it shows — so the
shown estimate and the reserved figure agree about the segmentation portion instead of the hold
quietly being the larger of the two (B7, "estimate matches reservation"; T-001d closed a shown-vs-held
gap that T-001c had measured at 113× before this add-on reached the display).

**The hold is a ceiling, and a clip past it is refused rather than settled past it.** The vendor
bills **every** frame at the clip's real rate, while the hold prices 30 fps; a 60 or 120 fps source
would therefore spend two to four times the reservation and settle the organization negative. Before
the presign and before any provider call, the analyze step measures the source itself —
`frames = ceil(duration × probed fps)` — and compares it with `SegmentationCreditPricing.PricedFramesFor(cap)`
(`FrameUnitsFor(cap) × 16`), refusing when the clip's own frame count is the larger:

> This clip is {frames} frames at {fps} fps, but one segmentation pass holds credit for at most
> {pricedFrames} frames: the {cap}s cap prices 30 fps, and the vendor bills every frame at the clip's
> own rate. Lower maxSegmentationSeconds or trim the clip (a lower frame rate or a shorter clip); the
> clip was not truncated.

`SEGMENTATION_FRAME_BUDGET_EXCEEDED` is `UserInput` (T-014): nothing was bought, the clip is not
truncated, and the fix is the customer's — a shorter clip, or the same clip at a lower frame rate. The
cap **sizes** the reservation (`ceil(cap × 30 / 16)` blocks), so lowering it shrinks the cover and
cannot make a long high-frame-rate clip fit; the message's own suggestion to lower
`maxSegmentationSeconds` is the one clause of it that does not help, and is worth correcting before
it ships. It is the second `SEGMENTATION_*` code in that class; the provider, HTTP and decode
failures stay with the platform.

**Refunds follow the house rules.** A refund follows `FailureClass` as everywhere else:
`EGRESS_REFUSED` (consent off) is `UserInput` and keeps the consumption it never spent;
`SEGMENTATION_PROVIDER_ERROR` is `ByoProviderError` on the organization's own key (kept) and
`PlatformProviderError` on the platform's (refunded); `SEGMENTATION_BAD_RESPONSE` and every
compile-side verification failure (`EFFECT_TARGET_UNVERIFIED`, `EFFECT_TARGET_AMBIGUOUS`,
`SEGMENTATION_MASK_UNVERIFIED`) is `PlatformInternal` and refunded. The structural `EFFECT_*`
codes are `UserInput`.

**Plan feature: `segmentation`.** The entitlement set carries `SegmentationAllowed`, the catalog
carries `entitlements.segmentationAllowed`, and `PlanGate.CheckSegmentation` refuses with
`402 PLAN_NOT_ENTITLED`, feature `segmentation`, before any provider call:

> The {tier} plan does not include text-prompted object segmentation (an object followed through the
> video, so an effect can be applied to just that part of the picture). Upgrade your plan to use it.

The shipped values are the approved ones (option A, 2026-10-18, recorded as D-09):

| Plan | `segmentationAllowed` | Per-target cap |
|---|---|---|
| trial | `false` | 60 s default (`maxSegmentationSeconds`), raisable to 600 |
| creator | `true` | 60 s default, raisable to 600 |
| pro | `true` | 60 s default, raisable to 600 |
| team | `true` | 60 s default, raisable to 600 |
| enterprise | `true` | 60 s default, raisable to 600 |

The cap is a step setting (`MaxSegmentationSeconds`, 1 to 600) rather than a plan limit, so raising
it is the customer's choice and shows up on the invoice; the plan flag only decides whether the
capability runs at all. With `Billing:Enabled=false` every organization gets the unlimited set, so
a self-hosted install is not gated.

## Audio cleanup: the AudioCleanup step

`StepType.AudioCleanup` cleans the audio of one audio file or video. Its config column is
`workflow_steps.audio_cleanup_config_json` (the program lead generates the migration after the final rebase).

**Local targets are free.** `VoiceBasic`, `Hum`, `Wind`, `Hiss` and `Echo` run ffmpeg on this host: no hold, no usage row,
no plan feature, the run counts as `Free` for the run rate limit (`RunRateClassifier` reads the target from the config; a
hosted, missing or unreadable config is `Paid`). `StepCreditDefaults.IsComputeOnly` answers true for a local target, so the
cold-start row, the usage profile and the engine's hold are all `0`.

**Hosted targets are metered per started audio minute** (`UsageKind.AudioCleanupMinutes`, `IsolateVoice` and `RemoveMusic`):

| Where | What |
|---|---|
| Plan feature | `audio_cleanup`: `OrganizationEntitlementSet.AudioCleanupAllowed`, catalog key `audioCleanupAllowed` (trial `false`; creator, pro, team and enterprise `true`; the API's `plans.catalog.json` and the site's copy are byte-identical, a CI diff). `PlanGate.CheckAudioCleanup` refuses with `402 PLAN_NOT_ENTITLED`, feature `audio_cleanup`, `FailureClass.EntitlementDenied`. It is a config-driven gate like segmentation, not a step-type gate, so a local target works on Trial. |
| Hold | Taken by the engine before the step, like every step's, from the price book: `AudioCleanupCreditPricing.TryStepHoldMillisAsync` = the effective cap (the step's `MaxSourceMinutes`, default and ceiling 30 minutes) at the resolved provider's `AudioCleanupMinutes` price at the usual markup. `0` for a local target; `null` (unpriced) falls back to the cold-start row `StepCreditDefaults` (the 30-minute cap at the default endpoint's price) and the step then refuses with `CONFIG_INVALID`. The cold row is never re-planned up (`ColdStartHoldPlan.IsCold` excludes it). |
| Settle | The step records the PROBED started minutes (`BilledMinutes`) through `IUsageRecorder`, idempotency key `audiocleanup:{execution}:{step}`, only for a result that passed the 50 ms length check. The engine settles the step at what was metered, so a shorter source settles down from the cap-sized hold to its metered minutes; the vendor's own billed figure never moves the charge. |
| Refusal order | plan feature, `EGRESS_REFUSED` (consent off), `NO_AUDIO_ISOLATION_PROVIDER`, `AUDIO_SOURCE_TOO_LONG` (over `min(step MaxSourceMinutes, Cleanup:Hosted:MaxSourceMinutes)`), unpriced model (`CONFIG_INVALID`). Nothing is uploaded before all of them pass. |
| Failure classes | `EGRESS_REFUSED`, `NO_AUDIO_ISOLATION_PROVIDER`, `AUDIO_SOURCE_TOO_LONG`, `AUDIO_SOURCE_NO_AUDIO`, `AUDIO_OUTPUT_KIND_UNSUPPORTED` are `UserInput`; `AUDIO_EGRESS_UNAVAILABLE`, `AUDIO_CLEANUP_UPLOAD_FAILED`, `AUDIO_CLEANUP_FAILED` and the local `AUDIO_CLEANUP_*`/`RNNOISE_*` codes are `PlatformInternal`; `AUDIO_DURATION_MISMATCH` (output not within 50 ms of the source) and `AUDIO_VENDOR_RETRYABLE`/`AUDIO_VENDOR_FAILED` are `PlatformProviderError` (all refunded); a vendor failure on the organization's own row is `ByoProviderError`. |
| Cache | Cacheable (deterministic): the key carries the config (target, source, provider id, consent), the source's content (project-file fingerprint, upstream digest or StepResult sources) and the provider fingerprint (`DeterministicStepRevisions` tag `2026-11-02.audio-cleanup-1`). A refusal is a failure and failures are never cached. The step is never retried (`ResolveMaxRetries` = 0), so a hosted retry cannot pay twice. |

A hosted request uploads the source's audio to `projects/{p}/agentFiles/audio-cleanup/{execution}/`, hands the vendor a
presigned GET valid for 10 minutes, and deletes the object in a `finally`. No URL, token or storage key is logged or
returned in the step output.

## fal.ai images: SAM 3.1, the image endpoints, and how a tier becomes a price

**`FalAi` serves `Segmentation`, `Depth` and every picture capability** - `ImageGeneration`, `ImageEdit`
and the six per-tool ones (`ImageErase`, `ImageFill`, `ImageExpand`, `ImageBackgroundReplace`,
`ImageRelight`, `ImageUpscale`) - and nothing else (`InferenceProvidersController.IsUnsupportedCombination` and `ProviderCapabilityUsage` agree for
every pair, enforced by `PriceCoverageTests`). The image half is `FalAiImageClient`, which shares the
whole queue contract with the segmentation client (`FalQueueClient`) and chooses its endpoint **per call**
from `FalAiImageClient.Catalog`: a `FalAi` row sends its own model when that is a catalogued endpoint for the
job, and an empty model otherwise, which the client reads as the catalogue's default for the job. The meter
records the endpoint that RAN (`GeneratedImageSet.ResolvedModel`), so the price that applies is that endpoint's
(see the lookup order in "Model-capability prices and rate multipliers").
The editor's calls are attributed to the project's organization and user under `UsageSource.Editor`
(appended); before, they were recorded `Unattributed` against the platform organization. Every catalogued endpoint has a priced `ModelPriceCatalog` entry
(`ModelPriceCatalog.FalImages.cs`), every entry cites the model page and the date it was read, and
`FalAiImageCatalogTests` fails for a callable endpoint (or a tier of one) with no price-book entry. The
one-click picture-tools setup (`POST /api/v1/inference-providers/{id}/image-tools`) adds one model per
job under the same fal.ai provider, and each new platform capability is pre-filled from these entries.

**Segmentation is billed per request.** `fal-ai/sam-3-1/image` is "$0.01 per request" and `fal-ai/sam-3/image`
"$0.005 per request". A call that returns up to 32 masks is therefore ONE `SegmentationImages` unit:
`MeteringImageAnalysisClient` records one unit per `SegmentAsync` call, not per mask. The unit is billed
under the endpoint that ran (`SegmentationResult.ResolvedModel`), never under the row's empty model.

**Three rules turn fal's price shapes into price-book entries:**

| fal's shape | How the price is written | Examples |
|---|---|---|
| A price that is a pure function of ONE enum parameter | One entry per value, keyed `{endpoint}#{value}` in the book and pre-filled as a price row with variant `{value}`. `FalAiImageClient` implements `IBillingModelKeyed`, and `MeteringImageClient` splits that key into the model and the variant, so the tier that ran is the tier charged. The key falls back to the schema default when the caller sent none. | `ideogram/v4.5#low\|medium\|high` ($0.03/$0.06/$0.22), `ideogram/v4.5/edit#very_low..high`, `fal-ai/object-removal/mask#low_quality..best_quality` ($0.006-$0.024), `google/nano-banana-2.1(/edit)#1K\|2K\|4K`, `openai/gpt-image-2.5/*/edit#low..max`, `fal-ai/ideogram/v3/generate-transparent#TURBO\|BALANCED\|QUALITY` |
| A price per megapixel (or per started block of megapixels) | The vendor's figure for the FIRST unit, labelled "from" - the same convention as the BFL FLUX.2 rows. A larger image costs more than the row; the basis says so. | `flux-pro/v1/fill` $0.05, `flux-pro/v1/erase` $0.042 (= $0.03 + the 3 MP reference minimum), `flux-2-pro/outpaint` $0.03, `iclight-v2` $0.1, `seedvr/upscale/image` $0.001, `topaz/upscale/image/generative` $0.08 per 8 MP, `qwen-image-edit-2511` $0.03 |
| A token price | The vendor's own canonical-size table for the default configuration, stated as a floor (thinking tokens bill extra). | `google/nano-banana-2.1`, `openai/gpt-image-2.5/{sunburst,flare}/edit` |

Where a vendor's own figures disagree (Seedream 5 Pro: the page sentence says $0.03375 under 1536x1536, its
gallery listing says $0.0675), the higher one is the price. Where the schema's default is a dearer tier than the
entry prices (Seedream's `auto_2K`), the client sends the priced tier by default (`SendByDefault`) and forwards
only that tier; the 4K / 2K tiers of FLUX.3 and `quality: auto` of GPT Image 2.5 have no published price and
are not forwarded. **An endpoint that renders "$0 per compute second" (`fal-ai/lama`, `fal-ai/inpaint`,
`fal-ai/imageutils/rembg`, ...) is unpriced, not free, and is never a default.**

**A tier is a price variant, not a schema change.** The ledger row carries the endpoint in `model_name`
and the tier in `variant` (`ideogram/v4.5` and `high`), and the lookup picks the exact variant, else
the row with no variant, else the dearest variant. A client that prices by model alone does not
implement `IBillingModelKeyed`.

## The credit ledger: grants, holds and refunds

`ReelBolt.Shared/Billing/CreditLedger.cs` is the money. It is one class over four tables, and every
operation follows the same shape: one transaction, a per-organization
`pg_advisory_xact_lock(hashtext(org_id))`, an immutable double-entry `credit_ledger_entries` row, and
the `credit_accounts` projection updated in that same transaction — so the balance can never disagree
with the ledger. Every call takes an idempotency key; replaying a key returns the original entry and
changes nothing (and refuses if the key belongs to a different organization).

| Table | What it holds |
|---|---|
| `credit_accounts` | One row per organization: `balance_millis`, `reserved_millis` (held by open reservations), and a `row_version` optimistic-concurrency token. Spendable now is `balance - reserved`. |
| `credit_grants` | Buckets of credits with their own expiry and source (`Trial`, `Plan`, `TopUp`, `Promo`). |
| `credit_ledger_entries` | The append-only journal. Kinds: `Grant`, `TopUp`, `PlanRefill`, `Reserve`, `Settle`, `Release`, `Refund`, `Adjust`, `Expire`. Each entry carries a balance delta **and** a reserved delta, the balance after it, an optional `ref_entry_id` (a settle or release points at its reserve, a refund at its settle) and, on a settle, an `allocations_json` array naming the grants it drew from. |
| `trial_claims` | One free trial per normalized email (SHA-256 only, so deleting an account does not let the address claim again). |

The invariant the ledger maintains is `sum(grant.remaining) == max(balance, 0)`, and
`IsConsistentAsync` is the audit check for it (plus "the balance equals the sum of the entries'
deltas"). Credits are consumed from the buckets in a fixed order: **non-top-up before top-up, then
earliest expiry first, never-expiring last, then oldest** — so plan credits burn before bought ones,
and a top-up is the last thing to go.

The five operations:

- **`GrantAsync`** adds a bucket, and absorbs a negative balance first (credits arriving over a debt
  repay it). The trial grant, the monthly plan refill and a top-up all use it.
- **`ReserveAsync`** holds an amount before work starts, refusing with `INSUFFICIENT_CREDITS` unless
  `available - amount >= minAvailableAfterMillis`. Expired grants are swept first, so they can never
  back a hold.
- **`SettleAsync`** closes a reservation: it drops the hold **and** charges the actual amount, which
  may exceed the hold. The overage is charged and the balance may go negative; the floor policy lives
  in the caller (the gate), not the ledger.
- **`ReleaseAsync`** drops a hold without charging — a step that consumed nothing.
- **`RefundAsync`** returns up to the settled amount, cumulatively across several refunds of the same
  settlement (`REFUND_EXCEEDS_SETTLED` otherwise). It refunds the overdrawn part first, then the
  grants the settlement drew from, newest draw first; a grant that has since expired is replaced by a
  new `Promo` bucket living 30 days (`RefundGrace`), so a refund is never swallowed by an expiry.

`AdjustAsync` is the manual correction (positive adds a never-expiring promo bucket, negative consumes
like a settlement), and `ExpireAsync` zeroes every expired bucket, one `Expire` entry per grant, keyed
by the grant so a rerun is a no-op.

`GetBalanceAsync` (every balance a user or a gate reads: the preflight 402, the billing summary,
`GetCreditBalance`, the editor and picture estimates) subtracts, read-only, the remaining amount of
grants whose `expires_at` has passed but that no reserve has swept yet, so it agrees with what
`ReserveAsync` will see. It writes nothing and takes no lock; the `Expire` entry is still written
only by the sweep inside reserve (or `ExpireAsync`), once per grant. There is no periodic sweeper:
reads already show the post-expiry figure, so a sweep would only move when the audit row is written.

## Promo grants: codes, admin grants and pack bonuses

Promotions add credits through the same `GrantAsync`, as `CreditGrantSource.Promo` buckets with an
expiry. A promo bucket is a non-top-up bucket, so it is spent before bought credits, earliest
expiry first. Three paths write one (saas-promotions; the rules and endpoints are in
[docs/billing.md](billing.md#promotions-and-discounts)):

| Path | Amount and expiry | Ledger key | Record row |
|---|---|---|---|
| A workspace redeems a credit code (`POST /api/v1/billing/promotions/redeem`) | The promotion's credits, expiring `creditExpiryDays` (default 90) after the redeem | `promo:{promotionId}:{orgId}:{n}`, `n` = the org's prior redemptions + 1 | `promotion_redemptions` (`CreditCodeRedeemed`) |
| A platform admin grants credits (`POST /api/v1/billing/admin/orgs/{orgId}/credit-grants`) | 1 to 1,000,000 credits, expiring at the chosen date (1 day to 3 years ahead), with a required reason in the entry's note | `admin-grant:{requestId}` | `admin_credit_grants` |
| A top-up checkout carried a pack bonus | `pack × bonusPercent / 100` credits, expiring 12 months later | `topup-bonus:{provider}:{transactionId}` | `promotion_redemptions` (`BonusGranted`) |

- **Exactly once.** Each key makes a replay a no-op. Two concurrent redeems by one organization
  derive the same `n`, so one grants and the other is refused. A replayed admin `requestId`
  returns the original row.
- **Verified email only.** A credit code redeems, and a checkout carries a promotion, only for an
  account whose email is verified; otherwise a code is `403 EMAIL_NOT_VERIFIED`.
- **Honoured after the promotion ends.** The pack bonus comes from the `promotion_checkouts`
  reservation the server stored when it created the checkout, never from the checkout's custom
  data, so a bonus shown at checkout is granted even if the promotion was paused or ended since.
  Past a limit, the bonus is granted (flagged `overshoot`) only for a live reservation that was
  within every limit when created, once per user and per organization, and at most
  `min(5, max(1, MaxRedemptions / 100))` times per promotion. A payment with no stored reservation
  gets no bonus. See [Paying](billing.md#paying).
- **Refused bonuses are recorded.** A bonus that is not granted writes no ledger entry, but a
  `promotion_redemptions` row with `outcome = NotGranted` and its reason, which never counts against
  a limit. See [Refused uses](billing.md#refused-uses).
- **Refund clawback.** A full refund of a top-up claws back the pack credits
  (`clawback:{provider}:{transactionId}`) **and** the bonus, as a negative `AdjustAsync` of the bonus
  entry's delta keyed `clawback-bonus:{provider}:{transactionId}`. Both keys name the transaction,
  so a refund and a later chargeback of one payment claw back once. An upgrade payment is clawed back
  once through its pending-upgrade row's claw-back marker instead. The balance may go negative if
  the credits were already spent.
- **What the customer sees.** `GET /api/v1/billing/summary` lists `credits.grants`: the buckets with
  credits left and not expired, each with `source`, `remaining` and `expiresAt`. The billing page
  shows them as "Credits by expiry".

## Promo discounts across a plan change

A plan discount whose duration outlives one payment (`months` or `forever`) is attached to the
provider subscription, so a plan change has to decide whether it stays (saas-promotions).
The plan-change endpoints and their Paddle calls are in
[docs/billing.md](billing.md#cancel-and-plan-changes); the promotion model is in
[Promotions and discounts](billing.md#promotions-and-discounts).

- **The rule.** The decision reads Paddle's live discount on the subscription. The discount stays
  when the new plan's price id is in the promotion's target price ids, and is removed otherwise. It
  is the same ordinal coverage rule checkout applies. A promotion created with an empty plan list
  targets the price ids of the plans on sale when it was created, so a plan added later is not
  covered and a change to it removes the discount.
- **Recurring promotions only.** The decision acts only on a promotion whose duration recurs
  (`Forever` or `Months`); a `Once` discount is left alone. A seat change on the same plan never
  touches the discount.
- **Removed in the same change.** The removal is sent as `discount: null` inside the same Paddle
  subscription `PATCH` that changes the plan, under the organization's billing change claim, so the
  plan change and the removal succeed or fail together. The customer's confirm dialog names the
  ending promo before they confirm.
- **Recorded on the redemption.** The promotion's `DiscountApplied` row in `promotion_redemptions`,
  looked up within the subscription's organization, gets `discount_removed_at` and `discount_removed_for_price_id` (the new plan's price id). The admin
  redemptions list shows it as "Ended on plan change" with that date.
- **Only promotion discounts.** A live discount that no promotion created, or one already ended, is
  left alone.
- **Not restored.** Keeping the old plan after a downgrade that removed the discount does not put it
  back; the subscription continues at the regular price.
- **Visible state.** `GET /api/v1/billing/summary` returns `subscription.promoDiscount`, which the
  billing page uses to warn before a change. It is derived from the subscription's local
  `DiscountApplied` redemption row in the organization, not from Paddle's live discount: the latest
  row not removed on a plan change whose promotion recurs (`Forever`, or `Months` with its months not
  yet run out since the redemption). With no such row it is null.
- **Metric.** Each removal increments
  `reelbolt.billing.promotion_discount_removed_on_plan_change`.

## The reserve → settle → refund lifecycle and the refundable failure classes

The per-step gate is `ICreditGate` / `CreditGate`
(`inference/src/ReelBolt.WorkflowEngine/Services/Billing/`), a thin layer over the ledger that adds a
per-org in-process semaphore, crash-resume, and the failure-class policy. Its options bind the
`Billing` section, and its enabled flag defaults to **false**, so a self-hosted ReelBolt never holds a
credit and writes nothing.

- **`ReserveAsync` holds credits for one step.** It is idempotent per `(stepResultId, attempt)` (key
  `gate-reserve:{stepResultId:N}:{attempt}`), so a crash-resumed step gets its existing reservation
  back rather than a second hold. Before touching the ledger it checks the plan entitlement the step
  needs (`Rooms` → `RoomsAllowed`; `VideoGeneration` → `VideoGenAllowed` *and* a non-zero daily credit
  budget) and refuses with `PLAN_NOT_ENTITLED:{requires}`. A zero estimate (a BYO-only or runner step)
  short-circuits to allowed with no reservation and no writes. Any exception is a refusal
  (`GATE_UNAVAILABLE`) — a gate that cannot be evaluated must not hand out free compute.
- **The daily video limit is the user's own setting, not a platform cap.** Besides the credit hold above and the step's
  `MaxSpendCredits`, `IVideoGenerationBudgetGate` applies the owner's `user_video_spend_limits.daily_limit_credits` (null =
  none, the default) to the video credits their runs charged today (UTC; audio excluded), then the organization's plan
  daily video budget. The only platform number left is an operator runaway-spend ceiling
  (`VideoGeneration:GlobalDailyBudgetCredits`, default the equivalent of $5000). See
  [video-generation.md, User daily video limit](video-generation.md#user-daily-video-limit).
- **`SettleAsync` charges the actual** and releases the hold in one entry, keyed
  `gate-settle:{reserveId}`.
- **`ReleaseAsync` closes the hold of a failed step** and decides by `FailureClass`:
  **`PlatformProviderError`, `PlatformInternal` and anything unrecognised refund** (the switch is a
  default-refund deny-list, not an allow-list), while `UserInput`, `UserCancelled`,
  `ByoProviderError`, `InsufficientCredits` and `EntitlementDenied` keep what was actually consumed.
  If the step had already been charged and then turned out to be our fault, the release refunds the
  settlement under key `gate-refund:{reserveId}`, so a retry is never billed twice.
- **`ReleaseExecutionAsync`** closes every still-open hold of an execution by the close rule under
  [When a step settle fails](#when-a-step-settle-fails). A hold of an unfinished step is released as a
  platform failure. It is what `CreditPlatformFailureHook` calls when the execution-recovery sweeper finds an orphaned run,
  so an engine instance lost mid-run does not cost the customer credits. It runs only for an execution no live run holds: the run's own failure path, or the recovery sweeper after the lease lapsed. A retry cannot start a second run of the execution while the first holds its lease. Two rare exceptions are recorded as follow-ups. First, a live engine whose lease renewal fails for 60 seconds or more can be recovered while it still runs (S-52). Second, the recovery sweeper fails its batch first and releases holds row by row after, so a retry accepted in that gap can have a new hold released (S-62).

**`FailureClass` is the taxonomy that drives this** — eight append-only members in the same
`Enums.cs`: `None`, `UserInput`, `UserCancelled`, `ByoProviderError`, `PlatformProviderError`,
`PlatformInternal`, `InsufficientCredits`, `EntitlementDenied`. The design record's rule for the last
of these is deliberate: **an unmapped code or unknown exception is `PlatformInternal`, so it refunds** —
charging a user for a failure we cannot explain is the worse error.

**Overdraft tolerance** exists because a step's real usage cannot be predicted exactly, and failing a
multi-million-token step at 99% of its estimate would be worse than a small overdraft. The gate
reserves at the estimate but passes the ledger a negative floor: `headroom = min(estimate x 20%,
floorCredits)`, where the tolerance is `Billing:OverdraftTolerancePercent` (default 20) and the floor
is `Billing:OverdraftFloorCredits` (default 100 credits). So a step may finish up to 20% over its
reservation, never more than 100 credits over; the overage is charged, and the balance may go
negative, which blocks further reservations until the organization tops up.

## When a step settle fails

A step that completes is charged for what it metered, even when its settle fails. Three layers
close the hold, in this order. The code is in `WorkflowExecutorService` (the step retry) and
`UnsettledHoldSweeper` (the sweep), both under `inference/src/ReelBolt.WorkflowEngine/`.

**1. In-process retry.** A settle that throws or is refused is tried three times in the step, with
waits of 200 ms and then 1 s between attempts. Every attempt uses the same reservation and the key
`gate-settle:{reserveId}`, so an attempt whose answer was lost after the ledger committed replays
that entry and is never charged twice. The retries run on `CancellationToken.None`.

A refundable failure class gets one attempt only. Its hold is closed by the run-level release or by
the sweep, and the step still completes. `ReservationClosed` and `ReservationNotFound` are terminal:
nothing is retried and nothing is recorded.

**2. Record on the step.** When all three attempts fail, the step keeps the metered amount in
`workflow_step_results.credits_unsettled_millis` (a nullable `bigint`, set only in this case). The
column is owned by the workflow engine, and the Inference API maps it read-only.

**3. Close the hold.** The run-level release (`ReleaseExecutionAsync`) and the sweep use the same rule:

- **Completed step.** The hold is settled, never refunded. A recorded `credits_unsettled_millis` is used as-is,
  except that the step is still charged at least its hold when it metered unpriced usage. When nothing is recorded,
  the amount is the sum of `usage_events.credits_millis` for the step result, and never less than the hold: the hold
  is the floor there because the actual is unknown (`CreditGate.SettleOpenAsync`, the no-recorded branch).
- **Failed step with a class other than `None`.** A refundable class releases the hold, which refunds.
  Any other class settles it at the recorded amount, or at the metered usage when nothing was
  recorded. A hold with no usage at all is released.
- **Anything else** (no step row, a step still running or pending, a skipped step, or a failure with
  no class). The hold is released as `PlatformInternal`, which refunds.

The rule closes only an open hold. A hold that already has its settle or release is left as it is and
is never refunded, so a sweep that races a settle cannot give that charge back.

### The unsettled-hold sweep

`UnsettledHoldSweeper` runs on every engine replica. A pass picks the reserve entries that have no
settle or release child and were created more than `MinAgeSeconds` ago. It handles three cases:

1. **Terminal step.** The step result is `Completed`, `Failed` or `Skipped`. The hold is closed by the
   rule in step 3 above.
2. **No step result.** The execution is `Passed`, `Failed` or `Cancelled`. The hold is closed by the
   same rule.
3. **Running or pending step.** The step result still says `Running` or `Pending`. The hold is swept
   only when all three of these are true: it is older than `RunningStepCeilingSeconds`, this process
   does not track its execution, and the execution's lease has been lapsed for at least
   `MinAgeSeconds`. Its hold is released as `PlatformInternal`, logged as a warning and counted as
   `phase=orphan`. See [Running steps and the lease](#running-steps-and-the-lease).

The pass takes the oldest holds first, and closes at most `MaxPerSweep` of them. One failure does not
stop the pass. Exactly-once comes from the ledger keys and the per-organization advisory lock.

The options are in the `Billing:SettleSweep` section (environment form `Billing__SettleSweep__*`):

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Enabled` | `true` | `true` or `false` | Runs the sweep. It only runs while `Billing:Enabled` is also `true`. |
| `IntervalSeconds` | `300` | 1 to 86400 | Seconds from the start of one pass to the start of the next. The timer is fixed-rate. |
| `StartupDelaySeconds` | `60` | 1 to 3600 | Seconds after startup before the first pass. |
| `MinAgeSeconds` | `600` | 1 to 86400 | A hold younger than this is never swept, because its step may still be closing it. It is also the lease grace for case 3. |
| `MaxPerSweep` | `100` | 1 to 10000 | Most holds one pass closes. The rest wait for the next pass. |
| `RunningStepCeilingSeconds` | `172800` | 129600 to 1209600 | Age a case-3 hold must pass before it can be swept (48 h by default, 36 h to 14 d allowed). It must be longer than the longest a hold can live: every retry attempt of a step shares one hold. |

Each value except `Enabled` must be inside its range, or startup fails with the key and the range:
`Billing:SettleSweep:<Key> must be between <Min> and <Max>.` For example,
`Billing:SettleSweep:MaxPerSweep must be between 1 and 10000.`

### Running steps and the lease

A running step keeps its hold open. Its engine renews the execution's lease every
`WorkflowEngine:Lease:RenewIntervalSeconds` (default `15`), with a lease length of
`WorkflowEngine:Lease:LeaseSeconds` (default `60`). The renewal runs for every execution the process
still runs, until the step loop exits. It writes no status, so it keeps going after a stop.

A stop taken by another replica writes `Cancelled` to the execution row, and the replica that took it
cannot reach the step. The owning engine reads that row at its next renewal and cancels the step
within one renew interval (15 s by default). The step is classified `UserCancelled`, which keeps what
it consumed. The sweep does not close the hold of a running step while its lease is live, so a stopped
step is never swept early.

If the owning engine is gone, its lease stops renewing and lapses. A hold whose step still says
`Running` is then swept once it is older than `RunningStepCeilingSeconds` and its lease has been lapsed
for at least `MinAgeSeconds` (case 3 above).

**A retry is refused while the lease is live.** A `Queued` or `Running` execution gets
`400 RUN_ALREADY_ACTIVE` first. For any other status, while the lease is live,
`POST …/executions/{id}/retry` answers `409 RUN_STILL_STOPPING` and changes nothing. The re-queue is one
conditional UPDATE that also requires a lapsed lease, so a renewal that lands after the check is never
overwritten. The engine ends the lease when the run exits (`EndLeaseQuietlyAsync` in
`WorkflowExecutionRunner`), so a retry after a stop is accepted once the run has finished its current step.
An engine refuses a second delivery of an execution it already runs (`already_running_here`, acked), so the
duplicate is dropped rather than run twice.

### Resume and Retry

Resume holds and charges only the steps from the first incomplete one on (`402 INSUFFICIENT_CREDITS`
is priced the same way); reused results hold and charge nothing. Retry gets the same plan gate and
credit preflight as a new run. Results a Retry supersedes stay in the ledger (credit keys are per step
result id). An `Abandoned` video take is never charged to the user. See
[Workflow execution optimization, section 8](./workflow-execution-optimization.md#8-run-snapshots-resume-and-retry).

### Watching it

The counter `reelbolt.billing.unsettled_holds` (unit `holds`, meter `ReelBolt.WorkflowEngine`)
counts holds that were left open. Its `phase` tag says where:

- `phase=step`: a step's three attempts all failed, and the amount was recorded on the step.
- `phase=sweep`: a sweep pass could not close a hold. It is retried on the next pass.
- `phase=orphan`: the sweep released the hold of a step that still said `Running` or `Pending`, after
  its lease had lapsed. The engine warning names the reservation and the execution.

The counter should stay at zero. A non-zero `phase=step` or `phase=sweep` value means a settle keeps
failing after the in-process retry. A non-zero `phase=orphan` value means an engine stopped renewing
while a step was still marked running. In both cases, check the engine logs for the reservation before
you change any option.

## Charging policy: work outside workflows

Workflow steps are charged through the engine's credit gate above. Every other metered call opens its
own usage scope with a source, and the source decides how it is charged (decision 9; design record
[docs/design/model-catalog.md](design/model-catalog.md) §7). Nothing here applies when
`Billing:Enabled` is false: the charge path is then a no-op and everything is only recorded.

| Source | What produces it | Policy |
|---|---|---|
| `Assistant` | the assistant chat, including `LookAtPicture` | Shared monthly allowance with `Mcp`, then charged |
| `Mcp` | an assistant tool run through the MCP bridge (Claude Code) | Shared monthly allowance with `Assistant`, then charged |
| `Editor` | picture-editor AI operations and subject cut-out | Charged per operation |
| `TimelineAssist` | timeline AI-assist proposals | Charged per proposal |
| `Moderation` | content moderation on generation prompts, ingest, generated text and publish | Free: `ModerationCalls` is priced at a real `$0`, one row per call |
| `FileSummary`, `Indexing`, `DocsIndex` | file summaries, file indexing and search, the platform-docs index | Free |
| `Storage` | the daily storage sample | Free (storage is a plan quota; project-history bytes count toward it, see [project-versioning.md](project-versioning.md#quota-counting)) |
| `Workflow` | workflow steps, including designed graphics and revisions | Unchanged: the engine's credit gate |
| `Unattributed` | a call that ran outside any scope | Recorded against the platform organization, never charged |

**The assistant allowance** comes from the plan entitlements in `plans.catalog.json`:

| Plan | Included assistant credits per month |
|---|---|
| Trial | 20 (`assistantMonthlyCredits`) |
| Creator | 100 (`assistantMonthlyCredits`) |
| Pro | 300 (`assistantMonthlyCredits`) |
| Team | 100 per seat (`assistantMonthlyCreditsPerSeat` × seats, at least one) |
| Enterprise | unlimited (`assistantUnlimited: true`) |

Unlimited is **only** the explicit `assistantUnlimited` entitlement. With `Billing:Enabled=true`, a plan without
either allowance key, a missing plan row, or the `Tenancy:DefaultEntitlements` fallback includes **0**, never
unlimited (billing off is unchanged: nothing is charged). Unlimited exempts the **allowance only**: an unlimited
turn holds zero credits (Enterprise is billed outside credits) but still takes a hold row, so its steps are
pre-claimed, the per-turn ceiling stops it and it counts against the in-flight cap.

The period is the entitling (`Trialing`, `Active` or `PastDue`) subscription's current period when it
contains now, otherwise the UTC calendar month. What the charge path counts against the allowance is the
authoritative counter in `assistant_allowance_holds` (one row per assistant turn or MCP tool call: its open
hold, then what it consumed), read and written only under the organization's ledger lock, never the
asynchronously flushed `usage_events`. `GET /api/v1/billing/summary` returns the allowance as
`assistantAllowance {includedCredits, usedCredits, periodStart, periodEnd}` (`includedCredits` null means
unlimited; `usedCredits` is that same counter over that same period, open holds plus consumption,
`AssistantAllowanceService.AllowanceInUseAsync`, the one definition the gate reads too, so a workspace the gate
refuses sees its allowance used up; it is reported on an unlimited plan as well), and `GET
/api/v1/billing/usage?groupBy=source` splits spend by source.

**How each policy charges** (`NonWorkflowChargeService`, Inference API). It uses `CreditLedger` with its
own idempotency keys (`nwc-reserve:{source}:{requestId}`, `nwc-settle:{reservationId}`,
`nwc-release:{reservationId}`), the same overdraft headroom as the credit gate
(`min(estimate × Billing:OverdraftTolerancePercent, Billing:OverdraftFloorCredits)`, 20 % and 100 credits
by default), and fails closed: a ledger it cannot read refuses (`GATE_UNAVAILABLE`: a `503` on the editor
and timeline routes, a plain "try again" message in the chat and over MCP).

- **Hold, then settle** (`Editor`, `TimelineAssist`). Before the vendor call it reserves an estimate:
  one unit of the operation's unit at its price (10 credits when the platform has no price for it), or
  for a timeline proposal about one input token per four characters of its brief and of the timeline view
  sent to the model plus its 2000 output tokens at the output price, priced on the agent's resolved chat
  model, and never less than
  `Billing:NonWorkflow:TimelineProposalHoldCredits` (default 5). The proposal resolves its chat provider
  once: the hold is sized on that row and the model is called on that same row. A proposal whose agent
  resolves to a chat provider the workspace pays for itself (BYO or Runner) costs no credits and takes no
  hold, like a BYO editor operation; if a proposal that took no hold, or a hold that reserved nothing
  (`NO_PLATFORM_COST`), still meters Platform usage, it is charged after the call under its request id. On a Platform picture row, a requested `model` must be a
  configured capability of that job with every unit priced, else `400` with a plain message before any hold
  or vendor call (a BYO row passes any model through); a subject cut-out applies the same rule to a requested segmentation `model`, and a default the tool substitutes for the row's own model (removeBackground's unpriced `fal-ai/birefnet/v2` on a fal.ai row) is refused on a Platform row with billing on as `409 MODEL_UNPRICED` before any hold, never billed at the dearest row (billing off and BYO unchanged). A proposal brief is at most 2000 characters (`400` with a plain message beyond
  that). When the balance cannot cover the hold, the call
  is refused **before** the vendor is called with `402 {code: "INSUFFICIENT_CREDITS", message, required,
  balance}`, and the message reads "This {picture edit | subject cut-out | suggestion} needs about N
  credits and the workspace has M available. …"; any other refusal is `503 GATE_UNAVAILABLE`. After the
  call the hold settles at the scope's metered credits, **capped at the hold plus its overdraft headroom**
  (anything above is not charged, is logged as a warning with the organization, source and request id,
  and counts on the `reelbolt.billing.nonworkflow_settle_capped` metric; a size-priced picture edit is the exception, see below), or is released when the call
  failed. A settle or release that fails is retried once (idempotent by key); a second failure records the metered usage on the hold's row in `assistant_allowance_holds` (key `nwc-hold:{reservationId}`, no allowance, never counted as a turn), is logged at error and counts on `reelbolt.billing.nonworkflow_hold_close_failed`, and the organization's next charged call sweeps any editor-operation, subject or proposal hold still open `StaleHoldAge` (the 90 s operation bound plus a 15 minute margin) after it was taken: recorded usage is charged exactly (capped like its settle), nothing recorded is released, under the key `nwc-swept:{reservationId}`, and a late close of a swept hold is charged only the rest, once, through `nwc-late:{rowId}`.
  **Size-priced picture edits** (a fal.ai or BFL endpoint billed per megapixel of the picture it delivers; `EditImageOperationsController`, `OutputSizePricing`). A megapixel is **1,048,576 pixels** (1024 x 1024, `OutputSizePricing.PixelsPerMegapixel`), rounded up, in the hold and the meter alike: 1024 x 1024 is exactly 1 MP, 1025 x 1024 is 2 MP, 2048 x 2048 is exactly 4 MP. The hold is priced on the `{n}MP` variant of the size the editor expects:
  - When the expected size is unknown, the hold uses the endpoint's documented maximum output (for an upscale, its maximum input scaled by the factor). With no documented maximum it uses the variant-less row, which is the largest. An unknown size no longer refuses the call. A `422` remains only when the size of a reference picture cannot be read (the `fal-ai/qwen-image-edit-2511` case), because that tool is priced by the size of every picture it works from.
  - `fal-ai/flux-2-pro/outpaint` bills input and output megapixels, so its variant is `max(input MP, output MP)`, and at least 2 when the output is 1 MP or less (the 1 MP row prices no input).
  - A target-mode upscale scales by at least 1 when the hold is sized, so a source already above the target never shrinks the hold.
  - The meter reads the delivered size from the picture, then clamps it to the model's documented maximum output; without one, to 4 times the expected size (`ExpectedSizeClampFactor`); without that, to a per-endpoint fallback (`OutputSizePricing.FallbackMaxOutputPixels`). A clamp logs a warning.
  - Once the vendor has answered, a measured, priced job settles at the **metered** amount, not capped at the hold plus headroom (the hold is only the preflight, so the charge can be below it). The settle is floored at the hold only when metering failed after the vendor answered: nothing was metered, or, for a size-priced job, the delivered picture's size could not be measured. The user keeps the picture they paid for; a settle above the cap is logged as a warning.
  - Linear per-megapixel endpoints (`LinearPerMegapixel` on the fal.ai catalog entry: `fal-ai/flux-pro/v1/fill`, `fal-ai/iclight-v2`, `fal-ai/seedvr/upscale/image`, `fal-ai/qwen-image-edit-2511`) price a `{n}MP` size with no row of its own as n times the per-megapixel rate, not the next seeded size up. For fill at $0.05 per megapixel, 1000 x 1000 is 1 MP, $0.05; 1920 x 1080 (1.98 MP) rounds up to 2 MP, $0.10. Erase and outpaint keep the next-row-up rule; outpaint's variant is `max(input MP, output MP)`, at least 2.
- **Hold, cut off, then settle at the actual** (`Assistant`, `Mcp`). Each chat turn and each MCP tool call
  first holds `Billing:NonWorkflow:AssistantTurnHoldCredits` (default 5), or less when the allowance left
  plus the available balance is less but still covers the **minimum turn**
  `Billing:NonWorkflow:AssistantMinTurnCredits` (default = the hold): from the allowance left, then a credit
  reservation with no overdraft. Below the minimum the call is refused (`INSUFFICIENT_CREDITS`); a partial
  hold is never run in full. An allowance-only workspace at a zero balance runs while its allowance left
  covers the minimum. The hold is decided in one transaction under the organization's ledger advisory lock
  (`pg_advisory_xact_lock(hashtext(org_id))`, the lock `CreditLedger` takes) against the
  `assistant_allowance_holds` counter, so concurrent calls are served one after another.
  - **In-flight cap.** At most `Billing:NonWorkflow:MaxConcurrentTurnsPerOrg` (default 4) open holds per
    organization (counted under the same lock); the next is refused with **429 `TURN_CONCURRENCY_LIMIT`** and
    a plain message, on chat and MCP. A chat turn on the organization's own provider (BYO) holds like any
    other but is not refused by the cap **until its first Platform-billed tool call** (vision `LookAtPicture`,
    the picture AI tools, a designed graphic, the docs search, a provider test: `AssistantPlatformTools`),
    which is admitted under the cap and refused there (`TURN_CONCURRENCY_LIMIT`, plain reply) when the
    organization is full. A pure BYO turn stays free and uncapped; billing off charges nothing.
  - **Pre-claimed steps.** Every model round-trip and every tool call of a chat turn, and an MCP call, is
    **claimed at its worst case before it runs** (`AssistantTurnGuard` → `ClaimStepAsync`, under the lock): a
    round-trip at the estimated input tokens of the exact request about to be sent (characters / 4 × 1.5,
    1.6k per image) plus its output ceiling, at the chat model's price (at `UnpricedEstimateCredits` when the
    chat model could not be resolved, with the input cap still applied); a tool call by its entry in
    `AssistantPlatformTools.Tools`, the one cost table every registered tool must have an entry in: a free tool at 0;
    `LookAtPicture` at its resolved vision model's quote for the question plus one input token per 750 pixels of the
    layer it sends (at least 4,000, above the largest per-image charge of the priced vision vendors; the 40 MP cap
    when its size cannot be read) plus its 1,024-token output ceiling, the look then pinned to the layer it was sized
    at and refused in plain words (nothing sent) when the bytes it downloads declare more pixels than were claimed, or
    cannot be read against a known size; `ApplyRunPictureTool` at the larger of `UnpricedEstimateCredits` (10) and the
    dearest estimate over every tier in force of its model (and the variant-less request) for the most images the
    operation returns; any other Platform-billed tool, a look whose model cannot be quoted, and (fail
    closed) any tool the table does not classify, at `UnpricedEstimateCredits`. A step that metered Platform usage the platform could
    not price counts at its claim, not 0, towards the ceiling and the funds check. The step is refused when metered + claimed + this step
    passes `min(Billing:NonWorkflow:AssistantTurnCeilingCredits (default 100), allowance free + available +
    headroom - what other open turns metered and claimed beyond their holds)`, including before the first
    round-trip. The turn's in-flight total is recorded on its open hold (from its first paid step on, which marks
    the hold used), and a landed step's claim is replaced by its metered actual. `Assistant:MaxRoundTripInputTokens` (default 320,000: the replay window, 40 × 32,000
    characters / 4) refuses a larger round-trip (`TURN_LIMIT`); `Assistant:MaxToolCallsPerStep` (default 8)
    answers a model step's further tool calls with a plain refusal; `LookAtPicture` sends at most 20 MB and
    40 megapixels. A stopped chat turn ends cleanly with a plain reply and a `data-credits` part (`TURN_LIMIT`
    or `INSUFFICIENT_CREDITS`); MCP returns a plain `isError` tool result. No tool runs after the stop.
  - **Wall clock.** `Assistant:MaxTurnSeconds` (default 1800) cancels a connected chat turn (answered "This
    took too long…") and an MCP call (an `isError` result). The stale threshold below must stay at least
    twice that: a `ValidateOnStart` check on the non-workflow charge options fails the Inference API's **boot**
    otherwise (and `NonWorkflowChargeService` still throws when it is built), so a misconfigured host never
    sweeps live turns.
  - **Settle.** Under the same lock, at what the call metered: the allowance first (consumed, no credit
    debited), then the **full remainder in credits**. The bound is the pre-claim, so the balance can end below
    zero by at most `NonWorkflowChargeService.WorstCaseOvershootMillis` = the headroom
    (`min(20 % of the hold, 100 credits)`) + one step's estimation error (actual minus claim) per open turn;
    that overshoot is logged and counted on `reelbolt.billing.nonworkflow_settle_capped`.
    `UnchargedMillis` is 0 for every Platform turn. A turn whose scope saw a Platform usage that could not be
    priced settles at `max(metered + the claims of its steps that metered unpriced usage, hold)`. BYO and Runner usage meters 0, so it charges 0. A call that
    metered nothing, or never got as far as settling, releases its hold.

  With the allowance used up and the balance 0, an MCP call to a tool that `AssistantPlatformTools.Tools` classifies `Free` still runs (no hold, nothing charged), while `PlatformBilled` and unclassified names are refused as below and a free tool that queues paid work (`ExecuteWorkflow`) is refused by that work's own credit preflight; the chat turn is unchanged, as its model round-trip is the paid step.

  Refusals are plain words: the chat answers with a normal message and a `data-credits` part; MCP returns an
  `isError` tool result and logs the call (`insufficient_credits`, `turn_concurrency_limit`, `turn_stopped`,
  `turn_timeout`). With billing on, a call with no workspace is refused, never run uncharged, and a host
  without `INonWorkflowChargeService` fails its boot (a `ValidateOnStart` check). Only billing off
  (`BILLING_DISABLED`) admits a turn with no hold; the explicit unlimited entitlement (`UNLIMITED_ALLOWANCE`)
  holds a row of zero credits. Any other admitted hold-less turn is still charged its metered actual at
  settlement, keyed on its request id. A settlement that fails is retried once; then what the turn metered is
  recorded on its still-open hold (`unsettled_metered_millis`, apart from the in-flight exposure) and the failure
  is logged as an error with the organization and request id.
  A hold still open an hour (`StaleTurnHoldAge`) after it was taken is **swept** (status `Swept`) by the
  organization's next call, through the settle path (allowance first, then credits, same keys): a hold with a
  recorded actual is **charged exactly that**; one marked used (a paid step was claimed, the actual unknown,
  e.g. the record failed too) is charged its recorded exposure, at least what it held; a hold that never
  metered anything (or recorded 0) has its allowance and credit reservation freed. A hold taken in an earlier
  allowance period is charged against that period, never the current one: its own allowance hold covers what it
  can, the rest is credits (that period's full terms are not derivable once the plan changed). If a swept call was not dead after all and settles later, it is charged only what the
  sweep did not charge, as a hold of 0 (allowance left, then credits), through a ledger reservation of its own
  (`nwc-late:<hold>`); only `Swept` holds settle late, so a call that released itself is never charged again.

### Recalibrating the assistant allowance

The allowance numbers above are a starting point. **No production usage was measured when they were
set**: there was no production `usage_events` data for the assistant or MCP at ship time. Once there is,
run this against the production database to see what organizations actually use per month, and set the
allowance near the p50 of `Assistant+Mcp` (most organizations never pay for the assistant) while
checking the p90 (heavy users pay for the excess):

```sql
-- Credits per month per active organization, by source (p50 and p90).
-- "Active" = at least one platform-billed row of that source in that month.
WITH per_org AS (
    SELECT date_trunc('month', occurred_at) AS month,
           organization_id,
           source,
           sum(credits_millis) / 1000.0 AS credits
    FROM usage_events
    WHERE billing_mode = 'Platform'
      AND source <> 'Unattributed'
      AND occurred_at >= date_trunc('month', now()) - interval '6 months'
    GROUP BY 1, 2, 3
),
with_pool AS (
    SELECT month, organization_id, source, credits FROM per_org
    UNION ALL
    -- The allowance is shared, so also measure the two sources together.
    SELECT month, organization_id, 'Assistant+Mcp', sum(credits)
    FROM per_org
    WHERE source IN ('Assistant', 'Mcp')
    GROUP BY month, organization_id
)
SELECT month::date AS month,
       source,
       count(*) AS active_orgs,
       round(percentile_cont(0.5) WITHIN GROUP (ORDER BY credits)::numeric, 1) AS p50_credits,
       round(percentile_cont(0.9) WITHIN GROUP (ORDER BY credits)::numeric, 1) AS p90_credits
FROM with_pool
GROUP BY month, source
ORDER BY month, source;
```

The query uses calendar months, while the allowance follows each subscription's own period, so treat
the result as an estimate. Change the allowance in `plans.catalog.json` and copy the file to
`site/lib/pricing/plans.catalog.json` in the same change (see "The mirror and the drift check" below).

## The pre-run estimate: what is shown versus what is held

Before a run starts, `WorkflowCostEstimator` (`inference/src/ReelBolt.Inference.Api/Services/Billing/`,
served by `GET /api/v1/projects/{projectId}/workflows/{id}/estimate`) answers two different questions
with two separate code paths:

| Figure | Answers | Computed from | Used by |
|---|---|---|---|
| `RequiredMillis` (`requiredCredits`) | "Can this run start?" | Σ of the per-step **holds** planned by `RunCreditHolds.Plan`: the `StepCreditEstimator` p90 for a step with history, the larger of its old default and its share of the cold steps' low edge for a step without (see [The cold-start hold](#the-cold-start-hold)), and a `VideoGenerate` step's own price | The **402** `INSUFFICIENT_CREDITS` preflight, and the engine's per-step reservation |
| `TypicalMillis`, `LowMillis`, `HighMillis`, `FixedMaxMillis` | "What will this run usually cost?" | The **display** path below | The "≈ X credits (usually A–B)" line in the UI, the assistant's approval card, and the extra `typical` and `fixedMax` fields of a 402 body |

A `DynamicsPlanner` step of a run with no effective pace (no brief pace and no style dynamics) is skipped by its executor, so it takes no hold and is left out of `RequiredMillis`, the shown estimate and the cold-start shares of the other steps. `DynamicsPlannerPricing.Skips` is the one rule the executor, the engine's hold and the API's estimate share; the API enters the run's pace (the brief's, else the style's, for Execute, Retry and Resume; the brief's alone for the app estimate, and none for the plain `GET` estimate), and a caller that does not say holds the planner as if it runs.

**The two paths stay separate.** The display path reads its own history through separate queries, so a
failure there never moves `RequiredMillis`, and the 402 decision is `available < RequiredMillis`. The two
figures answer different questions, so they differ: a step with history holds its p90, which is usually
above its usual range, while a cold step holds the larger of its old default and its share of the low
edge. On a run where every step is cold and the display has no history either, `RequiredMillis` is at
least the displayed `LowMillis` plus the fixed-price holds, and at least what the run needed before the cold-start hold re-plan;
it is often above the shown low. With display-side history (agent-type buckets, past runs) the shown low
moves and the hold's cold low edge does not. The UI shows both:
the range as the expected cost, and `requiredCredits` as what the run "holds before it starts".

**What `LowMillis`/`HighMillis` mean.** They are the usual range (p25 to p75) of the **variable** part of
the run. Fixed-price steps are outside it and reported in `FixedMaxMillis` as an "up to" ceiling.

**The run modal warns when a run may stop partway.** Because a run can start on less than it usually
costs, the web run modal (`mayRunOutPartway` in `web/lib/utils/billing.ts`) shows "You may run out of
credits partway through this run." when the run is affordable but the available balance is below
`typical + fixedMax`. It says nothing when billing is off, the balance is unknown or there is no
typical figure.

**What a refusal says.** The required amount stays the p90 hold; only the explanation changed. Every `INSUFFICIENT_CREDITS` refusal of a run (REST execute, `POST .../assistant/execute`, the assistant and MCP `ExecuteWorkflow`, and a revision's `402` or `{error, code}` refusal) carries a `message` built by `CreditShortfallMessage.Build`, plus `missing` (credits) next to the existing `required`, `balance`, `low`, `high`, `typical` and `fixedMax`. The message reads "This run needs up to 788 credits to start (it usually costs about 286); the workspace has 500 available, so 288 more are needed. Credits that aren't used are returned when the run finishes." The parenthesis is left out when there is no history, or the typical cost is not below the hold. The amount to have is rounded up and the balance down, one decimal below 10 credits and whole credits from 10, so "needs, has, so N more" always adds up the safe way. It is never built with billing off. The assistant's approval card for `ExecuteWorkflow` and `ApplyReviseComposition` prices through the same call as the preflight (`ApprovalCardPreflightParityTests`): its cost line reads "needs up to X credits (usually about Y)" and, when the workspace cannot cover it, adds "; the workspace has M, so N more are needed" (`CreditCostWording.RunNeeds`; the revision card also gets an info line). The dashboard shows the server's `message` as it receives it.

### The cold-start hold

`RunCreditHolds.Plan` (`inference/src/ReelBolt.Shared/Metering/ColdStartHoldPlan.cs`) decides every
step's hold for a run (the cold-start hold re-plan):

- **A step with history** (`StepCreditEstimator` basis `History` or `StepTypeHistory`) holds its p90,
  exactly as before the re-plan. For a step that renders the output this is the p90 of its Auto/1080p samples,
  then adjusted to the run's resolution (see [Output resolution and credits](#output-resolution-and-credits)).
  **App-chosen agent model** (docs/design/app-inputs.md 28): when the run's app chose the Chat model of an agent the
  step runs (`AppAgentModelAmbient`), the history (metered on the agent's ordinary model) is moved by
  `Chosen.Typical / Baseline.Typical`, the step's own cold-start profile priced at the chosen model and at the ordinary one
  (a `ProfilePriceKey` with `Baseline = true`) through the same `ProfilePriceTable` both services build, rounded up, and
  never held below the chosen profile's P75 (`AppModelHoldScaling`). The shown history moves by the same ratio. A run
  without a choice asks for no baseline price, so its holds, estimate and keys are unchanged. Per-model history buckets
  were not added (they would need a sample column).
- **A `Voiceover` step whose speech model the app chose** prices its `TtsInputBytes` at that model: the bound
  `providerId` is priced only when it is one of the run's step `model` choices (`AppAgentModelAmbient.StepModels`, from
  `AppModelRules.StepModelChoices`), so an author-set provider still prices the default speech model, as before.
- **A step that is never charged** holds 0.
- **A `VideoGenerate` step** holds its declared price, priced from its own settings, else the old
  per-step fallback. It is fixed-price and never part of the cold plan.
- **Every other step is cold** (basis `Default`: no usable history). Each cold step holds
  **`max(old default, share)`**: its pre-re-plan hold (`StepCreditDefaults`' per-step low, what
  `StepCreditEstimator` answers on a cold start) or its share of the **low edge of the cold steps' usual
  range**, whichever is larger. The low edge is `RunCreditRange.Combine(...).LowMillis` over the cold
  steps' priced cold-start profiles (each agent's resolved provider), with review loops at the cold-start
  1–2 passes and ForEach modelled as on the display path.

**Calls on top of the plan.** Three kinds of call buy something a step's token history cannot see, so their worst
case is added to the step's hold after the plan, with history or without:

- Web search: the most searches a step may make, at the default WebSearch provider's price (see
  [`web-search.md`](web-search.md)).
- Looks at single moments (`LookAtMoment`): 6 looks (`MomentLookLimits.MaxLooksPerStep`), each
  `StepUsageProfiles.EventLookInputTokens` in and `EventLookMaxOutputTokens` out on the Vision model (600, the
  ceiling the describer asks the model with; the typical 300 of `EventLookOutputTokens` is what the analysis step's
  shown estimate uses). Only for an `Agent` step of `SoundDesigner`, `VideoStoryEditor` or `MotionDirector`, an
  `EditRoom` (its solo fallback is a story editor), or a `Parallel` step whose own agent or any extra agent is one of
  those three (once: its agents share the step's counter), and only when a `VideoAnalyze` step of the run describes
  events (`describeEvents` with `analyzeEvents`, and `maxDescribedEvents` not `0`): no other run keeps a picture to
  look at. Priced on the Vision model that analysis names, else the default; the dearer one when
  two analyses differ. A Vision model the organization brought itself adds nothing. The run settles by the metered
  calls, not by the hold. See [`video-editing.md`](video-editing.md) "Looking at one moment".
- Looks at events of a describing `VideoAnalyze` step (`describeEvents` with `analyzeEvents`): the most looks the
  step can take, held exactly once. That is its `maxDescribedEvents` when one is given (at most 40,
  `VisionLookDefaults.MaxEventLooks`), else the most an automatic count gives (30, `MaxAutoEventLooks`), and nothing
  when the count is `0` or describing is off. Each look is held at the same worst case as above (2,200 tokens in,
  600 out), on the Vision model the step names in `visionProviderId`, else the default one. A model the run's app
  chose (`@visionModel`) is already bound into the step when the API's preflight and the engine plan the holds, so
  both hold on the chosen model. The looks are not part of the cold-start share, so a cold step does not hold them
  twice (`StepUsageProfiles.EventLookWorstCase`, `RunCreditHolds`). A step with history is different: its p90 base
  is made of past runs, and past describing runs include the looks they took, so the worst case is then held on top
  of looks already in the base. The hold is too high there, never too low; the step is charged what it used. The SHOWN estimate of a cold step is separate (`StepUsageProfiles.EventLookProfile`): an explicit
  count is shown as exactly that count, an automatic count as 20 looks with the range 12 to 30, each at the typical
  300 tokens out, so the shown high edge is never more than the step can take; the hold is above it.

`ColdStartHoldPlan.Allocate` splits that low edge across the cold steps in proportion to each one's
typical single-run cost (a loop body counts once in the weights), rounding by largest remainder so the
shares sum to the low edge exactly (each share floored at 1 millicredit,
`ColdStartHoldPlan.MinimumHoldMillis`). The plan then takes the larger of each share and the step's old
default. So the cold steps together hold at least the low edge and at least what they held before.

**Why the max.** The re-plan was to raise the cold-start requirement to the low edge of the usual range; it
never to lower one. The per-step hold is also what bounds overdraft: the gate reserves it
before the step runs and refuses the step when the balance cannot cover it, so a share far below a
step's own cost (or a 1-millicredit hold on a zero-typical step) would let settlement overrun much
further into overdraft. A step
priced at 0 (its agent on the organization's own provider) keeps its old default, never 1 millicredit:
any compute it does is still reserved and settled. The 1-millicredit floor survives only as a defensive
minimum for a charged step whose old default is 0, which no step type has today.

**The API and the engine use the same planner.** `WorkflowCostEstimator` sums the plan into
`RequiredMillis`. The engine's `UsageHistoryCreditHoldEstimator.RunHoldMillisAsync` plans once per
execution and reserves each step's entry, so the 402 preflight and the reservations cannot drift. If the
plan cannot be computed (or two steps share a step order), each cold step holds `StepCreditDefaults`' per-step low instead, identically on
both sides, with a logged warning. `Billing:StepReserveMillisOverride` still wins over the plan in the
engine.

**A composition revision runs only part of its run.** Its snapshot workflow carries every step of the
original run, and the engine plans the execution over all of them, reused steps included, so a cold step's
share depends on the steps it does not re-run. The revision preflight and the replay estimate therefore
plan over the whole snapshot too (`IWorkflowCostEstimator.PreflightChargedAsync`) and require only the
entries of the steps that hold: the revised step, every step that runs again, and every kept decision that
may be re-made.

**Compute is quoted on this deployment's own compute prices at the 1080p tier.** The estimate prices a
profile's compute seconds through the same `IModelPriceService.PriceAsync` request the meter makes
(`ComputeMetering.RecordSecondsAsync`: the system compute capability, variant `1080p`), so on any price
set a 1080p source is quoted at exactly what it is charged. A chosen output resolution then scales the render steps by its pixel factor (see "Output
resolution and credits"). Two things still separate the quote from a real charge: the source's actual
resolution tier for work on the source (an analysis of 4K footage meters at the UHD tier whatever the
output), and the prices themselves. The upgrader ported each install's own compute cards once, so a
deployment seeded before the D9 rebase keeps the earlier placeholder compute prices, which are about 100
times the current seed, until an admin edits them.

**Calibration of the Author profile (cost-honesty).** A custom 10-step reel workflow with an Opus Author quoted
370-1072 credits (typical about 540) while three real runs cost 166 (a 2-pass review loop), 73 and 71. The cause was
the cold-start `AuthorAgent` profile: its staging medians (303,593 input, 14,081 output, 632,576 cached tokens,
98.7 s render) were 5-8x the input, output and render of a real 15 s production pass (about 38,000 / 2,900 /
138,000 / 18 s), so one Opus pass was quoted near 325 credits against 44-70 charged, and a review loop doubled it.
The profile now uses the production per-pass medians (`AuthorColdStartCalibrationTests` holds the real step charges
as constants). The cold-start hold shares come from the same low edge, so the hold of such a run shrinks with the
estimate; each Author step still holds at least its `StepCreditDefaults` default. The Author profile deliberately sets `LowFactor` to .96, an exception to the usual
`<= 0.75` clamp of `Floored`: the low edge is the cold-start hold, and at .75 one Author pass would hold about 36
credits, under every real pass (44-75). The calibration is n=3 runs of 15 s reels and applies to every AuthorAgent
model, so longer films are quoted low. Other agents' profiles were not changed.

**Effect on real templates (as of the cold-start re-plan, before the Author calibration above; templates with an Author step, such as `quick-win-promo`, now quote and hold less, run `TemplateEstimateSpreadTests` for current figures).** No template needed less than before the re-plan. On deepseek-flash, `quick-win-promo`
now needs about 101 credits to start (58.7 before, shown low 86), and about 1,330 on Opus. Derush
templates keep their old figure or a little more on flash (`video-derush-edit`: 47.5, was 47, shown low
7.6), and rise on Opus (121). The `TemplateEstimateSpreadTests` table prints every template.

### Per-agent-type usage profiles, priced through the model capability prices

The cold-start figure of a step comes from `StepUsageProfiles.For(stepType, agentType)`
(`inference/src/ReelBolt.Shared/Metering/StepUsageProfiles.cs`). A profile is the median **usage** of
one execution, not a price: input, output and cached-input tokens, plus compute seconds
(`RenderOutputSeconds`, `EncodeOutputSeconds`, `AnalyzeMediaSeconds`), TTS bytes, transcription seconds or
generated video seconds. Each profile carries a `LowFactor` and `HighFactor` (p25/p50 and p75/p50 of the
measured credits):

- A bucket with at least 3 measured samples uses its measured factors. A thinner or unmeasured bucket
  uses the generic 0.6 / 1.8.
- The factors are then clamped: `LowFactor` is at most 0.75 and `HighFactor` at least 1.33, so a small
  sample never claims a narrow band.
- An agent type with no measurement uses an analogue: solo agents take the `NarrationWriter` quantities,
  and `Custom` or unknown types take the `DirectorAgent` quantities. Rooms are priced as their seat agent
  type (`EditRoom` as `VideoStoryEditor`, `GraphicsRoom` as `MotionGraphicsDirector`, and so on).
- `Conditional` and `Extract` have an empty profile and cost 0.

The estimator prices every profile quantity at estimate time through `IModelPriceService.PriceAsync`, the
same lookup the meter uses (`ProfilePriceTable`, shared by the API's estimate and the engine's hold):

- **Chat** quantities use the provider `IInferenceProviderResolver.ResolveAsync` resolves for the step's
  agent type and agent, with the billing mode from `MeteringChatClient.BillingModeOf`.
- **Compute** uses the system compute capability at the `1080p` reference variant. A run's chosen
  resolution is applied on top by `OutputResolution.PixelFactor` (see "Output resolution and credits"):
  the seeded `720p` and `uhd` prices are exactly that factor from the `1080p` price, so pricing at the
  run's own tier as well would count the resolution twice.
- **Speech synthesis, transcription and video generation** use their resolved providers.

Model capability prices, the platform markup and the multipliers therefore reach the displayed figure
without a code change. Providers are resolved once per estimate. When a provider cannot be resolved or
a quantity cannot be priced, that quantity falls back to the pre-catalog seed constants
(`StepUsageProfiles.SeedFallbackMillis`, before markup) with a logged warning. The estimate never throws, and never shows
0 for a chargeable step because of a pricing failure.

### Display history: exact agent, then agent type

`DisplayCreditHistory.LoadStepSamplesAsync` reads this organization's per-step-result credits over the
`Billing:Estimate:WindowDays` window (default 90 days), with the hold query's filters (workflow usage, not
from cache) plus `credits > 0`. Zero-credit samples (own-provider or unpriced work) are left out, so they
cannot drag a shown median to nothing. Each sample carries its execution's output resolution; a render
step reads its Auto/1080p samples only, or, at another resolution with enough samples of exactly that
resolution, those (see [Output resolution and credits](#output-resolution-and-credits)). Each step takes the
first of these that applies:

1. **`OwnProvider`.** The step's chat part runs on the organization's own provider (see below).
2. **`History`.** At least 3 samples of this exact `(step type, agent definition)`.
3. **`AgentTypeHistory`.** At least 3 samples of this `(step type, agent type)`.
4. **`Default`.** The priced cold-start profile.

A history bucket gives the step's typical, p25 and p75 as the bucket's p50, p25 and p75. A `VideoGenerate`
step's basis is `VideoGeneration`, and a step that is never charged keeps `NotCharged`.

### Review loops, ForEach and Parallel

`RunCreditRange.Combine` (`inference/src/ReelBolt.Shared/Metering/RunCreditRange.cs`) turns each step into a
distribution: its typical value m, plus a separate spread on each side, σ_lo = (m − p25) / 0.674 and
σ_hi = (p75 − m) / 0.674. Measured costs skew upwards, and a symmetric band around the median would push
the low end of a skewed step towards 0. With a spread per side, a lone step shows exactly its own p25–p75.
Repeating steps are modelled as loops:

- **ReviewLoop.** The body is the steps from `LoopTargetStepOrder` to the loop step. A step inside several
  windows belongs to the narrowest one. The pass count N comes from this loop step's past `Passed` runs
  (its result count per run, clamped to 1..`MaxIterations`) once there are at least 3 of them. Otherwise
  it is the cold-start 1–2 passes (mean 1.5, variance 0.25), or exactly 1 when `MaxIterations` is 1. A
  loop never assumes `MaxIterations` passes.
- **ForEach.** A `LoopSourceExpression` that is a literal JSON array runs exactly its length. Anything
  else is uniform on 1..`MaxIterations`, and adds the reason `foreach_items_unknown`.
- **Parallel.** The step's cost is the sum of its branch profiles: the distinct `ParallelAgentIdsJson`
  agents plus the primary agent.

A loop body with total mean B and per-side variances V_lo and V_hi contributes mean E[N]·B and, on each
side, variance E[N²]·V_side + Var(N)·B². Steps outside every loop contribute m, σ_lo² and σ_hi². A loop target that is missing,
below 1 or after the loop step gives the loop no body, and the loop step runs once.

### The run level: past runs, else the combined steps

- **Past runs.** `DisplayCreditHistory.LoadRunTotalsAsync` sums the credits of each `Passed` run of this
  workflow. It leaves out fixed-price steps and totals of 0, and only counts runs that started after the
  later of the history window's start and the workflow's `UpdatedAt`. Every step edit bumps `UpdatedAt`,
  so a run of an older version of the workflow does not count. Only runs that chose the same output
  resolution count (a 4K run's total does not describe an Auto run), and their totals are not scaled
  again. With at least `Billing:Estimate:MinRuns`
  totals (default 5), typical, low and high are their p50, p25 and p75, and `rangeBasis` is `PastRuns`
  with `pastRunCount` set.
- **Otherwise**, the combined steps: typical = the sum of the steps' typical (median) costs, low = typical − 0.674·√ΣV_lo (floored at 0) and
  high = typical + 0.674·√ΣV_hi. These are the p25–p75 points of a split normal. Because variances add, the run's relative spread is
  narrower than any single step's. `rangeBasis` is `StepHistory` when any step used `History` or
  `AgentTypeHistory`, else `FirstRun`.

### Generated video is always "up to"

A `VideoGenerate` step never enters the variable range. Its price goes into `FixedMaxMillis` instead: the
step's declared price, priced from its own settings exactly as its hold is. A step without a declared
price uses the most clips it can order (60 generated seconds), priced through `IModelPriceService` with the
resolved video-generation provider. A video step inside a review loop window counts `MaxIterations` times,
because the ceiling has to hold whatever the reviewer decides. The UI shows the ceiling as "Plus up to F
credits for generated video".

### Why a range is wide: the reason codes

`spreadReasons` lists the codes below, largest variance contribution first (ties in this order) and
without duplicates:

| Code | Set when |
|---|---|
| `review_loop_repeats` | Any review loop has a body |
| `footage_length` | A `VideoAnalyze` or `VideoCompile` step is priced from its cold-start profile |
| `foreach_items_unknown` | A ForEach's item count is only known at run time |
| `own_provider_free` | A step's chat part runs on the organization's own provider |
| `video_generation_max_clips` | `FixedMaxMillis` > 0 |
| `video_generation_in_review_loop` | A fixed-price step sits inside a loop window |

The web UI lists the first four as plain sentences under "Why the range is this wide:" when the high end is
at least twice the low end. It folds the two `video_generation_*` codes into the "up to" line.

### Own-provider (BYO) steps show 0

When a step's resolved chat provider belongs to the organization, its billing mode is not `Platform` and
its chat quantities price to 0 credits. The step's basis is then `OwnProvider`, even when the org has
platform-billed history for that agent, and the run gets reason `own_provider_free`. Any platform compute
in the same profile, such as an author step's render seconds, is still priced.

### Where the measurements came from

The profile quantities and factors are pinned from measurements taken on 2026-10-12:

- **Staging**: 3 days of `usage_events` joined to step results, 4,041 workflow events across 2 orgs,
  priced at the seed rate cards. These are the per-step credits, the median quantities and the review-loop
  pass counts (mean 1.25, max 2).
- **The local development database**: token totals only, from an older engine. These were used only for
  agent types staging had not run. They are split 18% input, 4% output and 78% cached input, the staging
  mix.

Agent types with no measurement anywhere are analogues, and each profile's `Source` says which. Every
agent-like step used to share one per-step-type default, a 76× spread across agents. The data shows a
p75/p25 of only 1.3–3× within one agent type, which is why the display prices each agent type on its own.

## The design system: free parse and import, charged fallback

Reading a project's design sources is free: the `DesignSystem` step (Parse, Compose, Require) holds and
costs nothing (`StepType.DesignSystem => (NoHold, NoHold)` in `StepCreditEstimation`), and the import,
export, library, drift and approval surfaces make no model call. `DesignSystemFreeOperationsTests` pins
both halves: none of those types can reach a chat client, a price or a charge path, and an import, approve
and export leave the usage scope at zero with no ledger row and no usage event.

The LLM fallback of "Build brand and product profile" (`product-profile-from-code`, v2) is ordinary
`Agent` steps: Chat capability, priced by model code through `IModelPriceService`, held and settled by the
credit gate like any Agent step (BYO and Runner ×0, billing off free). It runs only for the part the files
miss: Parse's view sets `fallbackProduct` and `fallbackDesign` independently, and every Conditional reads
that view by `sourceStepOrder: 1`, so a missing product map runs the four analyzers (steps 4–7) and not the
style extractor (step 9) unless the design is missing too.

- **Estimate.** `LowMillis` is the cheapest path (`WorkflowBranchReach.OnEveryPath`: a step a branch can
  skip counts as 0), so 0 for this template; `HighMillis` covers every fallback step; `RequiredMillis` is
  the holds of every fallback step, which is what the 402 preflight compares, because a run cannot know
  its branch before Parse.
- **Bound.** Every route that starts the profile-build template goes through one shared gate,
  `IDesignBuildEstimateGate`: `POST /design-system/builds`, the generic workflow Execute, Retry,
  `assistant/execute`, and the assistant `ExecuteWorkflow` tool in chat and over MCP. It refuses a build whose
  `HighMillis` is above the dynamic cap (`409 ESTIMATE_OVER_CAP`): the cold-start high of the reference
  profile-build template priced at the live prices of the resolved models, times
  `DesignSystem:Build:FallbackCapMargin` (default 1.3), lowered by the optional ceiling
  `DesignSystem:Build:MaxFallbackCreditsHigh` when set (about 49 / 387 / 773 credits high and 64 / 503 / 1005
  cap on deepseek-flash / Sonnet 5.5 / Opus 5.5 at the catalog prices of 2026-10-17, examples that move with
  prices); a reference that cannot be priced from real catalog prices (never from the seed constants) answers
  `503 ESTIMATE_UNAVAILABLE` on every route and starts one only after the caller accepts the high figure
  (`409 ESTIMATE_NOT_ACCEPTED`; over MCP the tool needs `acceptEstimateCredits`). During the run each fallback
  step reserves its hold before it runs and settles at what it metered. The cap bounds the estimate, not
  the settled total: a step that overruns its hold is charged the overage (see the overdraft rule above),
  and the cap is enforced the same way on every route above, not only on the builds route.

## Entitlements and the plan catalog

There is exactly **one** entitlement contract in ReelBolt: `IOrganizationEntitlements`
(`inference/src/ReelBolt.Shared/Tenancy/`), returning an immutable `OrganizationEntitlementSet`.
Tenancy, billing and compute all read it, and nothing defines a second one. The set carries `Tier`
(the plan key, or `selfhost` when billing is off), `ByoAllowed`, `MaxConcurrentExecutions`,
`MaxOutputHeight`, `Watermark`, `RoomsAllowed`, `VideoGenAllowed`,
`Priority`, `StorageGb`, `RunnerAllowed` and `SegmentationAllowed`. "No limit" is a sentinel
(`OrganizationEntitlementSet.UnlimitedInt` / `UnlimitedUsd`) rather than null, and `0` means "not
entitled at all" where that reading makes sense.

**How an organization's set is chosen** (`PlanBackedOrganizationEntitlements`): with
`Billing:Enabled=false` every org gets `OrganizationEntitlementSet.Unlimited` with tier `selfhost`.
Otherwise the org's **entitling subscription's plan** is used — `Trialing`, `Active` and `PastDue` all
entitle, so a plan survives the provider's dunning window — and an org with no such subscription falls
back to the plan named by `Billing:TrialPlanKey` (default `trial`). A missing trial plan row (an
unseeded catalog) degrades to the configured default rather than throwing. Results are cached per org
for `Billing:EntitlementCacheSeconds` (default 30), and the webhook processor calls
`IEntitlementCache.Invalidate(orgId)` on a subscription change so the change applies at once in the
process that handled it; the other service waits out the TTL. Callers must treat the result as a
per-call decision and never cache it beyond a request or a step.

**Where entitlements are enforced** is `PlanGate` plus `PlanGateService` (Inference API) and the
credit gate (engine). The gate returns a machine-readable violation (`code`, `feature`, `message`,
`limit`, `current`) with an HTTP status: `402 PLAN_NOT_ENTITLED` when the plan does not include a step
type (the three room steps, `VideoGenerate`, or text-prompted video segmentation — feature
`segmentation`, see "Prompted video segmentation"), checked at save time in the workflow create and
update path and again at run time, and with feature `resolution` when a run's `outputResolution` or an
editor export's explicit size (the `Uhd4k16x9` preset or an encode width and height; shape presets
and `Master` are clamped by the engine instead, see [editor.md](editor.md#export-resolution-and-the-plan-cap))
is above the plan's `MaxOutputHeight` on the short side (`limit` = the cap, `current` = the requested
short side); `429 CONCURRENCY_LIMIT_REACHED` when the org is at its
concurrent-execution cap, counted from its `Running` executions; and `413 STORAGE_QUOTA_EXCEEDED` when
an upload would pass the plan's storage quota, measured **live** as the sum of the org's project-file
sizes rather than from the daily sampler, which lags by up to a day.

**A plan refusal names its way forward (display only).** Every `PLAN_NOT_ENTITLED` body also carries
`unlockPlan` and `unlockPlanName`: the lowest purchasable plan of the catalogue (subscription and per-seat
rows, in catalogue order) that includes the refused feature, read through `PlanUnlocks` from the plan rows the
entitlement set was read with (`OrganizationEntitlementSet.UpgradePlans`, cached with the set). The two fields
are sent only when a plan is found, so a body keeps its shape with billing off. They authorize nothing and never
hold a price. `GET /api/v1/billing/summary` lists the same answer for every feature the current plan lacks as
`entitlements.unlocks` (`feature`, `planKey`, `planName`). The web renders one `UpgradeCallout` for every such
refusal (a failed step of a run, a refused Run, the editor): what is not included in plain words, "Available on
{plan}", a button to the plans page for an Owner or Billing member, "Ask the workspace owner to upgrade" for
anyone else, and nothing when billing is off. The assistant's tool results carry the same body under `planGate`,
and its instructions say to state the block, name the plan from `unlockPlanName` and point to Plan & billing.
**Run start:** a first run (Execute, not Retry or Resume) of a standard workflow of the route's own project is refused
with the same 402 at the Run button, before any earlier step is paid for, when the plan lacks a gated step type (video
generation, audio generation, rooms) that EVERY path of the run reaches: any gated step of a straight workflow, or, for an
app, every path through the steps its inputs left reachable (`StaticBranchResolver`). A Conditional the inputs decided
counts as the one side it took (a "Finish here" ends the run); one it could not decide counts as both sides, so it refuses
only when both sides reach a gated step (`music-or-sound-effect`). A ReviewLoop, ForEach or Parallel, a condition on any
other step, or a workflow of another project or a hidden kind is left to the engine (no answer, so a foreign id learns
nothing). When unsure it never refuses. Shipped templates on a plan without the feature: `avatar-explainer` and
`music-or-sound-effect` are refused in every input state; `ai-clip-from-idea` is refused (straight).
The config-driven gates (segmentation, audio cleanup, video enhancement, avatars) still refuse at their own
route or step.

**The plan catalog** is one file, `inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json`,
owned by billing and never edited by hand in the database. The Inference API upserts it into the
`plans` table at startup: unchanged rows are not rewritten, a plan removed from the catalog is
**deactivated rather than deleted** (subscriptions reference it), the upsert never touches
`provider_price_ids_json` (which is per-environment data), and an entitlement the set cannot represent
fails startup rather than serving a half-parsed plan. The catalog carries the display fields the
marketing site renders (`id`, `name`, `kind`, `priceMonthly`, `minSeats`, `includedCredits`,
`creditsOneTime`, `highlights`, `limits`, `cta`, `featured`) and the billing-only fields
(`entitlements`, `topUpAllowed`). The shipped rows are Trial (one-time 100 credits, watermark, 720p,
no BYO, no rooms, no video generation), Creator ($29, 1,000 credits a month, BYO, 1080p), Pro ($79,
3,500, rooms and video generation, 4K: `maxOutputHeight` 2160), Team ($49 per seat with a three-seat
minimum, 1,500 credits per seat pooled, 4K) and Enterprise (contact only, no resolution cap), plus the top-up packs (500, 1,000 and 3,000 credits). **Every
entitlement and limit value in it is a placeholder** until the pricing work package B13 sets them from
measured costs.

**The mirror and the drift check.** `/site` cannot read the canonical file (its Docker build context
is `./site` only), so it vendors a **byte-identical copy** at `site/lib/pricing/plans.catalog.json`,
loaded and validated by `site/lib/pricing/index.ts` (schema version, currency, unique ids, known kinds
and CTAs, prices present exactly on priced plans, a minimum seat count on a per-seat plan). CI enforces
the mirror with a diff step that runs for the `site` app before its typecheck and build:
`diff -u inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json
site/lib/pricing/plans.catalog.json`. Editing the canonical catalog therefore means copying it to
`/site` in the same change, or CI fails.

Credits reach an organization through three paths, all of them in the ledger and all of them keyed: the
**trial grant** (one per normalized email, only for a verified `Owner` of a `Personal` workspace, key
`trial:{orgId:N}`), the **monthly plan refill** (keyed per subscription period, see below) and a
**top-up** (keyed per provider transaction).

## Video enhancement: the VideoEnhance step

`StepType.VideoEnhance` enhances one video. Its config column is `workflow_steps.video_enhance_config_json` (the program lead
generates the migration after the final rebase).

**Local operations are free.** `Denoise`, `MotionBlur` and the engine=`Local` `Upscale` / `Interpolate` run ffmpeg on this host:
no hold, no usage row, no plan feature, `Free` for the run rate limit (`RunRateClassifier` reads the engine from the config; a
hosted, missing or unreadable config is `Paid`). `StepCreditDefaults.IsLocalVideoEnhance` is the one reading: the cold-start
row, the usage profile and the engine's hold are all `0`, and the credit gate asks for no entitlement.

**Hosted operations are metered in two units** (`engine=Hosted`, `Upscale`, `Interpolate` and `Extend`):

| Where | What |
|---|---|
| Plan feature | `video_enhance`: `OrganizationEntitlementSet.VideoEnhanceAllowed`, catalog key `videoEnhanceAllowed` (trial `false`; creator, pro, team and enterprise `true`; the API's `plans.catalog.json` and the site's copy are byte-identical). `PlanGate.CheckVideoEnhance` refuses with `402 PLAN_NOT_ENTITLED`, feature `video_enhance`, `FailureClass.EntitlementDenied`; it is config-driven (`CreditEntitlement.VideoEnhance` only for a hosted config), never a step-type gate, so local operations are never gated. |
| Hold | Taken by the engine before the step, from the price book: `VideoEnhanceStepPricing.TryStepHoldMillisAsync` prices the source the route measured (the route writes `sourceWidth`, `sourceHeight`, `sourceFps` and `maxSourceSeconds = ceil(seconds)` into the hosted step, so the hold, the estimate and the route's balance check are one figure), and falls back to the WORST CASE of a 1920x1080 source at 60 fps for the hosted cap when a step carries no hints (a hand-built workflow). The compute-second rate and the caps are read from `VideoEnhanceHostedSettings` (`VideoEnhance:Hosted:ComputeSecondsPerOutputFrame`, `...:MaxSourceSeconds`), the one reading the engine, the estimate and the route all share. The length is the LARGER of the container and the video stream, with the stream's frame count. `null` (unpriced) falls back to the cold-start row, never to a zero hold. The executor refuses a source whose real price exceeds the hold (`ENHANCE_SOURCE_TOO_LONG`, nothing uploaded). |
| Settle: upscale | `UsageKind.VideoEnhanceSeconds`: the vendor-reported (else probed) OUTPUT seconds rounded UP, at the price of the output tier (`720p`, `1080p`, `uhd` and the `-hfr` twin above 30 fps). Key `videoenhance:{ledger row}`. |
| Settle: extend | `UsageKind.VideoEnhanceSeconds` under `InferenceProviderCapability.VideoOutpaint`: Wan VACE bills `ceil(frames / 16)` video seconds at the canvas short-side tier (`720p` $0.08, `580p` $0.06, `480p` $0.04 per video second), Luma reframe the clip's whole seconds at a flat $0.2. The hold and the estimate use the same rows (`VideoEnhanceCreditPricing.TryHoldOutpaintMillisAsync`); a source over the model's frame ceiling or a canvas with no price row is unpriced and unofferable. Ledger clip id `enhance-extend`. |
| Settle: interpolation | `UsageKind.VideoEnhanceComputeSeconds`: the vendor reports no compute figure, so settlement is the compute-second hold (`ceil(outputFrames x ComputeSecondsPerOutputFrame x pixels/1080p)`, setting `VideoEnhance:Hosted:ComputeSecondsPerOutputFrame`, default 0.05, UNMEASURED). The unmeasured constant is the price the customer pays until it is measured with a real key. The settlement is the compute-second HOLD for the measured source (`ceil(outputFrames x rate x pixels/1080p)`, a whole number of compute seconds), so a fractional real compute time is never billed; the upscale settle is the vendor's billed seconds capped at the remuxed output's length and at the length the hold was priced for. |
| Not metered | A result that fails the length check or the conform (`ENHANCE_DURATION_MISMATCH`, `ENHANCE_PICTURE_INVALID`) records no usage; its class refunds the hold. |
| Failure classes | `NO_VIDEO_ENHANCE_PROVIDER`, `ENHANCE_SOURCE_TOO_LONG`, `ENHANCE_SOURCE_NO_VIDEO`, `ENHANCE_INVALID_REQUEST`, `ENHANCE_REQUEST_INVALID`, `ENHANCE_NOT_LARGER`, `ENHANCE_FPS_TOO_HIGH`, `ENHANCE_OUTPUT_TOO_LARGE` are `UserInput`; `ENHANCE_PROBE_FAILED`, `ENHANCE_FFMPEG_FAILED`, `ENHANCE_PICTURE_INVALID`, `ENHANCE_EGRESS_UNAVAILABLE`, `ENHANCE_UPLOAD_FAILED`, `ENHANCE_PROVIDER_CONFIG` and `ENHANCE_FAILED` are `PlatformInternal`; `ENHANCE_VENDOR_QUOTA`, `ENHANCE_VENDOR_RETRYABLE`, `ENHANCE_VENDOR_FAILED`, `ENHANCE_VENDOR_REFUSED`, `ENHANCE_STILL_RUNNING` and `ENHANCE_DURATION_MISMATCH` are `PlatformProviderError`; `ENHANCE_SOURCE_CHANGED` (the source measured at run time is bigger, faster or longer than the hints the hold was priced on; nothing was uploaded) is `PlatformInternal`, so the hold is returned. `PlatformInternal` and `PlatformProviderError` refund; on an organization's own provider row the vendor codes are `ByoProviderError`. A user cancel is settled by the engine's existing rules. |
| Cache | Cacheable (deterministic): the key carries the config (op, engine, parameters, source, provider id, consent), the source's content and the provider fingerprint (`DeterministicStepRevisions` tag `2026-11-03.video-enhance-1`). A consent-false hosted step fails and failures are never cached, so it can never poison the key; a cache hit costs nothing. The step is never retried (`ResolveMaxRetries` = 0), so a hosted retry cannot pay twice. |
| Ledger | One `external_generation_jobs` row per hosted job (`IVideoGenerationBudgetGate`, scope `Enhance`, clip id `enhance-{op}`, idempotent per execution and request hash). The vendor handle is stored in `remote_job_id` (`VideoEnhanceHandleCodec`); a resumed attempt polls it instead of submitting. |

## The IBillingProvider boundary

Everything provider-specific sits behind `IBillingProvider`
(`inference/src/ReelBolt.Inference.Api/Services/Billing/IBillingProvider.cs`, decision D11): the
platform sees only neutral shapes, so changing provider is one new class. The interface is `Name`,
`IsConfigured`, `CreateCheckoutAsync`, `CreatePortalSessionAsync`, `VerifyAndParseWebhook`,
`CancelSubscriptionAsync`, `ResumeSubscriptionAsync`, `ChangePlanAsync` (plan and seat changes alike),
`TopUpPriceId` and `TopUpPacks`, plus `CreateDiscountAsync`, `UpdateDiscountAsync`,
`PreviewPriceAsync` and `CancelTransactionAsync` for promotions. A provider implementation converts its
own payload into a `BillingEvent` — a neutral record with `Provider`, `EventId`, `EventType` and
`OccurredAt`, one of six kinds (`SubscriptionChanged`, `TopUpPaid`, `PaymentRefunded`, `UpgradePaid`
for the paid proration charge of a same-period upgrade, `TransactionCompleted` for a discounted
subscription payment, or `Ignored` for anything the
platform deliberately does not act on), the organization, customer and subscription ids, the status
and billing period, the priced line items, the resolved top-up pack size, and the discount id,
discount total and promotion ids a checkout carried.
`BillingEventProcessor` never sees a provider payload shape.

A checkout never grants anything: it returns a hosted-checkout URL and a transaction id, and the
subscription or the credits change only when a verified webhook is processed. The order of authority is
deliberate and is stated in the processor's own documentation — **webhooks are authoritative**; a
checkout redirect or a client call changes nothing.

Two implementations ship: Paddle (the default) and Stripe, selected for new purchases by
`Billing:Provider`, while an existing subscription keeps the provider named on its row. Their signature
schemes, event types, base URLs, checkout and portal calls, seat changes, the `Billing__Paddle__*` and
`Billing__Stripe__*` configuration keys, the endpoint table and the operator setup checklists are all in
[docs/billing.md](billing.md); this page deliberately does not repeat them. Three Stripe behaviours change
what lands in the ledger and are described in [Stripe](billing.md#stripe): an immediate plan or seat
change is sent with `payment_behavior=error_if_incomplete`, so an unpaid upgrade is refused and grants
nothing; a lost dispute (`charge.dispute.closed`, `status=lost`) claws credits back like a refund, with
the dispute id as the adjustment id; and `unpaid` and `incomplete` subscriptions are `Paused`, so they
grant no entitlements.

## The webhook pipeline and its idempotency keys

The receiver is `POST /api/v1/billing/webhooks/{provider}`, anonymous and authenticated by the
provider's signature over the exact raw body alone (see [docs/billing.md](billing.md) for Paddle's
header shape and the nginx treatment). Once a payload is verified, `BillingEventProcessor.ProcessAsync`
applies it with **exactly-once effects**:

1. It checks whether the `(provider, event id)` pair is already stored and answers `Duplicate` without
   doing anything if it is.
2. Inside one database transaction it inserts the `billing_webhook_events` row (provider, event id,
   event type, the raw payload as `jsonb`), protected by a unique index on `(provider, event_id)`. A
   concurrent delivery of the same event loses that index and rolls back as a `Duplicate` — the winner
   owns the work.
3. It applies the effect, marks the row processed and commits. **The row, the subscription change and
   every ledger entry commit together**, so a crash leaves nothing half done, and a failure rolls the
   row back, which is what makes the provider's retry run the event again from scratch.

The outcome is one of four: `Applied` (state changed, or an event the platform deliberately ignores),
`Duplicate`, `Stale` (older than the newest event already applied to that subscription) and
`Unresolved` (recorded, not actionable — no organization or no known plan, with the reason in the
row's `error` column). All four are a `200` to the provider; only an exception is a `500`, which is
what asks the provider to retry. The row's `outcome` column records which one it was.

**A lost webhook is replaced by a synthetic event through the same processor.** `BillingReconciler`
reads the provider's live state and wraps it in the envelope the lost webhook would have carried. A
subscription becomes event id `reconcile:sub:{subscriptionId}:{updated_at UTC ticks}` (stored event
type `subscription.reconciled`) with `OccurredAt` = the provider's `updated_at`; a completed upgrade
payment becomes `reconcile:txn:{transactionId}` (stored type `transaction.reconciled`) with
`OccurredAt` = its `billed_at`, else `updated_at`. Each goes through `ProcessAsync` like a webhook, so
the same snapshot read twice is a `Duplicate`, and the ledger keys above still hold. Because the
subscription's last applied event time is now the provider's `updated_at`, the real webhook that
arrives late (or a dashboard replay) ends `Stale` when its `occurred_at` is not newer, and otherwise
applies the same state again with no new grant; a late `transaction.completed` for an upgrade
already paired is a no-op. The `reconcile:` prefix is how the daily spend report tells healed
webhooks from received ones. Operator steps are in
[docs/billing.md](billing.md#when-a-paddle-webhook-is-lost).

**Credit effects carry their own idempotency keys** as a second line of defence, so a differently
numbered event describing the same payment cannot grant twice:

| Effect | Ledger key |
|---|---|
| Monthly plan refill | `plan:{provider}:{subscriptionId}:{periodStart UTC}` |
| Same-period upgrade difference | `plan-upgrade:{provider}:{transactionId}` (the paid proration transaction) |
| Top-up grant | `topup:{provider}:{transactionId}` |
| Refund clawback | `clawback:{provider}:{transactionId}` |
| Pack bonus | `topup-bonus:{provider}:{transactionId}` |
| Pack bonus clawback | `clawback-bonus:{provider}:{transactionId}` |

The refill key is the reason the created, updated and renewal events that all describe one period
grant exactly once between them, while a genuine renewal (a new period start) grants again. The
clawback keys name the refunded transaction, not the adjustment, so a refund and a later
chargeback of the same payment claw back once.

## The subscription state machine

A `subscriptions` row is ReelBolt's provider-neutral view of what an organization bought: the plan,
the provider and its subscription id, the status, the billing period, the seat count, whether a
cancellation is scheduled and when (`CancelAtPeriodEnd`, `CancelEffectiveAt`), a held downgrade
(`ScheduledPlanId`, `ScheduledSeats`, `ScheduledChangeAt`), and `last_event_at`. Only a verified
webhook writes it; the cancel, resume and change-plan endpoints ask the provider and answer `202`
(endpoints and Paddle calls: [docs/billing.md](billing.md), "Cancel and plan changes").

- **Statuses** are `Trialing`, `Active`, `PastDue`, `Paused` and `Canceled`, mapped from the
  provider's own status. `Trialing`, `Active` and `PastDue` entitle.
- **Cancel scheduled.** An event with a scheduled cancel sets `CancelAtPeriodEnd` and
  `CancelEffectiveAt` (the provider's effective time, else the period end); the subscription stays
  active with its plan and credits until then. An event without one (after a resume) clears both.
- **Canceled → Trial.** When the provider's cancel takes effect the row becomes `Canceled`. A
  canceled row is no longer the organization's current subscription, so the billing summary shows the
  Trial plan and entitlements fall back to `Billing:TrialPlanKey`. Credits already granted stay until
  their own expiry.
- **Ordering.** Providers do not deliver events in order, so every applied change stamps
  `last_event_at` with the event's `occurred_at`, and an event that is not newer is recorded as
  `Stale` and not applied. A late `active` update therefore cannot resurrect a subscription that a
  newer event cancelled.
- **Resolving the organization** goes from the existing subscription row, to the organization id the
  platform put into the checkout's custom data, to the stored billing customer. An event naming no
  known organization is stored with a note and acknowledged, not retried forever.
- **Plan and seats.** The plan is found by matching a subscription item's price id against the plan's
  provider price map; a per-seat plan takes its seat count from the item quantity and never drops below
  the plan minimum. A **first** paid subscription cancels any other non-cancelled subscription of the
  organization, which is how the internal Trial row is replaced.
- **Direction of a plan or seat change.** `BillingEventProcessor.IsDowngrade` compares the monthly
  price times seats (seats count only on a per-seat plan): lower is a downgrade, the same or higher is
  an upgrade.
- **Upgrade grant.** An event in the **same period** (unchanged period start) that moves to a higher
  plan or more seats, or the first event of a **new** period that already shows a higher price than the
  row (its renewal webhook lagged), applies at once but grants nothing by itself beyond the period's
  refill at the pre-upgrade plan: it records a pending upgrade
  (`billing_pending_upgrade_grants`). The credits arrive with the **paid proration transaction**
  (Paddle `transaction.completed`, `origin: subscription_update`): it grants the pending upgrade it
  belongs to: same subscription, and the items the transaction charged above 0 (`price_id` and
  quantity) **exactly equal** the upgrade's target items (`target_items` / `charged_items`, Security
  R-2). Time only narrows those exact matches (inside the upgrade's period, proration start (else
  completion time) within `BillingEventProcessor.UpgradePairingTolerance`, 10 minutes, of the upgrade
  event's time) and never pairs on its own. Pairing must be unambiguous both ways: the arriving payment
  or upgrade has exactly one exact candidate and that candidate has no other unmatched payment or unpaid
  upgrade for the same items within the tolerance; otherwise nothing pairs, a `Warning` names every
  transaction and row id for manual review, and those upgrades grant nothing (an under-grant rather
  than a mis-pairing whose refund could claw back the wrong grant). No items on either side never
  pairs. Details in [billing.md](billing.md). A paired payment grants the entitlement difference (monthly
  credits × seats, only when positive) prorated over the rest of the period: difference × (period end
  − at) ÷ period length, rounded down to whole millicredits, where `at` is
  `max(the instant the provider prorated from, the upgrade event's time)`, nothing when either period
  bound is unknown
  (`BillingEventProcessor.ProratedUpgradeGrantMillis`). A failed or never-completed payment grants
  nothing. A payment that arrives before its upgrade event is remembered, and the upgrade grants on
  arrival; either order grants once. Every webhook of one subscription runs under a transaction-scoped
  `pg_advisory_xact_lock` keyed on (provider, subscription id) (`SubscriptionLockKey`; a no-op off
  Postgres), so the update and its payment arriving together pair instead of both waiting unpaired. It is a `Plan` grant expiring at the period end, keyed
  `plan-upgrade:{provider}:{transactionId}`, so a redelivery or a differently numbered event cannot
  grant it twice. **Capped per period:** the difference is measured from
  `max(the period's refill entitlement, highest entitlement already granted for an upgrade this
  period)`, so upgrade/downgrade cycles inside one period never total more than the highest plan's
  prorated difference.
- **Scheduled downgrade.** A same-period event that moves to a lower plan or fewer seats does not
  change `PlanId` or `Seats`: the organization keeps what it paid for, and the lower plan is held in
  `ScheduledPlanId`, `ScheduledSeats` and `ScheduledChangeAt` (the period end). The hold exists
  because the provider (Paddle) has no scheduled item change; it switches items immediately and bills
  the new price from the next period. A same-period event back on the current plan and seats clears
  the hold ("keep my plan").
- **Period-end transition.** The first event with a **new** period start applies the event's plan and
  seats (by then the provider's items already are the downgraded ones), clears any hold, and refills
  at that plan.
- **Refill.** An `Active` or `Trialing` subscription with a known period start grants the plan's
  monthly credits (times the seat count for a per-seat plan), expiring at the period end. `PastDue`,
  `Paused` and `Canceled` grant nothing, and credits already granted stay until their own expiry.
  The refill is the entitlement the period started with: when the period's first `Active` event is
  itself an upgrade (or an upgrade was recorded while the period was not yet refilled), the refill is
  granted at the pre-upgrade plan before the upgrade is recorded, and the upgrade's paid difference is
  measured from that refill, so one period never totals more than `refill + prorated(new − refill)`.
  Inside a period only an upgrade grants extra credits (the prorated difference above, once its payment completes); a downgrade grants
  nothing until the next refill.
- **Top-up.** A completed payment for a configured top-up price grants the pack's credits as a
  `TopUp` bucket expiring 12 months later (`BillingEventProcessor.TopUpExpiryMonths`). The pack size
  comes from the price id that was actually paid, never from custom data.
- **Refund.** A full refund of a **top-up** claws the credits back as a negative ledger adjustment, so
  the balance may go negative if they were already spent. A full refund **or chargeback** of an
  upgrade's proration transaction claws back the unspent part of the grant made for that transaction
  (`min(grant remaining, amount granted)`, once per transaction), never another upgrade's. A full
  refund or chargeback of a transaction not seen yet (its `transaction.completed` has not arrived) is
  remembered as a tombstone (a `billing_pending_upgrade_grants` row with only the transaction and
  `ClawedBackAt` set), so that top-up or upgrade payment grants nothing when it arrives and the ledger
  is untouched; a redelivered or second adjustment for it is a no-op, and one whose organization cannot
  be resolved is recorded `Unresolved` with a warning; one that names a known subscription (possibly a
  renewal payment) notes `review manually (subscription payment?)` and logs a warning. A refunded
  upgrade payment, once it arrives, retires the one upgrade it pairs with (closed, 0 granted), so that
  upgrade never absorbs a neighbouring payment. A top-up payment and its refund are serialised by a
  per-transaction `pg_advisory_xact_lock` (`TransactionLockKey`, taken before the subscription lock).
  Those are automatic: a partial refund, or a refund of any other subscription payment, is recorded
  with a note and needs a human decision.

## What is designed but not built yet

One of the metering and billing work packages is **not finished**, and anything that reads as a live
per-step charge should be read against this list:

- **`VideoGenerate` enforces its cap on the spend ledger, not through the credit reservation
  (WP B6c).** Its budget gate (`VideoGenerationBudgetGate`), the `external_generation_jobs` spend
  ledger with `cost_estimate` and `actual_cost`, and the `BUDGET_EXCEEDED` / `MaxSpendCredits` caps
  are what stops a run overspending. Both are denominated in **credits** now — the vendor's USD quote
  is converted once, by `CreditCurrency`, at the boundary — so the ledger and the cap agree, but the
  ledger is still its own table rather than the credit ledger. The `VideoGenSeconds` usage row is
  written alongside it (keyed `videogen:{jobId:N}`) as the metering record, so the two never double
  count; what is not yet wired is the reserve → settle → refund lifecycle above, which a
  `VideoGenerate` step does not open. Also unmetered on the failure path: computed seconds are recorded on success
  only, so a failed render or encode is not yet charged before being refunded by class.

## What the assistant reports after a run

The assistant states the credits actually charged (the sum of `workflow_step_results.credits_charged_millis`, net of refunds), never the estimate shown on the proposal card. Credits a step metered but could not settle (`credits_unsettled_millis`) are reported as `creditsHeldOrUnsettled` and the figure is called not final. With billing off no cost is reported.

## Repeat refunded too-demanding runs

A `SOURCE_TOO_DEMANDING` step failure is refunded in full, so it costs compute and no credits. To stop an organization repeating it, the run gate (`RunStartGate.RefuseAsync`, shared by Execute, Retry, Resume, the assistant and MCP `ExecuteWorkflow`) refuses with 429 `TOO_DEMANDING_LIMIT` and a `Retry-After`, **before the credit preflight**, when the organization already has `VideoSources:MaxTooDemandingFailuresPerDay` (default 3, `0` or negative disables) such step results in the last rolling 24 h and the run has a known-heavy `VideoAnalyze` source. Nothing is reserved or spent by the refusal. If the count cannot be read the run is allowed. See `docs/video-editing.md`.
