# The editor's pages, panels and tools: aligning with professional editing conventions

**Status:** all five waves shipped (Waves 1 and 2 first; Waves 3, 4 and 5 together on 2026-11-11). The text below is the design record as approved; where a later change made a sentence untrue, the sentence says so. What shipped per wave is in `docs/editor.md`: "Pages and panel geometry" (1), the Compose sections (2), "Source and Record" and the trim sections (3), "Colour depth (pro UI wave 4)" and "Deliver page (pro UI wave 5)".
**Owner:** program lead, initiative `editor-pro-ui-alignment`.
**Scope:** the video editor (`web/components/editor/**`, `EditorWorkspace.tsx`, the `media/ cut/ compose/
color/ audio/ deliver/ timeline/ viewer/ panels/ phone/` surfaces) and its documentation. The Edit page
is `EditorWorkspace.tsx` itself, not a folder.
(Since 2026-11-11 `EditorWorkspace.tsx` is a thin component and its code lives in
`web/components/editor/workspace/`; the `EditorWorkspace.tsx:<line>` references below are to the file as it
was when this record was written.)
**Authority:** the initiative's `brief.md`, which is internal and deliberately not part of this PR (it is
the one file allowed to name the professional reference).

> **Naming rule.** The professional product this work is measured against is never named outside the
> brief: not in this document, code, comments, test names, commits, PR titles or bodies, docs,
> `docs/user-guide/**`, the marketing site or UI copy. This document says "the professional reference"
> and "professional editing conventions". We adopt conventions, not assets: every colour, control and
> icon is ReelBolt's (`--rb-ed-*`, Mantine, `@tabler/icons-react`).

---

## 1. What the owner is asked to decide

Read this document as a single approval with five parts:

1. **The page model** (§4, §5): seven pages, addressable, always labelled, each with its own geometry.
2. **The panel geometry** (§6): one table of zone sizes, one persistence model, one reset.
3. **Where masks and reframe live** (§7): tools on the clip, reachable from the timeline and the
   Inspector, with handles that do not vanish on play.
4. **Where colour lives** (§8): the colour page is the grade page, with no hidden mode.
5. **The keymap surface** (§10): a cheatsheet, a command palette, live chords, page shortcuts.

The wave plan (§13) then groups the work into five shippable waves. Wave 1 changes layout, navigation,
discoverability and the keymap surface only: **no document-format change**. Anything needing new
document fields or render work is named in §14 and needs separate approval.

## 2. Why now

The editor grew over many waves (P3-P9 and Waves 1-4) plus the thumbnail editor. Each wave added a
surface and each surface made its own layout decision. The result is an editor with all the capability
of a professional tool and none of its muscle memory: the pages exist but are not addressable, most of
the depth is behind local switches, and the on-canvas handles are tied to the transport rather than to
the tool. A person who edits professionally should not need a tour; today they do.

## 3. What exists today (the honest baseline)

This matters for the approval: **we are not building the page model from zero.**

- Seven pages exist as a frozen list: Media, Cut, Edit, Compose, Color, Audio, Deliver
  (`web/components/editor/shell/workspaces.ts:26-34`; `web/lib/editing/store/editor-store.ts:57`).
- A three-zone resizable layout exists, keyboard-operable, with saved layouts and per-user preferences
  (`shell/PanelLayout.tsx:192-285`; `prefs/LayoutMenu.tsx`).
- A 49-action keymap with defaults, conflict detection, per-user server persistence and JSON export/import
  exists (`web/lib/editing/keymap.ts:11-120`; `prefs/KeymapEditor.tsx`); `?` already opens the editable
  shortcuts dialog (`web/lib/editing/keymap.ts:220`; `EditorWorkspace.tsx:805-807`).
- On-canvas mask, reframe and grade-window handles exist, with a live draft and one-batch commit
  (`viewer/FrameHandlesOverlay.tsx`; `viewer/OverlayFrameHandles.tsx`).
- Colour has nodes, primaries, curves, LUT, HSL qualifier, tracked window, colour groups, gallery wipe,
  shot match and scopes (`color/GradeStackEditor.tsx`, `color/SecondaryFields.tsx`, `color/ScopesPanel.tsx`).
- Deliver is already a dedicated full-width two-column page (`deliver/DeliverWorkspace.tsx:444-462`).
- The viewer already has playback quality, simple preview, grade bypass, save still, Preview 3 s, a
  parity badge and an HDR note; it has no source/record switch and no fullscreen (§5.11).
- The editor is internationalised (`useT`; `web/messages/{en,it}/editor*.json`).

The gap is in the **model**, not the inventory: addressability, stable geometry, visible defaults and
entry points where a professional looks for them.

## 4. The page model

### 4.1 Definitions

- A **page** is one of the seven workspaces. It is a place, not a mode.
- A **zone** is one of left, centre, right, timeline. A page declares which zones it owns and their
  default sizes. A page may declare no centre (Deliver) or no timeline (Media, Deliver).
- A **tool** is an interaction state within a page (select, blade, mask, reframe, window, transform).
  Tools never change pages; they change what the pointer and the viewer overlays do.

### 4.2 Addressability

- The active page lives in the URL: `…/timeline/<timelineId>?page=media|cut|edit|compose|colour|audio|deliver`.
  `color` is accepted as a legacy alias. The default is `edit`; an unknown value falls back to `edit` and
  is normalised in the URL.
- A page change writes the URL with `history.pushState` (one entry) and updates the store; a `popstate`
  listener reads the page back from the URL. The page is client state, and the timeline page does not read
  `page` for data, so this adds **no request and no remount**. `router.push`/`router.replace` are not used
  for page changes. First-load normalisation uses `history.replaceState`.
- Reload restores the page from the URL. With no `page`, the editor restores the **last page used on this
  device** (localStorage, the same convenience model as panel sizes) and writes it to the URL with
  `replaceState`.
- A link with no `page` therefore opens the recipient's remembered page; a recipient who has never opened
  the editor lands on Edit. The link itself is not versioned.
- Back/forward across pages works because each page change pushes one entry; the document is never
  unloaded, so moving between pages cannot lose an edit. A test asserts the store, revision, history and
  save state are untouched and no fetch is issued.
- No server change: this is client state plus a history entry.

### 4.3 The switcher

| Surface | Presentation |
|---|---|
| Desktop (`>1100px`) | A labelled page row in its own 40px band under the 48px header, or at the start of the header if the width allows. **Labels never disappear.** |
| Tablet (`≤1100px`, coarse or narrow) | A 64px rail, icon over label, always labelled. The rail is 64px so "Compose" fits at the 12px floor. |
| Phone (`<600px` wide, or coarse and `<500px` tall) | **No page switcher.** The five-tool bottom dock is unchanged; Cut, Compose and Colour remain unreachable on a phone (§11). The only addition is the read-only Keyboard shortcuts entry. |

The order is fixed and matches the professional workflow: **Media, Cut, Edit, Compose, Colour, Audio,
Deliver**. The current page label and page title say "Color", while the rest of the copy says "colour";
this proposal renames the visible label to **Colour** for consistency (the Italian label stays "Colore").
It is a copy decision in three English strings plus one test fixture: the switcher label in
`web/messages/en/editor.json`, the page title in `web/messages/en/editor-color.json:3`, the `color` entry
in `shell/workspaces.ts:31`, and `__tests__/workspace-shell.test.tsx:57` (which compares rendered labels to
that entry). The Italian label stays "Colore"; any new keys are added to both locales. The owner can reject
it without affecting anything else.

Accessibility of the row:

- Inactive labels use one semantic token with a light and a dark value, `--rb-ed-fg-muted` (light
  `#495057`, 8.2:1 on the white header; dark `#c1c4cc`, over 10:1 on `#12141a`). The existing
  `--mantine-color-dimmed` (about 3.3:1 at 13px on white) is not used for page labels. The token is a
  design change in `web/app/global.css`; no other raw colour is introduced.
- The active page keeps `aria-current="page"`, a visible focus ring, and the existing tooltip description.
- A page switch updates the document `<title>` to "<page> · <film name>", makes the page name the page's
  `h1`, and moves focus to that heading, so a screen-reader user gets a "new page" cue
  (WCAG 2.4.2, 2.4.3, 4.1.3).
- The new band, palette and drawer respect `prefers-reduced-motion` and the documented z-index scale.

Each page carries a one-line description in its tooltip and its empty state (§12), so a new user learns
what the page is for without a tour.

### 4.4 Why not add pages

No page is added or removed in this proposal. The seven cover the workflow. What changes is that each
page **owns the area it needs** instead of most of them sharing one 300px column (§5).

## 5. Page structure and wireframes

The wireframes are the **target state**, shipped across the waves named in each heading; §13 says what
each wave actually ships.

### 5.1 Desktop, the shared frame

```
┌────────────────────────────────────────────────────────────────────────────────────┐
│ ← Film name   rev 12   [Early preview]      ⌨ shortcuts   ⤢ layouts   Export        │ 48
├────────────────────────────────────────────────────────────────────────────────────┤
│  Media   Cut   Edit   Compose   Colour   Audio   Deliver                           │ 40
├────────────────────────────────────────────────────────────────────────────────────┤
│ sync / relink notices (only when present)                                           │
├────────────────┬──────────────────────────────────────────┬────────────────────────┤
│ LEFT ZONE      │ VIEWER                                   │ RIGHT ZONE             │
│ page-owned     │ picture + transport + overlays            │ page-owned             │
│ (resizable)    │ (flex)                                   │ (resizable)            │
├────────────────┴──────────────────────────────────────────┴────────────────────────┤
│ TIMELINE (full width, page-owned height)                                            │
├────────────────────────────────────────────────────────────────────────────────────┤
│ status bar                                                                          │ 28
└────────────────────────────────────────────────────────────────────────────────────┘
```

The frame is one component. A page declares four things: its left content, its right content, whether
it shows the viewer, and its timeline height. The frame never changes shape; the page fills it.

### 5.2 Media (Wave 1)

```
┌───────────────┬──────────────────────────────────────────┬─────────────────────────┐
│ BINS          │ POOL (grid / list / filmstrip)           │ VIEWER + METADATA       │
│ search        │ [tiles…]                                 │ picture                 │
│ ▸ Campaign A  │                                          │ probe · tags · notes    │
│ ▸ B-roll      │                                          │ Place: Insert Overwrite │
│ + New bin     │                                          │        Append  On top   │
├───────────────┴──────────────────────────────────────────┴─────────────────────────┤
│ (timeline hidden by default; "Show timeline" reveals a 200px strip for context)     │
└────────────────────────────────────────────────────────────────────────────────────┘
```

Media keeps its three-pane pool (bins, pool, viewer+metadata) but becomes a page in the shared frame
with a header, the same storage model and one placement control (§9.5). The left tab "Media" on the Edit
page remains the same component in a narrower container (it already reflows by container width).

### 5.3 Cut (Wave 1 page shape; source/record in Wave 3)

```
┌───────────────┬──────────────────────────────────────────┬─────────────────────────┐
│ SOURCE TAPE   │ VIEWER  [ Source | Record ]              │ INSPECTOR               │
│ source + marks│ picture + transport                       │ clip properties         │
│ Insert Append │                                          │                         │
│ Fast review   │                                          │                         │
│ Boring shots  │                                          │                         │
├───────────────┴──────────────────────────────────────────┴─────────────────────────┤
│ OVERVIEW STRIP (whole timeline, fits)                                              │ 120
├────────────────────────────────────────────────────────────────────────────────────┤
│ TIMELINE                                                                            │ 240
└────────────────────────────────────────────────────────────────────────────────────┘
```

Cut stops being a 300px column: the source tape gets 320px, the overview strip gets its own band and the
record timeline sits below it. That is the shape the page was built for (it already contains a source
tape, overview strip, fast review and boring shots, `cut/CutWorkspace.tsx:150-263`). The Source/Record
switch in Wave 3 lives **inside the same viewer**, so it does not add a second viewer or change the
geometry.

### 5.4 Edit (Wave 1)

```
┌───────────────┬──────────────────────────────────────────┬─────────────────────────┐
│ PANEL TABS    │ VIEWER                                   │ PANEL TABS              │
│ Media Markers │ picture + transport + tools              │ Inspector Assist        │
│ Edits Analyse │                                          │ Settings Versions       │
│ Transcript …  │                                          │ Compare                 │
├───────────────┴──────────────────────────────────────────┴─────────────────────────┤
│ TIMELINE (toolbar, ruler, track headers 240, lanes)                                 │ 320
└────────────────────────────────────────────────────────────────────────────────────┘
```

Edit is today's default geometry, made explicit and given the same persistence as every other page.
Media, Markers, Edits, Analyse, Transcript, Subtitles, Takes, Structure, Comments, History stay the left
tabs; Inspector, Assist, Settings, Versions, Compare stay the right tabs.

### 5.5 Compose (Wave 2; mask/reframe entry points in Wave 1)

**Shipped in Wave 2.** The left list, the right properties and the on-canvas mask and corner pin tools are in. The on-canvas insert quad was left out because the renderer does not draw inserts in timeline renders (`SegmentPlanner.cs:71`); an insert's corners stay number fields.

```
┌───────────────────┬──────────────────────────────────────┬───────────────────┐
│ MASKS AND LAYERS  │ VIEWER                               │ PROPERTIES        │
│ clip: Close-up    │ picture + mask/reframe handles       │ Mask              │
│  ▸ Mask (rect)    │ (paused, or while the tool is on)    │  shape feather …  │
│  ▸ Reframe        │                                      │ Reframe           │
│  ▸ Keyer          │ parity: render-only                  │  point            │
│  ▸ Effects (3)    │                                      │ Keyer / Pin       │
│  ▸ Corner pin     │                                      │ Effects           │
│ + Add mask        │                                      │ Adjustment clip   │
├───────────────────┴──────────────────────────────────────┴───────────────────┤
│ TIMELINE (collapsed to 180px; still editable)                                    │
└──────────────────────────────────────────────────────────────────────────────────┘
```

Compose becomes the compositing page: a list of what is on the clip (mask, reframe, keyer, pin, effects,
adjustment), the viewer with the active tool's handles, and the properties of the selected item on the
right. It stops being one long scrolling form (§7). Wave 1 ships the **entry points** in the clip menu and
the Inspector (T-005); the Compose list/properties shell is Wave 2.

### 5.6 Colour (Wave 1 ungated; graph/wheels in Wave 4)

```
┌───────────────┬──────────────────────────────────────────┬─────────────────────────┐
│ NODES         │ VIEWER                                   │ GRADE TOOLS             │
│ 1 Balance  ●  │ picture + window handles                 │ Primaries | Curves |    │
│ 2 Look     ●  │                                          │ LUT | Secondaries |     │
│ 3 Shot     ●  │                                          │ Groups | Gallery        │
│ + Add node    ├──────────────────────────────────────────┤                         │
│ Target: Clip  │ SCOPES (waveform · parade · vectorscope  │ strength · fixes        │
│ Group: none   │ · histogram) always docked               │ before/after            │
├───────────────┴──────────────────────────────────────────┴─────────────────────────┤
│ TIMELINE (collapsed to 180px; clip selection drives the page)                       │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

The colour page is the grade page. The node stack is always visible, the scopes are always docked under
the viewer, and the depth (LUT, qualifier, window) is a labelled tab, not a hidden mode (§8). Named,
reorderable nodes, the serial graph and graphical wheels are Wave 4.

### 5.7 Audio (Wave 1)

```
┌───────────────────┬──────────────────────────────────────┬───────────────────┐
│ MIXER             │ VIEWER                               │ BUSES / VO        │
│ faders · pan      │ picture + transport                  │ submixes · duck   │
│ EQ · loudness     │                                      │ voice-over        │
├───────────────────┴──────────────────────────────────────┴───────────────────┤
│ TIMELINE                                                                            │ 280
└──────────────────────────────────────────────────────────────────────────────────────┘
```

T-003 moves the mixer into the left zone and the buses and the voice-over recorder into the right zone
(today all three share the left column). "Advanced audio" stays a persisted preference and keeps its
meaning: it folds **only secondary parameters** (buses, compressor, gate, limiter, de-esser, numeric EQ);
the faders, pan, mute/solo and loudness stay visible.

### 5.8 Deliver (frame in Wave 1; presets/queue/hand-off in Wave 5)

```
┌──────────────────────────────────────┬────────────────────────────────────────────┐
│ RENDER SETTINGS                       │ RESULT AND QUEUE                           │
│ Format · Quality · Range · Captions   │ latest film (player)                       │
│ Advanced (encoder, speed, loudness)   │ render queue with progress · Stop          │
│ estimate: ≈ N credits (range)         │ download · Export for other apps           │
│ [ Render ]                            │ review link · hand-off card                │
└───────────────────────────────────────┴───────────────────────────────────────────┘
```

Deliver keeps its two-column page and sticky footer, becomes a page in the shared frame (with the
timeline hidden) and gains the same header and chord hints as every other page. The estimate line is a
placeholder shape, not a number: the real figure comes from the existing estimate endpoint. Presets, the
queue and the hand-off content are Wave 5.

### 5.9 Tablet (Wave 1)

```
┌──────┬─────────────────────────────────────────────────────────────────────────┐
│Media  │ ← Film name  rev 12  [Early preview]          layout  Export          │
│Cut    ├─────────────────────────────────────────────────────────────────────────┤
│Edit   │                              VIEWER                                     │
│Compose│                     (drawer opens from the right)                      │
│Colour │  ────────────────────────────────────────────────────────────────────   │
│Audio  │                              TIMELINE                                   │
│Deliver│                                                                         │
└──────┴─────────────────────────────────────────────────────────────────────────┘
```

The rail is 64px so the longest label fits at 12px; if a label still does not fit, it uses a short label
plus an accessible name ("Compose" is the test case), never a truncation without a name. The right zone
is a drawer; the drawer button is named after what it opens (**"Inspector"** on Edit, **"Grade tools"** on
Colour, **"Properties"** on Compose), never a generic label over the picture. In Media the viewer is not
moved into the drawer.

### 5.10 Phone

The phone is unchanged in structure (see §11): header, viewer, clip strip, bottom dock, sheets. There is
no page switcher on a phone; the sheets already map to the tasks (Add media, Edit, Audio, Look and fix,
Captions, Export, Assist, Review, History).

### 5.11 Viewer and monitor (Wave 1: changes only what §7.3 says)

The viewer is one component on every page: program picture, transport, playback quality and simple
preview, grade bypass, save still, Preview 3 s, a parity badge and an HDR note. Wave 1 changes only the
handle rule (§7.3) and the chords in its tooltips.

- **Source/record** is Wave 3 and lives inside the same viewer as a **Source | Record** switch on Cut and
  Edit; it does not add a second viewer, so the geometry stays stable.
- **Fullscreen** shipped after the waves: keymap action `viewerFullscreen` (`P`) puts the viewer zone (picture and
  transport) on the whole screen; see `docs/editor.md` "The viewer on the whole screen".
- **Viewer zoom and safe-area editing** are not in any wave. They are named here so the omission is explicit rather
  than an oversight.

## 6. Stable panel geometry

### 6.1 The table

Desktop defaults, in CSS pixels. Sizes are stored as fractions of the container and pixels are derived
from them (see §6.2), so the table is the fallback, not the only record.

| Page | Left | Centre | Right | Timeline | Viewer |
|---|---|---|---|---|---|
| Media | 280 (bins) | pool (flex) | 420 (viewer + metadata) | hidden (200 on demand) | in the right zone |
| Cut | 320 (source tape) | viewer | 320 (inspector) | 240 + overview strip 120 | yes |
| Edit | 300 (panel tabs) | viewer | 320 (panel tabs) | 320 | yes |
| Compose | 340 (masks and layers) | viewer | 340 (properties) | 180 | yes |
| Colour | 300 (nodes) | viewer + scopes 150 | 360 (grade tools) | 180 | yes |
| Audio | 360 (mixer) | viewer | 320 (buses/VO) | 280 | yes |
| Deliver | 420 (settings) | — (no centre) | result (fills the rest) | hidden | no (the result column has the player) |

Deliver is a **two-column page that fills the frame**: a fixed 420px settings column and a result column
that takes the remainder. It declares no centre zone and no timeline; a page may do both. Every other
page keeps the three-zone shape.

Minimums: 240 per side zone, 160 for the timeline, 120 for the overview strip. The 320/340 columns are
the widths the current panels already assume; nothing becomes narrower than it is today.

### 6.2 One source of truth

- One module, `web/lib/editing/layout/editor-layout.ts`, owns the geometry table above.
- `PanelLayout` reads the page's entry; it no longer takes ad-hoc `initialLeftWidth`/`initialRightWidth`
  per call site.
- **Fractions are authoritative.** The record stores fractions; pixels are derived and clamped to the
  minimums. Hydration order: URL page → stored fractions for that page → the table default. On window
  resize, fractions re-derive pixels (no drift). Applying a saved layout replaces the page's fractions
  and persists them; the server saved layouts keep their existing shape (they already store fractions).
- One record, **per device** (localStorage is per origin and browser):
  `reelbolt:editor:layout:v2` → `{ [page]: { fractions: { left, right, timeline } } }`. When the page
  knows the signed-in user id, the key is suffixed with it; otherwise it is per device.
- The media pool's three panes are nested under the Media page's record. The legacy
  `pool-panes` localStorage is **read once on first load, migrated into `layout.v2.media`, and deleted**;
  it is never read again.
- Two explicit actions: **Reset this page** and **Reset every page** (the second asks for confirmation).
  There is no destructive double-press.
- The "Save current" mismatch is verified before it is changed. `panelSizes` starts at 280/300/320
  (`EditorWorkspace.tsx:772`) while the layout renders 300/320 (`:2270`); if the difference is only the
  mount race (`PanelLayout` reports its sizes on mount), the fix is the hydration order, not a new save
  model.

### 6.3 What this does not change

No document, op, sync, render or API change. The geometry is view state, exactly like today's panel
sizes.

## 7. Where masks and reframe go

### 7.1 Today, step by step

1. Open the **Compose** page (you must know it is there).
2. Select exactly one media clip (`compose/ComposeWorkspace.tsx:72`).
3. Scroll to **Mask**, flip **Shape mask** (`compose/ComposeWorkspace.tsx:240`).
4. Press pause, because the handles mount only while `playbackSpeed === 0`
   (`viewer/FrameHandlesOverlay.tsx:51,56`; `viewer/TransformOverlay.tsx:80,84`).
5. Drag the box; the sliders follow; release writes one `setProperty mask`.

Reframe is the same minus the shape select, with a fixed point (`compose/ComposeWorkspace.tsx:282`).

Nobody discovers this. The mask is a bare switch in a page reached by an icon that, below 1400px, has no
label.

### 7.2 The proposal

- **Add from the clip.** The timeline clip menu and a new **Mask and reframe** section in the Inspector
  both offer **Add mask** (rectangle or ellipse) and **Add reframe**. Adding one creates the existing
  document value, selects the clip, pauses the transport and activates the shape tool (§7.3). No page
  switch is forced.
- **Edit from two places, one component.** The Inspector and Compose render one shared
  `panels/MaskReframeFields.tsx`, so the two cannot drift; both write the existing fields through
  `setProperty` with the same one-batch commit. The Compose page is the deep surface (Wave 2).
- **The mask is listed** (Wave 2, shipped): Compose shows Mask (shape, feather, invert), Reframe (point), Keyer,
  Corner pin, Effects (n), Adjustment. Selecting an entry puts its handles on the picture and its fields
  on the right. The page stops being one long form.
- **The preview tells the truth.** When this was written, mask, keyer, pin, stabilise and reframe were render-only.
  They are drawn live now (the `editor-wysiwyg` work; `docs/editor.md` "What you see is what you export"), and the
  notes next to the controls say so. The parity badge above the viewer stays the one place that names a difference.

### 7.3 The tool state and the handles

- A new UI state, `viewerTool: 'select' | 'mask' | 'reframe' | 'pin' | 'window' | 'transform'`, lives beside the
  existing `tool` and is not part of the document.
- **The paused auto-show is unchanged, including on the phone.** Today, when playback is paused and the
  selected clip has a mask, a reframe point or a grade window, the handles are on the picture. That stays
  exactly as it is; the tool is not a prerequisite, so the phone (which has no Compose or Colour page)
  keeps showing a desktop-made mask's handles while paused.
- **The tool adds play-time persistence.** When a shape tool is active, the handles stay on the picture
  during playback. In that state the overlay subscribes through `withLivePlayhead` /
  `leafPlayheadFrame(state, PANEL_PLAYHEAD_QUANTUM_SEC)` (0.25 s, the 4 Hz leaf quantum the
  playhead-painting panels use) and memoises the target, so it recomputes at most a few times a second;
  the target is the selected clip while it is under the sampled playhead. When no shape tool is active, the paused behaviour is
  exactly today's and nothing new runs during playback.
- **The transform tool and a tracked grade window stay pause-only.** Their values follow the picture
  every frame, so their handles remain mounted only while paused; a tracked window is read-only at its
  tracked position.
- Entering a shape tool pauses playback and focuses the picture. Leaving it (Escape, or another tool)
  removes the play-time persistence but leaves today's paused auto-show in place.
- Every handle stays a focusable button with a name, arrow-key nudge (1%, Shift 10%) and one undo step
  per drag; 44px on coarse pointers is the existing rule
  (`viewer/OverlayFrameHandles.tsx:112`, keyboard nudge `:159-178`).
- The grade window follows the same rule: paused auto-show when the selected node has a window, plus
  play-time persistence while the Window tool is active (§8.2).

### 7.4 What stays out until separately approved

Multiple masks, combine modes, rotation, animated masks, reframe zoom, reframe keyframes and a live
compositing preview all need new document fields and/or render work (gap table M3-M5, C7). They are not
in any wave.

## 8. Where colour goes

### 8.1 Today

The Colour page opens on the look gallery and the adjustment-layer list. Everything else is behind one
button, **Advanced tools**, whose state is a local `useState(false)` that resets on every visit
(`color/ColorWorkspace.tsx:100,227-232,236-361`). Behind it: the clip/group target, the whole grade stack
(nodes, primaries, curves, LUT, qualifier, window), colour groups, the gallery and wipe, and the scopes.
"Match to reference" needs the scopes mounted, so it fails with "keep the scopes open" unless Advanced
is open (`color/ColorWorkspace.tsx:150-166`). The button's hint names "keyer", which lives in Compose
(`color/ColorWorkspace.tsx:230-232`).

### 8.2 The proposal

- **No gate.** The page opens on the grade. The look gallery becomes a section, not the page.
- **Three zones:** nodes on the left (name, on/off, selected, add), the viewer in the centre with the
  scopes docked under it, the grade tools on the right as tabs:
  **Primaries | Curves | LUT | Secondaries | Groups | Gallery**.
- **Scopes always docked** on the Colour page, with a collapse control. Statistics are computed for the
  page whether or not the panel is expanded, so "Match to reference" works from any tab (fix the
  dependency, not just its message).
- **Secondaries are a tab, not a mode.** The qualifier and the grade window live there. Selecting a node
  does **not** auto-activate the Window tool: the window section has an **Edit window on the picture**
  button that sets `viewerTool = 'window'`, and Escape leaves it. The handles then follow §7.3 (paused
  auto-show plus play-time persistence while the tool is active).
- **Copy fix.** The hint no longer names the keyer. Where the keyer is relevant, a link goes to Compose.
- **The graph and node editing** are Wave 4: named, reorderable, soloable nodes and the serial grade graph
  (the read-only stage graph already exists in Compose, `compose/NodeView.tsx:43-62`).
- **Preview fidelity.** Curves, LUTs and secondaries are previewed live in the viewer's grade pass
  (`web/lib/editing/preview/grade-pass.ts`), shipped by the `editor-wysiwyg` work ahead of Wave 4; the tabs carry no
  "render only" note.

## 9. Where deliver goes, and the other surfaces

### 9.1 Deliver

Deliver already has the right shape (two columns, sticky footer, full area). **Wave 1** moves it into the
shared frame with the timeline hidden, the shared header and a two-column split that fills the width
(§6.1); **Wave 5** ships the page content (presets, queue, export scope, hand-off, review links). No
render or billing change.

### 9.2 Cut

**Wave 1** gives Cut the page shape described in §5.3: source tape and overview strip where they belong.
Wave 3 adds the Source/Record switch inside the same viewer. No new capability, no engine change.

### 9.3 Media

Media keeps its three-pane pool, gains a header, one placement control and the shared geometry. The
pool's container queries keep working in the narrower Edit tab.

### 9.4 Audio

The mixer, buses and voice-over panels are laid out in the shared frame. "Advanced audio" stays a
persisted preference (§5.7).

### 9.5 One placement control (Wave 1, T-010)

The same four modes (Insert, Overwrite, Add to end, Place on top) are reached four ways today: a pool
tile menu (`media/ClipGrid.tsx:411-418`), source-viewer buttons (`place/PlaceMenu.tsx:53-65`), a Cut
"Insert" button (`cut/CutWorkspace.tsx:200`) and the phone's Insert/Append (`phone/AddMediaSheet.tsx:423-426`).
Wave 1 makes the mode set and its labels one component; the four hosts keep their placement but render
the same control, and the phone host is unchanged in behaviour. **No new chords in Wave 1**: placement
chords arrive with Wave 3's context menus, so the 57-action catalogue stays as §10.2 states.

## 10. The keymap proposal

### 10.1 Keep

The 49 actions, their NLE-standard defaults (space, J/K/L, I/O, B/V/H, R/T/Y/U, Delete/Shift+Delete,
Mod+C/X/V/D/Z), conflict detection, per-user server persistence and JSON export/import all stay
(`web/lib/editing/keymap.ts:11-120`).

### 10.2 Add

| Action | Default | Why |
|---|---|---|
| `pageMedia` … `pageDeliver` (7 actions) | `Shift+1` … `Shift+7` | A page is one keystroke away, in bar order. `Shift+digits` are unbound and browser-safe; `Alt+arrows` already moves clips between tracks. |
| `commandPalette` | `Mod+K` | Search every action by name and run it without knowing the chord. |
| — (not a catalogue action) | `?` | The cheatsheet; `isHelpKey` opens it. Today `?` opens the editable dialog, which becomes the cheatsheet's Edit button. |

`Shift+/` produces `?`; `isHelpKey` already recognises it (`web/lib/editing/keymap.ts:220`). The cheatsheet
is not a catalogue action. Catalogue total: **57 actions** (49 today, plus 7 page switches and the palette). Wave 2 adds nine Compose actions (Shift+M, Shift+F, Shift+P, Shift+S, Shift+I, Alt+Backspace, `[`, `]`, Mod+Alt+A): **66 actions** shipped. **Wave 3 shipped** five more: `toggleViewerMode` (`Shift+V`) and the placement chords `F9` Insert, `F10` Overwrite, `F8` Add to end, `Shift+F8` Place on top: **71 actions**.
`KeyActionInfo.group` (`web/lib/editing/keymap.ts:65`) gains a `'Pages'` member so the cheatsheet and the
editor can group them.

Touch mapping (`keymap-touch.ts`), which the CI test enforces:

- `pageMedia`, `pageEdit`, `pageAudio` → the phone dock (those are the phone's pages).
- `pageDeliver` → `touch('Header', 'Export')` (the phone's Export button, `phone/PhoneHeader.tsx:116`).
- `pageCut`, `pageCompose`, `pageColour` → **keyboard-only**, with the reason "Cut, Compose and Colour are
  desktop pages; the phone's dock has Media, Edit, Audio, Look and fix, Captions."
- `commandPalette` → **keyboard-only**, with the reason "A command palette needs a keyboard; the phone's
  More sheet lists actions, it does not search commands."

### 10.3 The surface

- **Cheatsheet (`?`):** grouped by Transport, Navigate, Marks, Tools, Edit, Pages; read-only; searchable;
  a no-match state; one **Edit shortcuts** button opens today's editor modal. Reachable from the header
  keyboard icon, the More menu on tablet and phone, and the page empty states.
- **Command palette (`Mod+K`):** a combobox over every action with its chord and whether it is available
  in the current selection; Enter runs it; unavailable actions are listed disabled with the reason.
  Empty state ("No command matches …"), and a loading state while the command list is prepared if it
  ever becomes async. Today the keydown handler is a private switch inside `EditorWorkspace.tsx:800-964`;
  Wave 1 extracts it into `lib/editing/commands.ts` (`Command` descriptors + `runCommand`) so the keymap
  and the palette are the same code path. This is a web refactor, no document change.
- **Live chords in the UI:** menus, tooltips and the palette read `effectiveChords` and render with the
  existing `displayChord` helper (`web/lib/editing/keymap.ts:189-192`, currently unused). The hard-coded
  "V", "B", "H" tooltips (`EditorWorkspace.tsx:1657-1658,1717-1719`) go away.
- **Accessibility:** `aria-keyshortcuts` is generated by a separate `ariaChord` formatter (Mod →
  `Control`/`Meta`, Space → `Space`, no glyphs; unit-tested), because `displayChord` is for visible copy,
  not the attribute. The cheatsheet is a dialog with a heading and a table; the palette is a combobox
  with the standard roles.
- **Phone:** a read-only **Keyboard shortcuts** entry in the overflow menu, listing the actions that need
  a keyboard with their reasons (`keyboardOnlyActions()`, `web/lib/editing/keymap-touch.ts:69-75`). It
  changes no phone behaviour.
- **Docs:** `docs/editor.md` and the e2e comment stop saying 37 actions; the user guide gains a
  **Pages and panels** page and a shortcuts page (§13, T-009).

## 11. What stays on the phone

The phone surface is out of scope for redesign and must not regress:

- `PhoneEditor` keeps its header, viewer (full/mini/hidden), clip strip, five-item dock, action bar and
  bottom sheets (`phone/PhoneEditor.tsx:41,94-118,203`).
- **There is no page switcher on a phone.** The dock keeps Media, Edit, Audio, Look and fix, Captions;
  the overflow keeps Assistant, Review page, History, touch-drag and copy link ("Open full view" was
  retired and is not listed).
- Cut, Compose and Colour stay unreachable on a phone; `NeedsLargerScreen` continues to explain the
  desktop-only regions (curves, window tracking, buses, compound clips, paragraph reorder).
- A desktop-made mask keeps drawing draggable handles on the phone while paused, exactly as today; the
  tool-state change in §7.3 does not make a tool prerequisite on the phone, and the 44px targets stay.
- The **Early preview** label stays on every surface until the owner lifts it (it gates nothing today;
  `shell/EarlyPreviewLabel.tsx`).
- The only addition is the read-only **Keyboard shortcuts** entry in the overflow (§10.3).

## 12. Discoverability rules (apply to every ticket)

1. **A page says what it is for** in one sentence in its default state, and what to do first.
2. **A capability is reachable from where it applies.** If it belongs to a clip, it is in the clip menu
   and the Inspector; if it belongs to a page, it is on the page.
3. **No hidden-by-default depth.** A switch may fold *secondary* parameters, never the primary tool of a
   page. "Advanced tools" is retired; "Advanced audio" may stay because it folds only buses, dynamics
   and numeric EQ, with faders, pan, mute/solo and loudness always visible.
4. **A control shows its chord** if it has one, read from the live keymap.
5. **A tool says when the preview cannot show it** (parity badge plus one plain sentence).
6. **Four header languages become one:** an `EditorPanel` heading on every docked surface; side tabs keep
   the tab row; no bare `Text fw={600}` headings.
7. **Text-floor and tokens:** 12px minimum (`no-small-fonts.test.ts`), `--rb-ed-*`, `--rb-clip-*` and the
   new `--rb-ed-fg-muted` only, no other raw hex, 44px touch targets, focus visible.
8. **Copy is a key:** new strings go into `web/messages/en/**` and `web/messages/it/**`.
9. **Loading, empty and error states** exist for the palette, the cheatsheet, the page bodies and the
   layout store (denied localStorage is a soft failure with a plain note).
10. **Motion and layering:** the page band, palette and drawer animate `transform`/`opacity` only, honour
    `prefers-reduced-motion`, and use the documented z-index scale.
11. **Placeholder numbers are marked.** A wireframe figure such as the Deliver estimate is a shape, not a
    price; real figures come from the estimate endpoint.

## 13. Wave plan

Each wave is its own PR and its own review. Depths marked **approval** need the owner's separate go-ahead
because they add document fields or touch the render path.

### Wave 1 — Find it (pages, panels, masks, colour, keymap surface)

**Goal:** a professional recognises where everything is without a tour.
**Ships:** URL-addressable and remembered pages (history API, no remount); an always-labelled switcher
with accessible tokens and page title/focus; the geometry table and one persistence model; Media, Cut,
Deliver and Audio in the shared frame (the mixer left, buses/VO right); masks and reframe reachable from the clip and the Inspector with a
non-regressive tool state and handles that survive play; the Colour page ungated with always-docked
scopes and the match fix; one placement control; the cheatsheet, command palette, live chords,
`aria-keyshortcuts` and the phone help entry; one header language; page empty states; docs and user
guide.
**Out:** any new document field; any render change; new pages; the phone's tool set; the Early preview
label.
**Cost:** M-L. Ten tickets, in dependency order: first T-001, T-004 and T-007; then T-002, T-003, T-005
and T-006; then T-008 and T-010; then T-009. One frontend executor: about 3 weeks. Two frontend
executors with design and docs part-time: about 2 calendar weeks.
**Review:** Design/UX + QA on the combined diff. No Security pass (no auth, storage or egress change).

### Wave 2 — Compositing fluency

**Goal:** masks and reframe behave like tools, not settings.
**Ships:** an on-canvas mask tool (draw, shape, feather, invert), the mask/reframe/keyer/pin sections as a
selected-target editor, adjustment-clip and insert editing in the same surface, chords for every compose
action.
**Out:** mask animation, multiple masks, reframe zoom or keys (**approval**), live compositing preview
(**approval**).
**Cost:** M. **Review:** Design/UX + QA.

### Wave 3 — Edit like a professional

**Shipped.** Source | Record in the one viewer, placement chords, context menus that read the live keymap, the four-way trim control with its readout in words, snap labels and the ripple preview.

**Shipped.** The Source | Record switch (`Shift+V`) in the single viewer on Cut and Edit; placement chords; context menus that show live chords, with lane and ruler menus; a visible four-way trim control with a readout in words; snap labels and ripple ghosts; the user guide section [From Cut to Edit](../user-guide/editor/pages-and-panels.md#from-cut-to-edit).

**Goal:** the timeline is keyboard-first and Cut earns its name.
**Ships:** the Source/Record switch inside the single viewer; trim modes with visible state; context
menus with chords (including Placement); snapping and ripple feedback; a documented Cut-to-Edit path.
**Out:** op catalogue, sync, render, a second viewer.
**Cost:** M. **Review:** Design/UX + QA.

### Wave 4 — Colour depth

**Shipped.** Rename, reorder, duplicate, reset and solo on the node list (solo is view state only), the grade chain on the Colour page, scopes layouts with a resizable dock, and the compare bar (bypass, split, reference, wipe). Graphical wheels and the live preview of curves, LUTs and secondaries had shipped before it. Parallel graphs, several windows per node, grade versions and per-node animation stay out (approval).

**Goal:** the Colour page is the grade page.
**Ships:** named, reorderable, soloable nodes; the serial grade graph on the Colour page; graphical
wheels; scopes layouts; before/after and gallery wipe as first-class controls; a live preview path for
curves, LUTs and secondaries (browser compositor).
**Out:** parallel graphs, multiple windows per node, grade versions, per-node animation (**approval**).
**Cost:** L. **Review:** Design/UX + QA; Security if the preview path touches media or storage.

### Wave 5 — Deliver and hand off

**Shipped.** Presets as the spine of the settings column (with per-device saved presets), one cost statement above Render, the queue under the latest film with Render again for renders started in this browser, and review links, export scope and the hand-off package as ordered sections beside the result. No renderer, billing or route change.

**Goal:** finishing a film is one page with no surprises.
**Ships:** the deliver page content: presets and queue as the spine, export scope, hand-off and review
links beside the result, one cost statement before a render. (The frame is Wave 1.)
**Out:** renderer and billing changes.
**Cost:** M. **Review:** Design/UX + QA; Security on the hand-off and review-link surfaces.

## 14. Migration cost, per change

Legend: **S** ≤ 1 day, **M** 2-3 days, **L** ≥ 4 days (one executor, tests included).

| Change | Files | Cost | Doc/engine | Wave |
|---|---|---|---|---|
| Page in the URL + remembered (history API) | `EditorWorkspace.tsx`, `editor-store.ts`, timeline page, `open-timeline.ts` | S | no | 1 |
| Labelled page row / 64px rail + accessible tokens + title/focus | `shell/WorkspaceBar.tsx`, `WorkspaceHeader.tsx`, `workspace.module.css`, `web/app/global.css`, `workspaces.ts`, messages, `__tests__/workspace-shell.test.tsx` | S/M | no | 1 |
| Geometry table + one persistence (fractions) | `shell/PanelLayout.tsx`, `EditorWorkspace.tsx`, `media/pool-panes.ts`, `prefs/LayoutMenu.tsx`, new `editing/layout/editor-layout.ts` | M | no | 1 |
| Media, Cut, Deliver and Audio in the shared frame | `EditorWorkspace.tsx`, `cut/CutWorkspace.tsx`, `deliver/DeliverWorkspace.tsx`, `audio/MixerPanel.tsx`, `audio/BusesPanel.tsx`, `audio/VoRecorderPanel.tsx`, media CSS | M | no | 1 |
| Mask/reframe entry points + tool state | `compose/ComposeWorkspace.tsx`, new `panels/MaskReframeFields.tsx`, `panels/InspectorPanel.tsx`, `timeline/ClipActionMenu.tsx`, `viewer/FrameHandlesOverlay.tsx`, `viewer/TransformOverlay.tsx`, `editing/viewer-transform/*` | M | no | 1 |
| Colour ungated + scopes docked + match fix | `color/ColorWorkspace.tsx`, `color/GradeStackEditor.tsx`, `color/ScopesPanel.tsx`, `EditorWorkspace.tsx`, messages | S/M | no | 1 |
| Cheatsheet + palette + live chords + a11y | `editing/keymap.ts`, new `editing/commands.ts`, `prefs/KeymapEditor.tsx`, `EditorWorkspace.tsx`, `WorkspaceHeader.tsx`, `phone/PhoneHeader.tsx`, `keymap-touch.ts`, messages | M | no | 1 |
| One placement control | `place/PlaceMenu.tsx`, `place/placeChoices.ts`, `media/ClipGrid.tsx`, `cut/CutWorkspace.tsx`, `phone/AddMediaSheet.tsx`, messages | S | no | 1 |
| One header language + empty states | `ui/EditorPanel.tsx`, `panels/*`, `media/*`, `shell/workspace.module.css`, messages | S | no | 1 |
| Copy: Color → Colour; docs correction | `messages/en/editor.json`, `messages/en/editor-color.json:3`, `shell/workspaces.ts`, test fixture, `docs/editor.md`, `docs/user-guide/editor/*`, `docs/user-guide-i18n/it/*` | S | docs | 1 |
| Compose as a selected-target editor | `compose/*`, `panels/InspectorPanel.tsx` | M | no | 2 |
| On-canvas mask drawing | `viewer/*`, `editing/viewer-transform/*` | M | no | 2 |
| Source/Record in the single viewer + trim affordances | `cut/CutWorkspace.tsx`, `timeline/*`, `viewer/*` | M | no | 3 |
| Context menus with chords (incl. Placement) | `timeline/*`, `editing/commands.ts` | S/M | no | 3 |
| Node list reorder/rename/solo | `color/*`, `editing/color/grade.ts` | M | no | 4 |
| Grade graph on Colour | `color/*`, `compose/NodeView.tsx` | M | no | 4 |
| Live preview for curves/LUT/secondaries | `editing/preview/*`, `editing/color/preview-grade.ts` | L | engine (browser) | 4 |
| Deliver page content (presets, queue, hand-off) | `deliver/*` | M | no | 5 |
| Animated/multiple masks, reframe zoom/keys | document types, engine, renderer | L | **new fields + render, approval** | — |
| Parallel grade graphs, multiple windows | document types, engine, renderer | L | **new fields + render, approval** | — |

## 15. Acceptance

This document is accepted for Wave 1 when:

1. The owner approves the page model (§4), the geometry table (§6), the masks/reframe model (§7), the
   colour page (§8) and the keymap additions (§10), or names the parts to change.
2. A Design/UX review and a QA consistency pass are recorded at the PR head in
   the internal verdict records (the live initiative record, not part of
   this docs PR).
3. The wave-1 tickets exist (`tickets/T-001.md` … `T-010.md`) with files, acceptance and department, and
   no ticket changes the document format.

Wave 1 itself is accepted when every ticket's acceptance criteria pass, the phone surface has no
regression (including a desktop-made mask's handles while paused and every existing sheet), the taste
checklist is ticked per ticket and the docs match the shipped behaviour.

## 16. What this document does not decide

- The render path, billing, the phone tool set, or the Early preview label.
- Any new document field. Those are §14's last two rows and need their own approval.
- The visual identity beyond one semantic label token with a light and a dark value (`--rb-ed-fg-muted`);
  no new icons or chrome.

---

## Appendix A. Gap analysis in one table

Full analysis with citations: the initiative's internal `plan.md` §3 (not part of this PR).

| Area | Reference convention | Today | Gap | Cost | Doc/engine |
|---|---|---|---|---|---|
| Page identity | A page is a place | In-memory only, default Edit | Not addressable | S | no |
| Switcher | Always labelled | Labels vanish <1400px | Unlabelled icons | S | no |
| Geometry | Stable per page | Media/Deliver differ; three persistence systems | Learned per page | M | no |
| Masks | A tool on the clip | Switch in Compose, single clip | Undiscoverable | S/M | no |
| Handles | Tied to the tool | Tied to pause | Vanish on play | M | no |
| Mask model | Several shapes, animated | One static rect/ellipse | Limited | L | fields + render |
| Reframe | Framed crop with zoom/keys | Fixed point only | No zoom/animation | M/L | fields |
| Colour | The page is the grade | Everything behind "Advanced tools" | Hidden depth | S | no |
| Scopes | Under the viewer | Inside Advanced; match depends on them | Hidden, fragile | S | no |
| Grade nodes | Named, reorder, solo | Append-only, read-only name | Rigid | M | no |
| Grade graph | On the colour page | Read-only graph in Compose | Wrong page | M | no |
| Curves/LUT/secondaries preview | Live | Not previewed | Blind grading | L | engine (browser) |
| Keymap help | One key away | `?` opens only the editor | No cheatsheet/palette | S/M | no |
| Chords in UI | Read the live keymap | Hard-coded tooltips | Stale after remap | S | no |
| Phone help | Reachable | No shortcuts entry | No keyboard-only list | S | no |
| Viewer/monitor | Source and record, fullscreen | One program viewer, no source/record | Partial | M | no (Wave 3) |
| Media | One pool, one geometry | Three geometries, own storage | Inconsistent | M | no |
| Cut | The page shape it needs | Crammed into 300px | Side panel feel | M | no |
| Place | One control | Four UIs | Unequal discovery | S | no |
| Deliver | A delivery page | Already two columns | Frame and content split | M | no |
| Headers | One language | Four | Vertical drift | S | no |
| Docs | Match the product | Says 37 actions; catalogue has 49 | Drift | S | docs |
