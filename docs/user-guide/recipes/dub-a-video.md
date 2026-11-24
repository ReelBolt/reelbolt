---
title: Dub a video into another language
sidebar_position: 6
description: Make a video you already have speak another language, with the original voices replaced by a spoken translation and subtitles of what is said.
---

# Dub a video into another language

**Goal:** a new video where the speech is in another language. The picture stays the same, the original voices are replaced by a spoken translation, and subtitles show what is said.

## Before you run it

- **Your video**, with speech in it, uploaded to the project.
- A speech-to-text service, a chat AI service and a voice service. If one is missing, the readiness line above **Run** names it and links to the fix. Your admin sets these up (see [Inference providers](../admin/inference-providers.md)).

## Dub it

1. Open your project and go to **Apps**.
2. Open [Languages](languages.md).
3. Choose **Your video**.
4. Choose the **Target language**.
5. Turn **Dub** on. Under **More options** you can pick the voice.
6. Press **Run**. The price shows on the run page before you start.

The lips are not re-synced: the mouth still moves to the original words.

## Other ways to dub

If your footage still needs cutting, the [Edit my video](edit-my-video.md) app can cut and add a spoken narration in one run. To keep the original speech but turn it down under a new voice, see [Voiceover](voiceover.md#when-the-footage-already-speaks).

## Where to go next

- [Languages](languages.md) has every field and what each one costs.
- [Add subtitles to a video](subtitles.md) if you only want subtitles, not a new voice.
- Not sure which app fits? Ask the [assistant](../basics/assistant-basics.md) or see [Choose an app](choose-a-recipe.md).

## How it works

This page covers the dub setting of the Languages app. The older template **Dubbed video in another language** (`video-derush-edit-dub`) is described under [Voiceover](voiceover.md#dubbing-into-another-language). It uses a `NarrationTranslator` agent and the `"voiceoverMode": "ReplaceDialogue"` setting.
