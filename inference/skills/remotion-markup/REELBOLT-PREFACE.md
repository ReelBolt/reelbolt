## ReelBolt preface to `remotion-markup` (written by ReelBolt, NOT part of the upstream skill)

Everything below the divider is the upstream Remotion skill exactly as vendored. It was written
for a developer with a network and a shell. ReelBolt's sandbox is different, so read these
corrections first; they win wherever the upstream text disagrees.

### What the sandbox is

- No network at all. Nothing can be downloaded or installed during a run: `InstallNpmPackages`
  only reports which packages are already installed, it cannot add one. The packages already in
  the template are: `@phosphor-icons/react`, `@react-spring/web`, `@react-three/drei`, `@react-three/fiber`, `@remotion/captions`, `@remotion/cli`, `@remotion/fonts`, `@remotion/gif`, `@remotion/layout-utils`, `@remotion/lottie`, `@remotion/media-utils`, `@remotion/motion-blur`, `@remotion/noise`, `@remotion/paths`, `@remotion/renderer`, `@remotion/shapes`, `@remotion/three`, `@remotion/transitions`, `@remotion/zod-types`, `d3`, `framer-motion`, `gsap`, `lottie-web`, `lucide-react`, `react`, `react-dom`, `remotion`, `simplex-noise`, `three`, `zod`. If a package is not in that list, choose another way (plain React,
  CSS, SVG, or one of the packages above); do not try to add it.
  For animated GIFs, Lottie, scene transitions and gsap, read the matching guide first (UseSkill remotion-markup: gifs.md, lottie.md, transitions.md; the gsap rules are in the skill's ReelBolt preface).
- Fonts are local: use an installed family for CSS `fontFamily` (the installed families listed in
  your instructions, if any). For any other family call `StageFont` (Google Fonts when enabled, or a project
  font file) and paste the `snippet` it returns (`loadFont` from `@remotion/fonts` with a local
  `staticFile(...)`) at module top level, outside any component; then use the plain family name in
  `fontFamily` (`loadFont` delays the render itself). A project file staged with `StageProjectFile`
  is loaded with the same snippet. Never import `@remotion/google-fonts` and never load a font from
  a remote URL.
- Blocks: if you have the `ListBlocks` tool, call it before building a transition, title or kinetic
  text, caption style, chart, map overlay, grain/CRT/glitch effect, background or device frame from
  scratch; `AddBlock` copies an editable, tested block into `src/blocks/`. Adapt it to the brief.
- Media is local: reference only files staged in the project (`staticFile(...)` or a staged
  path). Remote image, video or audio URLs do not load in the render.

### Upstream commands that do not apply

| Upstream says | Here |
|---|---|
| `npx remotion add <pkg>`, `npm i <pkg>`, `npm install` | Not available. Use the template packages above (and `ListBlocks`/`AddBlock` if you have them). |
| `npx create-video` | The project already exists from the template; edit `src/`. |
| `@remotion/google-fonts/<Family>`, `google-fonts.md` | Use an installed family, or `StageFont` and the `loadFont` snippet it returns (above). |
| Remote URLs for images, video, audio, fonts, data | Use files staged in the project. |
| `<Video>` from `@remotion/media`, or `<Video>`/`<Html5Video>` from `remotion` | Use `<OffthreadVideo>` from `remotion` for every clip. The HTML5 `<Video>` shows stale frames in a render (the clip steps back every few frames) and `RenderVideoAndUploadToStorage` refuses it. `<OffthreadVideo>` plays the clip's own audio unless `muted`; it keeps `trimBefore`/`trimAfter`, `playbackRate` and `volume`; for a loop wrap it in `<Loop durationInFrames={...}>`. |
| Running the CLI directly (`npx remotion render`, `studio`) | Use the sandbox tools (`RunSandboxNpmScript` `build`, `CheckLintAndTypeErrors`, `RenderVideoAndUploadToStorage`). |

Your step's own instructions, output schema and tool rules always win over this skill.

### In this skill

`google-fonts.md` is the upstream font recipe and does not apply; `local-fonts.md` applies only
with a font file you staged yourself or one of the installed families. Packages named in the
`3d`, `lottie`, `light-leaks` or similar pages are usable only if they are in the template list
above (`three`, `@react-three/fiber`, `@remotion/lottie`, `lottie-web` are); any other is
unavailable, so build the effect with what is installed.

## GSAP in Remotion (ReelBolt)

First-party ReelBolt guide (MIT), written from real renders in the sandbox. `gsap` 3.15.1 is installed in the template. It works in Remotion only in one form: **GSAP is driven by the frame, never by time.** Build the timeline paused, then seek it to `frame / fps` from `useCurrentFrame()` in a layout effect, so every frame is a pure function of the frame number. Remotion renders frames in separate tabs and out of order, so anything that runs on the wall clock gives different pixels on every render.

### Rules

- **Frame-driven rule:** create the timeline with `gsap.timeline({paused: true})` and call `tl.seek(frame / fps)` in a `useLayoutEffect` that runs on every render (no dependency array). Seeking forward, backward or to the same time always gives the same state.
- Use refs (or selectors scoped to a container ref) for targets. Wrap the setup in `gsap.context(() => {...}, containerRef)` and call `ctx.revert()` in the effect cleanup, so renders in parallel tabs and hot reloads do not leak tweens or inline styles.
- Never use real time: no un-paused tweens or timelines, no `requestAnimationFrame`, no `gsap.ticker`, no `delayedCall`, no `setTimeout`. A tween created outside a paused timeline starts playing on the real-time ticker; the negative control rendered it at two different positions in two renders.
- Never use `ScrollTrigger`, `ScrollSmoother`, `Observer`, `Draggable`, `ScrollToPlugin` or `GSDevTools`: scroll, pointer and dev-tool patterns are web only and have nothing to react to in a render.
- Avoid `repeat: -1` and `yoyo` loops. Prefer a finite timeline whose length you choose, or compute the looped time yourself (`seek((frame / fps) % loopSeconds)`). `seek` clamps at the end of a timeline (a finite timeline sought past its end stays at its final state), and an infinite repeat has no usable `duration()`.
- Callbacks are suppressed by `seek`: `onUpdate`, `onComplete` and `call()` do not fire unless you pass `false` as the second argument (`tl.seek(frame / fps, false)`). Only do that to format text or numbers inside `onUpdate`, derived from the sought value (for example `Math.round(counter.value).toLocaleString('en-US')`). Never put side effects in `onComplete`, never fetch, never keep state outside the timeline.
- `gsap.ticker.lagSmoothing(0)` is not needed: a paused timeline sought by the frame never reads the ticker, and the verified renders did not use it.
- Plugins must be imported from `gsap/<Name>` and registered once with `gsap.registerPlugin(...)` before use. Since gsap 3.13 all plugins ship free in the npm package. Present in 3.15.1 (checked in `node_modules/gsap`). Core and easing: `CSSPlugin`, `CSSRulePlugin`, `EasePack`, `CustomEase`, `CustomBounce`, `CustomWiggle`. Text and SVG: `SplitText`, `ScrambleTextPlugin`, `TextPlugin`, `DrawSVGPlugin`, `MorphSVGPlugin`. Motion: `MotionPathPlugin`, `MotionPathHelper`, `Flip`, `Physics2DPlugin`, `PhysicsPropsPlugin`, `InertiaPlugin`, `EaselPlugin`, `PixiPlugin`. Scroll, pointer and dev tools (web only, excluded by the rules): `ScrollTrigger`, `ScrollSmoother`, `ScrollToPlugin`, `Observer`, `Draggable`, `GSDevTools`. Useful in video and verified by rendering: `SplitText`, `DrawSVGPlugin`, `MorphSVGPlugin`, `MotionPathPlugin`, `ScrambleTextPlugin`, `CustomEase`, `TextPlugin`.
- `SplitText` rewrites the DOM: create it inside the `gsap.context`, animate `split.chars` / `split.words` / `split.lines`, and let `ctx.revert()` restore the original text. Rebuild the effect when the text, font size or font family change.

### Minimal example

```tsx
import React, {useLayoutEffect, useRef} from 'react';
import {AbsoluteFill, useCurrentFrame, useVideoConfig} from 'remotion';
import {gsap} from 'gsap';
import {SplitText} from 'gsap/SplitText';

gsap.registerPlugin(SplitText);

export const GsapTitle: React.FC<{text: string}> = ({text}) => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const containerRef = useRef<HTMLDivElement>(null);
  const titleRef = useRef<HTMLDivElement>(null);
  const tlRef = useRef<gsap.core.Timeline | null>(null);

  useLayoutEffect(() => {
    const ctx = gsap.context(() => {
      const split = SplitText.create(titleRef.current as HTMLElement, {type: 'chars,words'});
      const tl = gsap.timeline({paused: true});
      tl.from(split.chars, {yPercent: 110, opacity: 0, duration: 0.6, ease: 'power3.out', stagger: 0.03});
      tlRef.current = tl;
    }, containerRef);
    return () => {
      tlRef.current = null;
      ctx.revert();
    };
  }, [text]);

  useLayoutEffect(() => {
    tlRef.current?.seek(frame / fps);
  });

  return (
    <AbsoluteFill ref={containerRef} style={{background: '#0f172a', justifyContent: 'center', alignItems: 'center'}}>
      <div ref={titleRef} style={{fontSize: 120, fontWeight: 800, color: '#fff'}}>{text}</div>
    </AbsoluteFill>
  );
};
```

The block `gsap-kinetic-title` (`ListBlocks` / `AddBlock`) is this pattern with typed props.
