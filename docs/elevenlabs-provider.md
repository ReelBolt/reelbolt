# ElevenLabs provider (music and sound effects) - internal - DORMANT

> **Dormant (product decision 7: "ElevenLabs licence: fal only, now").** Music and sound effects go through fal's
> hosted ElevenLabs endpoints only (`docs/fal-ai-models.md` section 11). The `InferenceProviderKind.ElevenLabs`
> member stays (append-only, persisted by name) but is unreachable: `InferenceProvidersController.IsUnsupportedCombination`
> refuses it every capability (create and update fail with "The ElevenLabs provider kind is not offered ..."),
> `ProviderCapabilityUsage.ServableByKind[ElevenLabs]` is empty, the admin form does not offer the kind,
> `PlatformProviderSeeder` seeds nothing for it (no rows, no keys, `env:` no longer accepted for it), the resolver
> never returns an ElevenLabs row (a pre-existing one is logged and skipped) and `AudioGenerationClientFactory`
> throws for it. The client code (`ElevenLabsAudioGenerationClient`, its catalogue entries and price rows) is
> kept dormant, with its tests, so re-enabling it is a deliberate, reviewable change (the allowlist, the
> capability table, the seeder, the resolver, the factory and the web catalogue). The licence risk that remains
> through fal is must-confirm row 34 of `docs/fal-ai-models.md`. Everything below describes the dormant code.

## Exception: audio isolation via fal

A product decision narrows decision 7 for **one** use: hosted voice isolation (`InferenceProviderCapability.AudioIsolation`)
may use the ElevenLabs-based isolator **hosted on fal** (`fal-ai/elevenlabs/audio-isolation`). The product owner accepts the
licence risk: whether ElevenLabs' own terms bind a call routed through fal is still **unconfirmed** (must-confirm,
`docs/fal-ai-models.md` "Audio isolation"). Nothing else changes: the `InferenceProviderKind.ElevenLabs` kind stays
dormant and is refused every capability (including `AudioIsolation`) by `IsUnsupportedCombination`,
`ServableByKind[ElevenLabs]` stays empty, no row of that kind is resolved or seeded, and no direct ElevenLabs
isolation client exists. Isolation rides the `FalAi` kind and credential only.

The direct ElevenLabs route of product decision 2 (music and sound effects through fal **and** through ElevenLabs
itself), superseded by decision 7. Internal engineering doc: nothing here is a customer-facing claim, and **no call has been made with
a key** - every test replays recorded mocks
(`inference/tests/ReelBolt.WorkflowEngine.Tests/Inference/Fixtures/Audio/`, copied from the initiative's audio
fact sheet). The fal route is `docs/fal-ai-models.md` section 11.

## The kind

- `InferenceProviderKind.ElevenLabs` (appended last; persisted by name). Base URL `https://api.elevenlabs.io`
  (a row's endpoint overrides it; the docs mention regional servers, list unconfirmed).
- Authentication: header `xi-api-key: <key>`. The stored key is sent verbatim, except the literal admin-typed
  sentinel `env:`, which reads `ELEVENLABS_API_KEY`. A blank key, or `env:` with the variable unset, **throws**;
  nothing ever falls back to the ambient environment. The platform provider seeder accepts `env:` for this kind;
  organization rows never get it.
- Serves `MusicGeneration` and `SoundEffectGeneration` only (`InferenceProvidersController.IsUnsupportedCombination`
  is an explicit allowlist in both directions; `ProviderCapabilityUsage.ServableByKind` mirrors it, test-enforced).
- Client: `ElevenLabsAudioGenerationClient` (plain `HttpClient` built by `AudioGenerationClientFactory`, row
  timeout per request). Resolution: `ResolveMusicGenerationAsync` / `ResolveSoundEffectGenerationAsync` - step
  provider id, then the capability's default row, then none (no legacy fallback).

## Models

| `model_id` | Job | Role | Route | Duration the platform requests |
|---|---|---|---|---|
| `music_v2_5` | Music | default | `POST /v1/music` | 3-300 s (prompt mode, required) |
| `music_v2` | Music | alternative | `POST /v1/music` | 3-300 s |
| `eleven_text_to_sound_v2` | Sound effect | default | `POST /v1/sound-generation` | 0.5-30 s, or unset (model picks) |

`music_v1` is not offered: it is deprecated, and it is **still the API default**, which is why `model_id` is
always sent.

## Wire contract

- `output_format` is a **query parameter** (`?output_format=mp3_44100_128`), never a body field. Offered: the
  MP3 and Opus tokens only (raw `pcm_*`/`ulaw`/`alaw` cannot be probed for a duration).
- `POST /v1/music` body: `prompt` (max 4100) **xor** `composition_plan`; `music_length_ms` and
  `force_instrumental` with a prompt only; `model_id`. The plan for `music_v2`/`music_v2_5` is the chunks shape
  `{chunks: [...]}` (1-30 chunks; chunk = `text` at most 6132 chars and `duration_ms` 3000-120000 required,
  `positive_styles`/`negative_styles` at most 50 each, `context_adherence` low/medium/high). `conditioning_ref`
  and `condition_strength` exist in the vendor schema but their types were not read, so they are refused. A
  sections (v1) plan is refused. `seed`, `store_for_inpainting`, `sign_with_c2pa`, `finetune_*`,
  `generation_mode`, `lyrics_text` are not sent.
- `POST /v1/sound-generation` body: `text` (required), `duration_seconds` 0.5-30 or null, `prompt_influence`
  0-1, `loop`, `model_id` `eleven_text_to_sound_v2`.
- Response 200: raw audio bytes (`audio/*`), headers `song-id` (music), `character-cost` (SFX),
  `current-concurrent-requests` / `maximum-concurrent-requests`. **No duration**: the engine probes the file
  (`AudioGenerationDurationProbe`, ffprobe) and fails rather than metering zero. A 200 that is not audio fails.

## Errors

`{"detail": {type, code, message, status, request_id, data}}`, or the validation shape
`{"detail": [{loc, msg, type}]}`. `AudioGenerationException.Kind`:

| Answer | Kind | Code |
|---|---|---|
| 401, 403 (and 404) | `Configuration` | `AUDIO_PROVIDER_CONFIG` |
| 402 `payment_required` | `VendorQuota` | `AUDIO_VENDOR_QUOTA` |
| 400, 422, any `detail.status` `bad_prompt` / `bad_composition_plan` | `UserInput` (with `Suggestion` = `data.prompt_suggestion` or the suggested plan JSON) | `AUDIO_BAD_REQUEST` |
| 429 (`concurrent_limit_exceeded`, `rate_limit_exceeded`, legacy `too_many_concurrent_requests` / `system_busy`), 5xx | `Retryable` | `AUDIO_VENDOR_RETRYABLE` |
| anything else, or a 200 that is not audio | `VendorFailed` (not retried: billing unknown) | `AUDIO_VENDOR_FAILED` |

Messages are bounded and never carry the key.

## Pricing (fail closed)

`ModelPriceCatalog.Audio` + `AudioModelCatalog` (test-held equal). From elevenlabs.io/pricing/api (read
2026-10-17): Eleven Music **$0.15 per minute** -> `MusicGenMinutes` $0.15, each started minute;
Sound Effects **$0.12 per minute** -> `SoundEffectGenSeconds` $0.002, each started second. **Must measure:**
the same page's FAQ says "Music and Sound Effects are billed per generation", so the unit is ambiguous; both
entries carry `PriceMustBeMeasured`. `AudioGenerationCapabilities.EstimateUsd` throws on an out-of-range
duration or a non-positive estimate, so an unpriceable model is unofferable (`CONFIG_INVALID`, no ledger row).
Billed in USD by ElevenLabs, not credits; plan allowances (Starter 40 music min / 150 SFX generations ...) are
in the fact sheet.

Not done here: the versioned model-catalog seed (`model-catalog.seed.json`, built from
an operator document by an operator file) has no audio entries yet,
so an audio row is priced from the price book when an admin creates it, not by the catalog upgrader.

## Provider test

One `GET /v1/models` (free; generates nothing). 2xx = ok, 401/403 = rejected key.

## Licence (unresolved - flag, do not decide)

From elevenlabs.io/music-terms (summary, not legal advice): output belongs to the customer; commercial media use
needs a **paid** plan (API access starts at Starter; Free has no API and requires attribution); tiers Free to
Pro are individual use only, Scale under 10 employees, Business under 50; film/TV/radio/games media rights are
excluded below Enterprise; building a music library for third parties and **reselling or repackaging the
models** need written authorisation; prompts naming real artists or quoting lyrics are prohibited.

**UNCONFIRMED:** whether a multi-tenant SaaS may sell ElevenLabs-generated music inside its customers' videos
under a standard API plan ("individual use", "model resale"), and whether the same terms bind calls routed
through fal. This needs a written answer from ElevenLabs or counsel before launch. Nothing in the code decides it.

## Must measure with a real key

1. Real `/v1/music` maximum (5 min per the docs vs 600000 ms per the OpenAPI); the platform caps at 300 s.
2. The billing unit of music and SFX (per minute vs per generation); the cards assume per started minute/second.
3. The HTTP status of `bad_prompt` / `bad_composition_plan` (400 likely; the client classifies by `detail.status`).
4. SFX concurrency limits (not listed); music concurrency is Starter-Pro 2, Scale/Business 5.
5. WAV request syntax (the pricing page mentions WAV 48 kHz for non-looping effects).
6. Whether `POST /v1/music/plan` is billed (not used).
7. That `GET /v1/models` answers 2xx for every paid plan key (the probe's assumption).
