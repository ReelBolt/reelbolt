# Placing things in time

## Sequence

`<Sequence from={30} durationInFrames={60}>` shows its children only from frame 30 for 60 frames,
and **inside it `useCurrentFrame()` starts at 0**. That is the main reason to use it: a scene can be
written and tested on its own and dropped anywhere in the timeline.

```tsx
import {AbsoluteFill, Sequence} from 'remotion';

<AbsoluteFill>
  <Sequence from={0} durationInFrames={90}><Intro /></Sequence>
  <Sequence from={90} durationInFrames={120} name="Features"><Features /></Sequence>
</AbsoluteFill>
```

- `layout="none"` stops the sequence from wrapping children in an absolutely positioned div. Use it
  when the children are not full-frame layers.
- `premountFor={15}` mounts the content 15 frames early (invisible) so images, videos and fonts are
  ready on the first visible frame. Use it on any scene that loads media.
- `name` labels the sequence in the Remotion timeline; give every scene one.
- Without `durationInFrames`, a sequence lasts until the end of its parent.
- Sequences nest. Frames are always relative to the closest parent.

## Series

`<Series>` plays children one after another, without computing `from` values.

```tsx
import {Series} from 'remotion';

<Series>
  <Series.Sequence durationInFrames={90}><Intro /></Series.Sequence>
  <Series.Sequence durationInFrames={120} offset={-10}><Features /></Series.Sequence>
  <Series.Sequence durationInFrames={60}><Outro /></Series.Sequence>
</Series>
```

`offset` shifts a scene's start relative to where the previous one ends: negative overlaps them
(useful for hand-made cross-fades), positive leaves a gap. Only `Series.Sequence` children are
allowed inside `Series`.

## Matching scene length to narration

When there is voiceover, drive scene lengths from its measured duration, not from guesses:
`durationInFrames = Math.ceil(sceneAudioSeconds * fps) + tailFrames` (a tail of 6 to 15 frames lets
the last word land before the cut). Put the per-scene frame counts in one array and derive both the
`Series` and the composition's total `durationInFrames` from it, so they cannot drift apart:

```tsx
const scenes = [{id: 'a', frames: 120}, {id: 'b', frames: 150}];
export const total = scenes.reduce((n, s) => n + s.frames, 0);
```

## Loop and Freeze

- `<Loop durationInFrames={30} times={4}>` repeats its children; inside, the frame restarts at 0
  each iteration. `times={Infinity}` loops to the end of the parent.
- `<Freeze frame={0}>` renders its children as if the video were stuck on that frame, which is
  how to hold the last frame of an animation while the voiceover finishes.

## Absolute vs relative time

The composition's own `useCurrentFrame()` is global. Inside any `Sequence` or `Series.Sequence` it is
local. If a child needs to know the global frame (rare), pass it down as a prop from the parent.

## Checklist

- Sum of scene frames, minus overlaps, equals the composition's `durationInFrames`.
- No scene is shorter than its own enter animation plus a readable hold.
- Every scene that loads media has `premountFor`.
