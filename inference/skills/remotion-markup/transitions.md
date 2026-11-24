# Transitions between scenes

A transition is two scenes visible at the same time for a short while, with one value (progress 0
to 1) controlling how much of each is shown. Build it with overlapping sequences; that needs no
extra packages and works offline.

## Cross-fade with overlapping Series items

```tsx
import {AbsoluteFill, interpolate, Series, useCurrentFrame} from 'remotion';

const FADE = 12; // frames of overlap

const FadeScene: React.FC<{frames: number; children: React.ReactNode}> = ({frames, children}) => {
  const frame = useCurrentFrame();
  const opacity = interpolate(frame, [0, FADE, frames - FADE, frames], [0, 1, 1, 0]);
  return <AbsoluteFill style={{opacity}}>{children}</AbsoluteFill>;
};

<Series>
  <Series.Sequence durationInFrames={90}><FadeScene frames={90}><A /></FadeScene></Series.Sequence>
  <Series.Sequence durationInFrames={90} offset={-FADE}><FadeScene frames={90}><B /></FadeScene></Series.Sequence>
</Series>
```

The negative `offset` makes B start while A is still fading out. Total length is the sum of the
scene lengths minus `FADE` for every overlap; set the composition's `durationInFrames` to that.

## Slide and wipe

Same structure; replace the opacity with a transform or clip:

```tsx
// slide in from the right, over FADE frames
const p = interpolate(frame, [0, FADE], [1, 0], {extrapolateRight: 'clamp', easing: Easing.out(Easing.cubic)});
style={{transform: `translateX(${p * 100}%)`}}

// left-to-right wipe revealing the scene
const w = interpolate(frame, [0, FADE], [100, 0], {extrapolateRight: 'clamp'});
style={{clipPath: `inset(0 ${w}% 0 0)`}}
```

For the outgoing scene, apply the mirrored values (to `-100%`, or reveal the next scene on top).

## Choosing

| Situation | Use |
|---|---|
| Related ideas, calm pace | cross-fade, 10 to 15 frames |
| New topic | slide or wipe, 12 to 20 frames |
| Beat-driven, fast | hard cut (no transition) |
| Everything | pick **one** transition style per video and repeat it |

Do not mix more than two transition styles in one video.

## The transitions package (optional)

`@remotion/transitions` provides `TransitionSeries` with presets (`fade`, `slide`, `wipe`, `flip`,
`clockWipe`) and timings (`linearTiming`, `springTiming`):

```tsx
import {TransitionSeries, linearTiming} from '@remotion/transitions';
import {fade} from '@remotion/transitions/fade';

<TransitionSeries>
  <TransitionSeries.Sequence durationInFrames={90}><A /></TransitionSeries.Sequence>
  <TransitionSeries.Transition presentation={fade()} timing={linearTiming({durationInFrames: 12})} />
  <TransitionSeries.Sequence durationInFrames={90}><B /></TransitionSeries.Sequence>
</TransitionSeries>
```

It is not pre-installed. Install it only if it can be added at exactly the project's Remotion
version (4.0.542); otherwise use the hand-built version above. A transition shortens the total by its
own duration, same as a negative `offset`.
