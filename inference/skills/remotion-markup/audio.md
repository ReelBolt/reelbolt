# Audio

## Playing a file

```tsx
import {Audio, staticFile} from 'remotion';

<Audio src={staticFile('music/bed.mp3')} volume={0.25} />
```

- `<Audio>` placed inside a `<Sequence>` starts when the sequence starts. Put narration for a scene
  inside that scene's sequence so it stays in sync if the scene moves.
- Supported inputs include mp3, wav, aac, m4a, ogg. Files come from `public/` via `staticFile()`.
- The audio of a video clip comes from the video element itself (`<OffthreadVideo>`); mute it with
  `muted` or `volume={0}` if you want only your own soundtrack.

## Volume

`volume` is a number from 0 up, or a function of the frame **within the audio's own timeline**:

```tsx
const {durationInFrames, fps} = useVideoConfig();

<Audio
  src={staticFile('music/bed.mp3')}
  volume={(f) =>
    interpolate(f, [0, fps, durationInFrames - fps * 2, durationInFrames], [0, 0.3, 0.3, 0], {
      extrapolateLeft: 'clamp',
      extrapolateRight: 'clamp',
    })
  }
/>
```

That is a one-second fade-in and a two-second fade-out.

## Trimming and speed

- `trimBefore={fps * 5}` starts playback 5 s into the file; `trimAfter={fps * 20}` stops at 20 s
  (both are frame counts at the composition fps). The older names `startFrom` / `endAt` are
  deprecated.
- `playbackRate={1.25}` speeds it up. Keep it between 0.5 and 2.
- `loop` repeats the file to fill its sequence. Loop only music beds that are made to loop.

## Voiceover and music together

Narration must stay intelligible. A music bed under speech sits at **0.15 to 0.25**, narration at
1.0. Duck the music while the voice speaks: build a volume function that lowers it by about 60%
during each narrated range and ramps over 6 to 10 frames:

```tsx
const ducked = (f: number) => {
  const speaking = narrationRanges.some(([a, b]) => f >= a && f <= b);
  return speaking ? 0.12 : 0.3;
};
```

Use `interpolate` between the two levels around the edges of each range so there is no click.

## Sound effects

A short effect is just another `<Audio>` inside a `<Sequence from={frameOfHit} durationInFrames={...}>`.
Keep effects subtle (volume 0.3 to 0.6 relative to narration) and no more than one per beat.

## ReelBolt specifics

- Voiceover files that the platform generates are staged as `public/voiceover/scene-{n}.wav`; use
  the `timing` data that the staging tool returns to size each scene (see `sequencing.md`).
- Never fetch audio from a URL. Use only staged project files.
- Preview renders (stills) are silent; audio is only checked in a full render.
