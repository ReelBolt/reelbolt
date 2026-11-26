---
title: Make an avatar explain a topic
sidebar_position: 16
description: Recipe for making one of your project's avatars explain a topic, or say your exact words, in a short talking clip.
---

# Make an avatar explain a topic

**Goal:** have a person from your project's [avatars](../projects/avatars.md) explain a topic, or say words you wrote.

## Recipe: Avatar explainer from a topic

**Inputs.**

- **Avatar:** a ready avatar of the project with a voice linked to it.
- **I will write the words:** off, you give a **Topic** and ReelBolt writes what the person says. On, you type **What they say** and it is spoken word for word.
- **Tone**, **Language** and **Shape**. Under **More options** you can change the lip-sync, picture and script models. A workspace admin must have set up a lip-sync model first (see [Avatars](../projects/avatars.md#a-talking-clip-on-a-budget)); without one the run does not start.

**Length.** The clip is short: about 15 seconds at most. Words that run longer do not fit. This recipe does not make a long presenter video.

**What it costs.** It uses credits: one still picture of the person plus the seconds of video the speech lasts. The video step has a spend cap. The price shows on the run page before you start. If you write the words yourself, the step that writes them is skipped and not charged.

**What you get.** One short talking clip of the person.

**Permission to send media.** The clip step sends the avatar's pictures and voice to the video provider. That is off until you switch on **Allow sending your media to AI services** in the **Permission** block above **Run** on the app page; it applies to the run you start. In the assistant chat, such a run always asks you on its card first. See [Avatars](../projects/avatars.md).

**If it fails.**

- The run stops for the voice: link a voice to the avatar in the **Avatars** tab.
- The words are too long: shorten them to about 15 seconds of speech.
- The clip step does not start: check that sending media is switched on, and that the avatar is ready.
