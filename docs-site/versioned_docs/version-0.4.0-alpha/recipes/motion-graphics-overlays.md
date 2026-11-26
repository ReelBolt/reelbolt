---
title: Add titles, lower-thirds and callouts (motion graphics)
sidebar_position: 6
description: Recipe for adding animated titles, name captions and callouts to filmed footage, designed and rendered by ReelBolt's motion-graphics AI.
---

# Add titles, lower-thirds and callouts (motion graphics)

:::note
This now opens as part of [Edit my video](edit-my-video.md): choose the matching field there. This page stays as a reference for what each part does.
:::

**Goal:** add on-screen graphics to an edited video — a title at the start, a "lower-third" name
caption when someone is introduced, callouts for key points — placed where they do not cover faces
or busy parts of the picture, at moments that match what is being said.

ReelBolt designs each graphic as a small animation with a transparent background and lays it over
your footage. Plain text on a semi-transparent box is the fallback when an animation is not
appropriate or fails.

:::note How it works
A `MotionGraphicsPlanner` agent designs each graphic as a small animation that it writes and
renders itself, and the `VideoCompile` step lays it over the footage.
:::

## Prerequisites for motion graphics

- The video uploaded to the project.
- A chat AI provider, and the animation sandbox service running (standard installation).
- Recommended: a transcription provider, so graphics line up with what is said (for example a name
  appears when the person is introduced).

## The motion graphics workflow

Add the **Footage edit with titles and callouts** template (`video-derush-edit-graphics`) and select your
video in step 1.

| # | Label | Step type | Agent |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (not an AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan motion graphics overlays | `Agent` (context `FullWorkflow`) | `MotionGraphicsPlanner` |
| 4 | Compile edited video with graphics | `VideoCompile` | `VideoTransform` (not an AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

**Step 1** finds good spots for graphics:

```json
{"version": 1, "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
 "emitOverlayPlacements": true}
```

Each spot is offered to the AI as an id (`p0`, `p1`, ...) with its area of the screen (`LowerThird`,
`UpperThird` or `CenterBand`), a suitability score, a hint for light or dark text, and the moment it
belongs to. Long, still shots get several spots at different moments (`maxTimeSlicesPerRegion`,
default 3). Limits: `maxPlacementsPerShot` 2, `maxPlacements` 40.

**Step 3** must see both step 1 (the spots) and step 2 (what was kept), so its context is
`FullWorkflow`. Each spot is marked with whether its moment survived the edit, and the AI prefers
those.

**Step 4**:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableGraphics": true, "graphicsPlan": {"from": "Step", "stepOrder": 3},
 "transitionPolicy": "Auto", "programFadeInMs": 500, "programFadeOutMs": 800,
 "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900, "minSegmentMs": 800}
```

`graphicsPlan` must point at the planner step by number. "Previous" would also work here, but the
numbered form keeps working if you add steps.

## How to run and steer the graphics

When you click **Run**, describe what you want in **Your brief**:

> Add a title card "Spring Launch 2026" at the start, a lower-third "Maria Lopez, Head of Product"
> when Maria introduces herself, and at most two callouts for the key numbers. Bold, modern style,
> white text with a purple glow.

The AI chooses only among offered spots and picks a duration word for each graphic: `Short` (1.5 s),
`Medium` (3 s) or `Hold` (6 s) by default. It never chooses times or positions itself; ReelBolt
places the graphic exactly from the chosen spot.

Step 3 is the slowest step: for each graphic the AI writes an animation, checks it builds,
and renders it.

## What to check after adding graphics

- **Step 1** result: `meta.offeredPlacementIdCount` above 0, and a `view.placements` list.
- **Step 3** result: the `overlays` list. A graphic with a `renderedAssetStorageKey` is a rendered
  animation; one with only `text`/`subtext` is a plain text box (its `reason` should say why).
- **Step 4** result: the graphics section reports how many graphics were applied, and lists any that
  were dropped with a reason (for example a spot whose moment was cut out of the edit).

## Settings for plain-text graphics

These `VideoCompile` settings only affect the plain-text fallback boxes (defaults in brackets):
`overlayFontSizePct` (5), `overlayBoxHeightPct` (16), `overlayBoxWidthPct` (82),
`overlayFontColor` (`"white"`), `overlayBoxColor` (`"black@0.45"`), `maxOverlayTextChars` (80),
`maxOverlaySubtextChars` (60). Durations: `overlayShortMs` (1500), `overlayMediumMs` (3000),
`overlayHoldMs` (6000), with a fade of `overlayFadeMs` (300). `maxOverlays` (20) caps how many
graphics are applied.

## Variations of motion graphics

- **Panel of artists:** the **Footage edit with titles, designed by a graphics team** template
  (`video-derush-edit-graphics-room`) uses a `GraphicsRoom` step instead of step 3. A layout
  artist, a timing artist, a copy artist and an insert artist discuss the options, then a
  `MotionGraphicsDirector` writes the plan and renders the graphics. Its `view` must point at the
  analysis step: `{"version": 1, "view": {"from": "Step", "stepOrder": 1}}`.
- **Graphics inside a green screen:** the same AI can also fill a green phone or laptop screen with
  a rendered scene that follows the screen. See
  [Tracked screen insert recipe](tracked-screen-insert.md).
- **No cutting, just graphics:** tell the editor in the request to keep the whole clip.

## Pitfalls with motion graphics

- **No graphics at all:** step 1 was missing `"emitOverlayPlacements": true`, step 4 was missing
  `"enableGraphics": true`, or `graphicsPlan` pointed at the wrong step. Also, choosing zero
  graphics is allowed: the AI is told to prefer none over clutter, so ask for them explicitly.
- **Only plain text boxes:** rendering failed or the sandbox was not available. The overlay's
  `reason` usually says which.
- **A graphic appears at the wrong moment for the words:** without transcription the AI cannot know
  what is said when. Add a transcription provider.
- **`GRAPHICS_REQUIRE_REENCODE`:** graphics need `"mode": "Reencode"` (the default).
- **Long step 3:** expected. Each rendered graphic takes several tool rounds; plan for minutes per
  graphic.
