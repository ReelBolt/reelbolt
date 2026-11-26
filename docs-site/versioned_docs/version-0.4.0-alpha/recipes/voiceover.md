---
title: Add a voiceover
sidebar_position: 8
description: Recipe for adding spoken narration, generated from text, to an edited video or to a promo made from code.
---

# Add a voiceover

:::note
This recipe now opens as part of [Edit my video](edit-my-video.md): choose the matching field there. This page stays as a reference for what each step does.
:::

**Goal:** add spoken narration to a video. ReelBolt turns written lines into speech with a
speech service (Fish Audio) and mixes it into the video. There are two ways, depending on the kind
of video:

- **Edited footage:** you write the lines and when each starts; the `VideoCompile` step mixes them
  in.
- **Promo from code:** the `ScriptwriterAgent` already writes narration for each scene; a
  `Voiceover` step speaks it and the `AuthorAgent` puts each line into its scene.

ReelBolt speaks exactly the text it is given: yours, the scriptwriter's, or (with the footage
templates below) a narration writer's. No promo template includes a voiceover; you add a
`Voiceover` step to a promo workflow. Three footage templates do include one; see
[Letting an AI write the narration for footage](#letting-an-ai-write-the-narration-for-footage).

## Prerequisites for a voiceover

- **A speech provider**: an admin adds one under Admin, Inference Providers with capability
  `SpeechSynthesis` — kind `FishAudio` with a Fish Audio API key, or kind `OpenAICompatible` for
  the bundled free local speech server — and marks it default (or the step names it with
  `providerId`).
- **A voice id** from Fish Audio (its "reference id" for the voice you want), used as
  `defaultVoiceId`.
- Licence note: Fish Audio's hosted service has no documented non-commercial restriction; running
  Fish Audio's open models yourself is covered by a separate licence that needs a paid agreement
  for commercial use.

## Voiceover for edited footage

Start from any footage workflow, for example the **Highlight edit from footage** template
(`video-derush-edit`). Add a `Voiceover` step before the compile step and switch voiceover on in the
compile step. Example layout:

| # | Label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Synthesize narration | `Voiceover` | `VideoTransform` (not an AI) |
| 4 | Compile edited video | `VideoCompile` | `VideoTransform` |
| 5 | Review edit quality | `ReviewLoop` back to step 2 | `VideoReviewAgent` |

**Step 3** (`voiceoverConfigJson`):

```json
{
  "source": {
    "kind": "Inline",
    "lines": [
      {"text": "This is how we built our first prototype.", "startSec": 2.0},
      {"text": "Three months later, it shipped.", "startSec": 41.5, "voiceId": "OTHER-VOICE-ID"}
    ]
  },
  "defaultVoiceId": "YOUR-FISH-AUDIO-VOICE-ID"
}
```

- `startSec` is measured on the **original, uncut** video's clock. ReelBolt moves each line to the
  right moment in the edited video. A line whose start was cut out of the edit is dropped.
- `voiceId` per line is optional; otherwise `defaultVoiceId` is used.
- Text in brackets like `[laughs]` or `(pause)`, links and control characters are removed before
  speaking; a line that becomes empty is skipped and never billed.

**Step 4** — because steps were inserted, point at the editor by number:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableVoiceover": true, "voiceoverStepOrder": 3}
```

Note that `voiceoverStepOrder` is a plain step number, not a `{"from": ...}` reference. Voiceover
needs `"mode": "Reencode"` (the default). Narration is not turned down; if background music is also
on, the music does not rise while narration plays.

## Voiceover for a promo made from code

In the promo templates the `ScriptwriterAgent` writes narration for every scene. To have it spoken:

1. Add a `Voiceover` step **after** the `ScriptwriterAgent` step and **before** the `AuthorAgent`
   step.
2. Use the scriptwriter as its source:

```json
{"source": {"kind": "ScriptScenes", "scriptwriterStepOrder": 8},
 "defaultVoiceId": "YOUR-FISH-AUDIO-VOICE-ID"}
```

3. Run the workflow. The `AuthorAgent` picks up the spoken lines by itself, puts each into its
   scene, and renders the promo with narration. Scenes without narration text get no audio.

In **Promo video from your code** (`quick-win-promo`) the scriptwriter is step 8, so the new step
goes in as step 9 and the later steps move down by one: Director becomes 10, Author 11 and the
review step 12. **Update the review step's `loopTargetStepOrder` to 11** so it still sends work
back to the Author. In **Promo video from your code (lighter)** the scriptwriter is step 9. Always
set `scriptwriterStepOrder` to the scriptwriter's actual number.

The two narrated promo templates, **Quick vertical reel** (`cinematic-feature-spotlight`) and
**Narrated launch trailer** (`story-arc-launch-trailer`), already carry this step, right after the
scriptwriter and before the director, so you only need to set the voice.

## What to check after adding a voiceover

- **The `Voiceover` step result** lists each line (`vo0`, `vo1`, ...) with its status (`ok`,
  `failed` or `skipped`) and its spoken length. `meta.allLinesFailed: true` means nothing was
  spoken (usually a provider or voice id problem).
- **Edited footage:** the `VideoCompile` result has a voiceover section reporting how many lines
  were applied, which were dropped and why (for example a line whose clip was cut out). Each line
  plays over the clip it was written for, even in an edit made from many clips. If some lines were
  dropped, the run shows a warning; if more than half were dropped, the review step sends the edit
  back for another try instead of passing it.
- **Promo:** the `AuthorAgent` reports which scenes received narration in its output's
  `metadata.audio` note. A promo with no `Voiceover` step says "No audio: this workflow has no
  narration step, so the video is silent" there, rather than shipping silence without a word.

## Pitfalls with voiceover

- **No speech provider:** the step cannot speak anything. Ask an admin to add a Fish Audio or
  OpenAI-compatible provider with capability `SpeechSynthesis`; other provider kinds cannot be used
  for speech.
- **Wrong step numbers after inserting the step:** check `voiceoverStepOrder`,
  `scriptwriterStepOrder`, the compile step's `decision`, and every review loop's
  `loopTargetStepOrder`. In **Promo video from your code (lighter)**, later agent steps also read a hand-picked list
  of earlier steps by number (context `SelectedPriorSteps`); renumber those lists too.
- **`ScriptScenes` placed after the Author:** the Author only uses narration that already exists
  when it runs; put the `Voiceover` step before it.
- **Lines overlapping:** allowed; they play over each other. Space your `startSec` values by each
  line's length.
- **A cloned voice that will not work:** a `projectVoiceId` whose consent was revoked or whose
  retention ended fails the step on purpose (`VOICE_CONSENT_MISSING`). Clone the voice again with
  fresh consent, or use a library voice.
- **Which voice speaks when you did not choose:** if the project has exactly one cloned voice that can be used,
  a narration step with no voice of its own uses it. With two or more, the step stops and asks you to choose one
  (`VOICE_CHOICE_REQUIRED`) rather than narrating in the stock voice.
- **No lip sync:** pickups and dubs change the sound only. The mouth on screen is the original
  take's, and the result says so (`lipsync.applied: false`).
- **Not supported:** re-cutting footage to fit the narration (the review asks for shorter
  words instead), and lip sync.

## Letting an AI write the narration for footage

The **Footage edit with narration and captions** template (`video-derush-edit-voiceover`) adds a
`NarrationWriter` agent that writes the lines for the edit, each tied to a shot, pause or spoken
phrase rather than to a time, and a `Voiceover` step with `"kind": "NarrationPlan"` that speaks
them, with karaoke captions on. The editor cuts first and the writer follows it. When a line is
longer than the moment it is tied to, the result reports the overflow and the review step asks the
writer for shorter words; the picture is never re-cut to the voice. See
[Templates](../workflows/templates.md#footage-edit-with-narration-and-captions-video-derush-edit-voiceover).

To have the **story written first** and the clips chosen and ordered to fit it, use the
**Narrated product story** template (`narrated-story`): the same `NarrationWriter` runs before the
editor, from your brief and what the analysis saw in each clip, and the editor keeps the clips each
line is tied to, in the script's order. See
[Templates](../workflows/templates.md#narrated-product-story-narrated-story).

## Captions from the narration

On the **Compile** step, open **Narration & captions** and switch on **Burn captions** (in JSON,
`"enableCaptions": true`). The words burnt into the picture are the words that were **actually
spoken** (after ReelBolt removed anything it does not read aloud), timed by the measured speech.

**Pick a style:**

- **Subtitle** (default) shows a line or two at a time on a see-through box. It works everywhere.
- **Karaoke** lights up each word in your spoken-word colour the moment it is said.
- **Bold** shows big, bold words two or three at a time, white with a heavy outline and no box —
  the look most Instagram Reels and TikToks use. On a vertical video it sits about two-thirds of the
  way down, above the buttons and caption the apps draw over the bottom of the screen. It works
  without a transcription provider too. The **Fast social reel** style picks it automatically.
- **Punch** shows one big, bold, outlined word at a time in the middle of the frame, the look most
  short-form videos use.

Karaoke and Punch need a **transcription provider** so ReelBolt can hear where each word falls;
without one they are skipped with the reason `no_word_alignment`, and Subtitle still works.

**Pick a font.** The editor shows a sample of every font so you can see it before you run. Leave it
on **Automatic** to get the font made for your style (Inter for Subtitle, Montserrat for Karaoke,
Anton for Punch), or choose Inter, Montserrat, Poppins, Bebas Neue, Anton or DejaVu Sans. Every font
is built in and licensed for commercial use.

**Size and position.** Leave **Automatic text size** on: captions are then sized for the style and
for phones, larger on vertical (9:16) videos. Turn it off to set a size yourself, as a percentage of
the video's height. **Position** is the bottom of the frame (or the middle for Punch) unless you
choose Bottom, Middle or Top. Captions always keep clear of the edges, and on vertical videos they
stay out of the bottom fifth and the top of the frame, where TikTok, Reels and Shorts put their own
buttons and text.

**Colours.** Choose the text colour, the box behind it (dark or light see-through, solid black, or no
box with outlined text instead), and for Karaoke the spoken-word colour. Punch never uses a box.

Captions are drawn last, so they never fade with the picture. The compile step's result has a
`captions` section that says which font, position and size were used; if it shows
`degradeReason: "libass_unavailable"`, the video engine was missing its caption renderer and plainer
captions were drawn instead. Captions saved before automatic sizing existed (size `4`) now get the
automatic size too.

## Narration at the end of the video

The last line of narration always finishes. If it would still be playing when the picture ends,
ReelBolt holds the last frame of the video a little longer (until a quarter of a second after the
line ends) instead of cutting the voice off. A fade-out at the end of the video
(`programAudioFadeOutMs`) fades the music, the original sound and the sound effects, never the
narration, so a sentence spoken during the fade is heard in full.

The compile step's result tells you when this happened: `voiceover.extendedForNarrationSec` is how
many seconds the last frame was held, and the video's length (`outputDurationSec`) includes them.

## Cloning a voice (with consent)

The project's **Voices** tab lists the voices a `Voiceover` step can use by `projectVoiceId`:

- **Library voices** are voices the speech provider already offers; register one by its id.
- **Cloned voices** are built from recordings of a real person that are already in the project.
  Cloning needs a Fish Audio provider. Before ReelBolt sends anything, you must tick the consent
  statement word for word and name the person; the statement, who ticked it and when are kept
  with the voice. Choose how long the voice is kept (up to two years). **Revoke consent** stops
  every step from using the voice immediately; **Delete** removes the voice from the provider, the
  recordings from the project and the entry itself, and reports what it removed. Voices past their
  retention date are deleted automatically.

A step that names a cloned voice without valid consent fails; it never falls back to another voice.

## Teaching the narrator how to say a word

If the narrator says a name or a word wrongly, open the project's **Pronunciation** tab. Write
the word, then the same word with an accent on the syllable to stress, for example `Novara` and
`Novàra`. Write the second spelling the way the word should be read: add an accent, or change letters (for example `Xiaomi` and `Sciaomi`). Use letters and accents only, no digits or links. ReelBolt checks every spelling you save, and refuses one that breaks the content rules. Tick **Match capital letters exactly** only when the same letters in another case should be
left alone. The list applies to the spoken narration only, never to subtitles or on-screen text,
and it works in every narrated video in the project. You can keep up to 200 words, and you can
edit or delete any of them. The assistant can also add a word for you when you ask it to.

**If your Fish provider is a Fish speech server you run yourself** (its address is not
`api.fish.audio`), cloning works a little differently, and ReelBolt notices this on its own:

- Use **exactly one** recording, and it must be a **.wav** file. A clear 10–30 seconds of the person
  speaking works best.
- The server needs to know what is said in the recording, so ReelBolt writes it down first using your
  default **Transcription** provider. Without one, cloning stops with a message asking you to set one
  up. If that provider is a hosted service rather than a local one, the recording is sent there too.
- Consent, the retention period, **Revoke consent** and **Delete** work exactly as above. Delete also
  removes the voice from your server.

## Pickups in the presenter's own voice

The **Fix flubbed lines in a talking-head video** template (`video-derush-edit-pickups`) needs
talking-head footage with clear speech. It finds the sentences the presenter stumbled over and
writes the corrected sentence for each. With a consented cloned voice of the presenter, it mutes the
original phrase and plays the pickup in its place. The picture does not change.

:::note How it works
A `PickupPlanner` agent names the sentences and writes the corrections. Set `projectVoiceId` in its
`Voiceover` step to the cloned voice, and the compile step does the swap.
:::

## Dubbing into another language

:::tip Short version
To dub a video in a few clicks, see [Dub a video into another language](dub-a-video.md). The rest of this section is the detail behind the older template.
:::

The **Dubbed video in another language** template (`video-derush-edit-dub`) needs footage with
speech. It translates the narration plan
(name the language in the request box when you run it), speaks
the translation, replaces the original speech under every line (`"voiceoverMode":
"ReplaceDialogue"`) and burns subtitles of what was spoken. To merely turn the original speech
down under narration instead, use `"voiceoverMode": "DuckDialogue"`.

## When the footage already speaks

By default the footage's own sound keeps playing under the narration. If the video you start from
already contains the same narration (for example a video the assistant edited earlier), you would
hear it twice. When that film is mostly speech, the run turns the film's own sound off for you and
adds a note to the result ("the video already has narration in its own sound ..."). Music, sound
effects and the new narration still play.

If your own footage has people talking where the narration goes, the run does not change the sound,
it only adds a note. To turn that speech down under the narration, use `"voiceoverMode":
"DuckDialogue"`; to remove it, use `"ReplaceDialogue"`. These limits are starting values that have
not been tuned on real videos yet.

## A free, local speech service

An admin can register the bundled local speech server as an **OpenAI-compatible** provider with
capability `SpeechSynthesis` (endpoint `http://whisper:8000/v1`, model `speaches-ai/Kokoro-82M-v1.0-ONNX`, voice ids such
as `af_heart`). It costs nothing and sends no audio to a vendor, but cannot clone voices.

## Recipe: Footage edit with narration and captions

Template: **Footage edit with narration and captions**. It cuts your clips first, then writes a
narration line for the kept shots, speaks it and adds captions.

**Set before you run it.** Your clips, **Language** (the language of the narration and captions),
length, shape, style and **Narration** with the voice. Use the notes for the topic and tone. For
a story written before the cut, use a narrated story recipe instead. Setting meanings are in
[Run settings](run-settings.md).

**What it costs.** It uses credits and a voice: the editor, the narration writer, the speech and
the reviewer. The run page shows the price.

**What you get.** One edited video with a spoken narration and burned-in captions.

**Common problems.**

- The run stops at the speech step: no voice provider is connected, or no voice is chosen. Ask
  your admin, and choose a voice you have consent for.
- The narration is in the wrong language: set **Language** instead of asking in the notes.
- The narration runs over the picture: lower the length or shorten what you ask it to cover.
