# Caption fonts — upstream sources

Fonts burned into captions by the workflow engine's libass renderer (see `docs/voiceover.md`
"Captions" and `CaptionFontCatalog` in
`src/ReelBolt.WorkflowEngine/Services/Video/AssSubtitleBuilder.cs`). Vendored rather than
downloaded at image build time so the build needs no network and the set never drifts. The
WorkflowEngine Dockerfile copies this directory to `/usr/share/fonts/reelbolt-captions`
(`VideoEditing:CaptionFontsDir`). `web/public/fonts/captions` carries WOFF2 subsets of the same
files (Latin-1 plus typographic quotes, made with fontTools' `pyftsubset --flavor=woff2`) for the
dashboard's font-picker samples, with the same licence files; none of these fonts declares a
Reserved Font Name, so the OFL permits the subsetting.

Every font here is licensed under the **SIL Open Font License 1.1**; each family's licence text
sits beside it (`OFL-<Family>.txt`). The OFL permits bundling and redistribution with software,
including commercial software, provided the licence travels with the fonts and the fonts are not
sold on their own.

| File | Family | Source | sha256 |
|------|--------|--------|--------|
| `Inter-Bold.ttf` | Inter | rsms/inter release v4.1, `Inter-4.1.zip` → `extras/ttf/Inter-Bold.ttf` | `288316099b1e0a47a4716d159098005eef7c0066921f34e3200393dbdb01947f` |
| `Montserrat-Bold.ttf` | Montserrat | github.com/JulietaUla/Montserrat @ `555facfb2a18c72c3c0380f0d9c0f060453a9058`, `fonts/ttf/` | `bc6e854971cea46b463be6f9eef4d9cd52f51cfc1fc0dd90c9d3e6483dc0ec61` |
| `Poppins-Bold.ttf` | Poppins | github.com/google/fonts @ `9710da1eacb3be272583c3224dcb70f9da6eadbb`, `ofl/poppins/` | `983676516167748b74de6f4771fb384c664fd913acb8b471122ecacf5da5ea6c` |
| `BebasNeue-Regular.ttf` | Bebas Neue | github.com/google/fonts @ `9710da1eacb3be272583c3224dcb70f9da6eadbb`, `ofl/bebasneue/` | `08e4623805102d819f58601e46e345648846075e363b2ceb23313c2d1c83ec73` |
| `Anton-Regular.ttf` | Anton | github.com/google/fonts @ `9710da1eacb3be272583c3224dcb70f9da6eadbb`, `ofl/anton/` | `a4ba3a92350ebb031da0cb47630ac49eb265082ca1bc0450442f4a83ab947cab` |

Fetched 2026-10-04. Only the one weight each caption style uses is vendored. Adding a font means
adding its file and licence here, a `CaptionFont` enum member (appended last), a
`CaptionFontCatalog` entry, and the web mirror — a font the image does not ship must never be
offered.
