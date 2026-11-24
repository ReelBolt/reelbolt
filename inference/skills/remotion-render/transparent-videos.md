# Transparent output (alpha channel)

Use this for lower-thirds, titles, badges, callouts and other graphics that are laid over footage
later. The graphic is drawn on a transparent background and exported with its alpha channel.

## 1. Draw on nothing

- Do not paint any background on the composition: no background colour on `AbsoluteFill`, `body`
  or the root div. Leave it empty so the pixels are transparent.
- Make the composition's size the size of the graphic's layer (often the full frame, 1920 x 1080,
  with the graphic positioned inside it) so it can be dropped on the footage at 1:1.
- Soft shadows and blurs are fine; they become partial alpha.

## 2. Pick the right export

**Single image: Still as PNG**

```bash
npx remotion still src/index.ts LowerThird out/lower-third.png --image-format=png
```

PNG keeps alpha. JPEG does not: never use it for a transparent graphic.

**Animated: VP9 WebM** (small, plays in browsers and many editors)

```bash
npx remotion render src/index.ts LowerThird out/lower-third.webm \
  --codec=vp9 --pixel-format=yuva420p --image-format=png
```

**Animated: ProRes 4444** (large, best for professional editors)

```bash
npx remotion render src/index.ts LowerThird out/lower-third.mov \
  --codec=prores --prores-profile=4444 --pixel-format=yuva444p10le --image-format=png
```

Rules that matter:

- `--image-format=png` is **required**; Remotion captures frames as JPEG by default and JPEG cannot
  hold transparency.
- The pixel format must carry alpha (`yuva420p` for vp8/vp9, `yuva444p10le` for ProRes 4444). h264
  and h265 (`.mp4`) cannot store alpha at all.
- `--pixel-format=yuva420p` is accepted only with `vp8` or `vp9`; any other pair is an error.
- `--prores-profile` is valid only with `--codec=prores`. Values: `proxy`, `light`, `standard`,
  `hq`, `4444`, `4444-xq`.

## 3. Check it

Open the result over a checkerboard or a contrasting solid colour: edges that look like a halo are
usually text with a hard-coded dark or light shadow baked in. A still is the quickest check; render
the animation once the still is right.

## 4. Use it

Treat the output as an overlay asset: it is placed over the footage by a later compile step at an
agreed time and position. Name it for what it is (`title-card.webm`), keep it short (a few seconds),
and do not bake the footage, a background or a drop-shadow-on-black into it.

## Common failures

| Symptom | Cause |
|---|---|
| Black background where it should be clear | exported as h264/mp4, or `--image-format` left on JPEG |
| White/black fringes | a background colour or `mix-blend-mode` on the root; remove it |
| Error about pixel format and codec | alpha pixel format with a codec that cannot carry it |
| Overlay too heavy | switch from ProRes to VP9, shorten it, lower `--scale` |
