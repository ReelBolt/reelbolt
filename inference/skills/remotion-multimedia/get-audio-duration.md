# Audio duration

## With Mediabunny

```ts
import {Input, ALL_FORMATS, UrlSource} from 'mediabunny';
import {staticFile} from 'remotion';

export async function audioSeconds(file: string): Promise<number> {
  const input = new Input({formats: ALL_FORMATS, source: new UrlSource(staticFile(file))});
  return input.computeDuration(); // seconds, the longest track in the file
}
```

`computeDuration()` reads the packets and is exact. There is also a cheaper metadata-based
estimate on the input (`getDurationFromMetadata()`), which can be approximate or missing; use the
exact call unless the file is very large.

## With media-utils

```ts
import {getAudioDurationInSeconds} from '@remotion/media-utils';
const seconds = await getAudioDurationInSeconds(staticFile('voiceover/scene-1.wav'));
```

## Use it for scene length

```tsx
<Composition
  id="Promo"
  component={Promo}
  fps={30}
  width={1920}
  height={1080}
  durationInFrames={1}
  defaultProps={{files: ['voiceover/scene-1.wav', 'voiceover/scene-2.wav']}}
  calculateMetadata={async ({props}) => {
    const secs = await Promise.all(props.files.map(audioSeconds));
    const frames = secs.map((s) => Math.ceil(s * 30) + 10);   // 10-frame tail per scene
    return {durationInFrames: frames.reduce((a, b) => a + b, 0), props: {...props, frames}};
  }}
/>
```

The returned `props` reach the component, so the same frame counts size each `Series.Sequence`.

## Notes

- Do this probing only when the platform did not already tell you the durations.
- A WAV from the platform's text-to-speech is fine; mp3 durations from metadata alone can be off,
  which is why the exact call is preferred.
