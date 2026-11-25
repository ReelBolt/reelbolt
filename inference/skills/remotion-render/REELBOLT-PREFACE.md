## ReelBolt preface to `remotion-render` (written by ReelBolt, NOT part of the upstream skill)

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

Rendering is done by ReelBolt, not by you on a shell. `npx remotion render`, `still`, `lambda` and
cloud-render pages do not apply; build with `RunSandboxNpmScript` `build`, then call
`RenderVideoAndUploadToStorage`.
