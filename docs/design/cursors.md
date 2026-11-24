# Custom cursors

Status: **applied across the dashboard and the marketing site (Phase 3, 2026-10-30)**. The artwork
and the contact sheet shipped in the proposal; the owner then redrew the final shapes himself
(`scrub` and `pan` were rebuilt, and the `pan`, `pointer` and `scrub` hotspots moved onto the new
points of action), and Phase 3 wired the set into `web/` and `site/`. Audience: developers.
(`docs/design/**` is excluded from the documentation site and from the assistant's documentation
search.) Before/after sheets: [`before-after.png`](cursors/before-after.png) (real 32 px on the
three backgrounds) and [`before-after-detail.png`](cursors/before-after-detail.png) (4× detail for
the most-changed nine).

## Why

ReelBolt's pointer is the OS arrow everywhere, and the editor's ad-hoc `cursor:` rules are the
browser's stock set (`col-resize`, `grab`, `nwse-resize`, …). The result is inconsistent — the
dashboard speaks violet everywhere except under the mouse — and several of those stock cursors read
poorly on the editor's dark surfaces. This initiative draws one set that belongs to the brand, ships
it as swappable masters, and lets the owner judge it on a contact sheet before anything is wired.

The proposal contains, in one PR:

1. the masters, `/brand/cursors/**` (SVG sources + rendered PNG `@1x`/`@2x`);
2. `brand/cursors/cursors.json`, the contract (hotspot, fallback, intent) the apps consume;
3. the contact sheet at `/app/cursors` (unlinked, `noindex`);
4. this document, including the surface→cursor mapping the apply PR will implement.

Nothing here changes a live cursor, a hit area, a pointer-event handler or a drag behaviour. Cursors
are presentation.

## Shape language

The mark (`/brand/mark.svg`) is a bold rounded R with a lightning bolt across its lower right leg:
lavender R `#C4B5FD`, bolt `#A855F7`, deep ground `#2E1065`. A 32 px cursor cannot carry the mark —
shrunk to that size the R and bolt become a blob, which `docs/brand.md` forbids. So a cursor
**quotes** the mark's geometry instead of reproducing it:

- **Rounded and slightly angled.** Every join is rounded (`stroke-linejoin="round"`); the directional
  cursors and the tool shapes are laid on a slight diagonal rather than a stiff axis where that
  suits them (the arrow, the blade, the brush, the dropper, the diagonal resizes), matching the
  bolt's lean. The symmetric shapes (`move`, the axis resizes, `text`, `not-allowed`, `progress`,
  `crosshair`) stay balanced on purpose.
- **One angular gesture per shape.** The arrow's tail, the blade's edge, the cut line of `ripple`,
  the fist's knuckles: the pointed, faceted gesture is where the bolt's character lives. Some
  symmetric shapes carry no notch; they carry the family through weight, rim and palette.
- **Violet at the point of action, paper everywhere else.** The body of a filled cursor is paper
  `#F5F3F8` with a deep `#2E1065` rim; the accent that marks *where the action happens* is violet
  `#A855F7` (and lavender `#C4B5FD` for a secondary facet). Line cursors are drawn twice — a 5 px
  deep rim under a 2.1 px paper core — so the same shape reads on light and dark.
- **No neon, no glow, no drop shadow.** A cast shadow blurs to mush at 32 px, so none is used; the
  rim is the only separation device.

### The two the owner flagged, as drawn now

The first pass read ambiguously at 32 px; the owner redrew both, and these shapes are canonical.

- **`scrub` is a playhead, not a map pin.** A flat-topped pentagon head sits on a full-height
  vertical line, with a drag chevron either side low on the line. The pentagon is what separates it
  from the old pin-shaped reading; the full-height line says "this is the time position", and the
  chevrons say "drag sideways". The hotspot `(16, 16)` is on the line at the neck of the head.
- **`pan` is an open hand, not a compass.** The open palm sits inside four violet arrowheads that
  state "move in any direction"; the violet glyph that used to sit in the palm (and read as a
  sparkle next to `grab`) is gone. The hotspot `(16, 16)` is the palm centre, the point where the
  content is held.

### Contrast rim

A cursor cannot know what is behind it. Every shape therefore carries a rim that contrasts with both
light and dark: filled shapes are paper inside a deep outline; line shapes are a deep outline around
a paper core. Measured on the three required backgrounds:

| Background | What carries the shape |
|---|---|
| White `#FFFFFF` | the deep `#2E1065` rim (15.2:1 vs white) |
| Deep `#2E1065` | the paper `#F5F3F8` core (13.8:1 vs deep) |
| Mid grey `#808080` | the deep rim (3.86:1) and the paper core (3.58:1) |

On mid-grey the violet accent is ≈ 1:1 against the ground, so it is **decorative there**: the paper
body and the deep rim carry the shape and its outline (both above the 3:1 bar for non-text
graphics). The one place the accent also marks geometry — the `ripple` cut line — is drawn as a
violet core inside a deep rim so the line survives grey.

## The contract

- **32 × 32 px visual maximum**, shipped as `@1x` 32 px and `@2x` 64 px PNG. PNG, not SVG: SVG
  cursors are not reliably supported across browsers. No `.cur`, no animation.
- **Hotspot in 32 px space**, declared per cursor in `cursors.json` and marked on the contact sheet.
  The hotspot is the **point of action**, never the image centre: the arrow's tip, the brush's
  bristle, the dropper's tip, the magnifier's lens centre, the playhead's head.
- **Fallback chain always.** Every shipped rule is
  `cursor: url(<img>@1x) <x> <y>, url(<img>@2x) <2x> <2y>, <standard keyword>;` so a failed load or
  an unsupported browser degrades to a keyword, never to nothing.
- **Never the only signal.** A cursor states *where* the click lands; tooltips, labels and `aria`
  remain the source of truth for *what* the tool does.
- **No phone cursors.** The phone surface is touch; it keeps its existing (keyword-only) rules and
  is explicitly out of scope for the apply PR.

## The set

27 cursor names, covering the 21 numbered items in the brief (some items bundle two or four
cursors): 12 UI and 15 editor.

| Name | Group | Hotspot (32 px) | Fallback | Intent |
|---|---|---|---|---|
| `default` | UI | 6, 2 | `default` | Resting pointer; the arrow's tip is the click point. |
| `pointer` | UI | 12, 2 | `pointer` | Links and buttons; fingertip on the target. |
| `text` | UI | 16, 16 | `text` | Editable text; marks where typing lands. |
| `not-allowed` | UI | 16, 16 | `not-allowed` | A control that cannot take the action. |
| `progress` | UI | 16, 16 | `progress` | Static busy arc; never animated. |
| `grab` | UI | 16, 16 | `grab` | Content that can be dragged. |
| `grabbing` | UI | 16, 16 | `grabbing` | A drag already in progress. |
| `move` | UI | 16, 16 | `move` | Reposition in any direction. |
| `resize-ew` | UI | 16, 16 | `ew-resize` | Left–right sizing. |
| `resize-ns` | UI | 16, 16 | `ns-resize` | Up–down sizing. |
| `resize-nwse` | UI | 16, 16 | `nwse-resize` | Top-left ↔ bottom-right. |
| `resize-nesw` | UI | 16, 16 | `nesw-resize` | Top-right ↔ bottom-left. |
| `razor` | editor | 6, 27 | `crosshair` | Blade tool; cut point at the blade tip. |
| `trim-left` | editor | 13, 16 | `ew-resize` | In-point trim edge. |
| `trim-right` | editor | 19, 16 | `ew-resize` | Out-point trim edge. |
| `ripple` | editor | 16, 16 | `ew-resize` | Ripple edit closing toward the cut. |
| `slip` | editor | 16, 16 | `ew-resize` | Slip: source shifts inside a fixed clip. |
| `slide` | editor | 16, 16 | `ew-resize` | Slide: clip and neighbours shift; arrows point outward. |
| `pan` | editor | 16, 16 | `grab` | Hand tool; open hand in four arrowheads, grip at the palm centre. |
| `scrub` | editor | 16, 16 | `ew-resize` | Dragging the playhead; pentagon head on a full-height line. |
| `mask-draw` | editor | 16, 16 | `crosshair` | Drawing a mask region. |
| `mask-brush` | editor | 4, 28 | `crosshair` | Painting a mask; bristle tip is the point. |
| `eyedropper` | editor | 5, 28 | `crosshair` | Picking a colour; tip is the point. |
| `keyframe` | editor | 16, 16 | `pointer` | A point on an animated channel. |
| `zoom-in` | editor | 13, 13 | `zoom-in` | Zoom in; lens centre is the point. |
| `zoom-out` | editor | 13, 13 | `zoom-out` | Zoom out; lens centre is the point. |
| `crosshair` | editor | 16, 16 | `crosshair` | Precise or freeform placement. |

## Which surface uses which cursor

This is what the dashboard and the marketing site actually ship. Values are the custom cursor; the
keyword in parentheses is the token's fallback.

### Editor tools and gestures

| Surface | Cursor |
|---|---|
| Timeline canvas, select tool | `default` (`default`) |
| Timeline canvas, blade tool | `razor` (`crosshair`) |
| Timeline canvas, hand tool | `pan` (`grab`) |
| Timeline canvas, trim handle (in edge) | `trim-left` (`ew-resize`) |
| Timeline canvas, trim handle (out edge) | `trim-right` (`ew-resize`) |
| Viewer transform: move clip / crop body / overlay frame | `move` (`move`) |
| Viewer transform: rotate | `grab` (`grab`) |
| Thumbnail canvas: hand / space-drag | `grab` (`grab`) |
| Thumbnail canvas: while panning | `grabbing` (`grabbing`) |
| Thumbnail canvas: brush and layer-mask tools | `mask-brush` (`crosshair`) |
| Thumbnail canvas: subject (cut-out) tool | `crosshair` (`crosshair`) |
| Thumbnail canvas: text tool | `text` (`text`) |
| Curve editor graph | `crosshair` (`crosshair`) |
| Draggable lists (layers grip, track headers, keyframe lane) | `grab` (`grab`) |
| Disabled controls | `not-allowed` (`not-allowed`) |
| Buttons, links, rows, tabs | `pointer` (`pointer`) |
| Long-running operation (gallery refresh) | `progress` (`progress`) |

Set members that are drawn and available through the tokens but **not wired to a surface** yet:
`ripple`, `slip`, `slide` (there is no trim-mode switch in the current timeline), `scrub` (the
playhead is hit-tested on the canvas bitmap, not a DOM strip), `keyframe` (the lane is a drag
surface, not per-diamond), `mask-draw` (one brush covers both mask tools), `eyedropper` (no colour
swatch owns a cursor rule), and `resize-*` / `zoom-*` (kept native, below). They stay in the
catalog and the contact sheet.

### Deliberately native (Owner Phase-3 rule)

| Surface | Cursor | Why |
|---|---|---|
| Text inputs, textareas, `contenteditable` | native `text` | The I-beam is where the caret lands; branding it makes typing feel wrong. |
| Panels and media-pool splitters, bottom splitter | native `col-resize` / `row-resize` | Resize affordance, precise OS glyph. |
| Timeline seam width, crop-dialog edges and corners, viewer scale corner | native `ew-resize` / `ns-resize` / `nwse-resize` / `nesw-resize` | Same reason: a resize handle should look like every other resize handle on the OS. |
| Timeline zoom controls and Ctrl/⌘+wheel | native `zoom-in` / `zoom-out` | Precision/OS convention; the branded zoom art is unused. |
| `ProgramViewer` notes (`help`), `EarlyPreviewLabel` (`inherit`) | native | OS-provided / inherited on purpose. |
| `web/components/editor/phone/**`, `components/editor/review/MobileReview.tsx` | native | The phone surface is touch-only and unchanged. |

The `resize-*` and `zoom-*` art therefore appears only on the contact sheet. Keeping it in the set
lets a later decision reverse this without redrawing anything.

### Every ad-hoc rule and what it became

| File | Was | Now |
|---|---|---|
| `web/components/editor/timeline/TimelineCanvas.tsx` (canvas) | `blade → col-resize`, `hand → grab`, else `default` | `razor` / `pan` / `default` |
| `web/components/editor/timeline/TimelineCanvas.tsx` (trim zones) | `ew-resize` | `trim-left` / `trim-right` by edge |
| `web/components/editor/thumbnail/EditorCanvas.tsx` (canvas) | `grabbing` / `grab` / `crosshair` / `text` / `default` | `grabbing` / `grab` / `mask-brush` / `crosshair` / `text` / `default` |
| `web/components/editor/thumbnail/CropDialog.tsx` (crop body) | `move` | `move` |
| `web/components/editor/thumbnail/CropDialog.tsx` (corners/edges) | `nwse-`/`nesw-`/`ns-`/`ew-resize` | native (unchanged) |
| `web/components/editor/thumbnail/LayersPanel.tsx` (grip / rows) | `grab` / `pointer` | `grab` / `pointer` |
| `web/components/editor/thumbnail/ExportDialog.tsx` | `not-allowed` / `pointer` | `not-allowed` / `pointer` |
| `web/components/editor/viewer/TransformOverlay.tsx` (move / rotate / scale) | `move` / `grab` / `nwse-resize` | `move` / `grab` / native |
| `web/components/editor/viewer/OverlayFrameHandles.tsx` | `default` / `move` | `default` / `move` |
| `web/components/editor/viewer/ProgramViewer.tsx` (notes) | `help` | `help` (native) |
| `web/components/editor/viewer/RenderRequiredBadge.tsx` | `pointer` | `pointer` |
| `web/components/editor/color/CurveEditor.tsx` | `crosshair` / `default` | `crosshair` / `default` |
| `web/components/editor/color/ColorWorkspace.tsx` | `pointer` / `default` | `pointer` / `default` |
| `web/components/editor/panels/KeyframeLane.tsx` | `grab` / `default` | `grab` / `default` |
| `web/components/editor/panels/StructurePanel.tsx`, `EditIndexPanel.tsx`, `TranscriptPanel.tsx` | `pointer` / `not-allowed` | `pointer` / `not-allowed` |
| `web/components/editor/timeline/TrackHeaders.tsx` | `grab` | `grab` |
| `web/components/editor/timeline/SeamControls.tsx` | `pointer` / `ew-resize` | `pointer` / native |
| `web/components/editor/shell/PanelLayout.tsx` | `col-resize` / `row-resize` | native (unchanged) |
| `web/components/editor/media/media-pool.module.css` | `col-resize` | native (unchanged) |
| `web/components/editor/panels/clip-depth/clip-depth.module.css` | `pointer` / `not-allowed` | `pointer` / `not-allowed` |
| `web/components/editor/shell/workspace.module.css` | `pointer` | `pointer` |
| `web/components/editor/shell/EarlyPreviewLabel.tsx` | `inherit` | `inherit` (native) |
| `web/components/editor/phone/**`, `MobileReview.tsx` | `grab`, `ew-resize`, `pointer`, `default` | **unchanged** — touch surface |
| Every other `web/**` `cursor: pointer` / `default` / `not-allowed` / `progress` / `grab` / `move` / `crosshair` in CSS modules and inline styles | stock keyword | the matching token |
| `web/app/global.css` | no cursor rules | the token block, the root default arrow, the interactive-element rules, and the coarse-pointer collapse |
| `site/app/globals.css` | no cursor rules | the UI-subset tokens, `@utility` classes, and the base rules for links/buttons/inputs |
| `site/components/blog/ArticleFaq.tsx`, `layout/NewsletterSignup.tsx` | stock `cursor-pointer` utility | `cursor-brand-pointer` |
| `site/components/home/UseCaseStrip.tsx` | `disabled:cursor-not-allowed` | `disabled:cursor-brand-not-allowed` |

On the site the eight tokens are also exposed as `cursor-brand-*` utilities; the three components
above use them where a stock Tailwind utility used to win over the base rules. `text`, `progress`,
`grab`, `grabbing` and `move` are available for a component that needs them — the base rules wire
default, pointer, not-allowed and the native I-beam today.

## The contact sheet

`/app/cursors` renders every cursor at real size (32 px, via `srcSet` so a HiDPI screen uses the
`@2x` file) on white, `#2E1065` and `#808080`, in a light and a dark page theme, each labelled with
its name, its fallback and a ring on its hotspot. It is deliberately unlinked from the app, carries
`noindex`, and is behind the normal sign-in. It is a review artefact, not a product page.

## How the set is produced

```bash
node brand/scripts/cursors.mjs        # brand/cursors/src/*.svg → brand/cursors/*.png (+ provenance manifest)
node brand/scripts/sync.mjs           # masters + catalog → web/public/cursors/**, web/lib/cursors/catalog.json
                                      #                   and the site subset → site/public/cursors/**
node brand/scripts/sync.mjs --check   # fails on a drifted PNG, a stale copy or a missing source
```

`brand/cursors/src/*.svg` is the artwork; the PNGs are generated artefacts and are never edited by
hand. `brand/cursors/cursors.json` is the single contract — adding a cursor means adding an SVG and
a manifest entry, regenerating, and syncing; `sync.mjs` reads the catalog so no copy list is
maintained by hand. A cursor marked `"site": true` is also copied into `site/public/cursors/`; the
eight UI cursors carry that flag, the editor tools and the `resize-*` art do not.

## How it is wired

- **One token block per app.** `web/app/global.css` declares `--rb-cursor-<name>` for all 27 with
  the chain `url('/app/cursors/<file>') x y, url('/app/cursors/<file>@2x') 2x 2y, <keyword>`;
  `site/app/globals.css` declares the eight site cursors with `/cursors/...`. The `/app` prefix is
  the dashboard's basePath (Next does not rewrite absolute `url()` paths), `/cursors/` is the
  site's Pages root. A bare `url()` in a component is a bug — consume the token.
- **Root default.** `html, body { cursor: var(--rb-cursor-default) }` makes the branded arrow the
  resting pointer; a zero-specificity `:where(...)` rule brands links, buttons, tabs, summaries,
  labelled selects, menu items and options, and the Mantine button/anchor/action-icon/select/combobox
  /menu classes get an explicit rule because Mantine sets the stock hand at class specificity. Any
  component rule still wins.
- **Touch and forced colors collapse the tokens.** One `@media (pointer: coarse), (forced-colors:
  active)` block redefines every token to its bare keyword, so a phone or tablet gets exactly today's
  OS cursors and a high-contrast user keeps the OS cursor they rely on, while the same rules stay in
  the components. The phone editor surface is not edited at all.
- **The I-beam stays native** in `input`, `textarea` and `contenteditable`; `resize-*` and `zoom-*`
  stay native except the timeline trim handles. Those are the recorded exceptions above.
- **Fallbacks are tested, not assumed.** `web/lib/cursors/usage.test.ts` and
  `site/lib/cursors.test.ts` fail on a rule that is not a token or a recorded exception, a token
  without a standard keyword, a token pointing at a missing asset, or a site reference outside
  `/cursors/`.

## Non-goals

- Animating any cursor.
- Replacing the platform's text caret, selection or IME behaviour.
- A cursor that hides its point of action.
- A per-user cursor theme or a setting (a later question, not this initiative).
