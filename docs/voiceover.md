# Voiceover

Text-to-speech narration for ReelBolt workflows — a deterministic, non-LLM `StepType.Voiceover`
step that synthesizes one WAV per configured script line through a Fish Audio provider, and a
`VideoCompile` mix path that lays those WAVs onto the compiled video's audio track next to the
dialogue, background music and sound effects.

Phase 2 adds narration *planning* on top of that spine: a `NarrationWriter` agent that writes the
words and anchors each line to an offered cut-anchor id, deterministic word alignment over each
synthesized line, and a deterministic fit solver whose overflow facts reach the review loop. The
spine is unchanged — an agent chooses opaque ids and prose, and code measures and mixes; nothing on
this path judges audio with a model.

This document is the reference for that feature as built in phase 1, plus phase 2's narration
planning. For the surrounding workflow engine (step types, executors, inference-provider resolution
in general) see `CLAUDE.md`; for the video-editing feature the compile path extends, see
`docs/video-editing.md`. What phase 1 left out, and which of it phase 2 now delivers, is enumerated
at the end, under
[Phase 1 scope, and what phase 2 delivers](#phase-1-scope-and-what-phase-2-delivers).

---

## Table of Contents

- [The shape](#the-shape)
- [The Fish Audio provider](#the-fish-audio-provider)
- [Fish Audio transcription](#fish-audio-transcription)
- [Narration planning (`NarrationWriter`)](#narration-planning-narrationwriter)
- [Config reference](#config-reference)
- [Word alignment](#word-alignment)
- [The fit solver and the review loop](#the-fit-solver-and-the-review-loop)
- [Step output and error codes](#step-output-and-error-codes)
- [Artifacts, caching and where the audio lives](#artifacts-caching-and-where-the-audio-lives)
- [The mix path (`VideoCompile`)](#the-mix-path-videocompile)
- [Narration and the end of the program](#narration-and-the-end-of-the-program)
- [The promo pipeline path (`ScriptScenes`)](#the-promo-pipeline-path-scriptscenes)
- [Text sanitization](#text-sanitization)
- [Per-line artifact resolution](#per-line-artifact-resolution)
- [Admin surfaces](#admin-surfaces)
- [Phase 1 scope, and what phase 2 delivers](#phase-1-scope-and-what-phase-2-delivers)
- [Phases 3 and 4: what landed after narration planning](#phases-3-and-4-what-landed-after-narration-planning)
- [Story-first narration and the narrated templates](#story-first-narration-and-the-narrated-templates)
- [Local fish-speech servers and endpoint rules](#local-fish-speech-servers-and-endpoint-rules)

---

## The shape

Two things were added, and nothing else: a step that *produces* narration audio, and a flag on the
existing compile step that *consumes* it.

```
┌────────────────────┐      ┌──────────────────┐      ┌────────────────────┐
│  StepType.         │      │  object storage  │      │  StepType.         │
│  Voiceover         │─────▶│  voiceover/      │─────▶│  VideoCompile      │
│                    │      │  lines/          │      │                    │
│  deterministic,    │      │  {sha256}.wav    │      │  EnableVoiceover   │
│  Fish Audio TTS    │      └──────────────────┘      │  VoiceoverStepOrder│
└────────────────────┘                                └────────────────────┘
   emits {view, meta}                                     resolves each line
   + a JSON artifact                                      to an OUTPUT-timeline
                                                          moment, then amixes
```

There is no LLM anywhere on this path. `VoiceoverStepExecutor` sends text to a provider and probes
what comes back; `VideoCompileStepExecutor` reads the step's own output JSON and builds ffmpeg
arguments from it. No audio content is ever judged by a model, the same discipline
`docs/video-editing.md` applies to `VideoAnalyze`.

The step type is `StepType.Voiceover` (`ReelBolt.Shared/Data/Models/Enums.cs`). Like
`VideoAnalyze`/`VideoCompile`, it is a deterministic step whose `WorkflowStep.AgentDefinitionId` FK
is satisfied by the existing built-in `AgentType.VideoTransform` placeholder row — no new
`AgentType` was introduced for it.

Phase 2 adds exactly one thing *upstream* of that diagram and changes nothing inside it: a
`NarrationWriter` **agent** step that decides what the narration should say, plus the
`VoiceoverSourceKind.NarrationPlan` source kind that feeds that plan into the same step. The step
itself stays deterministic — the model writes words and names offered ids, and everything that
measures, mixes or positions audio is still code. A `NarrationWriter` step is an ordinary `Agent`
step; `AgentType` is appended, never reordered.

That split is the point of the design, so state it plainly: a `NarrationPlan` line carries **no
start time and cannot carry one**, and a plan cannot know where the picture will land. The step
therefore *declares* the line positions itself — narration-first, back-to-back in plan order — and
the compile step *measures* the picture against the result afterwards. Those are two different
measurements made by two different components, neither reading the other; see
[The fit solver and the review loop](#the-fit-solver-and-the-review-loop).

---

## The Fish Audio provider

`InferenceProviderKind.FishAudio` is a provider kind that serves exactly two capabilities,
`InferenceProviderCapability.SpeechSynthesis` and
`InferenceProviderCapability.Transcription` — Fish Audio publishes a text-to-speech endpoint and a
speech-to-text endpoint and nothing else. **Both halves of that pairing are enforced at the API
boundary** (`InferenceProvidersController.IsUnsupportedCombination`): a `FishAudio` row with any
capability other than those two is rejected with a 400, and any other kind with `SpeechSynthesis` is
rejected with a 400. The rule is written as one negative test against the two capabilities the kind
*does* serve, not as a positive rejection per other capability, because that method falls through to
ALLOW and the positive form would silently admit every capability later added to the enum.
`SpeechSynthesisClientFactory` and `TranscriptionClientFactory` each throw a `NotSupportedException`
as a second line of defence, the way `TranscriptionClientFactory` does for Anthropic.

This section covers the **text-to-speech half** (`POST /v1/tts`). The speech-to-text half
(`POST /v1/asr`) is its own section — see [Fish Audio transcription](#fish-audio-transcription).

### Wire format

`FishAudioSpeechSynthesisClient` (`ReelBolt.Shared/Inference/FishAudioSpeechSynthesisClient.cs`)
calls **`POST https://api.fish.audio/v1/tts`** with `Authorization: Bearer <key>` and a JSON body.
Every fact below was checked against Fish Audio's primary OpenAPI schema rather than against the
feature request that proposed them, and several of the request's original assumptions turned out to
be wrong — the corrections matter, because code written against the wrong shape would fail at the
boundary and look like a credential problem.

- **Model selection is a request HEADER** (name: `model`), not a body field. The client sets it with
  `httpRequest.Headers.Add("model", model)`.
- **The model enum is `s1`, `s2-pro`, `s2.1-pro` (default), `s2.1-pro-free`, `drama-3-preview`.**
  There is no `openaudio-s1` value; the request's guess was wrong.
- **`reference_id`** selects a stored voice. Phase 1 uses `reference_id` only.
- **`format`, `sample_rate`, `latency`** are top-level body fields. ReelBolt sends `format: "wav"`,
  `sample_rate: 48000`, `latency: "normal"`.
- **`speed`, `volume` and `normalize_loudness` are nested under a `prosody: { … }` object**, NOT
  top-level request fields — another correction. ReelBolt sends
  `prosody: { speed: 1, volume: 0, normalize_loudness: true }`. (A top-level `normalize` field does
  exist, but means something different: text normalization for numbers, English/Chinese only — not
  audio loudness.)
- **Expressiveness tag syntax depends on the model and is not uniformly open-ended.**
  - The **S2 family** (`s2-pro`, `s2.1-pro`, `s2.1-pro-free`) and `drama-3-preview` use
    **`[bracket]`** syntax over open-ended natural language (e.g. `[whispers sweetly]`).
  - **`s1`** instead uses **`(parenthesis)`** syntax over a **fixed, documented vocabulary of 64**
    expressions (24 basic + 25 advanced + 5 tone + 10 audio-effect) — not open-ended.
  - Phase 1 ships no free-text emotion/delivery control, so this matters only to the text
    sanitizer, which must therefore strip **both** `[...]` and `(...)` forms. It does — see
    [Text sanitization](#text-sanitization).
- **Pricing is exactly $15.00 per M UTF-8 bytes** for `s2.1-pro` / `s2-pro` / `s1`, and **$0.00**
  for `s2.1-pro-free`. This is why the step's byte budget is measured over sanitized text
  (`VoiceoverStepConfig.MaxBytes`), not over an estimated duration.
- **There is no official .NET/C# SDK** — Fish Audio ships Python and JavaScript only — so the plain
  `HttpClient` in `FishAudioSpeechSynthesisClient` is the intended integration, not a shortcut.
  Because phase 1 uses `reference_id` alone (no zero-shot `references` array), a **JSON body is
  sufficient and no MessagePack NuGet package is needed at all**, keeping the repository's
  "add no new NuGet dependency if avoidable" constraint satisfied. MessagePack remains an option
  Fish Audio advertises; it is simply not required for what phase 1 sends.

### Licence: two separate things

The licensing position is more favourable than "the free tier is non-commercial", and the two
things that phrasing conflates are actually separate:

> Fish Audio's hosted API, including the free `s2.1-pro-free` tier, has no documented
> non-commercial restriction — it's rate/SLA-limited only. Self-hosting Fish's open-weight models is
> licensed under the Fish Audio Community License Agreement (v1.0): free for research and non-commercial use
> and for limited commercial use below its annual-revenue threshold; above the threshold, and for offering a
> hosted service, a separate license from Fish Audio is required.

Concretely:

- **The hosted API — including the $0 `s2.1-pro-free` tier — carries no documented non-commercial
  restriction.** Fish's own documentation recommends `s2.1-pro-free` for "testing, prototyping,
  development, and smaller businesses"; the only thing it lacks is an SLA (latency/uptime)
  guarantee. There is no "free plan = non-commercial" clause for API access. **The framing "the
  free plan is non-commercial" is verified incorrect** and must not be used.
- **What *is* non-commercial-only is self-hosting Fish's open-weight models** (`fishaudio/fish-speech`).
  Its current licence — checked directly against the repository's `LICENSE` (version 1.0, last updated
  October 13, 2026) — is the **Fish Audio Community License Agreement**. (The model cards on Hugging Face
  still list `CC-BY-NC-SA 4.0`; read both.) The agreement is free for Research and Non-Commercial use and
  for limited commercial use while the licensee's annual revenue is below its Revenue Threshold; above the
  threshold, any other Commercial Purpose and any Hosted Service require a **separate written licence from
  Fish Audio** (`business@fish.audio`).

ReelBolt talks to the hosted API, so the second bullet only becomes load-bearing if someone points
an `Endpoint` at a self-hosted `fish-speech` instance. The admin form
(`web/components/admin/InferenceProviderForm.tsx`) renders the notice above whenever `Kind` is set
to `FishAudio`, so the distinction travels with the provider row rather than living only here.

### Resolution

Speech synthesis resolves through `IInferenceProviderResolver.ResolveSpeechSynthesisAsync`, and —
unlike transcription and vision — the choice is **health-aware** (`ProviderHealthSelector`, shared
library):

`VoiceoverStepConfig.ProviderId` (always honoured, healthy or not) → the single enabled row with
`IsDefault = true AND Capability = SpeechSynthesis`, **unless its last connection test failed**
(`LastTestOk == false`; a never-tested default is trusted) → the enabled SpeechSynthesis row whose last
test passed, most recently tested first → the default anyway (it may have recovered) → **none**.

This exists because a deployment's default (`local-fish-tts`) had failed its test while a working row
(`qa-local-fish`) sat unused, and every narrated video was synthesized against the broken one with no
warning. The resolved provider carries `SelectionReason`, `LastTestOk` and `PassedOverDefaultName`, and
the `Voiceover` step turns them into top-level output `warnings` (the C1 shape, present only when
non-empty): `voice_provider_fallback` when a healthier row was used instead of the default, and
`voice_provider_unhealthy` when the row that narrated had failed its last test. The resolver caches rows
for `Inference:ProviderCacheSeconds` (60 s), so a re-test takes effect within a minute. A step-result cache hit replays a recorded narration; it drops the two provider-health warnings from the replayed output (`WithoutProviderHealthWarnings`), because nothing was synthesized now and the recorded notice would otherwise survive a passing re-test. Warnings about the narration itself (`voice_language_unlisted`) stay. Health-aware
choice is deliberately **not** applied to chat, video generation (another row is another vendor's bill)
or embeddings (another model is another vector space).

`GET /api/v1/inference-providers/status?capability=SpeechSynthesis` reports the same choice to any
signed-in user — `{ capability, providerName, lastTestOk, lastTestAt, healthy, usingFallback, message }`,
never an endpoint, model, key or raw error — so the step settings can warn before an hour-long run
(web helper `getProviderStatus`). Other capabilities report their default row.

There is deliberately no legacy-config fallback, for the same reason transcription and vision have
none: silently sending text to a chat deployment would produce a confusing 404 rather than a clear
"no speech provider configured". The explicit-id branch also re-checks
`Capability == SpeechSynthesis` before honouring the id — a chat provider id authored directly into
a `VoiceoverConfigJson` blob (bypassing the UI, which only offers speech rows) falls through to the
default instead of being handed to the speech client factory.

The resolved provider is a `ResolvedSpeechSynthesisProvider` — its own type, not the shared
`ResolvedInferenceProvider`, since a chat, a transcription and a speech-synthesis resolution are
never interchangeable. It carries a `CacheKey` hashed over `Kind|Endpoint|ModelName|SHA256(ApiKey)|
TimeoutSeconds` so the same configuration reuses one client, and it overrides `ToString()` so a
stray `LogError("… {Provider}", provider)` cannot print the API key.

## Fish Audio transcription

This section describes the speech-to-text half of the `FishAudio` provider kind. It stands alone:
nothing here depends on the text-to-speech half or on any other section of this document.

### What it is

`InferenceProviderKind.FishAudio` can serve `InferenceProviderCapability.Transcription`. A
`Transcription` capability on a model (for example `transcribe-1-pro`) under a Fish Audio provider —
the same provider, and key, as its speech models — makes Fish Audio's hosted ASR API available to the two
consumers of transcription in the platform: `VideoAnalyze` (which transcribes the audio it extracts
from a source video) and voiceover word alignment. Both reach it through the same
`ITranscriptionClientFactory` every other ASR backend goes through, so nothing downstream knows or
cares which vendor produced a transcript.

`FishAudioTranscriptionClient` (`ReelBolt.Shared/Inference/FishAudioTranscriptionClient.cs`) is the
implementation. It is a plain `HttpClient` + `System.Text.Json` client with **no new NuGet
dependency** — Fish Audio ships no official .NET SDK, and the repository's package set
(`Microsoft.Agents.AI.*` / `Microsoft.Extensions.AI.*` / `OpenAI` / `Azure.AI.OpenAI` / `Anthropic`)
moves together, so an unrelated package is not a free addition.

### Wire format

**Every fact on this page is taken from Fish Audio's own published OpenAPI document at
`https://api.fish.audio/openapi.json`** — OpenAPI 3.1.0, `info.title` "FishAudio OpenAPI",
`info.version` "1", path `/v1/asr`, operation `post`, summary **"Speech to Text"** — and not from a
blog post, a wrapper library, or inference. It is the same primary source the TTS half was checked
against, and it is reachable directly (the rendered docs site is client-side, so the specification
document is the reliable form).

**The request ReelBolt sends:**

| | |
|---|---|
| Method + path | `POST {endpoint}/v1/asr` (default endpoint `https://api.fish.audio`) |
| Auth | `Authorization: Bearer <the key stored on the provider row>` |
| Model | the **`model` request header** (not a body field) — `transcribe-1` or `transcribe-1-pro` |
| Body | `multipart/form-data` |
| `audio` | required, the WAV chunk (binary) |
| `ignore_timestamps` | sent explicitly as `false` |
| `language` | omitted entirely when no hint is set |

Two details are easy to get wrong and are therefore worth stating plainly:

- **Model selection is a request header**, exactly as it is for text-to-speech, not a form field.
  The vendor's schema gives the header `default: "transcribe-1"` and
  `enum: ["transcribe-1", "transcribe-1-pro"]`. The client uses the provider row's `ModelName` when
  it is non-empty and falls back to `transcribe-1`, resolved through the public static
  `FishAudioTranscriptionClient.ResolveEffectiveModel` so the value sent and the value recorded
  cannot drift.
- **`ignore_timestamps` defaults to `true` in the vendor's schema**, which means the default request
  comes back *without* the precise timings this platform needs. Fish's own description of the field:
  "Whether to return precise timestamps in the text, this will increase the latency in audio shorter
  than 30 seconds". ReelBolt always wants segments, so the client sends `false` explicitly rather
  than relying on a default that would silently degrade every transcript to untimed text.

**The response ReelBolt parses** — the vendor's 200 schema is flat JSON:

| Field | Required | Mapped to |
|---|---|---|
| `text` | yes | `TranscriptResult.Text` |
| `duration` | yes | (not used) |
| `segments[]` | yes | `TranscriptResult.Segments` — each `ASRSegment` is exactly `{text, start, end}` |
| `language_code` | no | `TranscriptResult.Language` (ISO 639-1, e.g. `en`) |
| `language` | no | fallback for `TranscriptResult.Language` (display name, e.g. `English`) |

The vendor is explicit that `language_code` is "the field to use in code" and `language` is "for
display only", so the code is preferred and the name is only a fallback for a response carrying one
and not the other.

Error responses (the document defines 401, 402 and 503) are `{status, message, reason?}`; the client
unwraps `message` and then `reason`, falls back to the raw body for anything that is not JSON, and
suffixes the HTTP status — the same vocabulary the TTS half uses, so an admin does not have to learn
two error dialects for one vendor. The body read is bounded (4 KiB for an error, 4 MiB overall) and
the message is truncated, so a hostile or broken endpoint cannot push an unbounded string into a log.

### Two things this endpoint does not give you

The vendor's schema is the authority here, and two gaps matter to callers:

- **There are no word-level timestamps.** `ASRSegment` is exactly `{text, start, end}` and the 200
  schema carries no words array, so `ITranscriptionClient.TranscribeAsync`'s `wordTimestamps`
  argument is accepted and deliberately ignored. The client returns an **empty** word list rather
  than interpolating word timings from segment bounds, which would be fabricated data wearing the
  costume of a measurement. Voiceover word alignment therefore has nothing to align against on this
  provider and degrades to its unaligned path; a workflow that needs per-word timings should use a
  Whisper-family Transcription provider for that step.
- **There are no Whisper confidence figures.** `no_speech_prob`, `avg_logprob` and
  `compression_ratio` are not in Fish's segment schema, so `TranscriptSegment`'s three optional
  fields stay `null` — the value that means "the backend does not report this". They are not
  defaulted to `0`, which would read as a confident transcription of speech and would defeat the
  check `VideoAnalyze` uses to discard segments recognised from music or silence.

### Credentials

A `FishAudio` transcription row sends **the key stored on the row, verbatim**, as
`Authorization: Bearer`. There is **no `env:` sentinel** on this kind and nothing is ever read from
the container environment, so a misconfigured row can never spend the host's own credential.

**A blank key throws.** `TranscriptionClientFactory.BuildFishAudio` raises an
`InvalidOperationException` when the row's key is empty or whitespace-only, rather than degrading to
an unauthenticated request. This deliberately differs from the OpenAI-compatible transcription arm
and from the text-to-speech half of this same kind, both of which treat a blank key as the
legitimate no-key self-hosted case: Fish's ASR endpoint is a hosted, key-authenticated service and
no documented no-key self-hosted contract exists for it, so an empty key can only mean an empty form
field or a key that failed to decrypt. Throwing where the client is built is what turns that into a
clear message instead of a vendor 401 three steps into an expensive workflow. A blank **endpoint**
throws for the same reason.

The stored key is encrypted at rest with ASP.NET Core Data Protection (`ISecretProtector`) like every
other provider key; nothing in this path bypasses that.

### Endpoint rules

`FishAudio` is one of the three provider kinds **exempt from the private-address endpoint guard**
(`InferenceProvidersController.IsDisallowedEndpoint`; the others are `OpenJev` and
`OpenAICompatible`), because a self-hosted or proxied Fish Audio server on a docker service name, a
LAN address or `host.docker.internal` is a first-class target rather than an SSRF attempt. The
exemption is **kind-aware, not capability-aware**, so a `FishAudio` row serving `Transcription` gets
exactly the same latitude as one serving `SpeechSynthesis` — and exactly the same restriction: the
exemption applies to platform-managed rows only. A row an *organization* creates is refused a
private address whatever its capability, because a tenant aiming a provider at internal addresses is
SSRF rather than self-hosting.

`TranscriptionClientFactory.BuildFishAudio` additionally builds its `SocketsHttpHandler` with
`AllowAutoRedirect = false`. The endpoint was validated once, at write time; following a 3xx would
hand the request — and the `Authorization` header with it — to a second host that was never
validated at all. A redirect surfaces as an ordinary non-success status the caller can report.

### Licence: the same two things as text-to-speech

The commercial-use position for ASR is the one already documented for TTS, because it is the same
vendor and the same hosted service:

> Fish Audio's hosted API, including the free tier, has no documented non-commercial
> restriction — it is rate/SLA-limited only. **Self-hosting** Fish's open-weight models is a
> separate thing: those are licensed under the **Fish Audio Community License Agreement**, free for
> research and non-commercial use and for limited commercial use below its revenue threshold; beyond
> that, and for any hosted service, a separate licence from Fish Audio is required.

ReelBolt talks to the hosted API by default, so the self-hosting caveat becomes load-bearing only if
someone points a row's `Endpoint` at their own `fish-speech` instance. Note the limit of what is
verified: Fish's own OpenAPI document covers the hosted API, and **whether a self-hosted fish-speech
server implements `/v1/asr` at all is not something this document establishes** — the self-hosted
contract ReelBolt relies on elsewhere is the `/v1/references/*` reference library, not ASR.

---

## Narration planning (`NarrationWriter`)

Phase 1 narrated text a human wrote. Phase 2 adds the agent that writes it: `AgentType.NarrationWriter`,
an LLM agent whose output is a **plan**, not audio — `NarrationPlanOutput`, the seventh schema in the
house's id-anchored, number-free family alongside `VideoEditDecisionOutput`,
`MotionGraphicsPlanOutput`, `MusicPlanOutput`, `SfxPlanOutput`, `ColorGradePlanOutput` and
`GeneratedShotPlanOutput`. Those six plus `NarrationPlanOutput` are the invariant-test files that
guard such a schema; `RoomConveneGateInvariantTests` also carries the name but is not one of them —
it guards the decision gate, not an agent output.

It is appended **last** in `AgentType` and adds no step type of its own: a narration plan is an
ordinary `Agent` step. Enums in this repository are appended, never reordered.

**Numbers in the spoken text.** The schema carries no numeric PROPERTY, but the spoken `text` may
contain digits, and the prompt (both copies) asks for years and large numbers as digits ("1989",
"14 mila"): Fish reads digits with the right stress, while a long number spelled as one word
("millenovecentottantanove", "quattordicimila") was stressed wrong by every Italian voice tried. A
brief that says how to write numbers wins. The prompt used to ask for every quantity as words.

### The schema cannot carry a number

```csharp
public sealed record NarrationPlanOutput(List<NarrationLine> Lines, string PlanRationale);
public sealed record NarrationLine(string AnchorId, string Text, string Delivery, string Reason);
```

Every property on both types is a plain `string` or a `List<NarrationLine>`. `AnchorId` names an
**offered** cut-anchor id — an `s{n}` shot, `g{n}` gap or `t{n}` transcript span that the
`VideoAnalyze` step's bounded view actually offered, the same vocabulary `SfxCue.AnchorId` and the
graphics planners draw from. `Delivery` is prose describing how the line is read; it is deliberately
not an enum-mapped setting and carries no rate, pitch or dB value. An **empty `Lines` list is a
valid outcome** — a silent stretch is allowed, and the `Voiceover` step completes on one rather than
failing; see [An empty plan is a successful step](#an-empty-plan-is-a-successful-step).

Stated as the guarantee it is: a narration plan is **structurally incapable of naming a timestamp or
a coordinate**. There is no field it could put one in, so a line that wanted to say "0:07" or
"1280,720" has to name an offered anchor id instead and let the deterministic half do the arithmetic.

`NarrationPlanOutputInvariantTests` is the guarantee, and it is a reflection test rather than a
review habit: it enumerates the schema's properties and fails on any
`int`/`long`/`float`/`double`/`decimal`/`TimeSpan`/`DateTime`/`DateTimeOffset` property anywhere in
it, **and** on any `string` property whose *name* is time-shaped (`Sec`/`Seconds`/`Ms`/`Millis`/
`Time`/`Timestamp`/`Duration`/`Frame`/`Frames`/`Offset`/`Start`/`End`). A `NarrationPlanOutput` that
grows a `double`, or a string that merely *sounds* like a time, fails the wave rather than a review.
It also pins both property sets and round-trips camelCase JSON, so a renamed member is a test failure
rather than a silently ignored field.

### Tool scope: read-only, deliberately

`ToolGroupCatalog.GroupsFor(AgentType.NarrationWriter)` is exactly `[ToolGroup.ProjectRead,
ToolGroup.WorkflowControl]` — the minimal read-only set `VideoStoryEditor` has, with no
`SandboxAuthoring`, no `SandboxRender` and no `ProjectWrite`. An agent that writes prose about a
picture has no reason to touch a file or start a render, and its prompt does contain media-derived
content (on-screen text, transcript spans), which is the one place in this repository where
read-only tools are the *point* rather than a default. The grant is written as its own explicit arm
in the catalog rather than left to a default, so a future default change cannot silently widen it.

`NarrationWriterAgent.DefaultPrompt` and its `DatabaseSeeder.BuiltInAgents` entry are byte-identical,
asserted by a `[Fact]` in `VideoStoryEditorPromptConsistencyTests` — the test run is the proof, not a
visual diff. That duplication is what every built-in agent carries, and it is why a prompt edit
changes both sides in one commit.

---

## Italian (and other non-English) narration

Why Italian narration rarely lands on the first try (repo-only analysis):

1. The step language (or, when the step has none, the run brief language) is sent to Fish Audio as `language`, mapped to a BCP-47 tag Fish documents (`it` -> `it`, `it-it` -> `it-IT`; a language Fish does not list is omitted and Fish auto-detects). It only drives how Fish reads numbers and dates; it does not change stress or accent. It also drives the `voice_language_unlisted` warning and the per-language track. For Fish, the sent tag is part of each line's content key (a line sent without one keeps its old key).
2. `pronunciationDictionary: Auto` (IPA, which Fish ignores for Italian) only fires for analysis-derived English tech terms (the first-party IPA table, about 60 entries). It is `Off` by default and never fires for Italian prose.
3. Before this change nothing normalised Unicode: decomposed accents from a model (`e` + U+0300) stayed decomposed. `VoiceoverTextSanitizer.Sanitize` now NFC-normalises first; NFC input is unchanged, so existing line content keys are stable.
4. There was no author-supplied stress or phoneme control.

Measured result (2026-10-25, Fish `s2.1-pro`, a cloned voice, the sentence "Lo studio di Novara offre garanzie complete."): written accents in the text (`Novàra`, `garanzìe`) gave the right stress. A pronunciation dictionary with the same respelling, a dictionary with IPA, a dictionary with Arpabet, and the `language: it` field did NOT. (`language` only changes how numbers and dates are read, per the Fish docs.)

Recommended practice:

- Pick a voice whose library language matches the narration language.
- Put the words the voice misstresses in the project's pronunciation glossary (next section), written with an accent on the stressed syllable (`Novara` -> `Novàra`). This is the only mechanism measured to work for Italian on Fish.
- `pronunciationOverrides` (IPA through the provider dictionary) does NOT work for Italian on Fish: do not use it for Italian. It stays for other providers and languages, where it is unchanged (term 2 to 60 characters, `ipa` 1 to 120 characters, at most 64 entries, no duplicate terms; a bad entry is a 400 on save naming the field).
- Keep the authored accents (`è`, `perché`); never strip them.

Not done: the voice-language warning does not name the voice, because the step has no voice-library language data without a new network call.

## Project pronunciation glossary

A project keeps a list of words and how to say them (`project_pronunciation_entries`: `Term`, `Spelling`, `CaseSensitive`; up to 200 per project, owned by the Inference API and read by the engine through a read-only mapping). `Spelling` is a written-accent form such as `Novàra`.

- **Text-only substitution.** Before a line is sent to the speech provider, `PronunciationRespeller` rewrites matching words to their spelling. Only the `SpeechRequest.Text` changes: captions, on-screen text, the narration plan, timing and alignment, the WER comparison and the saved line text keep the original words. Glossary entries are never put in the provider's `pronunciation_dictionary` (`Pronunciation` stays null for them).
- **Matching.** NFC on both sides; whole words only (`Novara` does not touch `Novarese`); one pass over the original text, longest term first, so a replacement is never matched again. Case-insensitive entries (the default) match any case and adapt the spelling (`NOVARA` -> `NOVÀRA`, `novara` -> `novàra`); `CaseSensitive` matches exactly. The first 200 entries are used.
- **Respellings, bounded.** A spelling may change letters (`Xiaomi` -> `Sciaomi`), not only accents. It may hold letters, accents, stress marks (`ˈ` `ˌ` `ː` `ˑ`), spaces, hyphens and apostrophes, at least one letter, no digits, links or markup, and at most twice the term's length plus two (never over 120). The same rules run where an entry is saved and again in the engine, which skips a stored entry that fails them.
- **Moderation sees what is saved and what is spoken.** Every spelling is screened by content moderation when it is saved (REST create and update, and the assistant's apply; one check in `ProjectPronunciationService`, surface `GeneratedText`): a flagged spelling is refused and nothing is written. The glossary is applied before the byte budget and content moderation run in the Voiceover step, so `MaxBytes` is judged on the respelled text (the larger of written and spoken) and moderation screens the respelled text the provider will speak, and the written text as well when they differ; a Block fails the step `CONTENT_BLOCKED`. Both checks follow the generated-text surface's rule: they fail open when moderation is off (`ContentModeration:Enabled` false), has no provider, or is unavailable, and block only on a Block. Moderation is enabled in production.
- **Intelligibility checks read the written words.** The compile step's narration WER fact scores the transcript of the audio against the written text, so a respelling that changes letters (`Nike` -> `Naiki`) can raise the WER figure for that line even though the narration sounds right. Accent-only respellings do not.
- **Precedence.** A term the step's own `pronunciationOverrides` already names is left to that override; the glossary covers the rest. The Auto table is untouched.
- **Cache.** The step result is filed in the step cache under the digest of the glossary entries the step actually read (reported in `meta.pronunciation.glossaryDigest`), not under one re-read afterwards, so a glossary edited while the step runs cannot attach this run's audio to its new digest. The per-line content key uses the text actually sent, so a changed glossary changes only the lines it touches (a line with no match keeps its key and cached audio). The step-result cache key gets a glossary digest only when the glossary is non-empty (existing keys are byte-identical; `SchemaVersion` is not bumped), and the Voiceover algorithm revision carries `+project-pronunciation-glossary-1`.
- **Failure.** If the glossary cannot be read the step fails with `GLOSSARY_UNAVAILABLE` (a platform failure, refunded) rather than speaking without it.
- **Output.** `meta.pronunciation.glossaryCount` (entries loaded) and `glossaryApplied` (`[{term, spelling, count}]`, entries actually used).

## Changing one line of a finished narration

A change request (see [`change-requests.md`](change-requests.md)) redoes chosen lines of a finished run's Voiceover step and carries every other line over verbatim: no object-store probe, no speech call, no alignment call. Per-line overrides are `text`, `voiceId`, `speed` (0.5 to 2.0, Fish `prosody.speed`, in the content key only when set) and `pronunciation` (`{term, spelling}` respellings that change that line's spoken text only, with the project glossary's rules and precedence over it; IPA is not offered). The fit solver runs over the final durations; a new text above the 4.0 words/s hard ceiling still fails `NARRATION_DOES_NOT_FIT` before any speech call, and any other new text is spoken and judged on its measured speech against the window. A change-request line is never handed to the shortener, so measured speech decides. A line the request does not name is still made again when the project's pronunciations changed how it is spoken since the run (see "Pronunciations saved after the run" in [`change-requests.md`](change-requests.md)): each `ok` line records `spoken`, the project glossary's respelling of the line, only when it differs from `text`, and a carried line is kept only when the current glossary gives that same spoken form. A request that matches no line fails `CHANGE_NOT_APPLIED` (a user-input failure). Each `view.lines[]` entry carries the spoken `text`, and `meta.changeRequest` reports `requestId`, `redoneLineIds` and `reusedLineCount`.

## Config reference

`VoiceoverStepConfig` (`ReelBolt.Shared/Workflows/VoiceoverStepConfig.cs`), serialized to
`WorkflowStep.VoiceoverConfigJson` (`voiceover_config_json`, `jsonb`, nullable). Later fields (listed in
the phase sections) include `pronunciationOverrides` (`[{term, ipa, caseSensitive}]`, default null; see
"Italian (and other non-English) narration"):

```csharp
public sealed record VoiceoverStepConfig(
    VoiceoverSource Source,
    Guid? ProviderId = null,
    string? Model = null,
    string? DefaultVoiceId = null,
    long MaxBytes = 2_000_000);

public sealed record VoiceoverSource(
    VoiceoverSourceKind Kind,
    IReadOnlyList<VoiceoverLine>? Lines = null,
    int? ScriptwriterStepOrder = null,
    int? NarrationWriterStepOrder = null);

public sealed record VoiceoverLine(
    string Text,
    double StartSec,
    string? VoiceId = null,
    string? AnchorId = null);

public enum VoiceoverSourceKind { Inline, ScriptScenes, NarrationPlan }
```

Three members, and the record is **append-only** in three separate places. `VoiceoverSourceKind`'s
source file says so in its own doc comment: every member is serialized into
`voiceover_config_json` (`jsonb`) and read back by the execute path *and* by the frontend's
TypeScript mirror, so a member inserted before `ScriptScenes` would silently reinterpret every
stored `Inline` or `ScriptScenes` config. `NarrationPlan` is therefore appended **last**, and the
two members that preceded it keep their ordinal values. `VoiceoverSource.NarrationWriterStepOrder`
is appended last for the same reason, and is a **distinct** member rather than a reuse of
`ScriptwriterStepOrder` — the two name different agents with different output schemas, and
overloading one member would make a stored config's meaning depend on which kind it happens to
carry. `VoiceoverLine.AnchorId` is appended last on the line record so an `Inline` line stored
before it existed still deserializes with the same first three values.

Two JSON facts follow from `VoiceoverStepConfig.JsonOptions` (Web defaults plus a
`JsonStringEnumConverter`): properties are camelCase, and the enum member is written as the
**PascalCase literal** `"NarrationPlan"` — not `"narrationPlan"`. That is what the committed
template, the tests and the frontend mirror all write:

```json
{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":3}}
```

The same `JsonOptions` instance is used by the executor at run time and by the Inference API's
save-time validation, so the two cannot drift into reading one config two ways.

| Member | Meaning |
|---|---|
| `Source.Kind` | `Inline` (lines embedded in the config), `ScriptScenes` (lines read from an earlier Scriptwriter step's output) or `NarrationPlan` (lines read from an earlier `NarrationWriter` step's `NarrationPlanOutput`). |
| `Source.Lines` | The `Inline` lines. Each has `Text`, a `StartSec` on the **source/picture** timeline, and an optional per-line `VoiceId` that overrides `DefaultVoiceId`. |
| `Source.ScriptwriterStepOrder` | Required when `Kind = ScriptScenes` — the `StepOrder` of the step whose output is a `ScriptwriterOutput`. |
| `Source.NarrationWriterStepOrder` | Required when `Kind = NarrationPlan` — the 1-based `StepOrder` of the `NarrationWriter` step whose output is a `NarrationPlanOutput`. |
| `VoiceoverLine.AnchorId` | The opaque cut-anchor id the line was written for (`s{n}`/`g{n}`/`t{n}`), carried **only** by a `NarrationPlan` line — no other source kind can declare one. It is never spoken and never affects synthesis or the content-addressed artifact key; it is threaded through to the fit facts so an overflow stays attributable to the moment the line was written for. |
| `ProviderId` | Optional per-step speech-synthesis provider override, resolved as above. |
| `Model` | Optional model name; wins over the provider row's `ModelName`, whose value in turn wins over the client's hardcoded `s2.1-pro` default. |
| `DefaultVoiceId` | Fish Audio `reference_id` used for any line that does not carry its own `VoiceId`. |
| `MaxBytes` | Budget in **UTF-8 bytes of sanitized text**, checked before any provider call. Default 2,000,000. |

The step is registered as `IStepExecutor`/`StepType.Voiceover` in
`WorkflowEngine/WorkflowEngineServiceCollectionExtensions.cs` — the composition root was extracted
there and `Program.cs` delegates to it — and `StepCachePolicy` marks `StepType.Voiceover` cacheable
(`StepCachePolicy.cs`): it is deterministic, non-LLM, and its only side effects are a
WorkflowEngine-owned scratch file and content-addressed objects of its own in storage. The
*content-addressed* half is what makes a cached output replayable — see
[Step-result caching and the content-addressing invariant](#step-result-caching-and-the-content-addressing-invariant).

### `Inline` and `ScriptScenes`

`Inline` is the general case: the workflow author writes the narration and its start times directly
into the step config. Nothing is resolved against a cut decision on this path — those times are the
author's.

`ScriptScenes` exists for the existing promo pipeline, where a `ScriptwriterAgent` step has already
produced a `ScriptwriterOutput` with per-scene `Voiceover` text and a `StartTime`. The executor
finds the step named by `ScriptwriterStepOrder` in the step-output history, deserializes it as
`ScriptwriterOutput`, and converts every scene with non-empty `Voiceover` text into a
`VoiceoverLine` whose `StartSec` is that scene's `StartTime` and whose `VoiceId` is `null` (so
`DefaultVoiceId` applies). Scenes with empty/whitespace voiceover are skipped — which is exactly the
convention the staging side relies on, since empty scenes consume no line index.

None of the three kinds re-cuts picture to match narration. Phase 1 adds narration audio to the
existing picture; phase 2 measures whether it *fits* and makes that measurement readable downstream
([The fit solver and the review loop](#the-fit-solver-and-the-review-loop)); moving the picture
itself is phase 3's "audio-first timing".

### `NarrationPlan`

`NarrationPlan` is the model-authored path, and it is the one kind whose lines arrive without a
position. The executor finds the step named by `NarrationWriterStepOrder` in the step-output
history, deserializes it as `NarrationPlanOutput`, and turns each plan line into a `VoiceoverLine`
carrying that line's `Text` plus its opaque `AnchorId` — and nothing else, because the plan has
nothing else to give:

```json
{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":3}}
```

Lines whose text is empty or whitespace are dropped here, exactly as the `ScriptScenes` arm drops an
empty scene voiceover (the sanitizer would skip them anyway) — and such a line takes its anchor id
with it, because a line that was never emitted carries no fit to attribute. Every line that *is*
kept carries its anchor, and the fit pass reads that anchor rather than inventing one: a fabricated
id would claim an anchor the writer never named.

**A plan line carries no start, so the step declares one.** `NarrationPlanOutput` is structurally
incapable of carrying a start time — every property is a plain `string`, and
`NarrationPlanOutputInvariantTests` bans a property even *named* for a time — and the voiceover step
holds no picture timeline to place a line against. So after the synthesis loop,
`AssignNarrationPlanStarts` walks the emitted lines and assigns each one a start **back-to-back in
plan order**: line 0 at `0.0`, each next line at the previous line's declared start plus that line's
measured `durationSec` (`0` for a skipped or failed line, which synthesized nothing, and `0` for a
non-finite or negative duration, which cannot advance a position). The result is deterministic and
never overlapping.

Two consequences are worth stating, because both are load-bearing:

- The declared start written to `view.lines[].startSec` and the declared window the fit pass reads
  come from **one** assignment, so they cannot disagree. Because the next line's start is the
  previous line's start plus its measured duration, a `NarrationPlan` line's declared window is
  exactly its own duration and its **declared-window overflow is structurally zero**. That is not a
  bug to fix here: the declared-window number answers "did the line overrun the slot it declared",
  and a narration-first line declares exactly the slot it occupies. Whether the narration overruns
  the **shot** it lands in is the compile step's separate measurement.
- This path deliberately does **not** read the analysis step, require one, or invent an
  analysis-step reference in its config. Placing narration against the picture would be a new
  cross-step dependency for a number the compile step already produces.

`Inline` and `ScriptScenes` are untouched by the placement pass: their starts come from the
config/script, and their fit numbers stay byte-identical.

---

## Word alignment

A synthesized line is a WAV with no notion of where its words sit inside it. Phase 2 recovers that:
an `IVoiceoverAlignmentService` (`Services/Voiceover/` in the WorkflowEngine) aligns each synthesized
line by re-transcribing its own WAV.

### Why `ITranscriptionClient`, and not Fish's native timestamps

Fish Audio advertises per-word timestamps through `POST /v1/tts/stream/with-timestamp` (SSE) and its
live WebSocket twin, and phase 1 explicitly left the choice between those and a second ASR round-trip
to whoever scoped phase 2. It is settled by **measurement, not preference**: the self-hosted `fish-tts`
returns

```
POST http://localhost:8180/v1/tts/stream/with-timestamp
-> 404 {"statusCode":404,"message":null,"error":"Not Found"}
```

That is the server the whole local pipeline runs against, so a Fish-native alignment path would be
permanently dead code on every self-hosted deployment — and phase 4 adds a *local* TTS provider kind,
which makes self-hosted the direction of travel rather than an edge case. Meanwhile
`ITranscriptionClient` (Local Whisper) already returns a real
`words[{text, startSec, endSec}]` array on this stack, through a provider-agnostic seam that already
exists and is already exercised by `VideoAnalyze`.

So phase 2 aligns by re-transcribing the line WAV through `ITranscriptionClient`, and Fish's native
endpoints stay unused. If a later wave wants them for hosted-Fish deployments they are a **fast path
behind the same interface, never a replacement** — the self-hosted case has to keep working.

ASR word boundaries are the recognizer's, not the synthesizer's, and that is the *right* source here
for the same reason it will be right for captions: it measures what a listener would actually hear,
which is exactly what a caption must match.

### How it runs, and how it fails

`AlignAsync(projectId, storageKey, cancellationToken)` resolves the provider with
`IInferenceProviderResolver.ResolveTranscriptionAsync` — the documented precedence, with no legacy
fallback — downloads the line WAV the step already stored, and calls `ITranscriptionClient` with word
timestamps requested. It runs on **both** paths that produce an `ok` line: fresh synthesis *and* the
object-store cache-hit path, so a repeat run does not silently lose its words.

**What leaves the host.** Alignment is a host egress, and it is unconditional: for **every** `ok`
voiceover line the step downloads that line's synthesized WAV and **POSTs the audio to the
deployment's default Transcription provider row** (`Capability = Transcription`, `IsDefault = true`),
which in the normal deployment is a hosted third party. The payload is the narration audio the step
itself just produced — text that was, on the `ScriptScenes` arm, derived from media-derived material
— and the returned transcript is what becomes the line's `words`.

Two properties follow from how the row is resolved, and both are worth stating plainly:

- **There is no per-step override.** `ResolveTranscriptionAsync` is called with a **null** id, so
  unlike `SpeechSynthesis` — where the step's own `ProviderId` is tried first — a workflow author
  cannot point alignment somewhere else, and cannot leave it unset for a given step. The destination
  is whatever row the deployment has marked `IsDefault = true AND Capability = Transcription`; an
  admin changes it by editing that row, not by editing the workflow.
- **A deployment with no `Transcription` row configured sends nothing.** Resolution returns none, the
  path records `NO_TRANSCRIPTION_PROVIDER` and degrades — so alignment is never silently redirected to
  some other provider, and a stack with no transcription default simply has no word timings.

This is the same provider row, reached by the same `ITranscriptionClientFactory`, that the shipped
`VideoAnalyze` step already sends the project's **own source-video audio** to, so it widens an
accepted external-dependency flow rather than opening a new kind of one. It is nevertheless a second
reachable production call site through that factory. `docs/decision-models.md`'s "What leaves the
host" is the register for the **Decision** call sites and does not inventory this one — this section
is that inventory for voiceover.

Alignment is the third soft-failure seam on this path and behaves like the others: **it degrades with
a recorded reason and never fails the step.** A missing provider, a failed download, a throwing client
and an empty word list each return "not applied" with a distinct reason from a closed set —
`NO_TRANSCRIPTION_PROVIDER`, `DOWNLOAD_FAILED`, `TRANSCRIPTION_FAILED`, `NO_WORDS`. Nothing escapes as
an exception except a genuine caller cancellation, which is **re-thrown**, the same way
`GuardrailScreener` treats one: a cancelled run must not be recorded as a degraded line.

Two deliberate omissions. Alignment is **not** part of the step-cache key — the whole output JSON is
cached, so a hit already carries the words of the run it cached. And a `skipped` or `failed` line is
never aligned: there is no audio to align.

### What lands in the output

Additively, per line ([Step output and error codes](#step-output-and-error-codes)):

- **`words`** — `[{ "text", "startSec", "endSec" }]`, present **only** when alignment applied.
- **`alignmentReason`** — the named reason, present **only** when it did not.

Both are *omitted* rather than null when they do not apply, the same convention `storageKey` already
uses, so "field absent" keeps its existing meaning.

`meta.alignment` rolls the whole step up: `{ applied, alignedLines, unalignedLines, reasons: {
<reason>: <count> } }`. It is always present once lines are produced, never null, and computing it
never throws.

---

## The fit solver and the review loop

A narration line is anchored to a shot, and it has to be **speakable in that shot**. Phase 1 probed a
line's duration and reported it; phase 2 measures it twice — against the slot the line declared, on
the voiceover step, and against the shot it landed in, on the compile — and turns both answers into
facts the review loop can act on.

### Deterministic code, never an agent

**No model computes a duration anywhere on this path, and neither half of the fit is an agent.** The
overflow is arithmetic over data the pipeline already has — the line's declared or mapped start, the
duration probed from its WAV, and the words that were actually spoken — so there is nothing for a
model to judge. An agent here would also be unfalsifiable: a narration that overruns its slot is a
measurement, not an opinion, and the review loop's whole lever is that it can threshold a number.

There are **two** fits, deliberately, and they answer different questions:

| | The `NarrationFitSolver` | The compile's picture-window facts |
|---|---|---|
| Where | `StepType.Voiceover` — `ReelBolt.Shared/Workflows/NarrationFitSolver.cs` | `VideoCompileStepExecutor` — `WorkflowEngine/Services/Video/NarrationFitFacts.cs` |
| Question | "does the narration overrun the slot it was written for?" | "does it overrun the shot it landed in?" |
| Window | The **declared narration window** `[StartSec_i, StartSec_{i+1})` | The **containing output placement**, after seam overlaps |
| Unit | Words (and seconds, as evidence) | Seconds only |
| Node | `view.lines[].fit` + `meta.fit` on the voiceover step | `voiceover.fit` + per-line keys on the compile step |

`VideoCompileStepExecutor` did **not** reimplement the solver's arithmetic and `NarrationFitSolver`
does **not** read, require or reimplement the picture window. `NarrationFitSolver` lives in
`ReelBolt.Shared/Workflows` rather than in the WorkflowEngine's own tree precisely because both
services may read a fit report and `Services/Video/**` is a different department's surface. **A
consumer must not conflate the two**, and neither type may grow a "read the other" path.

### Acting on the fit: the `NarrationFitPolicy` ladder

The solver used to only measure. With `NarrationFitPolicy: "Enforce"` (the default when the key is
absent; `"Report"` is the old measure-only behaviour, byte-identical output) the step makes sure two
voices never overlap. It applies to `Inline` and `ScriptScenes` lines; `NarrationPlan` and
`PickupPlan` lines occupy their own slot and are left as they are.

**Before the ladder: the spend bound, always on.** `MaxBytes` caps the request size, never how many
words are synthesized, and the 4.0 words/s ceiling bounds each line but not how many lines a
pathological script can declare. Independently of `NarrationFitPolicy` and of the source kind, the
step's total **admitted** words (the lines it will synthesize; a change request's carried lines are
not counted) are capped per step by `Voiceover:MaxAdmittedNarrationWords`, default **5000** (about 33
minutes of speech at 2.5 words/s — far beyond any real promo, finite for a runaway one); a
non-positive configured value falls back to the default. The check runs **before any provider call**,
the shortener's model call included, on the pre-shortened text (shortening only reduces the count).
Exceeding it fails the step `NARRATION_TOO_MANY_WORDS` (a config/validation failure, `UserInput`,
never retried). The predicate (`NarrationFitSolver.IsSynthesisAdmissible`) is separate from
`IsUnrescuable`: that is per-line physical possibility, this is the per-step cost ceiling.

- **Window.** `[this line's start, the next line's start)`. The LAST line's window runs to the film
  length when the run brief declares `lengthSec`; without a brief it has no window, as before.
- **Word budget: about 1.8 words per second of scene time.** `NarrationFitSolver.WordBudget(window)` is
  `floor(window x 1.8)` (a 3.5 s scene carries 6 words). It is the **comfort writing budget**, used for
  advice and the writer prompts (Scriptwriter, NarrationWriter, the run-brief Length row), and is
  reported per line as `fit.wordBudget` (append-only; absent under `Report`). It is **not** a pass/fail
  limit: fit is judged on the measured play duration described below.
- **1. Before any synthesis.** Only a line above the **hard ceiling of 4.0 words per second**
  (`NarrationFitSolver.HardCeilingWordsPerSecond`, `HardWordCeiling(window) = floor(window x 4.0)`) is
  refused up front, so no paid synthesis is wasted: `words > HardWordCeiling(window)`. Everything at or
  below that ceiling is synthesized. The `INarrationLineShortener` (`NarrationLineShortener`: one
  NarrationWriter chat call per batch, answer validated deterministically: non-empty, at most the word
  budget, sanitized, no URL or tag) still gets the impossible lines first — only text the script wrote,
  never a change-request override or a carried line — and a shortened line must fit the **1.8 word
  budget**; measured speech decides the rest. Ordinary speech runs about 2.2-2.7 words per second, so
  the old effective threshold of 2.07 words/s (1.8 x the 1.15 tempo fit) refused lines that plain
  reading fits; only the 4.0 ceiling is physical.
- **2. Tempo fit.** A line that runs more than 0.1 s past its window is sped up with ffmpeg `atempo`
  (pitch-preserving), at most `MaxTempoFit = 1.15x`, through `IVideoToolRunner` (first-party argv). The
  result is stored under a NEW content-addressed key (`voiceover/lines/{key}-t{tempo}.wav`); the original
  object is never overwritten, and the line's `storageKey`, `durationSec` and `words` are the retimed
  ones. Without a tool runner this step is skipped.
- **3. Shorten once, then fail.** Still overflowing after the tempo fit, the shortener gets ONE attempt
  per batch; only the changed lines are re-synthesized (the others come back from the object store) and
  the ladder re-runs. The call is metered inside the step's hold (see docs/metering-and-billing.md).
  Still overflowing: the step fails with `NARRATION_DOES_NOT_FIT`. This failure is
  never retried, because the same input fails again and a retry would buy the synthesis twice; the shortener
  therefore runs at most once. The failure class depends on who wrote the narration: `Inline` (the user's own text) is
  `UserInput`, so the spend is kept (the synthesized audio stays in the content-addressed object cache, and
  refunding it would let a re-run with wider `startSec` reuse it free); `ScriptScenes` (written by our script
  step) is `PlatformInternal` and fully refunded. The step sets this class explicitly and it wins over the
  `FailureClassifier` table, whose `NARRATION_DOES_NOT_FIT` default is the platform case. `NarrationPlan` and
  `PickupPlan` lines are exempt from the fit check and never reach it. A `CONTENT_BLOCKED` on text our
  shortener wrote is `PlatformInternal` (refunded); on the user's own text it stays `UserInput`. The message
  names each scene in the run language (Italian or English) and has two shapes. With measured speech it
  quotes the measured seconds, for example: "The line for scene 3 is too long for its scene: it has 9
  words and takes 3.9 s of speech but the scene lasts 3.5 s. Shorten the text or lengthen the scene."
  Before synthesis it names the hard ceiling instead, for example: "The line for scene 3 is too long for
  its scene: it has 15 words, but the scene lasts 3.5 s and a voice can speak at most about 14 words in
  that time. Shorten the text or lengthen the scene."
- **Report.** `meta.fit` gains `retimedLineCount` and `maxTempoApplied`; a retimed line carries
  `fit.tempo`. All `overflow*` numbers describe the FINAL state, so a passing run says overflow 0.
  The step's cache revision was bumped (`narration-fit-measured-1`, on top of `narration-fit-enforce-1`).

**No free audio from a refunded attempt.** The object store is the per-line cache, so a refunded failure
after synthesis (`NARRATION_DOES_NOT_FIT` from `ScriptScenes`, or `CONTENT_BLOCKED` on shortener text) would
otherwise leave paid audio for a later run to reuse free. The step records the line objects it newly created
in that attempt (not ones that already existed; the `-t{tempo}.wav` retimed objects included) and deletes
them through `IProjectFileWorkspace.DeleteArtifactAsync` (scoped to `projects/{projectId}/agentFiles/voiceover/`) before
returning the failure. A delete that fails is logged and never changes the failure. An `Inline` fit failure
keeps its objects (the spend is kept), and a successful step still reuses cached lines on a re-run.
The delete is scoped to `projects/{projectId}/agentFiles/voiceover/` and refuses `..`, backslash,
percent-encoded, empty and bare-prefix keys. Known limits, accepted as low:

- A retried attempt (the step's single automatic retry) can reuse audio an earlier refunded attempt had not yet deleted.
- A concurrent upload of the same content-addressed key by another run in the same project can race with the delete.
- The step-cache HEAD does not check line keys, so a step cache hit can point at a line that was deleted.
- An `UNEXPECTED_ERROR` after synthesis does not delete what the attempt created.
- The shortener hold covers one chat call; usage above it is charged as overage rather than capped by the hold.

### The declared window on the producing step (`NarrationFitSolver`)

`NarrationFitSolver.Solve(IReadOnlyList<NarrationFitLine>?)` returns a `NarrationFitReport`, a closed
serializable record:

```csharp
public sealed record NarrationFitReport(
    IReadOnlyList<NarrationFitLineResult> Lines,
    int MeasuredLineCount,
    int OverflowLineCount,
    int OverflowWords,
    double OverflowTotalSec,
    double MaxOverflowSec,
    int UnknownWindowLineCount);

public sealed record NarrationFitLineResult(
    string Id, string? AnchorId, double? WindowSec, double PlayDurationSec,
    int WordCount, int? AlignedWordCount, double WordsPerSecond,
    double OverflowSec, int OverflowWords, bool Fits, string WindowSource);
```

Every member of `NarrationFitLineResult` is **always present** in the serialized form — this is a
closed record whose key set the compile's passthrough reads unconditionally (`overflowWords` in
particular), and a null already means exactly one thing per member: no window, no anchor declared, or
no alignment applied.

The rules, from the committed code:

- **The window** is `[this line's start, the next declared line's start)` — `NarrationFitLine`'s
  `WindowStartSec`/`WindowEndSec`, both of which the step leaves null on the **last** declared line,
  which has no successor to end at. Such a line carries `WindowSec: null` on the result, is reported
  as `WindowSource: "none"` and is counted in `UnknownWindowLineCount` rather than being given an
  invented duration. A windowless line never contributes overflow and always counts as fitting — an
  unknown window is an absence of evidence, never a defect to report as one. The other literal is
  `"declared_starts"`; both are a closed set, and the usable-window test is that both ends are
  present, finite, and `end > start`.
- **`WordCount`** counts whitespace-separated tokens in the **sanitized** text — what was actually
  spoken, which is what makes the number comparable to a duration. `NarrationFitSolver.CountWords` is
  public for exactly that reason: the step that has the sanitized text must count it here rather than
  with a second, subtly different split.
- **`OverflowSec`** is `max(0, playDurationSec - windowSec)`, rounded to 3 decimals;
  **`OverflowWords`** is `ceil(overflowSec * wordsPerSecond)`. Ceiling, not rounding: a line that
  overruns by a fraction of a second still overruns by at least one spoken word, and "0 words over"
  would read as a fit. `WordsPerSecond` is the line's own rate and is `0.0` — never NaN, never
  Infinity — whenever it is undefined.
- **No alignment is required to solve.** `AlignedWordCount` is carried through only as evidence;
  `WordsPerSecond` and `OverflowWords` are identical on a deployment with no ASR provider and on one
  with a perfect recognizer.
- **Never throws.** A null list, a null element, a non-finite duration or a negative word count all
  produce a well-formed report: non-finite durations normalize to `0`, negative counts clamp, and
  `Empty` — every count `0`, both second totals `0.0`, never null and never NaN — is returned for an
  empty input.

The step attaches it in one pass over the finished line list, after synthesis (a duration is only
known once a line has been synthesized or probed), and from the **declared** line list: a failed line
still occupies its declared slot, so the window of the line before it still ends where that failure
was declared to begin — but the failed line itself gets no `fit` key at all, because it synthesized
nothing to measure. Fit rides in the output JSON rather than in the step-cache key, so a step-cache
hit re-serves the fit computed by the run it cached, exactly like the alignment words.

### The window is the containing shot, not the gap to the next line

The compile step measures every resolved line against the **output placement its start landed in** —
the shot the line starts in — **extended across every following kept shot that starts no line of its
own** (a line may carry over a cut), and reports per line:

| Key | Meaning |
|---|---|
| `windowStartSec` / `windowEndSec` | The containing placement's start, and the end of the last shot the line may carry into. |
| `windowSec` | `windowEndSec - outputStartSec` — the picture time left from the line's own start to the end of that window. |
| `carriedShots` | Present only when the window was extended: how many following shots it spans. |
| `overflowSec` | `max(0, playDurationSec - windowSec)`, rounded to 3 decimals like its sibling keys. Zero means it fits. |
| `fits` | `overflowSec <= 0`. |

Measuring instead against `[line start, next line start)` would report "fits" for a line that runs
straight past the cut into the next line's shot — precisely the defect these facts exist to surface.
The carry stops at the first shot that another line was WRITTEN FOR — the shot its anchor starts
in, not the second its audio happens to start: narration plays back to back, so a line pushed back
behind a long one must not let that long one claim the picture it was anchored to. A line followed
by another line's anchor in its own starting shot never carries. An overrun of at most 100 ms (`NarrationFitFacts.OverflowToleranceSec`) is the
WAV's trailing breath, not a word over the next shot, and reports `overflowSec: 0`: a 3.019 s opener
over a 3.0 s shot had capped a reel's review at 4.

#### Lines that carry over a cut

A beat-cut reel holds a shot for under two seconds, and one spoken sentence takes three or four: with
the window limited to the starting shot, every such line overflowed and the narration cap held every
review at 4 however good the edit was. Speaking over a cut is ordinary editing; speaking over the
NEXT line's picture is the defect, so only that still counts as an overflow. That is why the window is
the *containing* placement, and why the lookup belongs on `OutputTimeline`: it is the only thing that
knows where a span landed once seam overlaps are accounted for.

The two shapes are complementary rather than redundant, and a `NarrationPlan` step is where that
becomes visible: the step's own declared-window overflow is structurally zero (a narration-first line
declares exactly the slot it occupies — see
[`NarrationPlan`](#narrationplan)), so the compile's picture fit is the one that reports an actual
overrun. On an `Inline` or `ScriptScenes` step, where the author or the script declared the starts
independently of the measured durations, both can be non-zero.

### How the overflow reaches the `ReviewLoop`

A review loop's lever is `VideoReviewAgent`'s score against the compile step's own output JSON, which
is already in the pipeline history the reviewer is handed — so the fact has to be in the node the
reviewer reads. It is: `voiceover.fit`, a node-level roll-up carried, like every other key on that
node, into both `outputSummary["voiceover"]` and `edl["voiceover"]`. The voiceover step's own
`meta.fit` and its two promoted integers are the same fact one step earlier — gate on whichever step
your `Conditional`/`ReviewLoop` sits behind.

The reviewer's prompt **does** carry the narration clause, so a `ReviewLoop` over `VideoReviewAgent`
acts on overflow twice over, and the two mechanisms are complementary rather than redundant.

**In the prompt.** `VideoReviewAgent`'s fact list gained a `voiceover` clause describing the per-line
picture windows (with `fitSource`/`solverFit`), the `voiceover.fit` roll-up, and the rule the whole
check turns on: the containing output placement is the window a listener actually hears, so a line
that runs past the end of its own shot is an **overflow, never a fit, however much room there was
before the next line began**. It scores no higher than **4** when `overflowLineCount > 0`, names
`maxOverflowSec` and the `lineId` of each overflowing entry, and quotes the narration text so the
retry knows which line to shorten. It also states the only two remediations the upstream agents can
follow — rewrite the line shorter for the same anchor, or keep a longer span — and forbids telling
any agent to "re-time", "shift", "extend" or "move" a line, because the plan carries no timestamp.

That clause's "not measurable" list names **both** degradation reasons the node can carry, and its
`unknownWindowLineIds` sentence describes a partial measurement. On the shipped executor the list is
narrower than the prose: the no-`fit`-node case is live (a compile with `EnableVoiceover: false`), of
the two reasons only `no_resolved_lines` is producible, and the partial measurement it describes does
not occur — see [the degraded shape](#how-the-overflow-reaches-the-reviewloop). The instruction is
still correct and still load-bearing: it is what stops the reviewer capping a fit that was never
computed. It simply has fewer live cases than it enumerates, and a reviewer must not report
`window_not_found` as something to expect.

**In code, because a prompt is a request.** There are **three** deterministic caps, applied together
by `ReviewLoopStepExecutor.ApplyNarrationCaps`. They are separate `Math.Min`s, so the order is
immaterial; each can only lower a score, never raise one, none throws, and none rewrites anything —
the reviewer's own output JSON is persisted verbatim, so the capped value drives the loop decision
only. Every constant is **4**, below every video template's `MinScore` (8), which is what makes a cap
iterate the loop rather than merely score lower.

- **`ApplyNarrationFitCap` — a line overran its window.** `min(score, NarrationOverflowScoreCap)`
  when the prior step's output carries a top-level `voiceover.fit` with `applicable` literally `true`
  and a positive `overflowLineCount`; the score unchanged otherwise. It is inert to a fit that was
  never measured: `applicable: false`, a missing `fit` node and `reason: voiceover_not_enabled` are
  all "not measurable, therefore never a defect". A **partial** measurement is not exempted — a
  non-empty `unknownWindowLineIds` means fewer lines were measured, and the lines that *were*
  measured still count. That list is empty by construction on the shipped executor, so this is a
  statement about the cap's definition rather than a case a reviewer meets.
- **`ApplyNarrationDeclaredLostCap` — narration was declared and never reached the picture.**
  `min(score, NarrationDeclaredLostScoreCap)` on the other half of the silence story: the compile
  rendered a green, narration-less video, and because that node has **no** measurable fit its
  `fit.applicable` is `false`, so the overflow cap above is inert to it by design. Without this cap a
  green reviewer score advances the loop over a video with no narration at all.
- **`ApplyNarrationMostlyDroppedCap` — more than half the narration is missing.**
  `min(score, NarrationMostlyDroppedScoreCap)` when the top-level `voiceover` node has
  `lineCount > 0` and `(lineCount - appliedLineCount) * 2 > lineCount`. This is the gap between the
  other two: the declared-and-lost cap needs **zero** resolved lines and the fit cap only measures the
  lines that did resolve, so a compile that placed 1 of 8 lines (the multi-clip bug fixed in
  [Multi-clip narration](#multi-clip-narration)) passed review green over ~60 s of silence. It reads
  the same declared-vs-applied pair the compile's own `narration_mostly_dropped` warning is computed
  from, so the warning the customer sees and the loop decision cannot disagree. Exactly half missing
  is not "mostly" and is left to the reviewer (the compile still warns `narration_dropped`).

`IsDeclaredAndLostNarration` accepts **either** of two machine-readable forms on the top-level
`voiceover` node, and only after two gates: `applied` must be literally `false`, and
`appliedLineCount` must be literally `0`. (That pair is what makes the cap inert for every **partial**
resolve — one where `appliedLineCount` is positive — and for the empty plan below, which declares
nothing at all.)

1. **The declared-vs-applied key pair** — `lineCount > 0` with `appliedLineCount == 0`, the form the
   contract names and the one the cap is keyed on. `lineCount` is the **declared** count: every
   parsed `view.lines` entry carrying a line id, counted *before* the usability filter, so a
   `skipped`/`failed` line carrying `durationSec: 0` still counts as asked-for. `appliedLineCount` is
   the **resolved** count, so the two are equal only when nothing was lost. This form fires on every
   arm where lines were declared and none reached the picture.
2. **The compile's terminal outcome token** — `reason: "all_lines_unavailable"`, which
   `VideoCompileStepExecutor` sets if and only if at least one line was declared, not one of them
   resolved, **and** the `amix` probe succeeded. It is a closed terminal value the rest of this
   codebase already decides on, **not** an inference from prose, and on that arm it is carried
   alongside form 1 rather than instead of it.

Neither form is a rename, a reshape or a change to media's node, and **both are live**: the key pair
stopped being a duplicate of itself when `Finish()` was corrected to report the declared count, which
is the declared-vs-resolved distinction its own comment always said it was for.

**The `amix`-unavailable arm is capped too.** A container whose ffmpeg build exposes no `amix`
`normalize` option reports `lineCount > 0, appliedLineCount == 0, unavailable: true` with an
explanatory `reason`. The cap fires on it exactly as it does on a planner-side total loss. A loop
cannot fix an environmental cause, and that is not the point: a bounded (`MaxIterations: 3`) loop that
ends with the run **not** reporting green is the desired outcome, because the alternative is a green
render with no narration — the single failure this whole path exists to prevent. A wrong-but-loud
report beats a wrong-and-silent one. The node's own `reason` and `unavailable` fields are what let a
human tell an environment problem from a planner problem, which is why the reviewer's feedback must
name the cause rather than only the missing narration.

**What the cap is inert for, now.** Exactly two shapes: a compile with **no `voiceover` node at all**
(narration switched off, `EnableVoiceover: false`) and a Voiceover step that **declared nothing** —
the legitimate empty `NarrationPlan`, whose `lineCount` is `0` and which therefore carries neither
form. A node reporting `lineCount > 0` with `appliedLineCount == 0` is capped whatever the cause, and
a `fit.applicable: false` node is **not** an exemption: of the two reasons that node can carry, only
`no_resolved_lines` is producible, and zero resolved lines with lines declared is precisely the shape
this cap exists for.

Both caps are also honoured by the **calibrated decision gate's early stop**, which is the one path
that returns *before* the reviewer runs and therefore before either cap is applied:
`NarrationCapsWouldFire` probes the caps themselves (with `UncappedProbeScore`, `int.MaxValue`, chosen
so the predicate cannot drift from the caps it describes) and, when one would fire, **escalates to the
reviewer** instead of accepting. That costs the gate nothing but the escalation it already treats as
its safe failure mode, and it short-circuits *before* the outbound provider call, so a step the caps
will reject never costs a provider round trip. It also means the caps cannot be declined by turning
that gate on. The gate's own fact whitelist is deliberately **not** widened with a voiceover fact —
the set of things that leave the host to a third-party `Decision` provider is not being increased —
so the fix lives on the local, inert side.

A `Conditional`/`ReviewLoop` that would rather not depend on the prompt can still gate on the node
explicitly (`overflowLineCount` / `maxOverflowSec`).

```json
{ "applicable": true, "measuredLineCount": 3, "overflowLineCount": 1,
  "overflowTotalSec": 1.8, "maxOverflowSec": 1.8, "unknownWindowLineIds": [] }
```

`maxOverflowSec` is the worst single overflow and `overflowTotalSec` the sum; `overflowLineCount` is
how many lines missed their window. `unknownWindowLineIds` is where a resolved line whose containing
placement could not be found would be named, so a partial measurement is reported rather than silently
averaged into the totals — on the shipped executor it is empty by construction (below), so every
number here is a total over all resolved lines. It is present — possibly empty — **only on the
`applicable: true` shape**; the degraded shape below has no such key. `applicable` is true only when
at least one line was actually measured.

A compile that cannot measure fit **degrades to a different shape**: `{ "applicable": false,
"reason": <reason> }`, carrying no `measuredLineCount`, no totals and no `unknownWindowLineIds`.
**On the shipped executor `reason` is always `no_resolved_lines`** — nothing resolved, so there was
nothing to measure. The other value, `window_not_found` ("lines resolved but none had a containing
placement"), survives in `BuildVoiceoverFitNode` as a **defensive arm only, unreachable from the
mapped path**: containment is looked up with the line's **raw** `MapToOutputSec` second while the
reported `outputStartSec` stays the 3-decimal rounded one, and `MapToOutputSec` returns
`placement.OutputStartSec + (sourceSec - span.SnappedStart)` for a span whose bounds are exactly what
`OutputTimeline.Build` sets that placement's `OutputEndSec` from — so a second it produced is always
inside its own placement and the lookup cannot fail. A reviewer will not meet this reason; do not
write it up as one to expect.

Rounding for **reporting** while looking up **raw** is itself load-bearing, and is why the arm closed:
testing containment on the rounded value dropped a line that rounded onto its shot's exclusive end out
of `measuredLineCount` while leaving `overflowLineCount` at `0` — a real overflow hidden behind a
clean-looking node. It still returns `StepStatus.Completed` with valid JSON, exactly like every other
soft-failure path on that node. A missing measurement must never become a failed render.

### Optional upstream passthrough

The passthrough now **has a producer**: the `Voiceover` step's own `view.lines[].fit`, the
`NarrationFitSolver` verdict described above. A line node may carry that fit object contributed
upstream, copied verbatim under the key **`solverFit`**, with **`fitSource`** set to `"solver"`. The
object the compile reads is that line's own upstream `fit` key; it re-emits it here under
`solverFit`. Without one, `fitSource` is `"compile_measured"`. The passthrough is tolerant by
construction: it is never required, a malformed one is ignored rather than failing the compile, and
it **never changes** the compile-measured `overflowSec`/`fits` — the two numbers sit side by side on
the same line node, each labelled by which measurement it is.

`fitSource` is written on **every** resolved line; `solverFit` only when an upstream one existed. The
node-level `voiceover["fit"]` keeps its name: `fit` at the top level of that node is the roll-up, and
the per-line passthrough is deliberately *not* called `fit` for exactly that reason.

---

## Step output and error codes

`VoiceoverStepExecutor` never throws: every failure mode is representable in the JSON it returns,
because `output_json` is `jsonb` and a thrown exception would otherwise be a persisted-format
problem rather than a step result.

Successful output:

```json
{
  "view": {
    "lines": [
      { "id": "vo0", "startSec": 0.0, "status": "ok", "durationSec": 3.42,
        "storageKey": "projects/{projectId}/agentFiles/voiceover/lines/{sha256hex}.wav",
        "words": [ { "text": "ReelBolt", "startSec": 0.12, "endSec": 0.71 } ],
        "fit": { "id": "vo0", "anchorId": null, "windowSec": 6.0, "playDurationSec": 3.42,
                 "wordCount": 7, "alignedWordCount": 7, "wordsPerSecond": 2.05,
                 "overflowSec": 0.0, "overflowWords": 0, "fits": true,
                 "windowSource": "declared_starts" } },
      { "id": "vo1", "startSec": 6.0, "status": "failed", "durationSec": 0 }
    ]
  },
  "meta": { "totalLines": 2, "failedLines": 1, "skippedLines": 0, "allLinesFailed": false,
            "alignment": { "applied": true, "alignedLines": 1, "unalignedLines": 0,
                           "reasons": {} },
            "fit": { "measuredLineCount": 1, "overflowLineCount": 0, "overflowWords": 0,
                     "overflowTotalSec": 0.0, "maxOverflowSec": 0.0,
                     "unknownWindowLineCount": 0 },
            "overflowWords": 0, "overflowLineCount": 0 }
}
```

Line ids are positional over the resolved line list — `vo{index}`, zero-based — and `durationSec` is
the **probed** duration of the synthesized WAV, not an estimate.

An `ok` line carries its full `storageKey`: the content-addressed key described under
[Per-line artifact resolution](#per-line-artifact-resolution), not a path a consumer reconstructs.
The property is **omitted**, never null, on a line that has no artifact (`skipped`/`failed`) — and
that absence is precisely the signal a consumer reads as "fall back to the legacy execution-scoped
key".

An `ok` line may also carry the two word-alignment fields described in
[Word alignment](#word-alignment): **`words`** (`[{ "text", "startSec", "endSec" }]`) when alignment
applied, or **`alignmentReason`** (the named reason) when it did not. Both follow the same
omitted-rather-than-null convention as `storageKey`, so an absent field keeps meaning "not
available" rather than "present and empty".

Every `ok` **and `skipped`** line then gains **`fit`**, the `NarrationFitSolver` verdict described in
[The declared window on the producing step](#the-declared-window-on-the-producing-step-narrationfitsolver).
A `failed` line carries no `fit` key at all — it synthesized nothing to measure — and that absence is
how a consumer tells "not measured" from "measured and fitting".

`meta` carries **four** counters — `totalLines`, `failedLines`, `skippedLines` and `allLinesFailed` —
and all four are present on every envelope, success or failure. Once the step has produced lines it
also carries:

- **`alignment`** — the word-alignment roll-up described in [Word alignment](#word-alignment).
- **`fit`** — the complete `NarrationFitReport` totals, always present (never null) once lines are
  produced, so "nothing overflowed" reads as zeros rather than as an absent key: `measuredLineCount`,
  `overflowLineCount`, `overflowWords`, `overflowTotalSec`, `maxOverflowSec` and
  `unknownWindowLineCount`. Note the last member: it is a **count** here, not the compile node's
  `unknownWindowLineIds` array.
- **`overflowWords`** and **`overflowLineCount`** — the same two integers **promoted** to `meta`
  itself, deliberately duplicated so a `Conditional` step can gate on a bare scalar comparison
  instead of walking `view.lines[]`. The nested `meta.fit` object stays the complete report; this
  pair is the one that has to be cheap to read.

A step that produced **no** lines at all never reaches this shape — it returns through `Failure()`,
whose envelope is unchanged. **One exception**, described next.

### An empty plan is a successful step

A **well-formed `NarrationPlan` declaring zero lines completes the step.** The planner is the only
thing that knows whether an edit wants narration, and both `NarrationWriterAgent`'s prompt ("if the
edit needs no narration at all, output an EMPTY `lines` list rather than filling the silence") and
`NarrationPlanOutput`'s own remark say so. Failing the step here made a model that obeyed its prompt
redden a correct run — and after `MaxStepRetries`, fail the whole execution, on a video that is not
wrong.

This is **not** a general weakening of `NO_LINES`, and the two kinds differ for a reason:

- **`NarrationPlan` — a decision.** Nothing in the plan's emptiness is a mistake; it is an editorial
  choice made by the agent that was asked to decide. The step did its job and narrates nothing.
- **`Inline` and `ScriptScenes` — a misconfiguration.** There, the lines come from the author (a
  `Voiceover` step configured with none) or from a referenced `ScriptwriterOutput` whose scenes carry
  no voiceover. Nothing *decided* silence; something was left out. `NO_LINES` remains exactly as it
  was for those two, and phase 1's semantics for them are deliberately untouched.

The empty-plan step returns a Completed envelope of the ordinary shape with every count at zero —
`view.lines: []`, `totalLines`/`failedLines`/`skippedLines` at `0`, `allLinesFailed: false`, and
`alignment` and `fit` **present rather than omitted** so a downstream `Conditional` reading
`meta.fit.overflowLineCount` does not have to special-case the empty plan. No speech provider is
resolved, no line is synthesized, no artifact is uploaded: the step completes by doing nothing.

What distinguishes it, for a machine, is one extra key:

- **`meta.emptyReason: "narration_plan_empty"`** — present **only** on this path, and *absent* rather
  than null or false on every other Completed envelope. "The writer chose silence" is therefore a key
  **existence** check, never an inference from a reason string.

It is added because the envelope otherwise cannot say whether the empty list was a **decision** or an
absence, and a consumer must never have to guess that. The two silences are also already separable
without it — this step is `Completed` with `allLinesFailed: false`, whereas narration that was
declared and every line of which was lost is `Completed` with `allLinesFailed: true` (or `Failed`)
and additionally trips the review cap in
[How the overflow reaches the `ReviewLoop`](#how-the-overflow-reaches-the-reviewloop) — but neither
of those keys says *why* the list is empty, which is what this one does.

### The third status: `skipped`

A line whose **sanitized** text is empty is never sent to the provider and therefore never billed.
Sanitization runs before the byte budget and before any provider call, so a line consisting only of a
`[...]`/`(...)` tag, a bare URL, control/format characters, or whitespace reduces to the empty string
— and such a line is emitted with `"status": "skipped"` and `durationSec: 0`, counted in
`meta.skippedLines`.

The load-bearing part is that the entry is **retained** in `view.lines` rather than dropped. Both
consumers — `VideoCompileStepExecutor.ResolveVoiceoverAsync` and
`ReactRemotionSandboxTools.StageVoiceoverAudio` — pair scenes to voiceover lines **positionally**, so
removing an entry would shift every later line and silently pair a later scene with an earlier line's
audio. A skipped line holds its slot precisely so that cannot happen; it simply has no `storageKey`
for a consumer to resolve.

### `meta.allLinesFailed`

`meta.allLinesFailed` is true only when **at least one line was emitted AND every emitted line
failed** — the step was asked to narrate and produced nothing usable. The executor also logs at
**Error** in that case, naming the failed-line count and the execution id, so that a green step which
synthesized nothing is not the only thing an operator sees.

Stated plainly, and without softening it: **such a step still returns `StepStatus.Completed` by
design.** "Never throw, degrade one line, never fail the whole step" is the contract, and it is
deliberately unchanged. A workflow author who needs a hard failure must therefore gate on
`meta.allLinesFailed` with a `Condition`/`ReviewLoop` step — the executor will not fail the step for
them.

Failure output keeps the same shape with an empty `lines` array and a `meta` that is zeroed but still
carries all four keys (`{ "totalLines": 0, "failedLines": 0, "skippedLines": 0, "allLinesFailed":
false }`), and moves the reason into `ErrorDetails` as `CODE: message`. The codes are:

| Code | When |
|---|---|
| `CONFIG_INVALID` | `VoiceoverConfigJson` is absent, is not valid JSON, or deserializes to null. |
| `NO_LINES` | The resolved line list is empty on a source kind where that is an **author misconfiguration**: no `Inline` lines, or a referenced `ScriptwriterOutput` whose scenes carry no non-empty voiceover. The `NarrationPlan` arm deliberately does **not** return this code for an empty plan — see [An empty plan is a successful step](#an-empty-plan-is-a-successful-step). |
| `SCRIPT_SOURCE_INVALID` | `Kind = ScriptScenes` without a `ScriptwriterStepOrder`. |
| `SCRIPTWRITER_NOT_FOUND` | No step output in history at that `StepOrder`. |
| `SCRIPTWRITER_OUTPUT_INVALID` | The referenced output is not deserializable as a `ScriptwriterOutput`. |
| `NARRATION_SOURCE_INVALID` | `Kind = NarrationPlan` without a `NarrationWriterStepOrder`. |
| `NARRATION_STEP_NOT_FOUND` | No step output in history at that `NarrationWriterStepOrder`. A **distinct** code from `SOURCE_INVALID`, deliberately: "the step you named is not in this run's history" is an operator-visible wiring mistake (wrong step order, or the named step sits *after* this one), and reporting it as "unknown source kind" would send whoever triages it looking at the enum instead of at the workflow. The `ScriptScenes` arm's `SCRIPTWRITER_NOT_FOUND` is the same code for the same reason. |
| `NARRATION_OUTPUT_INVALID` | The referenced output is not deserializable as a `NarrationPlanOutput`, deserializes to null, or is a **well-formed document that is not a plan** — an explicit JSON `null` for `lines`, or a `null` entry inside it. Those two shapes parse perfectly, so without their own check they would dereference and escape as `UNEXPECTED_ERROR`; they get the arm's own code instead. A plan that is *merely empty* is none of these — it succeeds. |
| `SOURCE_INVALID` | An unknown `VoiceoverSourceKind`. |
| `BYTE_BUDGET_EXCEEDED` | Sanitized text totals more than `MaxBytes` — **the provider client is never invoked**. |
| `PROVIDER_NOT_RESOLVED` | `ResolveSpeechSynthesisAsync` returned null (or threw; the exception is logged and treated as null). |
| `UNEXPECTED_ERROR` | Any other exception that escaped to the executor's top level. |

A **per-line** synthesis failure is not a step failure: that line is recorded with
`"status": "failed"` and `durationSec: 0`, and its count goes into `meta.failedLines` — one of the
two per-line counters, the other being `meta.skippedLines` — after which the step still completes.
Only the guardrails above (bad config, byte budget, unresolvable provider) fail the step outright.
This is the same soft-failure discipline the compile step applies to music, graphics and SFX.

Two of those per-line refusals are **named**, and both mean the vendor's bytes were rejected before
anything was stored, so a bad payload cannot poison the content-addressed key:
`VOICEOVER_PAYLOAD_NOT_WAV` is a payload that is not a RIFF/WAVE container at all, and
`VOICEOVER_PAYLOAD_NOT_DECODABLE` is one whose container looks right but which `ffprobe` could not
decode to a positive duration with an audio codec. Those two are the only ones; the literal appears in
the engine log for that line, while the line's own output entry stays the generic
`"status": "failed"`.

Ordering is deliberate: all lines are sanitized and the UTF-8 budget is summed **before** the
provider is even resolved, so a config that would blow the budget costs nothing.

---

## Artifacts, caching and where the audio lives

Two kinds of object come out of a `Voiceover` step, both written through
`IProjectFileWorkspace.UploadArtifactAsync`:

| Object | Name passed to `UploadArtifactAsync` | Content type |
|---|---|---|
| One WAV per successfully synthesized line | `voiceover/lines/{sha256hex}.wav` | `audio/wav` |
| The step's output JSON | `voiceover/{executionId}/artifact.json` | `application/json` |

Per-line WAVs are **content-addressed**: the file name is a hash of what was actually synthesized,
so **no execution id appears in the path**. The hash input is the literal tag `voiceover-line-v2`
followed by `sanitizedText`, `effectiveVoiceId`, `effectiveModel`, `providerId` and `endpoint` — six
fields joined with `\n`, hashed with SHA-256 over their UTF-8 bytes and rendered as **lowercase**
hex. The five value fields are length-prefixed — each written as `value.Length + ":" + value` rather
than joined by a delimiter — so the material is injective by construction: two distinct field tuples
can never hash to one name; the `voiceover-line-v2` tag is a fixed literal and is **not**
length-prefixed itself. Lowercase because that hex string is a URL path segment. Four of the six
fields are resolved rather than taken verbatim from the step config.
`effectiveVoiceId` is `line.VoiceId ?? config.DefaultVoiceId` — the voice actually sent, not merely
the config's default. `effectiveModel` is
`FishAudioSpeechSynthesisClient.ResolveEffectiveModel(config.Model, provider.ModelName)`:
`config.Model` when non-empty, else the provider row's `ModelName` when non-empty, else the literal
`s2.1-pro` — and since `config.Model` is null by default, the resolved model is normally the
provider row's. `providerId` and `endpoint` are the resolved provider row's `ProviderId` and
`Endpoint` verbatim. Provider name, kind, timeout and API key are deliberately **not** part of the
material: none of them changes the audio, and the key stays free of secrets. Only the step's own
JSON artifact stays execution-scoped, under `voiceover/{executionId}/artifact.json`.

`UploadArtifactAsync` writes a **bare artifact** — it creates no `ProjectWorkspaceFile` row — and
prefixes the caller's layout with the category, so the object that actually lands in the bucket is
`projects/{projectId}/agentFiles/voiceover/…`. The **full** key handed to consumers for a line is
therefore `projects/{projectId}/agentFiles/voiceover/lines/{sha256hex}.wav`, and that is what both
consumers download the line by (see
[Per-line artifact resolution](#per-line-artifact-resolution)). The key returned for the step's own
JSON is what the step records as `WorkflowStepResult.ArtifactStorageKey`. This is the correct column
for a non-playable artifact (see `docs/video-editing.md` — the column is kept separate from
`OutputStorageKey` precisely so the execution UI never mistakes a JSON artifact for a render).

### There is no in-memory per-line cache

`VoiceoverStepExecutor` is registered as a **singleton**, but it now holds no mutable state at all.
A previous design kept a process-wide `ConcurrentDictionary<string, double>` keyed by
`SHA-256("{text}|{voiceId}|{model}|")` mapping to the probed duration, and a hit skipped **both**
the synthesis and the artifact upload. That dictionary is **deleted**, not scoped.

The replacement is the object store itself. Before synthesizing a line, the executor asks
`IProjectFileWorkspace.ArtifactExistsAsync`, which is a **HEAD** (metadata) request and **never** a
download. A hit means "a previous run uploaded exactly these bytes", so the line is not re-sent to
Fish Audio; the object *is* still downloaded to scratch and `ffprobe`d, because the duration has to
be re-measured as *this* execution's output (the entry the step emits is `ok` with a freshly probed
`durationSec` and the same full `storageKey`). A repeat run therefore pays a HEAD, a download and
one ffprobe per line — **never a second Fish Audio request**. Re-synthesis is bought only when the
content, the effective voice, the effective model or the provider identity genuinely changed, since
those are the only inputs to the key.

**Why deleting it, rather than scoping it, was the fix.** The old key's *shape* was the bug, not
the cache's lifetime. The artifact key was built from `Execution.Id`, so a cache hit emitted output
naming objects under *another execution's* prefix — and, for a line the current run's own upstream
had never produced, no object at all. Compile then dropped every line as `line_file_not_found`
while the workflow still reported success: the second run rendered with no narration and said
nothing about it. The execution id is also deliberately absent from the step-cache key, so a
step-cache hit reopened the identical hole. Content-addressing removes the whole class of bug — the
key no longer depends on *which* execution uploaded the object, so a hit verified within the current
execution is a verified claim that the exact object the output names exists, and no decision can
leak across executions.

Because an object written under a content-addressed key is what *every future execution* will find
by HEAD and trust as the real audio for those words, the executor validates an upload with two
independent gates before it stores anything: the payload must carry a canonical RIFF/WAVE container
header, **and** `ffprobe` must decode it to a positive duration with an audio codec. A failure —
reported as the per-line reason `VOICEOVER_PAYLOAD_NOT_WAV` or `VOICEOVER_PAYLOAD_NOT_DECODABLE` —
fails that one line and the bytes are never stored, so a bad vendor payload cannot permanently
poison the cache.

### Step-result caching and the content-addressing invariant

`StepCacheKeyInputs.VoiceoverConfigJson` (`StepCacheKeyInputs.cs`) is part of the step-cache key,
appended by `StepCacheKeyBuilder.cs` and populated by `WorkflowExecutorService.cs`, exactly like
every sibling config blob — without it, two distinct `Voiceover` steps in one project hashed
identically and the second replayed the first's lines as if they were its own.
`StepCacheKeyInputs.SchemaVersion` is **`v2`**; the bump is what makes an entry written under the
older key shape an orphan rather than a false hit.

Content-addressing is precisely what makes caching this step safe. An execution id is not in the
step-cache key, so a Voiceover step's cached output can legitimately be replayed by a *later*
execution — that is the point of the cache. Because the per-line `storageKey` values inside that
replayed output contain no execution id either, they still name objects that exist regardless of
which execution uploaded them, and both consumers resolve lines from those keys. Had the per-line
keys stayed execution-scoped, every cache hit would have replayed keys the new execution never
wrote, and the failure would again have been silent.

---

## The mix path (`VideoCompile`)

`VideoCompileStepConfig` gains two members (`ReelBolt.Shared/Workflows/VideoCompileStepConfig.cs`):

```csharp
bool EnableVoiceover = false,     // false is byte-identical to the pre-voiceover compile path
int? VoiceoverStepOrder = null    // which step's output to take lines from
```

Both defaults preserve the previous behaviour exactly: `EnableVoiceover = false` means no voiceover
is even looked for and the generated ffmpeg command is unchanged. As with the rest of the compile
config, applying audio requires `Mode = Reencode`.

`VideoCompileStepExecutor.ResolveVoiceoverAsync` performs the whole resolution, and like the
music/graphics/SFX/inserts paths it is **purely soft-failure**: a missing or malformed voiceover
step, an unresolvable line, or an ffmpeg build whose `amix` lacks `normalize` all degrade to "no
voiceover applied", never to a failed compile. The `voiceover` node it produces therefore exists on
every path and reports why it did nothing:

```json
{
  "enabled": true,
  "applied": true,
  "unavailable": false,
  "appliedLineCount": 2,
  "lineCount": 2,
  "lines": [ { "lineId": "vo1", "outputStartSec": 2.0, "playDurationSec": 3.0,
               "windowStartSec": 0.0, "windowEndSec": 6.0, "windowSec": 4.0,
               "overflowSec": 0.0, "fits": true, "fitSource": "compile_measured" } ],
  "dropped": [],
  "overlap": true,
  "headroom": { "applicable": true },
  "fit": { "applicable": true, "measuredLineCount": 1, "overflowLineCount": 0,
           "overflowTotalSec": 0.0, "maxOverflowSec": 0.0, "unknownWindowLineIds": [] }
}
```

The two counts on that node are **not** the same fact. `lineCount` is the **declared** count — every
parsed `view.lines` entry carrying a line id, counted *before* the usability filter — and
`appliedLineCount` is the **resolved** count, so the two are equal only when nothing was lost. A node
reading `lineCount > 0` with `appliedLineCount == 0` is stating that the producer *was asked for*
narration and the render about to be produced carries **none** of it, which is the shape
[the declared-and-lost cap](#how-the-overflow-reaches-the-reviewloop) reads.

The non-fatal `reason` values are `voiceover_not_enabled`, `voiceover_step_not_found`,
`voiceover_output_empty`, `voiceover_output_invalid_json`, `voiceover_lines_not_found`,
`voiceover_lines_extraction_failed`, `no_valid_voiceover_lines`, and — for a build whose `amix`
does not expose `normalize` — `unavailable: true` with an explanatory reason. Lines that cannot be
used individually are listed in `dropped` with `line_file_not_found`, `line_cut_away`,
`anchor_cut_away`, `line_past_program_end` or `line_download_or_probe_failed` (an anchored drop also
names its `anchorId`).

Two additions to that node report the **outcome** an operator needs, as opposed to explaining a
lookup that went wrong:

- **`reason: "all_lines_unavailable"`** — set when the step declared at least one line and **not
  one** of them resolved. The declared count is taken from every `view.lines` entry that carries a
  line id, *before* the usability filter, so a step whose lines all carried `durationSec: 0`
  scores as "was asked and delivered nothing usable" rather than as "was asked for nothing". Like
  every other reason on this node it is a report, not a failure: the compile still returns a valid
  result.
- **`voiceover["missingArtifactLineIds"]`** — present only when at least one line's artifact object
  was genuinely absent from the bucket, and naming exactly those line ids. It is kept separate from
  `dropped` (which also carries cut-away and probe failures) because this one array is the
  operator's "the narration is missing from storage" signal. The `NoSuchKey`/`NotFound` path behind
  it logs at **Warning** — it used to be `Information`, a level nobody reads, which is how a
  completely failed TTS produced a green, narration-less render with no signal anywhere — and it
  **names the full storage key** alongside the line id, because the key is the only thing an
  operator can hand to the object store to find out what happened.

What resolution does per line:

1. Place the line on the **output** timeline (`PlanVoiceoverLinePlacements`, before anything is
   downloaded). A line carrying an `anchorId` the artifact knows is placed at the first surviving
   second of that anchor **on the anchor's own source clip** — see
   [Multi-clip narration](#multi-clip-narration). A line without one (`Inline`/`ScriptScenes`) maps its
   declared **source** `startSec` with `OutputTimeline.MapToOutputSec(startSec, 0)`, exactly as
   before. A line whose start was cut away is dropped as `line_cut_away` (unanchored) or
   `anchor_cut_away` (anchored) rather than being silently clamped to zero.
2. Take the object key from the line's own `storageKey` — an `ok` line always carries its full
   content-addressed key — download the WAV to scratch and `ffprobe` it; a file with no audio
   stream or zero duration is rejected. `storageKey` is *omitted*, not null, on `skipped`/`failed`
   lines, so the read is a `TryGetProperty`; when it is absent the executor rebuilds the legacy
   execution-scoped `voiceover/{executionId}/{lineId}.wav`, which is the only object a step result
   persisted before content-addressing can mean. The download enforces the `projects/{projectId}/`
   scope, so a key carried in a step output can never be coerced into reading another project's
   object.
3. Build a `ResolvedVoiceoverLine` carrying the output start, the duration recorded by the
   voiceover step, and a full `GainLinear = 1.0`. Voiceover is deliberately **not** attenuated:
   unlike an SFX cue (bounded, and a deliberate accent), narration is the message.
4. Append the line's output window to a list that is handed to music planning as
   `additionalNoLiftWindows`, so `MusicMixPlanner.PlanLiftWindows` will not lift the music bed while
   narration is playing.

Overlapping lines are detected and set `overlap: true` plus a log line, but are otherwise allowed —
they mix, and the compile never fails over them.

### Multi-clip narration

A `NarrationPlan` line's declared `startSec` is a back-to-back layout the Voiceover step made
**without** a picture (see [`NarrationPlan`](#narrationplan)). The compile used to read it as a time
on source 0 (`VoiceoverSourceIndex`), which is harmless for a one-clip edit and fatal for a multi-clip
one: in an 8-clip narrated story every line written for clips 2..8 either landed over the wrong clip
or fell past clip 1's end and was dropped as `line_cut_away` — 7 of 8 lines, one sentence over 67 s,
while the run reported Completed.

The compile now places an anchored line the way SFX cues and overlays are placed: its `anchorId`
(`s{n}`/`g{n}`/`t{n}`) is resolved through the analysis artifact to `(start, end, sourceIndex)`, and
`OutputTimeline.MapWindowToOutput(start, end, sourceIndex)` gives the first second of that anchor
that survived the cut **on its own clip**. Anchored lines are then laid out in output order, and a
line that would start before the previous anchored line has finished is pushed back to that line's
end — two narration lines are never spoken over each other — reported on the line node as
`shiftedSec`. A line pushed to the end of the program is dropped as `line_past_program_end`.
Anchored line nodes additionally carry `anchorId` and `sourceIndex`; unanchored nodes keep their
exact previous shape.

Everything downstream follows the placed start: the mix's `adelay`, the caption cues (which are cut
from the placed lines), the duck/replace windows, the music no-lift windows, `voiceover.headroom`
(the shot under the line is looked up on the line's own clip), `voiceover.wer`, and the
picture-window fit below, which for an anchored line is measured against whatever placement is on
screen at its start (`OutputTimeline.TryGetContainingPlacement(outputSec)`, any clip). Pickups follow
the same rule: a pickup's `t{n}` segment is mapped through **its own** source clip.
`OutputTimeline.MapToOutputSec` also no longer gives up at the first span of a clip that starts after
the requested second, so a clip that returns later in the cut with an earlier part of itself still
maps.

### Picture-window fit facts

Every resolved line is also measured against the output placement its start landed in, additively on
the same node: `windowStartSec`, `windowEndSec`, `windowSec`, `overflowSec` and `fits` — written only
on a line whose containing placement was found — plus `fitSource` on **every** resolved line, and
`solverFit` on the lines whose upstream output carried one. Nothing pre-existing on the node changes —
the new keys are appended after every key that was already there, so a pre-wave consumer reading
`lineId`/`outputStartSec`/`playDurationSec` sees exactly what it saw before.

Nothing in this block can fail the compile: the window lookup is a list walk and the arithmetic is
total. `unknownWindowLineIds` stays the reported home for a line whose containing placement could not
be found, but it is **empty by construction** — the lookup runs on the raw `MapToOutputSec` second,
which is always inside its own placement, so no line that the mapper placed can miss it. It remains on
the `applicable: true` shape for a caller that supplies a second from somewhere else. A compile with
nothing measurable records `{ "applicable": false, "reason": "no_resolved_lines" }` — a shape with no
`unknownWindowLineIds` at all — and still returns a valid result.
[The fit solver and the review loop](#the-fit-solver-and-the-review-loop) has the meanings, the
degradation reasons and how the roll-up reaches the compile's own output.

`EnableVoiceover = false` remains **byte-identical** to the pre-voiceover compile path — no extra
ffmpeg inputs, no new filtergraph, and no `voiceover` node at all — so the fit facts are reached only
by a compile that was already asking for narration.

### The ffmpeg fragments

`VoiceoverMixFilterBuilder` (`WorkflowEngine/Services/Video/SfxMixFilterBuilder.cs`) mirrors
`SfxMixFilterBuilder`'s role:

- `BuildVoiceoverLineBranch(inputIndex, lineIndex, line)` emits one line's branch:
  `[{inputIndex}:a]atrim=end={duration},asetpts=N/SR/TB,aformat=sample_rates=48000:channel_layouts=stereo,volume={gain},adelay={ms}|{ms}[vo{k}]`.
  `asetpts` normalizes the branch to PTS 0 regardless of container start-time weirdness,
  `aformat` is mandatory because `amix` requires matching sample rate and channel layout across
  inputs, and `adelay` (integer milliseconds, one value per channel) is the **one and only place a
  line's timing enters the filtergraph** — and it was computed server-side, from the step's own
  recorded start time, never from anything a model wrote.
- `BuildVoiceoverMixStage(baseLabel, lineCount, finalLabel)` emits the mix:
  `{base}[vo0][vo1]…amix=inputs={n+1}:duration=first:dropout_transition=0:normalize=0{final}`.

Those three `amix` options are load-bearing, for the same reasons music and SFX spell them out:
`normalize=0` (without it `amix` divides every input's level by the input count, quietly turning the
dialogue *down*), `duration=first` (pins the mixed length to the base input, so a line near the end
can never extend the file), and `dropout_transition=0` (no gain re-ramp when a line's branch ends
before the base does — which most do).

Labels chain in a fixed order: music first, then voiceover, then SFX. With voiceover present the
pre-voiceover audio becomes `[abase]` and the voiceover mix stage becomes the new final label (or
`[abase]` again when SFX is also present, which then mixes into it). Each resolved line is added as
one extra `-i` input, in exactly the order the branch calls assumed.

When the source has no audio stream and there is no music bed, the base the lines mix into is a
synthesized program-length silence (`anullsrc`) — narration over silent b-roll used to ship with no
audio track at all. See [`video-editing.md` "Program audio on silent footage"](video-editing.md#program-audio-on-silent-footage).

### Where it surfaces

- `outputSummary["voiceover"]` — the node above is copied into the compile step's output summary, so
  a reviewer can see which lines landed and why others did not.
- `edl["voiceover"]` — the same node is written into the compile **EDL** artifact when voiceover was
  attempted, alongside the music/graphics/SFX/color-grade nodes.

---

## Narration and the end of the program

Two things used to cut the last narration line of a `VideoCompile` short, and both are fixed in the
compile step itself (`VideoCompileStepExecutor`, both encode paths):

1. **The program audio fade-out faded the narration.** `ProgramAudioFadeOutMs` was the last audio
   stage, after the voiceover mix, so a line still being spoken inside the fade window was faded
   mid-word. Now, whenever a tail stage exists (a program audio fade, a seam audio ramp, or the hold
   below), the narration is mixed **after** it: the base program (source dialogue, music, sound
   effects) goes through the tail into an internal `[anar]` label, and the voiceover mix stage
   `[anar][vo0]…amix…[aout]` runs last. The fade still shapes music, dialogue and effects exactly as
   before; it never touches a line. With no tail stage nothing moves, so a compile without a program
   fade is byte-identical.
2. **A line running past the last picture frame was cut off.** The voiceover `amix` is
   `duration=first` (pinned to the program), so a line ending after the picture simply stopped. Now
   `ComputeNarrationHoldSec` measures how far the last line's end, plus `NarrationTailSec` (0.25 s),
   runs past the program — a generated cold open in front of the body moves every body-timed line by
   its own length — and the compile **holds the last picture frame** for exactly that long:
   `tpad=stop_mode=clone:stop_duration={hold}` as the first stage of the picture tail (before the
   program fade, after censoring, so the held frame is the finished picture; captions are drawn
   after it), and `apad=whole_dur={program+hold}` as the first stage of the audio tail, so the base
   the narration is mixed over lasts as long as the picture. The video and audio program fades are
   re-resolved against the lengthened program, so the fade-out still finishes on the last frame —
   now a quarter second after the line ends.

A hold of 0 (every line fits) changes nothing. When there is a hold:

- `outputSummary.outputDurationSec` is the lengthened program;
- `outputSummary.voiceover.extendedForNarrationSec` and, when a program fade is configured,
  `outputSummary.programFade.extendedForNarrationSec` report the hold in seconds (both also in the
  EDL);
- the step reports "Holding the last frame … so the narration can finish" while it runs.

The held frame is the program's own last frame, inserts and overlays included — the hold is applied
after every picture stage except captions. Music is not extended: under a hold it ends where the
picture would have, its own fade-out included, and the line finishes over silence (or over the held
source audio's padding). There is no configuration member for any of this: cutting a line off was
never a choice anyone wanted.

---

## The promo pipeline path (`ScriptScenes`)

For the existing promo pipeline (`Scriptwriter` → `Director` → `Author`), narration reaches the
video without any new template or agent. The chain is:

1. A `StepType.Voiceover` step with `Source.Kind = ScriptScenes` and `ScriptwriterStepOrder`
   pointing at the pipeline's `ScriptwriterAgent` step. The executor reads each scene's `Voiceover`
   text and `StartTime`, synthesizes one WAV per narrated scene into the content-addressed key
   `voiceover/lines/{sha256hex}.wav`, and records each line's probed duration **and its full
   `storageKey`** in the step output — that key, not a reconstructed path, is what the staging side
   reads.
2. `AuthorAgent` calls `StageVoiceoverAudio` (its prompt instructs it to call the tool once, step
   14). `ReactRemotionSandboxTools.StageVoiceoverAudio` finds the latest **Completed**
   `StepType.Voiceover` result and the latest **Completed** `ScriptwriterAgent` result for the same
   execution, walks the scenes in order, skips whitespace-only voiceovers, skips lines whose status
   is not `"ok"`, and copies each remaining line's WAV into the sandbox at
   `public/voiceover/scene-{n}.wav` — under `public/` because that is the only folder Remotion's
   `staticFile()` reads (until October 2026 the files went to the workspace root, where
   `staticFile()` could not find them) — **0-indexed by scene position**, so the mapping between scene index
   and voiceover-line index survives empty scenes. Each line's object is resolved from **the emitted
   `storageKey`**, with the legacy execution-scoped `voiceover/{executionId}/{lineId}.wav` used only
   as a fallback when a step result carries none. It returns
   `{ "staged": ["voiceover/scene-0.wav", …], "failed": [] }` — the paths `staticFile()` takes,
   without the `public/` prefix.
3. The **`failed` array is additive**, and `staged` is unchanged in both shape and contents, so
   `AuthorAgent`'s existing parsing of the result is unaffected. An entry is
   `{ lineId, scenePosition, reason: "object_not_found" }`, added when the line's object is
   genuinely missing from the bucket. `NoSuchKey`/`NotFound` is **no longer swallowed** by the
   generic catch — that catch previously logged at Error next to every other failure while the tool
   still answered `{"staged":[]}`, so a promo rendered without narration and nothing in the tool
   result said why. The missing-object path is now a Warning that names the full key, the line id
   and the scene position, *and* reports itself to the caller.
4. `AuthorAgent` then passes `voiceoverSrc="voiceover/scene-{n}.wav"` for exactly the scene positions
   that appear in `staged`, and omits the prop for every other scene. An empty `staged` array
   always carries a `reason` (see "Narration that is not scene-sourced" below); `no_voiceover_step`
   is the normal case for a promo with no narration, and the prompt says to omit the prop everywhere
   then.
5. `sandbox/template/src/root.tsx` implements the receiving half: each of `SceneWordmark`,
   `ScenePills` and `SceneCta` takes an optional `voiceoverSrc?: string` and renders
   `{voiceoverSrc && <Audio src={staticFile(voiceoverSrc)} />}`; the `LaunchPromo` composition
   exposes `voiceoverSrc0`/`voiceoverSrc1`/`voiceoverSrc2` and passes one per `<Sequence>`. The
   file's own comment documents the `voiceover/scene-{n}.wav` convention and points here.

The tool lives in `ToolGroup.SandboxAuthoring` (`AgentToolProvider.cs`), so it is reachable by
exactly the agents that already have sandbox authoring — `AuthorAgent` among them — and by nobody
else.

### Narration that is not scene-sourced

The scene mapping above applies only when the Voiceover step spoke the Scriptwriter's scenes
(`Source.Kind = ScriptScenes`, or a step with no readable config, which is how step results before
this rule were read) **and** a completed `ScriptwriterAgent` result with scenes exists. In every
other case — an `Inline` voiceover (lines written into the step with their own `startSec`), a
`NarrationPlan` or `PickupPlan` voiceover, or a scene-sourced one whose Scriptwriter result is
missing or unreadable — `StageVoiceoverAudio` stages **every `ok` line by its own id** at
`public/voiceover/{lineId}.wav` (`voiceover/vo0.wav`, …), through the same copy path and the same
`storageKey` (with the same legacy fallback). The answer is

```json
{ "staged": ["voiceover/vo0.wav", …],
  "failed": [{ "lineId": "vo3", "reason": "object_not_found" }],
  "lines": [{ "path": "voiceover/vo0.wav", "lineId": "vo0", "text": "…", "startSec": 0.0,
              "durationSec": 2.41, "durationInFrames": 73, "fps": 30 }, …] }
```

where `text` is the sanitized text actually spoken, `startSec` the line's declared start and
`durationSec` its measured length. A line id that is not a plain file name (`[A-Za-z0-9_-]`, up to
64 characters) is not staged and is reported as `invalid_line_id`; any other copy failure is
`stage_failed`.

Before this (issue 16, run `a540ff38`: an `Inline` voiceover with 7 spoken lines and no Scriptwriter
step) the tool answered `{"staged":[],"failed":[]}` and the Author rendered a film with no
narration. **The tool never answers an empty success any more.** An empty `staged` array always
comes with a `reason` and a plain `message`:

| `reason` | When |
|---|---|
| `no_voiceover_step` | The run has no completed Voiceover step. The only case the Author treats as "no narration". |
| `voiceover_output_unreadable` | The Voiceover result has no `view.lines` array. |
| `no_spoken_lines` | Every line was skipped or failed. |
| `nothing_staged` | Spoken lines exist but none could be copied (see `failed`). |
| `unexpected_error` | The tool itself failed. |

A successful scene-mapped answer is byte-identical to what it was; `reason`/`message` appear only
when `staged` is empty.

---

## Text sanitization

Every line's text is passed through `VoiceoverTextSanitizer.Sanitize(text, int.MaxValue)` before the
byte budget is measured and before anything is sent to Fish Audio. The executor still passes
`int.MaxValue` as the length cap: `MaxBytes` is the real budget, and silently truncating narration
by byte count would produce audio that no longer matches the script.

The pass order is: **cap the raw input at 4096** → strip Unicode `Cc` control **and** `Cf` format
characters → strip `[...]` (non-greedy) → strip `(...)` (non-greedy) → strip `https?://\S+` URLs →
collapse runs of whitespace → trim → truncate by Unicode text element (grapheme cluster, via
`StringInfo`) so a surrogate pair or a combining mark is never split mid-character.

Three guarantees on that order are load-bearing, and each one is enforced by
`VoiceoverTextSanitizerTests`:

- **The raw input is capped at `VoiceoverTextSanitizer.HardMaxRawChars` = 4096 characters BEFORE any
  regex runs.** The bracket patterns are non-greedy and backtracking, so on a line with many
  unmatched open brackets and no closer they cost O(n²) — a 1.28M-character line of `[` measured
  28.5 s of CPU on the engine shared by every tenant. The cap keeps those patterns off an unbounded
  string in the first place; a match timeout alone would only have turned a slow line into a thrown
  `RegexMatchTimeoutException`, i.e. a failed step. **The consequence, stated honestly: a line
  longer than 4096 characters is silently truncated to 4096, and the remainder is never spoken.**
  4096 UTF-16 code units is roughly 700 words — several minutes of continuous speech, for a line
  whose whole point is to be anchored to a start time — so no legitimate line approaches it, but the
  truncation is silent by design, and `MaxBytes` is measured against what survives it.
- **Control (`\p{Cc}`) and format (`\p{Cf}`) characters are stripped BEFORE the bracket tags.** This
  ordering *was* the bug. When the bracket patterns ran first, `[whis\npering]` was untouched by them
  (`.` does not match `\n`), and the control-character strip then simply *deleted* the newline —
  reconstituting the literal tag `[whispering]` in the text handed to Fish Audio. Stripping first
  yields `[whispering]`, which the bracket patterns then remove normally. `Cf` is a separate
  category and invisible by definition: U+200B, U+202E, U+FEFF, U+00AD, U+2066 and U+2069 all used
  to reach the TTS request verbatim.
- **Both bracket patterns are `RegexOptions.Singleline`, and every static `Regex` in the class
  carries a finite 100 ms match timeout.** `Singleline` makes `.` match `\n`, so a tag split by a
  literal newline is still matched as one tag — defense in depth over the ordering fix, not a
  substitute for it. The timeout is finite on every pattern, because a `Regex` constructed without
  one silently gets the infinite timeout back, which is how the super-linear behaviour above went
  unnoticed.

Both bracket forms are stripped because both are meaningful to Fish Audio and both would otherwise
be *spoken* when they came from model- or media-derived text: `[...]` is the S2-family /
`drama-3-preview` emotion syntax, `(...)` is `s1`'s fixed-vocabulary equivalent. Stripping only the
square form — which is what the feature request originally specified — would have left `(excited)`
audible on any `s1` provider row. Since phase 1 offers no emotion control at all, no legitimate
config can be harmed by removing them.

---

## Per-line artifact resolution

The producer and both consumers agree on **one** key per line, and that agreement is what makes a
cached or replayed `Voiceover` step work at all:

- **The producer** (`VoiceoverStepExecutor`) uploads each line under the content-addressed relative
  path `voiceover/lines/{sha256hex}.wav`, where the hash is lowercase SHA-256 over the
  [six-field material described above](#artifacts-caching-and-where-the-audio-lives), and records
  the **full** key —
  `projects/{projectId}/agentFiles/voiceover/lines/{sha256hex}.wav` — on the line in its output as
  `storageKey`. That property is *omitted*, not null, on `skipped`/`failed` lines, which is exactly
  the signal a consumer uses to tell "this line has an artifact" from "this line does not".
- **`VideoCompileStepExecutor.ResolveVoiceoverAsync`** reads each line's own `storageKey` and
  downloads it. It does **not** list project files: no `ProjectWorkspaceFile` row is ever created
  for a bare artifact, so a `ListFilesAsync` match on a file whose key contains the line id could
  never find one, dropped every line as `line_file_not_found`, and still reported the compile
  Completed. Only when a step result carries no `storageKey` — i.e. output persisted before
  content-addressing — does it rebuild the legacy execution-scoped
  `voiceover/{executionId}/{lineId}.wav`, which is the only object such a result can mean.
- **`ReactRemotionSandboxTools.StageVoiceoverAudio`** resolves each scene's line the same way: the
  emitted `storageKey` first, the legacy execution-scoped key only as a fallback.

Because the content-key formula changed, per-line WAVs written under the old formula are now
ordinary cache misses: nothing migrates them, so the next run re-synthesizes those lines once and
uploads them under their new keys. That is a one-time vendor cost, paid per line, after which the
objects are content-addressed as described above. The legacy execution-scoped fallback in the two
bullets above is unchanged — it is still the only key a `storageKey`-less step result can mean.

Four properties of this contract are worth remembering before changing anything about it. First, the
key contains **no execution id**, which is why the step is safe to cache and why a cache hit replayed
in a later execution still names objects that exist. Second, the key is derived from what was
actually synthesized — `effectiveVoiceId`, not the config's raw `DefaultVoiceId` — so two configs
that differ only in default voice cannot collide on one object. Third, its value fields are
**length-prefixed** rather than joined by a delimiter, so the key is injective by construction: two
distinct field tuples can never share one object. Fourth, it covers the project's **provider identity
and the model actually sent**, so re-pointing the step at another provider row — or editing that
row's model — produces different objects instead of silently reusing another configuration's audio.

---

## Admin surfaces

- **`InferenceProvidersController`** carries the kind/capability pairing rules described above, the
  `SpeechSynthesis` member of the accepted-capability list, and a `SpeechSynthesis` branch in both
  test endpoints. `RunSpeechSynthesisTestAsync` synthesizes a one-word `"ok"` request through the
  saved (or unsaved) provider and validates the result with `WavDuration.TryGetSeconds` — no ffmpeg,
  no new dependency — treating "audio with zero duration" as a failed test. It must be
  `WavDuration`, not a strict RIFF parse: Fish's `/v1/tts` answer is a *streamed* WAV whose header
  carries a placeholder data size (0 or 0xFFFFFFFF), which a strict parser reads as "no audio" and
  so failed every working Fish provider with "Provider returned audio with zero duration." (the
  fixture is assumed, see [Browsing the Fish voice library](#browsing-the-fish-voice-library)).
  The provider detail page labels the per-capability button **Test again** once it has a last
  test, and refreshes `LastTest*` from the saved-capability test endpoint. Like every
  other test branch it never lets a bad endpoint, model or key escape as a 500.
- **`web/lib/types/inference-provider.ts`** mirrors the enums, including the `FishAudio` kind with
  its `SpeechSynthesis`-only note and a pointer to this document.
- **`InferenceProviderForm`** exposes `Kind` and `Capability` as fields and renders the Fish Audio
  licence notice for a `FishAudio` row.
- **The workflow builder** ships a `VoiceoverStepConfig` editor (Inline line list with per-line
  text/start/voice; a Scriptwriter-step picker for `ScriptScenes`; a NarrationWriter-step picker for
  `NarrationPlan`; provider id, model, default voice, byte budget), a `VoiceoverNode` for the
  flowchart, and a JSON schema in `web/lib/schemas/workflow-step-configs.ts` whose `VoiceoverSource`
  definition pins `kind: ['Inline', 'ScriptScenes', 'NarrationPlan']` and adds
  `narrationWriterStepOrder` (`integer` or `null`, minimum 1). A `NarrationPlan` source with no step
  chosen is reported as **unconfigured** rather than merely empty —
  `getVoiceoverSourceError`/`NARRATION_PLAN_STEP_REQUIRED_ERROR` in
  `web/lib/utils/voiceover-validation.ts` — so the builder says so instead of the step failing at
  execution time. Step orders are 1-based, so `0` counts as unconfigured too.

The two halves of that kind now agree, and it is worth being precise about where, because for one
commit window they did not. The frontend mirror (frontend `029b8998`) accepts and validates
`NarrationPlan`; the engine half is committed too (backend `ba1160f1`) — the enum member, the config
member, the executor arm and the save-time arm below — so a config the builder accepts is a config
the engine can execute. The validation is deliberately duplicated on both sides rather than left to
one: the builder blocks early for the author's sake, and the save-time validator plus
`NARRATION_SOURCE_INVALID` remain the authority, since the API is reachable without the builder.

### Provider-network hardening

A provider row's `Endpoint` is operator-supplied, which makes every path that dials it an SSRF
surface. These are the checks that make such an endpoint safe to call.

`IsDisallowedEndpoint` gates Create, Update, and the **unsaved** `POST /test` path. The third is the
one that was missing: "test an unsaved config" finalized its endpoint — from the caller, or inherited
from the saved row when the stored key is reused — and went straight to a client, so the one test
path that persists nothing was also the one where the server could be aimed at `169.254.169.254`,
`127.0.0.1` or a Docker bridge address and make the request on the caller's behalf. The check now
sits *after* that id-inheritance has settled the endpoint and *before* any client is constructed, so
a rejected endpoint never reaches a factory at all. (The saved-capability test, `…/capabilities/{capId}/test`, does not re-run it — that
endpoint comes from a provider that already cleared the same gate when it was written.)

What the predicate refuses is an address, not a string. It rejects anything that is not an absolute
`http`/`https` URL, anything whose host cannot be resolved, and any resolution that yields a
non-public address. The blocklist covers loopback (`127/8`, `::1`), the unspecified addresses
(`0.0.0.0`, `::`), link-local (`169.254/16`, `fe80::/10`), the RFC 1918 ranges (`10/8`, `172.16/12`,
`192.168/16`), CGNAT shared space (`100.64/10`), IPv6 unique-local (`fc00::/7`) and the deprecated
site-local `fec0::/10`. `::ffff:127.0.0.1` is classified as its IPv4 self rather than being waved
through by the IPv6 branch, and an address in a family the code does not understand fails closed. An
IPv6 literal arrives from `Uri.Host` still wearing its brackets, which `IPAddress.TryParse` will not
accept, so the brackets are stripped before parsing — otherwise a literal would fall through to DNS,
fail to resolve, and be rejected for entirely the wrong reason (a public IPv6 literal wrongly with
it).

A hostname is checked against **every** address it resolves to, not just the first
(`IsDisallowedAddressSet`). A name carrying one public and one private record — split-horizon DNS, or
a deliberately poisoned entry — has to be refused, because which record a given connect actually uses
is not something the caller controls.

Two client-side limits bound what a hostile endpoint can do once a request is under way.
`SpeechSynthesisClientFactory.BuildFishAudio` builds its `SocketsHttpHandler` with
`AllowAutoRedirect = false`: the endpoint was validated once, at write time, and a 3xx from it would
hand the request to a second host that was never validated at all — the "point the vendor at
`169.254.169.254` and let the vendor's own client fetch it" move. Refusing the redirect turns such a
response into an ordinary non-success status the caller can report. On the response side,
`FishAudioSpeechSynthesisClient` buffers a success body behind `MaxAudioResponseBytes` (32 MiB,
roughly nine minutes of 48 kHz 16-bit mono WAV), checking both the declared `Content-Length` and the
bytes actually arriving — a hostile endpoint can simply omit or understate the former, so the
streaming check is what makes the cap real, and the size message names the limit without carrying any
part of the payload. An error body is read only up to `MaxErrorBodyBytes` (4 KiB), with the remainder
left unread on the wire, and the text that reaches the exception message is truncated independently,
since a JSON `message` field is exactly as attacker-controlled as the bytes around it and is
interpolated into a logged `HttpRequestException`.

This is the speech-synthesis path only. Whether the sibling chat, transcription, vision and
video-generation factories set redirect or read limits of their own was not examined, and is not
asserted either way.

### How a `Voiceover` step is created and persisted

`voiceoverConfigJson` is a first-class field on `CreateWorkflowStepRequest` and
`WorkflowStepResponse` (`Controllers/Dto/ProjectDtos.cs`), not a blob smuggled through some other
field. The `WorkflowsController` read paths project it, and both `WorkflowEditDiffService` — which
diffs it, so editing a Voiceover step's config is a real, reviewable diff — and
`WorkflowTemplateProvisioningService` carry it exactly like every sibling config.
`StepConfigSaveValidator` now has a `StepType.Voiceover` arm, so a config-bearing `Voiceover` step
with **no** config is rejected **at save time** instead of failing mid-run with `CONFIG_INVALID`.
That arm is also **domain-aware** for exactly one kind: a config that deserializes to
`VoiceoverSourceKind.NarrationPlan` is checked further, and a missing, null, zero or negative
`narrationWriterStepOrder` is rejected at save time with

```
Step 4 (Voiceover): VoiceoverConfigJson source narrationWriterStepOrder is required and must be at
least 1 when the source kind is NarrationPlan.
```

The same reasoning as the missing-config check: the executor hard-fails exactly that config with
`NARRATION_SOURCE_INVALID`, so saving it only defers the failure to a real execution run. The arm is
deliberately **additive** — `Inline` and a `ScriptScenes` config (including one whose
`ScriptwriterStepOrder` is absent, which the *executor* rejects as `SCRIPT_SOURCE_INVALID`) stay
savable exactly as they were, and an unrecognised `kind` string is left to the executor's
`SOURCE_INVALID` rather than becoming a new save-time rejection. The message goes through the shared
step-error prefix, because that prefix is what every save path renders into one 400 body.

On the frontend, a newly added Voiceover step is assigned the `VideoTransform` placeholder agent in
both the add path (`FlowchartBuilder.handleAddStep`) and the change path (`StepCard`) — without that
it could not be saved at all, since `WorkflowStep.AgentDefinitionId` is non-nullable. Its editor is
reachable from the flowchart node: clicking the node selects it and opens the `StepConfigPanel`
drawer, whose Voiceover branch renders the type-specific `VoiceoverStepConfig` editor. The node
itself is keyboard reachable — `role="button"`, `tabIndex={0}`, an `aria-label`, and an
`Enter`/`Space` key handler forwarding to the click.

The editor warns before a run rather than after it. `VoiceProviderStatusNotice`
(`web/components/workflows/`) calls `getProviderStatus('SpeechSynthesis')`
(`GET /api/v1/inference-providers/status?capability=SpeechSynthesis`, readable by every signed-in
user and carrying no endpoint or key) and shows a red **Narration will probably fail** box when
`healthy` is false, or a yellow **Using a backup voice service** box when `usingFallback` is true,
preferring the server's own `message`. It renders nothing while loading, on an error (an older API
without the endpoint) or for a healthy provider. The **Voice** section links to the project's
`?tab=voices` page; model, default voice id, byte budget, pronunciation and casting sit behind the
shared `AdvancedSection` disclosure (`config-kit.tsx`), which opens itself when the assistant's
proposal or a rejected save names a field inside it.

---

## Phase 1 scope, and what phase 2 delivers

Phase 1 shipped the spine: a deterministic `Voiceover` step, content-addressed per-line audio, a
soft-failing compile mix path, and two source kinds. It was deliberately narrow. Phase 2 was scoped
to add four things on top of that spine, and **all four are now in the wave's committed branches**:
the planning agent and its id-anchored schema, word alignment over the synthesized lines, the fit
measurement — which landed as **two** halves, the deterministic `NarrationFitSolver` on the producing
step and the picture-window facts on the compile — and the `NarrationPlan` source kind plus the
template that wires the whole narration path together. This section names each with the branch and
commit it was verified against, and the next section covers what phases 3 and 4 added on top.

### Delivered by phase 2

- **`NarrationWriter`** — phase 1 narrated text a human wrote (`Inline`) or text an existing
  `ScriptwriterAgent` step had already produced (`ScriptScenes`); nothing turned a picture edit into
  narration. Phase 2 adds the agent and its id-anchored schema — see
  [Narration planning](#narration-planning-narrationwriter).
- **Word alignment** — nothing mapped synthesized words back onto audio. Phase 2 records per-line
  word timings by re-transcribing each synthesized WAV through `ITranscriptionClient`, and degrades
  to *absent* words rather than to an error — see [Word alignment](#word-alignment). Phase 1 noted
  that Fish Audio exposes word-level timestamps through
  `POST /v1/tts/stream/with-timestamp` (SSE) and `wss://api.fish.audio/v1/tts/live/with-timestamp`,
  and guessed they would be "simpler and cheaper" than a re-transcription round trip. **That guess
  does not hold for this deployment: the self-hosted `fish-tts` returns `404` on
  `POST /v1/tts/stream/with-timestamp`**, which is why phase 2 aligns through the transcription
  client and why neither Fish endpoint is consumed.
- **The picture-window fit facts** — nothing measured narration against the picture; durations were
  probed and reported, and no number reached the compile's output. Phase 2 measures every resolved
  line against the shot it starts in, reports `overflowSec`/`fits` per line plus the node-level
  `voiceover.fit` roll-up, and carries that roll-up into the compile step's own output JSON — so a
  `Conditional` or `ReviewLoop` step downstream of the compile can gate on `overflowLineCount` /
  `maxOverflowSec`. It is deterministic code, never an agent — no model computes a duration anywhere
  on this path — see [The fit solver and the review loop](#the-fit-solver-and-the-review-loop).
- **The `NarrationFitSolver`** — the same question asked in words, on the producing step:
  `NarrationFitSolver` (`ReelBolt.Shared/Workflows/NarrationFitSolver.cs`) measures every line
  against its **declared narration window** and emits per-line `fit` plus the `meta.fit` report and
  the promoted `meta.overflowWords`/`meta.overflowLineCount` — see
  [The declared window on the producing step](#the-declared-window-on-the-producing-step-narrationfitsolver).
  Deterministic code, never an agent: no clock, no RNG, no I/O, no ffmpeg.
- **The `NarrationPlan` source kind** — the piece that lets a `Voiceover` step actually *consume* a
  `NarrationWriter` step's plan: `VoiceoverSourceKind.NarrationPlan` appended last,
  `VoiceoverSource.NarrationWriterStepOrder`, `VoiceoverLine.AnchorId`, the executor arm with its
  three narration failure codes, the `NarrationPlan` save-time validation arm, and the placement pass
  that declares the line starts — see [`NarrationPlan`](#narrationplan). The frontend has the matching builder, schema and client-side
  validation.
- **The reviewer's narration clause and its deterministic cap** — the compile node carried
  `voiceover.fit` and the voiceover step carried `meta.fit`, but nothing downstream *read* them:
  `VideoReviewAgent`'s prompt listed only the pre-wave facts, so a loop that had to react to
  overflow had to gate on the step output itself. Phase 2 adds a `voiceover` clause to the prompt
  (and its byte-identical seeder copy) and `ReviewLoopStepExecutor.ApplyNarrationFitCap` —
  `min(score, 4)` on a measured positive overflow, inert otherwise — so the fact is acted on
  whether or not the model heeds the instruction. See
  [How the overflow reaches the `ReviewLoop`](#how-the-overflow-reaches-the-reviewloop).
- **The empty-narration contract** — a well-formed `NarrationPlan` carrying zero lines used to be a
  `NO_LINES` failure, so a `NarrationWriter` doing exactly what its own prompt instructs reddened a
  correct run and, after retries, failed the execution. It is now a **successful, empty** `Voiceover`
  step, marked by `meta.emptyReason: "narration_plan_empty"`. `NO_LINES` deliberately remains a
  failure for `Inline` and `ScriptScenes`, where an empty resolved list is an author misconfiguration
  rather than a decision — see
  [An empty plan is a successful step](#an-empty-plan-is-a-successful-step). The same commit gives
  `{"lines": null}` and a `null` entry inside `lines` their own `NARRATION_OUTPUT_INVALID` instead of
  escaping as `UNEXPECTED_ERROR`.
- **The declared-and-lost narration cap** — the overflow cap reacts only to a *measured* overrun, and
  a compile that applied **zero** narration lines has no measurable fit at all, so a green review
  score advanced the loop over a narration-less render. `ApplyNarrationDeclaredLostCap`
  (`min(score, 4)`) now caps exactly that shape — the `amix`-unavailable arm included, since a
  narration-less render is a defect whichever side caused it — and is inert only for a compile with no
  `voiceover` node and for the empty plan. `NarrationCapsWouldFire` makes the calibrated decision
  gate's early stop escalate rather than accept when either cap applies — see
  [How the overflow reaches the `ReviewLoop`](#how-the-overflow-reaches-the-reviewloop).
- **The declared count on the compile's node, and the raw window lookup** — `lineCount` was a copy of
  `appliedLineCount`, which made the key pair the cap is keyed on unsatisfiable on the very path it
  was written for (`all_lines_unavailable` reported `0/0`). It now reports the **declared** count, so
  both cap forms are live; the picture-window containment lookup also moved onto the raw
  `MapToOutputSec` second, which stops a line that rounds onto its shot's exclusive end from being
  dropped out of `measuredLineCount` and leaves `unknownWindowLineIds` empty by construction. No key
  renamed or reordered; on every path where declared equals resolved the emitted JSON is unchanged.
- **The `video-derush-edit-voiceover` template** — the twelfth entry in `WorkflowTemplateCatalog.cs`
  and the first catalog entry that wires narration end to end, in **six** steps:
  `VideoAnalyze` (`Source: ProjectFile`) → `Agent(VideoStoryEditor)` → `Agent(NarrationWriter)`
  (`AgentInputContextMode: FullWorkflow`) → `Voiceover`
  (`{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":3}}`) → `VideoCompile`
  (`enableVoiceover: true`, `voiceoverStepOrder: 4`, `decision: Step 2`, `analysisStepOrder: 1`) →
  `ReviewLoop(VideoReviewAgent)` looping back to step 2. `AutoCreateOnProject: false`, like every
  other derush template.

  **Why six steps and not five.** The catalogue makes `VideoCompileStepConfig.Decision` a required
  positional member, so a template with no `VideoStoryEditor` step before its compile step cannot
  build at all — the narration steps are inserted *between* the story editor and the compile, not in
  place of either. The `Decision`/`Voiceover` refs are explicit `StepOrder`s rather than `Previous`
  for the same reason every sibling template spells them out: `Previous` relative to the compile step
  would resolve to the voiceover step's output (step 4), not the story editor's decision (step 2).
  `Mode` is left at its `Reencode` default, which `EnableVoiceover` requires.

## Phases 3 and 4: what landed after narration planning

Phase 2 ([Narration planning](#narration-planning-narrationwriter) through
[The fit solver and the review loop](#the-fit-solver-and-the-review-loop)) made narration a decision an
agent takes and a measurement code makes. Phases 3 and 4 build on that spine without changing its
contract: every addition below is an opt-in whose default is byte-identical to the phase-2 behaviour,
every model-authored output stays id-anchored and number-free, and every soft stage degrades with a
recorded reason rather than failing a compile. Each subsection names the code that owns it.

### Captions

`VideoCompileStepConfig.EnableCaptions` (default `false`, byte-identical off, requires
`Mode = Reencode` — `CAPTIONS_REQUIRE_REENCODE` otherwise) burns the applied narration into the
picture during the same encode. `CaptionStyle` is `Subtitle` (a chunk of text per cue, split at word
boundaries at `CaptionMaxCharsPerCue`), `Karaoke` (the same chunks, with the words already spoken
redrawn left-anchored in `CaptionHighlightColor` as each word begins) or `Punch` (one word at a time,
larger and centred).

Two facts about where the words come from are load-bearing:

- **The text is the Voiceover step's own sanitized, actually-synthesized text** — the `text` key every
  `ok` line now carries — never the narration plan's raw text. If the sanitizer dropped a `[tag]` or a
  URL, the caption does not say it either; a caption that says words the narrator never spoke is a
  correctness bug, not a cosmetic one.
- **The timing is the measured word alignment** (`words[]` on the line, from
  [Word alignment](#word-alignment)), mapped onto the output timeline from the line's resolved start.
  `Subtitle` works without words too — each chunk then owns a share of the line's play duration
  proportional to its characters — but `Karaoke` and `Punch` need words and degrade per line with
  `no_word_alignment` when there are none. A deployment with no Transcription provider therefore gets
  Subtitle captions and no Karaoke, and the node says so.

The cues are cut by `CaptionCueBuilder` (`WorkflowEngine/Services/Video/CaptionFilterBuilder.cs`)
and burned through **libass**: `AssSubtitleBuilder` (`WorkflowEngine/Services/Video/AssSubtitleBuilder.cs`)
writes one ASS script, `captions.ass`, into the step's scratch space at encode time — laid out for the
canvas actually being encoded, `PlayResX`/`PlayResY` equal to its size — and the picture stage is a
single `ass=filename='…':fontsdir='…'` filter. Captions are the **last picture stage** of all — after
censoring and the program fade — so a caption is never dipped or faded with the picture it annotates.
Both encode paths (single-source and segmented) carry the stage.

**The text never enters the filter string**, the same discipline as drawtext's `textfile=` +
`expansion=none`: the filter names two paths and nothing else, each escaped with
`EscapeFilterPath` inside single quotes. Inside the script, spoken text goes through
`AssSubtitleBuilder.EscapeText`, because libass reads override syntax anywhere in event text: `{`/`}`
become parentheses (no override block can open), a word joiner (U+2060, invisible) follows every
backslash (no `\N`/`\n`/`\h` can form) and every control character becomes a space (a newline would
end the `Dialogue:` line and let the rest parse as a new script line). Before that, the text passes
`CaptionTextSanitizer` — the overlay allowlist plus the punctuation narration needs (commas,
apostrophes, quotes, colons, semicolons, an ellipsis), which the overlay sanitizer strips. Golden tests
pin all of it: `AssSubtitleBuilderTests`.

**Presets.** Every number in the script is computed in C# from the canvas, the style and the config:

| Style | Look | Automatic font | Automatic size (% of the canvas's short side, landscape / portrait) | Automatic position |
|-------|------|----------------|------|----------|
| `Subtitle` | One or two lines on a box (`BorderStyle 3`, the box in `CaptionBoxColor`); with box `none`, outlined text plus a soft shadow | Inter Bold | 5.5% / 6.5% | Bottom |
| `Karaoke` | The same chunks as ONE event each, every word carrying a `\k` duration equal to the gap between its measured start and the next word's, so libass flips it to `CaptionHighlightColor` exactly when it is spoken | Montserrat Bold | 6.5% / 8% | Bottom |
| `Punch` | One word at a time, centred, heavy outline and shadow, never a box | Anton | 11% / 13% | Centre |
| `Bold` | Up to two words (≤ 12 characters) a cue, heavy outline and shadow, never a box, no highlight — the Reels/TikTok look; word timings used when present, else shared by length like Subtitle | Poppins | 10.5% / 14% | Bottom, at 28% of the height on a portrait canvas (10% landscape) — above the caption, handle and buttons the platforms draw |

On a 1920x1080 canvas that is 59 / 70 / 119 px; on 1080x1920 it is 70 / 86 / 140 px. A cue that would
need more than two lines (one for Punch) inside the safe width is shrunk for that cue alone with an
`\fs` override — libass wraps only at spaces, so a long single Punch word would otherwise run off a
9:16 frame. Portrait means taller than wide.

**Size.** `CaptionFontSizePct` `0` means automatic, and so does `4`: that was the default every config
saved before the automatic size existed carries, and it rendered ~4%-of-height captions that were too
small to read on a phone. Any other value keeps its old meaning — a percentage of the frame height,
clamped 2..12, Punch doubled within the clamp. The step output says which applied (`fontSize: "auto"`
or `"explicit"`).

**Position and safe margins.** `CaptionPosition` (`Auto` | `Bottom` | `Center` | `Top`, appended after
`TargetDurationSec`) maps to ASS alignment 2/5/8. Side margins are 8% of the width. A landscape canvas
keeps 8% clear at the top and bottom; a portrait canvas keeps the bottom 20% and the top 12% clear,
where Reels/TikTok/Shorts draw their own caption, buttons and header.

**Fonts.** `CaptionFont` (`Auto` | `Inter` | `Montserrat` | `Poppins` | `BebasNeue` | `Anton` |
`DejaVuSans`) is a closed enum — never a path or a family string. The files are vendored in
`inference/fonts/captions` (SIL Open Font License 1.1, provenance and hashes in its `UPSTREAM.md`), and
the WorkflowEngine Dockerfile copies them to `/usr/share/fonts/reelbolt-captions`, the default of
`VideoEditing:CaptionFontsDir`. `CaptionFontCatalog` resolves a font against that directory: a missing
file (or a missing directory) falls back to DejaVu Sans and records `fontFallback: "font_not_installed"`
rather than letting fontconfig silently substitute something else. Single-weight display faces (Anton,
Bebas Neue) are never asked for bold, so libass never smears a synthetic bold over them.

**Colours** are validated by `CaptionColor`: `white`, `black`, `yellow` or `#RRGGBB`, each optionally
`@opacity` (0..1) — e.g. `black@0.6`; `CaptionBoxColor` also takes `none`. Anything else falls back to
that field's default and is listed in `colorFallbacks`. Before this, caption colours reached the
drawtext filter string unvalidated; they now pass the same check on both renderers.

**The drawtext degrade.** When the ffmpeg build has no `ass` filter (probed once per process from
`ffmpeg -filters`, exactly like the drawtext probe), captions fall back to the older drawbox/drawtext
chain (`CaptionFilterBuilder.BuildFilterChain`: per-cue `textfile=` scratch files, half-open
`gte(t,a)*lt(t,b)` windows, Karaoke as a highlighted prefix redrawn over the base text), still at the
automatic size when the size is automatic, and the node records `renderer: "drawtext"` plus
`degradeReason: "libass_unavailable"`. Alpine's ffmpeg is built with `--enable-libass`, so the shipped
image takes the libass path.

The compile node is `captions`: `{enabled, applied, style, renderer, degradeReason?, font, fontFallback?,
position, fontSize, colorFallbacks?, cueCount, lineCount, cues[], dropped[], reason?}`, present only
when `EnableCaptions` is on. Degrade reasons: `voiceover_not_enabled`, `no_applied_voiceover_lines`,
`drawtext_unavailable` (neither renderer exists), `no_word_alignment`, `no_cues`;
`max_caption_cues_exceeded` truncates rather than drops (`MaxCaptionCues`, default 400). The tests are
`CaptionCompileTests` (the `EnableCaptions=false` filtergraph is the phase-2 voiceover graph, byte for
byte; the libass graph and script; the drawtext degrade; the colour allowlist), `AssSubtitleBuilderTests`
(golden scripts per preset, escaping, layout, fonts, and a real-ffmpeg render that runs when ffmpeg has
libass) and `CaptionCueBuilderTests`. The dashboard's compile editor shows each font as a live sample,
served from `web/public/fonts/captions` as small WOFF2 subsets — no font CDN.

### Subtitles from the transcript

`VideoCompileStepConfig.CaptionSource` (append-only; `Voiceover` is the default and everything above is unchanged,
byte for byte) chooses where burned captions take their words from. `Transcript` cuts them from the words **spoken
in the footage**: one caption line per segment of the `AnalysisStepOrder` step's transcript, timed on the output
timeline with the same mapping the voiceover lines use (`OutputTimeline.MapWindowToOutput`, so a cut that removes a
segment removes its caption and a crossfade shifts it with the picture). It needs `EnableCaptions` and a transcribing
analysis (`transcription` `Optional` or `Required`), and **no** `EnableVoiceover`; the two sources never mix in one
compile. The `video-subtitles` template is exactly that: `VideoAnalyze` (transcription `Required`, vision off), then
`VideoCompile` (`KeepWholeSources`, `Reencode`, `CaptionSource = Transcript`). No agent runs.

- **Text and timing.** The text is the segment's text through the same caption sanitizer as the voiceover path; the
  timing is the segment's own span, and the recogniser's word timings are used when the segment survived the cut whole
  (so Karaoke and Punch work), otherwise the words are spread over the line by character weight. A segment the edit
  cut away entirely gets no caption (`transcript.cutAwayCount`). A cut that passes **through** a segment is handled per
  kept piece (`OutputTimeline.MapWindowToPortions`): each piece becomes its own line (`t3`, `t3~2`, ...) timed where that
  piece lands, captioned only with the recognised words that fall inside it (or, with a translation or no recognised words,
  with the share of the tokens that matches the piece's share of the segment).
- **Reading comfort** (`SubtitleCueFit`, first-party constants): at most 42 characters per cue
  (`CaptionMaxCharsPerCue`, split at word boundaries), a cue is held to at least 0.9 s and long enough to read at 20
  characters per second when the gap before the next cue allows, never past 7 s and never into the next cue. The word-timed
  Karaoke and Punch styles keep their own timing.
- **Files.** The same cues are written as `subtitles.srt` (SubRip) and `subtitles.vtt` (WebVTT) and published next to the
  edit decision list as `video-analysis/{executionId}/step-{n}-subtitles.srt|vtt`. The compile output carries
  `subtitleStorageKeys` `{srt, vtt}` (absent on every other compile) and `GET
  /api/v1/projects/{id}/step-results/{stepResultId}/subtitles/{srt|vtt}` streams them as downloads. The files are always cut in
  the plain Subtitle style, whatever style is burned (a word-per-cue Punch file is not a subtitle file), and are written
  even on a host that cannot draw the captions. Cue text is flattened to one line; WebVTT escapes `&`, `<` and `>`.
- **Moderation and argv.** The caption text is screened once before any file or filter exists, exactly like the voiceover
  path, and never enters a filter string: libass reads an ASS script, the drawtext degrade reads `textfile=` with
  `expansion=none`.
- **Failure.** An analysis with no transcript segments at all fails the compile with `CAPTIONS_NO_TRANSCRIPT`
  (user input): a subtitles job must not return the clip unchanged. A transcript whose every segment was cut away is the
  soft `no_cues`. The node reports `source: "Transcript"` and a `transcript` object; with `Voiceover` neither key exists.
- **Language.** `brief.sourceLanguage` is the transcription hint (the run-brief override of the analyze step's
  `language`). `brief.language` is the translation target of `video-subtitles-translated`; to keep the clip's own language
  and pay for no translation, pick `video-subtitles` (the translated template always runs its translation step).

**Translation** (`CaptionTranslationStepOrder`). Design: the smallest step that translates the transcript is the existing
`NarrationTranslator` fed a deterministic projection, so there is no new `AgentType`. The `video-subtitles-translated`
template is `VideoAnalyze`, an `Extract` projection of `view.segments` (`fields: ["text"]`, `idField: "id"`), the
`NarrationTranslator` (input `PreviousStepOnly`, target language from the run brief) and the compile. The translator's
prompt (both the `DefaultPrompt` and the seeded copy, one commit) has a short "subtitles instead of a plan" paragraph: a
list of `{id, text}` segments is treated as plan lines with `anchorId` = the segment id. The compile reads that step's
`NarrationPlanOutput` into an id-to-text map (`CaptionTranslations.Parse`, ignoring any line whose anchor is not a known
segment) and swaps the text in; **timing and anchors always come from the transcript**, so the model never supplies a
time, and a segment the translation does not cover keeps its original words and is counted in
`transcript.untranslatedCount`. A segment can be left untranslated by: the analysis view's `maxViewSegments` (1000 in
the template) or its `maxOutputChars` (100,000), the projection's own `maxOutputChars` (100,000), or the model dropping a
line. The template raises `maxSegmentTextChars` to 2000 because the view cuts each segment's text at 160 by default, and a
translation of a cut text would replace the whole segment (a longer segment is cut at the limit and is a visible
shortfall of the *source*, never silently shortened by the translation step). An unreadable translation
output degrades to the original-language subtitles and says so (`transcript.translation`); translated lines get their
word timings spread by character weight. Two templates rather than one conditional step: the plain template has no
agent and costs no model call.

### Keeping captions clear of the picture's own text

With `CaptionPosition = Auto`, Subtitle and Karaoke captions sit in the lower third — which, on a
screen recording or a tracked app, is where the app's own titles are: a narrated launch reel put
"No timeline wrangling" straight over the UI's "Timeline editor" label. Automatic placement now
checks each kept shot that carries a caption (`CaptionPlacementProbe`): one frame from the middle
of the shot, fitted to the canvas exactly as the encode fits it (crop focus included), is
edge-mapped at a fixed 640 px width (`edgedetect`), and the mean of the edge map is read in the band
the caption would cover — two lines at the layout's size, above the bottom margin — and in the same
band under the top margin. When the bottom band reads at least 8 and at least twice the top, the
captions over that shot are drawn top-centre at the top title-safe margin instead (`{\an8}` and the
event's own `MarginV`; every other cue keeps the style untouched).

Measured on the launch reel's sources (bottom vs top): a UI timeline 12.9 vs 3.1 and a monitor over
a keyboard 16.8 vs 6.1 move up; a desk shot 0.2 vs 8.3 and a phone held in front of a face 8.0 vs
6.6 stay down. Two small ffmpeg calls per captioned shot; a frame that cannot be measured leaves its
captions at the bottom. Punch (centred) and an explicit `Bottom`, `Center` or `Top` are never moved.
The `captions` node reports `movedToTopCount` (cues) when any moved.

Over a DESIGNED composition (a source the analysis marks `IsComposition`, i.e. a Remotion render
with its own typography) the probe reads three frames (a quarter, half and three quarters through
the shot) and keeps each band's busiest value, since such a render animates its type in and out.
When both bands are busy (`CaptionPlacementProbe.BothBusy`: each at least the busy threshold), the
cues over that shot are NOT drawn — two layers of text on one frame is unreadable, and a render that
burned its own subtitles over the narration showed exactly that — while the narration still plays;
`captions.hiddenOverDesignedTextCount` reports how many. Footage keeps the single middle frame and
is never hidden.

### Pronunciation dictionary

`VoiceoverStepConfig.PronunciationDictionary` (`Off` default, `Auto`) sends Fish Audio a pronunciation
dictionary with each line. Three rules keep it deterministic and first-party
(`Shared/Workflows/PronunciationDictionaryBuilder.cs`):

1. **Candidate terms come from the code analysis**, never from prose: dependency names, framework,
   project type and component names read leniently from any `DependencyAnalysisOutput` /
   `CodeStructureOutput` / `ComponentInventoryOutput` in the run's history. "go" in ordinary narration
   is never read as Golang unless the analysis named it.
2. **Phonemes come from a fixed table** (`PronunciationDictionaryBuilder.Table`, IPA). A term the
   table does not know contributes nothing — failing closed, because the phoneme alphabet the engine
   expects is verified only for what the table carries. The table is code, not a config knob, like the
   colour-grade and SFX tables.
3. **An entry goes only with a line whose text contains the term as a whole word.** Fish matches keys
   by plain substring, so three-letter acronyms are sent case-sensitively (`API` never rewrites
   "rapid") and nothing shorter than three characters is sent at all. `MaxPronunciationEntries`
   (default 16) drops the entries that appear later in the line, never the whole dictionary.

The wire shape was verified against `api.fish.audio/openapi.json` on 2026-10-04 and corrects a
phase-1 note: Fish caps the request at **3 dictionaries**, each with up to 5,000 items (not "3
entries"); ReelBolt sends one inline dictionary (`pronunciation_dictionary: [{items: [{key, value,
case_sensitive}]}]`), omitted entirely when empty so the phase-1 request body is byte-identical. The
dictionary joins the per-line content key only when non-empty, so lines without one keep their cached
objects. `meta.pronunciation` records `mode`, `analysisTermCount`, `entryCount` and the keys per line.
The OpenAI-compatible speech contract has no pronunciation field; the entries are ignored there, never
smuggled into the text.
The request also carries `language` (a BCP-47 tag such as `it-IT`, see "Italian (and other non-English) narration"), omitted when the language is unknown or not one Fish lists, so a request without a language is byte-identical to before.

### Review facts: `voiceover.headroom` and `voiceover.wer`

Both are always present on the compile's `voiceover` node whenever voiceover was attempted, and carry
`applicable: false` with a `reason` (never an absent key, never a throw) when they cannot be computed
(`WorkflowEngine/Services/Video/NarrationIntelligibilityFacts.cs`):

- **`headroom`** — per applied line, the line WAV's own mean RMS (power-averaged over 250 ms windows
  through `WavRmsSampler`) minus the analyze step's measured `Audio.RmsDbfs` of the shot the line
  starts over: `{applicable, measuredLineCount, meanHeadroomDb, minHeadroomDb, lines[]}`. Not
  measurable when the line bytes are not a 16-bit PCM WAV or the shot carries no audio descriptor.
  A line whose clip is muted (`MuteSourceAudio`), has no audio stream, or whose dialogue is replaced
  (`ReplaceDialogue`) plays over no dialogue and is left out; when every line is, the node is
  `{applicable: false, reason: "no_dialogue_audio_in_output"}`. Measuring the muted take had held
  muted reels at a review score of 5 for "buried" narration nobody could hear.
- **`wer`** — per line with word alignment, the Levenshtein word error rate of what the recogniser
  heard against the sanitized text the step asked for, over normalized tokens:
  `{applicable, measuredLineCount, meanWer, maxWer, lines[]}`. `no_word_alignment` otherwise.
  A recogniser writes an invented name however it likes, so a reference word of six letters or
  more matches the recogniser's spelling of it — one token, or two adjacent tokens joined — when they
  differ by at most a fifth of its letters (`AlignSpelling`): "ReelBolt" heard as "Real Bolt" is not
  a mispronunciation (it was scored WER 0.4–0.5 on every launch reel and capped the review), while
  "Real boat" still is. Short words compare exactly. A word of six letters or more also matches a
  sound-alike spelling, one whose consonant skeleton is the same once voiced and unvoiced pairs (b/p, d/t, g/k, v/f, z/s) are merged
  and vowels dropped, with at least four consonants: "Real bold" is how a recogniser writes the
  spoken "ReelBolt" (`r-l-p-l-t` both ways).

`VideoReviewAgent`'s prompt (both byte-identical copies) names both: below roughly 6 dB of headroom
or above roughly 0.25 WER on any line, score no higher than 5 and name the line; the headroom remedy
is a dialogue treatment on the compile step (below), never a rewrite.

### Audio-first timing (promo pipeline)

Nothing re-cuts footage to narration — that stays out of scope by design — but the promo pipeline
now cuts its *picture* to the voice. `StageVoiceoverAudio` (the Author's staging tool) returns, next
to `staged`/`failed`, a `timing` array: one entry per staged scene with the narration's measured
`durationSec` and `durationInFrames` at the `fps` the entry names. The `AuthorAgent` and
`DirectorAgent` prompts (seeder copies byte-identical; the Director's is now pinned by the consistency
test too) set a narrated scene's length from that measurement instead of the script's guess, and
leave scenes with no narration — and promos with no Voiceover step — exactly as before.

### Voices and consent

A project has a **voice library** (`project_voices`, owned by the Inference API, migration
`AddProjectVoices`; mapped read-only by the WorkflowEngine). Two sources:

- `Library` — a voice the provider already offers, registered by its vendor id. No person in the
  project is its subject, so no consent is captured and it never expires.
- `Cloned` — a voice built from the project's **own** recordings through Fish Audio's `POST /model`
  (`IVoiceCloningClient`, implemented by `FishAudioSpeechSynthesisClient`; multipart, `visibility:
  private`, `train_mode: fast`; `DELETE /model/{id}` to remove it) — or, when the `FishAudio` row
  points at a self-hosted fish-speech server, through that server's reference library (see
  "Cloning on a self-hosted fish-speech server" below). **This is biometric data**, and the
  row carries the facts as columns, not free text: `ConsentAttestedByUserId`, `ConsentAttestedAt`, the
  verbatim `ConsentStatement`, `ConsentSubjectName`, `ConsentRevokedAt`/`ConsentRevokedByUserId`,
  `RetentionUntil`, and the reference `project_files` ids.

**One read-path rule**, `ProjectVoice.IsSelectable(now)`: a cloned voice is usable only while consent
is attested, not revoked, and within retention. The API's `selectable` flag, the web picker (which
offers only selectable voices) and the engine's `IProjectVoiceResolver` all apply it, so a
`VoiceoverStepConfig.ProjectVoiceId` naming an unusable voice **fails the step** —
`VOICE_CONSENT_MISSING` / `VOICE_NOT_FOUND` — and never falls back to `DefaultVoiceId`: a step that
asked for a cloned voice must not quietly speak in another one. A voice is resolvable only within the
project that owns it.

### The project's one voice is the default

A step that names no voice at all (no `ProjectVoiceId`, no `DefaultVoiceId`, and no voice in the run brief) does
not fall back to the provider's stock voice while the project owns a selectable one. `ProjectVoiceDefault`
(`Services/Voiceover`) reads the project's selectable voices through `IProjectVoiceResolver.ListSelectableAsync`
and counts every source: a `Cloned` voice and a `Library` voice are equally selectable (Library voices need no
consent and never expire; a clone is selectable only while its consent is attested, not revoked and within
retention, per `ProjectVoice.IsSelectable`):

- **No selectable voice:** nothing changes (provider default, as before).
- **Exactly one:** it narrates, whether it is Cloned or Library. `meta.voice` records `projectVoiceId`,
  `source: "project_voice"` and `defaulted: true` (the last only in this case), so the reuse guard re-checks it like
  a named voice.
- **Several (of any source mix):** the step fails with `VOICE_CHOICE_REQUIRED` (failure class `UserInput`, not
  retried) and a message that says the project has several voices, names them and says to choose one in the step's
  voice setting or the run brief. With `VoiceCasting: Decision` the gate chooses among them first, and the step fails
  only if casting picks none. A line that carries its own `voiceId` needs no step voice.
- **A named voice still wins and still fails closed:** an unusable `ProjectVoiceId` is `VOICE_CONSENT_MISSING` /
  `VOICE_NOT_FOUND`, never replaced by the one other selectable voice; a step with its own `DefaultVoiceId` keeps it.

**This is a behaviour change where a clone and a Library voice coexist.** A project that has a cloned voice and a
Library voice now fails `VOICE_CHOICE_REQUIRED` when the step names none (previously it auto-narrated the clone); to
run it, choose one in the step's voice setting or name it in the run brief.

The default is part of the step-cache key (`StepCacheKeyInputs.VoiceoverDefaultVoice`, appended only when present, so
`SchemaVersion` and every key of a step that names a voice, or of a project without a selectable voice, are
unchanged): adding, revoking or deleting a voice misses instead of replaying narration in the old voice. When the
assistant runs a workflow, `voice.projectVoiceId` is only needed to choose among several voices; with one, leave it
out.

**Vendor ids are checked too.** A cloned voice's vendor id can also arrive as `DefaultVoiceId`, a line's own
`VoiceId` or the cast voice. Before any synthesis or object-store reuse the step checks every effective vendor id
against the project voice rows with `VendorVoiceIdRule` (shared by the engine, the reuse guard and the save check):
if a row of **this** project carries the id, that row decides (usable passes, otherwise the step fails); else if a
**cloned** row of **another project** carries it and is in scope, it is refused, because a clone's consent belongs to the
project that attested it. In scope: a clone of any other project of **this project's organization** (read from the
project row), whatever its provider lookup says; or a clone made on a **platform-owned** account (the voice's
`InferenceProviderId` is a model capability id, resolved capability -> model -> provider account). Otherwise (no row,
only `Library` rows elsewhere, a clone on another organization's **private** account, or a foreign clone whose
provider/capability is gone) it is a raw vendor id and passes. The last two are deliberate: nobody else can speak
through another organization's private endpoint, and refusing a foreign clone whose provider was deleted would let any
organization mint a clone with a popular vendor id and then delete its own provider to make that id refuse for every
tenant. If the project's organization cannot be resolved, every foreign clone whose account resolves is refused
(conservative). The predicate lives once, in `VendorVoiceIdRule.Scope`.
A refusal is
`VOICE_CONSENT_MISSING` with one wording that names neither the owner nor the reason; with ids present and no
resolver the step fails closed (`VOICE_RESOLVER_UNAVAILABLE`). **Residual:** a deleted clone's vendor id is
indistinguishable from a raw one, so once the row is gone the id passes. The save path
(`VoiceoverVoiceConsentSaveValidator`, run by the shared `StepProviderOwnershipValidator` for every save site, step
type parsed case-insensitively, config read with `VoiceoverStepConfig.Parse`) refuses the same ids, and an
unselectable or missing `ProjectVoiceId`, with a 400 as an early warning; the engine's check stays authoritative.

**Reuse is re-checked too.** The step's guard runs only when the step runs, so audio that is *reused* would
otherwise outlive a revocation. Before a recorded Voiceover result is served — a step-result cache hit, a resume
reusing a completed result, or a composition replay copying one — `IVoiceReuseGuard`
(`Services/Voiceover/VoiceReuseGuard.cs`) re-checks the voices it used, read from the output through
`VoiceoverOutputVoices` (`meta.voice` beside `view.lines`): the step's configured `ProjectVoiceId`,
`meta.voice.projectVoiceId`, the config's `defaultVoiceId` and line `voiceId`s (as vendor ids), and the cast voice at `meta.voice.casting.chosenVoiceId` (when `casting.applied`).
Each recorded id must still resolve to a row of the execution's project that `ProjectVoice.IsSelectable` accepts
(`ProjectVoiceResolver.Project`). An older result with `source` `project_voice` or `cast` and no recorded id is
matched by its vendor id against the project's `RemoteVoiceId` rows and needs a usable match; any other row
matching a recorded vendor id must be usable too. A `default_voice_id` / `provider_default` result naming no
project voice is allowed. A refusal is a **miss**: the step runs live and the executor's guard decides
(`VOICE_CONSENT_MISSING` / `VOICE_NOT_FOUND`); a refused resume is the first miss (the stale rows are
superseded); a refused replay copy runs the step, and every later replay step that depends on it is re-made too.
An output that is not a Voiceover result (unparseable, no `meta`) and an error in the re-check are also misses.
Other step types are never checked. A voice-free Voiceover step costs no query; a project voice, `Library` ones
included, costs one lookup. The cache key and the output shape are unchanged.

Endpoints (`ProjectVoicesController`, owner-scoped like every project resource; a voice in another
project is `404`):

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/v1/projects/{projectId}/voices` | List, with `selectable` and `unselectableReason`. |
| `GET` | `/api/v1/projects/{projectId}/voices/library?q=&language=&page=` | Browse the resolved speech provider's public voice library (View, like the list). `{items:[{voiceId,name,languages[],tags[],previewAvailable}],total,page,pageSize}`, 24 per page, cached 10 min per provider+query. `404 voice_library_not_supported` for any provider but hosted Fish Audio; `502 voice_library_failed` on a vendor error. |
| `POST` | `/api/v1/projects/{projectId}/voices` | Register a library voice. |
| `POST` | `/api/v1/projects/{projectId}/voices/clone` | Clone. `consentStatement` must equal `RequiredConsentStatement` **verbatim** and `consentSubjectName` must be given, or `400` and nothing is read or sent; recordings must be this project's audio/video files (≤ 20, ≤ 50 MiB); `retentionDays` 1..730 (default 365). `400 cloning_not_supported` for a provider without a cloning API; a self-hosted fish-speech row adds `400 too_many_reference_files` (more than one), `reference_file_not_wav`, `transcription_provider_required` and `reference_has_no_speech`, all before anything is sent to it. |
| `POST` | `/api/v1/projects/{projectId}/voices/{id}/revoke-consent` | Unselectable everywhere from now on; the attestation stays on record. |
| `DELETE` | `/api/v1/projects/{projectId}/voices/{id}` | Deletes the vendor model (best effort), the reference recordings (object + `project_files` row) and the row; the response reports each outcome rather than assuming it. |

Retention is enforced, not described: `ProjectVoiceRetentionService` sweeps every six hours and
deletes cloned voices past `RetentionUntil`, or revoked more than 30 days ago, the same way `DELETE`
does. The web **Voices** tab shows the consent state on every row, requires the attestation checkbox
before a clone, and reports what a delete actually removed.

### Browsing the Fish voice library

The web **Voices** tab's **Browse voices** modal (search, language filter, pagination) calls the
library endpoint above and **Add** reuses the register-library-voice call. The library client is
`IVoiceLibraryClient`, implemented by `FishAudioSpeechSynthesisClient` against the hosted API's
public `GET /model?page_size=&page_number=&title=&language=&sort_by=score`; a self-hosted
fish-speech server has no library. No preview audio is proxied (`previewAvailable` only says a
sample exists).

The same library is reachable from the assistant and MCP (`SearchVoiceLibrary`, `ListProjectVoices`,
`ProposeAddLibraryVoice` / `ApplyAddLibraryVoice`, MCP `AddLibraryVoice`; see
[platform-assistant-mcp.md](platform-assistant-mcp.md)). Both the route and the tools go through
`ProjectVoiceLibraryService`. Adding a voice from the assistant first looks the id up with
`IVoiceLibraryClient.GetLibraryVoiceAsync` (Fish `GET /model/{id}`): only a public voice that is not
a voice cloned on the platform account is added. Cloning stays in the Voices tab.

**Must measure** (assumed fixtures, recorded under `ProjectVoiceLibraryTests`; no key or live
vendor call is made from the repo):

- the `GET /model/{id}` response shape (assumed: the listing item plus `visibility`; a present
  `visibility` other than `public` is treated as "not a library voice") and its 404 for an unknown id;

- the `GET /model` response shape: `{total, items:[{_id,title,languages[],tags[],samples[]}]}`;
- the query parameter names above (`title` for free text, `language`, `page_size`, `page_number`);
- that `GET /model` cannot list private clones (the platform's own filter is unconditional; the
  vendor's listing is not measured);
- that `/v1/tts` with `format: "wav"` answers with a streamed header (data size 0 / 0xFFFFFFFF) —
  the root cause of the stale "zero duration" provider-test failure.

### A local, zero-cost TTS server

`OpenAICompatible` rows may now serve `SpeechSynthesis`: `OpenAICompatibleSpeechSynthesisClient`
speaks OpenAI's `POST {base}/audio/speech` (verified against the OpenAI OpenAPI specification —
`model`, `input` ≤ 4096 chars, `voice` required, `response_format: wav`), which the bundled
`whisper` (speaches) container serves at `http://whisper:8000/v1` with the Apache-2.0 kokoro voices.
Register it as a provider of kind `OpenAICompatible` with a `SpeechSynthesis` model capability; a blank API key sends no
`Authorization` header and **reads nothing from the environment** — a hosted endpoint then answers
`401` loudly rather than spending the host's `OPENAI_API_KEY`. The kind is exempt from the
private-address classification (it already was, for the Transcription case), which is what makes
`http://whisper:8000/v1` reachable through the product API — the narrowest answer. No cloning
on this kind: only Fish exposes one.

### Pickups and dubs

Two more things the compile step can do under a narration line, both requiring `Mode = Reencode`:

- **`VoiceoverMode`** (`OverMusic` default — the phase-1 mix, byte-identical; `DuckDialogue`;
  `ReplaceDialogue`) decides what happens to the **source dialogue** under each applied line: a
  deterministic keyframed `volume` expression (`DialogueGate`, ramped by `VoiceoverDuckRampMs`,
  depth `VoiceoverDuckDb`) applied to the dialogue cut **before any mix**, so neither narration nor
  music is gated with it. The node records it under `voiceover.dialogue`. `ReplaceDialogue` is the
  basis for dubs.
- **Source speech guard** (`OverMusic` only). `OverMusic` keeps the source sound, so a voiceover over a
  film that already carries the same narration plays it twice. When `EnableVoiceover`,
  `VoiceoverMode = OverMusic`, `MuteSourceAudio` is off and at least one line is placed,
  `VoiceoverSourceSpeechGuard` reads the analysis artifact (transcript segments, else the per-shot
  audio speech ratio minus the detected silences; unreadable facts mean no change) and maps the speech
  to the output timeline.
  - **Narrated film**: every used source is a rendered film (not a camera upload and not a generated
    clip: the artifact source has no `projectFileId` or `clipId`) and source speech covers at least
    `VideoEditing:VoiceoverGuard:NarratedFilmSpeechCoverage` (default `0.60`) of the kept program. The
    compile then behaves as `MuteSourceAudio` for this run (argv identical to that setting); music, SFX
    and the voiceover still play. Warning `voiceover_source_narration_muted`.
  - **Speech under the lines** (any other source): source speech covers at least
    `VideoEditing:VoiceoverGuard:SpeechUnderLinesCoverage` (default `0.50`) of the lines' total time.
    **Warning only**, `voiceover_source_speech_under_lines`, suggesting the Duck dialogue / Replace
    dialogue modes. No audio and no argv change: ducking camera dialogue is the author's own choice.
  - Otherwise the output is byte-identical. When a case fires the decision is recorded under
    `voiceover.sourceSpeechGuard` `{applied, case, speechCoverage, threshold, reason}`; otherwise the node is
    absent. Both thresholds are **uncalibrated** starting values (clamped to 0..1); a shot counts as
    speech from a speech ratio of 0.5. `DuckDialogue`, `ReplaceDialogue` or `MuteSourceAudio` set
    explicitly always disable the guard. It never fails the step.

  | Config key | Default | Meaning |
  |---|---|---|
  | `VideoEditing:VoiceoverGuard:NarratedFilmSpeechCoverage` | `0.60` | Share of the program a rendered film speaks before its sound is dropped |
  | `VideoEditing:VoiceoverGuard:SpeechUnderLinesCoverage` | `0.50` | Share of the lines' time with source speech before the warning |

- **Pickups** (`EnablePickups` + `PickupVoiceoverStepOrder`): `AgentType.PickupPlanner` names the
  transcript segments the presenter flubbed and the corrected sentence for each
  (`PickupPlanOutput` — its **own** schema with its own invariant test, so
  `VideoEditDecisionOutput`'s rushcut invariant stays untouched); a `Voiceover` step with
  `VoiceoverSourceKind.PickupPlan` re-records them (point it at a consented cloned voice); the compile
  step resolves each line's `anchorId` to that `t{n}` segment's **source window from the analysis
  artifact** — never from a number the model wrote — maps it through the cut, mutes the original take
  for exactly that window and plays the pickup from its start. Node `pickups`: `{lineCount,
  appliedLineCount, lines[] (with overflowSec), dropped[], lipsync}`. Template
  `video-derush-edit-pickups`.
- **Dubs**: `AgentType.NarrationTranslator` re-emits a `NarrationPlanOutput` in the target language
  named in the run's request — same lines, same anchors, translated words — so the existing
  `NarrationPlan` arm speaks it; `VoiceoverMode = ReplaceDialogue` removes the original dialogue under
  every line and `EnableCaptions` burns Subtitle captions from the words actually spoken.
  `VoiceoverStepConfig.Language` is recorded on the output as provenance; the speech providers detect
  the language from the text. Template `video-derush-edit-dub`.

**Lipsync is not available.** The #93 lipsync purpose this plan depended on does not exist, so a
pickup or a dub keeps the original take's picture — mouth and all. The compile node states it
(`pickups.lipsync: {applied: false, reason: "lipsync_not_available"}`) rather than implying a match,
and the templates' descriptions say so in plain words.

### Voice casting

`VoiceoverStepConfig.VoiceCasting` (`Off` default — asks nothing; `Decision`) asks the configured
Decision provider **one** `Choice` question through the existing decision gate (site `VoiceCasting`,
recorded in `decision_observations` like every other gate call): the options are the project's
selectable voice ids, the state is the lines' text plus each candidate's own description — never a
vendor voice id. The answer is used only when the gate **accepted** it and it names an offered voice;
a declined gate, no provider, no selectable voices or an answer outside the set keeps the configured
voice and says why under `meta.voice.casting`. A single candidate is cast without a call.

### What is still not here

- **Re-cutting picture to narration** in the derush pipeline. Audio-first timing is the promo
  pipeline's; for footage the picture is cut by the story editor and narration is measured against it.
- **Lipsync** for pickups and dubs — see above; stated on the node, not papered over.
- **Narration on the starter promo** — `quick-win-promo` and `lean-context-promo` still gain
  narration only by inserting a `Voiceover` step (see the user guide's
  [voiceover recipe](user-guide/recipes/voiceover.md)); the two narrated promo templates are
  described under [Story-first narration and the narrated templates](#story-first-narration-and-the-narrated-templates).
- **Fish's native word timestamps** (`/v1/tts/stream/with-timestamp`) stay unused; alignment is the
  transcription client's, for the reasons under [Word alignment](#word-alignment).
- **Pronunciation on non-Fish kinds** — the OpenAI speech contract has no dictionary field.

## Story-first narration and the narrated templates

A customer's verdict on the derush-and-narrate pipeline was "no story line to follow, random clips".
The structural cause: in `video-derush-edit-voiceover` the `VideoStoryEditor` picks spans first and
the `NarrationWriter` then writes a line per kept clip, so the script describes clips instead of
telling a story. The `narrated-story` template ("Narrated product story",
`WorkflowTemplateCatalog.NarratedStoryTemplateKey`) inverts the order without a new agent type:

```
VideoAnalyze (Source: ProjectFile, Vision: Optional)
  → Agent(NarrationWriter)   FullWorkflow — sees only the analysis view and the brief
  → Agent(VideoStoryEditor)  FullWorkflow — sees the view AND the narration plan
  → Voiceover                {"kind":"NarrationPlan","narrationWriterStepOrder":2}
  → VideoCompile             decision: Step 3, voiceoverStepOrder: 4, enableCaptions: true, captionStyle: Karaoke
  → ReviewLoop(VideoReviewAgent) back to step 2 (the story), MinScore 8, MaxIterations 3
```

**Why no new agent.** `NarrationPlanOutput` is already a script that names the picture under each
line (an offered `s{n}`/`g{n}`/`t{n}` anchor plus prose) and carries no time, so it satisfies the
rushcut invariant unchanged. A new `AgentType` would have meant an enum append, a seeded row, tool
scoping, drift guards and a second schema for the same shape. Instead both prompts gained a section
for this order: the `NarrationWriter`'s **"Story first"** section (no edit decision in the input
means "write the script from the brief, one story, lines in the order they are heard, each anchored
to the shot that best carries it, not empty"), and the `VideoStoryEditor`'s **"When the narration
script came first"** section (keep every anchored shot, list Keep spans in the script's order). Both
copies of each prompt stay byte-identical (`VideoStoryEditorPromptConsistencyTests`).

**Ordering is real across clips, not within one.** `VideoCompileStepExecutor` sorts and coalesces
spans only within a run of consecutive same-source spans; spans from different `src` clips keep the
list order the editor wrote. So the story's beat order maps onto the decision's span order for a
multi-clip project. Two moments of the **same** clip always play in source order — reordering within
one clip would need the normalisation pass to stop sorting a same-source run, i.e. a compile change
(it currently treats that as `spanNormalization.reordered`). The narration is placed narration-first
(back-to-back in plan order), which matches a picture cut in the same order; resolving each line
against its anchor shot's own source clip is the compile step's job.

**Captions are on in both footage narration templates.** `video-derush-edit-voiceover` and
`narrated-story` set `enableCaptions: true` with `captionStyle: "Karaoke"`. Karaoke needs word
timings from a Transcription provider; without one each line degrades with `no_word_alignment`
and the `captions` node says so. Both templates, like every template, now set
`RequiresUserInput: true` so the brief is asked for at run time.

**The narrated promo templates.** `cinematic-feature-spotlight` ("Quick vertical reel") and
`story-arc-launch-trailer` ("Narrated launch trailer") gained a `Voiceover` step with
`{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":N}}` directly after the Scriptwriter and
before the Director, so audio-first timing applies and the Author's `StageVoiceoverAudio` finds the
lines; their review loops now target the Author at its new position (step 9 in both). They fail
with `PROVIDER_NOT_RESOLVED` on a deployment with no `SpeechSynthesis` provider, which their
descriptions state. `WorkflowTemplateCustomerFacingTests` pins all of this, plus catalog-wide facts:
every template is savable through `StepConfigSaveValidator`, every `Voiceover` step reads an
earlier step of the agent its source kind expects, and every review loop's target sits inside the
window it re-runs.

## Local fish-speech servers and endpoint rules

A `FishAudio` row may point at a self-hosted fish-speech server (for example
`http://host.docker.internal:8180`). Like `OpenJev`, the kind is exempt from the private-address
SSRF classification (admin-only writes; the scheme must still be http(s)). `inference` and
`workflow-engine` carry `extra_hosts: host.docker.internal:host-gateway` so that name resolves.
The client requests `sample_rate: 44100` — the cloud API rejects 48000 for WAV ("Supported sample
rates: 8000, 16000, 24000, 32000, 44100"); the local server ignores the field. The admin test button
now surfaces the vendor's own error text (e.g. a cloud `402 Insufficient API credit`, an account
balance problem, not a client bug).

### Cloning on a self-hosted fish-speech server

A self-hosted fish-speech server has no `/model` endpoint; it keeps its own **reference library**
instead: `POST /v1/references/add` (multipart `id`, `audio`, `text`),
`GET /v1/references/list` and `DELETE /v1/references/delete` (JSON `{"reference_id": …}` — the
handler is registered for `DELETE` despite the path). Each reference is one directory holding
`sample.wav` beside a `sample.lab` transcript, and `POST /v1/tts` with `reference_id` conditions
synthesis on that pair — the same request field the hosted API uses, so a voice cloned either way is
spoken by the unchanged synthesis path. Every response is msgpack unless the request sends
`Accept: application/json`, which the client always does.

**Detection.** `FishAudioSpeechSynthesisClient.DetectCloningApiAsync` treats a row whose endpoint host
is `api.fish.audio` as hosted without a request. Any other endpoint is probed once with
`GET /v1/references/list`: a 2xx JSON object with a `reference_ids` array means self-hosted, any other
HTTP answer means the hosted `/model` API (e.g. a proxy in front of api.fish.audio). The answer is
remembered for the life of the cached client; a probe that could not connect is not remembered.

**What changes for a clone.** `IVoiceCloningClient.GetRequirementsAsync` reports the backend's
constraints before any recording is read: the hosted API takes 1–20 recordings of any container and
transcribes them itself; a self-hosted server takes **exactly one WAV** (it stores the upload as
`sample.wav` whatever it is) **plus its transcript**, which it cannot produce. `ProjectVoicesController`
checks the count first, then the RIFF/WAVE header, then transcribes the recording through the
**default `Transcription` provider** (`ResolveTranscriptionAsync(null)`, no per-request override) and
refuses an empty transcript. The reference id is generated (`reelbolt-` + a GUID, which always
matches the server's `^[a-zA-Z0-9\-_ ]+$`), never user-supplied, and becomes the voice's
`RemoteVoiceId`.

**Consent is unchanged.** The verbatim statement, subject name, retention, revocation and the single
read-path rule apply exactly as above, and every check runs before the recording is downloaded. The
one new egress is the transcription call: when the default `Transcription` row is a hosted service,
the reference recording goes there as well as to the fish-speech server. `DELETE` and the retention
sweep remove the server-side reference through `DELETE /v1/references/delete` (404 counts as
already gone).

**Server-side caveats.** References live in the server's `references/` directory, so mount it as a
volume or they vanish with the container. A `POST /v1/tts` naming an unknown `reference_id` does not
fail: the server creates an empty directory and speaks in its default voice, which is why ReelBolt
never sends an id for a deleted or unselectable voice (the engine's `IProjectVoiceResolver` fails the
step first). And the server must be able to decode reference audio: a fish-speech image whose
torchaudio is 2.9 or newer without `torchcodec` installed accepts the upload but answers every
reference-conditioned synthesis with HTTP 500 ("TorchCodec is required for load_with_torchcodec").

## Language and voice from the run brief

A run's brief can set the narration language and voice (`docs/run-brief.md`, "What reads the brief"). `language` fills `VoiceoverStepConfig.Language` when the step has none and is passed to the speech client as `SpeechRequest.Language`; neither shipped client's API takes a language, so the request on the wire is unchanged. If the provider publishes the languages it speaks (Fish Audio does) and the language is not among them, the output carries a `voice_language_unlisted` warning; a provider with no published list is passed through. `voice.projectVoiceId` or `libraryVoiceId` replaces the step's `ProjectVoiceId` and goes through the same consent guard, so a foreign or unusable voice fails with `VOICE_NOT_FOUND` or `VOICE_CONSENT_MISSING`. `voice.mode: none` skips the step with a plain reason.

A per-line voice id carried by the upstream narration plan still beats the voice the brief named.
