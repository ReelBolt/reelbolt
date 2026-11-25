---
name: remotion-markup
description: Writing scene markup that renders correctly: timing, sequencing, transitions, text, fonts, audio and media
version: 4.0.535
---

How to write the React markup of a Remotion scene so it renders correctly and looks good. Read the
topic file you need with `ReadSkillResource`:

| File | Read it when you are... |
|---|---|
| `timing.md` | animating anything: `interpolate`, `spring`, easing, staggering |
| `sequencing.md` | placing scenes and layers in time: `Sequence`, `Series`, `Loop`, `Freeze` |
| `transitions.md` | moving between scenes: cross-fades, slides, wipes |
| `text.md` | titles, captions-as-text, counters, typewriter and highlight effects |
| `fonts.md` | choosing and using fonts (the sandbox has no network) |
| `audio.md` | voiceover, music and sound effects: volume, trimming, ducking |
| `media.md` | images, video clips, GIFs, SVG, Lottie |
| `compositions.md` | props, dynamic duration (`calculateMetadata`), several compositions |

## The model in one paragraph

Remotion renders each frame by mounting your component with a given frame number and taking a
screenshot. So a scene is a **pure function of the frame**: the same frame number must always
produce the same pixels. Everything that moves is computed from `useCurrentFrame()` and
`useVideoConfig().fps`.

## Hard rules

1. **No real-time animation.** No CSS `transition`, no CSS `@keyframes`, no `setTimeout`,
   `setInterval`, `requestAnimationFrame`, or animation libraries that advance on a clock
   (`framer-motion` `animate`, `react-spring` `useSpring` etc. will not follow the frame). Compute
   values from the frame with `interpolate` and `spring`.
2. **No non-determinism.** No `Date.now()`, `Math.random()`, or unseeded randomness. Use
   `random('some-seed')` from `remotion`.
3. **Always clamp.** `interpolate` extrapolates by default; pass
   `{extrapolateLeft: 'clamp', extrapolateRight: 'clamp'}` unless you want the value to keep going.
4. **Use Remotion's media components**, not the HTML ones: `<Img>`, `<Audio>`, `<OffthreadVideo>` (or
   `<Video>` from `@remotion/media`), not `<img>`, `<audio>`, `<video>`. They wait until the media has
   loaded before a frame is captured. A plain `<img>` can render empty.
5. **Local files via `staticFile()`**; never remote URLs (the sandbox has no network by default).
6. **Seconds to frames once, at the top**: `const sec = (s: number) => Math.round(s * fps)`. Do all
   timing in seconds in your head and convert.
7. **Wait for async work.** If a scene loads something asynchronously (a font, data), hold the
   frame with `const [handle] = useState(() => delayRender('loading x'))` and call
   `continueRender(handle)` when done. Always release the handle, including on error, or the render
   times out (default 30 s per frame).
8. **Layers**: stack full-frame layers with `AbsoluteFill`; later siblings paint on top. Use
   `zIndex` only when the order cannot be expressed by sibling order.
9. **Keep the frame cheap.** Heavy `box-shadow`, `filter: blur()` on large areas, huge SVGs and
   many layers slow every frame. Prefer a pre-rendered image for a complex background.

## Good-looking defaults

- Enter with a soft spring (`damping: 200`) or a 10 to 15 frame ease-out; exit faster than you enter.
- Stagger a group of items by 3 to 6 frames each rather than animating them all at once.
- Hold each message long enough to read it: about 0.3 s per word plus 0.5 s, at least 1.5 s.
- Move less than you think. A 20 to 40 px slide plus a fade reads as premium; large flying
  movements read as cheap.
- Keep one accent colour and two font weights. Contrast and spacing do more than effects.

## 3D

`three` and `@react-three/fiber` are pre-installed. Inside Remotion, three.js content must be
wrapped in `ThreeCanvas` from `@remotion/three` (same version, 4.0.545), because the stock R3F canvas
runs its own clock. If `@remotion/three` is not available, do not use 3D; use CSS transforms
(`perspective`, `rotateX/Y`) instead, driven by the frame.
