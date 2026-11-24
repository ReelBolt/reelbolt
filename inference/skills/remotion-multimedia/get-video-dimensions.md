# Video and image dimensions

## Video

```ts
import {Input, ALL_FORMATS, UrlSource} from 'mediabunny';
import {staticFile} from 'remotion';

export async function videoSize(file: string) {
  const input = new Input({formats: ALL_FORMATS, source: new UrlSource(staticFile(file))});
  const track = await input.getPrimaryVideoTrack();
  if (!track) throw new Error(`${file} has no video track`);
  return {width: track.displayWidth, height: track.displayHeight};
}
```

Use `displayWidth` / `displayHeight`, not the coded size: they already account for rotation
metadata (a phone clip recorded upright is often stored sideways and flagged as rotated) and
pixel aspect ratio. Using the coded size gives a sideways or stretched picture.

## Image

For an image you only need the size when it must drive layout. In a component the loaded `<Img>`
can report it (`onLoad` event, `event.currentTarget.naturalWidth`), but that is asynchronous. For
anything that sizes the composition, probe in `calculateMetadata`, or use
`getImageDimensions(src)` from `@remotion/media-utils` when installed.

## Fitting footage into the frame

```tsx
const {width: W, height: H} = useVideoConfig();
const scale = Math.max(W / clipW, H / clipH);   // cover; use Math.min for contain
<OffthreadVideo src={...} style={{width: clipW * scale, height: clipH * scale, position: 'absolute',
  left: (W - clipW * scale) / 2, top: (H - clipH * scale) / 2}} />
```

or just give it `width: '100%'`, `height: '100%'` and `objectFit: 'cover' | 'contain'` when it
should fill the frame.

## Matching the output to a clip

```tsx
calculateMetadata={async ({props}) => {
  const {width, height} = await videoSize(props.clip);
  return {width, height};
}}
```

Keep the result even (`width - (width % 2)`): h264 needs even dimensions.

## Aspect-ratio decisions

| Source | Output | Do |
|---|---|---|
| 16:9 | 16:9 | place at 100% |
| 16:9 | 9:16 | `cover` crops the sides; choose the crop centre on purpose, or letterbox on a blurred copy |
| 9:16 | 16:9 | pillarbox; fill the sides with a blurred, scaled-up copy of the same clip |
