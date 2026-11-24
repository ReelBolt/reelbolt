# Timing and animation

All motion is a mapping from the frame number to a value.

## interpolate

```tsx
import {interpolate, useCurrentFrame, useVideoConfig} from 'remotion';

const frame = useCurrentFrame();
const {fps} = useVideoConfig();

const opacity = interpolate(frame, [0, 15], [0, 1], {
  extrapolateLeft: 'clamp',
  extrapolateRight: 'clamp',
});

// Several stops: fade in, hold, fade out. Input range must be strictly increasing.
const {durationInFrames} = useVideoConfig();
const alpha = interpolate(frame, [0, 12, durationInFrames - 12, durationInFrames], [0, 1, 1, 0]);
```

- Both arrays must have the same length; the input array must be strictly increasing.
- Add `easing` for non-linear motion: `import {Easing} from 'remotion'`, then
  `easing: Easing.out(Easing.cubic)` (decelerate: use for entrances), `Easing.in(Easing.cubic)`
  (accelerate: exits), `Easing.inOut(Easing.cubic)`, `Easing.bezier(0.2, 0.8, 0.2, 1)`,
  `Easing.back(1.4)`, `Easing.bounce`.
- Colours: `interpolateColors(frame, [0, 30], ['#000', '#7c3aed'])`.
- Percent or unit strings: interpolate the number, then build the string:
  `` `${x}%` ``, `` `translateY(${y}px)` ``.

## spring

A physics curve that goes from 0 to 1 and settles. Good for entrances, scale-ups and anything that
should feel alive.

```tsx
import {spring} from 'remotion';

const enter = spring({
  frame,
  fps,
  config: {damping: 200},      // smooth, no bounce
  durationInFrames: 20,        // optional: stretch the curve to a fixed length
  delay: 8,                    // optional: start 8 frames late
});
const y = interpolate(enter, [0, 1], [40, 0]);
const scale = interpolate(enter, [0, 1], [0.92, 1]);
```

Config presets that work well:

| Feel | config |
|---|---|
| Smooth, no bounce (default choice) | `{damping: 200}` |
| Snappy UI | `{damping: 20, stiffness: 200}` |
| Playful bounce | `{damping: 8}` |
| Heavy, slow | `{damping: 15, stiffness: 80, mass: 2}` |

Other options: `from`/`to` (default 0 and 1), `reverse`, `overshootClamping`. `measureSpring({fps,
config})` returns how many frames the spring needs to settle; use it to size a `Sequence`.

Animate the exit with a second spring started at the exit frame, or by subtracting:
`const exit = spring({frame: frame - exitStart, fps, config: {damping: 200}}); const visible = enter - exit;`.

## Staggering and repetition

```tsx
items.map((item, i) => {
  const s = spring({frame, fps, delay: i * 4, config: {damping: 200}});
  return <Row key={item.id} style={{opacity: s, transform: `translateY(${(1 - s) * 30}px)`}} />;
});
```

Periodic motion: `Math.sin((frame / fps) * Math.PI * 2 * hertz)`. Looping progress:
`(frame % period) / period`.

## Typical durations (at 30 fps)

| Thing | Frames |
|---|---|
| Fade or small slide in | 9 to 15 |
| Spring settle (`damping: 200`) | about 20 to 30 |
| Scene transition | 12 to 20 |
| Minimum time a short line stays on screen | 45 |

## Deterministic randomness

`random(seed)` returns 0 to 1, identical on every render. Use it for scattered particles, jitter
and variation: `random(`particle-${i}`)`.
