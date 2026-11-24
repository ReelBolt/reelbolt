---
title: Tighten a talking-head video (derush edit)
sidebar_position: 5
description: Recipe for cutting dead air, false starts and retakes out of filmed footage, with a single AI editor or a panel of AI editors.
---

# Tighten a talking-head video (derush edit)

**Goal:** take a raw recording — a talking-head video, interview, vlog or screen recording with
narration — and get a tighter edit with long pauses, false starts and repeated takes removed. The
result is a new MP4 (`edited.mp4`).

"Derushing" is the editor's word for going through raw footage ("rushes") and keeping only the good
parts. In ReelBolt an AI editor chooses which parts to keep, and ReelBolt itself does the precise
cutting.

## Prerequisites for a derush edit

- **The video uploaded to the project** (Files tab). Up to 30 minutes and 2 GB per video.
- **A chat AI provider** for the editor.
- **Strongly recommended: a transcription provider**, so the editor knows what is being said and
  keeps whole sentences. An admin adds one under Admin, Inference Providers with capability
  `Transcription`. Options:
  - the local `whisper` service that ships with ReelBolt (free, runs on your server; kind
    `OpenAICompatible`, endpoint `http://whisper:8000`);
  - an Azure OpenAI or other OpenAI-compatible speech-to-text service.

  Anthropic (Claude), Gemini and DeepSeek providers cannot transcribe. Without any transcription
  provider the edit still works, but the editor only sees pauses and shot changes, not words.

## The derush workflow

Add the **Highlight edit from footage** template (`video-derush-edit`) from **New workflow**. Its steps:

| # | Label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Compile edited video | `VideoCompile` | `VideoTransform` (not an AI) |
| 4 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` (back to step 2 when below 8, up to 3 rounds) |

**Step 1** — open it and select your video as the source. Its settings then look like:

```json
{"version": 1, "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"}}
```

Speech-to-text is used automatically when a provider exists (`"transcription": "Optional"`). Use
`"transcription": "Required"` to make the run fail clearly instead of editing without words, and
`"language": "en"` (or your language) if detection guesses wrong.

**Step 3** — as shipped:

```json
{"version": 1, "decision": {"from": "Previous"}, "analysisStepOrder": 1,
 "transitionPolicy": "Auto", "programFadeInMs": 500, "programFadeOutMs": 800,
 "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900, "minSegmentMs": 800}
```

This makes soft automatic joins between cuts, fades the start and the end, and drops kept pieces
shorter than 0.8 seconds.

## How to run a derush edit

1. Optional but useful: when you click **Run**, write a brief in the confirmation box to tell the
   editor what you want, for example "Cut to about 90 seconds, keep the story
   about the launch, drop the off-topic tangent near the end".
2. Run it. Step 1 takes roughly as long as the video plus speech-to-text time; step 2 is one AI
   decision; step 3 encodes the video.
3. Watch the result in step 3's **Output Video**, or in the project's Renders tab. The edit is also
   added to the project's Files.

## What to check after a derush edit

- **Step 1** result: `meta.transcription.applied` should be `true` if you expected words. If it says
  `degraded`, speech-to-text failed or no provider was found. If it shows `droppedNoSpeech`, that
  many "words" were thrown away because there was no speech under them: speech-to-text tends to
  invent lines such as "Thank you for watching." on music-only or silent clips, and ReelBolt
  discards them so the editor never mistakes them for dialogue. Clips with no sound at all are not
  transcribed.
- **Step 2** result: the list of kept spans (which ids were kept) and the editor's explanation of
  why.
- **Step 3** result: how long the result is, and the **Open in Editor** timeline to see each cut.
- **Step 4** result: the review score and comments. A low score sends the run back to step 2 with the
  comments.

## Variation: a panel of editors (edit room)

The **Highlight edit, decided by an editing team** template (`video-derush-edit-room`) replaces step 2 with an
`EditRoom` step: three AI editors (pacing, story, craft) discuss the footage and a director writes
the final decision. It is slower and uses more AI calls, but tends to make more considered choices
on long or story-driven footage. If the panel fails, a single editor decides instead
(`"fallbackToSoloEditor": true`).

```json
{"version": 1, "view": {"from": "Previous"}}
```

You can give the room your own seats, for example:

```json
{"version": 1, "view": {"from": "Previous"},
 "seats": [
   {"name": "BrandEditor", "persona": "argues for keeping every product mention and the call to action"},
   {"name": "PacingEditor", "persona": "argues for rhythm and removing dead air"}
 ]}
```

The room's discussion is kept on the step's result for reading; only the director's final decision
counts.

## Other variations of the derush edit

- **Several takes or angles:** in step 1 use `"sources": [ ... ]` with several videos instead of
  `source`. All clips are analysed together and the editor can pick the best parts from any of them.
- **Open on the best shot, not the first one:** by default the parts of one video play in the order
  they were filmed. Set **Order of the clips** to **In the editor's order** in step 3
  (`"keepOrder": "AsListed"`) and say what you want in the brief, for example "open on the moment
  the product launches, then tell how we got there". The editor may then move a later moment to the
  front. It never shows the same moment twice. This needs the normal (re-encoded) cut.
- **Use only part of a video:** in step 1, fill in **Use from** and **Use until** (seconds) on a clip
  to analyse and edit only that stretch of it (`"inSec": 12, "outSec": 48` on the source). Everything
  outside it is left out of the edit.
- **Keep part of a long shot:** in step 1, set **Cut points inside long shots** to **Where the picture
  pauses or changes** (`"subShotMode": "Content"`). A long shot is then offered cut points where the
  picture pauses or changes, and the editor can keep only the good part of it. Choose **Even** to cut
  at equal intervals instead, or **Off** for no cut points. The footage-edit templates, except the
  dailies one, start on **Where the picture pauses or changes**.
- **Plain cuts:** set `"transitionPolicy": "Off"` in step 3 (and the fades to 0).
- **Fast, lossless cut:** `"mode": "StreamCopy"` with `"allowKeyframeSnapping": true` in step 3.
  A single continuous cut is a lossless copy: it lands on the nearest keyframe (less precise). A cut with
  more than one part is re-encoded, so it is not lossless. No extras (graphics, music...) are possible.
- **Describe shots with AI:** `"vision": "Optional"` in step 1 adds a short AI description of what
  is in each shot (needs a `Vision` provider; costs one AI call per described shot, at most
  `maxCaptionedShots`, default 50).
- **Add extras:** graphics, music, sound effects, colour grade, b-roll and voiceover each have their
  own recipe in this section.

## Pitfalls of a derush edit

- **Run fails at step 1 with `SOURCE_UNRESOLVED`:** no video selected in step 1.
- **`EMPTY_KEEP` or `EXPECT_FAILED` at step 3:** the editor kept nothing, or less than 15% of the
  footage. Give clearer instructions, or lower `expect.minRetainedRatio` in step 3 if a very short
  cut is intended.
- **Sentences cut mid-word:** usually no transcription. Add a transcription provider.
- **Re-runs give the same edit:** the template sets the editor to never reuse, but if you built your
  own workflow, set the editor step's `cacheMode` to `"Never"`.
- **If you add steps between the editor and the compile step,** change step 3's `decision` from
  `{"from": "Previous"}` to `{"from": "Step", "stepOrder": 2}` — "Previous" would then point at the
  wrong step.

## Recipe: Highlight edit from footage

Template: **Highlight edit from footage**.

**Set before you run it.** Your clips (one to twenty), the spoken language of the footage, length
(30, 60 or 90 seconds are offered), shape, style, and a note on what to keep. There is no
narration or design setting: the edit uses your own sound. Setting meanings are in
[Run settings](run-settings.md).

**What it costs.** It uses credits for the AI editor and the reviewer. No voice and no generated
shots. The price shows on the run page.

**What you get.** One edited video (`edited.mp4`) with dead air, false starts and retakes removed,
and a reviewer's check on the result.

**Common problems.**

- The run stops with no clip chosen: pick your clips in the files slot before you run.
- Words are cut mid-sentence: the speech was not transcribed. Ask your admin to connect a
  transcription provider.
- The edit keeps almost nothing, or fails the check: say in the notes what must stay, or ask for a
  longer length.

## Recipe: Highlight edit, decided by an editing team

Template: **Highlight edit, decided by an editing team**. Three AI editors (pacing, story, craft)
discuss the cut and a lead editor decides.

**Set before you run it.** The same settings as the single-editor edit: clips, spoken language,
length, shape, style, notes.

**What it costs.** It uses more credits than the single-editor edit and takes longer, in
exchange for a more considered cut. The run page shows the price.

**What you get.** One edited video, like the standard edit. The editors' discussion is kept with
the run for reading.

**Common problems.**

- Short or simple footage: the team adds cost without a visible gain. Use the single-editor edit.
- The panel fails: a single editor decides instead, so you still get an edit.
- Clip, transcription and empty-edit problems are the same as in
  [Highlight edit from footage](#recipe-highlight-edit-from-footage).
