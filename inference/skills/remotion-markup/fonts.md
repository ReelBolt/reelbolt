# Fonts

The sandbox has **no internet by default**. Fonts must already be on the machine. A font that is
loaded from a URL at render time cannot be fetched, the render fails (the platform refuses such a
project up front with `NETWORK_FONT`).

Do **not**:

- import `@remotion/google-fonts` (it downloads at load time),
- use `@import url(...)` or `<link href=...>` for a font host,
- use `@font-face` with a remote `src: url(https://...)`.

## Use an installed font by name

Reference the family in CSS. Installed in the sandbox image: Inter, Montserrat, Poppins, DM Sans,
Space Grotesk, Chakra Petch, Bebas Neue, Anton, Playfair Display, DM Serif Display, JetBrains Mono,
Liberation Sans/Serif/Mono, and Noto Color Emoji.

```tsx
<h1 style={{fontFamily: '"Inter", "Liberation Sans", sans-serif', fontWeight: 700}}>Title</h1>
```

Always add a generic fallback at the end of the list.

| Need | Try |
|---|---|
| Clean modern UI / SaaS | Inter, DM Sans |
| Friendly and geometric | Poppins, Montserrat |
| Tech / dev tool | Space Grotesk, JetBrains Mono (for code) |
| Big punchy headlines | Anton, Bebas Neue (uppercase, large sizes only) |
| Editorial / premium | Playfair Display, DM Serif Display (headlines) |

Use at most two families: one for headlines, one for body. Weights 400 to 700 only.

## A brand font supplied by the user

If the project contains a `.ttf`, `.otf` or `.woff2` file, stage it into `public/fonts/` and load it in code through `staticFile()` (a plain absolute URL breaks when the bundle is served from
a sub-path), holding the frame until it is ready:

```tsx
const font = new FontFace('Brand', `url(${staticFile('fonts/Brand.woff2')})`);
const [handle] = useState(() => delayRender('font'));
useEffect(() => {
  font.load().then(() => { document.fonts.add(font); continueRender(handle); }).catch((e) => cancelRender(e));
}, [handle]);
```

Only use fonts you have the right to use; do not bundle a font from an unknown source.

## Emoji

Noto Color Emoji is installed, so emoji render. Their size follows `fontSize`. Avoid emoji in
brand-sensitive copy.
