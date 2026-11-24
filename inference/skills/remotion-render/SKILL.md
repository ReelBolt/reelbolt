---
name: remotion-render
description: Render stills and video from a composition, with CLI flags, codecs and transparent output
version: 4.0.535
---

How to turn a composition into a file, and how to check your work cheaply first. Read
`transparent-videos.md` for graphics that must keep an alpha channel.

## Strategy: check small, render once

A full render is the slowest and most expensive step. Do not use it to find mistakes.

1. **List compositions** to confirm your ids registered:
   `npx remotion compositions src/index.ts`
2. **Render a few stills** across the timeline (first, middle, last frame, and each scene's middle):
   `npx remotion still src/index.ts Promo out/f90.png --frame=90`
3. Fix layout, text and timing problems from the stills.
4. **Render the video once.**

If the platform gives you tools for this (composition info, preview stills, a layout check, the
render-and-upload tool), use them instead of raw commands: they run with the right browser, limits
and output location. The commands below are what those tools run, for reference.

## Video

```bash
npx remotion render src/index.ts Promo out/video.mp4
```

Frequently used flags (all verified for Remotion 4.0.535):

| Flag | Meaning |
|---|---|
| `--codec=h264` | default; `h265`, `vp8`, `vp9`, `av1`, `prores`, `gif`, plus audio-only `mp3`, `aac`, `wav` |
| `--crf=18` | quality for h264/h265/vp8/vp9 (lower is better and bigger; 18 is high, 23 is fine) |
| `--x264-preset=medium` | h264 only; slower presets compress better |
| `--pixel-format=yuv420p` | default; the one that plays everywhere |
| `--image-format=jpeg` / `png` | per-frame capture format; `png` is needed for transparency |
| `--jpeg-quality=90` | with `--image-format=jpeg` |
| `--props='{"title":"X"}'` or `--props=./props.json` | override `defaultProps` |
| `--frames=0-89` | render only a frame range |
| `--scale=0.5` | multiply output size (`2` for crisper text) |
| `--muted` | no audio track |
| `--enforce-audio-track` | always write an audio track, even if silent |
| `--audio-codec=aac` | `aac`, `mp3`, `opus`, `pcm-16`; which ones are valid depends on `--codec` (h264: aac/mp3/pcm-16; vp8/vp9: opus/pcm-16) |
| `--audio-bitrate=192k`, `--video-bitrate=8M` | explicit bitrates instead of CRF |
| `--concurrency=2` | parallel browser tabs; keep low on small machines (default: half the cores) |
| `--timeout=60000` | per-frame `delayRender` timeout in ms (default 30000) |
| `--public-dir=./public` | where `staticFile()` reads from |
| `--sequence` | write a numbered image sequence instead of a video |
| `--log=verbose` | more output when diagnosing |
| `--gl=angle` | choose the WebGL backend (needed when a scene uses WebGL/three.js headless) |
| `--chromium-executable=<path>` | use a specific browser binary (the sandbox sets this for you) |
| `--overwrite` | replace an existing output file |

Choose output size by editing the composition's `width`/`height`, or use `--scale`. Do not render
above what the composition was designed for.

## Stills

```bash
npx remotion still src/index.ts Thumb out/thumb.png            # frame 0
npx remotion still src/index.ts Promo out/f45.jpg --frame=45   # a frame of a video composition
npx remotion still src/index.ts Thumb out/thumb.png --scale=2  # 2x pixel density
```

The image format follows the output extension (`png`, `jpeg`, `webp`, `pdf`) or `--image-format`.

## Platform notes

- Output files go where the platform tells you (an `out/` folder inside the project); the upload
  tool picks the finished file up from there.
- A render uses the composition's own fps and length. To shorten or lengthen the video, change the
  composition (or its `calculateMetadata`), not the render flags.
- Only one full render per attempt is allowed. If the tool refuses a second render, fix the stills
  first.
- If a render fails, read the first error in the log. The usual causes: a remote URL
  (no network), a missing file in `public/`, a duplicate composition id, `delayRender` never
  released, an unsupported flag/codec pair.

## Codec choices

| Goal | Settings |
|---|---|
| Plays everywhere (social, web) | `--codec=h264 --pixel-format=yuv420p --crf=18` |
| Smaller file, modern players | `--codec=h265` or `av1` (slower) |
| Web video with alpha | `--codec=vp9 --pixel-format=yuva420p --image-format=png` |
| Editing master / alpha for editors | `--codec=prores --prores-profile=4444 --pixel-format=yuva444p10le --image-format=png` |
| Animated GIF | `--codec=gif --every-nth-frame=2` |
| Audio only | `--codec=mp3` (or `aac`, `wav`) |

Stills accept `png` (default), `jpeg`, `webp` and `pdf` as the image format; video frames accept only
`png`, `jpeg` (default) or `none`.
