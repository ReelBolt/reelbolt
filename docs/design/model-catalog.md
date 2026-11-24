# Model catalog: providers → models → capabilities → prices, and the non-workflow charging policy

Status: design, 2026-10-12, initiative `saas-model-catalog` (issue #261). It implements sections C and D of the
SaaS billing overhaul and product decisions 6 to 9. Paths are relative to
`inference/src/` unless they start with `web/`, `mcp/` or `docs/`.

## 1. Summary

- `inference_providers` becomes the **account**: kind, endpoint, key, headers, timeout, OAuth mode, org, enabled.
  One row per real provider account.
- Under it, `inference_models` (vendor `model_code` + customer-facing `display_name`), and under each model,
  `model_capabilities` (capability, default, enabled, last test, price source). Under each capability,
  `model_capability_prices` (unit, cost USD, optional credits override, optional variant, effective dates).
- **Identity is preserved.** Every old `inference_providers.id` becomes the id of the `model_capabilities` row it
  turned into. Every reference that held a provider row id keeps its value and now names a model capability:
  the three hard FKs, step-config JSON ids, config-sourced ids, `usage_events.provider_id`,
  `external_generation_jobs.provider_id`, `decision_observations.provider_id` and the step-cache fingerprint.
  **No step-config JSON is rewritten, no engine migration is needed, and every step-cache key stays valid.**
- Credits = `cost_usd × markup ÷ credit_usd_value` (markup 1.2, credit value $0.006, both in `pricing_settings`,
  markup editable), unless the price row sets `credits_millis_per_unit_override`. BillingMode multipliers
  (`rate_multipliers`, Platform ×1, BYO ×0, Runner ×0) are kept and stay editable.
- Metering prices by **model capability id + unit + variant**. String-pattern matching on rate cards is gone;
  `rate_cards` is frozen (kept as history, never read at runtime). `ModelPriceCatalog` becomes the **price
  book**: it only pre-fills price rows when a capability is created or upgraded, and is never read while metering.
- Non-workflow charging: the assistant chat (including LookAtPicture) and MCP share a monthly allowance per
  plan, then are charged. Picture-editor AI operations and timeline AI proposals are charged per call. File
  summaries, indexing, the docs index and storage stay free.

## 2. Schema (all Inference-API-owned; the engine maps the new tables read-only)

### `inference_providers` (existing table, narrowed)

Kept: `id`, `organization_id`, `name` (unique per org, NULLS NOT DISTINCT), `kind`, `endpoint`,
`api_key_encrypted`, `api_key_last_four`, `is_enabled`, `personal_o_auth_mode`, `timeout_seconds`,
`extra_headers_json`, `created_at`, `updated_at`.
Added: `is_system bool not null default false` (the platform compute provider, §5.4; not editable or deletable).
Moved out: `capability`, `model_name`, `is_default`, `last_test_at`, `last_test_ok`, `last_test_error`.

### `inference_models` (new)

`id uuid`, `provider_id` → `inference_providers` (cascade), `organization_id` (denormalised from the provider,
FK organizations cascade), `model_code text`, `display_name text`, `is_enabled`, `created_at`,
`updated_at`. Unique `(organization_id, display_name)` NULLS NOT DISTINCT, so a picker label is never ambiguous
inside one scope (old row names were already unique per org, so the migration satisfies it). Both text columns are
unbounded, like the old `name` and `model_name` they are copied from, so a legacy value of any length migrates; the
admin API and the assistant tools refuse NEW values over 200 characters.

### `model_capabilities` (new)

`id uuid` (= the old `inference_providers.id` for migrated rows), `model_id` → `inference_models` (cascade),
`organization_id` (denormalised), `capability` (string enum), `is_default`, `is_enabled`,
`price_source varchar(16)` (`Table` | `Oracle`), `last_test_at`, `last_test_ok`, `last_test_error`,
`created_at`, `updated_at`. Unique `(model_id, capability)`; unique `(organization_id, capability) WHERE is_default`
NULLS NOT DISTINCT (the old one-default-per-capability rule, moved).

### `model_capability_prices` (new)

`id uuid`, `model_capability_id` → `model_capabilities` (cascade), `unit` (`UsageKind` name, §2.1),
`variant varchar(64) null`, `cost_usd_per_unit decimal(28,12)`, `credits_millis_per_unit_override decimal(28,10) null`,
`effective_from`, `effective_to null`, `notes`, `created_at`.
Unique `(model_capability_id, unit, variant, effective_from)` NULLS NOT DISTINCT. A new price for the same
(unit, variant) closes the open predecessor, exactly like rate cards did.

### `pricing_settings` (new, one row, id = 1)

`markup decimal(12,6) default 1.2`, `credit_usd_value decimal(18,8) default 0.006`, `catalog_upgraded_at null`,
`updated_at`. Only `markup` is editable from admin. `credit_usd_value` is the published credit COGS and is changed
only by migration.

### `usage_events` (existing)

Added: `price_id uuid null` (soft ref to `model_capability_prices`), `variant varchar(64) null`.
`provider_id` keeps its column and now holds the model capability id (same value as before for migrated rows).
`rate_card_id` stays for history and is never written again.

### `rate_cards`, `rate_multipliers`

`rate_cards` is frozen: no seeder, no endpoint and no runtime reader. The upgrade (§4) reads it once.
`rate_multipliers` is unchanged and stays the multiplier source. The engine drops its read-only `rate_cards`
mapping.

### 2.1 Units

`unit` reuses `UsageKind` (append-only, persisted by name): it already is the closed unit list. Per token:
`LlmInputTokens`, `LlmOutputTokens`, `LlmCachedInputTokens`, `EmbeddingTokens`. Per second: `AsrAudioSeconds`,
`VideoGenSeconds`, `RenderOutputSeconds`, `EncodeOutputSeconds`, `AnalyzeMediaSeconds`. Per byte: `TtsInputBytes`.
Per image: `ImageGenImages`, `ImageEditImages`, `SegmentationImages`, `DepthImages`. Per GB-day: `StorageGbDay`.
Per call: `ModerationCalls` (one call to `ContentModeration`, recorded at `$0`). The enum is append-only;
the metering doc carries the live list. A capability's required units come from
`ProviderCapabilityUsage.ByCapability`.

### 2.2 Variants

`variant` distinguishes prices of one unit inside one capability:
- video generation: the resolution token the vendor quotes (`768P`, `2K`, `720p`, `1080p`…);
- fal image tiers: today's `#tier` suffix of `IBillingModelKeyed.BillingModelKey` (`low_quality`, `1K`, `max`…);
- compute: the tier `720p` / `1080p` / `uhd` (§5.4).

### 2.3 New enum members

`InferenceProviderCapability.Compute` (appended). It is only served by the system compute provider. No admin
create path, picker or resolver method offers it.

`InferenceProviderCapability.ContentModeration` (appended). It is served by
`OpenAICompatible` providers only, resolves like Vision/Transcription through
`ResolveContentModerationAsync`, and meters `UsageKind.ModerationCalls` at `$0`. The first model is
OpenAI `omni-moderation-latest` (pinned snapshot `omni-moderation-2024-09-26`); see
[`docs/content-moderation.md`](../content-moderation.md).

## 3. Tenancy and authority

- The `"Tenant"` query filter applies to `inference_providers`, `inference_models` and `model_capabilities`: all
  three are addressable by caller-chosen ids. `TenantBoundaryInventoryTests` grows from six to eight entities.
  `model_capability_prices` and `pricing_settings` carry no org, are reached only through platform-admin endpoints
  or an authorized parent capability, and stay unfiltered (the house rule).
- Platform rows: `organization_id` null. BYO rows: the org's id on all three levels, kept equal by the admin
  service (a model or capability is always created under its parent's org).
- Prices, markup, multipliers, coverage and the price book are **platform-admin only**. BYO capabilities never
  get prices (BYO is ×0) and the org endpoints never return or accept prices.
- `StepProviderOwnershipValidator` checks referenced ids against `model_capabilities` (platform or own org), with
  the same single refusal message (D5). The `foreign_provider_ref` counter still fires in the resolver's
  `TryGetExplicit`.
- Keys stay on the provider (account). Changing a key or endpoint changes every model under that provider; the
  admin UI and the assistant approval card both say so.

## 4. Migration and upgrade (zero manual steps)

### 4.1 EF migration `AddModelCatalog` (Inference API context; touches only API-owned tables)

1. Create the four new tables, add `inference_providers.is_system` and `usage_events.price_id`/`variant`.
2. For each `inference_providers` row: insert one model (`id = md5('model:' || id)::uuid`, `provider_id = id`,
   `model_code = model_name`, `display_name = name`, enabled) and one capability (`id = id`, `capability`,
   `is_default`, `is_enabled`, `price_source = 'Oracle'` for `Higgsfield` else `'Table'`, the `last_test_*`
   fields). Each old row stays as its own provider, so this step is 1:1 and lossless.
3. Drop the old `(organization_id, capability) WHERE is_default` index and the moved columns.
4. Re-point `agent_definitions.inference_provider_id`, `project_voices.inference_provider_id` and
   `organization_agent_provider_overrides.inference_provider_id` to `model_capabilities(id)` (ON DELETE SET NULL).
   Column names and C# property names (`InferenceProviderId`) are unchanged.
5. Insert the `pricing_settings` row.

`Down` reverses it. It first refuses, with an error naming the first offending row, when any model's organization
differs from its provider's or any capability's from its model's. Every capability whose id is not a provider id
gets a provider row copied from its parent (`id = capability id`, `name = display_name`, joined on the organization
too), the columns come back from the capabilities, the FKs are re-pointed, providers that no capability names are
deleted, and the new tables are dropped. Prices are lost. `usage_events.price_id` and `variant` stay (§4.3), and
`Up` adds them only if missing.

### 4.2 `ModelCatalogUpgrader` (C#, runs in `DatabaseSeeder.SeedAsync` right after the API migration, every boot, idempotent)

It needs C# because keys are encrypted with Data Protection (random IV), so SQL cannot compare them, and price
porting reuses the old pattern-matching rule one last time.

1. **System compute provider.** Ensure one exists (`is_system`, kind `OpenAICompatible`, no endpoint or key,
   name "Platform compute") with one model "Rendering and encoding" (`model_code compute`) and one `Compute`
   capability, both with fixed well-known ids (`WellKnownModelCatalog`).
2. **Price port, once** (when `pricing_settings.catalog_upgraded_at` is null). For every **platform** capability
   and every unit its capability requires, find the effective `rate_cards` row with the old rule (same kind and
   unit; exact name > longest `prefix*` > `*`; later `effective_from` wins) using the model code. Image tier
   cards (`code#tier`) become variant rows. Each port writes `cost_usd_per_unit = card.cost`. It sets
   `credits_millis_per_unit_override = card.credits` **only** when the admin had set credits that differ from
   `cost × 1000 ÷ 0.006` (tolerance 1e-9). Seeded cards are cost-derived, so they pick up the 1.2 markup
   (decision 7). With no `rate_cards` rows at all (a fresh install), it ports from the price book instead.
   Compute cards port onto the compute capability, one variant per tier. A future-dated card is ported too, with
   its own `effective_from`, wherever the old rule would have switched to it (logged when the capability already
   had its own rows for that unit). It then stamps `catalog_upgraded_at`. On PostgreSQL the whole upgrader run is
   one transaction under `pg_advisory_xact_lock`, so concurrent boots port once.
3. **Video resolution variants.** For platform `VideoGeneration` and `LipSync` capabilities of kind MiniMax or FalAi, it adds
   one variant row per resolution from the client's static `Describe(model).PricePerSecondByResolution`, if that
   variant has no row yet. This is an intentional price change: 2K MiniMax-H3 seconds now cost the vendor's 2K
   rate. Higgsfield stays `price_source = Oracle`. ComfyUI keeps its ported `VideoGenSeconds` rows (its
   budget stays in GPU-seconds, unchanged).
4. **Consolidation** (`ModelCatalog:Consolidation` = `Apply` (default when unset) | `DryRun` | `Off`; any other
   value means `DryRun`, with a warning). `Off` stays an operator switch, but nothing depends on it: a normal
   upgrade does not need it. **Grouping rule:** providers are one account only when the organization (the
   platform, or one same organization; never across organizations), kind, endpoint (trimmed), OAuth mode, timeout
   and extra headers match **and** both carry a present, decryptable, equal key (compared by SHA-256 of the
   plaintext). Never grouped: a provider with no key (null or empty; key-less rows of one kind and endpoint are
   often separate local services, and "no key" is no account identity), the `env:` ambient-credential sentinel
   (the ambient credential is not an identity we can verify), and a key the key ring cannot decrypt. Skipping is
   always safe: the 1:1 split already resolves every id. One exception to the header match: a provider with no
   `extra_headers_json` whose `api_key_encrypted` is byte-identical to that of the providers of exactly one
   headered set joins that set (the pre-catalog image-tool rows copied the fal row's ciphertext but not its
   headers) and the survivor keeps the headers. In each group, the oldest provider (created_at, then id)
   survives. The models of the others move to it, the survivor is enabled if any member was, and the emptied
   providers are deleted. Inside the survivor, models with the same `model_code` merge when their capability
   sets do not collide (the older model's display name wins). Capability rows never change, so **resolution is
   identical before and after**. Every merge is logged as one line without key material.
   `GET /api/v1/inference-providers/consolidation-report` (platform admin) returns what a run would merge now
   (the dry run); it uses the same plan over the same rows as the apply.

   **Concurrency.** The apply runs inside the upgrader's transaction holding `pg_advisory_xact_lock`
   (`ModelCatalogUpgrader.AdvisoryLockKey`, a fixed key; no lock on Sqlite/InMemory) and re-reads the providers
   inside the lock, so a second replica booting at the same time waits, then finds the groups already merged
   and does nothing; a second run is a no-op. Each group is written by foreign key only (moves, then one delete
   of each emptied model and provider), so no row is updated or deleted twice.

   **Failures never block boot.** Every upgrader step (compute provider, price port, video variants,
   consolidation) runs behind a savepoint. A step that throws is rolled back to it (no half-merged state),
   logged as an error with the step, the consolidation group and the exception type (never key material), and
   counted (`reelbolt.model_catalog.consolidation_failed`, or `reelbolt.model_catalog.upgrade_step_failed`
   tagged `step` for the others; meter `ReelBolt.Inference.ModelCatalog`). Boot continues to the seeders and
   the next boot retries. Cancellation on shutdown still propagates.

On a realistic install, the Anthropic Chat and Vision rows sharing one key become one provider with one model and
two capabilities. A fal row plus its eight image-tool rows (ciphertext copied byte for byte) become one provider
with nine models.

### 4.3 Rollback

Before rolling the image back, run the new image's `Down` (`dotnet-ef database update AddAssistantAutoApprove
--context InferenceApiDbContext`), then deploy the previous image. `rate_cards` is intact, so the old metering
resumes. Usage recorded in between keeps its credits and `price_id`; the old code ignores the column.

## 5. Resolution and metering

### 5.1 Resolver

The provider store loads enabled capabilities joined to enabled models and enabled providers, flattened into one
read model, `ModelCapabilityView`. It carries the old entity's member names (`Id` = capability id,
`OrganizationId`, `Name` = model display name, `Kind`, `Capability`, `Endpoint`, `ModelName` = model code,
`ApiKeyEncrypted`, `IsDefault`, `IsEnabled`, `PersonalOAuthMode`, `TimeoutSeconds`, `ExtraHeadersJson`,
`LastTestOk`, `LastTestAt`) plus `ProviderId`, `ProviderName`, `ModelId`, `PriceSource` and `IsSystem`. The
resolver and `ProviderHealthSelector` work over it unchanged in logic. **Every fallback chain, the org filter,
the BYO entitlement drop, the cache and the TTL are unchanged.**

For built-in agents the chain gains a platform recommended default: after the org per-agent choice and before the default `Chat` capability, the built-in definition's `inference_provider_id` applies, and `AgentRecommendedModelSeeder` fills it (only while NULL, only for an enabled, fully priced platform `Chat` model) from `Billing/agent-model-recommendations.seed.json`. Nothing else in the chain changes, and `GET /api/v1/org/agent-models` reports the outcome per agent.

Every `Resolved*` record keeps its positional members. `ProviderId` is documented as "the model capability id".
Each record gains optional trailing members `Guid? AccountId` (the provider) and `Guid? ModelId`. Call sites do
not change.

### 5.2 Fingerprint and cache

Fingerprint lines stay `{capability}:{ProviderId}:{ModelName}`. Ids and model codes are preserved, so every line
and every hash is byte-identical, and `StepCacheKeyInputs.SchemaVersion` stays `v5`. A golden test pins a
migrated capability's fingerprint line.

### 5.3 Price lookup (`IModelPriceService`, replaces `IRateCardService`)

`PriceAsync(PriceRequest)` with `ModelCapabilityId?`, `AccountId?`, `Kind`, `Capability`, `ModelCode`,
`Unit`, `Variant?`, `BillingMode`, `Quantity`, `At`, returning `PriceQuote(long CreditsMillis, decimal CostUsd,
Guid? PriceId)`. It picks the priced capability in this order:
1. the referenced capability, when its model code equals the model actually used (or none was reported);
2. a capability of the same `Capability` under the same provider whose model code equals the model used;
3. platform only: any platform capability of the same kind, `Capability` and model code (covers the legacy
   `AzureOpenAI:*` configuration fallback, which has no row);
4. fail-expensive: the highest-priced effective row for that unit among the provider's capabilities of that
   `Capability` (keeps the old `*`-wildcard intent, so an explicitly requested model never prices below the
   configured one). The picture editor does not lean on it for Platform rows: a requested model that is not a
   configured, fully priced capability of the job is refused (400) before any hold or vendor call.

"The provider" in steps 2 and 4 is the one the referenced capability belongs to now, and the request's `AccountId`
only when no capability is referenced: an id cached before a consolidation names a merged-away provider.

The variant is then chosen by exact match, else a null-variant row, else the highest variant. With no row on the
Platform mode, it throws `UnpricedUsageException`: the meter records the unit at 0 credits flagged `unpriced`
(error log, `reelbolt.billing.unpriced_usage`, coverage) and its settle charges at least the hold (§5.6). BYO and Runner
give 0. Credits are `round(qty × perUnit × multiplier, AwayFromZero)`, floored at 1 on Platform when > 0, where
`perUnit = override ?? cost × markup × 1000 ÷ credit_usd_value`. Snapshot cache 60 s, like rate cards.

**Prompt-length tiers.** `PriceRequest.PromptTokens` (and `UsageRecordRequest.PromptTokens`, forwarded by the
recorder) is the call's whole prompt length, cached input included. When it is positive and no variant was requested,
the variant step first takes the in-force row of the unit whose variant matches `^gt(\d+)k$` (case-insensitive) with
the largest N where `N × 1000 < PromptTokens`; otherwise it is unchanged. `MeteringChatClient` passes
`InputTokenCount` on all three token units (the Anthropic adapter sums uncached input, cache reads and cache writes
into it; OpenAI and Gemini prompt counts include cached tokens) and `MeteringDecisionClient` its input count; the
event records the tier as its variant. Pricing is not part of the step cache key, so `StepCacheKeyInputs` is untouched.

Oracle-priced capabilities (Higgsfield) are recorded with the USD the oracle quoted, converted at the same markup,
with `price_id` null. That is the only billed usage without a price row.

### 5.4 Writers

The chat, decision and media decorators pass `ProviderId` (capability), `AccountId`, `Capability` and the
variant (image tier from `BillingModelKey`) on `UsageRecordRequest`. Video generation passes the resolution as
the variant. `ComputeMetering` prices against the system compute capability with the tier as the variant
(initiative A's pixel scaling plugs in there). Storage stays unpriced (free, decision 9).

### 5.5 Coverage, estimates, holds

- `RateCardCoverage` becomes `PriceCoverage`: enabled platform capabilities missing an effective price for a
  required unit.
- `VideoGenerateCreditPricing`, `UsageHistoryCreditHoldEstimator` and `WorkflowCostEstimator` take
  `IModelPriceService`.
- `StepCreditDefaults` keeps its heuristic constants (initiative F owns them). Its references move to
  `PricingDefaults` (`CreditUsdValue = 0.006`, `DefaultMarkup = 1.2`).
- `CreditCurrency.CreditsFromUsd` (video budget caps) is unchanged and stays cost-based.

### 5.5.1 The design system

Static parse, import, export, libraries and drift are free: no model call and no charge
(`DesignSystemFreeOperationsTests`). The "Build brand and product profile" LLM fallback is ordinary Agent
steps priced by model code like any Agent step, and it runs only for the part the profile is missing (each
Conditional reads the Parse step's view by `sourceStepOrder`). The estimate is shown before the run: low =
the free path, high = every fallback step, and the 402 preflight requires every fallback step's hold. It is
bounded on the estimate's high figure by a dynamic cap (the cold-start high estimate of the reference profile-build template priced at the live prices of the models the resolver picks, times `DesignSystem:Build:FallbackCapMargin` (default 1.3), optionally lowered by `DesignSystem:Build:MaxFallbackCreditsHigh`; about 64, 503 and 1005 credits on deepseek-flash, Sonnet 5.5 and Opus 5.5 at the catalog prices of 2026-10-17, examples that move with prices; `503 ESTIMATE_UNAVAILABLE` when the reference cannot be priced from real prices), enforced
by one shared gate (`IDesignBuildEstimateGate`) on every route that starts the profile-build template
(`POST /design-system/builds`, generic Execute, Retry, `assistant/execute`, the assistant `ExecuteWorkflow`
tool in chat and over MCP), plus the per-step hold and settle; the settled total itself is not capped.

### 5.6 Unpriced capabilities fail closed

With `Billing:Enabled`, an enabled platform `Table` capability that is not fully priced (a price in force for every
unit it meters, `ModelCapabilityView.IsFullyPriced`; the enable gate uses the same rule) is not offered
(pickers), not resolved (explicit id → default → `MODEL_UNPRICED` refusal: REST 409 `{code, message}`, a plain
chat reply, an MCP `isError` result; a failed step keeps `failure_code = MODEL_UNPRICED` and is refunded) and cannot
be enabled, nor exposed by switching on its model or provider account, through the admin API or the assistant
(400 / refusal; oracle capabilities pass while the oracle answers a probe; a new one the price book cannot price is
created disabled). A metering-time lookup failure writes the row flagged `unpriced` at 0
credits, and its settle charges at least the hold. Estimates never return 0 for an unpriced Platform call. BYO,
Runner, oracle rows and billing off are unchanged. Details: docs/metering-and-billing.md, "Unpriced models fail
closed".

## 6. Admin API (platform `/api/v1/inference-providers`, org twin `/api/v1/org/inference-providers`)

| Method | Path | Notes |
|---|---|---|
| `GET`/`POST` | `/` | Providers with nested models and capabilities (prices on platform only). `POST` may carry the first models. |
| `GET`/`PUT`/`DELETE` | `/{id}` | `DELETE` returns 409 while any of its capabilities is a default. System provider: 409 on update or delete. `PUT` moving `endpoint` to another scheme/host/port while a key (including `env:`) is stored needs `apiKey` (new key, or `""` to clear): else 400 `code: API_KEY_REQUIRED_FOR_ENDPOINT_CHANGE`. A path change on the same origin keeps the key. LocalRunner rows (endpoint = device id, no key) are exempt. `apiKey: "env:"` is accepted only with an empty endpoint or https on the kind's vendor host (Anthropic, Gemini, DeepSeek, Higgsfield, ElevenLabs); else 400 `AMBIENT_KEY_ENDPOINT_NOT_ALLOWED` (also on create, `/test` and the assistant tools, which apply the same two rules, plus a kind change needing a key). |
| `POST` | `/{id}/models` | `{modelCode, displayName, isEnabled, capabilities:[{capability,isDefault,isEnabled}]}`. |
| `PUT`/`DELETE` | `/{id}/models/{modelId}` | |
| `POST` | `/{id}/models/{modelId}/capabilities` | Kind/capability rules unchanged. A new platform capability is auto-priced from the price book. |
| `PUT`/`DELETE` | `/{id}/models/{modelId}/capabilities/{capId}` | `isDefault: true` clears the others in (org, capability). |
| `POST` | `…/capabilities/{capId}/test` | Persists `last_test_*` on the capability. |
| `POST` | `/{id}/image-tools` | fal only: adds the default image-tool models under the same provider (no key copy). |
| `POST` | `/test` | Unsaved test (`kind, endpoint, apiKey, modelCode, capability, personalOAuthMode, providerId?` to reuse a saved key; with `providerId`, no `apiKey` and an `endpoint` on another origin: 400 `API_KEY_REQUIRED_FOR_ENDPOINT_CHANGE`). |
| `GET` | `/selectable?capability=` | Model capabilities: `id, name (display), capability, model, isPlatformManaged, kind, providerName`. |
| `GET` | `/status?capability=` | Unchanged semantics. |
| `GET` | `/consolidation-report` | Platform only (§4.2). |

Platform pricing (platform admin): `POST …/capabilities/{capId}/prices`, `POST …/prices/{priceId}/retire`,
`GET`/`PUT /api/v1/pricing/settings` (`markup`), `GET /api/v1/pricing/coverage`,
`GET`/`POST /api/v1/pricing/multipliers` (moved from `/api/v1/rate-multipliers`),
`GET /api/v1/pricing/price-book?kind=&capability=&model=` (form pre-fill). `/api/v1/rate-cards*` is removed.

Fields that held a provider row id keep their names and now carry a model capability id:
`PUT /api/v1/agents/{id}/inference-provider`, org agent overrides, voices, and every step-config `*ProviderId`.

**Assistant gated tools** keep their ten names (the count stays 60). The id they take is a model capability id,
the exact old meaning of "a provider row". `Create` gains an optional `providerId` that adds the model under an
existing platform provider instead of creating a new account. `Update` edits the capability, its model and its
provider. The approval card and the tool's summary list the sibling models that any account-level change affects
(endpoint, key, timeout, OAuth mode, or switching the capability on, which switches the account on), decided by one
shared predicate (`AssistantAdminTools.AccountChanges`); a name or model-code change names the model's other
capabilities, and the card never describes the system compute provider. `Delete` removes the capability, then the
model, then the provider, each only once it is empty; the card says when the account and its stored key go with
it. `List` returns the tree. Names and model codes over 200 characters are refused, as in the admin API.

## 7. Non-workflow charging (D)

### 7.1 Attribution

Every non-workflow call opens a `UsageScope`:

| Call | Source |
|---|---|
| MCP bridge `tool-call` | new `Mcp` |
| File summarizer | new `FileSummary` (org, project, no user) |
| Timeline proposals | new `TimelineAssist` |

`UsageSource` is append-only. `Metering:FailOnUnattributed` (true in tests) makes the recorder throw
`UnattributedUsageException` instead of writing an `Unattributed` row.

### 7.2 Policy (decision 9)

| Source | Policy |
|---|---|
| `Assistant` (chat, LookAtPicture), `Mcp` | Shared monthly allowance, then charged |
| `Editor` (picture AI ops, subject cut-out) | Charged per operation |
| `TimelineAssist` | Charged per proposal |
| `FileSummary`, `Indexing`, `DocsIndex`, `Storage` | Free (storage stays a quota) |
| `Workflow` (incl. DesignGraphic, revisions) | Unchanged: engine `CreditGate` |

Allowance: plan entitlements `assistantMonthlyCredits` (trial 20, creator 100, pro 300) and
`assistantMonthlyCreditsPerSeat` (team 100 × seats). Unlimited is only the explicit `assistantUnlimited: true`
(Enterprise); with billing on, a missing key, plan row or the default-entitlements fallback includes 0. The period is
the active subscription's current period, else the UTC calendar month. Used allowance, for charging, is the
authoritative `assistant_allowance_holds` counter (open holds plus settled consumption in the period, read and
written under the org's ledger lock); the summary still reports the `usage_events` sum. The charged part of a
call is the part of its credits that falls beyond the allowance.

### 7.3 Charge path (`NonWorkflowChargeService`, Inference API, a no-op when `Billing:Enabled` is false)

It reuses `CreditLedger` and its idempotency keys (`nwc-reserve|settle|release:{source}:{requestId}`), with the
same overdraft headroom as `CreditGate` (`min(estimate × 20 %, 100 credits)`, bound from the `Billing` section).
It fails closed (`GATE_UNAVAILABLE`).
- **Hold then settle** (Editor, TimelineAssist). Before the vendor call it reserves an estimate: one unit of the
  op's unit priced by `IModelPriceService` for editor ops, or for proposals the brief (≤ 2000 chars) plus the
  serialized view at ~4 chars a token plus the 2000 output tokens, priced on the resolved chat model, floored at
  `Billing:NonWorkflow:TimelineProposalHoldCredits` (default 5); no hold when the resolved chat or image provider
  is not Platform-billed (BYO, Runner). A proposal resolves its chat provider once and calls the model on that row;
  a hold-less call, or one whose hold reserved nothing (`NO_PLATFORM_COST`), that still meters Platform usage is
  charged after the call under its request id. It refuses with `402
  {code:"INSUFFICIENT_CREDITS", message, required, balance}` before calling (any other refusal is `503
  GATE_UNAVAILABLE`). After the call it settles with the scope's `RunningCreditsMillis`, capped at the hold plus
  its headroom (the rest is logged and counted on `reelbolt.billing.nonworkflow_settle_capped`), or releases on
  failure.
- **Hold, cut off, settle at the actual** (Assistant, Mcp). Before the call it holds
  `Billing:NonWorkflow:AssistantTurnHoldCredits` (default 5), allowance first, then a ledger reservation with no
  overdraft, under the org's ledger advisory lock, or `allowance left + available balance` when that is less but
  covers `AssistantMinTurnCredits` (default = the hold); refused (`INSUFFICIENT_CREDITS`) below that, when billing
  is on and there is no org, and with 429 `TURN_CONCURRENCY_LIMIT` when the org already has
  `MaxConcurrentTurnsPerOrg` (default 4) open holds (a BYO chat turn is exempt until its first Platform-billed
  tool call, which is admitted under the cap). Each model round-trip and tool call (chat) and the call (MCP) is
  pre-claimed at its worst case by `ClaimStepAsync` before it runs (round-trip: estimated input tokens of the
  request × 1.5 plus its output ceiling at the chat price, or the unpriced default when the chat model could not be
  resolved; tool calls by the one cost table below; a step that metered unpriced usage counts at its claim) and refused
  when metered + claimed + the step passes `min(AssistantTurnCeilingCredits (default 100), allowance free +
  available + headroom - other open turns' metered and claimed beyond their holds)` (`TURN_LIMIT` /
  `INSUFFICIENT_CREDITS`), the first round-trip included; a landed claim is replaced by the actual.
  `Assistant:MaxRoundTripInputTokens` (default 320k) and `Assistant:MaxToolCallsPerStep` (default 8) bound a
  round-trip, `LookAtPicture` sends at most 20 MB / 40 MP; `Assistant:MaxTurnSeconds` (default 1800, at most half
  of the 1-hour stale threshold) cancels it. Tool pre-claims: `AssistantPlatformTools.Tools` classifies
  every registered assistant / MCP tool as `Free` (claims 0, with a one-line why) or `PlatformBilled` (with the
  capabilities it can call), and a drift-guard test fails the build when a registered tool has no entry or an entry
  names no registered tool. Fail closed: a tool the table does not classify is claimed at the unpriced default and
  admitted under the BYO in-flight cap like a Platform-billed one. `LookAtPicture` claims its vision quote at the
  question plus `ImageTokensFor(pixels)` = max(4,000, ceil(pixels / 750)) input tokens (T-095, SEC-F8-4: the floor
  sits above the largest per-image charge of the priced vision vendors), the pixels being the larger of the sent
  layer's recorded and header size (the 40 MP cap, 53,334 tokens, when it cannot be sized), plus 1,024 output tokens.
  The sized layer is pinned into the call's arguments (the chat Gate and the MCP bridge invoke the tool with the same
  dictionary the claim saw) and its sized pixels are recorded as the look's bound: bytes that declare more, or that
  cannot be read against a known size, are refused in plain words and nothing is sent (T-095, SEC-F8-5).
  `ApplyRunPictureTool` claims max(unpriced default, the dearest estimate over one request per tier in force of its
  model plus the variant-less one, × the most images the operation returns; T-095, SEC-F8-6); every other Platform-billed tool claims the unpriced default. The chat ends a stopped turn with a normal message and a `data-credits` part; MCP
  returns an `isError` tool result in plain words. Settlement charges the metered actual: allowance first, then
  the full remainder in credits (below zero by at most the headroom plus one step's estimation error per open
  turn, `WorstCaseOvershootMillis`, counted); an
  unpriced Platform usage settles at `max(metered + its unpriced steps' claims, hold)`; a call that metered nothing
  releases. A turn's first paid claim marks its hold used; a settlement that fails twice records the metered actual
  on the open hold, apart from the exposure. Holds left open over an hour are swept (`Swept`) by the org's next
  turn through the settle path: one with a recorded actual is charged exactly that, one marked used at least its
  hold, one that metered nothing is freed; a hold from a closed allowance period is charged only its own allowance
  hold, then credits, never the current period's allowance; only a swept turn settles late, charged the rest of its actual
  as a hold of 0 under its own ledger key. Only billing off admits a turn with no hold; the explicit unlimited
  entitlement exempts the allowance only (a zero-credit hold, still pre-claimed, capped and counted in flight). Any
  other hold-less admitted turn is charged at settlement anyway (keyed on its request id). With billing
  on, a host that does not register `INonWorkflowChargeService` fails its boot.

`GET /api/v1/billing/usage` gains a `source` axis. `GET /api/v1/billing/summary` gains
`assistantAllowance {includedCredits|null, usedCredits, periodStart, periodEnd}`. The usage page shows spend by
source and the allowance meter.

## 8. What does not change

Fallback chains, the BYO entitlement rules, the kind/capability allowlists (three copies plus `Compute` for the
system provider only), approval-card gating, step-cache keys, the engine's `CreditGate`, `Billing:Enabled`
symmetry, storage quotas and the video budget gate's USD and GPU-minute caps.

## 9. Risks

- Upgrading changes billing on purpose: +20 % credits (markup) and per-resolution video prices. Initiative G
  must say so on the site.
- Consolidation is skipped for any key the key ring cannot decrypt. Those providers stay separate and keep
  working.
- Initiative A edits `ComputeMetering` (pixel scaling) and initiative F edits `StepCreditDefaults`. Whichever
  merges second rebases. Initiatives A, D and E all touch `plans.catalog.json` and its vendored copy.
