# Brand font previews

Self-hosted Latin WOFF2 subsets of the SIL OFL 1.1 families pinned in `sandbox/fonts/` (pins and
upstream commit: `sandbox/fonts/UPSTREAM.md`). They are loaded lazily by `web/lib/design-system/brand-fonts.ts`,
only when a font preview is shown (Brand & product tab), never from a third-party CDN. The licence text of
each family sits beside the files as `OFL-<Family>.txt` (unmodified copy from the same upstream commit).

The fonts are subset and re-encoded, not otherwise modified. The CSS aliases are neutral
(`ReelBolt Brand Font 1` .. `11`), so no Reserved Font Name is used for a modified font; the mapping is
`ENTRIES` order in `brand-fonts.ts`.

## Reproduce

With `pip install fonttools brotli`, for each row below (variable fonts keep all their axes):

```bash
python3 -m fontTools.subset "sandbox/fonts/<source>" \
  --unicodes="U+0000-00FF,U+0131,U+0152-0153,U+02BB-02BC,U+02C6,U+02DA,U+02DC,U+2000-206F,U+2074,U+20AC,U+2122,U+2191,U+2193,U+2212,U+2215,U+FEFF,U+FFFD" \
  --flavor=woff2 --layout-features='*' --output-file=web/public/fonts/brand/<file>
```

The unicode set is the Google Fonts `latin` range. Rebuilding with fonttools 4.67.0 reproduces every sha256 below byte for byte.

| File | Source TTF | sha256 of the WOFF2 |
|---|---|---|
| `Inter-Variable.woff2` | `sandbox/fonts/Inter/Inter[opsz,wght].ttf` | `1fad881334fdef56d483d1f1fd71c9b3b31eb66a9a5dc1eec285ee08fb0184f7` |
| `Montserrat-Variable.woff2` | `sandbox/fonts/Montserrat/Montserrat[wght].ttf` | `1109e53cc5a57ef39ac0fc610031bb5d38a7282be39f1dd3eec451ec1553e848` |
| `DMSans-Variable.woff2` | `sandbox/fonts/DM Sans/DMSans[opsz,wght].ttf` | `40f4c709608cd8ae8a9cb2a66aeae766d2ea95ebb96371ff6a7801a5df452a10` |
| `SpaceGrotesk-Variable.woff2` | `sandbox/fonts/Space Grotesk/SpaceGrotesk[wght].ttf` | `f0deb20a581a861869e730d60c91484ce05d21bcc06940d2b463078dc57a644b` |
| `JetBrainsMono-Variable.woff2` | `sandbox/fonts/JetBrains Mono/JetBrainsMono[wght].ttf` | `1ddace4b01938fc8f4581b4db5201964fedf003b94da9115e11de8f6ae3dda99` |
| `PlayfairDisplay-Variable.woff2` | `sandbox/fonts/Playfair Display/PlayfairDisplay[wght].ttf` | `6e1b414b4d26f2c04539726073eee25d9df66a8b2fed0894eb40f6ba09b43546` |
| `Poppins-Regular.woff2` | `sandbox/fonts/Poppins/Poppins-Regular.ttf` | `19800878b91bd10df9e927b3886a4d48c0dc93a3998201f591f7b59a3e2e6511` |
| `Poppins-SemiBold.woff2` | `sandbox/fonts/Poppins/Poppins-SemiBold.ttf` | `fc000a72c3fd8cf01d91521c546e3adb2fd0e0b1ce523dfab2b4f4ba76bdcabe` |
| `Poppins-Bold.woff2` | `sandbox/fonts/Poppins/Poppins-Bold.ttf` | `038f854d65d9527484d8f1ebd54c24303fcff8c392300bc5590a2cfcd1868ab6` |
| `ChakraPetch-Regular.woff2` | `sandbox/fonts/Chakra Petch/ChakraPetch-Regular.ttf` | `d5129e35d04aca5d6bfcba11ed6e2d2a5f32133c994ef84fa6191fd72fe7e7c8` |
| `ChakraPetch-SemiBold.woff2` | `sandbox/fonts/Chakra Petch/ChakraPetch-SemiBold.ttf` | `c3021b9b11dd4f580be4fb6342ab76e20703e2f9124eb81dd6e711c073b1e135` |
| `ChakraPetch-Bold.woff2` | `sandbox/fonts/Chakra Petch/ChakraPetch-Bold.ttf` | `79157ec26853ae000f92c5140d138e13e51119ce8795849f4bed1ed2d34864e4` |
| `Anton-Regular.woff2` | `sandbox/fonts/Anton/Anton-Regular.ttf` | `5265b881af83db99a69da16efb0b3558a383b3aa8dd7ae8d3b451883f1ea8844` |
| `BebasNeue-Regular.woff2` | `sandbox/fonts/Bebas Neue/BebasNeue-Regular.ttf` | `32f67dcc9d1e3e2af78db01fff056e1c9024f17ad1b4e8aec26ff27be1b7ef0f` |
| `DMSerifDisplay-Regular.woff2` | `sandbox/fonts/DM Serif Display/DMSerifDisplay-Regular.ttf` | `ccdfc4f5bb4d2345742d6ac2f5cecab97259d7036706196d65277f8498c342bd` |
