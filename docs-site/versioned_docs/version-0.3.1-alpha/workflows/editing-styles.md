---
title: Editing styles and video shape
sidebar_position: 5
description: Choose how ReelBolt cuts your video — fast and punchy, calm and cinematic — what shape it comes out in (vertical, square, portrait or landscape) and at what resolution, each time you run a workflow.
---

# Editing styles and video shape

The same workflow and the same footage can become very different videos. An **editing style** tells
the AI how to cut it — how fast, in what order, how shots join, what it should feel like — and the
**video shape** decides whether it comes out tall for Reels and TikTok, square for a feed, or wide for
YouTube. You choose both when you press **Run**, and they apply to that run only.

## Choosing a style and shape when you run a workflow

Press **Run** on a workflow. Below your brief, the confirmation box has three extra choices:

- **Editing style** — pick one of the built-in styles, one of your own saved styles, or **No
  particular style**. A short summary under the menu says what the style does.
- **Video shape** — one click: **As set up** (keep the shape the workflow was built with), **9:16**
  (Reels, TikTok, Shorts), **1:1** (square feed posts), **4:5** (portrait feed posts that fill more of
  the screen) or **16:9** (YouTube, websites).
- **Resolution** — **Auto**, **720p**, **1080p** or **4K**; see
  [Choosing the resolution of a run](#choosing-the-resolution-of-a-run).

Some built-in styles suggest a shape — "Fast social reel" picks 9:16 for you. If you click a shape
yourself, your choice wins. The run history shows each run's style, shape and resolution, so you can compare two
versions of the same video side by side.

## The built-in styles

ReelBolt comes with five styles you can use straight away:

- **Fast social reel** — opens on the best moment, quick hard cuts, one big caption word at a time,
  bright colour. Suggests 9:16.
- **Cinematic** — slow and atmospheric, long shots, gentle joins, a film-like colour look and very
  little text. Suggests 16:9.
- **Corporate explainer** — states the problem, then shows the answer; clear captions and tidy
  titles. Suggests 16:9.
- **Documentary** — tells the story in order and lets people finish their sentences, with natural
  colour.
- **Beat-synced music video** — cut to the music, a montage of moments, vivid colour, no titles.
  Suggests 9:16.

## Making your own styles

Open **Styles** in the left menu to see the built-in styles and your own.

- **New style** starts from scratch; **Make a copy** on a built-in style starts from it, so you can
  change just one thing.
- For each choice — **Pace**, **Story order**, **Cuts**, **Tone**, **Captions**, **Colour**, **Music
  energy**, **On-screen text** — pick an option or leave it at **No preference**.
- **Notes for the AI** is an optional sentence or two about the look and feel ("warm and a little
  nostalgic", "keep the product on screen as much as possible"). Notes are treated as a preference,
  never as instructions that change how the workflow works.
- When you create a style from inside a project you can tick **Only use in this project**.

Editing or deleting a style never changes runs you already made: each run keeps its own copy of the
style it used.

## What a style changes, and what it does not

A style steers every AI agent in the run — the editor that picks the shots, the people deciding on
music, colour, sound effects, titles and narration — towards your choices. It also adjusts a few
finishing touches directly:

- **Smooth** cuts switch on gentle transitions between shots, unless the workflow already set its own.
- A **Captions** choice changes how captions look when the workflow already adds captions. It never
  adds captions to a workflow that has none.

A style cannot add steps to a workflow. If your workflow has no music step, choosing "high music
energy" will not add music; if it has no captions, the caption choice does nothing. To add those, use
a template or ask the assistant to change the workflow.

## How the video shape works

Choosing a shape changes the size of the finished video for every editing step in the run. When the
shape is taller or squarer than your footage, ReelBolt crops each clip around its subject (the person
or the phone screen) rather than just cutting off the sides.

The AI editor is told the target shape and the shape of each of your clips, and prefers clips that
already match: for a 9:16 reel it reaches for footage you filmed upright, and uses wide footage only
for moments nothing else covers. For the best vertical video, upload some footage filmed in portrait.

## Choosing the resolution of a run

When you press **Run** on a workflow, the confirmation box offers a **Resolution** choice next to the
video shape. It applies to that run only:

- **Auto** — the usual size, and the default. A vertical, square, portrait or landscape video comes
  out at 1080p, and the original shape at your footage's size, never above 1080p or your plan's limit,
  whichever is lower (so 720p on Trial). On Enterprise, the original shape keeps your footage's size.
  This is how runs worked before the choice existed.
- **720p**, **1080p** or **4K** — the finished video comes out at that resolution.

Resolution is measured on the **short side** of the video, so it works the same for every shape: 4K
is 3840×2160 for a 16:9 video and 2160×3840 for a 9:16 one, and 1080p is 1920×1080 or 1080×1920.

Your plan sets the highest resolution you can choose: 720p on Trial, 1080p on Creator, 4K on Pro and
Team, and 4K and above on Enterprise. Choices above your plan are shown disabled with a hint to
upgrade. A higher resolution uses more render credits — 4K about four times as many as 1080p — and the
estimate before the run includes it. See
[Export resolution on each ReelBolt plan](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan).

## Asking the assistant

You can ask the [assistant](../assistant/index.md) to "list my styles", "save a style called Launch reel:
fast, hook first, punchy captions", or "run the reel workflow in the cinematic style as a 4:5
video in 4K". It shows you what it is about to save or run and waits for your yes.
