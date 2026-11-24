---
title: Blur licence plates and other objects (track and censor)
sidebar_position: 11
description: Recipe for finding objects such as licence plates in your footage, following them as they move, and blurring, pixelating or blacking them out.
---

# Blur licence plates and other objects (track and censor)

ReelBolt can **find an object in your footage, follow it as it moves, and obscure it** — the
typical use is blurring licence plates, but it works for any flat thing you can name: a shop sign,
a name badge, a screen showing something private. No green screen is needed.

It takes two steps:

1. **`VideoAnalyze`** finds and follows the objects. You type what to look for ("licence plate");
   a vision AI finds every instance on frames spread through the clip, and a tracker then follows
   each one frame by frame — through camera moves, zooms and tilts.
2. **`VideoCompile`** obscures every tracked object whose name you list — blurred, pixelated or
   solid black — and keeps following it in the finished video.

## What you need to censor objects

Before you start:

- **A vision AI provider** set up by your administrator (any provider with the **Vision**
  capability). It is only used to *find* the objects; following them uses no AI.
- **Your footage**, uploaded to the project.

Cost: each "look" at a frame is one or two calls to the vision provider. By default ReelBolt looks
once a second, at most 12 times per clip.

## Build the track-and-censor workflow

**Step 1 — `VideoAnalyze`.** Pick your video as the source. Under **Track objects**, type what to
look for, for example `licence plate`, and press Enter. In JSON:

```json
{
  "version": 1,
  "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
  "transcription": "Off",
  "trackObjects": [{"label": "licence plate"}],
  "objectDetectEverySec": 1.0,
  "maxObjectDetectFrames": 12
}
```

- `trackObjects` — what to find. Use a short, plain description; you can list several.
- `objectDetectEverySec` — how often ReelBolt looks again, so cars that drive in later are caught
  too. Lower is more thorough and costs more lookups.
- `maxObjectDetectFrames` — the most lookups per clip. On a long clip they are spread evenly over
  the whole clip.
- `objectTrackMaxSide` (default 960) — the resolution the tracker works at. Raise it (up to 1920)
  for small, distant plates.

**Step 2 — `VideoCompile`.** Switch on **Keep every source whole (no edit)** if you don't want the
video cut, and under **Censor tracked objects** type the same label. In JSON:

```json
{
  "version": 1,
  "decision": {"from": "Previous"},
  "analysisStepOrder": 1,
  "keepWholeSources": true,
  "censorLabels": ["licence plate"],
  "censorStyle": "Blur",
  "censorPadding": 0.25,
  "censorHoldSec": 0.4
}
```

- `censorLabels` — which tracked objects to obscure, by the name you gave them in step 1. `"*"`
  obscures everything that was tracked.
- `censorStyle` — `"Blur"`, `"Pixelate"`, or `"Fill"` (solid black; the only style that leaves
  nothing of the original behind).
- `censorPadding` — extra cover on every side, as a fraction of the object's longer side
  (0.25 = a quarter of a plate's width above, below and either side), to cover small detection and
  tracking errors.
- `censorHoldSec` — extra seconds of cover before an object is first tracked and after it is last
  tracked.

You can also put an edit in between: keep a `VideoStoryEditor` step as step 2 and point the compile
step's `decision` at it. Censoring follows the object into **every** part of the finished video
where it appears, however the edit cut it up.

## Check the censored result

- **Step 1's result** shows `meta.objectTracking`: how many objects were tracked (`tracks`), how
  many vision lookups were made, and — if something went wrong — `degraded: true` with a `reason`
  (for example "no Vision provider is configured"). Progress messages name each object as it is
  tracked, the settings the tracker chose for it, and its confidence.
- **Step 2's result** has a `censor` section listing every obscured stretch, and an
  `unmatchedLabels` list. **Anything in `unmatchedLabels` was not obscured anywhere** — no object
  with that name was tracked. Check this list every time.
- **If finding objects went wrong** (no vision provider, a failed lookup, too many objects for
  `maxObjectTracks`), step 2 **stops with an error** instead of making a video, because the result
  might show what you wanted hidden. Fix the cause and run again.
- **The Editor** shows each censored object as a red dashed outline over the video, with its name
  and confidence, on its own **Censored** track in the timeline.

## Limits of automatic censoring

Read this before publishing anything where privacy matters:

- **The vision AI can miss objects**, especially small, blurry or partly hidden ones. Always watch
  the result.
- **Between lookups, a newly appearing object is only found at the next lookup.** With the default
  of one look per second, a plate that drives in can be visible for up to a second before it is
  obscured. Lower `objectDetectEverySec` for busy scenes.
- **A tracked object that gets fully covered** (a person walks in front) ends its track; the next
  lookup starts a new one. `censorHoldSec` covers short gaps.
- **Blur and pixelation are not irreversible.** For anything sensitive, use `"censorStyle": "Fill"`.
- The tracker follows **flat** things best. A face turning away, or an object that bends, is
  followed less precisely; raise `censorPadding` to compensate.

## Placing a region by hand instead of searching

When you know exactly where the object is at one moment, you can skip the vision AI: give the
target a `region` (its four corners as fractions of the frame, top-left, top-right, bottom-left,
bottom-right) and the time it is there. The tracker follows it from that moment, forward and
backward, with no AI call:

```json
"trackObjects": [
  {
    "label": "shop sign",
    "atSec": 2.0,
    "region": {"x0": 0.62, "y0": 0.18, "x1": 0.80, "y1": 0.18,
               "x2": 0.62, "y2": 0.27, "x3": 0.80, "y3": 0.27}
  }
]
```

The assistant can fill this in for you: tell it what to obscure and roughly where.
