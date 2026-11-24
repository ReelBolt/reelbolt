# Video duration

```ts
import {Input, ALL_FORMATS, UrlSource} from 'mediabunny';
import {staticFile} from 'remotion';

export async function videoSeconds(file: string): Promise<number> {
  const input = new Input({formats: ALL_FORMATS, source: new UrlSource(staticFile(file))});
  return input.computeDuration();
}
```

This is the container duration: the end of the last track. If the audio is longer than the picture,
the number reflects the longer one. To get the picture only:

```ts
const track = await input.getPrimaryVideoTrack();
const seconds = track ? await track.computeDuration() : 0;
```

## Using it

- Size a scene that shows a whole clip: `durationInFrames = Math.ceil(seconds * fps)`.
- Cap a scene that shows part of a clip: `Math.min(wanted, available)`; never ask a video element
  for frames past its end.
- When the clip is shorter than the scene, hold the last frame with `<Freeze>` (see
  `remotion-markup`, `sequencing.md`) or loop it if it is made to loop.

## Compositions that follow a clip

Put the probe in `calculateMetadata` so the composition is exactly as long as the clip, and also
read its size there if the output should match it (`get-video-dimensions.md`).

## Alternative

`getVideoMetadata(src)` from `@remotion/media-utils` returns `{durationInSeconds, width, height,
...}` in one call, but it is deprecated and loads the whole file into a hidden `<video>`. Prefer
Mediabunny.
