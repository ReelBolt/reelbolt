---
name: remotion-multimedia
description: Reading audio and video duration and dimensions with Mediabunny
version: 4.0.535
---

Reading facts about media files (duration, dimensions, tracks) from inside a Remotion project, so
a composition can size itself to its footage instead of guessing.

Topic files (read with `ReadSkillResource`):

- `get-audio-duration.md`: how long an audio file is.
- `get-video-duration.md`: how long a video file is.
- `get-video-dimensions.md`: the pixel size (and rotation) of a video or image.

## Tools, in order of preference

1. **Facts the platform already gives you.** Voiceover staging returns per-scene durations; analysed
   source footage comes with duration and size in the project data. Use them: no probing needed.
2. **Mediabunny** (`mediabunny` on npm) is a library that reads media containers in JavaScript. It
   is installed alongside Remotion's tooling (check `node_modules/mediabunny`; if it is missing,
   it cannot be installed offline, so use option 3 or 1).
3. **`@remotion/media-utils`** (`getAudioDurationInSeconds`, `getVideoMetadata`, `getImageDimensions`)
   is the older helper set. Not pre-installed; its video helpers are marked deprecated in favour
   of Mediabunny.

## Where to probe

Probe **outside the frame loop**, never in a component body:

- in `calculateMetadata` of the `<Composition>` (runs once before rendering and may return a
  `durationInFrames`, `width`, `height` or `props`), or
- in a `delayRender` / `continueRender` pair if a scene truly needs the value on mount.

Each frame runs the component again. Probing in the component body repeats the work thousands of
times and can time the render out.

## Reading a local file

Files in `public/` are reached with `staticFile()`, which gives a URL path:

```ts
import {Input, ALL_FORMATS, UrlSource} from 'mediabunny';
import {staticFile} from 'remotion';

const input = new Input({formats: ALL_FORMATS, source: new UrlSource(staticFile('clips/demo.mp4'))});
```

Create the `Input` once per file and call `input.dispose()` when you are done with it.

## Handling failure

Probing can throw (corrupt file, unsupported format, the file is not where you think it is).
Catch it, report which file failed, and fall back to a stated default (for example the planned
scene length) rather than letting the whole render fail. Never invent a duration silently.

## Converting to frames

```ts
const frames = Math.ceil(seconds * fps);
```

Round **up** for durations so the last bit of audio or video is not cut off.
