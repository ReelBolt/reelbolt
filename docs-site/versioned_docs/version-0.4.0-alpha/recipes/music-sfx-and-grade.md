---
title: Add music, sound effects and a colour grade
sidebar_position: 7
description: Recipes for adding a background music bed, short sound effects and an overall colour look to an edited video, alone or all together.
---

# Add music, sound effects and a colour grade

:::note
This app now opens as part of [Edit my video](edit-my-video.md): choose the matching field there. This page stays as a reference for what each step does.
:::

This page covers three finishing touches for an edited video, each with its own app, and how to
combine them in one app:

- **Background music** — one music track under the whole edit, quieter while people speak.
- **Sound effects** — short sounds (whooshes, clicks, stingers) at chosen moments.
- **Colour grade** — one overall look (warm, cool, filmic, ...) applied to the whole video.

In all three, an AI chooses with **words and ids only** (which track, "Balanced", "Warm", ...), and
ReelBolt turns those choices into exact volume levels, timings and colour settings. All three need
the `VideoCompile` step's `"mode": "Reencode"` (the default).

## Prerequisites for music and sound effects

- The video uploaded to the project.
- **Audio files uploaded to the project** (for example MP3 or WAV). There is no built-in music or
  sound-effects library, so upload your tracks and effects first. Every audio file in the
  project is offered both as a possible music track and as a possible sound effect. With none
  uploaded, the music and sound-effects steps choose nothing and their reason says so in plain
  words ("No music: this project has no audio files to choose from…").
- Give files clear names (`calm-piano-bed.mp3`, `whoosh-short.wav`): the AI chooses by file name.
- Make sure you have the rights to use the music and sounds.

## Background music recipe

Add the **Footage edit with background music** app (`video-derush-edit-music`) and select your video
in step 1.

| # | Label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Pick background music | `Agent` (context `FullWorkflow`) | `MusicSupervisor` |
| 4 | Compile edited video with music | `VideoCompile` | `VideoTransform` (not an AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 1 adds `"offerMusicTracks": true`, which offers each audio file as `m0`, `m1`, ... (up to
`maxMusicTracks`, 20). Step 4:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableMusic": true, "musicPlan": {"from": "Step", "stepOrder": 3},
 "transitionPolicy": "Auto", "programFadeInMs": 500, "programFadeOutMs": 800,
 "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900, "minSegmentMs": 800}
```

The `MusicSupervisor` picks at most one track, how present it is (`Quiet`, `Balanced`, `Feature`),
how much it dips while someone speaks (`Off`, `Light`, `Normal`, `Heavy`), and whether it repeats to
fill the edit (`LoopToFit`) or plays once (`PlayOnce`). Picking no track is allowed. The music rises
a little in longer pauses and fades in (1.5 s) and out (2.5 s).

**Without the AI:** skip step 3 and name the track yourself with
`"musicTrackProjectFileId": "ID-OF-THE-AUDIO-FILE"` on step 4 (keep `"enableMusic": true`, remove
`musicPlan`). This file is also used as a backup when the AI's choice cannot be used.

Useful step 4 settings: `musicDucking` (`"SpeechEnvelope"` default, or `"Off"` for a constant
quieter bed), `musicFitPolicy` (`"LoopToFit"` default or `"PlayOnce"`), `musicFadeInMs` (1500),
`musicFadeOutMs` (2500).

**A reel cut to music, with no sound from the clips:** set `"muteSourceAudio": true` on step 4
(the **Mute the clips' own sound** switch in the editor). Generated clips often come with their own
audio, which would otherwise play under the track. When nobody speaks and there is no narration, the
music is the whole soundtrack and plays at its own full level, not at the quieter level it uses
under speech. With narration but no sound from the clips, the music plays fuller between the
narration lines and dips under each one, by the ducking amount the music step chose.

**Check:** step 4's result has a music section saying whether music was applied and why not if not.
The review step also checks that speech stays clearly above the music.

## Cutting to the beat of the music

For a reel cut to music, ReelBolt can measure the music and make the cuts land on its beat:

1. In step 1, switch on **Measure the music's beat** (`"analyzeMusicBeats": true`) and pick the
   track under **Music the video is cut to** (`"musicBeatTrackProjectFileId"`). The editor is then
   told the music's tempo (for example "fast, 135 beats per minute"), how long a beat and a bar
   last, where the drop is (the moment the music hits hardest, for example "bar 8, at 13.9 s") and
   where it winds down. Offered music tracks are measured too.
2. In the brief, ask for it: "cut to the beat, put the big product reveal on the drop".
3. In step 4, set **Cut to the beat** to **On every beat** or **On every bar**
   (`"beatSync": "Beat"` or `"Bar"`). Every cut then moves onto the nearest beat or bar by
   shortening the end of a shot a little, never by stretching it, and the cut closest to the drop
   lands exactly on it when the intro is long enough. A shot too short to keep a whole beat stays
   as it is, and a shot is never shortened so much that the narration spoken over it gets cut off.

This needs background music on in step 4 (a fixed track or the music step's choice). Step 4's
result has a `beatSync` section listing every cut: where it was, where it moved to, and whether it
landed on a bar, a beat or the drop. Measuring a track takes a few seconds per track.

## Sound effects recipe

Add the **Footage edit with sound effects** app (`video-derush-edit-sfx`) and select your video in
step 1.

| # | Label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan sound-effect cues | `Agent` (context `FullWorkflow`) | `SoundDesigner` |
| 4 | Compile edited video with sound effects | `VideoCompile` | `VideoTransform` (not an AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 1 adds `"offerSfxClips": true`, which offers each audio file as `x0`, `x1`, ... (up to
`maxSfxClips`, 40). Step 4:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableSfx": true, "sfxPlan": {"from": "Step", "stepOrder": 3},
 "transitionPolicy": "Auto", "programFadeInMs": 500, "programFadeOutMs": 800,
 "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900, "minSegmentMs": 800}
```

The `SoundDesigner` plans zero or more cues. Each cue is an offered sound (`x` id) that plays when
an offered shot, pause or phrase begins in the edit, with a timing word (`OnCut`, or `Lead` /
`Lag`, 150 ms before or after) and a volume word (`Subtle`, `Normal`, `Strong`). No effects at all is
a valid result.

Limits on step 4: `maxSfxCues` (8) and `maxSfxCueSeconds` (4 seconds, longer files are trimmed).
There is no "same sound at every cut" setting; placement is always chosen by the AI.

**Check:** step 4's result lists applied cues and any dropped ones with a reason (for example a cue
whose moment was cut out of the edit). Effects need some audio to mix into: a video with no sound
and no music gets no effects.

## Colour grade recipe

Add the **Footage edit with a colour grade** app (`video-derush-edit-grade-room`) and select your
video in step 1.

| # | Label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Color grade room deliberation | `ColorGradeRoom` | panel plus `ColorGradeDirector` |
| 4 | Compile edited video with grade | `VideoCompile` | `VideoTransform` (not an AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Step 1 needs no extra setting: it measures each shot's colour by default. Step 3's `view` points at
step 1: `{"version": 1, "view": {"from": "Step", "stepOrder": 1}}`. Step 4:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableColorGrade": true, "colorGradePlan": {"from": "Step", "stepOrder": 3},
 "transitionPolicy": "Auto", "programFadeInMs": 500, "programFadeOutMs": 800,
 "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900, "minSegmentMs": 800}
```

The panel (a tone artist, a palette artist and a continuity artist) discusses the footage, and the
director chooses one look for the whole video:

- `look`: `None`, `Warm`, `Cool`, `Filmic`, `Vibrant`, `Muted`, `Mono`
- `strength`: `Subtle`, `Normal`, `Strong`
- `shadowTone`: `Neutral`, `Lifted`, `Deepened`
- `highlightTone`: `Neutral`, `Softened`, `Brightened`

`None` (no grade) is a perfectly valid result for footage that already looks right.

**Cheaper alternative:** replace step 3 with a single `Agent` step running `Colorist` (context
`FullWorkflow`); it gives the same kind of answer, and step 4 stays the same.

**Check:** step 4's result has a colour grade section saying which look was applied, or why none
was.

## Make the music or sound effects instead of uploading them

Where your plan includes generated media, add a **Generate music or sound** step
(`AudioGenerate`) before the compile step. Pick **Music** or **Sound effect**, describe what you
want, set the length and a **Spending limit (credits)**, and run. Each result is added to the
project's files as an audio file, so the music and sound-effects steps offer it just like one you
uploaded. To have it offered in the same run, put the generating step before the `VideoAnalyze`
step; otherwise it is available from the next run. See
[Generate music or sound step](../workflows/step-types.md#generate-music-or-sound-step-audiogenerate).

## Play the music at full volume

By default the music bed sits quietly under the picture. To play a track at its own level, set
`musicGainDb` on the compile step to `0` (the allowed range is -40 to +6 dB). Speech can still lower
the music when ducking is on. A generated clip that arrives with its own sound is controlled by
`generatedClipAudio`: `Keep` leaves it as background without lowering the music, `Mute` removes it.
The step's result reports the music level it used and how each generated clip's sound was treated.

## Combining music, effects and grade in one workflow

You can use all three in one app. Put the AI steps between the editor and the compile step,
and point the compile step at each by number:

| # | Step type | Agent |
|---|---|---|
| 1 | `VideoAnalyze` with `"offerMusicTracks": true` and `"offerSfxClips": true` | `VideoTransform` |
| 2 | `Agent` | `VideoStoryEditor` |
| 3 | `Agent` (`FullWorkflow`) | `MusicSupervisor` |
| 4 | `Agent` (`FullWorkflow`) | `SoundDesigner` |
| 5 | `Agent` (`FullWorkflow`) | `Colorist` |
| 6 | `VideoCompile` | `VideoTransform` |
| 7 | `ReviewLoop` back to step 2 | `VideoReviewAgent` |

Step 6:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableMusic": true, "musicPlan": {"from": "Step", "stepOrder": 3},
 "enableSfx": true, "sfxPlan": {"from": "Step", "stepOrder": 4},
 "enableColorGrade": true, "colorGradePlan": {"from": "Step", "stepOrder": 5}}
```

Set the AI steps' `cacheMode` to `"Never"` so each run gets fresh choices.

## Pitfalls with music, effects and grade

- **"Previous" points at the wrong step.** In these apps the step just before the compile step
  is the music, effects or grade step, not the editor. Always use `{"from": "Step", "stepOrder": N}`.
- **Nothing offered.** Music and effects need `offerMusicTracks` / `offerSfxClips` on step 1 and at
  least one audio file in the project.
- **The same file used as music and as an effect.** Both lists contain every audio file; clear file
  names help the AIs tell beds from short effects.
- **`MUSIC_REQUIRES_REENCODE`, `SFX_REQUIRES_REENCODE`, `COLOR_GRADE_REQUIRES_REENCODE`:** these
  extras need `"mode": "Reencode"`. Music and effects also fail with
  `MUSIC_REQUIRES_AUDIO_REENCODE` / `SFX_REQUIRES_AUDIO_REENCODE` when `audioCodec` is `"copy"`.
- **Colour grade with a green-screen insert.** A strong grade can make the exact screen cut-out less
  precise; see the [tracked screen insert app](tracked-screen-insert.md).

## Recipe: Footage edit with background music

Template: **Footage edit with background music**. A highlight edit with a music track mixed under
the voices.

**Set before you run it.** Your clips, the spoken language, length, shape, style and notes about
what to keep. Upload the music track to the project's files first: there is no built-in music
library. Setting meanings are in [Run settings](run-settings.md).

**What it costs.** It uses credits for the editor, the music pick and the reviewer. The track is
yours, so nothing is generated or bought. The run page shows the price.

**What you get.** One edited video with the track under the voices. If no uploaded audio fits,
the music step chooses nothing and says why in plain words, and you still get the edit.

**Common problems.**

- No music in the result: the project has no audio file. Upload a track and run again.
- Music too loud or too quiet: say so in the notes and run again.
- Clip and empty-edit problems are the same as in
  [Highlight edit from footage](derush-edit.md#recipe-highlight-edit-from-footage).
