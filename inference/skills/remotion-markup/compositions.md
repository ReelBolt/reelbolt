# Compositions, props and dynamic length

## Several compositions

One `Root` can register many compositions (different formats, different cuts). Share components;
only the `<Composition>` settings differ. Group them in a `<Folder name="...">` to keep the Studio
sidebar tidy.

```tsx
<Folder name="Promo">
  <Composition id="Promo-16x9" component={Promo} width={1920} height={1080} fps={30} durationInFrames={600} defaultProps={props} />
  <Composition id="Promo-9x16" component={Promo} width={1080} height={1920} fps={30} durationInFrames={600} defaultProps={props} />
</Folder>
```

Lay out from `useVideoConfig().width/height` so the same component works in both.

## Props

`defaultProps` supply the values for preview and render. They can be overridden at render time with
`--props='{"title":"Other"}'` (inline JSON) or `--props=./props.json`. Inside the component, read
overrides from the props; if you need them outside a component use `getInputProps()`.

Keep props to data: strings, numbers, booleans, arrays, plain objects. No functions, no class
instances, no `Date`.

## calculateMetadata: length from data

When the length depends on data (narration lengths, the number of items), compute it before
rendering instead of hard-coding:

```tsx
<Composition
  id="Promo"
  component={Promo}
  width={1920}
  height={1080}
  fps={30}
  durationInFrames={1}              // placeholder, replaced below
  defaultProps={{scenes: [{frames: 120}, {frames: 150}]}}
  calculateMetadata={async ({props}) => ({
    durationInFrames: props.scenes.reduce((n, s) => n + s.frames, 0),
  })}
/>
```

`calculateMetadata` may also return `fps`, `width`, `height` and new `props`. It may be async (for
example to read the duration of a staged audio file, see `remotion-multimedia`). It runs once per
render, outside the frame loop, so it is the right place for any probing.

## Stills

`<Still id="Thumbnail" component={Thumbnail} width={1280} height={720} />`: one frame, no duration.
Render with `remotion still`. See `remotion-render`.

## Typical build errors and their causes

| Symptom | Likely cause |
|---|---|
| Composition rejected at start-up | `id` contains a space, underscore or symbol; use letters, digits, hyphens |
| Duration rejected | `durationInFrames` is not a positive integer; wrap in `Math.round` / `Math.ceil` |
| Two compositions clash | duplicate `id` |
| `Cannot find module` | missing explicit extension in a relative import, or a package that is not installed |
| Render times out on one frame | an unreleased `delayRender` handle, or an asset that cannot be fetched (remote URL) |
