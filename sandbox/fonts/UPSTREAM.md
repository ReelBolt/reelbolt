# Sandbox font set — upstream sources

Fonts installed into the sandbox-runtime image (`sandbox/Dockerfile`, stage `sandbox-runtime`) at
`/usr/share/fonts/truetype/reelbolt/`, so Remotion renders resolve them with local `font-family`
names and never fetch fonts from the network at render time. The image keeps `fonts-liberation`
(Liberation Sans/Serif/Mono) and `fonts-noto-color-emoji` alongside this set. The manifest is
`fonts.json` (family, category, relative path, sha256 per file); the C# side reads the family list
from the same definition (`ReelBolt.Shared.DesignSystems.SandboxFonts`).

Repository: https://github.com/google/fonts
Pinned commit: `47d54ca1ebb14fd864695f1d39dc734f39017c33` (`HEAD` of `main` at fetch time)
Upstream path: `ofl/<directory>/` per family (directory names lowercase, no spaces)
Fetched: 2026-10-17 via `https://raw.githubusercontent.com/google/fonts/47d54ca1ebb14fd864695f1d39dc734f39017c33/ofl/<directory>/<file>`

Licence: every font here is licensed under the **SIL Open Font License 1.1**. Each family's licence
text sits beside its files as `OFL.txt` (unmodified copy from the same commit). The OFL permits
bundling and redistribution with software, including commercial software, provided the licence
travels with the fonts and the fonts are not sold on their own. Files are unmodified (no renaming
of Reserved Font Names, no subsetting). `Playfair Display` and `DM Serif Display` declare Reserved
Font Names; they are shipped unmodified under those names, as the OFL requires.

Variable fonts are preferred where upstream ships one (it covers all weights), together with its Italic
variable file. Static-only families ship Regular, SemiBold and Bold plus Italic and Bold Italic where it exists.
The set (41 families, 85 files, about 24 MB; the previous 11 families were 3.6 MB) is produced by
`sandbox/scripts/generate-fonts.py` from a checkout of the pinned commit; it also writes `fonts.json` and the
Google Fonts catalog `StageFont` uses (`inference/src/ReelBolt.Shared/DesignSystems/Data/google-fonts-catalog.json`).

| Family (`category`) | Files (path under `sandbox/fonts/`) | Upstream directory |
|---|---|---|
| Inter (sans) | `Inter-Italic[opsz,wght].ttf`, `Inter[opsz,wght].ttf` | `ofl/inter/` |
| Montserrat (sans) | `Montserrat-Italic[wght].ttf`, `Montserrat[wght].ttf` | `ofl/montserrat/` |
| Poppins (sans) | `Poppins-Bold.ttf`, `Poppins-BoldItalic.ttf`, `Poppins-Italic.ttf`, `Poppins-Regular.ttf`, `Poppins-SemiBold.ttf` | `ofl/poppins/` |
| DM Sans (sans) | `DMSans-Italic[opsz,wght].ttf`, `DMSans[opsz,wght].ttf` | `ofl/dmsans/` |
| Space Grotesk (sans) | `SpaceGrotesk[wght].ttf` | `ofl/spacegrotesk/` |
| Chakra Petch (sans) | `ChakraPetch-Bold.ttf`, `ChakraPetch-BoldItalic.ttf`, `ChakraPetch-Italic.ttf`, `ChakraPetch-Regular.ttf`, `ChakraPetch-SemiBold.ttf` | `ofl/chakrapetch/` |
| Bebas Neue (display) | `BebasNeue-Regular.ttf` | `ofl/bebasneue/` |
| Anton (display) | `Anton-Regular.ttf` | `ofl/anton/` |
| Playfair Display (serif) | `PlayfairDisplay-Italic[wght].ttf`, `PlayfairDisplay[wght].ttf` | `ofl/playfairdisplay/` |
| DM Serif Display (serif) | `DMSerifDisplay-Italic.ttf`, `DMSerifDisplay-Regular.ttf` | `ofl/dmserifdisplay/` |
| JetBrains Mono (mono) | `JetBrainsMono-Italic[wght].ttf`, `JetBrainsMono[wght].ttf` | `ofl/jetbrainsmono/` |
| Roboto (sans) | `Roboto-Italic[wdth,wght].ttf`, `Roboto[wdth,wght].ttf` | `ofl/roboto/` |
| Open Sans (sans) | `OpenSans-Italic[wdth,wght].ttf`, `OpenSans[wdth,wght].ttf` | `ofl/opensans/` |
| Oswald (sans) | `Oswald[wght].ttf` | `ofl/oswald/` |
| Raleway (sans) | `Raleway-Italic[wght].ttf`, `Raleway[wght].ttf` | `ofl/raleway/` |
| Source Sans 3 (sans) | `SourceSans3-Italic[wght].ttf`, `SourceSans3[wght].ttf` | `ofl/sourcesans3/` |
| Nunito (sans) | `Nunito-Italic[wght].ttf`, `Nunito[wght].ttf` | `ofl/nunito/` |
| Work Sans (sans) | `WorkSans-Italic[wght].ttf`, `WorkSans[wght].ttf` | `ofl/worksans/` |
| Rubik (sans) | `Rubik-Italic[wght].ttf`, `Rubik[wght].ttf` | `ofl/rubik/` |
| Barlow (sans) | `Barlow-Bold.ttf`, `Barlow-BoldItalic.ttf`, `Barlow-Italic.ttf`, `Barlow-Regular.ttf`, `Barlow-SemiBold.ttf` | `ofl/barlow/` |
| Barlow Condensed (sans) | `BarlowCondensed-Bold.ttf`, `BarlowCondensed-BoldItalic.ttf`, `BarlowCondensed-Italic.ttf`, `BarlowCondensed-Regular.ttf`, `BarlowCondensed-SemiBold.ttf` | `ofl/barlowcondensed/` |
| Archivo (sans) | `Archivo-Italic[wdth,wght].ttf`, `Archivo[wdth,wght].ttf` | `ofl/archivo/` |
| Manrope (sans) | `Manrope[wght].ttf` | `ofl/manrope/` |
| Outfit (sans) | `Outfit[wght].ttf` | `ofl/outfit/` |
| Plus Jakarta Sans (sans) | `PlusJakartaSans-Italic[wght].ttf`, `PlusJakartaSans[wght].ttf` | `ofl/plusjakartasans/` |
| IBM Plex Sans (sans) | `IBMPlexSans-Italic[wdth,wght].ttf`, `IBMPlexSans[wdth,wght].ttf` | `ofl/ibmplexsans/` |
| Lora (serif) | `Lora-Italic[wght].ttf`, `Lora[wght].ttf` | `ofl/lora/` |
| Libre Baskerville (serif) | `LibreBaskerville-Italic[wght].ttf`, `LibreBaskerville[wght].ttf` | `ofl/librebaskerville/` |
| Cormorant Garamond (serif) | `CormorantGaramond-Italic[wght].ttf`, `CormorantGaramond[wght].ttf` | `ofl/cormorantgaramond/` |
| EB Garamond (serif) | `EBGaramond-Italic[wght].ttf`, `EBGaramond[wght].ttf` | `ofl/ebgaramond/` |
| IBM Plex Mono (mono) | `IBMPlexMono-Bold.ttf`, `IBMPlexMono-BoldItalic.ttf`, `IBMPlexMono-Italic.ttf`, `IBMPlexMono-Regular.ttf`, `IBMPlexMono-SemiBold.ttf` | `ofl/ibmplexmono/` |
| Fira Code (mono) | `FiraCode[wght].ttf` | `ofl/firacode/` |
| Roboto Mono (mono) | `RobotoMono-Italic[wght].ttf`, `RobotoMono[wght].ttf` | `ofl/robotomono/` |
| Space Mono (mono) | `SpaceMono-Bold.ttf`, `SpaceMono-BoldItalic.ttf`, `SpaceMono-Italic.ttf`, `SpaceMono-Regular.ttf` | `ofl/spacemono/` |
| Abril Fatface (display) | `AbrilFatface-Regular.ttf` | `ofl/abrilfatface/` |
| Lobster (display) | `Lobster-Regular.ttf` | `ofl/lobster/` |
| Pacifico (display) | `Pacifico-Regular.ttf` | `ofl/pacifico/` |
| Caveat (display) | `Caveat[wght].ttf` | `ofl/caveat/` |
| Dancing Script (display) | `DancingScript[wght].ttf` | `ofl/dancingscript/` |
| Archivo Black (display) | `ArchivoBlack-Regular.ttf` | `ofl/archivoblack/` |
| Alfa Slab One (display) | `AlfaSlabOne-Regular.ttf` | `ofl/alfaslabone/` |

Each file's sha256 is recorded in `fonts.json`. Adding or removing a family means changing this
directory, `fonts.json`, the table above, and the C# `SandboxFonts.Families` list together; the
consistency test fails if they drift.
