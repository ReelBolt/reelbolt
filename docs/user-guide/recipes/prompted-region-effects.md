---
title: Blur or highlight one object (prompted region effects)
sidebar_position: 13
description: Follow one object through your footage from a text prompt and apply an effect to just that part of the picture — blur a speedometer, highlight a product label, dim everything except a face.
---

# Blur or highlight one object (prompted region effects)

:::note
This app now opens as part of [Effects on a clip](effects-on-a-clip.md): choose the matching field there. This page stays as a reference for what each step does.
:::

ReelBolt can **follow one object through a shot from a text prompt and change only that part of the
picture**. Blur the speedometer in a driving shot, brighten the label on a product, dim everything
except the person talking. The rest of the frame is left alone; the effect keeps to the object's own
shape as it moves, changes size and turns, and while the object is completely hidden no effect is
drawn at all.

It takes two steps:

1. **`VideoAnalyze`** finds and follows the object. You type what it is ("speedometer"); a hosted
   segmentation model on fal.ai returns a mask for every frame — the shape of that object alone.
2. **`VideoCompile`** applies an effect through those masks — blur, pixelate, highlight, spotlight,
   colour pop or outline — and draws it into the finished video.

Neither step does this unless you ask for it. An app without a segmentation target and without
an effect entry compiles exactly as it did before.

## What you need

- **A fal.ai connection with the Video segmentation capability**, added by a workspace administrator
  (see [Inference providers](../admin/inference-providers.md)). The model is
  `fal-ai/sam-3-1/video-rle`; it is the only service that follows video objects today.
- **Your footage**, uploaded to the project.
- **A plan that includes segmentation.** Trial plans do not; Creator and above do. A trial run stops
  with `PLAN_NOT_ENTITLED` and says so before spending anything.

### Add the provider (administrator)

1. Open **Inference providers** in the dashboard and press **New Provider**.
2. **Kind**: **fal.ai**. **Capability**: **Video segmentation**. **Model**:
   `fal-ai/sam-3-1/video-rle`. Paste the fal.ai key.
3. Press **Test connection**, then **Create**. If this is the only segmentation connection, tick
   **Set as default segmentation provider** so the steps pick it up without naming it.

The step also has a **Segmentation provider** field, so an app can name a specific connection
when a workspace has more than one.

## Step 1 — follow the object in `VideoAnalyze`

Under **Track objects**, each row is one object. For a row that should use segmentation:

- **Following** — choose **AI segmentation (paid)** instead of the free tracker. The free tracker
  follows flat, rigid things well (signs, plates, screens); segmentation is for anything else — a
  gauge, a face, a logo on a curved or deforming surface.
- **What to look for** — plain words: `speedometer`, `product label`, `the red mug`. This is the
  model's prompt as well as the name you will use in the compile step. Keep it short and specific;
  "the small round dial on the left" is a worse prompt than "speedometer".
- **Optional: mark it by hand.** A region you drew on one frame (or an area the assistant marked)
  becomes a box hint, and the box's centre becomes a click hint, on that frame. Use it when the clip
  holds two similar objects and words are not enough to tell them apart.

In JSON:

```json
{
  "version": 1,
  "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
  "transcription": "Off",
  "trackObjects": [{"label": "speedometer", "method": "Segmentation"}],
  "allowSourceMediaEgress": true,
  "maxSegmentationSeconds": 60,
  "segmentationDetectionThreshold": 0.5,
  "maxSegmentationObjects": 4
}
```

### The consent checkbox, and what it means

A hosted model can only follow the object if it can **see the clip**, so this mode sends the picture
to fal.ai. That is a different thing from every other ReelBolt step, which keeps your footage on the
server, so it is asked for separately:

> **Send this clip to the configured provider** — required for segmentation. Until you tick this, a
> run stops with `EGRESS_REFUSED` and names the clip; nothing is uploaded.

You can also give it for one run without opening the step: on the app page, switch on **Allow sending your media to AI
services** in the **Permission** block above **Run**.

Tick it (`allowSourceMediaEgress: true`) and only the following is sent: the clip itself, through a
link that expires within minutes, and the words you typed. ReelBolt asks the provider not to keep a
copy (the request carries fal.ai's `X-Fal-Store-IO: 0` header) and sends nothing else about the
project — no other files, no transcript, no names. What comes back is a mask per frame, which
ReelBolt stores in the project.

Leave it unticked and a segmentation target cannot run: that is deliberate, not a bug, and the
message names the clip that would have been sent.

### Limits on following

| Setting | Default | Allowed | What happens outside it |
|---|---|---|---|
| `maxSegmentationSeconds` — **Longest clip analysed (seconds)** | 60 | 1 to 600 | A longer source is refused with `SEGMENTATION_SOURCE_TOO_LONG`, naming its own length and this cap, before anything is sent (the sentence is quoted below). |
| `segmentationDetectionThreshold` — **Detection threshold** | 0.5 | 0.01 to 1 | Lower finds more objects, including wrong ones; higher finds fewer. Sent to the model as-is. |
| `maxSegmentationObjects` — **Objects at most per clip** | 4 | 1 to 16 | The most objects one call may return. Extra candidates are dropped by the model, not by ReelBolt. |
| `segmentationTimeoutSeconds` — **Provider timeout** | 900 | 60 to 3600 | One call is abandoned after this long and the step fails with `SEGMENTATION_PROVIDER_ERROR`. |

The 60-second default is a cost guard, not a technical ceiling: a customer who needs a longer clip
raises **Longest clip analysed** up to 600 seconds and pays for the frames.

## Step 2 — add the effect in `VideoCompile`

Under **Effect on one object**, press **Add an effect**. Each entry has:

- **Object** — a track id such as `g1`, or the words the object was followed with
  ("speedometer"). Matched without regard to case. The id is the precise choice when two objects
  share a prompt.
- **Effect** — one of the six below.
- **Strength** — 0 to 2, default 1. Bigger is stronger; 0 changes nothing.
- **Soft edge (px)** — 0 to 64, default 0. Softens the edge of the mask, never the picture.
- **Opacity** — 0 to 1, default 1. How much of the effect is drawn inside the shape.
- **Everything except this object** — the invert switch, see below.

In JSON:

```json
{
  "version": 1,
  "mode": "Reencode",
  "keepWholeSources": true,
  "regionEffects": [
    {"target": "speedometer", "effect": "Blur", "strength": 1.0, "featherPx": 4}
  ]
}
```

**Mode must be Reencode.** An effect changes pixels, so a `StreamCopy` compile is refused with
`EFFECT_REQUIRES_REENCODE` rather than publishing a video without the effect.

### The six effects

| Effect | What it does | Strength 1 looks like |
|---|---|---|
| **Blur** | Softens the object; the picture around it is untouched. | Blur radius 1.5% of the frame's short side |
| **Pixelate** | Replaces the object with coarse blocks. | Blocks 2% of the short side |
| **Highlight it and dim the rest** | Brightens the object and gently darkens everything else, so the eye goes to it. | +10% brightness, +10% contrast, rest dimmed 15% |
| **Spotlight: dim everything else** | Darkens the rest of the frame and leaves the object as it is. | Rest dimmed 60% |
| **Colour pop: keep colour on it only** | Keeps the object in colour and videos everything else in black and white. | Full desaturation outside |
| **Outline** | Draws a white band along the object's silhouette. | Band 0.4% of the short side, at most 16 px |

### Invert: everything except the object

The last switch applies the effect to **the whole picture except the object** — for example
`Spotlight` inverted is a soft vignette that spares the product, and `Blur` inverted blurs the
background while the label stays sharp. Inversion is exact: ReelBolt flips the same mask rather than
growing the shape, so the edge you see is the edge the model gave.

Two entries may point at the same object with different settings; the object's mask is downloaded
and conformed once and fanned out to both.

### Limits on effects

| Limit | Value | Refusal |
|---|---|---|
| Effects per compile | 8 | `EFFECT_LIMIT_EXCEEDED` — the ninth is refused, never silently dropped |
| Object name length | 120 characters | `EFFECT_TARGET_INVALID` |
| Strength | 0 to 2 | `EFFECT_STRENGTH_OUT_OF_RANGE` |
| Soft edge | 0 to 64 px | `EFFECT_FEATHER_OUT_OF_RANGE` |
| Opacity | 0 to 1 | `EFFECT_OPACITY_OUT_OF_RANGE` |
| Compile mode | must be `Reencode` | `EFFECT_REQUIRES_REENCODE` |

Effects are drawn on the picture after inserts and graphics, and **before** the hide-objects pass
and the program fade. So an object you both highlight and censor is censored: hiding wins.

## What it costs

Following an object is a paid call, billed by fal.ai **per 16 frames of video input, rounded up**,
at **$0.01 per 16 frames**:

```text
units  = ceil(frames in the clip / 16)
cost   = units × $0.01
```

- **A 5-second clip is about $0.08**: 5 s at 24 fps is 120 frames, which is 8 units.
- 5 s at 30 fps is 150 frames, 10 units, $0.10.
- A 60-second clip at 30 fps is 1,800 frames, 113 units, $1.13.
- Each object you follow is its own call, so three prompts on one clip is three times that.
- Rendering the effect itself is ordinary encoding; there is no extra charge per effect entry.
- **The credits reserved for the step are the worst case at your cap**, assuming 30 frames per
  second — not this clip's own frame count. A clip that really carries more frames than that
  reservation covers (60 or 120 fps footage at the cap) is refused before anything is bought, with
  `SEGMENTATION_FRAME_BUDGET_EXCEEDED`: it is never truncated, and it never settles past the hold.
  A shorter clip or a lower **Longest clip analysed** makes it fit.

**The same clip with the same prompt, model and settings is free the second time.** ReelBolt keeps
the masks it received, keyed by the clip's contents, the prompt, the model, the detection settings
and the provider; a run that finds them makes no call and bills nothing. Change the prompt or the
threshold and it is a new call.

## When a run stops

Every refusal message starts with its code, and the wording is the run's own: the sentences below are
what the step writes, with the value it fills in shown as `{…}`. The first table stops the run before
anything is bought; the second refuses the compile before a frame is encoded.

### Before anything is bought (the analyze step)

Checked in this order, which is also the order the rows appear in:

| Code | The message, word for word | Fix |
|---|---|---|
| `INSUFFICIENT_CREDITS` | "This run needs about {required} credits and the workspace has {balance} available. Top up or use a smaller workflow." | Top up, follow fewer objects, or shorten the clip. |
| `PLAN_NOT_ENTITLED` | "The {tier} plan does not include text-prompted object segmentation (an object followed through the video, so an effect can be applied to just that part of the picture). Upgrade your plan to use it." | Upgrade the plan, or remove the segmentation target. |
| `EGRESS_REFUSED` | "Text-prompted segmentation sends the source clip to a hosted model, so it needs consent. "{label}" was configured for "{clip}" but allowSourceMediaEgress is false. Turn it on for this step to accept that the clip leaves this host." | Tick **Send this clip to the configured provider**, or set that object back to the free tracker. Nothing was uploaded. |
| `SEGMENTATION_NO_PROVIDER` | "No default VideoSegmentation provider is configured, so this step's segmentation targets cannot run. Add a fal provider for video segmentation and mark it the default." — when the step names a connection that no longer resolves it says "No enabled provider with the VideoSegmentation capability matches the id {id} this step names, and no default VideoSegmentation provider is configured. Add one under Inference providers." | Add a fal.ai connection with that capability, or set a default. |
| `SEGMENTATION_SOURCE_TOO_LONG` | "This clip is {duration}s long but one segmentation pass is capped at {cap}s (maxSegmentationSeconds). Raise the cap — the vendor bills per 16 frames of input — or analyse a shorter clip. The clip was not truncated." | Raise **Longest clip analysed** up to 600, or follow the object on a shorter cut. |
| `SEGMENTATION_FRAME_BUDGET_EXCEEDED` | "This clip is {frames} frames at {fps} fps, but one segmentation pass holds credit for at most {pricedFrames} frames: the {cap}s cap prices 30 fps, and the vendor bills every frame at the clip's own rate. Lower maxSegmentationSeconds or trim the clip (a lower frame rate or a shorter clip); the clip was not truncated." | The clip is inside the cap but carries more frames than the credits reserved for it cover. The cap *sizes* that reservation, so lowering it shrinks the cover instead of the clip: shorten the clip, or follow the object at 30 frames per second or less. |
| `SEGMENTATION_EGRESS_UNAVAILABLE` | "This deployment cannot mint a temporary download URL for the source clip (no presigner is configured, which is the case for local-disk storage), so a hosted model cannot read it. Configure S3-compatible object storage with presigning, or run the step without segmentation targets." | Run segmentation on an installation with object storage, or drop the segmentation target. |
| `SEGMENTATION_PROVIDER_ERROR` | "The segmentation provider "{provider}" did not answer the prompt "{prompt}" ({detail})." | Retry the step; if it repeats, check the key and the provider's status, then raise **Provider timeout**. |
| `SEGMENTATION_BAD_RESPONSE` | "The segmentation provider "{provider}" returned an answer for "{prompt}" that does not fit a {width}x{height} frame ({detail})." — or, when the masks could not be written, "The masks the provider returned for "{prompt}" could not be turned into a mask video ({detail})." | Retry; if it repeats, report it — ReelBolt refuses rather than draw a mask it cannot trust. |

### While the effects are rendered (the compile step)

All of these are checked when the list is saved **and** again when it would render, so a bad list
stops the compile before a single frame is encoded.

| Code | The message, word for word | Fix |
|---|---|---|
| `EFFECT_TARGET_INVALID` | "RegionEffects[{i}].target is blank; name a g{n} track id or the prompt label it was tracked under." — or "RegionEffects[{i}].target is {n} characters; at most 120 are allowed." | Name the object: a track id or the words it was followed with, 120 characters at most. |
| `EFFECT_LIMIT_EXCEEDED` | "RegionEffects has {n} entries; at most 8 are allowed (each one is a mask input and a filter chain). Remove {k}." | Remove entries until 8 remain; the extra one is never dropped quietly. |
| `EFFECT_INVALID` | "RegionEffects[{i}].effect is {value}, which is not a defined effect. Use one of: Blur, Pixelate, Highlight, Spotlight, ColorPop, Outline." | Pick an effect from the list again. |
| `EFFECT_STRENGTH_OUT_OF_RANGE` | "RegionEffects[{i}].strength is {value}; it must be between 0 and 2." | Set it inside the range. |
| `EFFECT_FEATHER_OUT_OF_RANGE` | "RegionEffects[{i}].featherPx is {value}; it must be between 0 and 64 pixels." | Set it inside the range. |
| `EFFECT_OPACITY_OUT_OF_RANGE` | "RegionEffects[{i}].opacity is {value}; it must be between 0 and 1." | Set it inside the range. |
| `EFFECT_REQUIRES_REENCODE` | "RegionEffects requires Mode=Reencode — masking an effect onto the picture has no stream-copy equivalent ({n} entr{ies} configured)." | Switch the compile to `Reencode`. |
| `EFFECT_TARGET_UNVERIFIED` | "Region effect target "{target}" matches no segmentation track. The analyze step tracked "{label}" ({id}). Use a track id or the exact prompt the analyze step was given." | Use one of the names the message lists, re-run the analyze step with that prompt, or fix the cut so the object is in it. |
| `EFFECT_TARGET_AMBIGUOUS` | "Region effect target "{target}" matches {n} segmentation tracks ("{id}", "{id}"). Name the track id instead of the shared prompt." | Name the track id (`g1`, `g2`) instead of the prompt. |
| `SEGMENTATION_MASK_UNVERIFIED` | "The mask for track "{id}" (prompt "{prompt}") does not match the hash the analyze step recorded (expected {expected}, got {actual}). Refusing to render through a mask that may belong to another clip." | Re-run the analyze step; ReelBolt will not render through a mask it cannot verify. |

## Worked example: blur the speedometer

A handheld shot of a car dashboard, speedometer visible at the left, the camera moving.

**`VideoAnalyze`**

```json
{
  "version": 1,
  "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
  "transcription": "Off",
  "trackObjects": [{"label": "speedometer", "method": "Segmentation"}],
  "allowSourceMediaEgress": true,
  "maxSegmentationSeconds": 60
}
```

Run it. The analysis records one `g` track per object it followed, with the prompt it was given and
the stretch of clip it was found on. A prompt that matches nothing produces no track at all — which
the compile step then refuses, rather than producing a picture without the effect.

**`VideoCompile`**

```json
{
  "version": 1,
  "mode": "Reencode",
  "keepWholeSources": true,
  "regionEffects": [
    {"target": "speedometer", "effect": "Blur", "strength": 1.0, "featherPx": 4}
  ]
}
```

A soft edge of 4 px keeps the blur from ending on a hard rectangle, which reads as an edit rather
than as a shallow focus. Raise **Strength** to 1.5 or 2 if the digits are still guessable. If the
object covers less than half the frame on average, the compile says so while it runs ("the effect
will be small and easy to miss") — a faint result on a small dial is the geometry, not a fault.

## Worked example: highlight the product label

A product turning in someone's hands; you want the label to be the thing the eye lands on, without
hiding the product.

**`VideoAnalyze`** — same shape, prompt `product label`:

```json
{
  "version": 1,
  "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
  "transcription": "Off",
  "trackObjects": [{"label": "product label", "method": "Segmentation"}],
  "allowSourceMediaEgress": true,
  "maxSegmentationSeconds": 60,
  "segmentationDetectionThreshold": 0.4
}
```

A slightly lower threshold is the usual adjustment for a label that is small, angled or partly
turned away in the first second.

**`VideoCompile`**

```json
{
  "version": 1,
  "mode": "Reencode",
  "keepWholeSources": true,
  "regionEffects": [
    {"target": "product label", "effect": "Highlight", "strength": 1.0, "featherPx": 8},
    {"target": "product label", "effect": "Outline", "strength": 0.6}
  ]
}
```

Two entries on one object: the highlight lifts the label and dims the rest, the outline draws a band
along its edge. Both use the same mask, so they line up exactly. If the outline feels heavy, lower
its **Strength** — that shrinks the band, not the object.

## What it does not do (yet)

- **No editor-timeline effects.** This works in an app's analyze and compile steps. Applying one
  object effect from the timeline editor is a later wave.
- **No follow-the-object crop (`zoomFollow`).** The data to do it is recorded, but the crop itself
  is not built.
- **The mask is as good as the model.** A fully hidden object has an empty mask, and ReelBolt then
  draws no effect on that frame — the alternative would be smearing the last known shape over
  whatever covers it. Look for too much flicker on a heavily occluded object and consider following
  a different one.
