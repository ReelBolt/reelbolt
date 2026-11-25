---
name: remotion-create
description: Set up a Remotion project or add a composition to it, with sizes and layout rules for video
version: 4.0.535
---

How a Remotion project is put together, and how to add a new composition to the one you already
have. In ReelBolt's sandbox the project already exists (see "The sandbox project" below): extend it,
do not start a new one.

## The pieces

A Remotion video is a React component that is a pure function of the current frame number. There
are four pieces:

1. **Entry file** (`src/index.ts`) calls `registerRoot(Root)` once. Leave it alone.
2. **Root component** (`src/root.tsx`) returns one `<Composition>` per renderable video, and one
   `<Still>` per renderable single image.
3. **Composition component** reads the frame with `useCurrentFrame()` and the video settings with
   `useVideoConfig()` and draws everything from those two values.
4. **Render** (CLI or API) loads a composition by its `id` and writes the file.

```tsx
import {Composition} from 'remotion';
import {Promo, type PromoProps} from './Promo.tsx';

export const Root: React.FC = () => (
  <>
    <Composition
      id="Promo"                 // unique, letters/digits/hyphens only
      component={Promo}
      width={1920}
      height={1080}
      fps={30}
      durationInFrames={30 * 20} // 20 seconds
      defaultProps={{title: 'Hello'} satisfies PromoProps}
    />
  </>
);
```

Rules that cause most first-run failures:

- `id` must be unique and may contain only letters, digits and `-`. It is what you pass to the render
  command.
- `width`, `height`, `fps` and `durationInFrames` must be positive integers. Convert seconds with
  `Math.round(seconds * fps)`.
- `defaultProps` must be plain JSON-serialisable data, and must match the component's props type.
- Use explicit `.tsx` / `.ts` extensions in relative imports when the project's tsconfig asks for it.
- Never read `Date.now()`, `Math.random()` or wall-clock time while drawing. A frame must look the
  same every time it is rendered. Use `random('seed')` from `remotion` for repeatable randomness.
- No CSS transitions, CSS keyframe animations or `setInterval`. They run on real time, not on the
  frame, so renders come out wrong. Drive every animation from `useCurrentFrame()`.

## A minimal composition

```tsx
import {AbsoluteFill, interpolate, useCurrentFrame, useVideoConfig} from 'remotion';

export type PromoProps = {title: string};

export const Promo: React.FC<PromoProps> = ({title}) => {
  const frame = useCurrentFrame();
  const {fps, durationInFrames} = useVideoConfig();
  const opacity = interpolate(frame, [0, fps / 2], [0, 1], {extrapolateRight: 'clamp'});
  return (
    <AbsoluteFill style={{backgroundColor: '#0b0b10', justifyContent: 'center', alignItems: 'center'}}>
      <h1 style={{color: 'white', fontSize: 120, opacity}}>{title}</h1>
    </AbsoluteFill>
  );
};
```

`AbsoluteFill` is a full-size, absolutely positioned flex container. Use it for every full-frame
layer; stack layers in document order (later children are on top).

## Stills

A `<Still id="Thumb" component={Thumb} width={1280} height={720} />` has no fps or duration and is
rendered with `remotion still`. Use a Still for thumbnails, posters, lower-thirds on a transparent
background and any other single image. See the `remotion-render` skill for the command.

## Static files

Files in `public/` are served by `staticFile('name.ext')`:

```tsx
import {Img, staticFile} from 'remotion';
<Img src={staticFile('logo.png')} style={{width: 240}} />
```

Always go through `staticFile()` for local files. A bare `"/logo.png"` or a relative path breaks as
soon as the bundle is served from a sub-path.

## The sandbox project

- It is pre-installed. Installed Remotion packages are all pinned to **4.0.545**; every extra
  `@remotion/*` package you add must be the same exact version (`@remotion/transitions@4.0.545`),
  or the render fails on a version mismatch.
- Pre-installed: the template catalog in `sandbox/template/package-catalog.json` (`remotion`,
  `@remotion/*` helpers, React 19, `three` with react-three-fiber, `d3`, `framer-motion`, `gsap`,
  `@react-spring/web`, icon sets). `@remotion/google-fonts` is not installed: fonts come
  from the installed families or `StageFont`. See `remotion-markup`, `fonts.md`.
- Do not edit `src/index.ts`. Register new compositions in `src/root.tsx`, and put each scene in
  its own file.
- Keep the licence comment at the top of `src/root.tsx` ("Built with Remotion ...") in place. It is
  part of how the project is licensed.
- By default the sandbox has no internet. Anything a scene needs (images, audio, fonts) must already
  be a project file, a staged file under `public/`, or a font installed on the machine. Never
  reference a remote image, audio, font or script URL.

See `video-layout.md` for sizes, safe areas and how to structure scenes.
