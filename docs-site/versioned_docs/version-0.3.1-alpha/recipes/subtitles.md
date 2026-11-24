---
title: Add subtitles to a video
sidebar_position: 9
description: Recipe for burning subtitles into a finished video from what is said in it, optionally translated, and downloading SRT and VTT subtitle files.
---

# Add subtitles to a video

**Goal:** take a finished video with speech in it and get a new MP4 with subtitles burned in, plus the same subtitles
as `.srt` and `.vtt` files you can upload to a video platform. Nothing else about the video changes: no cuts, no music,
no new voice.

## Prerequisites

- **The video uploaded to the project** (Files tab).
- **A speech-to-text provider.** An admin adds one under Admin, Inference Providers with capability `Transcription`
  (the local `whisper` service that ships with ReelBolt works). Without one the run stops at the first step and says so,
  instead of returning your video without subtitles.
- **For another language, a chat AI provider** for the translation.

## Subtitles in the language that is spoken

Add the **Subtitles for your video** template (`video-subtitles`) from **New workflow**. It has two steps and calls no AI
model:

| # | Label | What it does |
|---|---|---|
| 1 | Listen to the video | Transcribes the speech. Select your video as the source. |
| 2 | Burn in the subtitles | Draws each sentence on screen while it is spoken and writes the subtitle files. |

Say which language is spoken in your brief if detection guesses wrong (the **Spoken language** control).

## Subtitles in another language

Add **Subtitles in another language** (`video-subtitles-translated`) instead and put the language you want in your brief
(the **Subtitle language** control). It adds a step that translates what is said. The timing is always the original's:
the translation only replaces the words, one sentence for one sentence, so a subtitle never appears earlier or later
than the sentence it translates. A sentence the translation missed stays in the original language rather than
disappearing.

## What you get

- The video with the subtitles burned in (the run's output).
- `subtitles.srt` and `subtitles.vtt`, with the same sentences and timing as the burned subtitles. Cues are at most 42
  characters wide, split at word boundaries, held long enough to read. The compile step's result lists where the two files
  are (`subtitleStorageKeys`), and the API serves them at `/api/v1/projects/{projectId}/step-results/{stepResultId}/subtitles/srt`
  (or `vtt`). The run page has no download button for them yet; ask your administrator or use the API until it does.

## If something looks wrong

- **The run stops with "found no speech to caption".** The transcript is empty: check that the right video is selected,
  that it has speech, and that the transcription provider works.
- **A sentence is missing.** Sentences the transcription did not recognise cannot be subtitled; fix the spoken-language
  setting and run again.
- **Subtitles overlap the picture's own text.** They move to the top of the frame automatically where the bottom is busy.
- **The look.** Open the second step to change the style, font, size, colours and position, like any captions.
