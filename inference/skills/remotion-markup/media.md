# Images, video and other media

## Images

```tsx
import {Img, staticFile} from 'remotion';
<Img src={staticFile('screens/dashboard.png')} style={{width: '100%', height: '100%', objectFit: 'cover'}} />
```

- Always `<Img>` (or `AbsoluteFill` with a CSS `backgroundImage` of a `staticFile` URL for decor).
  `<Img>` blocks the frame until the image has decoded.
- `objectFit: 'cover'` fills and crops, `'contain'` fits and letterboxes. Pick on purpose.
- Slow push-in on a still: `transform: scale(${interpolate(frame, [0, durationInFrames], [1, 1.08])})`.
- Screenshots of a product: add rounded corners (12 to 24 px) and a soft shadow, and animate in
  with a small scale-up and a fade.

## Video clips

```tsx
import {OffthreadVideo, staticFile} from 'remotion';
<OffthreadVideo src={staticFile('clips/demo.mp4')} muted style={{width: '100%', height: '100%', objectFit: 'cover'}} />
```

- `OffthreadVideo` extracts frames outside the browser and is the reliable choice for rendering.
  `@remotion/media` also provides a `Video` component when it is installed.
- `trimBefore` / `trimAfter` (frames at composition fps) select the part of the clip to show;
  `playbackRate` changes speed; `muted` drops its audio.
- Put a clip inside a `Sequence` and give it `premountFor` so it is ready on its first frame.
- The clip's own length decides how long a scene can run: do not ask for frames after the clip ends.
  Hold the last frame with `<Freeze>` if you need longer.

## Pictures as backgrounds

A flat colour or a gradient is the cheapest background:
`background: 'linear-gradient(135deg, #0f0c29, #302b63)'`. A gradient rendered in CSS is crisp at
every size. For a texture, use a pre-made image.

## SVG and shapes

Inline SVG is fine and scales perfectly. Animate `stroke-dashoffset` with `interpolate` to draw a
line on: set `strokeDasharray={length}` and `strokeDashoffset={length * (1 - progress)}`. Animate
paths with `d` computed from the frame, never with SMIL or CSS animation.

## GIFs and animated images

Do not use `<img src="x.gif">`: it animates in real time. Use Remotion's `AnimatedImage` (from
`remotion`), which seeks to the frame being rendered, or convert the GIF to video first.

## Lottie

Lottie animations need `@remotion/lottie` at the project's version. If it is not installed, rebuild
the idea with SVG and `interpolate`.

## Do not

- Do not reference remote images or videos. There is no network.
- Do not use `<video>` or `<img>` HTML tags for content that appears in the render.
- Do not scale a low-resolution image up past 1.5x; it will look soft in the final video.
