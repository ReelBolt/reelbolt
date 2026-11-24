# The editor document (EditTimeline v2)

The **v2 editable document**: a JSON timeline a client can open, edit op-by-op and save back under
optimistic concurrency. This page is self-contained — the document model, the time model, the
closed op catalogue, the sync protocol, the trust boundary and the four accepted engine deviations
are all here, and you do not need any other page to work from it.

[`docs/video-editing.md`](video-editing.md) is the *other* half of the feature and deliberately
covers different things: the v1 **manifest** (`TimelineManifest`, the description of its tracks a
compiled render emits; the read-only "Studio" viewer that once displayed it is gone, and a manifest
is now only ever a v2 timeline's seed) and the `VideoAnalyze` → decision → `VideoCompile`
pipeline that produces it. A v2 timeline is **seeded from** a v1 manifest and then edited; the
pipeline that compiles one is documented there, not here.

---

## Table of Contents

- [What ships in this phase, and what does not](#what-ships-in-this-phase-and-what-does-not)
- [How the workspace code is laid out](#how-the-workspace-code-is-laid-out)
- [The Simple shell](#the-simple-shell)
- [The document model](#the-document-model)
- [The time model](#the-time-model)
- [The op catalogue: thirteen ops, closed](#the-op-catalogue-thirteen-ops-closed)
- [The sync protocol](#the-sync-protocol)
- [Never losing an edit (Wave 1)](#never-losing-an-edit-wave-1)
- [The trust boundary](#the-trust-boundary)
- [Accepted engine deviations (contract, not defects)](#accepted-engine-deviations-contract-not-defects)
- [The image document: layers that are subjects, not frames](#the-image-document-layers-that-are-subjects-not-frames)
- [The thumbnail editor's document API](#the-thumbnail-editors-document-api)
- [Thumbnail editor operations](#thumbnail-editor-operations)
- [Designed graphics](#designed-graphics)
- [Thumbnail editor uploads and exports](#thumbnail-editor-uploads-and-exports)
- [The thumbnail editor in the browser](#the-thumbnail-editor-in-the-browser)
- [Storage, ownership and revision history](#storage-ownership-and-revision-history)
- [Media: the pool, the bins, and the derivative pipeline (P2)](#media-the-pool-the-bins-and-the-derivative-pipeline-p2)
- [The editable timeline shell (P3)](#the-editable-timeline-shell-p3)
- [Pages and panel geometry](#pages-and-panel-geometry)
- [Core editing and rendering (P4)](#core-editing-and-rendering-p4)
- [Clip depth (P5)](#clip-depth-p5)
- [Workflow speed (P6)](#workflow-speed-p6)
- [Colour and compose (P7)](#colour-and-compose-p7)
- [Advanced structure and audio (P8)](#advanced-structure-and-audio-p8)
- [Polish (P9)](#polish-p9)
- [Visual system and accessibility (Wave 1 polish)](#visual-system-and-accessibility-wave-1-polish)
- [Timeline editing core (Wave 2, T-004: R03.1 / R03.2 / R03.3)](#timeline-editing-core-wave-2-t-004-r031--r032--r033)
- [Place from media, titles, colour cards, freeze frame (Wave 2, R03.6 / R10.5 / R07.5 / R07.6)](#place-from-media-titles-colour-cards-freeze-frame-wave-2-r036--r105--r075--r076)
- [Linked clips and multicam sound (Wave 2, R08.1 / R08.2)](#linked-clips-and-multicam-sound-wave-2-r081--r082)
- [Audio model UI (Wave 2, R08.2-R08.7, T-010)](#audio-model-ui-wave-2-r082-r087-t-010)
- [Loudness measurement and audition (R08.4, R08.6)](#loudness-measurement-and-audition-r084-r086)
- [Preview 3 s (Wave 4, R10.4)](#preview-3-s-wave-4-r104)
- [Analysis-driven editing and captions (Wave 2, T-014: R09.2-R09.9, R12.9)](#analysis-driven-editing-and-captions-wave-2-t-014-r092-r099-r129)
- [Direct transform on the viewer (Wave 2, R07.2)](#direct-transform-on-the-viewer-wave-2-r072)
- [Hand-off package and safe import matching (Wave 4, T-001: R12.2 / R12.3)](#hand-off-package-wave-4-t-001-r122--r123)
- [Pick an object](#pick-an-object)
- [Follow the subject](#follow-the-subject)
- [Export scope, safe preferences, layouts, presets, offline relink (Wave 2, T-019: R12.1 / R12.4 / R12.5 / R12.7 / R12.8)](#export-scope-safe-preferences-layouts-presets-offline-relink-wave-2-t-019-r121--r124--r125--r127--r128)
- [Picture editor, Wave 2 behaviour](#picture-editor-wave-2-behaviour)
- [Picture editor on a phone (Wave 3, RM.14)](#picture-editor-on-a-phone-wave-3-rm14)
- [Phone and tablet (Wave 3)](#phone-and-tablet-wave-3)
- [Dropdowns and menus (popover placement)](#dropdowns-and-menus-popover-placement)
- [Visual system sweep (Wave 2, T-026: VF4-VF7, VF13-VF15, VF18)](#visual-system-sweep-wave-2-t-026-vf4-vf7-vf13-vf15-vf18)
- [Deferred clip operations (Wave 4, R03)](#deferred-clip-operations-wave-4-r03)
- [What you see is what you export (editor-wysiwyg)](#what-you-see-is-what-you-export-editor-wysiwyg)
- [Grounded proposals (Wave 4, R11.2)](#grounded-proposals-wave-4-r112)
- [On-canvas mask and reframe handles (Wave 4, R10.3)](#on-canvas-mask-and-reframe-handles-wave-4-r103)
- [Deliver page (pro UI wave 5)](#deliver-page-pro-ui-wave-5)
- [Input colour handling (Wave 4, R10.7)](#input-colour-handling-wave-4-r107)
- [Audio previews for buses and dynamics (Wave 4, R08)](#audio-previews-for-buses-and-dynamics-wave-4-r08)
- [Layer masks (Wave 4)](#layer-masks-wave-4)
- [Guest review links (Wave 4)](#guest-review-links-wave-4)

---

## Phone and tablet (Wave 3)

The editor is a hybrid (decision D1). A **phone** is "review, fix and send": it opens the same timeline over the same store,
operations and sync client as the desktop (no new operation and no new document field), through a focused surface in
`components/editor/phone/` (`PhoneEditor`). A **tablet** and a **desktop** get the full editor. The tablet works but is not
certified (decision D2); real-device behaviour is an accepted, unverified risk (decision D8: emulation only, see
[the phone spike](editor-phone-spike.md)). The editor carries the "Early preview" label on every device.

**Device classes** (`lib/editing/device-class.ts`, read through `useDeviceClass()`, the one source):

| Class | Rule | Surface |
|---|---|---|
| phone | narrower than 600 px, or shorter than 500 px with a coarse pointer (a landscape phone) | `PhoneEditor`: header, viewer, clip strip, action bar, dock, bottom sheets |
| tablet | 600-1100 px, or any other coarse pointer (so 600-767 px is a tablet) | the desktop panel layout with a 64 px page rail, a docked Inspector on touch tablets (768-1100 px), 44 px handles |
| desktop | wider than 1100 px with a fine pointer | the full panel layout |

**Phone notes.** (a) The low-balance and unverified-email banners are hidden on the phone editor; each paid action shows its own price before it asks. (b) The Delete words sheet is 90 dvh tall, so on a short screen it covers the Undo in the header: press Done first. (c) On a tablet in landscape (1024x768) the picture is small, about 190 px wide, because the timeline takes the height. This is a known limitation to improve.

A phone that opens a full-editor link gets the phone surface and a dismissible note ("Phone view. Some tools need a larger
screen.") with Copy link. Full view is not offered on a phone, and a stored choice from the old button is cleared. The strip shows one
notice at a time: the sound-tracks offer (one row on a phone) goes first and the note follows it. Under 640 px tall in portrait both wait
for a taller screen. The preview chip shows its dot (tap for the words) and the viewer's header stays on one row, the chip and the way back first, the tools scrolling sideways after them; in portrait, at mini size and up to 740 px tall, it sits over the picture. On a phone the way back from the Simple preview is in that header, not on the picture. The transport never scrolls away: when room is short, the picture shrinks to fit.

**Where each phone feature is described** (all in this document): the frame, header, dock, sheets and conflict bar, the Add
media sheet, the clip strip, the action bar with Select mode and the trim sheet, the Look and fix sheet with the clip sheet,
the Audio sheet and the Captions sheet are the "Phone frame", "Phone Add media sheet", "Phone clip strip", "Phone action bar",
"Phone Look and fix sheet", "Phone Audio sheet" and "Phone Captions sheet" paragraphs under [The canvas
timeline](#the-canvas-timeline); the tablet and the device classes are under [Layout of the workspace](#layout-of-the-workspace);
Deliver on a phone is under the Deliver section; the proposal sheet and the review page are under [Review comments and the
mobile review page](#review-comments-and-the-mobile-review-page) and [Phone proposal review sheet (RM.13,
R11.3)](#phone-proposal-review-sheet-rm13-r113); the picture editor is [Picture editor on a phone (Wave 3,
RM.14)](#picture-editor-on-a-phone-wave-3-rm14).

**Gestures.** Every gesture has a visible button path. A tap selects; one finger on the strip pans; two fingers pinch to zoom
time or pan without touching the selection; a double tap on a clip zooms to it; the selected clip's trim handles have a 44 px
hit area; touches in the outer 20 px are left to the system (the iOS back swipe). Picking a clip up to move it (long press and
drag) is behind the per-device flag `reelbolt:editor:touch-drag` (default off) until real iOS devices clear it; with the flag
off a long press opens the clip action menu.

**Not on a phone** (each shows a plain "needs a larger screen" sentence instead of a control, or is simply not offered): marquee
selection, compound clips, slide, join, fit to fill, moving a clip to another track; layouts, presets and editing the keymap
(the read-only **Keyboard shortcuts** list is in the overflow menu); keyframes, curves, transitions and generators (existing
values are shown read-only); buses, EQ and dynamics numbers,
automation, multicam; takes, compare, the cut workspace and stills; colour wheels, curves, keyer, pin and qualifier; bin
reparenting and smart-bin editing; paragraph reordering in the transcript; the all/forward/backward selection menu.

**Touch parity.** `lib/editing/keymap-touch.ts` `TOUCH_PARITY` gives every keymap action a touch control or a keyboard-only
reason, and a unit test fails when an action is in neither:

| Keymap action | Touch control on a phone |
|---|---|
| Play / pause, step one frame, previous and next edit, go to start and end, mark in and out | Transport |
| Previous and next marker, clear in and out, snapping, lift, copy, cut, paste, link and unlink | More sheet |
| Add marker, split, duplicate, delete, nudge (1 and 10 frames) | Action bar |
| Ripple, roll, slip, slide | Trim sheet |
| Undo, redo | Header |
| Deselect | Select mode, Clear |
| Blade, select and pan tools; select all, forward, backward, all on track | Timeline toolbar (tablet and desktop); on a phone the action bar and Select mode do the work |
| Go to Media, Edit, Audio | The phone dock (those are the phone's pages) |
| Go to Deliver | The phone header's **Export** |
| Shuttle (J, K, L), stop, play in to out, step ten frames, move clip to another track | Keyboard only (reason in the table of the source) |
| Go to Cut, Compose or Colour; command palette | Keyboard only: those are desktop pages, and a palette needs a keyboard. The phone overflow lists them with the reason |

**Touch details that are guarded by tests.** Every sheet takes its height from its detent (peek, half, full) and keeps below
the status area; the grabber, close button, buttons, fields, switches and segmented controls inside a sheet or the phone shell
are at least 44 px high (`components/editor/__tests__/touch-targets.test.ts` fails on a smaller control). The phone surface pads
for the notch (top), the rounded corners (sides) and the home bar (bottom). Menus, popovers and dialogs, which sit outside the
editor surface, get the same 44 px sizes on a coarse pointer. With reduced motion on, the sheet and the editor's menus, dialogs
and drawers appear without sliding. The phone has no saved layouts (there are no panels to size); the tablet shows the Layouts
button in its header once the side panels are panels (900 px and wider) and hides it while they are drawers.

**Tests.** `e2e/tests/editor-v2/30-phone/` holds one spec per feature plus `acceptance.spec.ts` (the 14 phone acceptance
criteria, one test each, titled "AC n") and `rm1-spike.spec.ts` (the emulation measurements). Projects: `pixel7` (real CDP touch),
`iphone14webkit` (synthetic pointers; drag-only checks skip there with a reason), `tablet` (1024x768 touch, only
`tablet.spec.ts`) and `desktop` for regressions. The specs need the local docker stack; until it has run they are marked
"pending run" in the lane ledgers.

---

## What ships in this phase, and what does not

**A timeline editor now ships (P3).** `/app/editor/{projectId}/timeline/{timelineId}` opens a v2
document in an editable workspace: a canvas timeline with track headers, a program viewer that plays
**the timeline** (not the last mp4), a transport with JKL/timecode entry/in-out marks, selection and
snapping, markers including agent-rationale markers, an EDL-style edit index, undo/redo with a
visible history list, autosave with optimistic concurrency and a read-only banner on conflict, and a
server-side revision history with restore. The editor hub `/app/editor?project={projectId}` lists,
creates (blank or seeded from a compile result), imports and duplicates timelines; see
[The editor hub and the one video editor](#the-editor-hub-and-the-one-video-editor). See
[The editable timeline shell (P3)](#the-editable-timeline-shell-p3) for what is faithful, what is
approximate and what is render-required, and for the degradation contract.

**P4 made the timeline editable and renderable.** The op catalogue is twenty-five ops (the thirteen
P1 ops plus twelve P4 ops for placement, track structure, settings and in/out — see
[Core editing and rendering (P4)](#core-editing-and-rendering-p4)), track flags and timeline settings
are persisted through ops, remove-silence is a reviewable one-undo-step dialog, and
`StepType.TimelineRender` renders a stored revision through a region cache into a closed set of
delivery presets. The compositor previews cuts, stacking and gain faithfully and marks everything else
as approximate or render-required — it is best-effort by design, and the render is authoritative.
Link/group landed with Wave 2 (see [Linked clips and multicam sound](#linked-clips-and-multicam-sound-wave-2-r081--r082)). What was still deferred at P8: compound clips and `applyProposal` (the Wave 4 grounded-proposal path is described in [Grounded proposals](#grounded-proposals-wave-4-r112)). Keyframe authoring,
duration-changing retime and transitions landed with P5 (see [Clip depth (P5)](#clip-depth-p5));
interchange export/import, subtitles, text-based editing, smart bins, take stacking, compare and
stills landed with P6 (see [Workflow speed (P6)](#workflow-speed-p6)); grades, colour groups, scopes,
masks, keyers, corner pins, effects, stabilisation, reframe, adjustment clips and title templates
landed with P7 (see [Colour and compose (P7)](#colour-and-compose-p7)).

**P2 ships the media workspace** (the editor's own Media workspace; the hub's separate Media tab was folded away later, and
`view=media` now redirects to Videos): the project's media pool with bins, a
source viewer, a metadata panel and an explicit derivative-request affordance. It writes nothing to a
timeline — see [its own section below](#media-the-pool-the-bins-and-the-derivative-pipeline-p2).

**Media pool (Wave 2, R05.2-R05.8).** `components/editor/media/` + `lib/editing/media/pool-browse.ts` (pure search/sort/filter, `matchesSmartFilter`
mirroring `MediaSmartFilter.Matches`, `describeRule`, `moveTargets`). Tiles carry a lazy poster (`TilePoster`: image, or the video's first frame from its own
bytes, IntersectionObserver-gated), one-tap star/flag (`PUT .../media/annotations/{mediaRef}` per change, optimistic, rolled back on failure), a colour dot and
bin chips. The default sort is "Order added" (the server's order); the storage category is no longer shown. Bin actions are one `⋯` menu per bin (rename, new
sub-bin, move to, edit rule, delete-with-confirm); `Move to` on a bin and on a pin row use the same `PATCH` as a drag; smart bins are never offered as a target.
`SmartBinEditor` creates/edits a rule (empty refused; live match count); a smart bin's tile list and tree count are evaluated client-side with the same rules as the
server, so an edited rule or a new star shows at once. `SourceViewer` has no Original/Proxy switch (`ProxyToggle` removed): it plays the proxy when one exists
(label "Simple preview", `data-playing-source="proxy"`), else the original. Pool-to-timeline: tiles write `CLIP_DRAG_MIME` plus a `...-kind-<kind>` type;
WebGL context loss: the compositor cancels `webglcontextlost` (so the browser may restore it), stops drawing while lost, and on `webglcontextrestored` rebuilds its programs, buffers and textures and repaints the current frame (`gl-compositor.ts`).
`TimelineCanvas` (`onPoolDrop`) shows `pool-drop-indicator` (blue allowed / red wrong-kind) and `EditorWorkspace.dropPoolItem` places it with
`buildPlaceOps(mode: 'overwrite', at: snapped frame, trackId)` honouring the source viewer's marks - one undo step, no new op. **Touch drag (R05.4):** a finger that rests on a tile for 350 ms lifts it
(`useTouchLiftDrag` + `TouchLiftGesture` in `lib/editing/media/touch-drag.ts`; a swipe before that still scrolls, a vibration confirms the lift, a chip follows
the finger). The tile announces `lift/move/drop/cancel` on a window event and `TimelineCanvas` answers with the same target maths and `onPoolDrop` as a mouse
drop, drawing `pool-drop-lane` (blue allowed, red wrong kind) beside the indicator. The tile `⋯` menu (Place choices) and the source-viewer buttons stay.
**Delete bin, Undo (R05.3):** the server hard-deletes a deleted bin's pins (no `deleted_at` on `media_bin_items`), so there is no server undelete; `BinTree`
snapshots the subtree and its pins before deleting and the "Bin deleted. Undo" toast re-creates it (`lib/editing/media/bin-restore.ts`: parents first, new ids,
same names, smart rules and pins; a pin whose source is gone is skipped and said so). **Panes (R05.8):** the bins and details panes resize (separator with
arrow keys, Shift for big steps, Home/End, Enter hides; drag with mouse or touch) and collapse (`Hide bins` / `Hide details`), saved per device in
`localStorage` (`reelforge.editor.poolPanes.v1`, `lib/editing/media/pool-panes.ts`); separators and toggles are 44 px on coarse pointers. The metadata panel
draws only rows that have a value and names the hidden ones in one line. Annotations save on change (switches and colour at once, note and keywords after a
600 ms pause), with a small Saving/Saved line, plain error text and "Try again"; there is no Save button. The Edit workspace's left panel gets a first **Media** tab (default on desktop and tablet; a phone keeps
Markers and has the pool in the Media workspace).

Probe width/height are the DECODED dimensions: `FfprobeOutputParser` swaps them exactly when ffmpeg's autorotate transposes, derived from the raw Display Matrix (`displaymatrix` ints, ffmpeg's `-round(theta)` then `fabs(theta-90)<1` / `fabs(theta-270)<1`), so 90.7 degrees stays coded and 90.4 swaps. The legacy mkv/webm `tags.rotate` is never applied (ffmpeg does not autorotate it), and a missing matrix means no swap; a phone portrait stored as coded 1920x1080 + Display Matrix 90 probes as 1080x1920.

Segmentation never sends a sideways or an over-size clip to the vendor. When the source changes pixels on decode (rotation or mirror, `FfprobeOutputParser.AltersPixelsOnDecode`) or is over `SegmentationLimits.VendorSourceMaxBytes` (100 MB), `SegmentationTrackingService` first makes a **vendor copy** (`SegmentationMaskArgvBuilder.BuildVendorCopyArgs`: autorotate on, video only, no scaling, every frame kept, metadata cleared so no Display Matrix survives, H.264 CRF 20 under a VBV ceiling sized to the 100 MB budget), checks its geometry and size from ffprobe, stores it at `projects/{p}/agentFiles/segmentation/vendor1/{sourceHash}.mp4` (cached by source hash, never transcoded twice) and presigns THAT. A copy still over 100 MB is never stored or sent (`SEGMENTATION_EGRESS_UNAVAILABLE`, refunded). The mask content key hashes `upright1:{sourceHash}` for a rotated source, so masks cached from a coded upload miss; an unrotated source keeps its own hash (same frames, same size), and an unrotated source within the budget keeps the same key, URL and hash (no transcode). The earlier near-lossless `upright/` mezzanine is no longer written or read: it measured 4.5x its source and was never size-checked.

| Piece | Location |
|---|---|
| Authoritative contract | `inference/src/ReelBolt.Shared/Editing/EditTimeline.schema.json` |
| C# document records | `ReelBolt.Shared/Editing/` (`EditTimeline.cs`, `Item.cs`, `Keyframed.cs`, `Provenance.cs`) |
| Validator (server trust boundary) | `ReelBolt.Shared/Editing/EditTimelineValidator.cs` |
| v1 manifest → v2 document seeder | `ReelBolt.Shared/Editing/EditTimelineSeeder.cs` |
| REST surface | `ReelBolt.Inference.Api/Controllers/EditTimelinesController.cs` |
| TypeScript mirror + edit engine | `web/lib/editing/` (`types.ts`, `time.ts`, `engine/`) |
| Editor store, transport, selection, markers, edit index, sync client, preview compositor | `web/lib/editing/{store,transport,selection,markers,edit-index,sync,preview}/` |
| Editor UI | `web/components/editor/{shell,timeline,viewer,panels,sync}/`, `EditorWorkspace.tsx` (a thin component over `workspace/useEditorController` and `workspace/view/*`, see "How the workspace code is laid out"), routes under `web/app/(app)/editor/[id]/` |

The TypeScript mirror is hand-written, so `web/lib/editing/__tests__/schema-drift.test.ts` asserts
it against the JSON Schema — the item-type discriminator sets must match in both directions, and
the fixtures are validated with ajv. **Never edit one side without the other.**

---

## How the workspace code is laid out

`web/components/editor/EditorWorkspace.tsx` is the component the timeline page renders. It is about twenty lines:
it calls `useEditorController` and hands the result to `useEditorView`. Everything else lives in
`web/components/editor/workspace/`.

| File | What it holds |
| --- | --- |
| `types.ts` | `EditorWorkspaceProps` and the controller's input (the props with defaults applied) |
| `useEditorController.ts` | calls the domain hooks below in a fixed order; each receives what the earlier ones produced |
| `useWorkspaceCore.ts` | what is read from the store, save state, playback statistics, device class, audition, grade preview |
| `usePreviewDocument.ts` | the flattened preview document, the timeline's view of it, this timeline's renders |
| `usePreviewSources.ts` | proxies, readiness, stabilised copies, the playback plan, camera conversion, LUTs |
| `usePreviewParity.ts` | track flags, what the preview shows exactly, clip badges, marker rows |
| `useEditDispatch.ts` | dispatch with a readable refusal; the In/Out marks |
| `useTransport.ts` | seek, speed, play ranges, the audition, the beat grid, the Events lane, snapping |
| `useTimelineEdits.ts` | markers, undo and redo, gaps, links, trims, the blade, the clip menu's actions |
| `useKeymapChords.ts` | the user's keymap and the chords shown in the interface |
| `usePageLayout.ts` | panel sizes, saved layouts, switching pages |
| `useShellChoice.tsx` | which shell shows (Simple or Full), the Simple shell's place and panel view, the ways from Simple into the full editor |
| `useCommands.ts` | the Source monitor, the command context, the one keydown listener, the palette |
| `useTimelineCallbacks.tsx` | what the canvas and the track headers call back into |
| `useClipAndViewerActions.ts` | save before a render, restore a version, set a clip property, viewer tools, stills, scope statistics |
| `usePoolPlacement.ts` | insert, overwrite, add to end, place on top, place over, drop, extract audio |
| `useTimelineZoom.ts` | zooming the timeline |
| `useWorkspacePanels.tsx` | relink options, the selected clip, background jobs on clips, the AI tools, the busy-edit guard |
| `useShellState.ts` | the frame zones of the Media and Deliver pages, the import result sheet, the phone's actions |
| `helpers.ts`, `live-panels.tsx` | module-level helpers; the panel wrappers that follow the playhead on their own |
| `view/useEditorView.tsx` | calls the view hooks in order and returns the shell |
| `view/useSimpleView.tsx` | the Simple shell: picks the side panel's body and hands the pieces to `simple/SimpleWorkspace` |
| `view/useSidePanelsView.tsx`, `useViewerZoneView.tsx`, `useTimelineZoneView.tsx`, `usePagesView.tsx`, `useChromeView.tsx`, `useEditorShellView.tsx` | the markup: side tab groups, the viewer, the timeline pane, each page's frame content, header and dialogs, and the phone or desktop shell |

Rules that keep the split honest:

- The controller hooks hold all state and effects. The view hooks return elements, keep no state and call no hook
  except `useT`.
- Every file that shows text binds its own translator with `const tx = useT('…')`. The catalogue tests read those
  bindings per file, so a translator passed in from another file would make its keys look unused.
- A ref that crosses a file boundary is named `…Ref`; the lint needs the name to know it is a ref.
- A hook's context type is the previous hook's context plus its own return value (`XCtx = PrevCtx &
  ReturnType<typeof useX>`), so adding a value means returning it from the hook that owns it.
- Tests that read source text (`viewer-color-wiring.test.tsx`, `surface-headers-empty.test.tsx`) point at the file
  that now holds the code.

The split was mechanical: same hooks in the same order, same element tree. The context passed between pieces is
still wide, and the phone surface is still assembled in `useEditorShellView`, which also picks the surface: the
phone's, the Simple shell's (see "The Simple shell") or the full editor's.

## The Simple shell

The editor has two shells on a desktop and a tablet: **Simple** and **Full**. Both are views of the same
`useEditorController`: one store, one document, one preview session, one set of 34 ops. The shell is how the
editor is laid out on this device. It is never written to the timeline, a review link, an export or a hand-off, and
no route, op, document field, render or billing rule knows it exists. The phone has its own surface and no choice.
Design record: `docs/design/editor-simple-mode.md`. Customer page: `docs/user-guide/editor/simple-and-full.md`.

**Which shell opens** (`web/lib/editing/shell-choice.ts`, `workspace/useShellChoice.tsx`):

| Signal | Shell |
| --- | --- |
| A choice stored on this device (`reelbolt:editor:shell` = `simple` or `full`) | that one, at once |
| Tablet, no stored choice | Simple |
| Desktop, no stored choice, saved editor preferences empty (no remapped key, no saved layout) | Simple |
| Desktop, no stored choice, preferences not empty | Full |

A desktop with no stored choice waits for `GET /api/v1/editor/preferences` (or its failure, or
`PREFERENCES_WAIT_MS` = 2 s) before it shows either shell: the frame renders `data-shell="pending"` and nothing
else, so one shell is never swapped for the other. The first answer is kept for the visit. A default is not a
choice: nothing is stored until the user uses the switch. The first visit shows one dismissible line
(`reelbolt:editor:shell-intro-seen`).

**The switch** (`simple/ShellSwitch.tsx`) is in both headers. Switching keeps the selection, the playhead, undo
history and unsaved edits (they live in the store and the controller, which stay mounted). The markup is rebuilt,
so the viewer starts a new preview session, as it does today between Edit and Colour.

**What Simple shows** (`simple/SimpleWorkspace.tsx` is the frame; `view/useSimpleView.tsx` fills it):

| Zone | What it is |
| --- | --- |
| Header | back, the timeline's name, the save state (there is no status bar), the "Early preview" label, results waiting for an OK, the switch, keyboard shortcuts, Export |
| Left | three places (Edit, Look, Export) over the media pool: the same `mediaWorkspace` node as the Edit page's Media tab |
| Centre | the viewer zone (the same `viewerPicture` and `viewerTransport` nodes), the ask line, the timeline (the same `timeline` node) |
| Right | one panel. Look and Export are places; on Edit it is the task list, the selection, or one task's own panel with a Back button |

The places change the right panel only. The viewer and the timeline do not move or remount between them.

**No tool is new.** Each panel body is one the editor already had:

| Panel | Body |
| --- | --- |
| A selected clip | `phone/ClipSheet` `ClipSheetBody` (fit or fill, position, size, rotate, opacity, speed, volume, fades, object effects, enhance, follow), with Split here, Duplicate and Delete above it |
| A selected title | `phone/LookSheet` `TitlesSection` |
| Look | `phone/LookSheet` `LookSheetBody` with `panel` (no titles section, no "on desktop" note) |
| Export | `deliver/DeliverWorkspace` with `simple`: the film, the presets, the cost statement, Render, the queue, and "More export options" (opens the full Deliver page) |
| Captions | `panels/SubtitlesPanel` |
| Sound | `phone/AudioSheet` |
| Shape and frame rate | `panels/TimelineSettingsPanel` |
| Suggestions | `panels/ProposalPanel`, opened with `initialAgent` and `initialBrief` |

The task list (`simple/SimpleTasks.tsx`) only opens things: Remove the silences and Cut to the music open their
dialogs; Add captions, Add a title, Fix the sound and Change the shape open a panel; Tighten the edit opens
Suggestions on the Re-cut helper. A task that needs a clip is off on an empty timeline.

**The ask line** (`simple/AskBox.tsx`) picks one of the five helpers and takes the words. It only opens the
Suggestions panel with both filled in: the estimate, the hourly allowance and the disclosure are shown there, and
nothing is sent or charged until the user presses Ask there. It is the proposals flow
(`POST …/proposals`), not the dashboard assistant, which has no tool that edits a timeline.

**The AI tools button** sits in the side panel's header in every place and always reopens Suggestions
(`data-testid="simple-ai-tools"`, `aria-pressed` while it shows). Picking Look or Export from the rail replaces the
panel body, never the way back: once opened, Suggestions stays mounted but hidden (`SimpleWorkspace` `aiPanel` /
`aiActive`), so a draft or a pending suggestion list survives a visit to Look or Export. The layout is a grid
(rail | centre | panel), so no left menu draws over the panel. The Full shell's right panel is a drawer on a narrow tablet; its toggle sits above the drawer, so it stays reachable.

**Work made in the full editor is never hidden.** `simple/full-editor-work.ts` lists what a clip carries that
Simple has no control for (colour steps other than a look, the five fixes and Fix exposure; a mask, a keyer,
effects, a corner pin, stabilisation, a reframe, a crop, reverse, a freeze frame, animation). The clip panel says
it in one line with "Open in the full editor", which switches the shell and opens the page where that work is
done. An item type Simple does not edit (adjustment, compound, multicam, generator) gets the same line.

**The trimmed pieces** are flags on the existing view hooks, not copies:

| Piece | In Simple |
| --- | --- |
| Timeline toolbar | select, blade, undo, redo, split at the playhead, delete, snapping, zoom. Trim modes, edit-separately, the clip menu, frame steppers, close gaps, remove silence, cut to the music, events, select and add are not shown |
| Track headers | name and the switches; no add, remove, rename, reorder or view options |
| Transport (`TransportBar` `simple`) | start, step back, play, step forward, end, timecode, shuttle speed. Marks, edit and marker jumps and loop keep their chords |
| Viewer options | whole screen and the preview menu; no still, no grade switch (Look has "Show the original") |
| Viewer tools | the direct transform bar only; no Source or Record switch, viewing LUT, mask or reframe tools, Preview 3 s |
| `ProgramViewer` `quiet` | no size readout; the parity badge only when what is on screen is not exact; a "frames skipped just now" pill only while frames were skipped in the last 3 s (`preview/recent-drops.ts`) |

The dropped-frame counter is "always shown" in the full editor's status bar. Simple has no status bar: it shows the
recent count on the picture while it is happening, and nothing when playback is smooth.

**Keyboard** (`SIMPLE_DECISIONS` in `lib/editing/commands.ts`): an exhaustive `Record<KeyAction, 'works' |
'opensFull' | 'off'>`, so a new keymap action does not compile until someone decides. `works` runs as in the full
editor (`pageEdit` and `pageDeliver` move between Simple's own places). `opensFull` asks the host: the command
palette switches to the full editor and runs the action there; a key press shows a notice with "Open the full
editor" and changes nothing, so a slip never moves the user. `off` (the Source or Record switch and the five
placement actions) does nothing, and the palette says "Only in the full editor."

**One page underneath.** While Simple shows, `ui.workspace` is `edit`. A link or a remembered page that points at
Deliver or Colour opens the Export or Look place, and the page underneath returns to Edit.

**Tests.** `components/editor/__tests__/simple-shell.test.tsx` renders the real workspace in both shells: which
shell opens, the places, the panel following the selection, the tasks, Export, the ask line asking nothing by
itself, the switch keeping selection and playhead, and a ceiling on the interactive controls shown at rest
(`SIMPLE_CONTROL_CEILING`, raised only on purpose). `lib/editing/__tests__/shell-choice.test.ts` and
`commands-simple.test.ts` pin the rules. Tests about the full editor store `reelbolt:editor:shell = full` first.
E2E: `e2e/fixtures/editor-shell.ts` stores `full` before any page loads (the `editorShell` option of the
editor-v2 fixture, default `full`); `tests/editor-v2/31-simple` runs with `editorShell: 'simple'` on the desktop
and tablet projects.

**Not built yet** (recorded so nobody assumes it): picking the helper from the user's words; storing the choice
on the account; a media list simpler than the pool; resizable panels in the Simple frame; a single viewer host
shared by Edit and Colour in the full editor.

## The document model

The schema pins `schemaVersion` to `const: 2`, so a v1 document shape is not representable at all.
The C# `EditTimeline` record carries a `[JsonConstructor]` overload that always sets
`SchemaVersion = 2`; the field is not something a caller chooses.

**Every enum in the document travels as a camelCase string, and only as a string.** The five
contract enums — `TrackKind`, `AspectPolicy`, `InterpKind`, `ProvenanceOrigin`, `AssetRefKind` —
each carry a type-level `[JsonConverter(typeof(EditTimelineEnumJsonConverter))]`
(`ReelBolt.Shared/Data/Models/Enums.cs:411`, `:424`, `:438`, `:457`, `:472`), and that converter is
`JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)`
(`EditTimelineEnumJsonConverter.cs:44`). So `"aspectPolicy": "fit"` is the only accepted spelling,
matching `EditTimeline.schema.json`, and an integer body — `"aspectPolicy": 0` — is a **binding
failure**: `[ApiController]` answers the endpoint's ordinary `400` problem response, the action is
never entered, and nothing is written. It is never a `500` and never a silent accept of an ambiguous
number. The declaration lives on the enum types rather than in a `JsonSerializerOptions` on purpose:
an options-level converter outranks the type-level attribute, and
`EditTimelinesController.DocumentJsonOptions` (`EditTimelinesController.cs:143`) deliberately carries
no converter — a naming-policy-less `JsonStringEnumConverter` there would silently override the
attribute and keep persisting PascalCase. One place defines the encoding.

**This is a breaking change for a client written against the integer form.** It is not breaking for
stored data: reading is still case-insensitive (`JsonStringEnumConverter`'s own behaviour), so a
legacy row already stored as PascalCase — `"kind": "Video"` — still **reads back** and round-trips.
No migration is needed, and such a row simply converges on the contract the next time a `PATCH`
rewrites it.

### Timeline and settings

| Field | Type | Notes |
|---|---|---|
| `schemaVersion` | `2` | Fixed. A document that omits it reads as `2`; any other value is a `400` at `schemaVersion`. |
| `id` | uuid | The timeline's own identity. |
| `projectId` | uuid | The owning project. Set from the route on create, never from a seed manifest. |
| `name` | string | User-facing; required and non-blank on create. |
| `settings` | `EditTimelineSettings` | See below. |
| `tracks` | `Track[]` | Compositing order. |
| `markers` | `Marker[]` | Frame-ordered. |
| `inOut` | `EditRange?` | Optional `{ inFrame, outFrame }` playback range. Default `null`. |
| `seed` | `EditTimelineSeed?` | Optional `{ executionId, version }` naming the workflow execution whose manifest produced this document. Default `null`. |

`EditTimelineSettings` is `width`, `height`, `rate` (a `Rational`), `sampleRate`,
`pixelAspect` (a `Rational`), `aspectPolicy` (`fit` | `fill` | `stretch`) and
`startTimecodeFrames`. A **blank** timeline (created without a seed) is 1920×1080, `30/1`,
48 000 Hz, square pixels, `fit`, timecode starting at frame 0, with one empty `Video` track and one
empty `Audio` track.

The blank document's track ids come from `EditTimelineSeeder.TrackId` — the seeder's own
deterministic label → GUID function — so a blank timeline and a seeded one address their video track
by the *same* id. The labels are `V1` (video) and `A1` (audio); `A1` can never collide with a seeded
audio track because the seeder only ever emits `A2` (sfx) and `A3` (music).

### Tracks

A `Track` carries `id`, `kind` (`video` | `audio`), `name`, `color`, `height`, `locked`, `enabled`,
`mute`, `solo`, `busId` and `items`. Within a track, **items are sorted by `recordIn` and never
overlap** (`previous.recordOut <= next.recordIn`); gaps are legal and meaningful. A `locked` track
rejects every op that names it — directly by `trackId`, or indirectly through an item that lives on
it.

In a seeded document the six v1 track labels map to fixed names and kinds: `V1` → `Video` (video),
`V2` → `Inserts` (video), `V3` → `Graphics` (video), `S1` → `Captions` (video), `A2` → `SFX` (audio),
`A3` → `Music` (audio). The GUID is a name-based hash (SHA-256 over a fixed namespace plus the
label, first 16 bytes), so the same label yields the same id in every process and every run.

### Items

`Item` is a JSON-polymorphic union discriminated on `type`. Every member carries `id`, `name`,
`recordIn`, `recordOut` and an optional `provenance`. Ids are opaque and unique document-wide.

| `type` | Kind | Own fields |
|---|---|---|
| `mediaClip` | `MediaClip` | `assetRef` (**required**), `sourceIn` (default 0), `playbackRate` (default 1.0, must be > 0), `enabled`, `gainDb` |
| `titleClip` | `TitleClip` | `text` (required), `fontSize` (48), `fontFamily` (`Arial`), `color` (`#FFFFFF`), `enabled` |
| `generatorClip` | `GeneratorClip` | `generatorType` (required), `generatorConfig`, `enabled` |
| `insertClip` | `InsertClip` | `assetRef` (optional), `quad` (a keyframed `QuadCorners`), `enabled` |
| `caption` | `Caption` | `text` (required), `language` (`en`), `enabled` |
| `transition` | `Transition` | `transitionType` (required), `easing` (`linear`), `enabled` |
| `compoundClip` | `CompoundClip` | **stub — `TODO(P8)`**, no own fields |
| `multicamClip` | `MulticamClip` | **stub — `TODO(P7)`**, no own fields |
| `adjustmentClip` | `AdjustmentClip` | **stub — `TODO(P8)`**, no own fields |

The last three are the schema's **documented stubs**. Their `type` literal exists so a document
containing one round-trips through the contract unchanged, but nothing consumes their contents yet:
they are not a nested-item container, not an angle manager and not a layer effect. The engine
treats them accordingly — see the `setProperty` allowlist below, which gives them `name` only.

### Keyframed values and keyframes

`Keyframed<T>` is `{ value?: T, keys?: Keyframe<T>[] }`: `value` is the base/default used when there
are no keyframes or before the first one, and `keys`, when present, overrides it from each
keyframe's frame onward. A `Keyframe<T>` is `{ frame, value, interp }` where `interp` is one of
`linear`, `easeInQuad`, `easeOutQuad`, `easeInOutQuad`, `easeInCubic`, `easeOutCubic`,
`easeInOutCubic`, `step`.

Two places use it today, both on `mediaClip`/`insertClip`:

- **`MediaClip.gainDb`** — the clip gain in dB. The split is the one a DAW uses: `value` is the
  clip's static gain, `keys` a time-varying envelope that overrides it. The seeder keeps the two
  independent — neither is derived from the other, and an item the manifest gives no gain keeps a
  null `gainDb` rather than a defaulted one. The single forced exception: `Keyframed<double>.Value`
  cannot express "absent", so a ducking envelope present with no static bed level is emitted with
  the identity `0` dB, which the envelope then overrides from its first key.
- **`InsertClip.quad`** — a keyframed `QuadCorners` (four `QuadPoint`s in source-frame
  coordinates), corner-pinning an insert onto a tracked region. v1 records corner positions only,
  so every seeded keyframe is `linear`.
  **Rendered (export and viewer agree):** the insert's media is stretched to the canvas-shaped layer and
  pinned into the quad with `perspective` (destination sense) on a transparent layer, overlaid in track
  order. A quad whose corners (base and every keyframe) are all within 0..1 is canvas fractions, anything
  else is document-canvas pixels; coordinates clamp to -1..2 of the canvas. The media plays from its own
  start at the timeline rate; the insert has no opacity, crop or fades. Its `matte` (the same keyer a media clip
  uses) is keyed out of the picture before the warp (`InsertLayerBuilder.Chain`). An insert with no media or no
  quad is left out and named in `skippedItems`.

### Provenance

`Provenance` describes how an item came to exist: `origin` (`manual` | `generated` | `imported` |
`analyzed`), `editedByUser` (required), and the optional `stepResultId`, `agentDefinitionId`,
`providerId`, `offeredId`, `rationale`, `agentName`, `providerName`.

**Ids and names are populated by different paths, and neither is derived from the other.** The
three GUID fields are filled by v2-native creation paths, which hold the entities themselves. The
two display-name fields are filled by a **v1 seed**: a `TimelineManifest` records only the agent's
and the provider's *names* and no ids, and a pure seeder has no database in which to look an id up.
A name is therefore copied verbatim when the source carried one and left `null` when it did not —
never synthesized, and never resolved from the other field.

### AssetRef

`AssetRef` is `{ kind, projectFileId?, stepResultId? }` with `kind` of `projectFile` or
`stepResult`.
It is the **one place a timeline names an entity that lives outside itself**, which is exactly why
it is the thing the validator polices hardest (see [the trust boundary](#the-trust-boundary)).
`mediaClip.assetRef` is required; `insertClip.assetRef` is optional (an insert may be backed by a
composed asset rather than a file reference).

### Markers and InOut

A `Marker` is `{ id, frame, name, color?, note?, durationFrames? }`. The contract orders markers
**by frame only** — two markers sharing a frame have no canonical order. That single fact is what
forces `addMarker`'s optional `index` (see [the deviations](#accepted-engine-deviations-contract-not-defects)).

`inOut` is an optional `{ inFrame, outFrame }` playback range. `seed` is an optional
`{ executionId, version }` recording which workflow execution's manifest seeded the document.

---

## The time model

**All positions are integer frames on the OUTPUT clock** — the record clock, so `recordIn`/
`recordOut` describe where an item lands in the finished program, not where it sits in its source.
The source offset is a separate field (`mediaClip.sourceIn`). There is no seconds field and no
floating-point position anywhere in the document.

**Record spans are half-open: `[recordIn, recordOut)`.** An item ending at frame 100 and the next
starting at 100 are adjacent, not overlapping — which is precisely the adjacency the transition
rule and the ripple rules key off. The validator requires `recordOut > recordIn` on every item,
i.e. a strictly positive extent; a zero-length span is not a legal item.

**The frame rate is a rational, `Rational { num, den }`.** This is not decoration: the NTSC rates
29.97 and 59.94 are `30000/1001` and `60000/1001`, and a double cannot represent them. The v1→v2
seeder resolves a manifest's `fps` against a table of exact well-known rates (23.976 → `24000/1001`,
24, 25, 29.97 → `30000/1001`, 30, 50, 59.94 → `60000/1001`, 60, matched within ±0.01 so a rounded
`23.98` still lands on its exact rational). A rate matching none of them falls back to
`round(fps × 1000) / 1000` — deliberately *not* a continued-fraction approximation, which would
return an exact-*looking* rational (e.g. `2997/100`) whose precision is implied rather than
measured. The fallback is recognisable: its denominator is always 1000.

### Timecode conversion (`web/lib/editing/time.ts`)

`framesToTimecode(frames, rate)` and `timecodeToFrames(tc, rate)` are exact inverses for every
integer frame count `n >= 0` at any rate, including fractional ones.

- **Non-drop-frame only.** The output is `HH:MM:SS:FF`, counted at a constant integer frame base —
  the `:` before the frame field is the conventional mark of non-drop-frame. Drop-frame notation
  (`HH:MM:SS;FF`) is deliberately **not** supported: the timeline contract counts frames, so a
  dropped-frame label would be a second, disagreeing source of truth for the same value.
- **The base is `Math.round(rate.num / rate.den)`**, the same convention `lib/utils/timecode.ts`
  already uses for its `fps` argument — so `30000/1001` (29.97) works on a 30-frame base. A rate
  that is missing, zero, negative or non-finite falls back to 30; the base is never below 1.
- **Malformed input clamps to 0; it never throws.** `timecodeToFrames` returns `0` for a wrong
  segment count, a non-numeric segment, or a field outside its range (`mm`/`ss` > 59, `ff >= base`),
  rather than throwing or returning a partially parsed value. `framesToTimecode` treats a
  non-finite or negative frame count as `0` and floors a fractional one, since a timecode label can
  only address whole frames.

The module is standalone on purpose: it does not import `lib/utils/timecode.ts`, which is
seconds-based and has no notion of the `Rational` contract.

---

## The op catalogue: thirteen ops, closed

Every edit is one of these **thirteen** P1 ops, or one of the twelve P4 ops listed in
[Core editing and rendering (P4)](#the-twelve-p4-ops). There are thirteen P1 ops, not twelve — the
plan text says "exactly these twelve ops" and then names thirteen; the engine implements the
thirteen it named, and the thirteen are what the P1 contract is. The catalogue is still **closed**:
twenty-five members and nothing else, and every inverse program draws only from those twenty-five.

| Op | Payload | What it does |
|---|---|---|
| `insert` | `trackId`, `index`, `item` | Splices `item` at its canonical array index, then ripples the following items when it lands flush at the next item's start. |
| `overwrite` | `itemId`, `item` | Replaces the *content* of the addressed item. `item.id` must equal `itemId`. Any span is allowed as long as it overlaps nothing. |
| `lift` | `itemId` | Removes the item and leaves the gap. |
| `extract` | `itemId` | Ripple-delete: removes the item, then shifts the following items left by its duration when it was adjacent (`gapAfter === 0`) and not at all when it was already followed by a gap. |
| `trim` | `itemId`, `edge` (`in`\|`out`), `mode` (`ripple`\|`roll`\|`slip`\|`slide`), `deltaFrames` | Moves one edge, clamped to the invariants (fades still fit, a reversed clip at most 1800 frames, `sourceIn` >= 0 at the clip's own rate and direction). A ripple on the `in` edge keeps the clip's start and moves the following items by the opposite of the delta. |
| `blade` | `itemId`, `atFrame`, `newItemId` | Splits an item strictly inside its span. Left half keeps the id; right half takes `newItemId` and advances `sourceIn` (by `round(head × playbackRate)`; on a reversed clip the LEFT half's moves instead, on a held clip neither) when the kind carries one. The cut stays hard: fades are kept on the outer edges and clamped to each half. |
| `deleteGap` | `trackId`, `atFrame` | Deletes the gap containing `atFrame` by shifting every later item left by its length. |
| `closeGaps` | `trackId` | Removes every interior gap, preserving item order and the first item's own `recordIn`. The trailing space after the last item is not an interior gap and is untouched. |
| `move` | `itemId`, `toTrackId`, `toRecordIn` | Moves an item (keeping its duration) to another position and/or track, leaving a gap behind. |
| `setProperty` | `itemId`, `key`, `value` | Sets one allowlisted property. |
| `addMarker` | `marker`, `index?` | Adds a marker, spliced so the marker list stays frame-ordered. |
| `setMarker` | `markerId`, `patch` | Patches a marker's fields. `frame` may only move within the slot its neighbours allow. |
| `removeMarker` | `markerId` | Removes a marker by id. |

### The union is closed

`EditOp` is a closed TypeScript union in `web/lib/editing/engine/ops.ts`. An op that is not a
member cannot reach the engine at all — TypeScript rejects it, and `apply` has an exhaustive switch
whose `default` arm is a `never` assignment. So "which edits exist" is answered in exactly one
place, and there is no half-implemented member to find.

Every op is **pure**: `apply` clones the document up front, never writes to the argument or to
anything reachable from it, and the inverse program it returns never aliases the returned document
(removed items are detached from the clone before being embedded in the inverse).

### What is deferred

Deliberately absent, and therefore impossible to reach — not stubbed, not partially implemented:
**retime**, **keyframe ops**, **transition ops**, **track-structure ops**, **link/group**,
**compound ops** and **applyProposal**. A deferred op is a new ticket, not a variation of this one.

### The inverse is a program, not an op

`apply(doc, op)` returns `{ doc, inverse: EditOp[] }` — a *program*. Ten of the thirteen are exactly
self-inverse under one op, but three cannot be: `blade` must both drop the right half **and** restore
the left half's original span, and `deleteGap`/`closeGaps` translate an unbounded number of items.
Those three invert to a short program drawn **only from the same thirteen named ops**:

- `blade` → `lift` the right half, then `overwrite` the left half back to its original span (2 ops).
- `deleteGap` / `closeGaps` → one `move` per affected item, emitted **rightmost first**, so every
  destination is already free when its move runs.

`applyAll(apply(doc, op).doc, inverse)` deep-equals `doc` for every op, and every intermediate
document satisfies `assertInvariants` — undo/redo and optimistic concurrency both rest on that.
The inverse is always non-empty except for `closeGaps` on a track that has no gaps, which genuinely
changed nothing and so has nothing to undo.

### `setProperty`'s allowlist

`setProperty` may address only these keys, per item kind:

| Item kind | Settable keys |
|---|---|
| `mediaClip` | `name`, `enabled`, `playbackRate`, `gainDb` |
| `titleClip` | `name`, `enabled`, `text`, `fontSize`, `fontFamily`, `color` |
| `generatorClip` | `name`, `enabled` |
| `insertClip` | `name`, `enabled` |
| `caption` | `name`, `enabled`, `text`, `language` |
| `transition` | `name`, `enabled` |
| `compoundClip`, `multicamClip`, `adjustmentClip` | `name` only |

The three stubs carry no `enabled` field, so setting one there is rejected like any other unknown
key. `undefined` is a meaningful value, not a gap in the type: it **removes** the key, which is what
lets `setProperty`'s inverse restore a property that was absent before (a `Keyframed` gain, say)
without a dedicated delete op in the catalogue.

**But the allowlist is not the only check a clear has to pass.** `undefined` on a key the document
invariants *require* is **refused**, before the write, with an `EditEngineError` whose `op` is
`setProperty` (`apply.ts:958-964`). The rule is stated by an exported set beside the allowlist —
`REQUIRED_PROPERTY_KEYS` (`ops.ts:316`) — which today holds `name` alone, and holds it because
`assertItemShape` refuses an item whose `name` is not a string and `name` is on **every** kind's
allowlist. Honouring the clear there would hand back a document the engine may no longer edit, so
the two ways to make the conflict go away — deleting the field, or loosening the invariant to admit a
nameless item — would each trade a loud refusal for a silently corrupt document. This is a medium-severity QA finding. Every other
allowlisted key (`enabled`, `text`, `fontSize`, `fontFamily`, `color`, `language`, `playbackRate`,
`gainDb`) is optional to the invariants, so clearing it stays supported and keeps the op invertible.

**`setMarker` is held to the same shape invariant.** A patch may set any marker field and `undefined`
clears it, so the post-patch marker is checked by `assertMarkerShape` (`apply.ts:1072`) before the op
returns. That is what makes `setMarker { patch: { name: undefined } }` a refusal rather than a
nameless marker — the marker contract has the same shape rule `addMarker` and `assertInvariants`
already enforce. That is QA finding **F3** (low), same report. `apply` cloned the document up front,
so neither refusal can reach the caller's document.

Both rules serve one promise, the same one `insert`'s partial-overlap refusal serves: **an op cannot
*introduce* a violation** (`apply.ts:31`). Both came out of QA's review of the engine and shipped
together in a single fix (`6df773ce`, "refuse to clear a required field, and validate markers"); each
is pinned by tests under `web/lib/editing/engine/__tests__/`.

### What the engine refuses

The engine refuses an op when performing it would leave it holding a document it cannot honestly
undo. `insert` is one: it accepts exactly two positions relative to the following item, and
**throws `EditEngineError` for anything in between** — a partial overlap, where the inserted item
runs `0 < delta < duration` past where the next item starts. See accepted deviation 2 below for why. So
are the `setProperty` clear and the `setMarker` shape check described just above, and so is `setMarker`'s
refusal of a `frame` move that would pass a neighbour (accepted deviation 4 below). Everything else the
engine refuses
is the same error type: an unknown id, a locked track, a non-canonical index, a wrongly-typed
property, a document that violates an invariant. `EditEngineError.op` names the op that was being
executed, so a UI can point at the offending edit rather than just a message.

### Ops never produce a document the server refuses (T-013)

`assertInvariants` is the TS half of `EditTimelineValidator` plus the server's model binding, kept in
lockstep through the shared contract corpus (`web/lib/editing/__fixtures__/contract/*.json`, run by
vitest `engine/__tests__/contract.test.ts` and xUnit `EditTimelineContractCorpusTests`). Beyond the
structural rules it refuses: any id the server binds as a `Guid` that is not one (timeline, project,
track, item, marker, sequence and sequence track, colour group and member, bus, grade node, asset and
provenance ids); a keyframed channel whose static `value` is `null`; fades longer than their clip;
a reversed clip longer than 1800 frames; a clip both reversed and held; a transition not flush with
a neighbour; a picture-only item (title, caption, generator, insert, adjustment) on an audio track;
a `schemaVersion` other than 2 (a missing one reads as 2, as on the server; `null` is refused); a
negative `recordIn`. `assertInvariants(doc, { ids: 'opaque' })` skips
only the Guid shape, for unit fixtures; production callers (sync client, offline queue, proposals)
use the strict default.

- **Ids.** Every id the editor mints comes from `newGuid()` in `web/lib/editing/ids.ts`: an RFC 4122
  v4 UUID built from `crypto.getRandomValues`, so it works on a plain-HTTP origin where
  `crypto.randomUUID` does not exist (Math.random only if `getRandomValues` is missing too). Add
  track, Make compound's sequence tracks, markers, blade halves and every panel's `newId` use it.
- **First keyframe.** The first `setKeyframe` on an untouched channel writes the channel's identity
  as the static value (`gainDb` 0, `scale`/`opacity` 1, position/rotation 0), or keeps an existing
  numeric one; never `null`. A Latch pass on a track with no fader produces the same shape.
- **Source arithmetic.** Blade, trim (ripple, roll, slide) and slip move `sourceIn` by
  `round(frames × playbackRate)`; on a reversed clip `sourceIn` is the frame at the OUT edge, so a
  tail trim moves it and a head trim does not; a held clip's `sourceIn` never moves. Blade keeps the
  cut hard (left fade-out and right fade-in become 0) and clamps each kept fade to its half; a bladed
  clip in a colour group puts its right half in the same group. The rate in that arithmetic is
  clamped to the renderer's [0.1, 10] (`RegionFilterGraphBuilder.Min/MaxPlaybackRate`), so a stored
  1e-12 or 1e6 moves the source like 0.1 or 10 and a trim always finishes.
- **Clamps and refusals.** A trim stops where both fades still fit and where a reversed clip reaches
  1800 frames; `retime`, `fitToFill` and `setProperty` that would break a fade, reverse or hold rule
  are refused.
- **Ripple in-edge trim** trims the head in place: the clip keeps its start, and the items after it
  move earlier when the head shortens and later when it lengthens.
- **Keyframes follow their clip.** Nudge, move, ripple, close-gaps and slide shift a clip's keys
  (and a multicam clip's cuts) with it.
- **Transitions.** An edit that would leave a transition off its cut (delete, nudge, move of a
  neighbour) removes the transition in the same undo step; an edit that keeps it flush (ripple delete,
  a head trim of the incoming clip) keeps it on its new neighbour. A transition with a neighbour on
  one side only is valid. Undo restores the transition exactly (the whole track when two transitions
  sit back to back).

---

## The sync protocol

The four P1 endpoints are the write-path contract and are byte-for-byte unchanged since P1. P3 added
three read-mostly companions — `history`, `revisions/{rev}` and `duplicate` — on the same ownership
rules, without perturbing the four's observable step order (`EditTimelineStepOrderTests` pins it
over a real Kestrel). P4 added `POST …/render` and `GET …/renders` (see
[Core editing and rendering (P4)](#core-editing-and-rendering-p4)); export/import (P6) and
proposals (P8) are still absent; adding one is a new ticket.

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/api/v1/projects/{projectId}/edit-timelines` | Owner | Lists the project's timelines, newest-updated-first, as lightweight summaries (`id`, `name`, `revision`, `seedStepResultId`, timestamps) — deliberately without the document. |
| `POST` | `/api/v1/projects/{projectId}/edit-timelines` | Owner | Creates a timeline: blank, or seeded from a step result's timeline manifest. Writes a **revision-1 snapshot** at birth (P3), so the original document is always restorable. A name with a control character is `400` here, on `duplicate` and on `import` (a name read from the OTIO file falls back to "Imported timeline" instead): Postgres refuses NUL (`22021`) and C0 characters break the FCPXML export. Likewise a name longer than 200 characters after trimming (the `varchar(200)` column) is a `400` with a plain message on create, `duplicate` and `import` (a file name that long falls back to "Imported timeline"). |
| `GET` | `/api/v1/projects/{projectId}/edit-timelines/{id}` | Owner | Returns one timeline's current document and revision. |
| `PATCH` | `/api/v1/projects/{projectId}/edit-timelines/{id}` | Owner | Applies one revision: replaces the document, advances the revision, appends the caller's ops verbatim. |
| `GET` | `…/edit-timelines/{id}/history?limit=&before=` | Owner | (P3) The op log newest-first — each entry's `revision`, `createdAt`, the stored `ops` **verbatim**, and `hasSnapshot` — plus `currentRevision` and `restorableRevisions`. Projects the row (no `document_json` read). |
| `GET` | `…/edit-timelines/{id}/revisions/{rev}` | Owner | (P3) The document at `rev`: `source: "current"` from the live row, or `"snapshot"` from `edit_timeline_snapshots`. A revision with no stored document is `404` carrying `restorableRevisions` — **the server never replays ops**. |
| `POST` | `…/edit-timelines/{id}/duplicate` | Owner | (P3) Copies the current document into a new timeline at revision 1 (fresh id inside the document, the source's `seedStepResultId`, its own revision-1 snapshot). `201`. Without a name the copy is called "<source name> (copy)", the source's name shortened so the whole still fits 200 characters. |
| `DELETE` | `/api/v1/projects/{projectId}/edit-timelines/{id}` | Edit | Soft-deletes the timeline (`DeletedAt`, and advances the revision so a racing `PATCH` is `404`): `204`. A concurrent `PATCH` or rename makes it re-read the row and delete the fresh one; only a timeline that keeps moving under it twice answers `409` with `currentRevision` (retry the delete). It then vanishes from list, get, history and renders (all `404`); its op log and snapshots stay, and its renders' hidden `Render:` workflows follow the project's existing cascade. Unknown, another project's, another tenant's or already deleted: `404`. |
| `PUT` | `…/edit-timelines/{id}/name` | Edit | Renames: `{ "name": "…" }`, trimmed, then 1..200 characters with no control character (Unicode Cc: NUL, line breaks, tabs, escape codes) or `400` at `name`. The row's `name` changes and the stored document's `name` mirrors it, but the **revision does not advance** and no op row is written, so an open editor's next `PATCH` from the same base still applies. Answers the `GET` shape. A concurrent `PATCH` makes it re-read and re-apply (up to three times, then `409` with `currentRevision`); a timeline deleted meanwhile is `404`, never a `409` at revision 0. |

**Trash and version history (Wave 1, decision D14).** Deleting is still a soft delete; the timeline
stays restorable for `Editing:TrashRetentionDays` (default 30):

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| `GET` | `…/edit-timelines?state=deleted` | View | The trash: this project's deleted timelines inside the window, newest deletion first, each summary plus `deletedAt` and `purgesAt` (= `deletedAt` + retention). The default list never includes them. |
| `POST` | `…/edit-timelines/{id}/restore` | Edit | Brings the same row back (name, document, revision, comments kept): `200` summary. `404` when it is not deleted, was purged, is past the window, or belongs to another project or tenant. |
| `GET` | `…/edit-timelines/{id}/versions` | View | `{ versions: [{ revision, name?, cause, createdAt, expiresAt?, createdByName? }] }`, newest first: every stored snapshot that has not expired. This is the same set `revisions/{rev}`, `history.restorableRevisions` and render accept, so each listed version restores through the ordinary restore `PATCH`. |
| `POST` | `…/edit-timelines/{id}/versions` | Edit | Saves the **current** revision: `{ name, cause?: "named" \| "beforeProposal" }` → `201`. `named` (default) needs a name (trimmed, 1..80 characters, no control characters, else `400` at `name`) and never expires; `beforeProposal` is an automatic checkpoint with an optional label. A revision that already has a snapshot is reused, not duplicated. |
| `PUT` | `…/edit-timelines/{id}/versions/{revision}/name` | Edit | Names or renames a version (same name rules); naming makes it permanent. `404` for an unknown or expired version. |
| `DELETE` | `…/edit-timelines/{id}/versions/{revision}/name` | Edit | Un-names: the version becomes an automatic checkpoint and expires `Editing:AutoCheckpointDays` from now (revision 1 never does). |

`cause` is one of `auto`, `idle`, `beforeRestore`, `beforeProposal`, `beforeRender`, `milestone`,
`duplicate`, `import`, `named`. Retention rule (D14): **automatic checkpoints are kept 30 days**
(`Editing:AutoCheckpointDays`), **named versions and revision 1 (the origin) are kept for good**, and at
most 200 automatic checkpoints are kept per timeline (the oldest go first). The server takes checkpoints
itself: `idle` (a `PATCH` that arrives 2 minutes or more after the previous revision snapshots that
revision), `beforeRestore` (a `PATCH` whose ops carry the restore marker snapshots the revision it
replaces), `beforeRender` (a render pins its revision with a snapshot) and `milestone` (every 20th
revision, plus revision 1 at creation; `duplicate` and `import` snapshot revision 1 of the copy). One
snapshot per revision: an existing one is never replaced. Two hosted services enforce the windows:
`TimelineTrashPurgeService` (hourly; hard-deletes timelines past `Editing:TrashRetentionDays` with their
ops, snapshots and comments; idempotent) and `TimelineSnapshotPruneService` (daily; deletes expired
automatic checkpoints, never a named version or revision 1). Existing snapshots were migrated to
`cause = milestone` and expire 30 days after they were taken, except revision 1.

**Restore is not an endpoint.** The client fetches `revisions/{rev}` and submits that document as an
ordinary `PATCH` (with a `{"type":"restore","revision":N}` marker in `ops`, which the server stores
opaque like everything else). That produces revision N+1 and keeps the log append-only; history is
never rewritten. Snapshots exist at revision 1, every 20th revision, idle / before-restore / before-render
checkpoints, named versions, and the current row (see the retention rule above), so the
server-side history is a set of restore points; the fine-grained history is the client's
own undo stack (see P3 below).

**The Versions panel (T-008, `RevisionHistoryPanel`).** It reads `GET …/versions` (`listEditTimelineVersions`) and shows only restorable points, so no row
is greyed. Rows are grouped by editing session (a gap of 30 minutes or more starts a new one, `groupVersions` in `panels/versions.ts`), newest first, e.g.
"Today 14:02-14:40". Each row shows the plain cause (`causeLabel`: Auto-saved checkpoint, Before you restored, Before a render, Before an AI suggestion,
Named version; revision 1 reads Original version), the name, who (`createdByName`) and `retentionText` ("deleted in N days" from `expiresAt`, "kept forever"
for a named version or revision 1). **Save a named version now** (`POST versions {name, cause:'named'}`, 80 characters, 44 px high) and per row **Name this
version** / **Rename** (`PUT …/name`) / **Remove the name** (`DELETE …/name`, the point becomes an auto checkpoint with an expiry). **Restore** opens a
confirmation that names the version and says the current state is kept as "Before you restored"; confirming fetches `revisions/{rev}` and hands it to
`onRestore`, which replaces the store document (the session's undo history starts again) and sends the restore PATCH, so the server keeps the replaced
revision as `beforeRestore` (one point per revision: a revision that already has one is not snapshotted again). Accepting an AI proposal first calls
`POST versions {cause:'beforeProposal'}` (a failure never blocks the edit). Storage bounds are the server's.

**AI suggestions are metered, capped and disclosed (R11.1).** `POST …/proposals/estimate` (same body as `…/proposals`) answers the render-estimate shape plus
`included` (the workspace's own provider answers: no credits), `modelLabel`, `disclosure` and `used`/`limit`; it calls no model and does not touch the cap. Asking
holds credits before the model runs (`402 INSUFFICIENT_CREDITS`, nothing sent) and settles at what the call metered. At most `EditorProposals:MaxPerHourPerUserProject`
(default 20) suggestions per user and project per rolling hour; over it `429 PROPOSAL_RATE_LIMITED` with `retryAfter` and a plain message (the cap is per API
replica). An unusable answer answers `424 PROPOSAL_FAILED` (was 502; Cloudflare replaces 502/504 bodies) and is still charged for the tokens it used (`creditsUsed`, floored at the hold when usage is unpriced) and uses a cap slot; only a call that metered nothing (a provider error before any tokens) is released and refunded. The response and
the estimate carry `disclosure` (`itemCount`, `rangeSeconds`, `soundFileCount`, `briefChars`, `transcriptSeconds`, a plain `summary`): counts only, never ids.

**Suggestions run as background jobs (recut-timeout hotfix).** A model call can outlast nginx's 60 s default and Cloudflare's 100 s
origin limit, so the web asks with `POST …/proposals?background=true`: every gate above runs synchronously (404, 409, 429, 402, the
hold and the hourly slot), then the call continues on a background task and the route answers `202 {jobId, state: "running"}` with a
`Location`. `GET …/proposals/jobs/{jobId}` (Edit) answers `202` while it runs and then exactly the status and body the synchronous
route would (200 proposal, `424 PROPOSAL_FAILED`, `424 PROPOSAL_TIMEOUT`, `503 no_chat_provider`, `409 MODEL_UNPRICED`); an unknown,
expired or another user's/project's/timeline's job is one `404`. Bounds (`EditorProposals:*`): `TimeoutSeconds` 300 (then the call is
cancelled, the hold released, `424 PROPOSAL_TIMEOUT`), `AbandonAfterSeconds` 45 (a running job nobody polls is cancelled and
released; a finished answer nobody reads within 45 s is dropped and released), `ResultRetentionSeconds` 600 (a result with nothing to charge), `DeliveredRetentionSeconds` 60 (a delivered or released one),
`MaxRunningPerUser` 3, `MaxJobs` 500 (running plus reserved, store-wide) and `MaxStoredPerUser` 10 (oldest delivered/released evicted
first), all reserved atomically before the hold; over a cap `429 PROPOSALS_BUSY`. **Charge on delivery:** a background answer keeps its
hold open and is settled by the first `GET` that returns it, under the job's own lock, so delivery and release never both happen to one
job; an answer released before anyone collected it answers `410 PROPOSAL_EXPIRED` ("You were not charged") and never returns its body.
The store is in memory (`EditProposalJobStore`; one Inference API replica, like the hourly cap); a restart cancels running jobs and
releases their holds. Without the flag the route stays synchronous for tabs loaded before the change, bounded at
`SyncTimeoutSeconds` 75 (`424 PROPOSAL_TIMEOUT`, released; below Cloudflare's 100 s with room for the pre-work) under a 120 s nginx location. `requestEditProposal`
(`web/lib/editing/sync/api.ts`) polls every 2 s and still resolves an older server's direct 200. A reload does not resume polling
yet: the tray marks the request interrupted and the server cancels it after 45 s, uncharged.
The Assist panel (T-022) calls `…/proposals/estimate` (debounced; on agent, range, brief change and after each ask) and shows the price (`about N credits`; nothing when
billing is off; "Included" when `included`), `used`/`limit` ("12 of 20 suggestions left this hour"), the disclosure sentence and the model; Ask is disabled when `!affordable`
or the cap is reached. 424/429/503 are worded by `lib/editing/proposals/assist-text.ts`. Each proposed change has Accept (that one), Skip, and a checkbox for "Accept checked
changes" (one `dispatchBatch`, one undo step; user-owned changes still need their acknowledgement). Comment cards (`ReviewPanel`, editor tab and review page) have
jump, edit (PATCH body), resolve/reopen and delete.

**Phone (T-014).** The phone header's More menu lists *Assistant* (the proposal sheet above), *Ask the Assistant* (the platform assistant, which on a phone is a full-height sheet that lifts above the keyboard), Review, Review page, History, *Drag clips with your finger (beta)*, *Open full view* and *Copy link*. Dialogs and the hub read one phone rule (`useIsPhoneClass`, under 600 px); the Captions sheet's More tools hides Takes.

**Ownership is the outer gate, project scoping is the inner one.** Every endpoint loads the project
and returns `404` when it does not exist and `403` when it exists but belongs to somebody else.
Every `{id}` lookup additionally constrains `ProjectId == projectId` and inherits the soft-delete
query filter, so a timeline that is in another project, soft-deleted, or nonexistent is `404` in
all three cases and the body never distinguishes them.

### PATCH carries `baseRevision` plus the full document

`PATCH` takes `{ baseRevision, ops, document }`. The **document is the complete post-edit
document**, not a delta; `ops` is the client's edit operations and is **opaque to the server** —
serialized verbatim into `edit_timeline_ops.ops_json` and never applied server-side. The engine
lives in TypeScript on the client. An absent or null `ops` stores `[]`.

### A stale `baseRevision` is a `409` with `currentRevision`, and no write

If the stored `Revision` does not equal the request's `baseRevision`, the endpoint returns
`409 Conflict` with `{ "currentRevision": <n> }` — the revision the caller must rebase onto — and
**nothing is written**: no entity is modified, no row is added, no transaction is opened.

The server is protected **twice, on purpose**. The explicit `Revision != BaseRevision` pre-check
catches the ordinary stale writer before any work happens; the `DbUpdateConcurrencyException` catch
behind it catches the writer that raced between that check and the commit (`Revision` is an EF Core
concurrency token, so the UPDATE carries `WHERE id = @id AND revision = @original`). **Both paths
answer `409` with the same body shape and neither writes** — a client treats them identically and
simply rebases onto `currentRevision`. On the race path the current revision is re-read with
`AsNoTracking`, precisely because the tracked entity still carries this request's rejected edit.

The race has **two shapes, and both are a `409`, never a `500`.** Two writers that pass the pre-check
on the same base either collide on the concurrency token (the loser's UPDATE matches no row:
`DbUpdateConcurrencyException`), or — on Postgres, where the loser's op/snapshot insert waits on the
winner's commit — on the `(timeline_id, revision)` unique index of `edit_timeline_ops` /
`edit_timeline_snapshots` (SQLSTATE `23505`, surfaced as a `DbUpdateException`). Both are caught and
answered with the same `{ "currentRevision": <n> }` body; before this was handled the second shape
escaped as a bodiless `500` (audit d1 N2). `EditTimelinePatchRacePostgresTests` pins it against a real
Postgres (`REELBOLT_TEST_PG`): 20 parallel PATCH pairs on one base, exactly one `200` and one `409`
per pair, zero `500`.

**A `DELETE` racing a write is never a `500` either.** The soft delete advances the revision too (the
row is hidden from then on, so nothing reads that revision), which makes both orders collide on the
concurrency token. A `PATCH` or rename that commits between the delete's read and write makes the
delete re-read the fresh row and delete it once more: the delete wins, `204` (a timeline another
delete already removed is `404`). A `PATCH` that loses to a committed delete finds no current
revision to rebase onto and answers `404`, not `409`. The same Postgres test class pins both orders.

A **missing `baseRevision`** (it binds as `0`) or any value below `1` is not a conflict — there is no
revision 0 to rebase onto — so it is a `400` at `baseRevision`, not a `409`.

### The order is the contract, not an implementation detail

The steps run in this order, and the order is observable:

1. **whole-body size ceiling → `413 Payload Too Large`**, enforced by Kestrel through
   `[RequestSizeLimit]` while the body is still arriving — **before** the action body runs, and
   therefore before either lookup below (measured under a real Kestrel by
   `EditTimelineStepOrderTests`, with a control proving the route was reached: an over-ceiling body
   aimed at a nonexistent project answers `413`, not `404`)
2. project lookup (`404`) and timeline lookup (`404`)
3. **serialized document or `ops` past its own ceiling → `413`, no write**
4. missing or non-positive `baseRevision` → `400` at `baseRevision`, no write
5. **stale `baseRevision` → `409` with `currentRevision`, no write**
6. a null document → `400`; then the header check (document `id`/`projectId` must be this row's,
   `400` at `id`/`projectId`) together with `EditTimelineValidator` → one `400` with **every**
   violation, no write
7. one transaction: document + revision + op row (+ snapshot), commit
8. `DbUpdateConcurrencyException` or a `(timeline_id, revision)` unique violation from the racing
   writer → the same `409`

Both 413 steps run before the 409 check, and the null-document 400 runs *after* it — so a stale
caller is always told to rebase first, whatever else is wrong with their payload, while an oversized
`ops` blob is rejected **before** the stale-revision comparison rather than after it. (Earlier
revisions of this page listed the transport `413` after the lookups; that was imprecise, not a
contract change — the P1 action code is byte-identical.)

**Three ceilings, and only one of them is a pre-parse control.** `MaxDocumentBytes` is
`8 * 1024 * 1024` (`EditTimelinesController.cs:69`) and `MaxOpsBytes` is `1024 * 1024` (`:85`); the
third, `MaxRequestBodyBytes` (`:100`), is their sum plus 64 KiB of JSON-envelope headroom. That
third one is the real bound, because `[RequestSizeLimit]` on the action (`:358`) sets Kestrel's
`MaxRequestBodySize`: Kestrel stops reading an over-limit body while it is still arriving and answers
`413` before MVC binds, allocates or deserializes anything. The two per-part checks are **post-parse
defence in depth** — by the time either runs, the body has already been bound, so what they prevent
is *persisting* an oversized value, not *reading* it. They are what makes the transport ceiling a
bound on what the server actually accepts rather than a blunt byte count.

Each 413 body names the ceiling it exceeded — `maxDocumentBytes` (`:384`) or `maxOpsBytes` (`:396`)
— so a client can size its next attempt without guessing. The `ops` blob is serialized exactly once
(`:377`), so the bytes measured are the bytes written; re-serializing at the write would be a second,
unmeasured blob free to differ from the one the guard approved. A timeline document is a bounded
JSON structure (the validator caps it at 64 tracks × 5000 items), so anything past the ceiling is a
client defect or an attack, not an edit.

### Creating a timeline

`POST` takes `{ name, seedStepResultId?, settings? }`. `settings` (R01.3) is an `EditTimelineSettings` used for a BLANK timeline only (a seeded
one keeps the manifest's settings): size 16 to 8192 px per side, a frame rate above 0 and at most 120, positive pixel aspect, non-negative start
timecode. A value outside that is a `400` `{message}` and nothing is stored; absent keeps 1920x1080 at 30 fps. `name` is required and non-blank (whitespace is
rejected, not trimmed to empty).

With no `seedStepResultId`, the blank document described above is created at revision 1.

With a `seedStepResultId`, the step result's `TimelineStorageKey` manifest is downloaded and mapped
through `EditTimelineSeeder`. The seeder is **pure** — no I/O, no database, no clock, no random — so
the same manifest and the same asset resolver always produce the same document. The route's project
id and the caller's name win over the manifest's own: a manifest cannot plant a document claiming to
belong somewhere else. Asset identifiers resolve through this project's `ProjectFile` rows indexed
by `StorageKey` **and** by `OriginalFileName` — the manifest references video by storage key and
audio by file name — and an identifier that does not resolve drops that item rather than inventing
a reference.

Asset identifiers also resolve through `SeedAssetResolver`, which looks up this project's
`ProjectFile` rows and this project's step results by `OutputStorageKey`; when both match, the
`ProjectFile` wins. So a source that is a render of this run or of an earlier run of the same project
resolves as step-result media. The manifest carries the source's identity in two optional
`TimelineSource` members, `ProjectFileId` and `StepResultId`, plus `TrimInSec`, the start of a trimmed analysis window (span times are relative to it, so the seeder adds it to the clip's source-in). `VideoAnalyze` writes them for
`ProjectFile` and `StepResult` sources, and `TimelineRender` writes them too. `PreviousStepOutput`,
`StepOutput` and `GeneratedClips` sources carry neither, so a trimmed item from one of those cannot be
placed.

**Unplaced items.** The `201` response (`EditTimelineResponse`) carries `unplacedItems`, a list of
`{ track, itemId, label, reason }` for each item the seed dropped. `label` can be `null`. `reason` is
`source_unresolved` (a cut's source clip is not an asset of this project) or `asset_unresolved` (an
insert, graphic, sfx or music asset is not an asset of this project) or `censored_stretch_withheld` (a window of the manifest's `censor` track: `track` is `"censor"`, `itemId` the censor item's id). The entries hold no storage keys. `GET` and `PATCH` omit the
field. The web editor shows a yellow note with the counts.

**Censored stretches never seed uncensored.** The plain seed carries no obscuring, so it withholds the
picture instead: each `censor` window is widened outward to whole frames (start rounds down, end gets the
manifest's half-millisecond rounding plus one frame, as `LineageSeeder.WidenCensorWindows` does) and every
`V1` clip is trimmed or split so no original footage sits inside it, leaving a hole. A split clip's source
in-point advances with its start and a clip whose seam moved loses its outgoing transition. Camera audio is
left as the manifest has it. Each censor item is reported with reason `censored_stretch_withheld`. The
lineage seed (`LineageSeeder`) falls back to the compile output for a whole cut whose level enables
generated clips and also censors or applies region effects (`generated_clip_censored`), because the
original must not be replayed under a cutaway the timeline cannot cover. A region effect (`regionEffects`
on the seed step's compile settings) is not in the manifest, so it cannot be windowed: when the lineage
seed does not run, or its document fails validation, and the seed step has region effects (or settings
that cannot be read), `POST` refuses the plain fallback with `409` and code `SEED_WOULD_REVEAL_CENSORED`
and creates nothing. A lineage seed that succeeds is unchanged. The guard judges the settings the RUN used (the step of the run's
snapshot definition with the same order; the live step only for a run that has no snapshot). It also covers a seed
from an editor render (a `TimelineRender` step result): that render's manifest drops masks and region effects, so
the guard reads the timeline the render drew (its pinned revision; an unpinned render is judged on the current
document and every stored revision) and refuses when any clip, inside nested sequences too, carries a mask, a region
effect or a blur or pixelate effect, when an adjustment clip blurs or pixelates, or when that document cannot be read. A render of a timeline with none of those
still seeds as before. An original-source insert dropped over a censored window is listed as unplaced too.

**Camera audio.** A v1 manifest has no per-cut audio item, yet the footage's own sound is part of the
derush. So each `video` cut whose source the ingest probe measured **with an audio stream**
(`media_derivatives.source_probe_json` → `hasAudio: true`, see
[the media section](#probe-at-ingest-the-stored-probe)) also gets a companion `MediaClip` — same
asset, same `recordIn`/`recordOut`/`sourceIn`, same provenance, its own deterministic id — on an
`A1` **"Camera audio"** audio track placed right under `V1`. There is no link between the two clips:
moving one does not move the other. Preview and render play the companion. A source no derivative
run has probed yet falls back to the `VideoAnalyze` artifact's `sources[]` entry for its storage key
(the newest artifact a `VideoAnalyze` step wrote in the seed's own execution, else in the project;
the step type is joined, never inferred from the key, because the compile step's EDL shares the
`video-analysis/` prefix, is written later and has no `sources[]`): a named `audioCodec`
means sound, a `videoCodec` with no `audioCodec` means silent. The stored probe always wins over the
artifact, and the artifact is read only when some source has no stored probe. Nothing is assumed for
a source that is silent, or that neither the probe nor the artifact describes, or whose artifact
cannot be read (a warning in the log; the seed still succeeds): no companion, no `A1` track. The
seeder itself stays pure — the controller hands it a `sourceHasAudio(storageKey)` lookup built from
one query over the stored probes plus, when needed, that one artifact (`AnalysisAudioFacts`).

The seeded path's failure modes are deliberately distinguishable:

| Condition | Response |
|---|---|
| step result missing, or in another project | `404` (indistinguishable from nonexistent) |
| step result carries no timeline manifest (`timelineStorageKey` null) | `400` |
| manifest cannot be fetched or read | `424 Failed Dependency` with a fixed plain message — a storage failure, not a client error. Never 502 (Cloudflare replaces a 502/504 body); the manifest's storage key and the exception are logged (`LogWarning`), never returned |
| the **server-built** document fails validation | `500`, with the violations — the caller did nothing wrong |

---

## Never losing an edit (Wave 1)

The autosave `PATCH` is the only thing that makes an edit durable on the server. Everything in this section
is about the gaps around it. It lives in `web/lib/editing/sync/` (`journal.ts`, `tab-presence.ts`,
`sync-client.ts`, `offline-queue.ts`) and `web/components/editor/sync/` (`SyncNotices.tsx`,
`ReadOnlyBanner.tsx`); the contract is: **an edit is either saved, kept on this device, or in a file the user
was handed - never silently dropped.**

### Sign-out, expiry and unchecked lengths (T-023)

- **Sign-out in any tab.** A deliberate sign-out records `reelbolt:editor:signed-out-at` (a timestamp, no user data), broadcasts on
  `BroadcastChannel('reelbolt:editor:signout')` (storage event as fallback) and wipes the journal and offline queue. Every other editor tab
  releases its IndexedDB connection (so the database delete is not blocked; the journal also closes on `versionchange`), wipes, stops
  journalling and shows the sign-in prompt; a tab that missed the message still refuses its next write because the durable record is newer
  than its session start. A 401 / expiry is NOT a sign-out: it keeps the journal so the user can sign back in. A tab whose cookie user
  differs from its session user never journals.
- **Expiry.** Entries older than 30 days are never offered and are evicted when the journal opens; the journal also keeps at most 25
  entries (oldest evicted first).
- **Length not checked.** When a source's duration is unknown the validators still fail open (no trim is refused) but report the clip:
  `uncheckedMediaClips` (TS) and `EditTimelineValidator.UncheckedMediaClips` (C#) return the same `tracks[i].items[j]` paths
  (corpus fixture `source-unknown-duration-unchecked`, field `unchecked`). The timeline paints a small "Length not checked" note on such clips.

### The edit journal (R02.2)

One entry per timeline - `{ baseRevision, baseDoc, localDoc, ops, savedAt, tabId }`, the offline queue's shape plus
the tab - written to two places:

- **localStorage** `reelbolt:editor:journal:{userId}:{timelineId}`: synchronous, so a tab killed within one task still has
  it. It carries the whole entry up to about 200 KB (`SYNC_POINTER_LIMIT`); a bigger document leaves only a pointer.
- **IndexedDB** `reelbolt-editor` / store `journal`, keyPath `[userId, timelineId]` (database version 2; the old per-timeline store is dropped): the async `put` is *started* inside the
  page-hide handler (the database is opened ahead, so the transaction begins in the same task).

It is written 250 ms after an edit (so a tab that dies without any lifecycle event still has it), synchronously on
`pagehide` / `visibilitychange` to hidden / `beforeunload`, and whenever a save fails (401, 4xx, exhausted
retries, conflict). It is cleared when the server confirms the revision. Writes to one timeline are chained, so a
late `put` never resurrects a cleared entry.

**On load** (`SyncClient.checkRecovery()`, after the offline queue): an entry the server already holds is dropped
silently; one newer than the server is offered in a non-blocking banner, "We recovered edits from your last visit",
with **Restore** (rebased onto the server's current document by the same reconciler as the offline queue, then
saved) and **Discard**. Opening never writes anything.

**One send guard (T-014).** Every path that talks to the server (autosave, retry, keepalive / pagehide / unload,
reconnect, offline-queue and journal replay, save-as-copy, resume) first compares the account in the browser with the
account the session was opened as. A different account sets the different-account dialog; an unreadable or signed-out
account (`anonymous`, even on both sides) never matches and shows "sign in again". Nothing is sent, nothing is kept for the
new user. Journal and queue entries are keyed per user (`reelbolt:editor:offline-queue:{userId}:{timelineId}`); old
entries without a user in the key are deleted on sight. Signing in as a different account with nothing pending reloads the
editor for the new identity.

**Truth rule:** if neither store works (`journal.probe()` is `'memory'`) the chip adds "Edits are held in memory
only". The editor makes no promise it cannot keep. A failed IndexedDB write (quota, blocked) flips the journal to
`'memory'` too, so a failure is never silent.

**Per user, wiped on sign-out:** the journal and the offline queue hold whole unsaved documents, so every entry
carries a `userId` (the signed-in email, lower-cased). `checkRecovery()` and `restoreQueued()` ignore and delete an
entry written by another user, or by an older version that did not say (no `userId`). An explicit logout
(`useAuth().logout`, `lib/api/auth.ts logout`) removes every `reelbolt:editor:journal:*` and
`reelbolt:editor:offline-queue:*` localStorage key and deletes the `reelbolt-editor` IndexedDB database
(`wipeEditorLocalData`, best effort, never blocks the logout). A `401` / session expiry is not a logout: the journal
stays for the same user.

### A session that ends mid-edit (R02.2)

A `401` is its own state, `authExpired`: saving pauses, nothing is lost (the edit stays in the store and the
journal), the chip says "Sign in again to keep saving", and a dialog (a bottom sheet on a phone) signs the user in
**in place** with the app's own `POST /api/v1/auth/token` through `lib/api/auth.ts` - no navigation, the editor state is
kept. On success `SyncClient.resumeAfterSignIn()` flushes - unless a **different account** signed in: then the
pending edits are not sent under the new identity. `SaveState.accountChanged` opens a dialog ("You signed in as a
different account. The unsaved edits belong to <email> and were not sent.") with "Download them as a recovery file"
or "Discard them"; either way the editor then reloads as the new user without the old edits.

### Conflict recovery (R02.1)

A `409` no longer ends in a frozen tab. `SyncClient` first tries an **online rebase** through
`reconcileQueue` (the same proof-based reconciler as the offline queue: both op logs must replay exactly,
touch disjoint parts and commute): the server's document is read at `currentRevision`, its op log since the base is
read from `history`, and when the edits commute the rebased document is saved against the server's revision. The
user sees only a toast, "Your edits were merged with changes from another tab or device." Edits made while the
server was being read are folded in, not lost.

When the edits overlap (or the server's log cannot be read) the store freezes and a dialog offers:

- **Keep my copy** - "Save my edits as a new timeline": `POST …/duplicate` named "<name> (my edits)", then a `PATCH`
  of the copy (`baseRevision` 1, `ops: []`) with this tab's document and the copy's id. No new endpoint, no
  backend change.
- **Use the latest** - downloads a recovery file (`<name>-my-edits.json`, the journal entry) first, then drops the
  local edits and reloads.

**Two tabs of one browser** find each other over a `BroadcastChannel` per timeline (`tab-presence.ts`). The later
tab becomes `otherTab` (read-only, banner "Open in another tab" and **Edit here instead**); taking over asks the
editing tab to flush and let go (`takenOver`, read-only with a plain banner) and reloads the taker from what that
tab saved. Without `BroadcastChannel` there is no detection; the rebase above still protects every write.

### The save chip (R02.3)

`describeSaveState` is the one place the chip's words come from. One plain sentence per failure class, never a
revision counter, op name or id: "Saved", "Unsaved changes", "Saving...", "Can't reach the server. Trying again...",
"You are offline. N changes will save when you are back", "Sign in again to keep saving", "Not saved. The server had a
problem." (with **Retry**), "Not saved. This timeline is too large to save...", "Not saved. You no longer have
permission to change this timeline.", "Not saved. This timeline was deleted.", "Not saved. The server refused this change.
<plain reason>" (the reason is `refusal.ts` / the server's violations turned into words), "Not saved. This timeline was
changed somewhere else.", "Open in another tab", "View only". `SaveState.problem` (`serverError`, `tooLarge`,
`noPermission`, `deleted`) tells the classes apart without new statuses. **Retry** is a 44 px target on a phone and is
hidden where it cannot help (deleted, no permission).

### View-only open (R02.5)

`GET {id}` answers `canEdit` (added by the backend lane; the client reads `canEdit?: boolean`, undefined meaning
`true`). With `false` the sync client is built with `canEdit: false`: the store is read-only from the first frame,
`attachOps` is ignored, nothing is ever `PATCH`ed, journalled or recovered, and the page says "You can look but not
change this timeline." The same `readOnly` plumbing that the 409 state uses blocks Delete, undo/redo, Split, History
restore, Remove silence and every panel edit; `isReadOnlySave(save)` is the single predicate.


## The trust boundary

**`EditTimelineValidator` is the only thing the server trusts.** Nothing else in the write path
inspects the document: the controller parses it into the typed records, hands it to the validator,
and — if the validator is clean — persists it. The validator is pure: no I/O, no database, no clock.
It is given the caller-supplied lookups it needs as plain dictionaries, because `ReelBolt.Shared`
has no `DbContext` and must never query a database.

It returns **every** violation, in document order, never just the first. Each violation is a
`(Path, Message)` pair where `Path` is a precise dotted/indexed locator (e.g.
`tracks[0].items[2].recordOut`), so a `400` body tells the caller exactly which field to fix.

### Every `AssetRef` must resolve inside the timeline's own project

This is the point of the whole type. An `AssetRef` is the one place a timeline names an entity that
lives outside itself, so it is the one place a caller could smuggle a reference to another project's
file or step result into their own timeline. Every ref must resolve, through the ownership maps, to
the timeline's own project **and nowhere else**. Getting it wrong is a cross-tenant data leak, not
merely a bad document — so it mirrors the trust boundary the `VideoCompile` artifact endpoint
already enforces.

The ownership maps are built **without a project filter**, and that is the point: the question being
answered is "who really owns this id", not "does this project own it". An id belonging to another
project must resolve to *that other* project so the validator can reject it; filtering the query by
project would silently turn every foreign reference into "unknown" — still rejected, but for the
wrong reason, and the maps would be blind to the very leak they exist to catch.

A ref is a violation when it is null, internally inconsistent (both `projectFileId` and
`stepResultId` set, or the id the `kind` does not call for missing), unknown to the ownership map,
owned by a different project, or carries an `AssetRefKind` the validator does not support
(`EditTimelineValidator.cs:447`). That last branch is unreachable through the endpoint today — an
integer `kind` fails model binding before the action runs (see
[the document model](#the-document-model)), and the enum has only two values — but the validator
does not assume the endpoint is its only caller: it rejects an unsupported kind **however the value
got there**, rather than letting it fall through as valid.

**`Provenance.StepResultId` is held to exactly the same rule.** A provenance id is collected into
the same `StepResultOwners` map the asset refs use (`EditTimelinesController.cs:665-668`) — collecting
only the asset refs would leave every provenance id *absent* from the map, so the validator would
report each one as unknown instead of genuinely checking who owns it — and `ValidateProvenance`
(`EditTimelineValidator.cs:317-344`) applies the identical test: unknown to the map is a violation,
owned by another project is a violation, and because `Provenance` is optional, a null `provenance` or
a null `stepResultId` is simply not checked rather than defaulted into one.

**`AgentDefinitionId` and `ProviderId` are deliberately NOT validated**
(`EditTimelineValidator.cs:302-314`). They name rows in global, non-project-scoped catalogues —
`agent_definitions` and `inference_providers` carry no `ProjectId` column at all — so any project may
legitimately name any agent or provider and "does this id exist?" would be a referential-integrity
check with no cross-tenant boundary to protect. They are also write-only today: nothing on the server
reads either field. **That is a property of the current consumers, not of the fields.** A future
consumer that starts acting on either one must resolve-and-check it at its own point of use — where
it also gets to decide what "unknown" should mean — and a **retention pruner** that deletes step
results must revisit this validator's assumptions at the same time, since the ids it deliberately
leaves unchecked are exactly the ones that would dangle. `OfferedId` is the same kind of thing: an
opaque analysis-scoped token that is not a row id in any table.

### Missing ownership is a violation; missing DURATION is not

The validator's two lookups answer different kinds of question and are treated asymmetrically **on
purpose**:

- **Ownership** — a missing entry **is** a violation. Ownership is a security property held in a
  database the caller can always consult, and "unresolvable" must never be read as "trusted".
- **Duration** — a missing entry is **not** a violation. The source-extent check
  (`sourceIn + (recordOut - recordIn) <= assetDurationFrames`) can only ever *prove* an overrun;
  the absence of a duration means "not yet known", not "provably wrong", so the clip degrades to
  **unvalidated** rather than to a rejection.

The asymmetry is forced by what is available today: no caller in this phase can populate the
duration map at all. `ProjectFile` has no duration column, the `MediaDerivative` table has no writer
yet, and the Inference API has no ffprobe — so treating "unknown" as a violation would reject every
real `MediaClip` and make the whole validator unusable. The endpoint therefore passes an
**intentionally empty** duration map. A clip whose duration *is* supplied still gets the full
overrun check, so a future phase that starts populating durations re-arms the check with no change
to the validator.

In one line: **an unresolvable asset *identity* is rejected; an unresolvable asset *length* merely
goes unchecked.**

The overrun check is skipped when the ref itself is untrustworthy: a foreign or unknown ref is
already reported, and piling a second violation on top would bury the security-relevant one in noise.

### Structural limits and ordering

| Rule | Bound |
|---|---|
| Tracks per document | at most 64 |
| Items per track | at most 5000 |
| Keyframes per `Keyframed<T>` | at most 256 |

Every item's `recordIn` must be `>= 0` (the record clock starts at frame 0). A picture-only item —
a title, generator, insert, caption or adjustment clip — cannot sit on an `Audio` track: there is
nothing for it to play there. A `MediaClip` stays legal on an audio track, where it means the file's
sound (the seeder's music bed, a camera file's audio from an OTIO import).

Per track, items must be **sorted by `recordIn`** and **non-overlapping**. A `Transition` is
special-cased: it must sit **exactly at the boundary** between its neighbours (`recordIn` equals the
predecessor's `recordOut`), which is checked instead of the overlap rule. Once a pair is out of
order the overlap test is skipped for that pair — adjacency is meaningless in an unsorted list, and
reporting both would fabricate a second violation for a defect already reported once.

### Overlay text

`TitleClip` and `Caption` text must survive `OverlayTextSanitizer.Sanitize` with **something
renderable left over**, because the render path sanitizes again and drops an overlay whose text
sanitizes away — a clip whose text is null, whitespace-only, or composed entirely of characters the
allowlist strips is a document that cannot produce the output it claims.

Text that merely *contains* such characters (an apostrophe, `%`, `:`, an emoji) is **not** a
violation: real ASR captions contain apostrophes, and rejecting them would be a functional defect
rather than a hardening. There is deliberately no "text must equal its sanitized form" rule.

### Disabled items are still validated

Every item is checked regardless of its `Enabled` flag. The document's geometry is the contract, and
a disabled item that has since been corrupted still has to survive a round trip through the editor
before it can be re-enabled.

---

## Accepted engine deviations (contract, not defects)

Four places where the engine's implemented behaviour differs from the task text that specified it.
All four were raised with reasoning, accepted by the program lead, and are recorded in
`decisions.md`. They are **the contract**: QA treats a rejection as a rejection (not a failure), and
docs describe the behaviour below rather than the text that preceded it. Each is pinned by tests in
`web/lib/editing/engine/__tests__/`.

### 1. `apply` returns `inverse: EditOp[]`, not a single op

`apply(doc, op)` returns `{ doc, inverse: EditOp[] }` — a program. **Forced:** `blade` must drop the
right half *and* restore the left half's span, and `deleteGap`/`closeGaps` translate an unbounded
number of items, while compound *forward* ops are deferred and there is no merge/shift primitive in
the catalogue. **Only the same thirteen named ops may appear in a program, in either direction** —
length 1 for ten ops, 2 for `blade`, one `move` per affected item for the two gap ops.

### 2. `insert` refuses a partial overlap instead of rippling it

`insert` throws `EditEngineError` when the inserted item would **partially** overlap the following
item (`0 < delta < duration`). The ripple is provably non-invertible in that band: it destroys
`nextRecordIn`, and the post-op document is byte-identical to one where the item merely *filled a
gap* of that size — the two need different undo shifts, so an inverse that guesses silently
corrupts the document. Only two positions are accepted:

- **fits in the gap** (`recordOut <= nextRecordIn`) — ripples nothing; inverse is `lift`.
- **flush at the following item's start** (`nextRecordIn === recordIn`) — the classic ripple
  insert, shifting everything at/after that point right by the item's own duration; inverse is
  `extract`.

Placing over an existing item remains `overwrite`; pushing content aside from an arbitrary point
means inserting flush at the next item's start. The engine uses the ticket's *prose* rule for
`extract` ("adjacent → full ripple; already gapped → no shift"), which is what keeps `insert`,
`lift` and `extract` mutually exact inverses. E-501's generator must treat a refused op as a
rejection, not a failure.

### 3. `trim`'s `slide` TRANSLATES the neighbour's edge; it never snaps it

In `slide` mode the neighbour's facing edge is translated by the same delta as the moved item. It is
**never snapped onto the moved item's edge**. Snapping looks equivalent — the two coincide when the
items are adjacent — but it silently swallows a pre-existing gap, and that gap's width is not
recoverable from the post-op document, so the op would not round-trip. A translation leaves every
gap exactly as wide as it was.

(Related, and from the same rule — *an op may not destroy information its own inverse needs* —
`trim` clamps `deltaFrames` to the invariants and its inverse carries **minus the effective,
post-clamp delta**, so a clamped trim still restores the document exactly.)

### 4. `addMarker` takes an optional, validated `index`

The contract orders markers **only by frame**, so markers sharing a frame have no canonical order.
Restoring such a marker by frame alone would land it at the end of its tie group rather than in its
old slot, and `removeMarker`'s inverse would no longer deep-equal the document it undid. So
`addMarker` accepts an optional `index`, and the engine's own `removeMarker` inverse carries the
slot it removed the marker from.

Omitting `index` — the normal case for a hand-authored op — inserts after every marker whose frame
is `<=` the new one. When present, the index must be an integer within `0..markers.length` **and**
must itself keep the list frame-ordered, so it cannot be used to break the invariant.

Two smaller notes that travel with the same reasoning: `setMarker` refuses a `frame` move that would
pass a neighbour (a re-sort cannot be undone by one op — `removeMarker` + `addMarker` is the path
for that), and TypeScript optionality follows the **JSON Schema**, not C# serialization: the schema
is the wire contract.

---

## The image document: layers that are subjects, not frames

**A second document type, deliberately not this one.** Everything above describes a *time*-based
document with a pinned `schemaVersion: 2` and a hand-written, drift-tested TypeScript mirror. An image
editor needs a *layer* document instead — ordered elements with a transform, an opacity and a z-order —
so it is a separate set of tables (`edit_image_documents`, `edit_image_layers`, `edit_image_exports`)
behind its own controllers. Putting both behind one version number would have meant every existing timeline client
learning to ignore fields it does not understand.

**A layer is a subject cut out of an image, with an alpha matte.** That is the whole point of the
table. A layer carries its own pixels, and for a `Cutout` those pixels are a person or an object on
transparency rather than a rectangle of photograph, so moving, scaling and re-colouring it does not
drag the background along. The alternative — compositing whole images and calling them layers — is the
thing this replaced.

**What a layer holds.** Each row records: the document it belongs to; a `ZOrder` where lower
composites first; a `Role` (`background`, `subject`, `overlay`) that says what the layer is for; a `Kind` of `Source` or
`Cutout`, `Generated` (a vendor output: erase, fill, expand, upscale, new background, relight,
instruction edit or a text-to-image backdrop), `Text` (vector text, no bitmap) or `Image` (a project
image placed as a layer); a label; the project file it was cut from; the storage key and bucket of its
own image; an optional `MatteStorageKey` for the binary matte where the vendor returned one separately;
its pixel size; an opacity; a jsonb transform; a `visible` flag, a `locked` flag and a `blend_mode`;
`content_json` for a text layer; `provenance_json` for a generated layer; and — for a cut-out — the
model and the prompt that produced it, because a cut-out is not reproducible from the layer alone. The
`Kind` enum is persisted by name and append-only: `Source`, `Cutout`, `Generated`, `Text`, `Image`.

**Where the pixels come from, and who they are sent to.** Segmentation is a hosted call: the source
image is handed to fal.ai as a short-lived presigned link the vendor fetches itself, together with the
words describing the subject. That is disclosed to the customer rather than hidden — see
`site/lib/provider-data-handling.ts` and the sub-processor table in the Privacy Policy. Resolving no
segmentation provider is a clean `409` the UI can explain, never a failed request.

**Deleting a layer frees its objects, and that is not automatic.** A cut-out or a generated layer lives
under `projects/{id}/agentFiles/` with no `project_files` row, so the storage-retention sweep — which
marks an object for deletion when the row that referenced it expires — would never see it. The
controller therefore writes a `storage_retention_marks` row for the layer's image and its matte at the
moment the layer (or its document) is deleted, and the existing sweep picks them up with the normal
grace period. Only `Cutout` and `Generated` layers are marked: a `Source` or `Image` layer's key IS the
customer's own uploaded file, and marking that would delete an original they still own; a `Text` layer
has no object at all.

## The thumbnail editor's document API

**Four controllers, one route prefix** — `/api/v1/projects/{projectId}/image-documents`, camelCase
JSON, every action authorizing through the project access service exactly once and answering `404` for a
document in another project. `EditImageDocumentsController` owns documents, layers, subject detection
and the layer pixels; `EditImageOperationsController` owns the AI operations;
`EditImageAssetsController` owns presigned uploads and exports;
`EditImageGraphicsController` owns designed graphics (see "Designed graphics"). The server never rasterises: there is no
raster library in either .NET service, so compositing, text rendering, brush masks and the flattened export
all happen in the browser, and every server-side operation takes an existing layer's bitmap and
stores the vendor's output as a **new immutable layer**. The one exception is alpha: `PngAlpha`
(`ReelBolt.Shared/Inference`, `ZLibStream` only) reads a PNG's alpha and rewrites a PNG's alpha channel, row
by row, so a cut-out keeps its transparency through an AI edit (see "Transparency survives an edit").

| Method | Path | Body → result |
|---|---|---|
| `GET` / `POST` | `/` | list summaries / create from `{ sourceProjectFileId, name?, width?, height? }` (see "A new picture opens at the photo's size") |
| `GET` `DELETE` | `/{documentId}` | the document with its layers / soft-delete |
| `POST` | `/{documentId}/duplicate` | no body → `201` the same document body as `GET`, named "Copy of <name>" (<= 200 chars); canvas, background and layers in z-order with new ids; every layer image and matte is COPIED to `agentFiles/image-editor/{newId}/` (deleting one picture never breaks the other), counted toward the plan's storage (`409 PICTURE_COPIES_OVER_QUOTA` when it does not fit); exports are not copied; foreign/missing `404` |
| `PATCH` | `/{documentId}` | `{ name?, canvasWidth?, canvasHeight?, backgroundColor?, baseRevision? }` → document |
| `POST` | `/{documentId}/subjects` | `{ layerId?, prompt?, pointPrompts?, boxPrompts?, returnMultipleMasks?, maxMasks?, model?, variant?, role?, providerId? }` → `layer[]` |
| `POST` | `/{documentId}/layers/text` | `{ content, transform?, role?, label? }` → the `Text` layer |
| `POST` | `/{documentId}/layers/from-file` | `{ projectFileId, role?, label? }` → an `Image` layer |
| `PATCH` `DELETE` | `/{documentId}/layers/{layerId}` | `{ role?, zOrder?, opacity?, transform?, visible?, locked?, blendMode?, label?, content?, crop?, baseRevision? }` → layer / soft-delete |
| `GET` | `/{documentId}/layers/{layerId}/image` and `/matte` | PNG bytes; `404` for a `Text` layer, and `404 LAYER_IMAGE_MISSING` (never a `500`) when the stored object is gone |
| `POST` | `/{documentId}/operations` | see "Thumbnail editor operations" |
| `POST` | `/{documentId}/assets/upload-url` | see "Thumbnail editor uploads and exports" |
| `POST` `GET` | `/{documentId}/exports` | see "Thumbnail editor uploads and exports" |
| `GET` `POST` | `/{documentId}/graphics`, `GET /{documentId}/graphics/{graphicId}` | see "Designed graphics" (`EditImageGraphicsController`) |

**Document columns.** `canvas_width` / `canvas_height` (nullable ints; `NULL` means the source image's
size; the UI offers eight shapes — 16:9, 9:16, 1:1, 4:5, 4:3, 3:2, 2:3, 21:9 — at 720p, 1080p, 1440p
or 4K by the short side, plus any exact size, and stores only the two numbers) and `background_color`
(a CSS colour; `NULL` is transparent). `PATCH` cannot tell an omitted property from a `null` one, so
"clear" has an explicit spelling: an empty `name` or `backgroundColor` clears it, and
`canvasWidth: 0` with `canvasHeight: 0` restores the source size. The two canvas sides travel together
(each 16–8192) or the answer is `400`. A stale `baseRevision` is `409 REVISION_CONFLICT` and nothing is
written.

**A new picture opens at the photo's size** (editor remediation W0.8 T1). `POST /` measures the source
photo from its stored header (`PictureSizes.MeasureDisplaySizeAsync`: PNG, JPEG with EXIF orientation,
WebP and GIF read by `ImageHeader`, at most the first 2 MiB (`ImageHeader.PrefixBytes`), nothing decoded). When the header cannot be
read it uses the optional `width`/`height` the browser measured (both or neither, each 1–65535, else
`400`), and when neither is known the canvas stays `NULL` (the source image's size) as before. The canvas
is the photo's own size, its long side capped at **4096** with both sides scaled together
(`PictureSizes.FitNewCanvas`: 1080x1920 opens at 1080x1920, 8000x6000 at 4096x3072), and the `Source`
layer records the photo's size with `transform.scale` set so it fills that canvas exactly (0.512 in the
second example). Both canvas sides are also kept at least **16** (the smallest a later `PATCH` accepts): a
tiny or extreme-aspect photo grows until its short side is 16, its long side still capped at 4096, and the
`Source` layer's scale then covers the canvas (10x10 opens at 16x16, 10000x30 at 4096x16). Before this every new picture opened at 1280x720, so a phone photo exported as a
top-left crop.

**Deleting a photo a picture uses: copy-on-delete** (decision D10). `edit_image_documents.source_project_file_id`
is nullable with `ON DELETE SET NULL` (migration `PictureCopyOnDelete`; it was a cascade, so deleting the
photo deleted every picture made from it). Before any path deletes a project file of a project that keeps
living, `PictureSourceCopies.CopyBeforeDeleteAsync` gives every picture with a layer on that file's storage
key ONE server-side copy under `projects/{p}/agentFiles/image-editor/{documentId}/source-{guid}{ext}`,
repoints EVERY layer of that picture using the key, records the copy's size on those layers
(`edit_image_layers.copy_size_bytes`), clears the document's and the layers' links to the file and saves;
only then is the original deleted. The paths are: both Files delete handlers (one file, a folder), the
assistant's delete-file tool, the three sync removals (local folder, cloud drive, GitHub) and a workflow
output take's delete/prune. A file move or folder rename repoints the layers to the new key
(`RepointMovedAsync`) instead of copying. A copy counts toward the workspace's storage, once per
(picture, copy), through `StorageUsageService` and therefore the plan quota (`IPlanGateService`). One
request makes at most one copy per (picture, deleted key), so a photo used by N pictures costs N copies; a
delete the **user** asks for (both Files delete handlers and the assistant's delete-file tool) therefore runs
`PictureSourceCopies.CheckCopiesFitAsync` first: the NET growth (one copy per (picture, key) at the file's
recorded size, minus the deleted files' own bytes) goes through `IPlanGateService.CheckUploadAsync`, and if it
does not fit the delete is refused **before anything is copied or deleted** with
`409 { code: "PICTURE_COPIES_OVER_QUOTA", message, pictures: N }` - the message is a plain sentence ("This
photo is used by 12 pictures. Keeping their copies needs more storage than your plan has left. Delete some
pictures or free up space, then try again."; a folder delete starts "Photos in this folder are used by ...");
no id or key. The assistant tool returns the same `code`, `pictures` and the sentence as `error`. A photo used
by ONE picture never grows storage (its copy replaces the original byte for byte), so a full workspace can
always delete it; billing off or an unlimited plan never refuses. A **background** removal (the three sync
removals, an output take's delete/prune) cannot be refused - the file is already gone at its source - so it
copies regardless and the copies are counted afterwards (a workspace can end up over its quota that way and
then has to free space before adding more, exactly as with workflow outputs). An original already missing from the store is skipped (its layers answer
`404 LAYER_IMAGE_MISSING`); any other storage failure removes the copies made so far and fails the
delete before anything is lost. Deleting a project or account takes its pictures with it and copies
nothing; the plan-retention sweep deliberately does not copy. When the last layer on a copy is deleted,
the copy is marked for the retention sweep like any editor object. The Files delete dialog asks
`GET /api/v1/projects/{projectId}/files/{fileId}/picture-usage` → `{ pictures: N }` (View role; a file of
another project, or a project the caller cannot see, is `404`) so it can say "N pictures use this photo and
keep their own copy". No response carries a storage key or URL.

*Known limit (race).* The copy query and the delete are not one transaction: a picture or layer created from
the file in the moment between `CopyBeforeDeleteAsync` and the object delete keeps the original key, so after
the delete it shows the `404 LAYER_IMAGE_MISSING` placeholder (the picture still opens; the user re-places the
photo). Closing it needs a lock on the file row across the storage calls, which no cheap change gives; re-running
the copy query after the delete cannot help because the object is already gone.

**A missing layer object is a 404, not a 500.** The layer `image`/`matte` routes answer
`404 { code: "LAYER_IMAGE_MISSING", message }` when the object is gone (a legacy picture whose photo was
deleted before D10, a swept object), so the editor shows a placeholder and Export is never blocked.

**Layer transform.** `{ x, y, scale, rotation, flipX, flipY }`: `x`/`y` are **canvas pixels** of the
layer's top-left corner, `scale` is a uniform multiplier of the layer's own pixel size (greater than 0;
mirror with the flips, never a negative scale), `rotation` is degrees clockwise about the layer's centre.
`blendMode` is one of `normal`, `multiply`, `screen`, `overlay`, `darken`, `lighten`, `soft-light`,
`hard-light`, `difference`; anything else is `400`. A `PATCH` validates every field before it applies any,
so a request with one bad field changes nothing.

**Layer crop (T-035).** `crop` on a layer is `{ x, y, w, h }`, fractions (0 to 1) of the layer's own stored bitmap from its
top-left; absent or `null` means uncropped (stored in `edit_image_layers.crop_json`, migration `AddEditImageLayerCrop`;
older rows read as uncropped). It is additive and never changes the stored pixels, so a crop can be widened again. A `PATCH`
with `crop` must lie inside the picture and be at least 0.01 wide and tall (else `400`); not for `Text` layers (`400`);
`{ x: 0, y: 0, w: 1, h: 1 }` removes it. When a layer is cropped its `transform` places the top-left of the KEPT part and its
drawn size is the kept part (`placeLayers`), so the canvas, hit-testing, align and every export draw exactly the window
(`drawLayer` uses the 9-argument `drawImage`). `Duplicate` copies the crop with the layer. AI tools, the brush and
subject clicks work on the whole stored picture, so they ask the user to remove the crop first.

**Subjects.** The answer is an **array**: one `Cutout` layer per mask the model returned (capped by
`maxMasks`, default 8 when `returnMultipleMasks` is true, else exactly one). **Any picture layer can be cut
from**: with `layerId` the vendor gets a presigned GET of THAT layer's bitmap (an `Image`, the `Source`, a
`Generated` AI result or an earlier `Cutout`; a `Text` layer is `409 LAYER_HAS_NO_BITMAP`), and each cut-out
takes the layer's transform — its scale corrected when the vendor answered at another pixel width — and is
inserted directly above it, inheriting its `sourceProjectFileId`. Without `layerId` the document's source
project file is used and the cut-outs go on top with the identity transform, as before; a document whose
source file was deleted cuts from the picture's own copy of it (decision D10, below); only a legacy picture
whose source file went before copy-on-delete existed, and so has no copy, answers `409 NO_SOURCE_IMAGE`
("Select a picture layer…") rather than a bare `404`.
(Before this, the route ALWAYS segmented the source file whatever was selected, and the editor blocked the
tool outright once the `Source` layer had been deleted — so a picture made with **Create a picture** could
never be cut out.) `pointPrompts` (`{x, y, label}` with `label` 1 = include, 0 = exclude, at most 64) and
`boxPrompts` (`{xMin, yMin, xMax, yMax}`, at most 16) are in the **cut-from picture's pixels** and are
passed to the vendor client verbatim — fal's SAM schemas state integers and no coordinate space, so what the
endpoint actually expects is measured at live verification, not assumed. The editor cuts from the selected
picture layer, else the original, says which in the panel, and clears its marks when that layer changes.
At the client interface the multi-mask result is `SegmentationResult.Masks` (read it through
`AllMasks`, which yields the single cut-out for any client that never sets `Masks`).

**Text layers.** `content` is `{ text, fontFamily, fontWeight, fontSize, lineHeight, letterSpacing,
align, color, stroke?: { color, width }, shadow?: { color, blur, offsetX, offsetY }, maxWidth?,
uppercase }`, rendered by the browser only. The API enforces text up to 2 000 characters, font size
8–600, stroke width 0–64, font weight 100–900, line height 0.5–3, `align` of `left`/`center`/`right`, a
safe-character `fontFamily` and CSS-colour strings of at most 64 characters, and stores the normalised
payload. A text layer has `StorageKey` empty; `content` can be replaced through `PATCH` on a text layer
and is a `400` on any other kind.

**Avatars and objects in a picture (`subjectIds`).** `POST …/{documentId}/operations` takes
`subjectIds: [avatarId]` (1 to 3 avatars of the project, at most one of them a person) for the operations
`generate` and `instruct` only; every other operation answers `400 subjects_not_supported`. The checks run after
the operation's own, in this order, and none of them holds a credit or sends anything: the avatars the ids name
(`404 AVATAR_NOT_FOUND` for an unknown id, another project's avatar or a kind whose switch is off;
`400 too_many_subjects`, `too_many_people`, `duplicate_subject`), the plan feature of each kind
(`402 PLAN_NOT_ENTITLED`, feature `avatars` or `avatar_objects`), the permission to send their pictures out of the
host (`409 CONSENT_REQUIRED` unless the project's media consent is recorded, or the body says
`allowMediaEgress: true` and the one media rule `IMediaEgressCallPermission` lets this caller give it: the person's
own app session on this route, an API key holding `media.egress`, an assistant card that was shown and allowed, or MCP
while the user's `mcp_media_egress_enabled` is on; see [`avatars.md`](avatars.md) "The one media rule"; nothing is
recorded), the one selectability rule
(`409 AVATAR_NOT_USABLE` with `reason`, `avatarId`, `avatarName`), the picture consent version
(`409 AVATAR_CONSENT_NO_PICTURES`), and last the model (`409 OPERATION_UNSUPPORTED` with `models`, the
picture-edit models that take several pictures; only a fal.ai instruction-edit model with an image array can show
a subject).
`IProjectSubjectReader` reads up to 3 pictures per subject and 6 in all, each re-hashed against the digest
attested with the avatar (a picture that is still an output's bytes also needs a current moderation verdict, as
the layer does), and the copies that are sent are re-encoded to at most 1536 px and sent as data URIs,
with the vendor asked not to keep them when a person is shown. `generate` with subjects runs on the `ImageEdit`
capability (its price, hold and estimate follow that capability and the size asked for): the subjects' pictures are
the image array and the prompt sits inside the fixed frame `SubjectPictureInstruction`, with the requested
`width` x `height` sent as the nearest aspect ratio the endpoint documents. `instruct` with subjects keeps the
layer as picture 1, then the loose `referenceProjectFileIds` (at most 4), then the subjects' pictures, and one
fixed sentence says which pictures show which subject. The new layer's `content` is
`{ subjects: [{ avatarId, name, kind }], aiGenerated: true, disclosure }` (`Visible` or `MetadataOnly`: the
person's own label, else `MetadataOnly`; `PictureSubjectFacts`), which is what the editor reads to add the
"AI-generated" label at export; every other bitmap layer keeps a null `content`, duplicating a picture copies
it, and it stays when the avatar is deleted. The layer's `provenance` keeps its usual shape plus `instruction`
(the version of the fixed words). The answer adds `subjectPicturesSkipped` (pictures of the subjects that were
not sent).
`GET …/operations/{operation}/capabilities` adds `subjects: { supported, maxSubjects, maxPeople, models[],
provider }` for the two operations, and `?subjects=true` on `generate` describes and prices the `ImageEdit`
route a run with subjects takes. `GET …/avatars/{id}/usage` counts such pictures as `picturesGenerated`.
The web asks `?subjects=true` for "Create a picture" as soon as subjects are chosen (`subjectsChangeRoute` in
`lib/editor/ai-operations.ts`) and shows that answer's price and availability while they stay chosen; a refusal of
that route is kept apart from the plain tool's, the picker stays usable when the plain tool is not set up, and the
run is priced and built from the route it really takes.

## Thumbnail editor operations

`POST /api/v1/projects/{projectId}/image-documents/{documentId}/operations` with
`{ operation, layerId?, maskKey?, prompt?, negativePrompt?, model?, providerId?, params?, role?, label?, sourceKey? }`
answers `{ layers, operation, provider: { kind, model, capability }, usage: { unit: "images", quantity }, revision, notice }`
(`model` is the endpoint that actually RAN, `GeneratedImageSet.ResolvedModel`, also written to the layer's
provenance; `capability` is the one the serving row was resolved as; `notice` is a plain sentence or null).
It is synchronous and bounded: past **90 seconds** (Cloudflare's edge gives up at 100) the answer is
`424 OPERATION_TIMEOUT` and nothing is stored.

| `operation` | Needs | Client call | Output |
|---|---|---|---|
| `erase` | `layerId`, `maskKey` | `IImageClient.InpaintAsync`, prompt `background` unless given | `Generated`, same transform, directly above the source |
| `fill` | `layerId`, `maskKey`, `prompt` | `InpaintAsync` | `Generated` |
| `expand` | `layerId`, and `params.aspect` (`"9:16"`: grow to that shape) or `params.{top,right,bottom,left}` (pixels of the layer's picture) | `TransformAsync` (`Expand`) with a planned `ImageExpandGeometry` | `Generated`, placed so the original's pixels stay exactly where they were (see "Expanding to a shape") |
| `replaceBackground` | `layerId`, `prompt` | `TransformAsync` | `Generated` |
| `relight` | `layerId`, `prompt` and/or `params` | `TransformAsync` | `Generated` |
| `upscale` | `layerId`, `params.factor` | `TransformAsync` | `Generated`; scale divided so the displayed size is unchanged |
| `instruct` | `layerId`, `prompt` | `EditAsync` | `Generated` |
| `generate` | `prompt`, `params.{width,height}` or `aspect` | `GenerateAsync` | `Generated` at z-order 1, role `background`, scaled to cover the canvas when one is set |
| `removeBackground` | `layerId` | `IImageAnalysisClient.SegmentAsync`, variant `Matting` | `Cutout` with a matte |

**The fal.ai endpoint behind each tool** (`FalAiImageClient.Catalog`; schemas and prices in
[`fal-ai-models.md`](fal-ai-models.md)). One default per job, every alternative selectable through `model`:

| Tool | Default | Alternatives | Notes |
|---|---|---|---|
| subject (`/subjects`) | `fal-ai/sam-3-1/image` | `fal-ai/sam-3/image`, `evf-sam`, `sam2/image`, `birefnet/v2` | text, point and box prompts; `masks[]` become the layers' mattes; **point/box integers are passed as source pixels** (the schema states no space) |
| `erase` | `fal-ai/object-removal/mask` (white = remove, STATED) | `fal-ai/flux-pro/v1/erase` (white = erase, STATED) | no prompt; the neutral prompt `background` is dropped, a real one is refused |
| `fill` | `fal-ai/flux-pro/v1/fill` (polarity NOT STATED; white = act on is ASSUMED) | `ideogram/v4.5/edit` (Black edits, white preserves: refused unless the mask is already inverted) | the mask goes by presigned URL (`MaskUrl`) |
| `expand` | `fal-ai/bria/expand` (canvas + optional prompt, < 25 MP, flat $0.04) | `fal-ai/flux-2-pro/outpaint` (margins only, 0-2048 per side, no prompt, at most 4 MP in and out) | the editor sends a shape or margins; the server plans the endpoint's own geometry |
| `replaceBackground` | `fal-ai/bria/background/replace` | | exactly one of prompt / `ref_image_url` |
| `relight` | `fal-ai/image-apps-v2/relighting` (18 named styles, exposed through `Describe(...).Options`) | `fal-ai/iclight-v2` (prompt), `bria/fibo-edit/relight` | |
| `upscale` | `fal-ai/seedvr/upscale/image` | `topaz/upscale/image/precision` (Text Refine keeps type crisp), `topaz/upscale/image/generative` | `params.factor` |
| `instruct` | `google/nano-banana-2.1/edit` | FLUX.3, GPT Image 2.5 (`sunburst`/`flare`), Seedream 5 Pro, Qwen Image Edit 2511, Bria FIBO edit and erase-by-name, the YouTube-thumbnail LoRA preset | |
| `generate` | `ideogram/v4.5` (typography; explicit sizes from its 36-size list, 1280x720 included) | `google/nano-banana-2.1`, FLUX.3, Seedream 5 Pro, `fal-ai/ideogram/v3/generate-transparent` (the only transparent option; an older generation) | |

**Provider resolution: one capability per tool.** `erase` → `ImageErase`, `fill` → `ImageFill`, `expand` →
`ImageExpand`, `replaceBackground` → `ImageBackgroundReplace`, `relight` → `ImageRelight`, `upscale` →
`ImageUpscale`, `instruct` → `ImageEdit`, `generate` → `ImageGeneration`, `removeBackground` →
`Segmentation`, each through `IInferenceProviderResolver.ResolveImageJobAsync` (explicit `providerId` →
the single default row of exactly that capability → none; the resolver never falls back across
capabilities). Before the split every tool resolved `ImageEdit`, so one row and one model served an
instruction edit, an object removal and an upscale alike, and a non-fal row's model was sent to every tool.

- **Compatibility fallback, decided in the controller and visible in the response.** A job whose own
  capability resolves nothing falls back to the `ImageEdit` default row **only when that row's kind serves
  the job** (`ImageJobCapabilities.KindServes`, i.e. `ProviderCapabilityUsage.ServableByKind`). A fal.ai
  `ImageEdit` row therefore still serves every tool (each on its catalogue default, below), exactly as before;
  a MiniMax or Black Forest Labs `ImageEdit` row is never handed an upscale it would refuse. Otherwise the
  answer is `409` with `IMAGE_EDIT_PROVIDER_UNAVAILABLE` (`IMAGE_GENERATION_PROVIDER_UNAVAILABLE` for
  `generate`, `SEGMENTATION_PROVIDER_UNAVAILABLE` for `removeBackground`), a `capability` field naming what
  to add, and a plain message ("No AI service is set up for upscaling pictures yet…").
- **Which model.** An explicit `model` wins. Otherwise a `FalAi` row sends its OWN model when that is a
  catalogued endpoint for the job (`ImageJobCapabilities.FalModelFor`) and an **empty** model otherwise,
  which the client reads as the catalogue's default for the job — so an `ImageUpscale` row set to a Topaz
  endpoint upscales with Topaz, while a legacy `ImageEdit` row (model `google/nano-banana-2.1/edit`) serving
  an erase by fallback never sends that instruction-edit model to the erase job. Every other kind sends the
  row's own model. `removeBackground` on fal.ai always uses `fal-ai/birefnet/v2` (`Matting`).
- **Save-time rule.** A fal.ai row of a job capability (and `ImageGeneration`) must name an endpoint of that
  job (`InferenceProvidersController.IsUnsupportedModel` → `ImageJobCapabilities.FalModelProblem`); `ImageEdit`
  keeps its older rule (any catalogued editing endpoint), since a pre-split row's model was only the
  connection test's target. The connection test applies the same check before it sends anything.

**Errors.** `400` unknown operation, a missing `layerId`/`prompt`, a malformed or unrecognised `params`
entry (`OPERATION_INVALID`), `MASK_REQUIRED` (no `maskKey`, a key that no `upload-url` issued for this
document, an object that was never uploaded or is empty); `404` unknown document or layer;
`409 LAYER_HAS_NO_BITMAP` (a `Text` layer), `409 OPERATION_UNSUPPORTED` (the resolved client throws
`NotSupportedException` — raised before anything is sent, so it costs nothing), the `*_PROVIDER_UNAVAILABLE`
codes; `422 SOURCE_TOO_LARGE` (expand: the original is over the endpoint's documented limits - see below;
nothing was sent); `424 OPERATION_FAILED` (or `SEGMENTATION_FAILED`) for a vendor failure or an empty
answer; `424 OPERATION_TIMEOUT`; `501` when no presign service is registered. **Refetch contract
(W0.8 T2, R14.6):** a `424` writes no layer and does not bump the revision, so after ANY `424` the editor
refetches `GET /{documentId}` and shows what the server holds (the nginx timeout that lets the API's 90 s
bound answer first is a separate change). **Never 502 or 504:**
Cloudflare, in front of every cloud deployment, replaces the body of an origin 502/504 with its own HTML
page, so the reason never reached the editor (`EditImageDocumentSupport.VendorFailureStatus`). Every vendor
failure is also logged (`LogWarning`, the exception type and message with every URL's query string removed -
a presigned GET's query is its credential - bounded to 1 000 characters). Every vendor failure message starts
in plain words with what did not happen ("The AI service could not upscale the picture. Nothing was added
to your picture. It said: …", `EditImageDocumentSupport.VendorFailureMessage`, the vendor's text bounded to
400 characters), and the editor shows the server's message rather than a generic "try again".

### Expanding to a shape

The common need is "make this picture fit a shape" - a 5931x3954 phone photo filling a 9:16 reel cover - so
the Expand panel offers **Fit a shape** (the default: the canvas's own shape when the picture does not have
it, plus 9:16, 4:5, 1:1, 16:9, 3:4, 4:3, 2:3 and 3:2) and, as the second choice, **Pixels per side**. The
request carries `params.aspect` or `params.top/right/bottom/left`, never both; margins are in the layer's own
pixels. The server turns either into the endpoint's own geometry with `ExpandPlanner` (pure, in
`ReelBolt.Shared/Inference`): a shape grows the picture centred, only along the side that is short, never
cropping (5931x3954 to 9:16 is 3295 px at the top and 3295 at the bottom).

**Which picture is sent (product decision).** The ORIGINAL, whenever the endpoint's documented limits allow it:

- `fal-ai/bria/expand` (the default) takes `canvas_size`, `original_image_size` and `original_image_location`,
  so it is told to draw the original at a size that keeps the canvas under its 25 MP limit and always gets
  the original. Every size is stated in the request, so placement does not depend on the vendor's choices.
- `fal-ai/flux-2-pro/outpaint` keeps its input at the size it is sent and adds margins of at most 2048 px per
  side, and FLUX.2 makes at most 4 MP and resizes larger inputs by an unstated rule. When the original (or
  what it grows into) is over those limits, the server answers **`422 SOURCE_TOO_LARGE`** with
  `{ width, height, source }`, sends nothing and spends nothing. The size is the one the layer **appears at in
  the design** (its pixel size times its scale on the canvas, never larger than the original), or - only when
  even that is too large - the largest that fits (`source: "smaller"`). The browser draws the layer's own
  picture at exactly that size (`renderScaledCopy`, a PNG; the server has no general image library and the
  browser has already decoded the picture), uploads it through `assets/upload-url` with purpose `source`
  (PNG or JPEG, at most 48 MiB, 24 h retention mark), and runs the expand again with its `sourceKey`. The
  server reads the copy's size from its header, checks it has the original's shape, plans again for it and
  sends it. The result carries a `notice` the editor shows as a warning: "This picture was larger than the AI
  service accepts, so it used the picture at the size it appears in your design (W×H)."

**Placement.** The result is placed from the plan and the size the vendor actually delivered
(`ExpandPlanner.PlaceResult`): scaled so the original's part of it is as wide as the original was drawn, and
moved up and left by the part added there - so a vendor that rounds or resizes, and a copy sent instead of
the original, still land on the original's pixels. A rotated or mirrored layer keeps its position (the size
is still corrected). The size of a layer whose row states none (an uploaded photo) is read from the first
2 MiB of its object (`ImageHeader.TryReadDisplaySize`, PNG/JPEG/WebP, JPEG EXIF orientation applied as the
browser does).

**What the panel shows comes from the endpoint.** `GET .../image-documents/operations/{operation}/capabilities`
resolves the provider exactly as running the tool would and answers what its catalogue entry says
(`IImageClient.Describe`): `takesPrompt` (no prompt field when `false` - FLUX.2 outpaint documents none),
`maxPromptLength`, and for expand `{ geometry: "canvas" | "margins", maxMarginPx, maxOutputPixels,
maxInputPixels, basis }`. Margin inputs are clamped to `maxMarginPx` with a sentence saying so. Nothing is sent
to a vendor; a tool with no provider answers the same `409` a run would.

**Cost before and after (R14.1).** The capabilities answer also carries `estimate`
`{ billingEnabled, credits, balanceCredits, affordable, unavailableReason?, reason? }` (about figures: a
size-priced tool is priced at the document's canvas, or the `width`/`height`/`documentId` query), and
`canConfigure` (the caller is an organization Owner/Admin or platform admin, for the role-aware "ask your admin"
message). With billing off the estimate says `billingEnabled: false`. A tool with no provider answers `409` with
`reason: "NO_PROVIDER"` and `canConfigure`. `POST .../operations` returns `chargedCredits` (what it settled at),
`POST .../subjects` the same in the `X-Charged-Credits` header (its body stays the bare layer list), and each
designed-graphic job carries `chargedCredits` once finished; `GET .../graphics` uses the same estimate shape
(`billingEnabled: false` when billing is off) plus `unavailableCode` and `canConfigure`. The assistant's
approval card for `ApplyRunPictureTool` / `ApplyDesignGraphic` already shows the same figures.

**Blank pictures (R13.1).** `POST .../image-documents` with `width` and `height` (16 to 4096 each) and no
`sourceProjectFileId` creates an empty transparent picture named "New picture" (optional `name`,
`backgroundColor`); with a source it behaves as before. Out-of-range sizes are `400`; no source and no size is `404`.

**The 9-second failure on staging (2026-10-10).** A valid FLUX.2 outpaint (2048 px top and bottom on the
5931x3954 photo) failed after about 9 s with `502 OPERATION_FAILED`, whose body Cloudflare replaced, and the
controller logged nothing - so the vendor's words are not known. The request asked for 5931x8050 = 47.7 MP,
twelve times FLUX.2's documented 4 MP, from a 23 MP input over its documented 4 MP; that is the most likely
cause. It was not the 48 MB download cap (`ImagePayload.MaxImageBytes`): that applies only after a result
exists, and FLUX.2 cannot make an image large enough to reach it. Both blind spots are fixed (424 + log), and
the planner keeps every FLUX.2 request inside 4 MP.

**Transparency survives an edit.** Most models answer an RGBA cut-out with an OPAQUE picture: on staging,
`google/nano-banana-2.1/edit` given the 5931x3954 RGBA car cut-out (73% transparent) returned a 1264x843 PNG
with colour type 2 (RGB, no `tRNS`), the transparent area painted black, and the editor stored exactly that.
So for the pixel edits of a layer — `erase`, `fill`, `instruct`, `relight`, `upscale` — the controller reads
the alpha the layer is SEEN with (its bitmap's alpha, times its matte's for a `Cutout` that has one, as the
browser composites it; only a PNG is read, at most 96 MB) and, when anything is transparent, carries it over:

- a PNG result is re-encoded as 8-bit RGBA with its alpha multiplied by the source alpha, resampled
  bilinearly to the result's size (`PngAlpha.ApplyAlpha`, streamed one row at a time; ~0.3 s for that
  result, ~1.2 s to read the 24-megapixel cut-out's alpha);
- a result that is not a PNG this can read (a JPEG or WebP answer) keeps its bytes and gets a matte at its
  own size instead (white, the source alpha as its alpha channel), and the layer becomes a `Cutout`, which
  is how the browser knows to draw it `destination-in` that matte;
- `fill` takes the maximum of the source alpha and the painted mask, so what was painted into a transparent
  area (a hat above a cut-out head) stays visible;
- a result whose aspect differs from the source's by more than 2% cannot be lined up with it: it is stored as
  it came and the response's `notice` says so in plain words, which the editor shows.

`expand` and `replaceBackground` are exempt (filling the background is their job), as are `generate` and
`removeBackground`. An opaque source, or one that is not a PNG, costs one metadata read and changes nothing.
fal endpoints that document `output_format` are already asked for `png` (`FalImageEndpoint.OutputFormatField`).

**The output does not depend on what changed during the wait.** The document is read untracked before the
vendor call and loaded fresh to insert the result, so a user dragging a layer during a 60-second inpaint
does not turn an already-paid result into a concurrency failure. Metering runs in the existing client
decorators (`ImageEditImages`, `ImageGenImages`, `SegmentationImages`) under the endpoint that ran, so the
per-job capability split changes which row serves a job, never what it is billed as; the controller prices
nothing. Both the operation and the subjects route now open a `UsageScope` (source `Editor`, the project's
organization and the calling user); before, every editor call was recorded `Unattributed` against the
platform organization.

## Designed graphics

"Design a graphic" is the one AI tool of the picture editor that is not a single vendor call: an agent DESIGNS a
still graphic — a title treatment, a badge, a price tag, an arrow or callout, a stylised number, a shape composition —
in React, as a Remotion `<Still>` in the same sandbox and toolchain the video Author uses, on a TRANSPARENT frame, and
the result becomes a new `Generated` layer. Its source is kept as a composition workspace version, so the graphic can be
CHANGED later ("make it bolder") without starting over.

**Why an execution, not an operation.** The editor's operations are synchronous and bounded at 90 seconds; a design is
an agent tool loop plus a headless-browser render, which takes minutes. It therefore runs as an execution of the
project's hidden **"Picture graphics"** workflow (`WorkflowDefinitionKind.GraphicDesign`, one `StepType.DesignGraphic`
step, the same pattern as the composition revision library's hidden "Video revisions" workflow). That gives it, for
free, the credit preflight (402) and the reserve → settle → refund-by-`FailureClass` lifecycle, the execution lease and
recovery, cancellation, the per-execution SSE progress stream (`step.progress`: "Preparing the canvas", "Designing your
graphic", "Rendering the picture", "Checking the picture", "Saving the graphic") and the run history. The hidden
workflow is created per project on first use, excluded from every list/edit/delete/execute path, and its runs cannot be
re-queued through the workflow retry route (`409 GRAPHIC_RETRY_NOT_SUPPORTED`).

**The API.** `EditImageGraphicsController`, on the image-document prefix, each action checking project access itself:

| Method | Path | Body → result |
|---|---|---|
| `GET` | `/{documentId}/graphics` | `{ graphics: job[] (newest first), estimate, available, unavailableReason }` — and settles finished jobs first |
| `POST` | `/{documentId}/graphics` | `{ description, placement?, width?, height?, canvasWidth?, canvasHeight?, revisesLayerId? }` → `201` job |
| `GET` | `/{documentId}/graphics/{graphicId}` | one job (settled first when finished) |

A job is `{ id, documentId, executionId, workflowId, status, description, frameWidth, frameHeight, placement, target,
revisesLayerId, layerId, layer, summary, error, createdAt, completedAt }`; `status` is the execution's, `target` is the
canvas rectangle where the graphic is expected to land (the editor's work-in-progress region) and `layer` is the layer
once settled. Refusals, in order: `403 EMAIL_NOT_VERIFIED`, project `404`/`403`, `400` (description 1–2000 characters,
a placement word, `width`/`height` together and 64–4096 each, the canvas hint 16–8192), `404` (document or layer),
`409 GRAPHIC_NOT_REVISABLE` (the layer was not made by the designer or its saved design is gone), `429
GRAPHIC_RATE_LIMITED` (`GraphicDesign:MaxPerHourPerUserProject`, default 20, counted from the request rows, with
`retryAfterSeconds`), `503 GRAPHIC_AGENT_UNAVAILABLE` (the built-in agent row is not seeded), `402 INSUFFICIENT_CREDITS`.

**Frames and placement** (`GraphicDesignRules`, shared by the API and the engine). With no size the frame is the whole
picture (scaled down proportionally when it exceeds 4096 pixels a side or 16.7 megapixels, and the layer scaled back up)
and the placement word — `center`, `top`, `bottom`, `left`, `right`, `topLeft`, `topRight`, `bottomLeft`,
`bottomRight` — tells the designer where on it to draw; with a size the frame IS the graphic's box and the placement word
decides where the box sits (4% margins). The canvas is the document's own size, else the editor's `canvasWidth/Height`
hint, else the source layer's size (row or image header), else 1280 × 720.

**The table.** `edit_image_graphic_requests` (Inference-API-owned, migration `AddEditImageGraphicRequests`; the engine maps
it `ExcludeFromMigrations()` — engine snapshot migration `MapEditImageGraphicRequests`). The API writes the request
(description, placement, frame, target, revised layer, base version, requester); the engine writes only the `Result*`
columns; the API settles it into a layer and records `result_layer_id`. No tenant filter: a project child, constrained to
the document on every read (`TenantBoundaryInventoryTests`). The engine never maps the layer document itself.

**What the engine does** (`GraphicDesignStepExecutor` → `GraphicDesignRunner`, both thin and front-end neutral like the
revision runner):

1. **Prepare the sandbox.** A new graphic: write a scaffold — `src/root.tsx` registers `<Still id="Graphic">` at the frame
   size, `src/Graphic.tsx` draws an empty transparent frame. A revision: restore the revised graphic's composition
   workspace version (`ICompositionWorkspaceRestorer`, the revision library's layer 2) — it must be a version of this
   project whose source is `GraphicDesign`, or the run fails `GRAPHIC_BASE_UNAVAILABLE`.
2. **Brief and agent.** `GraphicDesignBrief` quotes the description as DATA in a fenced block it cannot close (backticks
   are neutralised), names the frame and where to draw, and says the platform renders the result. `GraphicDesigner`
   (below) designs it and checks it with `RenderPreviewStills`.
3. **The platform renders the still itself:** `npx remotion compositions` (the composition must exist — the agent's
   answer is a claim, the CLI is the truth: `GRAPHIC_COMPOSITION_MISSING`), then `npx remotion still <id>
   out/reelbolt-graphic/graphic.png --frame=0 --image-format=png` (`GRAPHIC_NOT_RENDERED`). Nothing the agent uploaded is
   used.
4. **Verify as hostile input** (`GraphicPngVerifier`): read under a 48 MiB ceiling, a PNG whose header is exactly the
   composition's size and within the caps (`PngAlpha.WithinCaps`, 4096 a side, 16.7 MP), the whole stream decoded
   (`GRAPHIC_INVALID_OUTPUT`), something drawn (`GRAPHIC_EMPTY`); then cropped to what was drawn plus 2 px
   (`PngAlpha.OpaqueBounds` + `PngAlpha.Crop`, row-streamed, no raster library), keeping the crop's origin.
5. **Upload** to `projects/{p}/agentFiles/image-editor/{documentId}/graphic-{guid}.png` (`GRAPHIC_UPLOAD_FAILED`).
6. **Record the source** as a composition workspace version (`CompositionVersionSource.GraphicDesign`, the PNG as its
   render, the description as its instruction): a new lineage whose origin is this run, or the next version of the revised
   graphic's lineage. Best effort — a graphic whose source could not be kept is still delivered, just not changeable. The
   film revision front-end refuses a lineage with a `GraphicDesign` version ("change it from the picture editor").
7. **Record the result** on the request row.

Every `GRAPHIC_*` code is in `FailureClassifier` and refunded. The step is never cached and never retried by the outer
loop (a retry would design a different graphic); it holds the Agent row of `StepCreditDefaults` until the organization has
history of its own.

**Settling** (`GraphicDesignService.SettleAsync`, run by the list and single-job reads and by the assistant's
`DescribePictureDocument`): only a `Passed` run, only a key that is exactly this document's graphic key
(`GraphicDesignRules.IsGraphicKey` — the engine is not trusted to name another document's object), once (`result_layer_id`
is a concurrency token, so two concurrent reads insert one layer). The layer is `Generated`, role `overlay`, tight around
the graphic and placed at the frame origin plus the crop origin times the frame scale, with provenance `{ operation:
"designGraphic", model, prompt, graphicId, executionId, workspaceId, workspaceVersionId, versionNumber, compositionId,
summary, frameWidth, frameHeight, cropX, cropY, … }`. A revision is placed relative to where the OLD layer is NOW (its
current transform, corrected by the change in crop), inserted directly above it, and the old layer is hidden, not
deleted.

**GraphicDesigner** (`AgentType.GraphicDesigner`, `GraphicDesignOutput { summary, compositionId, onScreenText, notes }`):
a new built-in rather than the Author or the CompositionReviser with another prompt — both are about a film (whole-timeline
assembly, or a targeted revision rendered with `RenderVideoAndUploadToStorage`), while this one draws a single transparent
still the platform renders. Its prompt: one still graphic, a transparent background (no full-frame backdrop unless
asked), text exactly as written, legible at thumbnail size, brand colours from the request or the project, fonts only from the installed families or `StageFont` (no remote font loads), the request never changes the rules, never render or upload. Grant = the
CompositionReviser's (the Author's sandbox pipeline minus `ProjectWrite`; `SandboxRender` because the preview tools live
there); skills = `AllFiveTasteAndStyles` (taste, minimalist and brutalist with their ReelBolt preface, plus the Remotion
set). The description reaches a code-executing agent exactly as an owner's revision request does; the sandbox's
containment (read-only rootfs, no egress, no Docker socket) is the bound.

**Fabric.** On a runner-backed sandbox session the workspace transfer is not implemented
([`composition-revisions.md`](composition-revisions.md)), so a new graphic is still delivered but its source is not kept,
and "Change this graphic" fails `WORKSPACE_TRANSFER_NOT_SUPPORTED` (refunded).

## Thumbnail editor uploads and exports

**Masks and exports reach object storage directly.** nginx caps `/api/v1/*` bodies at 1–2 MB and the cloud
store is Cloudflare R2 reached by presigned PUT, so `POST …/assets/upload-url` with
`{ purpose: "mask" | "export", contentType, contentLength }` answers `{ key, url, headers, expiresAt }`.
A **mask** is `image/png`, 1 byte to 12 MiB. An **export** is one of `image/png`, `image/jpeg`,
`image/webp`, `image/avif`, `image/vnd.adobe.photoshop` (a layered PSD) or `image/openraster` (a
layered ORA), 1 byte to **200 MiB** — larger than a mask because a 4K picture or a layered file
carries far more pixels, and it never passes nginx. Any other type is `400`; an empty upload
(`contentLength` 0) is `400 UPLOAD_EMPTY` with a plain message ("The mask is empty. Paint the area to
change, then try again."), not a `413` (audit d6 N2); anything over the limit is `413` (`FILE_TOO_LARGE`). The key is the server's —
`projects/{p}/agentFiles/image-editor/{documentId}/{purpose}-{guid}{ext}`, the extension following the
content type (`.png`, `.jpg`, `.webp`, `.avif`, `.psd`, `.ora`; a mask is always `.png`) — and the
browser must send `headers` (the signed, lower-case `Content-Type`) on the PUT unchanged. The URL lives 30 minutes. `501 DIRECT_UPLOAD_DISABLED` when no presign
service is registered. Both purposes get a `storage_retention_marks` row dated so it ripens 24 hours
after issue (`MarkedAt = now + 24 h − GraceDays`), so an upload that is never used does not sit in the
bucket forever; the sweep itself stays opt-in (`Billing:StorageRetention:Enabled`).

**Exporting.** `POST …/exports` with `{ key, width, height, fileName, setAsProjectThumbnail? }` checks that
`key` is exactly an `export` key issued for this document (`400 EXPORT_KEY_INVALID`, storage untouched),
HEADs the object (`409 UPLOAD_OBJECT_MISSING`; `413 FILE_TOO_LARGE` and the object deleted when its real
size is over 200 MiB; the plan's storage quota is checked against the real size), then **moves** it to
`projects/{p}/userFiles/thumbnails/{fileId}{ext}` and writes the `project_files` row (category
`userFiles`, directory `thumbnails`, the content type the key was issued for — read back from its
extension, never from the caller's file name, which gets that extension appended when it lacks it —
and recorded as already summarised and not indexed: an exported picture has no text) and the `edit_image_exports` row (document, file, width, height, the document revision
exported). The answer is `201 { export, projectFile, projectThumbnailApplied }`. **`setAsProjectThumbnail`
is a documented no-op**: the project model has no thumbnail field, so `projectThumbnailApplied` is always
`false`. A retried POST is not idempotent (the first call moved the object); `GET …/exports` lists what
the first call produced, newest first, each with a `fileUrl` to download from.

**Browser upload needs a reachable bucket.** The PUT goes from the page to the presigned host, so it needs
`MinIO:PublicEndpoint` to name a host the browser can resolve and a bucket CORS rule for the app's origin
(`docs/direct-upload.md` "The bucket CORS policy"). Without them "Save to project" and the erase/fill mask upload
say "The upload could not reach storage" (and name the storage address and CORS as the thing to fix); Download still
works, because it never touches storage. The isolated editor-v2 stack has neither by default:
`e2e/scripts/editor-v2-picture-storage.sh apply` sets the public endpoint and adds the CORS rule (test infrastructure only;
there is deliberately no API relay for these uploads).

## The thumbnail editor in the browser

The layered picture editor at `/app/editor/thumbnails/{documentId}?project={projectId}` runs on the
image document above. It is reached from the Editor page's **Pictures** view (`/app/editor?view=pictures`,
the same card as the videos list — `PicturesPanel` lists the project's documents and starts one from a
project image through `documentForImage`, which reuses an existing document of that image; each row's menu renames
or deletes the picture, the delete after a confirmation and without touching the source photo) and from a
project's Files tab. The contract is `docs/design/thumbnail-editor.md` (a repository file, not part of the rendered site); the
user-facing description is `user-guide/editor/thumbnails.md`. The project rides in the query string
because the document routes are project-scoped and a document id alone does not name its project.
This section says where the code is, and the decisions that are not obvious from it.

**Picture track (Wave 1, T-011).** What the Pictures view and the editor do beyond the basics:

- *New picture.* `PicturesPanel` starts a **blank** picture from a shape (Square 1080, Post 4:5 1080 x 1350,
  Story 9:16, Landscape 16:9, Custom; `web/lib/editor/new-picture.ts`) through `POST /` with `width` + `height`
  and no `sourceProjectFileId` (a transparent canvas, no layers), or from a photo (the existing select, or **Upload
  a photo**, which uploads to the project's files with progress and opens the editor on it). The editor page title
  shows the picture's name. Each row's tile is the newest export when the picture has one, else the source photo;
  a search box appears above 6 pictures; the row menu has Rename, **Start a new one from this photo** and Delete
  (the dialog says the photo and any saved export stay in Files). **Duplicate is not offered:** the API has no
  duplicate-picture route.
- *Shape refit.* Changing the canvas shape asks **Fit what's there** (scale every layer uniformly into the new
  frame and centre it, `web/lib/editor/refit.ts`, ONE undo step) or **Keep positions** (the old behaviour). The
  same shape at another size (a tier change) refits without asking. **Original size** restores the photo's size.
- *Upload an image.* Layers tab (and the empty state) **Upload an image**: file picker, upload into the project's
  files with progress, then `layers/from-file`. A failure is one plain sentence. On touch the project's images
  are a tappable list rather than a select.
- *Export.* JPEG, PNG and WebP first; AVIF, PSD and ORA under **More formats** (PSD and ORA say "Text becomes
  flattened pixels"). The dialog shows a real file-size estimate (it encodes the picture in the browser) that
  follows the quality, and **Under 2 MB** binary-searches the quality (`web/lib/editor/export-size.ts`,
  `fitQuality`) for the largest file that fits; when even the lowest quality does not fit it says so.
- *Credits and availability.* One source for all eleven tools: `GET operations/{op}/capabilities` (with
  `documentId`, `width`, `height` for size-priced tools) carries `estimate {billingEnabled, credits?,
  balanceCredits?, affordable}` and `canConfigure`; opening the AI tab asks about every tool once, and the
  estimate is asked again when the selected layer or the canvas changes. Each paid tool shows **about N credits**
  under its Run button before it runs (`CreditNote`), is disabled with an **Add credits** link when the balance
  is short, shows no price when billing is off, and says **Used N credits** afterwards (`chargedCredits` on the
  operation result; the `X-Charged-Credits` header for Cut out a subject; `chargedCredits` on a graphic job).
  A tool with no AI service says "Ask your workspace admin to set up picture tools." to a member and offers
  **Set up picture tools** (`/admin/inference-providers`) when `canConfigure` is true. "Design a graphic" answers
  the same way through `GET graphics` (`available`, `unavailableCode`, `canConfigure`, `estimate`).
- *Assistant cards.* `ApplyRunPictureTool` and `ApplyDesignGraphic` approval cards already carry a credit cost
  (`AssistantApprovalCardBuilder`); their wording was not changed in this ticket.

**The server never rasterises, so the browser does all of it.** `web/lib/editor/compose.ts` is the one
compositor, used for the on-screen canvas and for every export. It is pure: a
`CanvasRenderingContext2D`, the decoded bitmaps, a text measurer and a scratch-canvas factory go in;
pixels come out. Layers draw back to front by `zOrder`; each gets `translate(centre)`, `rotate`,
`scale` (negative for a flip), `globalAlpha` and `globalCompositeOperation` (`normal` is `source-over`).
A `cutout` with a matte is masked with `destination-in` **on a scratch canvas**, never the main one, or
it would erase everything beneath it. `hitTest` and `layerBounds` share the same transform, and
`canvasToLayerLocal` is its inverse: the brush mask and the subject prompts convert through it, so a
stroke or a click lands on the right pixel of the layer's own bitmap however the layer is placed.
Tests drive it with a recording context (`web/lib/editor/__tests__/recording-context.ts`) because
jsdom has no canvas.

**Undo is a command stack over local state, and the server is told the difference.**
`web/lib/editor/history.ts` holds the stack (merging a drag or a run of nudges into one step by
`mergeKey`; an absolute setter is the only mergeable command). `web/lib/editor/doc-sync.ts` diffs
the local document against what the server last confirmed into the exact `PATCH` calls, so an undo
needs no server code: it changes local state and the next flush sends the reverse patch.
`useEditorDocument` (in `web/components/editor/thumbnail/`) sends a batch about 700 ms after the last
edit, with `baseRevision` on the **first write of the batch only** (each write bumps the revision), and
answers a `409 REVISION_CONFLICT` by reloading with a notice. Leaving never loses that last batch:
unmounting the editor (another page of the app), the tab going hidden and `pagehide` all send what is
pending at once with `fetch(…, { keepalive: true })` (`WriteDelivery` in `image-documents.ts`), so the
browser finishes the writes after the page is gone. Layers the server creates (a cut-out,
text, an AI result) are undone by **hiding** them, since a row cannot be un-created; deleting a layer
is irreversible, is a confirmed immediate call, and clears the history.

**Choices a reader would otherwise have to rediscover.**
- The cut-out tool's leave-out mark is a mode (`Cut this out` / `Leave this out`, `SubjectPanel`), not only Shift-click, so a touch
  screen can make one. Arming the brush with nothing selected selects the original to paint on (`armMask`). On a touch screen a layer
  row wraps (name and kind on the first line, move and delete beneath) so the name is never squeezed out by five 44 px buttons.
- The layer model has one uniform `scale`, so a corner handle always keeps proportions. **Shift** therefore
  snaps rotation to 15 degrees and pins a move to one axis, rather than toggling an aspect lock.
- `kind` crosses the wire as the C# member name (`Cutout`); `parseLayerKind` and `normalizeLayer`
  (`web/lib/api/image-documents.ts`) lower-case it, once, for every response.
- `backgroundColor: null` (transparent) is sent as `""`; the canvas width and height are always sent
  together; both are the server's clearing rules (`documentPatchBody`).
- Masks (PNG) and exports (any of the six export types) are uploaded straight to storage with a presigned PUT from
  `assets/upload-url` (never through nginx), and a ticket is single-use, so every export attempt asks for a
  fresh one.
- `@dnd-kit` is not a dependency of `web/` (despite older notes); the layer list reorders with the
  browser's own drag and drop plus arrow buttons.

**The canvas is an infinite workspace.** `EditorCanvas` draws a viewport, not the frame:
`lib/editor/viewport.ts` holds the view (`{ zoom, x, y }`, document point × zoom + offset), fit, zoom
about a point, the wheel's zoom-or-pan decision and the two-finger pinch; `lib/editor/workspace.ts`
paints the workspace, the frame (checkerboard or background colour), every layer through
`composeLayers` with **no clip** — so a layer dragged past the frame stays visible beside it — then dims
everything outside the frame with one even-odd fill. Layers are composited on their own surface first,
with the background colour inside the frame only, so a blend mode blends exactly as in the export and
never with the workspace. Pan is Space-drag, the middle button, the hand tool or two fingers; zoom is the
wheel, a pinch, the corner buttons, or `+`/`-`/`0`/`1` with the canvas focused. The view is editor state:
never saved, never sent.

**An AI tool's work shows on the canvas itself.** While an operation or a cut-out runs, the affected
region — the painted area for erase and fill (`MaskPainter.paintedBounds`, carried through the layer's
transform), the whole frame for `generate`, the selected or source layer otherwise — is drawn blurred,
tinted and shimmering in place (`lib/editor/ai-effect.ts` samples the look, `drawEffectOverlay` draws it
by shrinking the region in halving steps and drawing it back, with the canvas `filter` where supported).
When the result arrives the canvas waits until the new layers' pixels have loaded, then resolves the
region from blur to sharp over 800 ms. `prefers-reduced-motion` gets a still blur and an instant result.
It is screen-only: never in the document or an export. (This replaced an earlier separate "reveal
preview", which is gone.)

**Exports are made in the browser, in six types** (`lib/editor/export-formats.ts`, pinned to the
server's `ExportFormats` by the drift test). PNG, JPEG (flattened onto the background colour or white),
WebP and AVIF (offered only when `canvas.toBlob` really produces it) are the frame flattened by
`composeScene`. **PSD** and **ORA** keep the layers: `rasterizeForExport` draws each layer on its own
surface at full opacity and normal blend (`drawLayer(…, { pixelsOnly: true })`), keeps pixels up to half
a frame past each edge (`rasterBounds`), and stores opacity, blend mode, visibility and name as layer
settings; a background colour becomes a bottom layer; text becomes pixels. The PSD is written by
[`ag-psd`](https://github.com/Agamnentzar/ag-psd) (MIT; brings `base64-js`, MIT, and `pako`, MIT and
Zlib), imported on demand so the editor bundle does not carry it; the ORA is a stored ZIP written by
`lib/editor/zip.ts` (`mimetype` first, `stack.xml` top layer first, `mergedimage.png`, a ≤ 256 px
thumbnail). Each file is either downloaded (`downloadBlob`) or saved through `upload-url` + `exports`
with its own content type. Layered TIFF and SVG are not offered: no maintained browser writer keeps
TIFF layers Photoshop reads, and an SVG would only wrap the same pictures.

**A picture added from the project is fitted.** `containFitTransform` scales a picture larger than the
canvas down to fit inside it and centres it; `useEditorDocument.addLayers` takes an `arrange` step so
the fit lands in the same undo step as the addition and reaches the server with the next flush.

**Designed graphics in the browser.** "Design a graphic" is in the AI tool picker's "Make something new" group
(`ai-tools.ts`, id `designGraphic`). `DesignGraphicPanel` takes the description, a placement and an optional size, shows
the cost line from the list's `estimate` (disabled when unaffordable or unavailable, with the server's reason), and lists
the document's jobs with the live stage of the running one (from the execution's SSE `step.progress`). The list is an SWR
read (`lib/hooks/use-image-graphics.ts`) polled every 4 s while a job is queued or running. While a job works, the canvas
shows the AI effect over its `target` (`EffectTarget` `{ kind: 'region' }`) unless another AI effect is on screen; when it
turns `Passed` its settled layer is brought in once, as one undoable step, revealed, selected and announced with the
summary. A layer whose provenance is `designGraphic` with a `workspaceVersionId` offers "Change this graphic" (in the
panel and in the layers list), which POSTs `revisesLayerId`. Pure logic: `lib/editor/graphics.ts`.

**The assistant's changes.** The platform assistant can change the open picture (see
[`platform-assistant-mcp.md`](platform-assistant-mcp.md) "Picture editor tools"). Each change arrives as a
`data-imageDocumentChanged` frame, is announced once per `changeId` as a window event (`lib/editor/assistant-events.ts`),
and the editor showing that document applies it through `useEditorDocument.syncFromServer`: pending local edits are
flushed first (a conflict takes the existing reload-with-notice path), the server document is adopted, added layers are
recorded as one undoable step and any other change restarts the history, then the changed layers are revealed and
selected with "The assistant: …". A queued graphic re-reads the graphics list.

**A whole photo, never a crop; a missing image never blocks.** A new picture opens at the photo's own
size: `documentForImage` decodes the photo in the browser (`measureImageInBrowser`, 5 s cap, never throws)
and sends it as `width`/`height` on the create request, which the server uses only when it cannot read
the file's header itself. The Canvas tab's **Original size** (`originalSize` in `history.ts`, one undo
step) sets the canvas to the photo's size with the long side capped at 4096 (`originalCanvasSize`) and puts
the photo back at the top-left at a scale that fills it. When a layer image fails to load,
`useLayerBitmaps` fetches the URL once to ask why. Only `404 LAYER_IMAGE_MISSING` (its file was deleted)
reports the layer as `missing`: the canvas draws a crossed-out placeholder where it sits, its row says
"Image missing", and Export leaves it out instead of waiting for it, with a note in the dialog naming the
layer. Any other failure (a network error, a `5xx`, a `404` without that code) is retried after
`BITMAP_LOAD_BACKOFF_MS` (0.5 s, then 1.5 s); if it still fails the layer is reported as `unavailable`,
and Export refuses, naming the layer, with a **Try again** button (opening Export also tries again), so a
flaky connection never gives a silently wrong export. Either way a change to the layers retries a failed
URL at most every 10 s. After any `424` from an AI operation or a
cut-out the editor reads the document again (`syncFromServer`), since the service may have written a
layer late. "Cut out a subject" asks the capabilities route as `removeBackground` (the same segmentation
service, `availabilityOperation` in `ai-tools.ts`), so a workspace without one says so before a click. The
AI panel stays mounted when another tab is shown, so a typed prompt survives. Before a photo is deleted in
Files, the dialog reads `GET …/files/{fileId}/picture-usage` (`getFilePictureUsage`, the only caller) and
says how many pictures use it and keep their own copy. When keeping those copies would exceed the storage
quota the server refuses the delete (`409 PICTURE_COPIES_OVER_QUOTA`, nothing deleted or copied); a Files
multi-select delete shows each refusal's own sentence via `describeDeleteFailures`
(`web/lib/files/delete-failures.ts`: identical sentences once, several quota refusals as one counted sentence,
the generic "N of M failed." only for failures without a server sentence, never ids or raw JSON).

**It fits the screen.** `useStageFit` measures where the layout starts and the padding below it and sets
`--stage-height` so the stage and panel end at the window's bottom edge (the CSS fallback is
`100dvh - 260px`); below 75em the panel is 300 px wide. "Fit" keeps room above the frame for the rotate
handle and below it for the corner handles clear of the zoom bar (`fitInsetsFor` in `viewport.ts`). On a
touch screen (`useCoarsePointer`, `(pointer: coarse)`) the layer and zoom buttons are 44 px, a finger
grabs a handle within 22 px, and "Add an image from this project" is a list of buttons to tap instead of
a dropdown (which did not open by touch on Android Chrome inside the scrolling panel).

**Where to change what.** Drawing and geometry: `compose.ts`, `transform-tools.ts`. Workspace, pan and
zoom: `workspace.ts`, `viewport.ts`. AI effect: `ai-effect.ts`. Exports: `export-formats.ts`,
`layered-export.ts`, `zip.ts`, and `renderExport` in `canvas-dom.ts`. The AI tool picker's list and
groups: `ai-tools.ts`. Canvas shapes and sizes: `canvas-size.ts`. Mask: `mask.ts`.
Text bounds and the font list: `text-content.ts`. The AI cards, their needs and the request each
builds: `ai-operations.ts`. Wire shapes: `web/lib/types/image-document.ts` and
`web/lib/api/image-documents.ts` only. `web/lib/types/__tests__/image-document-enum-drift.test.ts` pins
the `kind`, blend-mode and operation lists to the C# enum and the contract.

## Storage, ownership and revision history

The timeline **document's** four tables are all **Inference-API-owned** (migrated under
`__EFMigrationsHistory_Api`, added by `AddEditTimelineTables`) and mapped **read-only** in
`WorkflowEngineDbContext` with the same soft-delete filters, so engine-side code can query them
without owning their schema. The P2 media tables are API-owned too, and one of them is **not**
read-only in the engine — see
[the media section](#media-the-pool-the-bins-and-the-derivative-pipeline-p2).

| Table | Entity | Role |
|---|---|---|
| `edit_timelines` | `EditTimelineEntity` | The document row: `Id`, `ProjectId`, `Name`, `DocumentJson` (jsonb), `Revision`, `SeedStepResultId`, timestamps, `DeletedAt`. |
| `edit_timeline_ops` | `EditTimelineOp` | One revision's batch of client-authored ops, verbatim (jsonb). Append-only; `(TimelineId, Revision)` is unique. |
| `edit_timeline_snapshots` | `EditTimelineSnapshot` | A full document snapshot at one revision (jsonb). `(TimelineId, Revision)` is unique. |
| `media_derivatives` | `MediaDerivative` | Index of derived media artifacts (proxy / filmstrip / waveform peaks / keyframe index) in the object store. **Written by the WorkflowEngine**, which makes it the one table listed here that is not read-only engine-side; its identity is the single `cache_key` column. See [the media section](#media-the-pool-the-bins-and-the-derivative-pipeline-p2). |

`EditTimelineEntity` is deliberately named `...Entity` rather than `EditTimeline`: the *document*
type lives in `ReelBolt.Shared/Editing` and is what `DocumentJson` serializes, while the row
carries the relational columns (project, revision, seed step result) the API queries without
deserializing the document. `Revision` is an EF Core **concurrency token** on the Inference API's
own mapping (the engine's read-only mapping does not mark it) — that is what makes the race path in
PATCH fail loudly instead of clobbering. `DeletedAt` is a soft-delete marker hidden by the query
filter, and rows cascade-delete with their project and timeline. The ops and snapshots tables each
carry a unique `(TimelineId, Revision)` index and cascade-delete with their timeline; of the timeline
tables, only `edit_timelines` and `media_derivatives` carry the soft-delete filter (`media_bins` does
too — see [the media section](#media-the-pool-the-bins-and-the-derivative-pipeline-p2)).

### The op log and snapshots

Every PATCH that advances a timeline's revision writes exactly **one** op row, so the log is
append-only and `(TimelineId, Revision)` is unique. Alongside it, a full document snapshot is
retained every **20th revision** (`revision % 20 == 0` on the revision the PATCH produced). Ops and
snapshots share the same revision namespace. The op log is what makes history possible; P1 records
it and does not yet serve it.

### Reading a document back

Both GET and a successful PATCH return the single-item response shape: the document plus `id`,
`projectId`, `name`, `revision`, `seedStepResultId` and timestamps. Reading uses one JSON serializer
configuration in both directions — a `JsonSerializerOptions` built on `JsonSerializerDefaults.Web`
(camelCase, case-insensitive) with **deliberately no converter list**
(`EditTimelinesController.cs:143`) — so a stored document always round-trips. The string form for
`AspectPolicy`/`TrackKind` and the other three contract enums is not configured there: it is the
type-level attribute on the enums themselves (`EditTimelineEnumJsonConverter.cs:44`), which is what
keeps the stored form aligned with `EditTimeline.schema.json` for every other reader and writer too —
and what makes a bare integer a binding failure rather than a form this configuration could ever
produce (see [the document model](#the-document-model)).

An unparseable stored `document_json` is **data corruption**, not a client error: it is reported as
a failed parse and answered with a `500` that names the row, so an operator can find it — never a
`400` blaming the caller.

---

## Media: the pool, the bins, and the derivative pipeline (P2)

Everything above is the v2 document. This section is the **other** P2 deliverable — the media
workspace a user opens a clip from, and the pipeline that builds the cheap stand-ins the editor
scrubs with — and it is deliberately the only place both halves are described together. The engine's
own view of the same pipeline, with the same contracts stated from the writer's side, is
[`docs/video-editing.md`](video-editing.md) § "Media derivatives (editor v2)"; where the two
overlap, this page is the design record and that one is the pipeline record.

**What this is not.** There is no compositor, no renderer, and no editable timeline in P2. The media
workspace writes nothing to a timeline: in/out marks and subclip descriptors are **view state**, and
`SourceViewer` says so in words rather than implying a hidden write. The screen's playback is a plain
`<video>` over the source or over its proxy — WebCodecs, frame-accurate decoding and the compositor
are P3's, and the viewer's own header records that as a hand-off rather than an oversight.

### The pool, and the one token that addresses a source

`GET /api/v1/projects/{projectId}/media` returns the project's media pool: every `video/*`,
`audio/*` or `image/*` **project file**, union the renders that exist only as a
`WorkflowStepResult.OutputStorageKey`, newest first and capped at 500. A render that has been
*registered* as a project file is not listed twice — it is already the `file:` item.

A source is addressed only by its opaque **`mediaRef`** token: `file:<guid>` or
`step_result:<guid>`. The two shapes are not cosmetic. A registered render **is** a `ProjectFile`
row; an unregistered one has no row to point at, which is why `MediaDerivative.SourceProjectFileId`
is nullable and `SourceKind`/`SourceStepResultId` carry the reference instead. A non-nullable
foreign key there would make the render case unrepresentable, and the event, the row and every
endpoint all speak the same discriminated pair.

**No response from the media API ever carries a storage key, a bucket name or a presigned object
URL.** A derivative's bytes are reachable only through this API's own `/content` path, and every
storage key used internally is first checked against the project's own `projects/{projectId}/`
prefix. Every failure on every media route is **`404`** — a missing project, a soft-deleted one, a
foreign one, and a `mediaRef` belonging to another project all answer identically and with no body
that says which case applied. That is a deliberate deviation from `EditTimelinesController`'s `403`:
a project-wide inventory that distinguished "no such project" from "not yours" would be an existence
oracle a stranger could probe.

### Sound effects library

The pool has a **Sound effects** tab beside the media list (`SfxLibraryPanel`; on a phone a "Sound effects" row in `AddMediaSheet`, same store and ops). It lists the platform's built-in library (`GET /api/v1/sfx-library`: search box, category chips, a maximum-length filter, paging), plays a preview on tap or click (never on hover; `GET .../{id}/preview`), and has **Add** per row and **Add starter set**. Adding calls `POST /api/v1/projects/{projectId}/sfx-library/{id}/add` (or `.../starter-set`), which copies the sound inside the object store into `projects/{p}/userFiles/sound-effects/{slug}.{ext}` and registers it as an ordinary audio project file (`SourceKind = "sfx_library"`, pre-cleared, not counted in the storage quota) and answers a `file:<guid>` media ref. From there the existing placement path puts it on an audio track at the playhead. The pool shows an added sound as **Added**. No new op and no new keymap action. A project holds at most `SfxLibrary:MaxPerProject` library sounds (default 200; `409 SFX_LIBRARY_PROJECT_CAP`) and the add routes are rate-limited per user. The assistant and MCP can do the same add (`SearchSoundEffects`, `AddSoundEffect`). Provenance, licence rules, takedown and the rebuild procedure: [`sfx-library.md`](sfx-library.md).

### Bins

`GET`/`POST /api/v1/projects/{projectId}/media/bins` and
`PATCH`/`DELETE .../bins/{binId}`, plus `GET`/`POST .../bins/{binId}/items` and
`PATCH`/`DELETE .../bins/{binId}/items/{itemId}`. Bins nest, and **reparenting a bin under itself or
under any of its descendants is a `400` with a named error, never a persisted cycle**; nesting is
bounded at 16 levels. Deleting a bin soft-deletes its whole subtree and hard-deletes the items of
every bin in it.

**Smart bins (P6).** A bin created with a `smartFilter` (`MediaSmartFilter`: `keywords`, `kinds`,
`colorLabel`, `favourite`, `flag`, `nameContains` — clauses AND together, an empty filter is refused
by the client because it is "All media") has **no pins**: `GET …/bins/{binId}/items` evaluates the
filter over the pool at read time (capped at 500 sources, row ids deterministic per source so the
client can key them), `POST …/items` on it is `400 smartBinReadOnly`, and `PATCH` with
`smartFilter: null` turns it back into an ordinary empty bin. Keywords, notes, colour labels,
favourites and flags live in `media_annotations`, one row per source
(`GET …/media/annotations`, `PUT …/media/annotations/{mediaRef}` — omitted fields keep their value,
`colorLabel: null` clears only when the key is sent). See
[Workflow speed (P6)](#workflow-speed-p6).

A bin item is a **pin**, and a pin may dangle: the source row can be deleted, soft-deleted,
unregistered, or no longer under this project's `outputFiles/` prefix. The item listing renders such
an entry with its four resolved fields (`kind`, `fileName`, `mimeType`, `sizeBytes`) as `null` —
never a `500`, never a fabricated value, and never a dropped row, because the pin exists and the
client must be able to remove it. The pool payload carries each source's memberships as
`{binId, itemId}` pairs (always present, empty when unbinned) so a drag-out can name the pin it
means: `itemId` is visible nowhere else the pool client reads.

### The source viewer and the metadata panel

The viewer plays a source or its proxy, holds in/out marks, and can produce a subclip descriptor —
none of which touches a document or issues a request. The proxy toggle derives its availability from
exactly one observable fact, **whether a row of kind `proxy` exists**, and says "Proxy not ready
yet" when there is none rather than "failed", "unsupported" or "processing": the API's derivative
listing is a flat list of rows with **no `state` field**, so it cannot distinguish "not generated
yet" from "failed" from "impossible", and no UI may claim any of them. The Original position
plays a `file:` source through `files/{id}/media` and a `step_result:` source through
`outputs/{id}/download`; only a token of an unknown kind shows a stated reason instead of a
`<video>`.

The metadata panel renders eight technical rows — codec, container, frame rate, resolution,
duration, channel count, sample rate, bitrate — and every one is either a real fact or an explicit
unknown. **`null` means "not probed", never `0` and never an empty string**, and a `0` channel count
or a blank codec name would state something ffprobe never said. The probe comes from the one the
derivative run **stored at ingest** (see [Probe at ingest](#probe-at-ingest-the-stored-probe)); the
newest artifact of a `VideoAnalyze` step (selected by the step's type, so a newer compile EDL under
the same `video-analysis/` prefix is never read) is only the fallback for a source no run has probed
yet. There are two
levels of absence and both are stated: `probe == null` means neither source has a probe for the file,
while a `null` field inside a present probe means that field was never measured — the normal reading
of an artifact persisted before those fields existed. `hasAudio` (additive, nullable) is set only
from a stored probe. The container
row is the one that must not be over-simplified: `container` is ffprobe's `format_name` **verbatim**,
which for a sniffed MP4 is the comma-separated demuxer probe list `mov,mp4,m4a,3gp,3g2,mj2`. The
panel renders that readably while keeping the raw list visible, and falls back to the source's
**declared** MIME type — saying so — only when the probe carries no container at all, because a
declared type is a different fact from a sniffed one.

### The four derivatives: ffmpeg shape and storage layout

All four are produced by the WorkflowEngine from an **already-downloaded** local source, every
invocation scheduled at `VideoToolPriority.Low`. Everything a run produces lands under

```
projects/{projectId}/agentFiles/media-derivatives/{sourceHash}/{leaf}
```

where `sourceHash` is the **first 16 hex characters** of a SHA-256 over the length-prefixed join of
the source's storage key and its ETag — so every derivative of one source, of every kind and every
variant, shares one directory. Each leaf carries the first 16 hex of the **parameter hash**, so two
parameter sets of one source land as sibling objects and can never overwrite each other.

| Derivative | ffmpeg shape | Object leaves |
|---|---|---|
| **Proxy** (variant 0) | `scale=-2:{540 \| 720}:flags=bicubic` (landscape/square) or `scale={540 \| 720}:-2:flags=bicubic` (portrait); the target applies to the SHORT side, 720 once it is >= 2160, `libx264 -preset veryfast -crf 23 -profile:v main -pix_fmt yuv420p`, `-g` **=** `-keyint_min` **=** `max(1, round(fps × 0.5))`, `-sc_threshold 0`, `+faststart`; AAC 128k/48 kHz/stereo **only when the source has an audio stream**; `-protocol_whitelist file` | `proxy-<paramsHash16>.mp4` |
| **Filmstrip** (variants 0/1/2 = 1 s / 5 s / 30 s) | `fps=1/<density>,scale=160:-2,tile=10x10`, `mjpeg -q:v 5`, `-f image2` over `sheet-%03d.jpg`; plus a second, encoder-less `fps=1/<density>,showinfo -f null -` **inventory** pass at `-loglevel info` | `filmstrip-<paramsHash16>-sheet-001.jpg` … (1-based, `D3`), `filmstrip-<paramsHash16>-index.json` |
| **Waveform peaks** (variants 0/1/2 = 20 / 100 / 1000 ms) | `-vn -ac 2 -ar 48000 -sample_fmt s16 -f s16le` to a **raw file** — never a pipe, because the runner captures stdout as text lines and would mangle every byte that is not valid text | `peaks-<paramsHash16>-lod<N>ms.raw`, `peaks-<paramsHash16>-index.json` |
| **Keyframe index** (variant 0) | ffprobe `-select_streams v:0 -show_entries packet=pts_time,flags -print_format json`; packets whose `flags` contain `K` | `keyframes-<paramsHash16>.json` |

Every derivative ffmpeg/ffprobe invocation that reads the source (the source probe included) pins
both the protocol (`-protocol_whitelist file`) and the demuxer (`-format_whitelist`
`MediaDerivativeInputFormats.Whitelist`: `mov,matroska,mp3,wav,aac,flac,ogg,avi,mpegts,mpeg,flv,asf,aiff,caf,w64,mxf`; `mxf` admits Sony XDCAM and Canon camera files).
The source is a user upload, and an HLS playlist, concat list, DASH manifest or SDP file read
through the `file` protocol could still name other local files; those demuxers are not on the list,
so ffmpeg refuses them. The scratch copy keeps the storage key's extension only when it is a known
media container (`.mp4`, `.mov`, `.mkv`, `.mts`/`.m2ts`, `.mxf`, `.mp3`, `.wav`, ...); any other extension becomes `.bin`.

Four decisions in that table are load-bearing rather than incidental:

- **The proxy GOP comes from the source's real frame rate, not a constant.** `-g` and `-keyint_min`
  are the same `max(1, round(fps × 0.5))`, and `-sc_threshold 0` forbids scene-cut keyframes — that
  trio is what makes a seek land on a known frame instead of wherever a scene change happened to be,
  which is the whole point of the proxy. The AAC arguments are **dropped entirely** for a source with
  no audio stream rather than emitted against a stream that does not exist.
- **The filmstrip deliberately has no `-frames:v 1`.** ffmpeg's `tile` filter emits one image per
  full 10×10 tile plus a final partial sheet, so a long source yields **several** sheets; pinning one
  frame would silently cap every filmstrip at the first 100 thumbnails and go dead past
  `100 × densitySec` seconds with no error anywhere. The thumbnail count cannot be recovered from the
  sheets either — `tile` pads the final partial sheet to the full area with black, so a 50-thumbnail
  and a 100-thumbnail sheet are both 1600×1200 — and counting from `duration / densitySec` is exactly
  the off-by-one that makes hover-scrub land on the wrong cell (measured live: a 12 s @ 25 fps source
  decimated at 1/5 s yields **two** thumbnails, not the three that arithmetic predicts). Hence the
  second inventory pass, whose `showinfo` count becomes the index's ground truth.
- **Peaks are always decoded to a fixed 2-channel 48 kHz s16le stream**, server-chosen rather than
  taken from the source, so one spec describes one byte layout regardless of whether the source is
  mono or 5.1 — which is what keeps the parameter hash, and therefore the cache key, independent of
  the source's channel layout. The object layout is channel-major
  `[channel][bucket]{int16 min, int16 max}`, little-endian, `channels × bucketCount × 4` bytes per
  LOD, with a companion sidecar `{sampleRate, channels, lodsMs, bucketCounts, layout, byteOrder}` so
  a client can index into it without guessing.
- **The keyframe index reads packet flags rather than decoding.** `IKeyframeExtractor` — the existing
  analysis-path pair — produces a *picture* at a caller-chosen time and never asks what the source's
  keyframes actually are, so it cannot answer "where may I seek cheaply?", which is the entire
  content of this derivative. Reading packet flags never touches the decoder at all. A packet whose
  `pts_time` is missing or `N/A` is skipped rather than treated as time zero: a phantom keyframe at
  0 s would make every seek plan wrong. The sidecar is `{timesSec: [...], count, gopHintSec}`, where
  `gopHintSec` is the **median** of the positive deltas — a single scene-cut keyframe in an otherwise
  uniform GOP would drag a mean away from the value that describes the file.

**Waveform peaks need sound.** A source whose probe reports no audio stream gets no peaks: the peaks
stage is reported as **skipped** with the reason, never as failed, and no peaks row is written. A run
whose every derivative was already cached ends `Done`, not `Skipped`. Every JSON artifact (the
filmstrip and peaks sidecars, the keyframe index) is written as UTF-8 **without a byte-order mark**,
so a strict JSON reader parses it as is.

**A filmstrip density the source cannot support is skipped before any ffmpeg runs.** `fps=1/<N>`
emits **nothing** for an input shorter than **half** its interval (measured with ffmpeg 8.0.1:
`fps=1/5` emits its first thumbnail at 2.5 s and none at 2.4 s; `fps=1/30` at 15.0 s and none at
14.9 s), so `MediaDerivativeService` decides from the source's own probed duration and throws
`MediaDerivativeUnsupportedException` — deliberately its own type, because the dispatcher reports a
`Failed` stage for every unexpected exception and a density that was never runnable is **not** a
failure. A source whose duration ffprobe did not report at all (`0` means "not measured", never
"zero") is still attempted, unchanged.

### Probe at ingest: the stored probe

Every derivative run probes its source once with ffprobe and stores the result on the source's
`media_derivatives` rows in the nullable `source_probe_json` column (migration
`AddMediaDerivativeSourceProbe`, owned by the Inference API like the rest of the table; the engine is
the writer). The value is a `MediaSourceProbe` — `durationSec`, `fpsNum`/`fpsDen`, `width`, `height`,
`videoCodec`, `audioCodec`, `container`, `audioChannels`, `audioSampleRateHz`, `bitrateBps`,
`hasAudio` — in web-default camelCase JSON, ffprobe's own numbers with an unreported field left
`null`. `hasAudio` is true exactly when ffprobe reported an audio codec, the same rule the proxy's AAC
arguments and the peaks stage use. A run that only hits the cache still fills the column on rows that
lack it. `GET .../media` reads this probe for each source (one query for the whole pool, the newest
readable row per storage key), and the seed endpoint reads it first to decide camera audio.

### One row per (source, kind, variant) — and the filmstrip's honest shape

A run inserts exactly one `media_derivatives` row per `(source, kind, variant)`. `StorageKey` names
that row's **primary** artifact, and which object that is forms part of the contract:

- **proxy** — the encoded mp4, its only artifact;
- **filmstrip** — the **first** sprite sheet (the `image/jpeg` artifact), because that is what an
  editor draws;
- **peaks** — that variant's **own** LOD raw file, never the sidecar: the sidecar is identical for
  all three levels of detail, so pointing every peaks row at it would give three rows one shared
  object and none of them the peaks they name;
- **keyframe index** — the JSON index, its only artifact.

So **a filmstrip density is ONE row and that row owns the density's primary sheet only.** Sheets
1..N−1 have no row of their own. They are addressed through the **owning row plus the sheet's
0-based index** in the density's own sidecar array —
`GET .../media/derivatives/{derivativeId}/sheet/{i}/content` (both `/content` routes honour HTTP
`Range`: `Accept-Ranges: bytes`, `206` with exactly the requested slice, `416` for an unsatisfiable
range, the range pushed down to the object store rather than read whole) — and never by matching a sheet's leaf
to a row, which would find nothing for every sheet after the first and silently break hover-scrub
past `100 × densitySec` seconds. The filmstrip-index endpoint resolves that for the client: it is a
verbatim pass-through of the writer's sidecar (geometry and measured counts are **never**
recomputed server-side), with each sheet carrying its own `contentUrl`. A sheet whose bytes have not
been uploaded yet answers its **own** `404` rather than becoming a hole in the index — dropping it
would renumber the flat thumbnail sequence a client indexes into.

The sidecar reaches the row through **`MediaDerivative.IndexStorageKey`**: for a filmstrip sheet row,
the storage key of that density's index sidecar object; `null` for every other kind and for a
filmstrip whose index object is not written yet. It is a **companion pointer, and deliberately not a
component of `CacheKey`** — it names an index *about* a derivative, not the derivative's content
identity, so folding it in would make a re-produced index look like a different derivative. It is
never unique-indexed (several rows of one density legitimately share the one sidecar), never
returned raw to a client, and a client that needs the index reads a `null` pointer as "not available
yet", never as "no filmstrip".

What is persisted is the key `UploadArtifactAsync` actually **returned**, not the one the binding
predicted. The two are compared, and a leaf mismatch is logged as a finding and produces **no
pointer at all** — pointing at an object the builder never wrote *as the index* would be worse than
naming nothing.

### Cache identity: `(source storage key + etag, params hash)`

A derivative's identity is the single **`cache_key`** column: a lowercase-hex SHA-256 over the
**length-prefixed** join of, in this order, the **source storage key**, the **source ETag**, the
**kind's slug**, the **variant**, the **parameter hash**, and the **builder version**. Every
component is emitted as `<length>:<value>|`, which is what makes the join injective: the components
are storage keys and hashes and a storage key may itself contain the separator, so a bare `|` join
could not tell `key="a", etag="b|c"` from `key="a|b", etag="c"` — two genuinely different sources
that would then share one object and one row. A value containing the separator changes its own
length, so it cannot trade characters with its neighbour to reproduce another component set's
material. This is the same discipline `DecisionQuestionMemoizer` uses (uppercase there; lowercase
here, because these values are object-store path segments and S3-compatible stores are
case-sensitive).

The parameter hash is itself a length-prefixed SHA-256 over the canonical parameter JSON **plus the
builder version**, so changing a builder's argv invalidates its old objects automatically. The
parameter JSON is written by a purpose-built canonical writer — keys sorted ordinally, numbers in
invariant culture, no whitespace — because a `Dictionary`'s or an anonymous type's member order would
make the hash a property of the runtime rather than of the file.

Two implementations exist and **must agree byte for byte**: `MediaDerivativeCacheKey.ComputeCacheKey`
in the engine (the producer) and `MediaDerivativeIdentity.Compute` in `ReelBolt.Shared` (the
reader). A single-character divergence would silently produce a cache that never hits rather than an
error, so a test asserts the equality directly on separator-bearing, empty-etag and empty-storage-key
inputs rather than trusting the mirrors to stay in step. The `MediaDerivativeKind` enum's member
names **are** the spec's slugs, pinned name for name, so a rename on either side fails loudly.

**Why one key and not three columns.** The first shape was a partial unique index over
`(source_storage_key, source_etag, params_hash)`, and it was not an identity: the parameter hash
covers a spec's knobs and its builder version but **neither the kind nor the variant**, so two
different kinds of one source that happened to share parameter JSON produced the same triple — and
the index then made the second derivative *unpersistable* rather than merely redundant. Folding the
kind and the variant in is what lets a filmstrip and a proxy of one source, and three densities of
one filmstrip, all coexist. The columns above are kept alongside the hash because a hash proves two
rows differ but cannot say which component did, which would make a cache miss or a corrupt
derivative undiagnosable after the fact.

**An empty ETag is legitimate and never reaches a row.** The publisher reads the ETag with a
best-effort S3 HEAD, and a failed HEAD must never fail an upload, so the event's `SourceEtag` may be
`""`. The dispatcher substitutes a SHA-256 of the **downloaded object's bytes** (`sha256:<hex>`) as
the content-identity component. Persisting the empty string instead would make every derivative of
every source whose HEAD failed share one component, so a re-uploaded *different* file would satisfy
an old row — precisely the collision this design exists to prevent.

> **A known, carried limit: the unique index is Postgres-only.** `ux_media_derivatives_cache_key`
> (`WHERE deleted_at IS NULL`) is the real arbiter across processes and restarts, and it exists in
> the migration — but **EF Core InMemory does not enforce unique indexes at all**, and this
> repository has **no Postgres-backed test**. It was verified executably that two rows sharing a
> `cache_key` both persist under InMemory. The collision guarantee is therefore proved by unit tests
> over the key function and by the migration's own DDL, **not** by an integration test. That is a
> live ceiling every wave that adds a table inherits, recorded as such rather than softened.

In-process idempotency is a second, weaker layer: concurrent deliveries for one cache key share a
single production through a `ConcurrentDictionary` of `Lazy<Task<…>>`. That is correct for **one**
process and is claimed for exactly that — a second WorkflowEngine replica would run its own copy and
lose the database race, which the unique index then settles.

### Lazy derivation: requested on explicit open, never on hover

**`POST /api/v1/projects/{projectId}/media/derivatives/request` is the only thing that asks for a
source's derivative set from the editor**, and the client issues it when the user *opens* a clip —
issued once per opened clip by a synchronous ref guard, so a re-render mid-flight cannot queue a
second round of encoding. A pointer crossing the pool's grid requests nothing: hover may *display* a
filmstrip that already exists, and it produces no work.

The rule is enforced structurally rather than by convention. `PrepareDerivatives` is a component
that exists **only while a clip is open** and is the sole caller of the request in the feature, and
the workspace page keys the detail pane by `mediaRef`, so opening another clip mounts a fresh pane
that legitimately asks for *that* clip. **No `GET` ever writes**: the controller doc states it in as
many words — a list request that quietly triggered ffmpeg work would make a page load spend CPU, and
a retried `GET` would spend it twice. Since T-022 the editor also asks when a **timeline opens**,
once per source its clips use (`requestedBy: "timeline-open"`, `useRequestTimelineDerivatives`);
still never from a hover.

The endpoint answers **`202 Accepted` and writes nothing durable** — no `media_derivatives` row, no
bin row, no request log. Its only effect is publishing `MediaDerivativesRequested` with
`requestedBy` defaulting to `"editor-open"`. So `202` means *published*, never *produced*: the UI
copy says "requested", and the derivative list is **re-read** rather than assumed, which is why the
panel has a "Check again" affordance (kept as a manual fallback: the pane also re-reads on its own
when a `media.derivative.progress` stage arrives). A failed request is reported as a failed **request**, never as
"derivatives unavailable" or "generation failed", because the contract cannot distinguish those and
claiming one would invent a fact.

Two other sites publish the same event: a `video/*` or `audio/*` **upload** (`requestedBy:
"upload"`) and the registration of a render as a project output (`"render-output"`, which has no
publisher in this wave). **Every** upload path that creates a media project file publishes it — the
multipart form upload, the direct (presigned) upload the browser and the device folder sync use, and
the cloud drive sync — through one shared helper, after the row is committed, so footage has its
proxy, filmstrip and probe before anyone opens it. The upload path's ETag read **and** its publish sit inside one narrow
`try`/`catch` that logs a warning and lets the action continue to its existing `201`: a broker
outage must not turn a successful, already-committed upload into a `500` the client reads as "upload
failed". The catch rethrows `OperationCanceledException` when the request's own token is cancelled —
a cancelled request is never reported as a success — and does not swallow anything raised at or
before `SaveChangesAsync`, so a genuinely failed upload still fails.

An omitted `kinds` means **the kinds a derivative run produces**: proxy, filmstrip, waveform peaks
and keyframe index. It is not every `MediaDerivativeKind` member: `Thumbnail` rows come only from the
ThumbnailGenerate workflow step and the bitmap `Waveform` is superseded by peaks, so requesting them
on every upload only left statuses that never filled; a test pins the default set to the engine's
slug catalogue. Deciding what is worth producing per source is the **consumer's** job. The consumer narrows
that to a plan by MIME class — `video/*` is the full set (proxy + three filmstrip densities + three
peaks LODs + the keyframe index), `audio/*` is waveform peaks only (a filmstrip of an audio file is
a blank grid), and `image/*` or an unrecognised type is **nothing**, reported as a `Skipped` stage
with the reason. A caller's `kinds` can only **narrow** that set, never widen it.

### The derivative progress event

`MediaDerivativeProgress` is published by the WorkflowEngine through
`IMediaDerivativeProgressSink` — one message per stage transition of one run — and the Go API relays
it to the editor as the SSE `media.derivative.progress` frame. The stage is a **name** from the
closed vocabulary
`Queued | Probing | Proxy | Filmstrip | Peaks | KeyframeIndex | Done | Skipped | Failed`, carried as
a string rather than an enum because the producing vocabulary is engine-owned; `percentComplete` is
present only when the producer has a real percentage within a derivative (a proxy encode does, a
cache hit does not), and is `null` otherwise.

It is **deliberately not a `WorkflowStepProgress`**. That event is routed by its execution, step and
step-result ids and relayed as `step.progress` into the per-execution workflow monitor, so
publishing it from a derivative run would mean inventing three ids and rendering a **phantom step
card** for work no step ever did. A derivative has no step identity and must not pretend to one —
which is also why `CacheKey` is `null`, not `""`, on a run-level transition that is not yet
attributable to one derivative: an empty string would claim an identity that does not exist.

The message is **ephemeral and supersedable, never persisted**, and a `Done`/`Skipped`/`Failed`
stage is a UI hint rather than a completion signal: the committed `media_derivatives` row remains
the only durable statement that a derivative exists. Publication is **fire-and-forget** —
`Report()` is called from a path whose own success is already durable (the object uploaded, the row
inserted), so awaiting a broker there could fail work that already succeeded; every publish failure
is swallowed at `Debug` and an absent bus is legal. The sink takes the singleton `IBus` rather than
the scoped `IPublishEndpoint`, because a derivative runs on a process-lifetime task with no consume
scope to resolve from.

**The relayed frame is projected, not forwarded verbatim.** The event carries
`SourceStorageKey` — internal bus traffic, exactly as on the request event — and the Go API's relay
whitelists only `{projectId, sourceProjectFileId, stage, percentComplete, detail, occurredAt}`. The
storage key and the cache key are dropped before a browser sees them. The frame is scoped by
**project** rather than by execution (the only id a derivative has), so a non-admin subscriber is
authorised against project ownership, and an event carrying no project id at all is dropped rather
than broadcast.

Back to the document itself: [the sync protocol](#the-sync-protocol) and
[the trust boundary](#the-trust-boundary) are what a P3/P4 timeline will be built on, and neither P2
screen touches them.


### Analysis on demand per media file (Wave 2, R09.1)

A clip whose asset is a workflow render output (`assetRef.kind = stepResult`) is analysed the same way, by the mediaRef `step_result:<id>`
(the same rows and digest cache as `file:<id>`). Proposals read those rows too; an output the hold gate does not serve contributes no facts.

The silence, transcript and Cut panels no longer depend on a workflow run's seed analysis. An editor asks for an
analysis of one media file, the engine runs it, and the panels read the result by the file's `mediaRef`.

| Method | Path | Notes |
|---|---|---|
| `POST` | `/media/{mediaRef}/analysis` | `{kinds:['silence','shots','motion','events','transcript','beats'], language?}` → `202 {requestId, mediaRef, states}`. Edit. `402 INSUFFICIENT_CREDITS` when a transcript is asked for and the balance is below its estimate (nothing is queued then, free kinds included). Never triggered by a `GET` or a hover. |
| `POST` | `/media/{mediaRef}/analysis/estimate` | Same body, nothing persisted → `{billingEnabled, credits, seconds, balanceCredits, affordable, cached, unavailableReason}`. |
| `GET` | `/media/{mediaRef}/analysis` | View. Per kind `{state: none\|queued\|running\|ready\|failed, reason, requestId, updatedAt, summary}`; summaries carry counts only (silence ranges, shots, motion windows, transcript words with estimated vs measured). |
| `GET` | `/media/{mediaRef}/analysis/{kind}` | View. The data: silence ranges, shots, motion windows (`still\|low\|high`), transcript words `{text, startSec, endSec, estimated}`, beat grid (per-beat times, downbeats, tempo, drop, confidence). `409 {state, reason}` until ready. At most 20000 items (100000 words), `truncated` when cut. |

**Events.** The free `events` kind runs the layer-1 video event detector (`VideoEventDetector`: one gray 160x90 pixel pass plus speech edges and beats from the audio) and stores a bounded list `{id, type, frame, timeSec, strength, region?, confidence, endFrame?, direction?, speed?, kind?}` plus `fps`, `frameCount`, `suppressed` (events dropped by the detector cap) and `version` in the same `media_analysis_results` table (new kind value, no migration). `type` is the snake_case word (`appear`, `move_start`, `flash`, `caption_tick`...). The four optional fields are what the detector measured beside the frame: the last frame of a spread-out event, the direction and speed words of a move, and the kind of picture (`graphic`, `text` or `footage`); each is left out when the detector gave none. The plain words of an event are not stored: a reader works them out with `VideoEventWords.What`, which has one implementation for the analysis artifact's event and for this one. The summary is `{eventCount, byType, suppressed, version}` (plus `degraded: true` when a part of the detection failed, see below); the Analyse panel shows the count by type. The timeline reads the data in the Events lane ([The Events lane](#the-events-lane)) and the helpers read it as moments ([Moments in the picture](#moments-in-the-picture)).

**Events results carry a version.** The cache key (organization, digest, kind, language) has no analyser version, so the stored `events` result and its summary carry one: `MediaAnalysisJson.EventsVersion` (now `edit-moments-1`; change it whenever the detector gives other events for the same file, or the stored shape gains a field). A row with another version or none (every file analysed before versions) is not current (`MediaAnalysisJson.EventsAreCurrent`). Neither is a row marked `"degraded": true`: the detector sets that mark (in the result and in its summary, left out when false) when a part of the detection failed, for example the speech or the beat pass, so events are missing. A row that is not current is still served: its state reads `ready` with `outdated: true` and the data route returns it, so the Events lane shows its ticks (the web reader tolerates the fields an old row lacks). The engine does not count it as a cache hit, and the next `POST …/analysis` for the kind queues it again. The helpers get no moments from a row of another version; a row of this version that is only marked degraded still gives them its moments. The fresh result then replaces the old row in place (`MediaAnalysisFinishedConsumer`; a fresh result that is degraded too also replaces it and stays outdated), so there is still one row per key and no migration. Nothing is recomputed on its own: the lane counts an outdated file among the files to analyse (`needsEvents`), so "Find events in this video" is offered for it and one click replaces the result. While that run is queued or running the file's newest request is not ready, so the lane shows no ticks for the file; they come back when the new result is ready. If the run fails, the file's state is `failed` and the lane shows no ticks for it (the data route answers `409`, a reload included) until a later run succeeds; the old result stays stored and "Find events" is offered again. The same holds for a result an older engine writes while a deploy drains. Only the `events` kind has a version; the other kinds are unchanged.

**Free and metered.** Silence, shots, motion and events are deterministic ffmpeg (the engine's `FfmpegSilenceDetector`,
`FfmpegShotDetector` and a `signalstats` motion pass) and cost nothing; the `beats` kind is a free pure-C# tracker (below). The transcript calls the default
Transcription provider through the existing chunked path and is metered in `AsrAudioSeconds` through the catalogue
price (`INonWorkflowChargeService.EstimateAsync`, never a second estimator). The estimate is the file's stored probe
duration (`source_probe_json`) priced as ASR seconds; a file whose length is not yet known answers
`unavailableReason` and `409 TRANSCRIPT_UNAVAILABLE` rather than guessing. The balance for the estimate is a plain
single-row read: it does **not** take the per-organization ledger advisory lock (only the hold and settle writes do).

**Beats.** `beats` costs nothing and takes no hold: the engine decodes the audio, reads it with
`WavRmsSampler.ReadMonoSamples` and runs `MusicBeatTracker`, a dynamic-programming tracker that follows tempo drift
(see [`docs/video-editing.md`](video-editing.md#the-editors-on-demand-beat-tracker)). The result is
`{bpm, beatSec, beatPhaseSec, barPhaseSec, beatsPerBar, beatTimesSec, firstDownbeatIndex, durationSec, analysedSec,
dropSec, outroSec, confidence, steady, tempoWord, halfTempoScore, doubleTempoScore, truncated}`: `beatTimesSec` holds
the real per-beat times, downbeats are every `beatsPerBar` beats from `firstDownbeatIndex`, and `steady` is false when
the local tempo drifts more than ±2 % from the median. Only the first **20 minutes** (`MaxBeatSeconds`, 1200 s) are
decoded and tracked; `truncated` is true when the file is longer than that, when an unknown-length file fills the
20-minute decode, or when the beat list exceeds the item cap. A file
with no audio fails with "This file has no audio track."; one with no steady beat fails with "No steady beat was found
in this file."

**Cached by file digest.** The engine downloads the file once, hashes it (SHA-256) and looks the digest up in
`media_analysis_results` (key organization + digest + kind + language), so the same bytes reuse the result across the
projects of one organization and never across organizations. A cache hit runs nothing and charges nothing; the
transcript's up-front hold (taken at request time under the ledger lock, request id = the row id) is released. A
produced transcript settles the hold at what the recogniser metered (a `UsageScope` with source `Editor`), but never
below the hold when the usage was unpriced or unmetered or the metered amount is missing or 0 (a failed meter never
makes paid work free); a failed one releases it, so a platform failure is never charged. **Estimate equals reservation.** The hold is priced from the stored probe duration and that length (`heldSeconds`) travels
in `MediaAnalysisRequested`; if the real audio is longer the engine transcribes only the first `heldSeconds` (trim, not
refuse, so the charge is predictable and never above the hold), and the transcript covers that span. A kind already queued, running or ready for the same
project file is reused (no second job, no second hold).

**State and pipeline.** `media_analysis_requests` (one row per project, media, kind, language) and
`media_analysis_results` are API-owned (migration `AddMediaAnalysis`; the engine maps the results table read-only with
`ExcludeFromMigrations`). The API publishes `MediaAnalysisRequested` to the engine's own `media-analysis` queue
(prefetch 1, one at a time); the engine answers `MediaAnalysisStarted` (queued → running) and
`MediaAnalysisFinished` (every kind has an outcome: ready, or failed with a plain sentence such as "This file has no
audio track."; exception text never reaches a response). A run the engine never answers is reported failed after 60
minutes. Progress rides the existing media derivative progress event: stage `Analysis` (detail = the kind, percent 0 or
100) and `AnalysisFailed` (detail = the plain reason); no new event type.

## The editable timeline shell (P3)

P3 is the first phase in which a person edits a v2 document on a screen. Everything in it is built
on the P1 contract without extending it: the thirteen ops, the full-document `PATCH`, the validator
as the only server trust, and the inverse *program* an `apply` returns. This section records the
decisions a later phase would otherwise have to rediscover.

### The "Early preview" label

Every editor surface — the hub (`/editor`), the timeline header on desktop, tablet and phone, the
phone review page and the picture editor — shows one shared label,
`web/components/editor/shell/EarlyPreviewLabel.tsx`: a neutral Mantine `Badge` (12 px, default
variant, not violet: it is a status, not an action) inside a 44 px button that opens a popover with
`EARLY_PREVIEW_SENTENCE` on click, tap or Enter. On a phone it sits at the right end of the status line
under the action bar, in words, so the header keeps its room for the timeline name and save state. On the hub the header row wraps and the project picker is the item that
gives way: it shrinks first, and below 48em it takes its own full-width line (44 px tall) under the
title, so the label is never covered or clipped (`hub.module.css` `.headerTitle`, `.picker`). It stays on the whole editor until the maintainers lift it after launch (audit
decisions D2+ and D12); the editor UI never says "beta".

### The store, and why UI state is not in the document

`web/lib/editing/store/editor-store.ts` holds exactly two things: the **document** (only ever
replaced by `apply`, never mutated) and the **UI state** (playhead, selection, in/out marks, tool,
snapping, zoom, scroll, active workspace, per-track overrides). Components read it through
`useSyncExternalStore`, so a canvas repaint and a panel re-render observe the same snapshot.

`dispatch(op)` runs the engine, pushes `{ op, inverse }` onto the history, truncates any redo
branch, and marks the document dirty. `dispatchBatch(ops, label)` applies several ops as one
history entry and is **all-or-nothing**: a failure at op *k* replays the inverse programs of ops
*k-1 … 0* and leaves the document exactly as it was. Undo applies the entry's **inverse program**
(every op of it, in order), redo re-applies the op; `jumpTo(cursor)` walks whichever direction is
needed. Because the inverse is a program and not a guess, undoing an `overwrite` that had bladed
and lifted three items restores all three.

UI state lives **outside** the document on purpose. A playhead move or a selection change is not a
revision, must not be sent, and must not be undoable. The same rule keeps **track lock / enable /
mute / solo in P3 as UI overrides** (`setTrackOverride`): the thirteen-op catalogue had no track op,
so these flags could not be persisted. They did everything a user expects locally — a locked track
refuses edits (`refuseIfUiLocked`), a disabled or muted track drops out of the preview, solo
narrows the mix. **Since P4** the header toggles dispatch `setTrackProperty` and the override is
cleared for that flag, so the document is the one truth; the override path survives only for a
conflicted (read-only) timeline, where the preview must still obey a lock the server will not take.
Timeline settings edit through `setTimelineSettings` the same way.

### Autosave: the sync client, and what `markSaved` protects

`web/lib/editing/sync/sync-client.ts` watches the store, debounces (500 ms by default) and sends
**one** `PATCH` carrying `baseRevision`, the full current document and the ops dispatched since the
last confirmed save. It validates locally first (`assertInvariants`): a document the engine would
not accept is never sent and the status reads `invalid`.

The contract with the store is the subtle part. The user keeps editing while a `PATCH` is in
flight, so when the `200` arrives the client must **not** replace the document (that would discard
the newer edits and the undo history with them). Instead `markSaved(revision, sentOpCount)` records
the new revision and trims exactly the ops it sent off the pending prefix; anything dispatched after
the send is still pending and goes with the next save. The response's document is deliberately
ignored on success — the server stored what we sent, and the client is the only one applying ops.

| Response | Client state | What the user sees |
|---|---|---|
| `200` | `saved`, revision advanced, sent ops trimmed | Status chip "Saved · rev N" (the page header says "Version N") |
| `409` | `conflict`, **read-only**: the store is frozen (`EditorStore.setReadOnly`); the local document stays on screen, unsaved, until "Reload latest" fetches the server's | Banner: someone else saved; reload to continue |
| `409` on the retry of a network/5xx failure, `currentRevision = base + 1`, head document equal to what the failed request sent | `saved` at `base + 1` for that request's ops (it had landed); later edits go next on the new base | "Saved · rev N" |
| `400` | `rejected`, violations kept, `reasons` mapped | Chip "Why?" lists the reasons; **Retry** |
| `413` | `rejected` | "The timeline is too large to save."; **Retry** |
| `403` / `404` | `error` | Permanent — no automatic retry; the reason and **Retry** |
| anything else / network | `retrying` with exponential backoff (1 s base, 5 attempts), then `error` | "Retrying…"; **Retry** sends now |

`409` is read-only rather than "merge" because P1 made the document the unit of concurrency on
purpose (see [The sync protocol](#the-sync-protocol)); a client that silently rebased its ops onto
someone else's document would be applying ops to a document it had never validated them against.
**Saves never silently stop** (T-014; audit eng D8–D12, ui1 D7):

- **One flush at a time.** There is never more than one `PATCH` in flight. A `flush()` while one
  runs returns *that* promise and requests a follow-up, which runs inside the same promise against
  the revision the first one produced — two requests never carry the same `baseRevision`.
- **Only a document change schedules an autosave.** The client compares the store's `doc`
  reference on every notification; playhead, selection and zoom ticks keep the reference, so they
  neither restart the 500 ms debounce (autosave used to starve during playback) nor cut a retry
  backoff short (a failing `PATCH` used to be re-sent on every click).
- **Leaving the page flushes.** `attachLifecycle(window)` flushes at once on `beforeunload`, on
  `pagehide` and on `visibilitychange`→hidden with `fetch(…, { keepalive: true })` so the request
  outlives the page. On a full navigation `beforeunload` comes first, while the page is still whole;
  `pagehide` fires only once the next page has answered, and a keepalive first issued there was lost
  about one time in four on mobile Chromium (T-073). A later event only joins the save in flight.
  The browser's 64 KiB keepalive quota (`KEEPALIVE_BODY_LIMIT`) is shared by every keepalive
  request still in flight, so `patchEditTimeline` counts the bytes it has out and sends a body that
  would overflow what is left as a normal, best-effort fetch (one the browser would refuse is lost
  outright). If a normal `PATCH` is already in flight, the browser aborts it with the page and the
  server honours the abort, and a queued follow-up would die too: so the newest document goes at
  once with keepalive on the **in-flight request's base**, carrying every op since that base — with
  no edit since, that is the in-flight body again, identically. Exactly one of the two can
  win and neither can overwrite anyone: if the in-flight one lands, the keepalive is a harmless
  `409` (the same document); if it is lost to the unload, the keepalive saves everything. (`base + 1` would save more
  often but could silently overwrite another writer's revision.) If the page survives (bfcache, a
  tab switch) the outcome is folded in: a success is a save, a 5xx is an uncertain request, the
  expected `409` is ignored. `beforeunload` still warns while dirty (the save is already on its way). `dispose()` — which the timeline route
  calls when it unmounts, i.e. on every route change — flushes the pending edit (also with
  keepalive) **before** clearing its timers and listeners.
- **A request that failed in transit may have landed.** The client remembers every request that
  failed with a network error or 5xx (its document and op count) until the revision moves. A later
  request at the same base that answers `409` with `currentRevision = base + 1` makes the client read
  the head and compare it with the documents those requests *sent* — not the current one, since the
  user may have edited during the backoff (key order and absent-vs-`null` ignored). On a match the
  save counts as done at `base + 1` for exactly that request's ops, and the edits made since go next
  on the new base, instead of a false conflict that would lock the user out. If the head itself
  cannot be read, whether the request landed is unknown: the client keeps retrying with backoff
  ("The last save could not be confirmed. It will try again.") and never turns the blip into a
  conflict.
- **Read-only really is read-only.** While the client is in `conflict` it freezes the store:
  `dispatch`/`dispatchBatch` (Delete, Ctrl+B, Remove-silence apply, every panel edit) throw
  `EditEngineError` with a plain sentence, `undo`/`redo`/`jumpTo` (Ctrl+Z, the History panel) do
  nothing, and `replaceDocument` (revision restore) throws. View state (`setUi`) still moves. This
  is a store-level guard, so a control that forgets its `readOnly` prop cannot write.
- **Reasons and Retry.** Every refusal carries `SaveState.reasons`: plain sentences mapped from the
  server's `[{ path, message }]` violations or `{ message, errors }` body — array paths become
  "Track 1, clip 2", ids and an `<op>:` prefix are removed, camelCase field names are spelled out;
  text that is not a sentence is dropped for the plain "The server refused the timeline." — a
  JSON/HTML body, an exception or stack trace, a URL, a System.Text.Json/model-binding error (a
  `Path: $…` JSON path, `LineNumber`/`BytePositionInLine`, a type discriminator, the default "The X
  field is required."), a dotted .NET type name (`ReelBolt.Shared.Editing.EditOp`), or a message
  still naming an op (a camelCase op name, or any op name in quotes), so an op name never shows up
  spelled out as "set keyframe". The status
  bar shows them behind a **Why?** button and offers **Retry** (`SyncClient.retry()`) in the
  `rejected`, `error`, `invalid` and `retrying` states.

### Revision history and restore

Three endpoints joined the four of P1 — `GET …/history`, `GET …/revisions/{rev}` and
`POST …/duplicate` — documented in the sync-protocol table above. The design decision that matters:
**restore is not an endpoint.** The server never replays ops, so the only revisions it can hand back
are the ones a stored document exists for (revision 1, every 20th, and the current one). The panel
fetches such a document and the client submits it as an ordinary `PATCH` — same `baseRevision`
check, same validator, same `409` if someone else saved meanwhile — tagged with a
`{ "type": "restore", "revision": N }` marker in `ops`, which the server stores verbatim like any
other op it does not interpret. History therefore stays append-only and a restore is itself a
revision. The Versions panel asks before it restores (the restore clears the session's local undo history).

### The program viewer plays the timeline, not the last mp4

`web/lib/editing/preview/` is the compositor. The **scheduler** is pure: `activeLayersAt(doc,
frame)` returns the enabled items covering a program frame in stacking order (video tracks bottom
to top, UI overrides applied), `sourceFrameFor` maps a program frame through `sourceIn` and
`playbackRate` to the clip's own frame, `evaluateKeyframed` interpolates (`linear`/`step`/`ease`)
and `activeAudioAt` yields the gain per audio layer with mute/solo resolved. Every number the
compositor draws comes from these functions, so the unit tests pin the mapping without a browser.

Three session kinds, chosen at open time by `createPreviewSession`:

- **live** — `mediabunny` demuxes the proxy and WebCodecs decodes it (one `DecodeLane` per
  **clip** — `layer:<track>:<item>` — pooled by `DecoderPool`: a lane that is no longer on screen
  is **parked** — its source stays open with no decoded frames held, at most 4
  parked lanes for at most 10 s — so an A/B/A/B montage returns to A's open lane instead of fetching
  A again at every cut; concurrent requests for one lane share a single open, so a cold layer opens
  its source once rather than once per frame; a source that fails to open is tried again after 2 s,
  doubling up to 30 s, while its layer stays on screen). Playing, the render tick never waits for a
  source to open: it starts the open and shows the layer as missing meanwhile. mediabunny rejects a
  decoded frame whose colour primaries, transfer or matrix is outside the WebCodecs enums, and WebKit
  reports a stream that does not signal them (an ffmpeg output that tags only its matrix, such as a
  vertical phone clip) as `"unspecified"`; `colorspace-guard.ts` makes such a value read as unknown
  before the first source opens, so that clip is drawn instead of black on Safari. Drawing never
  reads those values. Frames composite through a WebGL2 program (`gl-compositor.ts`) with
  titles/captions rasterized to a 2D canvas overlay (`overlay-raster.ts`, font family sanitized);
  audio plays through `AudioGraph`, and **the audio clock is the master** — the video frame drawn is
  the one the audio clock says is due, never the other way round. The master can abdicate: a
  context that is not `running` (autoplay policy, no output device) reports no clock, and a clock
  that stops advancing for 300 ms of wall time is declared stalled — either way the session hands
  the clock to the wall from the current frame, because a master clock that never moves would
  freeze the video, which the headless e2e run demonstrated before this rule existed. A clip whose
  audio buffer is still being fetched and decoded when its start time passes **joins late, in
  sync**: it is scheduled against a fresh reading of the clock, from the frame the clock is at, never
  from its first sample (which lagged the picture by the decode time on the first play). The WebGL2
  context keeps its drawing buffer (`preserveDrawingBuffer`), so "Save still", the reference-still
  grab and the scopes read the picture on screen. Each source keeps its shape: the quad is scaled by
  the timeline's aspect policy (`fit` letterboxes or pillarboxes, as the render does; `fill` covers;
  `stretch` fills).
- **fallback** — no WebCodecs or no WebGL2: a plain `<video>` element over the proxy of the clip
  under the playhead. Cuts and stacking are approximated; the status bar says `fallback`.
- **unavailable** — nothing can play (no proxy yet, no media element); the viewer shows why.

**Which bytes play.** `ProxyUrlResolver` (`proxy-urls.ts`) asks the derivative list once per source
and keeps the answer however often the editor re-renders: the proxy when a row exists, else the
**original** — a `file:` source through `GET /api/v1/projects/{p}/files/{id}/media` and a
`step_result:` source through `GET /api/v1/projects/{p}/outputs/{id}/download` (both inline,
Range-aware, 404 outside the project; never the text editor's `files/{id}/content`, which answers 409
for media). When the media pool changes, sources without a proxy are asked again, and the viewer
re-presents the paused frame as soon as a URL arrives. A layer the session cannot draw is reported
(`missingLayers`) and the picture says why in one sentence: "Preparing preview…" while the lookup
runs or the clip's first picture is still being decoded, or the failure in plain words (the source
could not be opened — the preview tries again on its own — or has nothing readable). A late frame
during playback repeats **the same clip's** last picture (the compositor keys its textures by layer
and content: clip and source); after a cut to a clip with nothing decoded yet the layer is drawn
empty and reported missing, never filled with the previous clip's footage.

**Parity classes** (`parity.ts`) are reported, not assumed. Each item under the playhead is
`faithful` (a cut, stacking order, constant gain, an enabled flag), `approximate` (keyframed values,
titles and captions — the render's text layout is not this canvas's) or `renderRequired` (anything
the compositor does not implement). A clip in a colour group is judged by the group's grade stack as
well as its own. The viewer's badge (`honesty.ts#previewBadge`) shows the worst class at the
current frame, and is **never "Preview faithful"** while a layer is missing, a picture clip on
screen is not playing from its proxy, or the session is the single-clip fallback: it reads
"Preview not ready" (a missing layer, a clip without a proxy, no session), or "Approximate preview"
for the fallback. The render — P4's `StepType.TimelineRender` — is the authority and the badge
never claims otherwise.

### Degradation

Dropped frames are **counted and shown**, never hidden. `DropFrameLedger` records every deadline
the compositor had and every one it missed; the status bar's **Playback details** button (`playback-details-toggle`, 44 px on touch) turns yellow, then red above 5 %, and
opens a popover with "Skipped frames: N (p%)", the preview kind ("Live preview" / "Simple preview") and "Saved preview frames: N of M"
(the elements keep their test ids `dropped-frames`, `session-kind`, `render-cache-chip` and stay in the DOM while closed). Two knobs let a user trade fidelity for smoothness explicitly —
preview quality (Auto / Full / Half / Quarter) and the "Simple preview" switch — and the choice is a UI
convenience, not document state.

The decoder pool keeps `maxDecodedBufferedFrames >= lookAheadFrames + 2` (12 / 18 by default);
`assertPoolInvariant` throws on a configuration that violates it, because a pool that cannot hold
the look-ahead plus the frame being shown plus one in flight stalls on every seek. The headless
numbers in `web/app/spike/` were measured on SwiftShader and are **lower bounds**: a real GPU does
better, and no test asserts an upper bound on frame time.

### Preview readiness, the truthful badge and the device-aware session (R04.1, R04.2, R04.5, VF12)

**Readiness (`preview/readiness.ts`).** Opening a timeline already asks for the derivatives of every source it
uses (`useRequestTimelineDerivatives`: one `POST /media/derivatives/request` per source per open). The viewer now
also *listens*: `ReadinessStore` folds the existing SSE progress (`useMediaDerivativeProgress`, the
`media.derivative.progress` parser) into a per-source state - `ready` (proxy, or the original plays),
`preparing` (with the percent), `failed` (a `Failed` stage or a failed request) and `offline` (no proxy and no
original). The store is read by the viewer through `useSyncExternalStore`, so a progress event never re-renders the
editor shell. A stage past `Probing` makes the proxy resolver look the source up again, so a proxy built while the
editor is open is picked up without opening the Media pane. `viewerReason` produces the one line shown over a
picture with a hole ("Preparing preview 42%", "Source offline: ...", "Preview failed: ..." + **Try again**); the
viewer is never black without one. `ReadinessChip` is the project-level line in the viewer header (per-clip list,
Try again, **Prepare all** = `prepareSources` over `pictureSourceRefs`, requested with `requestedBy:
"preview-retry"`).

**The badge (`honesty.ts`, `RenderRequiredBadge`).** `previewBadge` returns `entries` (a line per reason, naming
the clip - `Clip "Interview": ...` - and carrying its `itemId`) next to the old `reasons`. Since editor-wysiwyg a clip
contributes one entry per **finding** (worst first), not one for its first matching rule, so a clip with a speed
change and a mask lists both. It is built from the
session as well as the document: a fallback session ("Simple preview"), an unavailable session, a clip with no
proxy, a layer with no frame, and a live session running at reduced quality (software graphics) are all
non-faithful. The badge is a button that opens a popover: the on-screen reasons first, the rest of the document's
non-faithful clips under "Elsewhere in this timeline"; a clip line selects the clip, moves the playhead to its start
and scrolls the timeline to it (`jumpToClip` in the workspace; on a phone it also switches to the Timeline pane).
**Retime is `approximate`**, not render-required: the scheduler maps frames through `playbackRate` and the audio
graph plays at that rate, so the timing is previewed; what the preview cannot do is the render's pitch-preserving
audio stretch and frame blending. (A non-positive rate is ignored by the scheduler and stays render-required.)

**Device-aware session (`preview/device-budget.ts`).** The Preview quality control is **Auto / Full / Half /
Quarter** (`ui.playbackResolution`, default `auto`, remembered in `localStorage` key
`reelbolt:editor:preview-quality`). `planPlayback(auto, facts)` treats a device as low-budget when it is a phone
(`device-class.ts`), draws with software graphics (`isSoftwareRenderer`), has `hardwareConcurrency <= 4` or
`deviceMemory <= 4`. **Auto always starts on the live preview, on every device class, a phone included**, so every
layer, mask, grade and effect is on screen; the plan's `simple` is always false. What Auto changes is the SIZE:
`fitMaxLongSide` is the long-side ceiling in device pixels (1280 normally, 854 on a low-budget desktop or tablet,
640 on a phone, 480 with software graphics) and the session draws the picture no larger than the viewer shows it
(see "Adaptive preview size" below). A low-budget desktop or tablet says why in `autoReason` ("The preview is
drawn at a smaller size because ..."), the viewer's reduced-quality line; a phone gets no sentence (its screen is
the reason). An explicit pick (Full / Half / Quarter) is an exact fraction of the timeline and is always
respected; the **Simple preview** switch is the user's own choice. The fallback is labelled "Simple preview" in the
viewer header, the badge and the status bar, with the line "Your edits are still exact in the final render".
`createDropWatch` watches the ledger while a live session plays: more than 25 % of the last ~3 s of deadlines
dropped (at least 30 deadlines) shows a chip offering **Switch to simple preview**, but only once the adaptive
size has no smaller level left (`session.adaptiveQuality.atFloor`). The e2e suite pins the remembered quality to
Half (`EditorPage.open` init script) so the specs written for the live compositor keep an exact canvas size on
machines that report few cores or software graphics.

**Adaptive preview size (`preview/resolution-governor.ts`).** This paragraph stands alone. The compositor's cost
follows the canvas size (the fill of every pass, and the working size of the effect chain, which is planned from
the canvas), so the live session keeps every effect on and changes only how large it draws:

- *Full playback size.* For a pick it is the timeline size times 1, 0.5 or 0.25. For Auto (`fitDisplay`) the
  viewer measures the picture frame (`ResizeObserver`, debounced 120 ms, CSS long side times
  `min(devicePixelRatio, 2)`) and calls `session.setDisplayLongSide(px)`; `fitRenderSize` snaps that up to a
  160 px step and caps it at `fitMaxLongSide` and at the timeline itself (never under 256 on the long side, even
  dimensions). Before the first measure the session starts at the plan's fraction, held under the ceiling.
- *While playing.* `ResolutionGovernor` judges windows of 1.5 s after a 1 s warm-up: a window is bad when more
  than 10 % of its slots were skipped in at least two separate skips; two bad windows in a row step the size down
  one level (`SCALE_STEPS` 1, 0.75, 0.5, 0.375 of the full playback size, never under 256 px). It never steps up
  within a play. What it learned is kept for the next play; after 20 clean windows in a row (30 s) at a reduced
  size the NEXT play starts one level higher. The frame-rate governor (`rate-governor.ts`) runs beside it,
  unchanged.
- *Paused.* `pause()` puts the canvas back at the full playback size and presents the frame afresh (titles are
  re-rasterised sharp), so a still, the scopes and a saved frame are never the reduced picture.
- *No flash.* Setting a canvas's size clears it; `compositor.resize` therefore repaints the held layers from their
  textures at the new size in the same call (no new decode). The real-GL harness pins it
  (`e2e/tests/gl-compositor`, "a resize repaints the held picture").
- *What the user sees.* `session.reducedQuality` is "Playing at a smaller size so every effect keeps up. Pause to
  see the full picture." while reduced; the viewer shows it as a pill ON the picture (`adaptive-size-pill`, so the
  viewer does not move when it comes and goes) and the parity badge lists it. `program-frame` carries
  `data-adaptive-scale`. `LiveSessionOptions.adaptive: false` pins the size.

**Why the viewer is not live, and the way back (`preview/fault-reasons.ts`).** `createPreviewSession` names ONE
cause and puts it on the session (`fault`, with `liveAvailable`); the viewer never infers it from capabilities.
`userChoice` (the Simple preview switch), `noWebGl2` (the browser gave the page no WebGL2
context: the reason carries the hint "turn on graphics acceleration (hardware acceleration) in the browser
settings, then Try live preview again"), `noWebCodecs`, `compositorFailed` (WebGL2 and WebCodecs exist but the
compositor could not start). Each has its own plain sentence. The actions sit on the picture: **Use live
preview** (`preview-use-live`) when the Simple preview was chosen and live could run, **Try live preview again**
(`preview-live-retry`) for `noWebGl2` and `compositorFailed`; both are 44 px on a touch surface. Retry bumps a
counter in the viewer's session effect: the session is rebuilt and the WebGL2 probe asks the browser afresh
(`reprobe`). **A lost graphics context** (`webglcontextlost`) is reported by the live session as
`contextLoss` - "The graphics context was lost. Reconnecting..." over the frame (`preview-context-lost`) and the
badge goes to "Preview not ready"; it clears when the browser restores the context and the compositor rebuilds. If
the rebuild fails the message says so and offers **Try live preview again**.

**One probe context (`preview/gl-probe.ts`).** Capability (`hasWebGl2`) and the software-renderer check
(`readDeviceFacts`) are answered by ONE throw-away WebGL2 context made with the compositor's own attributes
(`COMPOSITOR_CONTEXT_ATTRIBUTES`), read once and released at once (`WEBGL_lose_context`). The page then holds the
compositor's context only (the scopes read the composited canvas and make none). `resetGlProbe` forgets the answer
for a retry.

**Viewer skeleton (VF12).** The program frame (`data-testid="program-frame"`) has its aspect and size from its first
render (a container-query box), the phone layout flag is read on the first render (it used to arrive in an effect
and moved the viewer), and until the first session exists the frame shows a neutral grey skeleton instead of black
or a "No preview" alert (`data-booted`). The e2e check records the frame's size from its first paint and requires
it to equal the final size.

### Playback performance budget

**Target.** A representative multi-track timeline plays at its frame rate with the dropped-frame counter
reading about zero over a sustained play: 1080p sources, V1 (clip, 30-frame dissolve, clip) + V2
(picture-in-picture) + V3 (a title) + A1 + a music track, 30 fps, desktop Chromium. The viewer holds the
display's 60 Hz (requestAnimationFrame cadence) while presenting a new picture at the timeline rate.

**What was wrong** (profiled before anything was changed; headless Chromium on the dev box's AMD GPU through
ANGLE/EGL, and on SwiftShader; the same 22 s timeline, 12-14 s of play from frame 0):

| cause | measured | fix |
| --- | --- | --- |
| every look-ahead frame was a random-access `CanvasSink.getCanvas` (GOP re-decode + a 2-D `drawImage` readback per frame) | 5 fps, 77-82 % of deadlines dropped; `drawImage` 2.9-3.5 s and canvas `save` 0.7-0.9 s of main-thread time per 14 s | one forward `SequentialReader` per source (each packet decoded once); frames leave as `VideoFrame`s and are uploaded straight to the texture |
| the shell (`EditorWorkspace`, every panel) re-rendered on every playhead tick, plus a second commit for the stats counter | 2-3 commits a frame; React + Mantine about a third of the main thread; even one shell render a second is a 60-85 ms block (3-5 missed vsyncs) | shell reads a UI state whose playhead is frozen while playing; the timeline canvas, transport, status timecode and program viewer read the exact frame through `LivePlayhead`; playhead panels refresh 4x/s alone (`withLivePlayhead`); the counter is throttled to 4 paints/s and read by the status bar leaf |
| cuts and the dissolve opened their decode lane (and a decoder) cold in the middle of the play | a 70-180 ms main-thread block at each | lanes are per clip; clips due within ~1.2 s are opened and their first frames decoded ahead; demux/decode run in pooled Web Workers (booted while paused) |
| Play started the clock before the look-ahead was decoded; audio was fetched and decoded at Play | the first ~0.5 s of every play late | Play pre-rolls (<= 750 ms) before the clock and the audio start; audio buffers are preloaded and decoded concurrently |
| every presented frame re-uploaded held frames and the (static) title overlay | extra uploads per frame | skipped when the same image is already on the texture; overlay raster/upload cached per overlay set |
| software graphics (SwiftShader/llvmpipe) uploading 1080p/4K layers | 63 % dropped at 1080p, 85 % at 4K | decode scaled to the playback width (`ImageBitmap`, in the worker) and the viewer says "Reduced quality" |
| at 60 fps a steady 4-40 % dropped with an idle main thread: the audio clock (`currentTime`) advances in 10.7/21.3 ms device-buffer steps and was read once per 16.7 ms animation frame, so `floor(frame)` skipped pictures | the clock is smoothed: wall clock + the largest recent (`currentTime` - wall), decaying slowly (`AudioGraph.clockNow`); steady-state drops went to 0 |
| (backend) the media-derivatives consumer threw on every request, so no proxy was ever produced and the viewer always played the originals | `InvalidCastException ... DataTypeName 'text'` in `IsStillCurrentAsync` | `ProjectFile.ModerationStatus` string conversion in the engine's DbContext |

**Where the numbers come from.** GPU = the dev box's AMD Radeon AI PRO R9700 through HEADED Chromium (headless ANGLE/EGL
crashes the GPU process on the first `VideoFrame` upload and leaves the canvas black, so numbers from that path were invalid and the
spec checks pixels). Software GL (SwiftShader) cannot hold this timeline (10-32 % dropped); the viewer scales decode down, says
"Reduced quality", and the spec only measures it. The final measurements are in "Playback performance report" below.

**How the counter works.** `DropFrameLedger` records, per output frame slot the playback clock passes, whether a
composite was shown for it (presented) or the slot was skipped between two render ticks (dropped). A late
decoded frame is repeated and counted presented-with-repeat, never dropped. Paused, nothing is counted. The
status bar paints it at most four times a second ("Skipped frames: N (p %)" in the Playback details popover; the button turns yellow, red above 5 %) and exposes the
numbers as `data-dropped`, `data-deadlines`, `data-presented`, `data-repeats` (presented frames in which some layer re-showed a held picture instead of a new decoded frame: fresh = presented - repeats) and `data-achieved-fps` on
`[data-testid=dropped-frames]`; the chip's tooltip adds p50 lateness. The audio clock is the master
(`audio-graph.ts`); the playhead therefore advances at audio-clock speed, which the perf spec compares with the
wall clock.

**Budget and the spec.** `e2e/tests/editor-v2/14-playback-perf/playback-perf.spec.ts` (desktop only, never in
the expected-failures list) plays the timeline for 14 s six ways (proxy-shaped uploads, the real derivative
proxy, 1080p originals, a 60 fps timeline over 60 fps originals, 4K 30 fps, 4K 60 fps) and has PERF-02 (forced low budget) and fails when: dropped > 6 % of the deadlines (2.5 % with `EDITOR_E2E_PERF_STRICT=1`; 7 % / 4.5 % for the 60 fps
timeline), fewer than 90 % of the deadlines were **fresh** (every layer a newly decoded frame, `data-repeats` is the
rest), fewer than 3 of 3 compositor screenshots of the program canvas (before Play, after the measured play, after a short second play) differ (a frozen or black picture fails; nothing is sampled DURING the measured play, see the report), the
ledger's achieved rate is under 90 % of the timeline rate, the playhead speed is off the wall clock by more than 5 %
between two reads 8 s apart, the rAF cadence in steady play has a median over 20 ms / p99 over 100 ms / a gap over
600 ms, an AudioContext is not running or its clock is more than 300 ms off the wall over those 8 s, or fewer than three
audio sources (A1 x2 + music) were scheduled. It starts a headed Chromium of its own per measurement (the real GPU path;
a clean GPU process), waits up to four minutes for the machine's load average to fall (1-minute under 0.6 x cores, 5-minute under 0.45 x cores), prints
the load, and is **skipped with an annotation (never passed)** when it is still above (1-minute above 0.6 x cores (about
19 on the 32-core dev box: at load ~30 with other builds running, 30 fps read 9.9 % dropped and 60 fps 16 %, while the
bounds held below 19). Without a display, or with `EDITOR_E2E_PERF_GL=software`, it runs headless on SwiftShader, prints
the numbers and is skipped with an annotation: software GL is measured, not enforced.

    cd e2e
    eval "$(scripts/editor-v2-stack.sh env)"                      # local admin login
    npx playwright test -c playwright.editor-v2.config.ts --project=desktop \
        tests/editor-v2/14-playback-perf/playback-perf.spec.ts
    EDITOR_E2E_PERF_GL=software ...                               # force SwiftShader
    EDITOR_E2E_PERF_STRICT=1 ...                                  # the 2.5 % bound

The spec prints one `PERF-01 {...}` JSON line per run (renderer, load, counter, fresh frames, canvas hashes, ledger fps,
cadence, audio drift) and writes it next to the test's artifacts.

**Audio clock.** The smoothed clock (`AudioGraph.clockNow`) follows the wall clock but is held within
40 ms of `currentTime`: it cannot fall behind what the device has played, and when the audio runs slow
(an underrun) it resyncs to the sound instead of letting the picture run ahead (unit-tested).

#### Playback performance report

Machine: 32 cores shared with other work (5 `reelbolt-ci-slot` VMs ran during the last pass), real GPU, headed Chromium, one stack, Playwright
`--workers=1`, runs serialized. "load" is the 1-minute load average at the start of the run (the 5-minute figure is given where it mattered).

| bottleneck | examined with | what changed | before -> after |
| --- | --- | --- | --- |
| decode | per-frame timings, Chrome trace | one forward `SequentialReader` per source in pooled workers, lanes opened ahead of cuts | 5 fps / 77-82 % dropped (software GL, first build) -> 0 % (30 fps, load 8-12) |
| texture upload | `texImage2D` timer | held frames and the static title overlay not re-uploaded; `texSubImage2D` rejected (50-200 ms per VideoFrame) | upload 0.1-1.8 ms per frame; unchanged limit |
| compositing | `presentFrame` draw timer | `preserveDrawingBuffer: false` and `desynchronized` tried and rejected (no gain / black canvas) | draw p99 about 2.7 ms, listener notify 0.2 ms |
| rAF / rVFC pacing | spec rAF log, Chrome trace | HEADED window, `--disable-backgrounding-occluded-windows` (occluded window had read 12-45 % dropped); governor warm-up grace (1.5 s) and sustained-drop rule | rAF p50 6.9-7.0 ms (144 Hz display), p99 7.1 ms in quiet runs |
| audio clock | clock drift read at +3 s and +11 s | smoothed `AudioGraph.clockNow` (wall clock + largest recent offset, held within 40 ms of `currentTime`); buffers preloaded at load | steady 60 fps drops 4-40 % -> about 1-2 %; clock within 300 ms of wall in every run |
| React re-renders | commit counts | shell reads a playhead-frozen UI state while playing | 2-3 commits per frame -> none from the shell |
| GC | trace (`v8.gc`) | none needed | no GC pause correlated with a stall in the traces taken |
| title raster | main-thread tasks | first raster done before the clock starts | 8-60 ms mid-play block removed |
| the spec's own probe | traced 1080p60 run, load 8.2, 3.41 % dropped: every 21-42 ms rAF gap cluster followed one of the spec's once-a-second `toDataURL` samples (7-20 ms each); a thumbnail `drawImage` was worse (up to 500 ms), a compositor screenshot also perturbed | no sampling during the measured play: canvas evidence is three compositor screenshots (before Play, after the play, after a short second play); only the always-on counter and the clock are read at +3 s, +11 s and the end | 1080p60 at load 11-12: 3.4-4.5 % (probe on) -> 0.96-1.92 % (probe off, 5 runs) |

**Final numbers, probe-free spec, dropped % of deadlines (runs; load at start).** Quiet pass (1-min load 7-12, 5-min 10-15):

| case | runs | dropped % | rate |
| --- | --- | --- | --- |
| proxy-shaped 30 fps | 5 | 0, 0, 0, 0, 0.24 | 30 |
| derivative proxy 30 fps | 5 | 0, 0, 0, 4.3, 4.77 (one 507 ms and one 160 ms stall) | 25.7-30 |
| 1080p originals 30 fps | 11 | 0 in 9 runs, 4.3 (646 ms stall), 6.92 (653 ms stall) | 26-30 |
| 1080p 60 fps | 5 | 1.55, 1.19, 1.92, 0.96, 0.96 | 59.9-60.1 |
| 4K 30 fps | 5 | 0.24, 0.24, 1.67, 0.24, 0.24 | 29.4-30 |
| 4K 60 fps | 5 | 2.75; 19.2 (halved to 30 fps, unlabelled by the old spec read); 5.95; 8.43; 53 (halved) | 29-59.9 |

Final pass (1-min load 10.4-13.5, 5-min 15-17, five CI VMs running; the new 5-minute guard would skip these): 4K 60 fps 55.7 (rate 17.7, labelled), 4.25 and 6.87 (38.6-38.9 fps, labelled "Reduced frame rate"); 4K 30 fps 1.64, 8.59, 15.17 (722 and 590 ms stalls); 1080p 60 fps 8.61 (590 ms stall), 23.5 (972 ms stall), 2.75. PERF-02 passes (counter 789 deadlines at the reduced rate, 2 fps achieved with 12x CPU throttle).

**What limits the remaining drops.** In quiet runs 30 fps is held (0 % in 30 of 36 runs) and 1080p 60 fps sits at 1-2 % with all remaining drops in the first 100-300 ms after Play
(`cadence.lateAll`: 56-111 ms gaps at 60-200 ms) and one more cluster in 1.7-3.1 s. Every larger drop (4-24 %) is a single main-thread stall of 0.15-1.0 s
(`cadence.max`, `lateAll`, spec output `PERF-01 {...}`) with rAF otherwise at the display rate and the viewer's own per-frame cost small (draw p99 2.7 ms), and
the stalls appear when the box is busy (5-minute load above about 14, 5 CI VMs); a traced run at 1-min load 29 froze the renderer for 14 s. At 4K the decoder and upload cost is
large enough that 60 fps is only held in some runs; the governor then halves and labels it. So: 60 fps is NOT reliably achieved on this shared machine under this load; 30 fps
and 1080p 60 fps are held in quiet conditions. A single trace of a quiet-machine stall was not captured, so the cause of the 0.5-0.65 s stalls at load about 10 is unattributed
(candidates: the compositor/GPU process being descheduled, a worker-to-main handoff).
Early-play gaps: audio buffers are already preloaded when the editor opens (`AudioGraph.preload`), so Play does not wait for fetch/decode; the 1.7-2.0 s clusters were not tied to audio and no cheap fix was found.

**Further work that would help.** Audio prebuffer confirmation before the clock starts (await the preload promise in `play`), a WebCodecs worker warm-up decode of the first GOP
before Play, `requestVideoFrameCallback` presentation timestamps to count drops as the compositor saw them instead of inferring from the render tick, a Chrome trace of a stall on a quiet machine, and a proxy for 4K sources.

**Honest degradation.** `RateGovernor` (unit-tested) halves a 50+ fps timeline, and any timeline with a 3840 px source on screen, after three bad 90-slot windows past a 1.5 s warm-up; the viewer says "Reduced frame rate" (only while playing) and the counter counts the reduced rate. PERF-02
forces it with a CDP CPU throttle on the 4K 60 fps timeline and asserts the label and that the counter counts at most about 30 slots a second. PERF-01 4K cases pass when the rate is held or the label was on screen.

### Transport, selection, snapping, markers, edit index

- **Transport** (`lib/editing/transport/`): JKL shuttle with the 1/2/4/8 speed ladder (`K` stops,
  a second press in the same direction steps up), Space toggles play, arrows step a frame, Up/Down
  jump between edit points, Home/End — all of which also work **while playing** (a playhead the
  session did not report itself is a seek, and playback continues from there at the same speed; a
  reported frame counts as the session's own only until the playhead has echoed it, so seeking back
  to a frame just played is a seek too).
  **Loop** wraps between In and Out (the whole timeline when nothing is marked) and keeps playing;
  **Play in to out** (Shift+Space, or the transport button) starts at In and stops on the last frame
  before Out, also when pressed during playback; plain Space ignores the marks
  (`play-range.ts`). Marks set in the wrong order are read as the range they span. Timecode entry
  is **right-aligned field fill** (`1215` → `00:00:12:15`; an overfull field carries, so `95` at
  30 fps is 3 s 05 f), accepts `+15` / `-2s` / `+1:00` relative moves (that last one is one second, the
  colon separating seconds from frames), and drop-frame input is parsed when the rate is 29.97 or
  59.94 — the display itself stays non-drop-frame (see [The time model](#the-time-model)).
- **Selection** (`lib/editing/selection/`): click, shift-click range on a track, marquee across
  tracks, select-forward/backward from the playhead, select all on a track, select all. Every
  function returns a normalized id list, so equality is cheap and the store never holds a stale id:
  every document change (an edit, undo, redo, a server reload) also drops selected ids the timeline
  no longer holds. "Make compound" moves the selected clips into the compound's sequence; a
  selection still naming them made "Split at playhead" target nothing, so the edit after it was
  silently never made or saved (T-073, GUARD-COMPOUND).
  Forward/backward are **inclusive**: an item that straddles the playhead is selected either way.
  The timeline header's **Select** menu (`TimelineMenus.tsx`) offers forward, backward and all on
  the targeted track, and the keys `A` / `Shift+A` / `Alt+A` run the same `runSelectCommand` (W0.6,
  P3-SEL-03).
- **Add at the playhead** (`lib/editing/structure/add-at-playhead.ts`): the header's **Add** menu
  places a three-second **Title** or **Colour card** at the playhead on the topmost unlocked video
  track whose span is free (`placeOnTop`), or adds a new video track above the others holding it
  (`addTrack`). One undo step; the new item is selected; disabled on a read-only timeline (W0.6,
  P5-GEN-03).
- **In/out marks persist**: `EditorStore.setInOutMarks` writes a complete pair (in ≥ 0, out > in)
  as one `setInOut` op and clearing both writes `null`; a half-set pair stays view state. The view
  marks follow the document's `inOut` after every op, undo, redo and reload, and Deliver's range
  starts switched on when both marks are set (W0.6, P4-OPS-12).
- **Snapping**: candidates are every item edge, the playhead, in/out marks, markers and **frame
  0** (always), excluding the items being dragged; the threshold is 8 px converted to frames at the
  current zoom, so snapping gets finer as you zoom in. `N` toggles it.
- **Markers**: user markers are ops (`addMarker`/`setMarker`/`removeMarker`, so they undo and
  sync); **rationale markers** are derived read-only rows from each item's
  `provenance.rationale`, labelled with the agent and provider that made the decision, and are
  never written back — they are the agent's reasoning shown at the frame it applies to.
- **Edit index**: an EDL-style table (track, in, out, source in, name, parity) that seeks and
  selects; sortable and filterable, and derived from the document on every render.

### The canvas timeline

**Touch gestures (Wave 3: RM.3, R03.8, R04.6).** The pure classifier lives in `web/lib/editing/timeline-gestures.ts`
(pan, ruler scrub, tap, 500 ms long press, pinch, 20 px edge dead zone, double tap, tile-scrub intent). On the canvas a
one-finger drag pans, a tap selects, a pinch zooms time around its midpoint (clamped to the zoom limits, selection
unchanged; two fingers moving together pan) and a double tap on a clip zooms to it. A long press on a clip opens the clip
action menu; it picks the clip up to move only when the per-device flag `reelbolt:editor:touch-drag` is on
(`useTouchDragEnabled()` in `lib/editing/touch-drag.ts`, default off). Touches starting in the outer 20 px are ignored (iOS
back swipe). Trim handles of the selected clip get a 44 px hit area on coarse pointers. A `pointercancel` or a lost capture
reverts any half-applied drag, scrub, pan, trim or marquee, so no transaction is left open. Move and trim stay reachable
through the clip action menu and the frame steppers. On media tiles, press and drag sideways (after a horizontal intent
threshold) scrubs the filmstrip with a position label; vertical drags still scroll the page (`touch-action: pan-y`).
Specs: `e2e/tests/editor-v2/30-phone/touch.spec.ts`.

`web/components/editor/timeline/paint.ts` paints into a 2D canvas: ruler, marker lane, track rows,
items coloured by kind, selection, in/out shading (a tint over the ruler and every lane, painted
after them with an edge line at each mark), snap guides (a line at the frame a scrubbed, moved or
trimmed edge snapped to) and the playhead. It visits only the
items in the visible frame range (binary search for the first visible index per track) so a
thousand-item timeline paints the dozens on screen. Hit testing is the inverse of the same geometry
(`hitTestItem`, `trackAtY`). Drags are classified at pointer-down — scrub on the ruler, marquee on
empty track, move on an item, trim on one of the two DOM handles shown for a **single** selected
item, pan with the hand tool — and the op is dispatched **once at pointer-up** with the snapped
value, so a drag is one history entry and one pending op, not a stream of them.

**Touch gestures (Wave 3: RM.3, R03.8, R04.6).** A finger (`pointerType: 'touch'`) goes through its own path in
`TimelineCanvas`; mouse and pen keep the desktop path. The rules are pure functions in `lib/editing/timeline-gestures.ts`
(classifier, pinch maths, edge zone, double tap, tile-scrub intent) with unit tests. One finger: a tap selects (on lift, not on
press), a drag pans (never moves or marquees a clip), a drag that starts on the ruler scrubs. A tap on empty space clears the
selection. Two fingers: pinch zooms time around the midpoint (`onZoom`+`onScroll`, clamped to 0.01..64 px per frame like the toolbar
and wheel), the midpoint moving pans, the selection never changes; a second finger puts a started pan or scrub back first. A double
tap on a clip zooms to fit it (the synthesized `dblclick` is ignored for touch). A still finger held 500 ms on a clip (own timer, the
browser `contextmenu` is ignored for touch) opens the clip action menu, or, when the per-device flag
`reelbolt:editor:touch-drag` is `on` (`useTouchDragEnabled()` in `lib/editing/touch-drag.ts`, default off, W3-D3), picks the clip up
to move: that drag is the ordinary move drag, committed once on lift. A touch that starts in the outer 20 px of the screen is ignored
(iOS back swipe). Pointers are tracked in a map; pointer capture is released on up, cancel and `lostpointercapture` (a lost capture
without an up is treated as a cancel). The wrapper and canvas also carry `overscroll-behavior: contain`. A cancel at any point
reverts a pan (scroll), pinch (zoom and scroll) or scrub (playhead) and drops a move or trim: the canvas never holds a transaction, ops
are dispatched once on pointerup. In the media pool a touch press-and-drag on a tile (`touch-action: pan-y`, scrub only after a
horizontal intent of 10 px that is larger than the vertical travel) adopts the clip as the strip subject and scrubs it, with a
position label on the tile (`media-tile-scrub-label`); the lift after a scrub does not select the tile. The strip's touch wording has no
"hover". Specs: `e2e/tests/editor-v2/30-phone/touch.spec.ts`.

**Touch (remediation W0.7, the start of RM.3).** The canvas, its wrapper and the trim handles carry
`touch-action: none` (`TOUCH_SURFACE_STYLE`), so a phone browser does not claim the drag for scrolling
and cancel it after one move. The pointer is captured at drag start (a pointer that is already gone is
ignored). `pointercancel` ends the drag with nothing half-applied: move, trim and marquee only commit on
pointer-up, so they are dropped; a ruler scrub puts the playhead back and a pan puts the scroll back.
With a coarse pointer the selected item's handles are a 44 px hit area centred on the edge around a
10 px painted bar (10 px / 6 px with a mouse). On a phone an empty-area drag pans instead of drawing a
marquee (multi-select is cut there). Every gesture also has a button: the toolbar's **Clip** menu
(`timeline/ClipActionMenu.tsx`) offers Nudge one frame earlier/later, Split at playhead (disabled, with
a reason, when the playhead is not over a selected clip), Lift (leave gap), **Close gap before this clip**
(`deleteGap`) and **Close all gaps on this track** (`closeGaps`), and Delete (ripple: one
`extract` per selected item and per paired clip on another unlocked track (same asset, `sourceIn` and span: `lib/editing/ripple-delete.ts`), right-most first, one history entry), all 44 px tall, Delete below a
divider. Closing a gap (one, or all on the track) also closes the same gap on every unlocked track that holds a linked partner of a shifted clip (`buildDeleteGapOps` / `buildCloseAllGapsOps` in `lib/editing/timeline-edit/gap-objects.ts`), in the same undo step, so a picture and its camera sound stay in step. The toolbar's **Split at playhead** button and `Ctrl+B` say "Nothing to split here" instead of
doing nothing when no clip is under the playhead. A **Trim:** menu (ripple, roll, slip, slide) chooses what
a trim-handle drag does (view state; ripple is the default). Undo and Redo buttons sit in the toolbar
(a refused step reports why). The phone layout (the `phone` device class) has no timeline toolbar (T-008 retired it, and the fixed-height strip wrapper): its header has Undo and Redo, the action bar has Select, Split, Add text and Delete, the strip has a **Fit** chip (`strip-fit`, the whole programme across the strip) and pinch, the Audio sheet has track mute and solo, and Remove silence is in the Captions sheet. Desktop and tablet keep the toolbar. The transport keeps all its buttons on a phone.

**Phone frame (Wave 3 T-001: RM.2, RM.6, R04.4, VF8, VF9, RM.7).** `components/editor/phone/` holds the phone
surface. `PhoneShell` places the slots (header, viewer with transport, strip, banner, action bar, dock, sheets);
`PhoneEditor` wires them to the nodes the desktop uses. `PhoneHeader` (44 px; 36 px in a landscape phone) has Back, the
name and the save state (`save-state-chip`), an Offline chip, Retry / Sign in, Undo, Redo,
Export and an overflow menu (Assistant, Review, History, "Drag clips with your finger (beta)"). A phone never gets the
tablet layout ("Open full view" is gone; a stored `reelbolt:editor:full-view` is cleared). The "Early preview" label is
on the status line under the action bar.
`PhoneDock` opens a sheet per tool - Media, Edit (inspector), Audio, Look (colour), Captions (panel tabs) - each hosting the
existing panel; the header's Export opens Deliver in a full-height sheet. Cut and Compose workspaces are desktop and tablet.
`BottomSheet` has peek (120 px), half (45dvh) and full (90dvh) detents, a grabber (swipe down lowers, tap raises), Escape,
Android back and the iOS swipe-back close the top sheet through history entries (`sheet-history.ts`), the soft keyboard
lifts it from `visualViewport` (`use-keyboard-inset.ts`), safe-area insets, a focus trap and 44 px controls; while a sheet
is open the viewer shrinks to mini so the strip (never under 160 px) stays visible. `UndoSnackbar` (`useUndoSnackbar`,
`showUndoSnackbar({ message, onUndo })`) shows a 6 s Undo and replaces confirm dialogs for destructive phone actions.
The app bar and its banners are hidden on a phone editor route (`AppShell`). A landscape phone (coarse, under 500 tall) puts
the viewer left (~38%), the strip right, sheets as right drawers and the dock as a left rail. Escape hatch: a phone opening
the full editor gets the phone surface and a dismissible note ("Phone view. Some tools need a larger screen.") with Copy link
and Open full view, which loads the tablet layout and is remembered on the device (`reelbolt:editor:full-view`, guarded
localStorage). Sync UX (RM.7): the conflict is a compact bar above the dock (`SyncNotices`), Keep my copy works there; the
journal still flushes on `visibilitychange` and `pagehide`. Specs: `e2e/tests/editor-v2/30-phone/frame.spec.ts`.

**Phone Add media sheet (Wave 3 T-005: RM.8, AC 6).** On a phone the dock's Add media item opens `phone/AddMediaSheet.tsx` (titled "Add media",
passed to `PhoneEditor` as `renderMedia({ collapse })`) instead of the desktop pool. It reads the same SWR pool, bins and annotations
(`useMediaPool`, `useMediaBins`, `useMediaAnnotations`) and the same upload queue as `MediaPool`. Top to bottom: "Choose photos, videos or sound"
(`<input type=file multiple accept="video/*,audio/*,image/*">`) and "Take a photo or video" (`capture`), one upload row per file in plain words
(`media-readiness.ts#uploadText`: Waiting to upload, Uploading n%, Added, Did not upload + Try again), bin chips (All media, each bin, New bin with a
name field), and a tile grid. Each `MediaTile` has a readiness chip from `tileReadiness` (probe + derivative rows + the pipeline's progress stage:
Ready, Processing preview, "Preview not ready - you can still add it"; reads are GETs only, nothing asks for derivatives) and a 44 px Actions menu:
Insert at playhead, Add to end (both through `placePoolItem`, the desktop's Place logic, one undo step; a refusal comes back as plain text in the
sheet, success shrinks the sheet to a peek and shows the 6 s Undo snackbar), Add to bin (every bin is listed, a smart bin answers with the existing
plain refusal), Remove from bin, Keywords and notes (the annotation editor), Preview (the source viewer inside the sheet, with Insert at playhead / Add to
end for the marked range). No HTML5 drag, no bin reparenting or smart-bin editing (the sheet says that needs a larger screen). The touch filmstrip scrub
(R04.6) and its position label work on the tile pictures. The desktop pool's search, sort and filters are not on the phone. Specs:
`e2e/tests/editor-v2/30-phone/media.spec.ts`; older phone runs of the pool specs were pointed at the sheet or limited to desktop.

**Phone clip strip (Wave 3 T-003: RM.4, AC 3, AC 12).** `phone/ClipStrip.tsx` replaces the lanes in the phone's strip slot
(the timeline toolbar stays above it until the action bar carries its actions). It is not a second editor: it reads the same
document and selection, paints with the desktop's own `paintTimeline` (posters, waveforms, labels, `--rb-clip-*` colours)
over a presentation-only copy of the document whose lane heights are set by `strip-geometry.ts`, and issues the same ops
(`onSelect`, `onSeek`, `onTrimItem` go through the same editor handlers as `TimelineCanvas`). The playhead is locked to the
centre: the strip scrolls under it, dragging one finger pans (which seeks), two fingers pinch to zoom time or pan together
without touching the selection, a double tap on a clip zooms to it, and while playing the strip follows the playhead. A tap
selects a clip and never seeks; a tap on empty space deselects; a long press opens the clip menu (or, with "Drag clips by
touch" on, picks the clip up). The primary video track and its linked audio are full lanes (at least 56 px); every other
track is a thin summary lane (a tap there selects its clip, the action bar edits it). The selected clip alone shows painted
trim handles (11 px bar, 44 px finger area; ids `trim-handle-in` / `trim-handle-out`). The bar above the lanes has the
playhead timecode, a text alternative ("Clip 2 of 5, Beach.mp4, 0:04 to 0:09", a polite status) and Previous / Next clip
buttons, so a screen reader or switch user needs no drag. The region is labelled "Clip strip". Nothing animates, so reduced
motion needs no special case. `paintTimeline` skips ruler ticks before frame 0 (the centred view can start left of it).
The canvas keeps `data-testid="timeline-canvas"` and adds `data-centre-lock`, `data-playhead-frame` and `data-ppf`;
`EditorPage.frameX` reads them. Specs: `e2e/tests/editor-v2/30-phone/strip.spec.ts`; `touch.spec.ts` drives the same strip.

**Phone action bar, Select mode, trim sheet (Wave 3 T-004: RM.5, R12.6).** `PhoneActionBar` (52 px, `phone-action-bar`, buttons 44 px)
sits above the dock and is filled by `PhoneEditor`'s `actions` prop, built in `EditorWorkspace` by `usePhoneActions`
(`components/editor/phone/`). Nothing selected: Add media (opens the Media sheet), Add text (a title at the playhead, selected),
Marker, Select. A clip selected: Split, Duplicate, Trim, Nudge, More, then Delete after a spacer (at least 8 px from the neutral
buttons). Each calls the desktop handler or builder (`useTimelineEditing`, `buildDeleteOps`/`linkedOpsForDelete`, `linkedOpsForNudge`,
`linkedOpsForRetime`...), so linked picture and sound (R08.1) behave as on the desktop and a refusal is the same plain sentence.
Delete (ripple, D7) and Lift show the 6 s `UndoSnackbar` and never a confirm dialog. The bar scrolls sideways only as a last resort
(narrow screens); Speed, Volume, Move earlier/later, Extract audio, Freeze frame, Link/Unlink, Copy/Cut/Paste, marker jumps, Clear in
and out, Snapping and Lift live in the More sheet. `lib/editing/timeline-edit/phone-actions.ts` holds the pure builders
(`buildMoveInOrderOps` - the clip swaps places with its neighbour on the same track through `move` ops, `buildTrimStepOps`,
`buildSpeedOps`, `buildVolumeOps`, `foldHistorySince`); no new op. **Select mode** (`selectModeTap`) makes a tap on a clip toggle it in the
selection (what shift-click does), a tap on empty lane space keeps the group, Clear empties it, Done leaves; Duplicate, Nudge, Lift
and Delete act on the whole group with link-aware expansion. The "needs a larger screen" state remains for the marquee (feature
`marquee`) and compound clips. **Trim sheet** (`TrimSheetBody`): the existing `ClipTimingFields` (In/Out/Duration, frames or timecode, `touch`
size), -10/-1/+1/+10 steppers for Start and End separately, trim mode chips (labelled Shift everything after (default), Move the cut, Change the part shown, Move the clip; ripple, roll, slip, slide in the code, the same `ui.trimMode`);
every press is applied live as its own `dispatchBatch`, and when the sheet closes `foldHistorySince` undoes those presses and replays
their ops as ONE batch, so a session is one undo step. **R12.6 touch parity:** `lib/editing/keymap-touch.ts` `TOUCH_PARITY` gives every
`KeyAction` either a touch control (surface and plain label) or a keyboard-only reason; `__tests__/keymap-touch.test.ts` fails when a
keymap action is in neither. Keyboard-only: shuttle reverse/forward (J, L), Stop (K), play in to out, back/forward ten frames, move clip
to the track above/below. Specs: `e2e/tests/editor-v2/30-phone/actions.spec.ts`.

**Phone Look and fix sheet and clip sheet (Wave 3 T-007: RM.10, R10.6, R07.7, AC 7).** The dock's Look and fix item opens `phone/LookSheet.tsx`
(`LookSheetBody`, titled "Look and fix"; `PhoneEditor` no longer switches to the colour workspace for it). It acts on the selected
picture clip, else the one under the playhead (`lookTargetOf`). Look words are the desktop's `GALLERY_LOOKS` (chips, no thumbnails), strength and
the five `FIX_KEYS` sliders go through `look-gallery` (`withLook`, `withFix`), each commit being one `setProperty grade` (`look-actions.ts`
`buildLookOps`). **Apply to all clips** (`buildApplyToAllOps`) is one batch of `setProperty grade` over every picture-track media clip,
each keeping its own other nodes. **Compare** sets `ui.gradeBypass` (toggle, or hold) and clears it when the sheet unmounts. **Titles**
uses `buildGraphicOps` (R10.5) for Add title (text + `TITLE_TEMPLATE_CHOICES`) and `setProperty text|template` for the selected title.
Nodes other than `Look:` and `Fix` (wheels, curves, keyer, match) are untouched and counted in a read-only note. Cut on phone: wheels,
curves, keyer, pin, qualifier, mask and reframe, Stabilise (the desktop has it only in the node graph, not in Look or the inspector).
**Clip sheet** (`phone/ClipSheet.tsx`, `clip-sheet-actions.ts`): ONE sheet replaces the T-004 quick Speed and Volume sheets. It opens
from More (Fit, position and fades / Speed / Volume, which scroll to their section), and from the strip's **Clip** chip
(`strip-clip-chip`, via `clip-sheet-bus.ts`). Fit/Fill/pad use the viewer's `fitValues`/`fillValues`/`padValues` + `transformOps` (source size
from `sourceSizeFor`), X/Y/size/opacity/fades use the inspector's writers (`checkOpacityPercent`, `checkFadeSeconds`), speed and volume the
T-004 builders. `SliderField` (`phone-controls.tsx`) is a 44 px row with a 28 px thumb and a number box; a drag commits once on release.
A keyframed channel renders read-only ("Animated on desktop") and its builders refuse; rotation, crop, reverse are told in words, never
offered; transitions and generators are not offered, and More has no transition item. Specs: `e2e/tests/editor-v2/30-phone/look.spec.ts`;
unit: `phone/__tests__/look-and-clip-sheets.test.tsx`.

**Phone Audio sheet (Wave 3 T-008: RM.11, AC 7).** The dock's Audio item opens `phone/AudioSheet.tsx` (passed to `PhoneEditor` as `renderAudio`).
Quick fixes: **Duck music under the voice** is one tap and ONE undo step (`audio-sheet-logic.ts` picks the music and voice tracks with the desktop's
`guessDuckTracks`, writes the music fader curve with `compileDuck`, and keeps the ducking setting the desktop reads; a plain sentence says what is missing
when there is no music track or no voice clip; "Update ducking" and "Stop ducking" follow); **Clean voice** sets noise removal and the speech leveler on the
voice tracks (else the selected clip's track) in ONE undo step and a second tap turns it off. Then the selected clip's volume (the action bar's Volume sheet body),
every audio track with a 44 px Mute and Solo and a dB slider ("Back to 0 dB"), and the voiceover recorder (the desktop `VoRecorderPanel` with `phone`): without
`MediaRecorder` it says "Recording is not available in this browser. Upload a voice file instead." and offers a file picker; the file (or a take) is uploaded as a
project file and placed at the playhead on a new track. Not on a phone: buses, EQ and dynamics numbers, automation lanes, match loudness (it needs a measuring
render). Specs: `e2e/tests/editor-v2/30-phone/audio.spec.ts`.

**Phone Captions sheet (Wave 3 T-008: RM.12, R09.5, AC 5, AC 7).** The dock's Captions item opens `phone/CaptionsSheet.tsx` (`renderCaptions`). The caption
list (the Subtitles panel's data) has Edit (a full-width field in a nested sheet, saved on Done), Split at the playhead, Merge with next and Delete (a lift, so
the other captions keep their timing, with the 6 s Undo); plus Add at the playhead and Generate from speech (the transcript, the panel's defaults).
**Remove silence** opens the desktop `RemoveSilenceDialog` (full screen on a phone; the sheet closes first). **Delete words** (`WordPicker.tsx`) lists the
transcript words of the chosen track: press and hold a word for 500 ms (own timer, 10 px slop) or tap **Select words** and tap the first and last word; two
44 px handles drag the ends (and Start/End arrow buttons do the same), and Delete uses `compileRippleCut` (ripple, linked sound included, one undo step; words timed
only roughly ask first). Paragraph reordering stays "needs a larger screen". **More tools** under the list keeps the desktop panels (analysis, transcript, markers,
edits, caption looks, SRT import) one tap away. With no words yet, both **Generate from speech** and **Delete words** show `TranscriptStart` (the desktop Transcript panel's component, 44 px buttons via `tapStyle`) instead of sending the customer to a larger screen: "Find the words in this video" shows the credit estimate (free when billing is off), nothing starts before the confirmation, progress and failures are plain words with a retry, and the sheet fills in by itself when the words are ready. Each captions track has a 44 px **Captions on the picture** toggle (`setTrackProperty enabled`, one undo step) that hides or shows the lane without deleting captions. No new keyboard action, so `TOUCH_PARITY` is unchanged. Specs: `e2e/tests/editor-v2/30-phone/captions.spec.ts`, `transcript-lane.spec.ts`; `EditorPage.openRemoveSilence()` and `openPanelTab` know the phone paths.

### The Events lane

A read-only lane under the ruler and the marker lane (desktop and tablet) that shows the free `events` analysis of the files the timeline's video clips play. It is derived data: nothing is in the document, no op, no schema field, and the op catalogue stays at **34**.

- **Data** (`web/lib/editing/events/`). `SourceEvent` carries the optional `endFrame`, `direction`, `speed` and `kind` when the stored result has them (read as short lower-case words only, `kind` from the closed list; an older result simply lacks them), and the type list includes `flash` and `caption_tick`. `use-events-lane.ts` reads each file's `events` status (`file-events`, polled every 2 s while queued or running), fetches the data of ready files (`readEventsResult` drops bad entries) and maps it with `laneEvents`: `sourceSecToRecordFrame` (the beat grid's mapping, through the clip's `sourceIn`, `playbackRate` and `reverse`) puts each event on the record frame of every enabled, non-hold video-track clip of that file, keeping only frames inside the clip. At most 8 files. Nothing is requested while the lane is hidden; a file with no events is started only by the **Find events in this video** menu item (`POST …/analysis` with `kinds:['events']`), after `…/estimate` shows the price (free).
- **Paint** (`timeline/eventsLane.ts`, called from `paintTimeline`). `PaintUi.events` adds `EVENTS_LANE_HEIGHT` (26 px) to `headerHeight(ui)`, which replaces `RULER_HEIGHT + MARKER_LANE_HEIGHT` everywhere a y is mapped to a track (`trackRows`, `hitTestItem`, the canvas gestures, the phone strip keeps the old constant because it never has a lane). A tick is a stem (height = strength) with a head whose shape and family colour come from `EVENT_STYLE` (colour is never the only cue; `PaintTheme.eventColors` hold 3:1 on both lane backgrounds); ticks closer than 3 px merge to the strongest; labels (12 px) show from 2 px per frame when they fit. Visits are a binary search over the sorted events, like clips.
- **Interaction.** Click or tap a tick: the playhead goes to its frame (`onActivate`); right click, tap on touch, **Enter**, **Shift+F10** or the context-menu key opens the act menu (`onMenu`). Hover sets the canvas tooltip (type and time, and the kind of picture in brackets when the analysis gave one: graphic, text or filmed footage); `EventsLaneKeys.tsx` is a focusable `listbox` over the lane (Left/Right step, Enter opens the menu, the focused tick's sentence is the option text and shows as a tooltip), so global shortcuts leave it alone.
- **View setting** (`lane-pref.ts`). `{lane, snap}` per timeline in `localStorage` (`reelbolt:editor:events-lane:<timelineId>`), device-local like the viewing LUT, never in the document or preferences API. The lane starts hidden; snapping starts on. Keymap actions `toggleEventsLane` (**E**), `toggleEventSnap` (**Shift+N**) and `actOnEvent` (**Shift+E**) have `TOUCH_PARITY` entries (the phone has no lane; its More sheet has **Events at the playhead**, `PhoneEventsPanel`, tap-only, previous/next event, large buttons).
- **Snapping.** The event frames are `extraFrames` of `snapCandidates` (with the beat grid's), so the scrub, clip drags and trims snap to them within the usual 8 px; the lane header's magnet and each tick's snap foot show it is on.
- **Act on this event** (`event-actions.ts`, `useEventsLaneController.tsx`). One plan per action, dispatched as one batch (one undo step, label `<action> (<timecode>)`), times only from the event's record frame: **Zoom here** (`setKeyframe` `scale`, and `positionX/Y` centred on the event region when there is one, 1.25x for 0.8 s with ease in and out), **Pan here** (`scale` ≥ 1.2 and `positionX/Y` keys over 1 s), **Shake** (8 decaying `positionX/Y` keys from a generator seeded by the event key, headroom `scale` key), **Flash** (a 3-frame white colour `generatorClip` via `placeOnTop`/`addTrack`, the Add title rule), **Speed ramp** (`linkedOpsForBlade` ×2 and `linkedOpsForRetime` to half speed for the next 0.5 s, linked sound included), **Cut here** (`linkedOpsForBlade`), **Add title here** (`addAtPlayhead` at the event frame) and **Add sound here** (no op: the playhead moves to the frame and the Media page opens). An action no existing op can express is absent from the menu, not disabled: **glow** (it needs a region effect with a time window, which the document does not carry). Actions that cannot apply (locked track, missing clip, clip too short) are also absent.
- **Tests.** `lib/editing/events/__tests__` (data mapping, golden programs, availability), `timeline/__tests__/eventsLane.test.ts` (layout, hit test, 3:1 colours, header height, snapping), `paint-font-floor.test.ts` (12 px floor includes `eventsLane.ts`), `commands.test.ts`/`keymap-touch.test.ts` (77 actions, parity), and `e2e/tests/editor-v2/20-analysis/events-lane.spec.ts` (desktop and phone; written against the harness, not run here).

### Routes

| Route | Purpose |
|---|---|
| `/app/editor?project={projectId}&view=timelines\|media\|pictures` | The editor hub (see below). `/app/editor/{projectId}/timelines` and `/app/editor/{projectId}/media` redirect here. |
| `/app/editor/{projectId}/timeline/{timelineId}` | The editable workspace. `404`/`403` from the API render as "not found"/"not yours" states, never as a crash. |
| `/app/projects/{projectId}/editor/{stepResultId}` | "Open in editor" on a workflow render: finds the timeline already seeded from that render or seeds one, then replaces itself with it. |
| `/app/editor/{projectId}/review/{timelineId}` | The phone review page (P9). |

### The editor hub and the one video editor

There is one video editor, the v2 timeline. The v1 read-only "Studio" viewer (`TimelineStudio` and
its components) and the offline manifest viewer (`/app/editor/preview`, which now redirects to the
hub) were removed when the v2 editor superseded them; a compiled render's manifest is only ever a
timeline's seed now.

`/app/editor` (`components/editor/hub/EditorHub.tsx`) is one project at a time, the project and the
tab in the query so every view is linkable (the last project is remembered per browser as a
convenience only). The hub has exactly two tabs. **Videos** (`?view=videos`, `TimelinesView`) lists the
edit timelines and, under "From your workflow runs", every compile result no timeline was seeded from
yet; **Edit** on one, like the render route above, goes through `openTimelineForRender`
(`lib/editing/open-timeline.ts`): the most recently updated timeline whose `seedStepResultId` is that
render, else a new seeded one named after the run. Re-opening a render therefore returns to the user's
own edit and never forks a second copy. **Pictures** (`?view=pictures`) is the picture editor's list.
The old `?view=timelines` and `?view=media` links are rewritten to `?view=videos` with `router.replace`.

**Timeline lifecycle in the hub (Wave 1, R01.1/R04.3).** `TimelinesView` filters (`filterTimelines`: name contains, case-insensitive) and sorts
(`sortTimelines`: Recently edited, Name, Created) in `components/editor/hub/timeline-list.ts`; the sort is remembered in `localStorage`
(`reelbolt:editor:hub:timeline-sort`). Delete is the server's soft delete: the dialog says 30 days in Recently deleted, review page and comments
stop while deleted, they return on restore; a toast ("Deleted <name>" + **Undo**, 9 s) calls `POST .../{id}/restore`. **Recently deleted** reads
`GET .../edit-timelines?state=deleted` (`listDeletedTimelines`, rows carry `deletedAt` and `purgesAt`) and shows `Deleted N days ago - removed for
good in M days` with a **Restore** button; there is no permanent delete in the UI. Row menu buttons and items are 44 px on touch
(`hub.module.css`). **New timeline** starts with the name "Untitled video" selected, and a blank one sends `settings` from the **Format**
(`TIMELINE_FORMATS`), **Frame rate** (24, 25, 30, 50, 60 or **Match my first clip** = `matchFirstClipRate` over the pool's probe fps, else 30 with a
hint) and **Advanced** (pixel shape, start timecode) pickers. The in-editor Timeline settings panel offers the same Format and Frame rate but the
frame rate is disabled, with the visible reason "The frame rate can't be changed once there are clips on the timeline. Start a new timeline at
the frame rate you want.", once any track (or compound sequence) holds an item (`timelineHasClips`); nothing re-times a document silently, as the
server's source-length check would refuse the overrun. An empty timeline still changes the rate.

**Timecode entry (R01.5).** `parseTimecodeInput(text, rate)` in `lib/editing/time.ts` reads a digit stream right-aligned (`1215` = `00:00:12:15`) or
colon form (`12:15`), refuses minutes/seconds over 59, frames at or above the frame base, drop-frame `;` and junk with a plain reason, and never throws.
`TimecodeInput` (`components/editor/shell`) shows the typed digits as a timecode, commits on Enter or blur, cancels on Esc, reverts an invalid entry with
the reason under the field, and sets `inputMode="numeric"`.

**Media workspace upload and bins (Wave 1).** `MediaPool` shows an **Upload** button (header and empty state) and a drop zone; files go through
`useUploadQueue` (`uploadFileWithProgress`) with one progress row per file, a plain error and **Try again**, and the pool is re-read as each file
finishes. Deleting a bin opens a confirmation naming the bin and its sub-bin and clip counts ("Clips stay in your project; only the grouping goes."); there
is no undo for it because re-creating a bin cannot restore its pins.
The media pool is not a hub tab: it lives inside the timeline editor (`MediaView`, unchanged), where
`clipActions` adds **Insert at playhead** (`compileSmartInsert`: the nearest edit point, rippling) and
**Add to end** (`append`). Every link into either goes through `lib/editing/open-timeline.ts`.

**Pool inserts (T-022).** `compilePoolPlacement` (`lib/editing/media/pool-insert.ts`, pure) builds
the whole insert as ONE op batch the store applies as one history entry. The clip's length is the
probe's `durationSec` converted to the timeline rate (12 s at 30 fps = 360 f); when the source
viewer holds In/Out marks (source frames at the probe's rational rate, reported by
`SourceViewer.onMarksChange`) the clip is exactly that range (`sourceIn` = In). Only a source with
`probe == null` falls back to 5 s, and the Media workspace then shows "Length unknown until the
file is prepared"; a still is 5 s by design. **Create subclip** places the marked range at the end
of the timeline, named `stem [in-out]`. A video whose probe reports an audio stream
(`hasAudioStream`: the probe's typed `hasAudio` when the server sets it, else the audio codec and
channel fields) also gets a **companion clip** (same source, `sourceIn` and span) on a paired
audio track named **Camera audio** (`Camera audio 2`, … when the first has no room), created with
`addTrack` above the first audio track when absent; a smart insert ripples the paired track with
the picture. The Inspector's **Extract audio** (`compileExtractAudio`) does the same for a clip
already on a video track. Only existing ops are used (`insert`/`append`/`addTrack`); there is no
link between the two clips yet. Every id minted is a v4 Guid (the shared `lib/editing/ids.ts`
`newGuid`, built on `getRandomValues` so it works without `crypto.randomUUID`), because
the server binds item and track ids as `Guid`.

**Opening a timeline asks for its proxies.** `useRequestTimelineDerivatives`
(`lib/hooks/use-media-derivatives.ts`) sends one `POST …/media/derivatives/request`
(`requestedBy: "timeline-open"`) per distinct source the document's clips use, once per open and
sequentially; a source added later is asked for once. Hover still requests nothing.

**The media pane refreshes itself.** `MediaView` subscribes to `media.derivative.progress` on the
global SSE feed (`/api/v1/workflows/events`, scoped by project; `useMediaDerivativeProgress`). Each
stage change for a source bumps that source's derivatives version, which re-reads its rows in the
source viewer and, for a source the pool already read, in the hover filmstrip and its chips; a
terminal stage re-reads the pool (and so the probe) after 800 ms. The open clip shows the stage in
plain words. The chips list only what the pool pipeline produces for the kind
(`derivative-kinds.ts`: video = proxy, filmstrip, keyframe index, plus peaks when it may have sound;
audio = peaks; image = nothing), plus any kind a row exists for. An audio source plays in an
`<audio>` element and a still is an `<img>`; neither ever opens a `<video>`.

### Layout of the workspace

The timeline route is immersive: `AppShell` folds the navbar away on every screen size and drops the
page padding for `/editor/{p}/timeline/{t}` only. The workspace (`EditorWorkspace`, chrome in
`components/editor/shell/workspace.module.css`) is one 48 px header (`WorkspaceHeader`: back to the
hub, name, `Version N`, review page, layouts, shortcuts, **Export** opening Deliver), a labelled page
band on desktop (or a 64 px page rail on a tablet) that is the workspace switcher, `PanelLayout`
(hairline separators with a 9 px grab band, arrow-key resizable), the timeline toolbar, and the status
bar. The page model, the address and the geometry are in
[Pages and panel geometry](#pages-and-panel-geometry). Side panels are `PanelTabs`: one scrolling row of
tabs, never wrapping.
A phone (see "Phone frame" below) gets its own surface instead of a stacked desktop. The timeline canvas paints with
`LIGHT_THEME` or `DARK_THEME` (`timeline/paint.ts`) after the app's computed colour scheme.

**Tablet notes (RM.15, decision D2: works, not certified).** The tablet keeps the desktop `PanelLayout`, hardened for touch:
`WorkspaceBar variant="rail"` is a 64 px column down the left edge (icon over a 12 px label, the same `workspace-*` test ids)
and the header (`WorkspaceHeader tablet`) drops the switcher for one **More** menu (Review page, Keyboard shortcuts, "Editor
always dark") plus a 44 px **Export**; the menu ends with the plain line "Tablet: works, not certified yet"
(`data-testid="tablet-note"`). `PanelLayout tablet` turns the left tools and the right inspector into drawers over the viewer under
900 px wide (`TABLET_DRAWER_BELOW`): two 44 px buttons (`drawer-left-toggle`, `drawer-right-toggle`) open them, a tap on the dimmed
viewer or Escape closes them, and a rotation to a wider window puts the panels back in place. On a coarse pointer the resize
handles keep a 44 px grab band. The timeline toolbar wraps onto a second row (`data-wrap`) instead of scrolling sideways, so no tool is
hidden. Inspector rows, track headers and the clip actions button take their 44/48 px sizes from the `data-ed-touch` density tokens.
Playwright runs it as the `tablet` project (1024x768, touch) in `30-phone/tablet.spec.ts`; not run against a stack yet.

**Phone notes (W0.7).** The device classes of decision D1 live in `lib/editing/device-class.ts`:
phone is narrower than 600 px, or shorter than 500 px with `pointer: coarse` (a landscape phone);
tablet is 600-1100 px or any other coarse pointer; desktop is wider than 1100 px with a fine pointer.
**Tablet layout (<= 1100 px).** On a touch tablet (coarse pointer, 768-1100 px) the default `PanelLayout` (`dockRightOnTablet`,
`tabletTwoPane`) docks the Inspector beside the viewer (`panel-right`, 220-300 px) so a selected clip's details are always visible; the
left panel stays inline and is exactly 240 px wide and the Inspector 220 px under 1000 px (the viewer keeps about 303 px at 820); from 1000 px the stored sizes apply (300 / 320 by default, Inspector capped at 300). A desktop window of the same width, and a
touch screen under 768 px, keep the drawer: `PanelLayout` turns the right inspector into a drawer behind an **Inspector** button
(Escape closes it), keeps the left panel at least 300 px wide, gives the timeline at least 45% of the height, and the
timeline toolbar wraps onto two rows (`data-wrap`) instead of clipping. Coarse pointers get 44 px icon buttons, pane-tab
"more" buttons and status bar (`__tests__/touch-targets.test.ts`; the phone surface is scanned by
`__tests__/phone-touch-targets.test.ts`). The early-access banner is not shown on the timeline editor route.
`useDeviceClass()` (`lib/editing/use-device-class.ts`, `deviceClassOf` over the live viewport, SSR-safe, reacting to resize,
rotation and pointer change) is the one source; there is no width literal in the editor. 600-767 px is a tablet. The phone rule decides which tools are cut: those render
the one `NeedsLargerScreen` state (a plain sentence, `data-testid="needs-larger-screen"`,
`data-feature` naming the tool) instead of a control, via `LargerScreenOnly` or directly — compound
clips (`compound`), buses, automation and audio processing (`buses-automation`), curves (`curves`),
"Track through clip" for a power window (`window-tracking`; the window itself stays editable),
bin reparenting (`bin-reparent`; bins are not draggable there), paragraph drag in the transcript
(`paragraph-drag`; the arrows remain) and multi-select (`multi-select`, in the Clip menu). In the
stacked layout a 409 shows `ReadOnlyBanner variant="bar"`: one line and a 44 px **Reload**, so the
timeline keeps its height; **More > Export** switches to Deliver and shows its pane. With a coarse
pointer the timeline toolbar is 48 px tall, and at any width it scrolls sideways inside itself
(`overflow-x: auto`, `min-width: 0`): a toolbar that overflowed made mobile Chrome widen the layout
viewport to the content, which laid fixed dialogs (Remove silence) out past the screen edge. The
transport's timecode box sets `inputmode="numeric"` (a phone opens its number pad) and reads bare
digits right-aligned, two per field (`1215` = 00:00:12:15; a field past its range carries, `45` =
1 s 15 f), so no colon is needed. Clips reach a bin without a drag through the tile menu's
**Add to bin** (`ClipGrid` → `MediaPool`, `planAddToBin` in `lib/editing/media/bins.ts`): the same
plan and refusals as a drop, including the smart-bin refusal (`smartBinRefusal`).

### Item JSON and the discriminator

Items are polymorphic on `type`, and System.Text.Json by default refuses a discriminator that is not
an object's first property. PostgreSQL `jsonb` stores keys ordered by length, so a stored document
always comes back as `{ "id": …, "name": …, "type": … }`, and the editor's object literals send
`{ id, type, … }`. Every reader of a document therefore uses `EditTimelineJson.Options`
(`AllowOutOfOrderMetadataProperties`), the Inference API's MVC options set the same flag for request
bodies, and the client still writes `type` first (`stringifyDiscriminatorFirst`) for any older API.
`EditTimelineJsonTests` pins it; without it, a timeline stopped loading the moment it held a clip.

### What P3 leaves to P4

(Everything in this list landed in P4 — see the next section.) No renderer (the Deliver workspace is
a placeholder and the render-cache chip says "none yet"),
no track or timeline-settings ops, no retime/keyframe/transition ops (the compositor already
interpolates keyframes it finds in a document, it just cannot author them), no remove-silence
dialog. The e2e spec `e2e/tests/editor-timeline-p3.spec.ts` runs against a **stubbed** API — it
proves the rendering half (the viewer mounts a session for a document, the dropped-frame counter is
on screen, a marker add round-trips through undo and the save goes out as one `PATCH`) and says in
its own header that the server half is `EditTimelineHistoryControllerTests`' and
`EditTimelinesControllerTests`' job.

### Track headers and clip content (Wave 2, R03.4 / R03.5)

**Track options menu.** Every track header has a **Track options** button (44 px on a coarse pointer;
the menu items are 44 px tall there too). All entries are text labels, no icon-only controls. It is
built in `web/components/editor/timeline/TrackMenu.tsx`, with the pure helpers (palette, height presets,
drop index, confirmation sentence, view-toggle parsing) in `timeline/trackLayout.ts`. It adds no op:

| Menu entry | Op |
|---|---|
| Rename track (also double-click the name; Enter stores, Escape cancels, empty is refused) | `setTrackProperty` `name` |
| Colour (eight accessible swatches, each at least 3:1 on the light and dark lane; "No colour") | `setTrackProperty` `color` (`null` clears) |
| Row height: Small 40, Medium 64, Large 96 | `setTrackProperty` `height` |
| Lock, hide in the picture, mute, solo | `setTrackProperty` `locked` / `enabled` / `mute` / `solo` |
| Move track up / down, and the drag handle beside each row (`ActionIcon` with pointer capture; the row under the pointer is the drop index) | `moveTrack` |
| Add video / audio track above or below | `addTrack` at an index |
| Remove track (asks first when the track has clips: "V1 has 2 clips. Removing the track removes them too. You can undo this."; an empty track is removed at once; the undo toast stays) | `removeTrack` (inverse restores the clips) |

A locked track greys rename, colour and height with "unlock the track first"; a clip edit on it is
refused with the plain sentence "The track ... is locked. Unlock it to change its clips." (`refusal.ts`).
The three **view-only toggles** (Show waveforms / thumbnails / clip names) are labelled menu items kept in
`localStorage` (`reelforge.editor.timelineView`); they change what the canvas paints, never the document.

**Clip content.** Painting stays on the one canvas (`timeline/paint.ts` calls `clipPaint.ts` per visible
clip; only items intersecting the viewport are visited, by binary search, so 5 tracks x 60 clips visit
only what is on screen - `__tests__/clipPaint.test.ts` asserts the visit count and a per-frame budget).
Layout of a clip body: an 18 px label band in the kind colour (the Wave 1 AA tokens; kind badge V / A / T /
C / Compound, name, duration; always 12 px, so a narrow clip drops the duration, then the name, never
shrinks the text), then the art area: a poster (first frame) or, when the clip is at least two cells wide,
a filmstrip from the existing Filmstrip derivative; the waveform for audio clips and for video clips with
sound (peaks derivative, drawn on a dark scrim over the lower part of the thumbnails); a hatched edge on
each side that still has unused source behind it (the in/out handles of a trimmed clip); "Offline" hatching
when the source's derivative calls 404; a yellow stripe along the foot of a linked clip (`isLinked`);
the selected ring and the render-fidelity bar as before. The snap guide line is drawn while an edge is
dragged (d7 N2).

The art is loaded by `timeline/clipArtStore.ts`: once per source, `GET media/derivatives` (peaks rows) and
`GET media/filmstrip`, the sheet images as `Image`, the peaks as raw bytes. Peaks bytes are channel-major
`[channel][bucket]{int16 min, int16 max}`, two channels; which of the three LODs (20, 100, 1000 ms) a row is
comes from its size against the source duration (`peaksBucketMs`), and the store prefers the one nearest
100 ms. Nothing that has not arrived is drawn: a clip without art still shows its colour and label.

---

## Pages and panel geometry

The editor is one frame with seven addressable pages. A **page** is a place, not a mode: **Media**,
**Cut**, **Edit**, **Compose**, **Colour**, **Audio**, **Deliver**. A page declares its left content, its
right content, whether it shows the viewer and its timeline height; the frame never changes shape, and
the page fills it. A **tool** (select, blade, mask, reframe, window, transform) changes what the pointer
and the viewer overlays do; a tool never changes the page. The page model and the geometry are view
state only: no document, op, sync, render or API change.

### The seven pages and the address

The order is fixed and follows the editing workflow:

| Page | Left zone | Centre | Right zone | Timeline |
|---|---|---|---|---|
| Media | bins, search | the media pool (grid, list or filmstrip) | viewer and metadata | hidden; **Show timeline** reveals a 200 px strip |
| Cut | source tape | viewer | inspector | overview strip 120 + timeline 240 |
| Edit | panel tabs (Media, Markers, Edits, Analyse, Transcript, Subtitles, Takes, Structure, Comments, History) | viewer | panel tabs (Inspector, Assist, Settings, Versions, Compare) | 320 |
| Compose | Compose panels | viewer | Compose panels | 180 |
| Colour | node stack and target | viewer, scopes docked under it | grade tools | 180 |
| Audio | mixer | viewer | buses and voice-over | 280 |
| Deliver | render settings | — (no centre zone) | result column | hidden |

The active page lives in the URL:
`…/timeline/<timelineId>?page=media|cut|edit|compose|colour|audio|deliver`. `color` is accepted as a
legacy alias, the default is `edit`, and an unknown value falls back to `edit` and is normalised in the
URL. A page change writes **one** history entry with `history.pushState` and updates the store; a
`popstate` listener reads the page back from the URL. `router.push`/`router.replace` are not used for a
page change: the timeline route does not read `page` for data, and a router navigation can issue an RSC
request and remount the workspace. The switch therefore adds no request and no remount, and the
document, revision, history and save state are untouched.

Reload restores the page from the URL. With no `page`, the editor restores the **last page used on this
device** (`reelbolt:editor:last-page`, localStorage) with `history.replaceState`; a person who has never
opened the editor lands on Edit. A link without a page therefore opens the recipient's remembered page;
the link is not versioned.

### The switcher and page identity

- Desktop: a labelled page row in a 40 px band under the 48 px header. Labels never disappear.
- Tablet: a 64 px rail, icon over label, so "Compose" fits at the 12 px text floor. If a label still
  does not fit it uses a short label plus an accessible name, never a truncation without a name.
- Phone: no page switcher. The five-item dock is unchanged, and Cut, Compose and Colour stay
  unavailable there (see [Phone and tablet](#phone-and-tablet-wave-3)).

The visible label is **Colour** in English and **Colore** in Italian. The active page is
`aria-current="page"` with a visible focus ring and a description in its tooltip. A switch sets the
document `<title>` to "<page> · <film name>", makes the page name the page's `h1`, and moves focus to
that heading, so a screen-reader user hears a new-page cue (WCAG 2.4.2, 2.4.3, 4.1.3).

### The geometry table

Desktop defaults, in CSS pixels. These are the fallbacks: sizes are stored as fractions of the
container and pixels are derived from them.

| Page | Left | Centre | Right | Timeline | Viewer |
|---|---|---|---|---|---|
| Media | 280 (bins) | pool (flex) | 420 (viewer + metadata) | hidden (200 on request) | in the right zone |
| Cut | 320 (source tape) | viewer | 320 (inspector) | 240 + overview strip 120 | yes |
| Edit | 300 (panel tabs) | viewer | 320 (panel tabs) | 320 | yes |
| Compose | 340 (Compose panels) | viewer | 340 (Compose panels) | 180 | yes |
| Colour | 300 (nodes) | viewer + scopes 150 | 360 (grade tools) | 180 | yes |
| Audio | 360 (mixer) | viewer | 320 (buses / voice-over) | 280 | yes |
| Deliver | 420 (settings) | — (no centre) | result (fills the rest) | hidden | no (the result column has the player) |

Minimums are 240 px per side zone, 160 px for the timeline and 120 px for the overview strip. Deliver is
a two-column page that fills the frame and declares no centre and no timeline; every other page keeps
the three-zone shape.

### One persistence model

`web/lib/editing/layout/editor-layout.ts` owns the table above and the record. `PanelLayout` reads the
page's entry instead of per-call-site `initialLeftWidth`/`initialRightWidth`.

- **Fractions are authoritative.** The record stores fractions; pixels are derived and clamped to the
  minimums. Hydration order is URL page → stored fractions for that page → the table default. On a
  window resize, fractions re-derive pixels, so there is no drift.
- **One record, per device:** `reelbolt:editor:layout:v2` →
  `{ [page]: { fractions: { left, right, timeline } } }`. The key is per origin and browser; when the
  page knows the signed-in user id, it is suffixed with it. A denied localStorage is a soft failure
  with a plain note, not a blocking error.
- The media pool's three panes are nested under the Media page's record. The legacy `pool-panes`
  localStorage key is read once on first load, migrated into `layout.v2.media` and deleted; it is never
  read again.
- Applying a saved layout replaces the page's fractions and persists them. Server saved layouts keep
  their existing shape (they already store fractions).
- Two explicit actions: **Reset this page** and **Reset every page**. The second asks for confirmation
  first; there is no destructive double-press.

### Masks and reframe

**Add mask** (rectangle or ellipse) and **Add reframe** are offered in the timeline clip menu and in a
**Mask and reframe** section of the Inspector. Adding one writes the existing document value, selects the
clip, pauses playback and activates the mask or reframe tool; it never forces a page change. The
Inspector and Compose render one shared field set (`panels/MaskReframeFields.tsx`) and write the same
existing fields through `setProperty` in one batch, so the two hosts cannot drift. Compose is a list of what is on the selected clip (left) and the properties of the
selected entry (right), with an on-canvas mask tool (draw, `Shift` square, `Alt` from centre, `Escape`
cancels, `Enter` adds the default mask) and a corner pin tool (four handles, arrows 1%, `Shift` 10%). The
render warps the pin; the preview does not. An insert's quad has no handles; its corners stay number fields.

The viewer tool state is `viewerTool: 'select' | 'mask' | 'reframe' | 'pin' | 'window' | 'transform'`, beside the
existing `tool` and outside the document. The **paused auto-show is unchanged, including on the phone**:
when playback is paused and the selected clip has a mask, a reframe point or a grade window, its handles
are on the picture. A phone has no Compose or Colour page, and a desktop-made mask keeps its draggable
44 px handles there while paused. When a shape tool is active, its handles stay on the picture during
playback; the overlay subscribes through `withLivePlayhead` and computes at most a few times a second.
A tracked grade window remains pause-only, because its values follow every frame. The Transform tool is
no longer pause-only: see [The Transform tool on the viewer](#the-transform-tool-on-the-viewer). Entering a shape tool pauses playback and focuses the picture; Escape leaves the tool and restores
today's paused behaviour. Every handle stays a focusable button with a name, arrow-key nudge (1%, Shift
10%) and one undo step per drag.

Mask, reframe, pin, keyer and stabilise are no longer render-only: the viewer draws them (stabilise from a
stabilised proxy) and the badge names the small differences. See [What you see is what you export
(editor-wysiwyg)](#what-you-see-is-what-you-export-editor-wysiwyg) for the current table.

### Colour

The Colour page has no "Advanced tools" gate. It opens on the grade: the node stack and target selector
on the left, the viewer with the scopes docked under it, and the grade tools on the right. The tools are
tabs, not hidden modes: **Primaries | Curves | LUT | Secondaries | Groups | Gallery**.

- **Scopes are always docked** under the viewer, with a collapse control. Statistics are computed for
  the page whether or not the panel is expanded, so **Match to reference** works from any tab.
- **Secondaries** holds the qualifier and the grade window. Selecting a node does **not** auto-activate
  the Window tool; the window section has an **Edit window on the picture** button that sets
  `viewerTool = 'window'`, and Escape leaves it. A paused node with a window still shows its handles
  without that button, and a tracked window stays read-only at its tracked position.
- The look gallery is the **Gallery** tab, not the page.
- **Curves, LUTs and secondaries are drawn in the viewer** (`preview/grade-pass.ts`, editor-wysiwyg): curves as a
  natural-spline table per node, LUT nodes sampled tetrahedrally with the node's intensity, qualifier and power-window mattes
  confining the node's correction as `maskedmerge` does. Older text that calls them render-only is out of date;
  their parity is faithful.
- **Primaries** shows lift, gamma and gain as round wheels plus offset; see "Colour wheels and Fix exposure (color-tools)".
- The graph and node editing (named, reorderable, soloable nodes and the serial grade graph), the scopes
  layouts and the compare bar shipped in Wave 4; see "Colour depth (pro UI wave 4)".

### Media, Cut, Audio and Deliver

- **Media** is a page in the frame: bins 280 | pool | viewer and metadata 420, with the timeline hidden
  until **Show timeline** reveals a 200 px strip. The pool's container queries keep working in the
  narrower Edit tab. Placement is one control: **Insert**, **Overwrite**, **Add to end** and **Place on
  top** have the same labels and hints in the pool tile menu, the source viewer, Cut and the phone. Wave
  1 adds no placement chords.
- **Cut** gets the page shape it was built for: source tape 320 | viewer | inspector 320, an overview
  strip band of 120 and the record timeline 240. The Source/Record switch inside the same viewer is
  Wave 3; it adds no second viewer and does not change the geometry.
- **Audio** splits its content across the frame: the mixer in the left zone, the buses and the
  voice-over recorder in the right zone. "Advanced audio" stays a persisted preference and keeps its
  meaning: it folds only secondary parameters (buses, compressor, gate, limiter, de-esser, numeric EQ),
  while faders, pan, mute/solo and loudness stay visible.
- **Deliver** is a page in the shared frame with the timeline hidden: a fixed 420 px settings column and
  a result column that fills the rest, with the page header and the live chords. Wave 1 ships the frame;
  the page content (presets, queue, export scope, hand-off, review links) is Wave 5. No render or billing
  change.

---

## Core editing and rendering (P4)

P4 is the milestone: **a fully editable timeline that renders**. It extends the P1 engine without
reopening its contract, persists what P3 kept as overrides, and adds the first server-side consumer
of a v2 document that is not the validator — `StepType.TimelineRender`.

### The twelve P4 ops

The catalogue grows from thirteen to twenty-five. Every new op has a computed inverse drawn only
from the catalogue and a fast-check round-trip property beside the P1 ones
(`web/lib/editing/engine/__tests__/apply.property.test.ts`); the compile-time pin in that file and
in `apply.test.ts` stops compiling the moment a member is added without a property.

| Op | Payload | What it does | Inverse |
|---|---|---|---|
| `append` | `trackId`, `item` | Places the item at the track's end; only its duration is read. | `lift` |
| `replace` | `itemId`, `item` | Swaps the item's content, keeping its id **and span**. | `overwrite` (original) |
| `fitToFill` | `itemId`, `recordIn`, `recordOut` | Re-spans the item; on a `mediaClip` scales `playbackRate` so the same source range fills the new span. Refuses an overlap. | `overwrite` (original) |
| `placeOnTop` | `trackId`, `item` | Drops the item into free space at its own `recordIn`, no index, never a ripple. | `lift` |
| `join` | `itemId` | Merges the item with the follower that starts where it ends. **Refuses** a follower that is not an exact continuation (every field equal but id/span, `sourceIn` advanced by the item's length). | the one `blade` that made the pair |
| `nudge` | `itemId`, `deltaFrames` | Slides the item along its track within its neighbours. | opposite `nudge` |
| `addTrack` | `index`, `track` | Inserts a whole track — fresh id, items under the per-track invariants with fresh ids. | `removeTrack` |
| `removeTrack` | `trackId` | Removes a track and everything on it (one undo step). | `addTrack` at the old index |
| `moveTrack` | `trackId`, `toIndex` | Reorders compositing; refuses a no-op. | `moveTrack` back |
| `setTrackProperty` | `trackId`, `key`, `value` | One allowlisted field (`name`, `color`, `height`, `locked`, `enabled`, `mute`, `solo`, `busId`). `locked` is the **one** key a locked track accepts, or a lock could never be undone. `undefined` removes an optional key. | same op, old value |
| `setTimelineSettings` | `patch` | Validated patch of width/height/rate/sampleRate/pixelAspect/aspectPolicy/startTimecodeFrames. | same op, old values of exactly the patched keys |
| `setInOut` | `inOut` | A range, an explicit `null`, or `undefined` to remove the key. | same op, previous shape |

Why `join` refuses a non-continuation is the same information rule the P1 engine follows: its
inverse is a single `blade`, and a blade reproduces the right half **from the left half's fields**.
A right half that differed (a renamed clip, a different gain) cannot be resurrected by any blade, so
the join would undo to a different document; the engine refuses rather than performs it.

P5 then added keyframe authoring, a retime that changes duration and transitions (six more ops —
see [Clip depth (P5)](#clip-depth-p5)); link/group, compound clips and `applyProposal` still wait
for their item kind or phase (P6–P8).

### Editing UX

Track lock/enable/mute/solo are **persisted** through `setTrackProperty` (the header toggle clears
the P3 override for that flag, so the document is the one truth; a conflicted, read-only timeline
keeps the override so the preview still obeys). The header strip adds a video or audio track and
each row removes its track. The settings panel edits a local draft and **Apply** dispatches exactly
the changed keys as one `setTimelineSettings`. `,` and `.` nudge the selection by one frame (Shift:
ten), rightmost-first in the direction of travel so a group never collides with itself, as one
history entry.

**Refusals in plain words** (W0.6, d3 N1/N3, d1 N6/N8/N9): `lib/editing/refusal.ts` is the one
mapper from engine/validator refusals to sentences. An out-of-range field names its range, a locked
track names the track, op names, raw JSON and `[object Object]` are dropped, and every id the
document knows becomes its name (an unknown id is dropped, never shown). Every "Edit refused" toast,
the structure panel and the proposal panel go through it. A refused Inspector number field shows the
stored value again when left (only the field being left resets, so Tab into the next field keeps
focus there); clearing a clip or marker name is refused and the old name returns.

**Press `?` to open the keyboard shortcuts** (the header icon does the same; `?` is ignored in text fields and dialogs).

**Global shortcuts stay out of the way** (W0.6, UI2-09): `shortcutBlocked` in `keymap.ts` stops a
shortcut while a modal dialog is open, inside any dialog, in a text field or a control that owns its
keys (slider, menu, combobox), and for Space/Enter on a focused button or link. A clicked panel tab
keeps only the keys its tab list uses (arrows, Home/End, Space/Enter); every other shortcut still works.

**Timeline wheel and drag** (W0.6, ui1 D9/D12): the canvas registers its wheel handler as a native
**non-passive** listener (once, latest closure through a ref), so Ctrl+wheel and trackpad pinch zoom
the timeline rather than the page, with no passive-listener console error. While a clip is dragged a
dashed **ghost** shows the frame and lane it will land on, with the same snapping as the drop.

### Remove silence

`web/lib/editing/silence/remove-silence.ts` compiles a reviewed set of record-frame spans into
blades at the span edges plus a ripple `extract` of the piece between — rightmost first, so an
earlier cut's ripple never moves a later span — by running the engine itself against the document as
it will be when each op executes, so every id a later op names is one an earlier blade really made.
`RemoveSilenceDialog` reads the `VideoAnalyze` artifact the timeline was cut from (`silenceSpans`,
source seconds; found through the seed's edit decision list, see
[Where the analysis comes from](#where-the-analysis-comes-from)), maps each gap onto the chosen
track's clips **of the file it was measured in** through `sourceIn`/`playbackRate`
(`mapSourceSilenceToRecord` with a `SourceMatcher`), lists them with checkboxes and timecodes, applies padding and a minimum
length, shows the cut count and removed frames **before** anything changes, and dispatches the
program through `dispatchBatch` as ONE undo step. A cut picture takes its sound with it: linked partners, and also the exact UNLINKED twin on a track of the other kind (same source, `sourceIn`, span and speed, as a timeline made from a render holds them), are cut at the same frames. A timeline not seeded from an analysed render has
no measured silence to offer and the dialog says so instead of guessing. The toolbar entry
(`RemoveSilenceButton`) is a labelled button on desktop and an icon button named "Remove silence" on
a phone; it is never hidden below a breakpoint. On a coarse pointer or a phone width
(`(max-width: 48em), (pointer: coarse)`) the dialog opens full screen and every control in it (track
select and its options, the two frame inputs, each gap's checkbox label, Cancel, Remove and the close
button) is at least 44 px tall, with Cancel and Remove 12 px apart.

### `StepType.TimelineRender`

`TimelineRenderStepConfig` (`EditTimelineId`, `Version`, `Revision?` — null = latest when the step
starts, pinned into the output — `Range?`, `Preset`, `Encode?`, `OutputFileName`,
`RegisterProjectFile`, `Loudness?`) is validated by `Validate()` at every save path and again by the
executor, which loads the document at the pinned revision and **re-validates it through
`EditTimelineValidator`** — never trusting the stored row (a document whose only violations are ids
missing from the project, so a deleted file, fails `ASSET_UNRESOLVED`; anything else `DOCUMENT_INVALID`)
— then resolves every `AssetRef` inside the project (`ASSET_UNRESOLVED`, `ASSET_OUTSIDE_PROJECT`),
downloads and probes the assets, and renders.

The render (`Services/Video/TimelineRender/`):

1. `SegmentPlanner` splits the record timeline into **regions of constant composition** and plans
   the audio clips.
2. `RegionRecipeHasher` hashes each region's recipe — items, source ranges, every parameter and
   keyframe **within the region**, asset fingerprints, font fingerprint, renderer version.
3. Each region renders to a **mezzanine** — all-intra H.264 at CRF 10, yuv420p
   (`TimelineRenderPresets.MezzanineVideoArgs`) — through one bounded `filter_complex` built by
   `RegionFilterGraphBuilder` (overlay text through the sanitiser into `textfile=`, never into the
   filter string), unless `IRegionCache` has the hash: **a cache hit skips ffmpeg**, so trimming one
   clip re-renders only the regions it touches. `ObjectStoreRegionCache` stores regions under the
   project; `NullRegionCache` is the test/parity seam.
4. Regions are joined by the concat demuxer with stream copy.
5. `AudioMixBuilder` mixes every audio clip in **one sample-accurate pass** — `atrim`/`aresample`,
   `atempo` for a playback rate, keyframed `gainDb` as a balanced binary `if()` tree
   (`KeyframeExpressionBuilder`, capped per graph at the measured ceiling), `adelay`, then
   `apad=whole_dur=<program>` on **every clip before the sum**, `amix normalize=0` — and measures
   loudness; with `Loudness` set, a second linear `loudnorm` pass normalises. Each input is padded
   first because ffmpeg 8.0.1's `amix` ends at the last sound and an `apad` after it only sometimes
   recovers, so the mix length never depends on where `amix` stops.
6. The delivery encode muxes the concat and the mix, cut at the program length with an explicit
   `-t` (never `-shortest`, so a short stream cannot trim the other), to the preset (`Hd1080p16x9`, `Uhd4k16x9`,
   `Vertical9x16`, `Square1x1`, `Portrait4x5`, `Master` = the timeline's own geometry at CRF 14/slow,
   `AudioOnly` = AAC in `.m4a`), refined only by the allowlisted `Encode` fields (even dimensions
   16..7680×4320, codec `H264`/`H265`, CRF 0..51, speed `Fast`/`Medium`/`Slow` → x264 presets, audio
   64..320 kbps).

Output JSON: `status`, `revision`, `preset`, `width`/`height`/`fps`, the frame range, `regions
{ total, cached, rendered, list[{startFrame, endFrame, cached, hash, layers}] }`, `loudness
{ integratedLufs, truePeakDbtp, loudnessRange, normalised, checks[] }`, `skippedItems[{itemId, type, reason,
message}]` (each `message` a plain sentence, e.g. "A screen insert is not part of rendered videos
yet, so it was left out."), `warnings` (only notes that are not about one item; a skipped item is
never copied here, so Deliver shows each skip once), `invocationCount`. The
step result's `OutputStorageKey` is the playable file (so `OutputsController` serves it like a
compile), `TimelineStorageKey` a `TimelineManifest` of the result so the render reopens like compile
output. **Why the mezzanine stays at CRF 10:** a lossless (`-qp 0`) mezzanine was measured to buy
only ~1 dB of parity against the spike while being an order of magnitude larger in the region cache;
the second encode generation costs about 3.5 dB in total, and the gate was re-centred rather than the
renderer bent toward the spike (see the parity note below).

**Trust boundary (§3.7), end to end.** Presets, codecs and speeds are enums; every number is
range-checked in `Validate()` (so a save-path 400, never an argv); the file name is a bare,
allowlisted name; assets are server-resolved and prefix-checked; text is sanitised into files; work
is bounded by the document's own length and the regions' count. `FailureClassifier` treats the
config/document/asset codes as the author's (`INVALID_CONFIG`, `TIMELINE_NOT_FOUND`,
`REVISION_NOT_RESTORABLE`, `DOCUMENT_INVALID`, `ASSET_UNRESOLVED`, `ASSET_OUTSIDE_PROJECT`) and the
unreadable-document, ffmpeg and catch-all codes as ours (`DOCUMENT_UNREADABLE`, `RENDER_FAILED`,
`UNEXPECTED`). A mix carrying more than 8 keyframed gains is refused before any ffmpeg call as
`DOCUMENT_INVALID` (the author's, like any other document the render cannot take) with a plain
sentence. The step is bound to the `VideoTransform` placeholder agent, `StepCachePolicy` says
**never** (the region cache is its cache, keyed by recipe, and the step output carries its own cache
picture), the replay dependency graph knows it reads no earlier step, and credit estimation holds the
compile row.

### Caption looks, positions and sidecars (R09.6 / R09.7 / R10.5 render side)

`TimelineRenderStepConfig.Captions` (`None | BurnIn | Sidecar | Both`, additive; absent = the old
behaviour, enabled captions burned in, no sidecar, so stored configs and their argv are unchanged).
`None` and `Sidecar` leave captions out of the picture; `Sidecar` and `Both` also write `<film>.srt` and
`<film>.vtt` as run artifacts (`captionSidecars` in the step output: storage keys and cue count). The
cue list is every caption item (enabled or not, the track may be the disabled "sidecar only" track),
ordered by start, clipped to the render range and shifted to zero; the text is the sanitised lines the
burn-in draws.

Burn-in uses drawtext with `textfile=`/`expansion=none` (libass is not needed). A caption with no look
and no position keeps the legacy bytes. Otherwise: `CaptionBurnIn` maps `look` (clean, bold, boxed,
outline, spotlight) to size (4.5 percent of the SHORT side, bold 1.25x), colour and decoration, and the
3x3 `position` to `x`/`y` with an 8 percent safe margin (14 percent of the height on a vertical frame);
each `\n` line is sanitised on its own (max 6 lines), long lines wrap to 84 percent of the width, and
multi-line text is aligned to its column with `text_align`. `TitleClip.position` moves plain, boxed
and outline titles; `lowerThird`/`card` keep their own placement. `RendererVersion` is v5 (multiline
captions used to be collapsed to one line). Preview/render parity: the preview draws captions
approximately (browser font, no exact wrap), so the parity badge lists captions as "approximate".

The render request carries the same choice as `captions` (`none|burnIn|sidecar|both`, by name; an
integer is a 400; absent = burn in). Sidecars are registered as project files like the film and the
renders list row gains `captionFiles: [{format, fileName, sizeBytes, fileId}]` (download through the
project file download, never a storage key).

### The render endpoint and the queue

`POST …/edit-timelines/{id}/render` (Edit) first refuses an unverified email address
(`403 EMAIL_NOT_VERIFIED`, like every run), validates the request as a `TimelineRenderStepConfig`
(`preset`, `encode.codec` and `encode.speed` bind **by name only**, case-insensitively, through
`TimelineRenderEnumJsonConverter<T>`; the web sends `"Hd1080p16x9"`, `"H264"`, `"Medium"`; an integer
is MVC's binding `400`, never an ordinal that picks a preset — stored step configs still load a legacy
integer, because every reader of `TimelineRenderConfigJson` passes options with a plain
`JsonStringEnumConverter`, which wins over the type attribute), refuses a revision with no stored
document (`400`), creates a one-step `WorkflowDefinition` of
`Kind = WorkflowDefinitionKind.TimelineRender` (stored as `4`; hidden from the standard workflow
list, allowed through the retry endpoint) and queues it through
`IWorkflowExecutionService.QueueAsync` with that kind (the public `ExecuteAsync` only starts
`Standard` workflows) — so `WorkflowExecutionRunner`, `MaxConcurrency`, cancellation provenance,
`step.progress` SSE and the stop endpoint all apply unchanged — answering `202` with the
execution/workflow ids and the pinned revision. The workflow insert and the execution insert are
**one transaction**: `QueueAsync` takes a `beforePublish` hook that the endpoint uses to commit after
the execution row is saved and before the bus message goes out, so the engine never receives a run
whose rows are not committed, and any failure before the commit rolls everything back (no
`workflow_definitions`, `workflow_steps` or `workflow_executions` row). An org at its plan's
concurrency cap gets the `429` plan-gate body (`{ code, feature, message, limit, current }`, a plain
sentence in `message`), never a `500`; an explicit size above the plan stays `402 PLAN_NOT_ENTITLED`
(below). A publish failure after the commit is the ordinary lost-delivery case: the execution stays
`Queued` and `ExecutionRecoverySweeper` re-publishes it.

When `outputFileName` is blank the server derives it from the timeline name
(`EditTimelinesController.DefaultRenderFileName`), slugified to the config's allowlist: accents are
folded (`Café` → `Cafe.mp4`), dashes become `-`, anything else outside ASCII letters, digits, `.`,
`_`, `-` and spaces is dropped, runs of dots collapse, and a name with nothing left (blank, all CJK)
becomes `timeline-render.mp4` — never a `400` for a name the user did not type. A name the user did
type is still validated as-is.

**Orphan reaper.** Before the start became one transaction, every render attempt that failed after
the workflow insert left a hidden `Render: <name>` workflow with no execution. `TimelineRenderOrphanReaper`
(a hosted service in the Inference API, not a migration) deletes `Kind = TimelineRender` workflows with
no execution older than one hour (steps go by the FK cascade) at startup and then hourly, logging the
count; it is idempotent (one `DELETE` over the selection rule) and switched off with
`Editing:RenderOrphanReaper:Enabled=false` (`IntervalMinutes`, `MinAgeMinutes` tune it).

**In the Deliver panel** a refused start shows inline (`render-refusal`) and as a toast, in plain
words (`components/editor/deliver/render-refusal.ts`): the server's own `{ message, errors }` sentence
for a `400` it composed (field names rewritten to the form's words), the plan sentence for `402`/`429`,
and a fixed sentence for a binding ProblemDetails, a `404`, a `5xx` or a network failure — never a
converter string, a JSON path or raw JSON.

The render step re-validates the stored document; clip provenance step results resolve through the
same ownership map the API's
`PATCH` uses (`TimelineAssetReferences.ProvenanceStepResultIds`). A user cannot forge a render of somebody else's timeline: the timeline is looked up inside
the project the access service granted, and the step's `EditTimelineId` is written by the server.

`GET …/edit-timelines/{id}/renders` (View) is the queue: every execution of a `TimelineRender`
workflow whose step config names this timeline, newest first (≤ 50), with status, preset, file name,
pinned revision, error, the completed step result id (`hasOutput`) and the `regions` picture parsed
from the step output — tolerant of output it cannot read. No storage key is returned; the file is
fetched through the outputs endpoint by step result id. Each row also carries `skippedItems`
(`[{ itemId, type, message }]`, `EditTimelineRenderSkippedItemResponse`) and `warnings` (plain
sentences) from the same output, so Deliver can say what a render left out. The renderer's internal
`reason` code is not exposed; an entry without a readable message is dropped, an unreadable `itemId`
becomes null, and both lists are empty (never an error) when the output lacks them or is malformed.
Each list holds at most 50 entries and each sentence at most 300 characters.

Each render row also carries facts about the finished file, every one null when unknown and never an
error: `durationSec` (from the step output), `sizeBytes` and `outputFileId` (the project file the render
registered, matched by storage key within the project) and `createdAt` (when it was queued). `GET
…/edit-timelines/{id}` carries `canEdit` (the Edit permission `PATCH` checks), so a caller who may only
view opens the document read-only; `PATCH` by that caller is still `403`.

### Encode contract: rate control and file size

`TimelineRenderStepConfig.Encode` (and `VideoCompileStepConfig`, as top-level `RateControl` and `VideoBitrateKbps`) carry a
rate control: `TimelineRenderRateControl` `Quality` (default), `Capped` or `Average`, plus `VideoBitrateKbps`.
`Validate()` requires the bitrate for `Capped` and `Average` (200..100000 kbps, `MinVideoBitrateKbps`/`MaxVideoBitrateKbps`)
and refuses it for `Quality`; an audio-only `TimelineRender` refuses both. On `VideoCompileStepConfig` both fields are omitted from the stored JSON when null,
so existing configs and step-cache keys do not move; `TimelineRenderEncodeSettings` has no such attribute. `TimelineRenderer.RateControlArgs` turns them into ffmpeg arguments:
`Quality` is the historical `-crf n`; `Capped` is `-crf n -maxrate {kbps}k -bufsize {2*kbps}k`; `Average` is a single-pass
`-b:v {kbps}k -maxrate {1.5*kbps}k -bufsize {2*kbps}k` with no CRF. The price is flat: `EncodeOutputSeconds` by tier, never by bitrate.

`render/estimate` also returns `estimatedSizeBytes` and `sizeIsApproximate`, from `RenderSizeEstimator` (independent of
billing; omitted when the document cannot be read). `Average` is the stated video plus audio bitrate over the length times a 1.02
container allowance; single-pass, it lands within roughly 10-15% of that. `Quality` and `Capped` use 0.07 bits per pixel per frame at CRF 23, halving
every 6 CRF steps, scaled for codec and speed, and are approximate; `Capped` keeps the file close to or under its ceiling (a 2x buffer lets it run a few percent over).

The web table of suggested values per destination is `web/components/editor/deliver/deliver-platforms.ts`
(`PLATFORM_SUGGESTIONS`): YouTube 8 Mbps / 320 kbps (YouTube's published 1080p recommendation; audio capped at our 320 limit),
Reels 5 Mbps and TikTok 6 Mbps on `Vertical9x16` (our own suggestions: neither publishes a bitrate), small 2.5 Mbps / 128 kbps.
Re-read each platform's guidance and update this paragraph and the user guide when a number changes.

### Render cost estimate and the credit check

`POST …/edit-timelines/{id}/render/estimate` (Edit) takes the same body as `render`, validates it with the
same `TimelineRenderStepConfig` rules (a `400` lists every violation) and persists nothing. Billing off
answers `{ billingEnabled: false, affordable: true }`. With billing on, `credits` is the compute-metering
price of the render: the output length (the range, else the timeline's end) times the resolution tier of the
output (`ComputeMetering.TierFor`: 720p, 1080p, uhd; a 1080x1920 reel is 1080p) priced as
`EncodeOutputSeconds` through `INonWorkflowChargeService.EstimateAsync`. The price is deterministic, so
`creditsLow` and `creditsHigh` equal `credits`. `balanceCredits` is the workspace's available balance and
`affordable` is `balance >= credits`. A size the plan refuses answers `affordable: false` with the plan's
sentence as `unavailableReason`. `estimateSeconds` is a heuristic and is labelled an estimate wherever it is
shown: 10 s of start-up plus the output length times 0.5 (720p), 1 (1080p) or 3 (uhd), rounded up to 5 s;
the region cache can make a re-render much quicker.

`POST …/render` runs the same price before it writes anything, in the order every run start shares: the
plan's `402 PLAN_NOT_ENTITLED` first, then `402 INSUFFICIENT_CREDITS` with `{ code, message, missing }`
(`CreditShortfallMessage`), and only then the queue, whose plan concurrency cap stays `429`. A price that
cannot be worked out fails open: the engine's own credit gate still decides.

### Source length check

The save path (`PATCH`, create, import) fills the validator's source-duration map
(`SourceDurationResolver`): a project file's duration is its ingest probe's
(`media_derivatives.source_probe_json`), a step result's is its probe, else the newest `VideoAnalyze`
artifact's; frames are at the timeline's rate, rounded up, and read once per request. A media clip that
needs more source than that (`sourceIn` plus its extent times `playbackRate`, one frame for a freeze) is
refused with a `400` that names the clip by its label and gives the lengths in seconds. A source with no
known duration is not checked and never refused. The same rule is in `assertInvariants`
(`options.sourceDurations`), and the contract corpus carries it as the optional fixture field
`sourceDurations: [{ projectFileId | stepResultId, frames }]` (`source-*.json`).

### Export resolution and the plan cap

A timeline export respects the org's plan `MaxOutputHeight` (Trial 720, Creator 1080, Pro and Team
2160, Enterprise unlimited), measured on the **short side** of the delivered frame. The size table is
`TimelineRenderSizing` in `ReelBolt.Shared`, read by the API's gate, the engine's clamp and the
preset resolver alike: `Dimensions(config, timelineWidth, timelineHeight)` is the preset's size
(`Hd1080p16x9` 1920x1080, `Uhd4k16x9` 3840x2160, `Vertical9x16` 1080x1920, `Square1x1` 1080x1080,
`Portrait4x5` 1080x1350, `Master`/`AudioOnly` the timeline's own, evened), with an explicit encode
width **and** height winning over all of them. Only an **explicit** size above the plan is refused;
everything else is clamped.

- **At the endpoint.** `POST …/render` reads `TimelineRenderSizing.ExplicitShortSide(config)`: the
  short side of an encode width/height when both are set, else 2160 for `Uhd4k16x9`, else null (a
  shape preset, `Master`, `AudioOnly`). A non-null value above the cap answers `402 PLAN_NOT_ENTITLED`
  with feature `resolution`, `limit` = the cap and `current` = the requested short side, after
  validation and before anything is written. The value is judged on the **resolved** size, so
  `Uhd4k16x9` with a 1280x720 encode override is accepted on Trial.
- **In the engine.** `TimelineRenderStepExecutor` reads the plan's `MaxOutputHeight` the way
  `VideoCompile` does (no cap for an org-less run, an unlimited plan, a missing entitlement service or
  a lookup that fails; logged, fail-open). When the picture's short side is above the cap,
  `TimelineRenderPresets.CapToShortSide` rewrites the config's encode size to the aspect-preserving
  even size at the cap (`TimelineRenderSizing.CapToShortSide`, never upscaled) **before** the preset is
  resolved, so the region hashes and the encode agree. Only the size changes: a config without
  encode settings gets the preset's own CRF, speed and audio bitrate (`Master` keeps CRF 14, slow).
  This clamps the shape presets and `Master` on a capped plan (Trial `Vertical9x16` comes out
  720x1280), and covers a plan downgraded after an explicit size was accepted. The cap is the plan's
  raw `MaxOutputHeight` only for an editor export (the hidden workflow of `Kind = TimelineRender`). A
  `TimelineRender` step inside an ordinary workflow run caps at the run's effective resolution
  (`OutputResolution.EffectiveMaxHeight(plan, run choice)`), the same rule as `VideoCompile` and the
  Remotion render, so Auto stays at or under 1080 on a capped paid plan. A failed workflow-kind lookup
  takes the same fail-open path as a failed entitlement lookup.
- **In the output.** On a capped plan the step output JSON and the uploaded render report gain
  `planCap: { maxOutputHeight, applied }` (`applied` is true when the size was rewritten). An unlimited
  plan, or a run with no plan, adds no `planCap` field.

**In the Deliver workspace.** `DeliverWorkspace` reads the cap from `useOutputHeightCap()`
(`entitlements.maxOutputHeight` from `GET /billing/summary`, null when unlimited or billing is off) and
passes the timeline's short side for `Master`. `RenderSettings` disables only the 4K preset above the
cap and explains it under the select (`Your plan renders up to {cap}p. Upgrade to render in 4K.`, or
`… Upgrade to render at 1080p or 4K.` below 1080, with a **See plans** link to `/settings/billing`). A
shape preset or `Master` above the cap stays selectable and shows `Exports at {cap}p on your plan.`
with an **Upgrade for higher resolution** link. The server stays the authority.

### The Deliver workspace and the cache strip

`DeliverWorkspace` = `RenderSettings` (preset, marked in/out range when both marks are set, file
name, loudness, custom encode) + `RenderQueue` (rows with stop for an active render, download for a
finished one (`GET …/outputs/{id}/download?saveAs=<file name>` answers an attachment under that name, keeping the stored extension; without `saveAs` the endpoint stays inline for playback), the error for a failed one, and the `step.progress` stage/percent of the render being
followed, from `useExecutionStream`). A finished row whose summary carries `skippedItems` or
`warnings` adds a plain line ("2 items were left out of this render · 1 note") and a **Show details**
disclosure (`RenderNotes`, 12 px text, a 44 px target under `pointer: coarse`) listing each sentence:
a skipped item is prefixed with its name from the document on screen when its `itemId` resolves
(`itemNamesOf`, tracks and nested sequences), else its kind in plain words (Clip, Title, Caption…,
looked up by the engine's C# type name such as `InsertClip`, which is what `skippedItems[].type`
carries), else nothing; an unknown kind is never shown by its internal name. `warnings` carries only
notes that are not about one item (the engine no longer copies skip sentences into it); as a defence
for older stored renders, which did copy each distinct skip sentence there (as "… (n items)" when it
repeated), a warning that only repeats a skipped sentence is still dropped and is not counted as a note. Absent, null or empty fields (older
renders) add nothing, and a queued or running row says nothing. `useEditTimelineRenders` polls the queue every 5 s only while a
render is queued or running. The ruler's foot paints the **render cache strip** — blue where a region
came from the cache, red where it was rendered — and the status bar's Playback details popover reads `Saved preview frames: N of M` (was `Render cache
cached/total`), both from the newest finished render **of the revision on screen**
(`latestRenderForRevision`): over unsaved edits, or when the newest render is of another revision,
nothing is painted and the chip says so. The strip never guesses.

### Deliver, result first (Wave 1: R06.1-R06.9, R09.10)

Deliver opens on the **latest finished film** (`FilmCard`): the player (a native `<video>` on the inline
`outputs/{id}/download` stream, `<audio>` for an Audio only render; never a storage key), the file name, length, size,
format and "N min ago", and four 44 px buttons: **Download** (`?saveAs=<file name>`, an attachment), **Open in project files**
(`/projects/{id}?tab=files&file={outputFileId}`; `FileManager` reads `?file=`, opens that file's folder, selects it and
scrolls it into view), **Copy link** (there is no public share link for a render, so it copies that in-app address and says
so) and **Delete** (confirm, then `DELETE …/outputs/{stepResultId}`; an editor render is its own one-step workflow, so
`DeleteTake` lets it go even though it is the latest take of its group; every other video keeps the latest-take rule). A
finished render whose file was deleted leaves the queue. Under the card, the status line `renderStatusLine` reads "Last render:
your latest edit" or "Last render: before your latest edits (N changes since)" from the render's revision against the one on screen.

Settings use plain words (`deliver-model.ts`): Format names (Full HD landscape, 4K landscape, Vertical for phones, Square,
Portrait 4:5, Master (large, for editing elsewhere), Audio only), quality in three steps (Draft = H.264 crf 28 fast, Good = the
format's own default, Best = H.264 crf 16 slow), and an **Advanced** fold with loudness and "Choose codec and speed myself"
(replaces the quality step). `fitWarning` says "This shape will crop or letterbox your picture" when the format's aspect differs
from the timeline's. The queue shows status words (the activity words: Waiting for a free slot, Running, Done, Failed, Cancelled); a failed row gives a
plain reason (an engine code or id is replaced by a fixed sentence; otherwise `humanizeError`), **Try again** (starts a render
with the settings shown, using that row's format) and **Remove** (hides the row on this device; the server keeps no failed-render
delete), a running row shows progress and **Stop**.

**Save, then render (R06.6).** Render first calls `flushSave` (EditorWorkspace: `sync.flush()`, then ok only if the store is not
dirty and the status is `saved`) and pins the render to the revision that was just stored. If the save fails, Render is off with
"Save first: <plain sentence>" (`saveFailureSentence`) and a "Try saving again" link. **Export only <clip>** (R09.10) sets the
in/out marks to the clip, switches the range on, saves and starts the render in one action, and says "Rendering <clip name>"
while it runs. The marks persist as before.

**Credit estimate (R06.7).** `POST …/render/estimate` is called (debounced 400 ms) whenever the settings, range or revision
change. Billing off, or no figure, shows nothing (never "0 credits"); otherwise "About N credits, about M min". When the
balance is short, Render is off and the panel says "You need N more credits" with an **Add credits** link to the billing page;
a `402 {code:'INSUFFICIENT_CREDITS', missing}` from Render is explained in the same words (`render-refusal.ts`,
`missingCredits`).

**Outputs elsewhere.** The Renders tab's earlier-takes list and the workflow step result panel download through
`getOutputDownloadUrl` (attachment under the file's name); the step result panel gained a "Download video" button.

### The golden-parity gate, re-calibrated for the real renderer (P4-Q1)

`GoldenParityHarness` now drives the production `TimelineRenderer` through
`TimelineRenderSeamAdapter` (one emptied scratch directory per render, `NullRegionCache`). Measured on
this host (ffmpeg 6.1.1): P1 — the same render twice — is **byte-identical argv and bit-exact
frames**; P2 — the renderer against the spike's one-generation segmented-concat path — **43.97 dB**;
P3 — a one-frame boundary shift — **28.09 dB**. The PSNR floor is **36 dB**, the midpoint of the two
bands (7.97 / 7.91 dB margins), replacing the spike-era 40 dB (47.49 vs 28.04 when both legs were
spike paths). The audio leg tolerates **0.05 dB** between the two renderers (measured 0.003 dB: the
production mix's 16-bit PCM intermediate before AAC against the spike's float chain) while the same
renderer stays at exactly 0, and the 6 dB sensitivity probe still trips the 1 dB gate. Provenance,
including the lossless-mezzanine experiment, is in `GoldenParityThresholds`.

### What P4 leaves to later waves

Keyframe authoring, duration-changing retime and transitions (P5, below); link/group, compound clips
and `applyProposal` (P6–P8); export/import (P6). The browser assertion of a blade → drag → render →
play round trip (P4-Q2) still runs against a stubbed API: the e2e suite has no job that can host the
engine's ffmpeg, so the render half is proven by the real-ffmpeg parity gate and the executor tests,
and the browser half by the stubbed spec.

---

## Clip depth (P5)

P5 gives a clip depth: a picture transform, a crop, fades, reverse and freeze-frame; keyframes on
every numeric channel with a curve editor; a retime that changes duration; user transitions from the
full `xfade` catalogue with audio crossfades; colour/bars/test generators; a track fader, pan and EQ;
a live LUFS meter beside the render's measurement; and ducking as an editable envelope. Every one of
these is a **document field**, so the compositor previews it, the renderer renders it, the validator
bounds it and undo covers it — and every one of them defaults to the identity, so a document that
never touched them renders through the byte-identical P4 argv (the golden-parity gate keeps
measuring that path).

### The contract additions

| Where | Field | Shape | Range (validator + engine, identical) |
|---|---|---|---|
| `MediaClip` | `transform` | `ClipTransform { positionX, positionY, scale, rotation, opacity }`, each a `Keyframed<number>` | positions `[-2, 2]` (fractions of the canvas), scale `(0, 8]`, rotation `[-360, 360]` degrees, opacity `[0, 1]` |
| `MediaClip` | `crop` | `ClipCrop { left, top, right, bottom }` | each `[0, 1)`, `left+right < 1`, `top+bottom < 1` |
| `MediaClip` | `reverse` | bool | at most `MaxReverseFrames = 1800` frames; exclusive with `hold` |
| `MediaClip` | `hold` | bool | a freeze frame: every frame shows source frame `sourceIn`; silent |
| `MediaClip` | `fadeInFrames` / `fadeOutFrames` | int | `>= 0`, sum `<=` the clip's duration; picture AND sound |
| `Track` | `gainDb` | `Keyframed<number>` | `[-60, 20]` dB — the fader; keys are automation |
| `Track` | `pan` | number | `[-1, 1]`, constant-power |
| `Track` | `eq` | `TrackEq { lowCutHz, highCutHz, bands[≤4] { frequencyHz, gainDb, q } }` | 20–20000 Hz, low below high, gain `[-24, 24]`, q `(0, 10]` |
| `Transition` | `transitionType` | string | the closed catalogue `EditTimelineValidator.TransitionTypes` = `TRANSITION_TYPES` (45 `xfade` words) |
| `Transition` | `easing` | string | an `InterpKind` name |
| `GeneratorClip` | `generatorType`, `generatorConfig.color` | string, `#RRGGBB` | `color` / `bars` / `testsrc` |

Every keyframed channel also obeys the P1 keyframe rule (≤ 256 keys, strictly increasing frames).
C# records, the JSON Schema and the TS mirror moved together (`EditTimelineSchemaDriftGuardTests`
pins the four new `$defs`). A `Keyframed` whose JSON `value` is missing deserializes as `0`; on a
range that excludes 0 (`scale`) that reads as "no static value", the identity — on both sides.

### The six P5 ops

The catalogue grows from twenty-five to **thirty-one**; the same compile-time pins and the same
round-trip property per op apply.

| Op | Payload | What it does | Inverse |
|---|---|---|---|
| `setKeyframe` | `target` (`{itemId}` or `{trackId}`), `param`, `frame`, `value`, `interp` | Adds or replaces one key of a clip's `gainDb`/transform channel or a track's `gainDb`, creating the container when absent. | the old key re-set; `removeKeyframe` for an added key; or — when the op had to **create** the container — `setProperty`/`setTrackProperty` restoring the whole previous container (absence included), so `null`-vs-`[]` never reaches the inverse |
| `removeKeyframe` | `target`, `param`, `frame` | Removes one key; never deletes the container (so `keys: []` is a legal resting state). | `setKeyframe` |
| `moveKeyframe` | `target`, `param`, `frame`, `toFrame` | Moves a key in time; refuses an occupied frame. | `moveKeyframe` back |
| `retime` | `itemId`, `playbackRate`, `recordOut` | Both at once, because `round(duration × rate)` is not invertible alone; ripples the followers by the change in length. | `retime` with the old pair |
| `addTransition` | `afterItemId`, `transition` (every field but the span), `framesBefore`, `framesAfter` | The cut at `c` becomes the span `[c − before, c + after)`: A is shortened by `before`, B starts `after` later with `sourceIn` advanced by `round(after × rateB)`. B must own `before × rateB` frames of source **before** its `sourceIn` (`insufficient_handles` otherwise); A's outgoing handle is only checkable where the asset's duration is known, i.e. by the renderer, which pads with the last frame. | `removeTransition` at `c` (plus a `setProperty sourceIn undefined` when B carried no `sourceIn` key before — absent and `0` are distinct shapes) |
| `removeTransition` | `transitionId`, `cutFrame` | Restores a hard cut anywhere inside the span; B's `sourceIn` moves back by `round((T.out − cut) × rateB)`. | `addTransition` with the split the cut implies |

The `setProperty` allowlist gains `sourceIn`, `transform`, `crop`, `reverse`, `hold`,
`fadeInFrames`, `fadeOutFrames` on a media clip, `transitionType`/`easing` on a transition and
`generatorType`/`generatorConfig` on a generator; `transitionType` and `generatorType` join `name`
as **required** keys (clearing one is refused). `setTrackProperty` gains `gainDb`, `pan`, `eq`.
Every value is range-checked in the engine with the validator's numbers, so a hostile value is
refused client-side before it is ever sent, and refused again server-side if it arrives anyway.

### The renderer

`SegmentPlanner` now plans a `Transition` whose two neighbours are enabled media clips as a layer
carrying `TransitionNeighbours`, renders `GeneratorClip`s, reads a reversed clip from its lowest
source frame and a held clip from `sourceIn`, and plans audio fades as `AudioFade` ramps (a clip's
own fades; or, for an audio transition, the outgoing clip extended through the span with a fade-out
and the incoming clip pulled back with a fade-in — an equal-length linear crossfade — when the
incoming clip has the source handle, otherwise a hard cut reported as `insufficient_handles`).
Keyframes still add **no** region boundaries: a region's filtergraph carries the whole curve as a
time expression relative to the region start, and `RegionRecipeHasher` relativises the keys the
same way, so a keyframe edit changes exactly the regions it covers. `RendererVersion` is
`timeline-render-region-v2` (every earlier cached region is simply a miss).

`TransformFilterBuilder` builds the layer chain: a clip with no transform, crop or fade takes the
**exact P4 chain** (`scale … force_original_aspect_ratio=decrease,pad …,setsar=1,format=yuv420p` then
`overlay=eof_action=pass`); a clip that uses any of them fits **without padding**, then `crop`
(fractions of the source), `scale=w='trunc(iw*(expr)/2)*2':…:eval=frame` by the scale channel,
`format=rgba` + `rotate=a='(expr)*PI/180':ow='rotw((expr)*PI/180)':oh='roth((expr)*PI/180)':c=0x00000000`
for a static angle, `ow='hypot(iw,ih)':oh='ow'` for a keyframed one (the output size is evaluated once,
before any frame, where neither `a` nor `t` has a value), alpha — a constant
through `colorchannelmixer=aa=`, an animated one (keyframed opacity or a fade overlapping the region)
through `format=gbrap,geq=…:a='alpha(X,Y)*clip(expr,0,1)'` with the curve rewritten in `T`, used only
where the alpha actually varies because `geq` is per-pixel — and `overlay=x='(W-w)/2+W*(expr)':y=…:eval=frame`.
A transition is two legs over the WHOLE span (`-ss` past A's end / before B's start, each
`tpad=stop=-1:stop_mode=clone` so a source that runs out never shortens the blend), `xfade=transition=<word>:duration=<span>:offset=0`,
then `trim` to the region, so a title starting mid-transition splits it into two regions that still
blend correctly. A generator is a `color`/`smptehdbars`/`testsrc2` source of the region's length.
Reverse is `reverse,setpts=PTS-STARTPTS` on the trimmed leg (bounded by the validator's ceiling,
since it buffers the segment); hold is one frame `loop`ed. A graph longer than
`MaxInlineGraphChars` (60 000) is written to a file and passed as `-filter_complex_script` — the
P0-M1 spike measured ffmpeg dying at execve with an empty stderr past a few hundred KB of argv —
and below it the argv is unchanged.

`AudioMixBuilder` folds fades into the clip's `volume` expression (`gain × clip((t−t0)/d,0,1) × …`),
reverses with `areverse`, and — only when some track asks for a fader, pan or EQ — sums the clips
**per track** into a bus (`volume` static or automated through the same `if()` tree with the
range start as time origin, constant-power `pan=stereo|c0=L*c0|c1=R*c1`, `highpass`/`lowpass`/
`equalizer=f=:width_type=q:w=:g=` per band) before summing the buses; a document with no track
processing keeps the flat P4 graph byte for byte. The keyframed-curve ceiling counts track
automation too.

**Trust boundary.** Nothing new reaches ffmpeg that the validator did not range-check: every
transform/crop/fade/EQ number, the transition and generator words (closed catalogues on both sides),
and the generator colour (`#RRGGBB` or black). The renders endpoint now also returns the render's
`loudness` block (`EditTimelineRenderLoudnessResponse`), parsed tolerantly from the step output.

### The compositor

`activeLayersAt` returns BOTH legs of a transition (the outgoing clip underneath, the incoming one
on top at the eased progress); `layerTransformAt` evaluates the transform channels and folds the
fades into opacity; `sourcePositionFor` is the one source-position formula the renderer mirrors
(`SegmentPlanner.SourceFrameAt`): forwards, reversed from the clip's end, or held at `sourceIn`.
The WebGL compositor takes a per-layer transform as vertex-shader uniforms (rotation in pixel space
so the canvas aspect never shears it; crop as UV origin/size) and the layer's opacity in the fragment
shader. Parity: a dissolve/fade transition and a static transform are `faithful` (same math; GPU vs
swscale sampling for the transform is called out as `approximate` when a transform or crop is set), a
keyframed transform `approximate`, any other transition word `approximate` (previewed as a dissolve),
`color` generators faithful and the others approximate, reverse/hold/fades faithful.

### Audio

The WebAudio graph grew a bus per track — fader (static or `setValueCurveAtTime` automation), gate
(mute/solo, separate from the fader so a toggle never fights the automation), `StereoPannerNode`,
`BiquadFilterNode`s for the EQ, an `AnalyserNode` for the peak meter — and a channel-split pair of
analysers on the master. A reversed clip plays a reversed copy of its decoded buffer. `LufsMeter`
(`web/lib/editing/audio/lufs.ts`) is BS.1770-4 in plain TypeScript — K-weighting derived for any
sample rate from the published design, 400 ms/3 s windows at 75 % overlap, −70/−10 gating, 4×
linear-interpolated true peak (an approximation, labelled as such) — tested against a −20 dBFS sine
(reads −20 LUFS within 0.3 LU at 48 k and 44.1 k) and the filter's known coefficients. Because the
meter is fed sampled analyser windows it is an **indication**; the mixer shows the render's
`loudnorm` measurement beside it as the authority.

`deriveDucking` (`web/lib/editing/audio/ducking.ts`) writes the music track's fader automation from
the dialogue tracks' clip spans (merged when closer than attack + release; bed / bed + depth with
linear attack and release ramps, frames strictly increasing by construction), as ONE
`setTrackProperty gainDb` — so ducking is just keyframes: drag them in the automation lane, delete
them, re-derive. There is no separate ducking model.

### UI

The inspector's media-clip sections — Retime (speed, reverse, freeze), Transform, Crop, Fades,
Audio — put a keyframe toggle (◆) beside every numeric channel: with no keys the field edits the
static value; once a channel has keys the field writes a key **at the playhead** and the lane under
it (`KeyframeLane`, an SVG curve editor) selects, drags (one `moveKeyframe` on release), re-eases and
deletes keys. The Inspector follows the track kind of the selected clip (`InspectorPanel`: `onPictureTrack`):
a picture-track clip shows freeze frame, Transform and Crop and no gain or channel controls (the render mixes
sound from audio tracks only: `SegmentPlanner`), a sound-track clip shows gain and channels and no picture
controls. Number fields are `CommitNumberInput`s: one op per edit (Enter, blur, 600 ms pause, unmount), never
one per keystroke. A clip followed directly by another media clip offers **Add transition** (type +
length, centred on the cut, with the incoming handle shown); a transition item edits its type and
easing and can be removed to a hard cut. The **Audio** workspace is the mixer: a strip per audio
track (fader, pan, mute/solo, EQ, peak meter, automation lane), the master with live LUFS/true peak
and the last render's measurement, and the ducking derivation.

### Clip depth front door (Wave 2: R07.1, R07.3, R07.4)

No new op, no new document field. The new surfaces compose the existing catalogue and dispatch it with
`store.dispatchBatch` (one undo step):

| Piece | Where |
|---|---|
| Motion presets: zoom in/out, pop, slide (4 sides, in or out), fade, Ken Burns. `motionOps()` returns `removeKeyframe` for every transform key the clip has, then `setKeyframe` for the new keys (timeline frames inside the clip span; strength, length and entrance/exit are options). | `web/lib/editing/clip-depth/motion-presets.ts` |
| Plain units: opacity percent (`0..100` stored as `0..1`), fades in seconds (frames in the document, `checkFadeSeconds` refuses what does not fit with a sentence), speed chips and `planSpeed` (new length, fades shrunk to fit), volume percent (0% = `-60 dB`, the document minimum, so Mute is a stored gain, not a new field). | `web/lib/editing/clip-depth/units.ts` |
| Transition words (eight curated + the other 36 under More), parity line (`dissolve`/`fade` faithful, the rest approximate), `findSeams`, `addTransitionOp`, `addTransitionToAllOps` (right to left, skips cuts with no handle and counts them), `makeRoomOps` (ripple trim of the incoming head through `linkedOpsForTrim`, verified against the engine before it is offered). | `web/lib/editing/clip-depth/transitions.ts` |
| Inspector: `AnimateSection` (chips with looping CSS previews, Fine-tune keyframes collapsed), `ClipControls` (opacity, fades, speed, volume, Reset per section), `TransitionPicker` / `TransitionEditor`; expert items (dB, by-frames transition form with saved presets, easing) under **Advanced**. | `web/components/editor/panels/clip-depth/`, `InspectorPanel.tsx` |
| Timeline overlay: the "+" on every cut, an edit glyph on every transition, fade corner handles on the selected clip. DOM controls over the canvas, hidden on a phone (a phone reaches the same picker through the Inspector's Transition section). | `web/components/editor/timeline/SeamControls.tsx` |

Test ids: `animate-<preset>`, `animate-state`, `animate-remove`, `inspector-finetune-toggle`,
`inspector-advanced-toggle`, `inspector-opacity`, `inspector-fade-in|out`, `inspector-auto-fade`,
`inspector-speed-chip-<x>`, `inspector-volume`, `inspector-mute`, `inspector-reset-<section>`, `seam-add`
(`data-cut-frame`), `seam-edit`, `fade-handle-in|out`, `transition-word-<type>`, `transition-seconds-<s>`,
`transition-add`, `transition-apply-all`, `transition-make-room`, `transition-remove`.

### What P5 leaves to later waves

Blend modes and a grade (P7); keyframes on crop (crop is static); `xfade` words are previewed as a
dissolve; the LUFS meter is sampled, not gap-free (an `AudioWorklet` would make it exact); per-clip
EQ is not modelled (EQ is per track); surround channel weights; the browser assertion of a keyframed
transform playing back and a transition rendering (P5-Q2) runs against the stubbed API like P4-Q2.

---

## Workflow speed (P6)

P6 is about getting to a cut faster, and every piece of it is built on what P4/P5 already made
true: the engine's closed op catalogue, the store's one-undo-step batches, the analysis artifact's
measured facts, and the server's single trust boundary. **No new op was added.** Text-based editing,
subtitles, take stacking, boring-shot removal and smart insert all compile to programs of existing
ops (`blade`, `extract`, `insert`, `placeOnTop`, `replace`, `setProperty`, `lift`, `overwrite`,
`addTrack`), so they undo, preview, validate and render exactly like a hand edit. The one contract
change is on `setProperty`: `assetRef` joins the media-clip allowlist (and the required set, so it
can never be cleared) for **relink**.

### Where the analysis comes from

A timeline is seeded from a **VideoCompile** step result, whose artifact is the compile's edit
decision list (`VideoCompileStepExecutor.BuildEdl`): segments and codec, plus a pointer back to the
analysis it cut from — `analysisArtifactStorageKey` (always) and `analysisStepResultId` (only for a
cross-run recompile). It carries no silence, words, shots or duplicate groups. Every
analysis-driven surface (`RemoveSilenceDialog`, `TranscriptPanel`, `SubtitlesPanel`,
`TakeStackPanel`, `CutWorkspace`'s boring shots, `ColorWorkspace`'s groups from looks) therefore
reads it through one resolver, `web/lib/editing/api/analysis.ts` (`resolveAnalysisArtifact`, SWR
hook `useTimelineAnalysis`):

1. the seed artifact already is an analysis (it has `silenceSpans`/`shots`/`words`/…): use it;
2. the EDL names `analysisStepResultId`: stream that step result's artifact;
3. otherwise take the run id from the key (`…/video-analysis/{executionId}/…`), read the run
   (`GET /api/v1/projects/{p}/executions/{executionId}`), pick the step result whose
   `artifactStorageKey` equals the key, and stream its artifact
   (`GET …/step-results/{id}/artifact`).

Only existing project-scoped routes are used; the key is compared, never fetched or shown. A seed
that points at no analysis resolves to `null` and the panels say the run kept no footage analysis.

**Source-safe mapping.** The analysis lists its sources (`sources[].sourceIndex/storageKey`); the
resolver matches each to the project file with that storage key, and `sourceMatcher` builds a
`SourceMatcher(clip, sourceIndex)` that is true only when the clip's `assetRef.projectFileId` is
that file. Silence spans, words, paragraphs, shots and look groups are mapped only onto matching
clips, and word runs never merge across sources — on a two-file track, file A's silence never cuts
file B. An analysis without `sources` (written before multi-source support) is trusted only when
every media clip in the document shows one file; a source whose window was trimmed by the workflow
(its key is the trimmed copy) matches no project file and is not offered.

**Render outputs are sources too.** A clip whose asset is a render output (`assetRef.kind === 'stepResult'`) is read through the same per-source API with the ref `step_result:<guid>` (a project file is `file:<guid>`). `documentMediaRefs(doc)` lists both, capped at 8 sources per document, and the analysis is merged by ref, so a render output's words, silence and shots reach the Transcript, Cut, Subtitles and phone caption surfaces exactly like a file's. While any source is queued or running the status and results are read again every 2 s; starting an analysis from any panel refreshes them at once (`startMediaAnalysis` calls `refreshAnalysis`).

### Interchange: export and import

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/api/v1/projects/{projectId}/edit-timelines/{id}/export?format=otio\|edl\|srt\|vtt` | View | The saved document as a download (`File`, name = the timeline's name + the format's extension). `otio` is the default. |
| `GET` | `/api/v1/projects/{projectId}/edit-timelines/{id}/handoff?format=otio\|edl\|fcpxml&media=zip\|manifest&track=&includeAudio=&roles=` | View | The hand-off package (R12.2), a streamed ZIP. See [Hand-off package](#hand-off-package-wave-4-t-001-r122--r123). |
| `POST` | `/api/v1/projects/{projectId}/edit-timelines/import` | Edit | `{ name?, otio }` — the OTIO timeline as a JSON object. `201` with a new revision-1 timeline (and its snapshot); `400 EditTimelineImportRefusedResponse { message, errors, unresolvedUrls }` when the file is not an OTIO timeline or names media this project does not hold; `400` with the validator's violations when the imported document fails `EditTimelineValidator`. Body ceiling = the document ceiling + 64 KiB. |

`ReelBolt.Shared/Editing/Interchange/TimelineInterchange.cs` holds the four writers and the one
reader, all pure:

- **OTIO** (`Timeline.1 → Stack.1 → Track.1 → Gap.1 | Clip.1 | Transition.1`). Media are
  `ExternalReference.1` with `reelbolt://project-file/{guid}` / `reelbolt://step-result/{guid}` URLs,
  so another tool sees a stable identifier, never a storage key. **A ReelBolt export is lossless**:
  the whole document rides in `metadata.reelbolt.document`, and the reader restores it from there
  (new id, caller's name) before it even looks at the tracks. Titles, captions, generators and
  inserts have no OTIO clip; they show as `Gap.1` to other tools and come back through the metadata
  block. OTIO's transition *overlaps* the two clips (`in_offset` into the outgoing, `out_offset`
  into the incoming) where the v2 model gives it its own span, so the writer extends the outgoing
  clip by `in_offset = span/2` and starts the incoming `out_offset = span − span/2` earlier, and the
  reader trims exactly those offsets back off — `write → strip metadata → read` reproduces the
  cuts and the transition frame-exactly (pinned by `EditorP6BackendTests`).
- **Reading another tool's OTIO** maps `Clip.1` to a media clip (source range → `sourceIn`/duration
  at the file's own rate), `Gap.1` to a gap, `Transition.1` as above, and resolves each
  `target_url` through a resolver the controller builds from the project's own files and step
  results: a `reelbolt://` URL by id, anything else by **file name** (`file:///…/interview.mov`
  matches a project file named `interview.mov`). Any clip whose URL resolves to nothing refuses the
  **whole** import with every such URL named — a half-imported timeline would be worse than none.
  Imported clips carry `provenance.origin = imported`.

  **Offline media (T-020).** The document has no offline-clip type, so a lenient import (the default)
  imports every resolvable clip and leaves each unresolvable one as a hole of its length plus a red
  `Offline: <name>` timeline marker; the `201` body adds `offline: [{ itemId, name, reason, message }]`
  (`itemId` is the marker's id; `reason` is `notFound`, `ambiguous` or `durationMismatch`, see [Hand-off package](#hand-off-package-wave-4-t-001-r122--r123)). A ReelBolt-exported file restores the same way (its missing-asset
  clips are dropped and marked). The user relinks by placing the file at the marker. `?strict=true`
  restores the old all-or-nothing `400 { unresolvedUrls }`. No new op.
- **Scope facts.** `GET …/export/scope?format=` returns `{ format, carries[], dropped[], mediaMode }`
  so the UI words the export dialog from the server. FCPXML export takes `includeAudio` and `roles`
  (both default true); the version stays 1.10. Apple's FCPXML DTD is not redistributable, so the
  test is well-formedness plus the structural skeleton, not DTD validation.
- **CMX3600 EDL**: the first video track only, `TITLE:`/`FCM: NON-DROP FRAME`, one `C` event per
  cut, a `D nnn` event (plus `* EFFECT NAME:`) per transition between two media clips,
  `* FROM CLIP NAME:` and `* SOURCE FILE:` comments, CRLF line ends. Pinned byte-for-byte against a
  committed fixture.
- **SRT / WebVTT**: every enabled `Caption` in record order, millisecond clocks (`,` and `.`
  respectively), text verbatim. Deliver shows the SRT/WebVTT entries as plain "(no captions yet)" text, not links, while the document on screen has no enabled caption with text (`countCaptions`).

### Hand-off package (Wave 4, T-001: R12.2 / R12.3)

For a marketer handing the cut to a pro in Premiere Pro or Final Cut Pro: the timeline
file, the media under its real file names, a manifest and a README.

**`GET …/edit-timelines/{id}/handoff`** (View, 404 not 403, access consulted once; free of credits)

| Query | Meaning |
|---|---|
| `format` | `otio` (default), `edl`, `fcpxml`. `srt`/`vtt` have no media: `400`. |
| `media` | `zip` (default) puts the media in the package; `manifest` leaves it out and lists it as "not included". |
| `track` | EDL only: the id of the video track to write instead of the first one. Another id: `400`. |
| `includeAudio`, `roles` | FCPXML only, as for `export` (both default true). |

Answers a streamed `application/zip`; entries are written straight to the response (no temp file, no
whole-zip buffer). Headers: `Content-Disposition: attachment` with an RFC 6266 `filename*` of
`<timeline-slug>-hand-off.zip`, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` (plus the
usual download policy headers). Entries, in this order:

1. `<timeline-slug>.<ext>`: the chosen interchange file. Media references carry the real file name as a
   relative path: OTIO `ExternalReference.target_url` = `media/<name>` (percent-encoded), EDL
   `* SOURCE FILE: media/<name>`, FCPXML `media-rep src="media/<name>"` (percent-encoded). OTIO also
   gets `available_range` when the duration is known, and the media reference's
   `metadata.reelbolt` keeps `assetUrl` (the `reelbolt://` id) and `sizeBytes`; the document in
   `metadata.reelbolt.document` is unchanged, so a ReelBolt round trip is still lossless. No
   `reelbolt://` URL appears outside `metadata.reelbolt`. Without the option (the normal `export`) every
   writer's output is byte-identical to before (`TimelineInterchange.WriteOtio/WriteEdl(document, names)`
   and `FcpxmlOptions.HandOff`, the "hand-off media naming" option; the existing golden fixtures pass).
2. `media/<safe-name>`: every project file or render the document references, once each, method Store
   (they are already compressed). Names come from the one shared `HandOffNaming`: the file's own name,
   NFC, no path separators, control or invisible characters, no `..`, no Windows-forbidden characters,
   no reserved Windows device names (`CON`, `NUL`, `COM1`...: prefixed with `_`), no trailing dot or
   space, at most 120 characters with the extension kept, duplicates (case-insensitive) become
   `name (2).ext`. The writers and the zip read the same assignment, so a reference is exactly an entry.
3. `README.txt`: plain sentences on how to open the package in Premiere Pro, Resolve and Final Cut Pro.
4. `manifest.json`, LAST so it reflects what happened:
   `{ timeline, format, generatedAt, mediaIncluded, mediaNotIncludedReason, files: [{ name, path,
   status, inPackage, reason, sizeBytes, sha256, durationSec, usedBy[] }] }`. `status` is
   `included`, `missing`, `notIncluded` or `incomplete`. `sha256` is only filled where one is already
   stored with the file (never computed on the request path), otherwise `null`. Only file names appear:
   never a storage key, bucket or URL, in any entry or header.

**Caps and refusals.** `Editing:HandOff:MaxMediaBytes` (default 2 GiB; `0` = manifest only) and
`Editing:HandOff:MaxFiles` (default 200). Over a cap, `media=zip` does NOT silently drop anything:
`409 { code: "HANDOFF_TOO_LARGE", message, totalBytes, capBytes }`, and the UI then offers `media=manifest`.
`Editing:HandOff:MaxConcurrentPerUser` (default 2) hand-off streams per user; over it,
`429 { code: "HANDOFF_BUSY", message }`. Messages are plain sentences.

**Missing or unreadable media (decision).** A file that is gone from storage, that the timeline names
but another project owns (every reference is resolved inside the timeline's own project; a foreign id is
never read), or that cannot be opened, is listed `missing` in the manifest and the zip still completes.
A file that fails part way through is listed `incomplete` (a streamed zip cannot take an entry back) with
a sentence telling the pro to use the original. The package is never left without its manifest. A client
abort cancels the storage reads promptly and stops writing.

**Publish gate.** A hand-off is publication, like `export`: the interchange text goes through the same
publish moderation (`EvaluateTextAsync`), and so does each media object that would be included
(`EvaluateObjectAsync`), before the first byte is streamed; `CONTENT_BLOCKED` is `422`, an unavailable
classifier is `409`, fail closed when moderation is enabled.

**Scope.** `GET …/export/scope?format=` gains `handOff: { supported, mediaFiles, mediaBytes,
wouldExceedCap, capBytes }` (additive; `supported` is false for `srt`/`vtt`). The counts are distinct
resolvable sources and their stored sizes; `wouldExceedCap` is true when `media=zip` would answer `409`.

**Safe import matching (R12.3).** The import resolver (`ImportMediaMatcher`, in `ReelBolt.Shared`) maps
a media reference to a project file in this order: a `reelbolt://` id this project owns; else the file
name (case-insensitive, NFC, directory ignored, percent-decoded, either slash kind). When the OTIO states
`available_range` the project file's probed duration must agree within one frame at the clip's rate (an
EDL has no duration: name only; a file never probed is matched by name). Several files share a name:
the unique one that agrees on duration wins, then on size (a hand-off records `sizeBytes`); if it is
still more than one it is NOT guessed. The `201` body adds `matched: [{ name, fileId, how }]` with `how`
= `id` | `name` | `nameAndDuration` (`stepResultId` instead of `fileId` for a render), and
`ambiguous: [{ name, candidates: [{ fileId, name, durationSec?, sizeBytes }] }]`; `offline[]` entries
carry `reason` = `notFound` | `ambiguous` | `durationMismatch` and a plain `message`. Offline stays a hole
plus the red `Offline: <name>` marker (W2-D14); there is no document change. A hand-off OTIO imported
into a project that holds the same file names under other ids is remapped by the file name on each clip
(`metadata.reelbolt.itemId`), so the cuts come back intact. `?strict=true` is unchanged: any unresolved
reference refuses the whole import with `unresolvedUrls`.

**UI (Wave 4, T-002).** `components/editor/deliver/HandOffCard.tsx` ("For another editor", rendered inside `ExportPanel`),
words and choices in `lib/editing/interchange/handoff-ui.ts` (editor to file type: Premiere Pro and Resolve offer OTIO then FCPXML,
Final Cut Pro FCPXML, another editor OTIO, "Cuts only" EDL; size words; cap states; reason words; candidate ranking).
On a phone the card is a plain "desktop job" note. The download is a plain anchor to `…/handoff` (`editTimelineHandOffUrl`); on click
the card first asks once for the response headers (the fetch is aborted, no body is read) so `409 HANDOFF_TOO_LARGE`, `429
HANDOFF_BUSY` and `404` read as sentences, then navigates to the same URL. `scope.handOff.wouldExceedCap` disables "Include the video
files" with the reason and preselects "List files only". The track picker is offered for EDL only (the endpoint's `track` is
EDL only). Import: `TimelinesView` passes `?strict=true` only when the advanced switch "Import fails if anything is missing" is on,
and stashes the 201 body in `sessionStorage` (`reelbolt:import-result:<timelineId>`); the editor opens `ImportResultSheet`
(matched with how, offline with the reason in words) and the sheet's Relink button opens `OfflineRelink` for that marker, which
shows the reason on each banner row and, for an ambiguous name, a candidate picker (name, length, size) that relinks through the
same one-undo-step ops (`file:<fileId>` media reference). Not in this slice: "Relink by folder" (bulk), importing the ZIP.
Playwright: `e2e/tests/editor-v2/12-interchange/handoff.spec.ts` (pending run).

**Left out.** Importing the ZIP itself (`import-handoff`) is out of scope: the pro hands back an OTIO. A
real open check in Premiere Pro, Resolve and Final Cut Pro cannot be done here: the hand-off is verified
structurally and by the writer tests (relative references equal zip entries, FCPXML well-formed with the
1.10 skeleton), NOT by opening it in those applications. "Real-app open check" is a must-measure item
before the feature is called verified.

### Annotations and smart bins

`media_annotations` (Inference API owned, migration `AddMediaAnnotationsAndSmartBins`) holds one row
per source — `(project, sourceKind, projectFileId | stepResultId)` unique — with `keywordsJson`
(normalised lower-case, ≤ 32 × 64 chars), `note` (≤ 2000), `colorLabel` (closed: red, orange, yellow,
green, blue, violet, grey), `favourite`, `flag`. `media_bins.smart_filter_json` holds a bin's
`MediaSmartFilter`. The pool and bin routes stay as they were; the new routes and the smart-bin
behaviour are in [Bins](#bins). The client: `AnnotationEditor` under the media page's metadata
panel (one `PUT` per Save, only the changed fields), `SmartBinFilterFields` in the bin tree's
create form, a `smart` badge on such bins, drops on them refused client-side with a stated reason,
and the pool listing a smart bin's sources through the same items route.

### Text-based editing

`web/lib/editing/transcript/transcript.ts` reads the analysis's `segments` and `words`
(estimating word timing by character share when a segment carries no words — those words are marked
`estimated` and the panel says so), groups them into paragraphs by pause, and maps a word
selection onto record frames **through each media clip's `sourceIn`/`playbackRate`**, exactly as
remove-silence maps a silence span. **Delete words** is `compileRippleCut` = `compileRemoveSilence`
with no padding and no minimum: blade, blade, `extract`, rightmost first, one `dispatchBatch`. A run
of consecutive selected words is one window (the pause between two selected words goes with them;
`RUN_GAP_SEC = 0.35`), a selection with a hole cuts two pieces. **Find fillers** lists
`DEFAULT_FILLER_WORDS` and immediate repeats (the first utterance of "the the") as
`FillerCandidate`s, each a checkbox, and cuts only the accepted ones. **Paragraph reorder** (drag a
paragraph onto another, or the arrows) is `compileMoveSpan`: blade the span's edges and the target,
remember the target as an **item** (not a frame — the extracts ripple), `extract` the pieces
rightmost first, then `insert` them in order at the anchor's live start. Only media clips move; a
title or caption inside the span stays. `TranscriptPanel` re-reads the artifact against the live
document, so words whose frames left the track stop being offered.

### Subtitles

`web/lib/editing/subtitles/subtitles.ts`: `buildCues` packs timed words into cues under a
characters-per-line × lines-per-cue budget, closing a cue on a sentence end, a pause over a second
or the duration ceiling, balancing two-line cues and enforcing a one-second minimum and a two-frame
gap. `compileGenerateCaptions` places them as `Caption` items with `placeOnTop` on an existing video
track (a cue over an existing item is skipped and counted, never forced) or on a new track
(`addTrack` after the last video track). With no track chosen it reuses the captions track
(`findCaptionTrack`: a video track holding only captions, preferring one named "Subtitles"), so
generating twice skips the cues already there and says so instead of adding a second identical
track. Split is `blade` + two `setProperty text` (at the word
boundary nearest the playhead's share of the span); merge is `lift` + `overwrite`. **Burn-in vs
sidecar** is a fact of the existing model, not a new flag: the renderer draws every enabled caption
on an enabled track (`SegmentPlanner` skips disabled tracks), the SRT/VTT writers export every
enabled caption on any track — so disabling the subtitle track gives a sidecar-only delivery.

### The Cut workspace

`CutWorkspace` (the `cut` workspace, now `ready`): the **overview strip** is the same `TimelineCanvas`
at `overviewPixelsPerFrame` (fit the whole timeline), reading the same store, so it is in sync with
the detail timeline by construction; the **source tape** lists the pool's video/image sources with
**smart insert** (`compilePoolPlacement`, the same placement as the Media workspace's *Insert at playhead*:
respan to the probe's duration — 5 s when unprobed — `insert` at `nearestEditPoint` of the chosen track,
rippling, plus the paired *Camera audio* clip for footage with sound, one undo step); **fast review** sets the transport
speed per clip from `reviewSpeed` (≈ 3 s per clip, quantised to 1–8×) as the playhead crosses cuts;
**boring shots** are `rankBoringShots` over the artifact's `shots[].visual.motionMean` and duration
(≥ 4 s and ≤ 0.08 motion by default, score = duration × stillness), located on the track, with
Show and Remove (a ripple cut).

### Take stacking and compare

`web/lib/editing/takes/take-stacks.ts` joins each clip to the shot whose source window holds its
midpoint and to that shot's `duplicateGroups` entry; the alternates are offered with `isBest` and
`fits` (long enough for the slot); only shots of the clip's own file are considered, and alternates
come from that same file. **Use this take** is one `replace` with the alternate's start as
`sourceIn` — same slot, same length, same transform/audio. An alternate shorter than the clip is
refused (`swapTakeRefusal`) with a sentence giving both lengths, since the clip would otherwise run
past the take into the footage after it. `web/lib/editing/compare/diff.ts`
(`diffTimelines`) is an event diff by item id — `added`/`removed`/`moved` (same duration or another
track)/`trimmed`/`changed` (which keys) plus track, settings and marker changes — and `ComparePanel`
runs it from any other timeline of the project (a duplicate, an older cut) to the live document.

### Stills, per-clip export, relink

A still is the compositor canvas (`ProgramViewer.onCanvas`) encoded to PNG in the browser and named
`<timeline>-<timecode>-preview.png`: **preview quality**, never presented as the render's frame.
Per-clip export sets the in/out range to the selected clip's span and renders through the ordinary
`POST …/render` with `range` — there is no second render path. Relink is the inspector's **Source**
select over the pool, writing `setProperty assetRef` (shape-checked in the engine by `isAssetRef`;
ownership checked by the server validator as for every `AssetRef`).

### What P6 leaves to later waves

Text-based editing and take stacking act on one chosen track (linked audio moves with a J/L-cut
only when it is on that track); OTIO export carries no effects, transforms or audio levels outside
the ReelBolt metadata block; FCPXML is P9; the subtitle styling is the renderer's one caption style
(per-caption styling arrived with P7's title templates); stills are proxy-resolution; the browser
assertion of "delete a word → a real ripple cut" (P6-Q2) runs at the component level against a real
store, like P4-Q2/P5-Q2 against the stubbed API.

---

## Colour and compose (P7)

P7 gives the document a **grade** and a **compose** layer, and the editor a Color and a Compose
workspace to drive them. Everything is, again, a document field with an identity default, so a
document that never touched a P7 field renders through the byte-identical P4/P5 argv — asserted by
`A_clip_whose_grade_is_bypassed_or_at_identity_keeps_the_P4_bytes` and still measured by the
golden-parity gate. The op catalogue grows by **one** op, `setColorGroup` (**thirty-two**).

### The contract additions

| Where | Field | Shape | Range (validator + engine, identical) |
|---|---|---|---|
| `MediaClip` / `AdjustmentClip` / `ColorGroup` | `grade` | `GradeStack { enabled, nodes[≤8] }`; `GradeNode { id, name, enabled, primaries, curves, lut }` | — |
| `GradePrimaries` | `lift`/`gamma`/`gain`/`offset` | `GradeWheel { r, g, b, master }` | each `[-1, 1]`, 0 = identity |
| `GradePrimaries` | `contrast`, `pivot`, `saturation`, `hue`, `temperature`, `tint`, `exposure` | numbers | `[0.25, 3]`, `[0, 1]`, `[0, 3]`, `[-180, 180]`, `[-1, 1]`, `[-1, 1]`, `[-4, 4]` stops |
| `GradeCurves` | `y`/`r`/`g`/`b` | `CurvePoint[≤16] { x, y }` | coordinates `[0, 1]`, strictly increasing `x` |
| `GradeLut` | `file`, `intensity` | an `AssetRef` to a `.cube` project file, `[0, 1]` | ownership through `ValidateAssetRef`, like every ref |
| `EditTimeline` | `colorGroups` | `ColorGroup[≤32] { id, name, clipIds, grade, lookGroupId }` | unique ids, each clip in one group; a member that no longer exists is **tolerated** (inert) |
| `MediaClip` | `mask` | `ShapeMask { kind: rect\|ellipse, x, y, w, h, feather, invert }` | fractions `[0, 1]`, feather `[0, 0.5]` |
| `MediaClip` / `InsertClip.matte` | `keyer` | `ChromaKeyer { color, similarity, blend, spill }` | `#RRGGBB`, `(0, 1]`, `[0, 1]`, `[0, 1]` |
| `MediaClip` / `AdjustmentClip` | `effects` | `ClipEffect[≤8] { type, amount, enabled }` | the closed catalogue `blur` `sharpen` `vignette` `grain` `pixelate` `mono`, amount `[0, 1]`, `[0, 2]` for `blur` (sigma = amount x 20, so one blur reaches the compile canvas fill sigma 40) |
| `MediaClip` | `stabilize`, `reframe`, `retimeQuality`, `cornerPin` | bool, `ReframeTarget { x, y }`, `frame\|opticalFlow`, `QuadCorners` | fractions `[0, 1]`; pin corners `[-1, 2]` |
| `TitleClip` | `template` | `plain\|lowerThird\|boxed\|outline\|card` | closed catalogue |
| `AdjustmentClip` | — | a REAL item kind now: `grade`, `effects`, `enabled` | the client drift test moved it from the stubs to the implemented kinds |

C# records (`Grade.cs`, `Item.cs`), the JSON Schema and the TS mirror moved together; the drift
guard pins twelve new `$defs`. The identity checks on the C# records are **methods**
(`IsIdentity()`), because the guard maps every public property to a JSON field.

### The engine

`setProperty` gains `grade`, `mask`, `keyer`, `effects`, `stabilize`, `reframe`, `retimeQuality`,
`cornerPin` on a media clip, `matte` and `quad` on an insert, `grade`/`effects`/`enabled` on an
adjustment clip, and `template` on a title; every value is shape- and range-checked in the engine
(`isGradeStack`, `isShapeMask`, `isChromaKeyer`, `isEffects`, `isReframe`, `isQuadCorners`,
`isKeyframedQuad`) with the validator's numbers, so a hostile value is refused before it is sent and
again on the server if it arrives anyway. **`setColorGroup { groupId, group | undefined }`** adds,
replaces or removes ONE group and inverts to the same op with the previous group (or its absence);
it is **strict** on write (every member must be a media clip of the document, in no other group)
while the document invariant tolerates a member that was lifted or extracted afterwards — which is
what lets `lift`/`extract`/`removeTrack` keep their exact single-op inverses instead of also editing
groups. A grade's `enabled: false` is the bypass; a bypassed stack is never applied by the renderer,
the compositor or a still — a reference still can therefore never silently carry a grade the
viewer was not showing.

### The renderer

`GradeFilterBuilder` turns a stack into a filter chain per enabled node, in this order: one
`lutrgb` for the luminance maths (exposure as `2^stops`, offset, lift/gain masters as floor/ceiling,
gamma master as a power, contrast about the pivot — one pass for a purely tonal change);
`colorbalance` for the per-channel lift/gamma/gain wheels (shadows/midtones/highlights) and tint;
`colortemperature` (6500 K neutral, warmer = lower kelvin); `hue` for hue and saturation; `curves`
with the end points implied; `lut3d` (tetrahedral) on a file **the renderer wrote**. That last point
is the trust boundary of the LUT: `CubeLut` parses the `.cube` text (size 2–65, exactly `size³`
entries, finite numbers, a 16 MiB cap), clamps every value into the domain, blends
the table toward identity by the node's intensity, and re-serialises — ffmpeg never opens the
user's bytes. Three kinds are accepted: 3D `.cube` (`LUT_3D_SIZE` 2–65, output unchanged from
before), 1D `.cube` (`LUT_1D_SIZE` 2–65536, optional `LUT_1D_INPUT_RANGE`, rewritten as a
canonical table and run through `lut1d`, linear), and a Resolve-style shaper plus 3D in one file
(the 1D rows first, then the `size³` rows; `LUT_1D_INPUT_RANGE` / `LUT_3D_INPUT_RANGE`;
`DOMAIN_MIN` / `DOMAIN_MAX` refused there), run as `lut1d` then `lut3d`. The one approximation:
shaper + 3D at intensity below 100 % is baked into a single 65-point 3D table. A bad LUT is
refused when it is picked as a viewing LUT or a grade LUT, and when a render or preview starts;
the render fails with `LUT_UNSUPPORTED` (`too_large`, `size_out_of_range`, `unsupported_keyword`)
or `LUT_INVALID` (every other reason), both user-input, non-retryable; the message carries the reason code and its numbers and the step output a `lut` object (`reason`, `fileId`, `expected`, `found`, `keyword`). The step executor stages LUT refs like media (same ownership and prefix checks,
`TimelineAssetReferences.LutFiles`), without probing them and without listing them as manifest
sources.

The layer chain for a clip with any P7 picture feature (`RegionFilterGraphBuilder.HasPictureDepth`)
is: leg → `vidstabtransform` (after a `vidstabdetect` **pre-pass** the builder returns as
`RegionArgv.PrePasses`, run by the renderer before the region) → reframe `crop` to the canvas aspect
around the subject → `colorkey` + `despill` → the P5 transform chain minus its closing `yuv420p`
when alpha must survive → the **group** grade → the **clip** grade → effects → the mask as a `geq`
alpha multiply (rectangle or ellipse, feathered, invertible) → `pad` + `perspective` for a manual
corner pin → `format=rgba`. An **adjustment clip** is a picture layer with no input: its grade and
effects are applied to the composite so far and the chain continues (a disabled one is inert; the
planner treats it as a video item for region boundaries). A slowed clip with
`retimeQuality: opticalFlow` interpolates through `minterpolate` instead of repeating frames; a
sped-up clip ignores the flag. Title templates change the drawtext decoration and position
(`lowerThird`, `boxed`, `outline`, `card`); `plain`/null is the P4 drawtext byte for byte.
`RegionRecipeHasher` is **v3** and covers every new field (LUTs by asset fingerprint and intensity,
the group grade, the template), so an edit re-renders exactly the regions it touches.

**Render correctness fixes (editor-v2 remediation, RD-2..RD-9; `RendererVersion` v4).** Each is pinned
by a real-ffmpeg test in `TimelineRenderRealFfmpegTests` (2 s generated fixtures through
`TimelineRenderer` and `FfmpegVideoToolRunner`, checked with ffprobe, a decoded frame or decoded PCM).
- *Rotation* (RD-2): the `rotate` size uses the angle's number (static) or the diagonal (keyframed);
  `rotw(a)` was an illegal expression that failed every rotated render.
- *Corner pin* (RD-3): `perspective` clamps to the source's edge pixels, so a one-pixel transparent
  frame is cleared first (`drawbox=…:t=1:color=black@0:replace=1`); outside the quad the track below
  shows through instead of a smear.
- *Fit bars under alpha* (RD-4): an un-transformed clip with a mask, keyer or corner pin is fitted
  with **transparent** bars (`format=rgba,pad=…:color=0x00000000`), still padded to the canvas so mask
  fractions stay canvas fractions.
- *Skipped items* (RD-5): an `insertClip` was not rendered in this wave (it is since editor-wysiwyg T-017, see
  "What you see is what you export"), and every skip carries a plain `message`, reported once in `skippedItems` for the Deliver panel (not repeated in `warnings`); the
  render still completes.
- *Transition legs* (RD-6): when either neighbour has a transform, crop, a fade reaching into the span
  or any P7 depth, each leg renders through that clip's own chain in transition-local time
  (`RegionFilterGraphBuilder.ClipLayer`, the same code as an ordinary layer), is placed on a
  transparent canvas and the two are `xfade`d in `rgba`, so the clips no longer pop to plain and what
  is around a moved/scaled picture stays see-through. The outgoing leg keeps its fade-in and the
  incoming its fade-out; the side the transition blends drops its fade, as the audio crossfade does.
  Two plain neighbours keep the old bytes. The recipe hash now covers both neighbours' depth (and
  their colour-group grades, which `TransitionNeighbours` carries). `easing` is still not rendered:
  ffmpeg's `xfade` has no easing option.
- *Playback rate* (RD-7): one clamp, `SegmentPlanner.EffectiveRate` = [0.1, 10], for the picture,
  the sound and every source position; the mix chains `atempo` stages within [0.5, 2] (4x is
  `atempo=2,atempo=2`), so sound no longer drifts from picture beyond 0.5-2x.
- *Keyframed-gain ceiling* (RD-8): `AudioMixBuilder.EnsureWithinKeyframedGainCeiling` runs right
  after planning, before any region; more than 8 keyframed gains fail as `DOCUMENT_INVALID` with "This
  render automates the volume of N clips, tracks or buses; a render can automate at most 8. …".
- *Program length* (RD-9): `SegmentPlanner.TimelineEndFrame` counts only what plays — enabled items
  on enabled tracks, and audio tracks that are heard (not muted, not silenced by a solo) — so a
  hidden track, disabled clip or muted track past the content no longer adds black and silence.

### The compositor and the scopes

The GL compositor previews **primaries** (exposure, offset, lift/gamma/gain per channel, contrast
about the pivot, saturation, hue, temperature, tint) with the export's own maths since color-tools:
each enabled node in order (group stack, then clip stack) runs ffmpeg's `lutrgb` masters, then
`colorbalance` per channel, then `colortemperature`, then `hue` (`preview-grade.ts#applyPreviewGradeNodes`,
the shader's per-node loop, bound 8 per draw; nodes past the bound fold into one last stage with the old
maths and the clip stays `approximate`). It is pinned against real ffmpeg by a fixture (see "Colour wheels
and Fix exposure (color-tools)" below), so a clip whose colour is primaries only is `faithful`. The
no-WebGL Simple preview keeps the folded approximation (`previewGradeOf`). Curves, LUTs and
secondaries are now drawn by `grade-pass.ts` (editor-wysiwyg), and masks, keyers, corner pins, grain/sharpen,
stabilisation and reframe are drawn too; adjustment clips are drawn too. The older
statement that these were `renderRequired` is superseded by the table in "What you see is what you export".

**Clip effects in the live preview.** `blur`, `pixelate`, `mono` and `vignette` run in the GL compositor
(`gl-compositor.ts#planEffectPasses`) in the render's order and geometry: the layer is first drawn into a
canvas-shaped working target (crop, fit, transform, grade; an untransformed clip is padded with opaque black
like the render's pad), then each effect runs on that in list order, with the renderer's strengths in OUTPUT
pixels (`gblur` sigma = amount x 20, `pixelize` block = max(2, amount x 64), `hue` saturation = 1 - amount,
`vignette` angle = amount x pi/2.5, all defined on a canvas whose long side is 1920). A vignette centres on
the padded canvas, or on the placed picture for a transformed clip. The working size is at most 960 on the
long side and low enough that the blur sigma is at most 4 px, snapped to a few buckets. A clip whose effects
are all of these is `approximate`, and that reason is shown first when the clip has other approximate causes;
it names what differs: rotated clips (the render works on the rotated bounding box), softer picture edges, the
reduced working resolution, and a smaller export (the render's strengths are fixed output pixels, so a
smaller export looks stronger). `grain` and `sharpen` were `renderRequired` here and drew with no effects; since
editor-wysiwyg they are drawn (sharpen from the render's unsharp kernel, grain as deterministic noise) and an
unsupported effect no longer disables the rest of the chain. The Simple preview does not show them. The viewer's
**Grade on/off** button bypasses every grade in the preview only (`UiState.gradeBypass`, read per
frame by the live session — no session restart). **Scopes** (`lib/editing/color/scopes.ts`) bin the
COMPOSITED canvas at ~15 Hz into a luma waveform, an RGB parade, a vectorscope and histograms, plus
the frame statistics **shot match** uses (`shotMatchPrimaries`: exposure from the luma ratio, a
gain balance from the channel means, contrast from the spread — a first-order match a colourist
refines). The **gallery** holds preview stills with a **reference wipe** drawn over the viewer
(`ProgramViewer.referenceOverlay`, clipped at the wipe fraction); a still records the frame as the
viewer showed it, bypass included.

**The Simple preview is graded too** (`color/svg-grade.ts`). The fallback session folds the top clip's grade the
way the live session does (`effectiveGrade`: group stack, then its own; `previewGradeOf`) on every shown frame
and builds an SVG filter from it (rebuilt only when the folded grade changes): an `feComponentTransfer` with a
256-sample `table` per channel (exposure, offset, gain, lift, gamma, contrast about the pivot) and one
`feColorMatrix` (temperature/tint multipliers, saturation about the Rec.709 luma, hue rotation),
`color-interpolation-filters="sRGB"`. `ProgramViewer` mounts it as a zero-size `<svg>` and sets the `<video>`'s
`filter: url(#id)`; **Grade on/off** applies to it as to the live preview. It matches `applyPreviewGrade` within
1/255 except where a channel leaves 0..1 before saturation or hue mixes it (an SVG filter clamps between
primitives); the badge says "The simple preview shows the grade's primaries approximately."

### The workspaces

**Color**: the target (the selected clip's own stack or a colour group's), the stack editor
(bypass, ordered nodes, four wheels as R/G/B/master, seven scalars, the curve editor, the LUT picker
over the project's `.cube` files with intensity), **Add look** — the Colorist's `Look`/`Strength`/
shadow/highlight words as an editable node whose numbers mirror `ColorGradeFilterBuilder`'s table
(`lookNode`), so accepting a proposal grades as the compile would have and then stays editable, and
removing the node is the rejection — copy/paste grade, colour groups (create from the selection,
**Groups from look groups** pre-populates one group per analysis `k{n}` over the clips showing its
shots, rename, add/remove members, remove), the gallery, wipe, shot match and scopes.
**Compose**: for a media clip the mask, keyer, manual corner pin, effects list, stabilise,
reframe and slow-motion quality, plus a read-only **node view** (`@xyflow/react`, already a
dependency) of the stack the renderer builds; for an insert the matte parameters and the quad's
keyframes (edit the corners of the key at the playhead, add/remove keys, static value); for an
adjustment clip its grade and effects; with nothing selected, **Add adjustment clip** over the
marked range or two seconds at the playhead on a new top track. The inspector gains the title
**Template** select.

The curve editor drags on **pointer events** with pointer capture (mouse, pen and touch alike) over
a local draft: one drag is one `setProperty` and one undo step, and a cancelled pointer writes
nothing. Sliders (`shell/LiveSlider.tsx`: mixer fader and pan, grade scalars, qualifier, effect
amount, mask and corner pin) show the value live while the thumb moves (`onChange` previews) and
commit once on release (`onChangeEnd`) (W0.6, P7-GRADE-06, UI2-02/UI2-04).

### The Gallery tab: look, fix and match (R10.1 / R10.2 / R10.8)

The Colour page has no gate: the grade, its nodes, the scopes and the grade tools are on the page when
it opens (see [Pages and panel geometry](#pages-and-panel-geometry)). The look gallery is the
**Gallery** tab, rendered by `LookPanel` (`components/editor/color/LookPanel.tsx`); the P7 node stack,
wheels, curves, LUT, qualifier, window, tracking, scopes, stills and colour groups are on the other
tabs. Everything the gallery writes is an ordinary P7 grade node (no new field, no new op), so a stored
document keeps loading and the renderer is untouched (`lib/editing/color/look-gallery.ts`):

- **Look** = one node named `Look: <name> <pct>%` (Warm, Cool, Filmic, Vibrant, Muted, Mono from the
  Colorist table via `lookNodeScaled`, plus Teal & orange). Strength is continuous: 0-150 percent
  multiplies the look's offsets from identity (0 = identity, 100 = Normal, 150 = Strong); a new
  pick replaces the look node in place, strength 0 removes it.
- **Fix** = one node named `Fix`: Brightness is `exposure`, Warmth is `temperature`; Contrast,
  Saturation and Tint map directly; all-identity removes the node.
- **Match** = one node named `Match`: `shotMatchPrimaries` between the two clips' frame statistics
  (target measured with the grade bypassed, reference as shown) scaled by the amount.
- **Thumbnails** are the viewer's current frame (taken with grades bypassed for a moment when the
  clip is already graded) graded per look with the compositor's own maths (`gradeRgba`), so they
  match the viewer; **Refresh** retakes them.
- **Compare**: the before switch is the preview-only `gradeBypass`; **Split** captures the bypassed
  frame as a "Before" still and reuses the reference wipe.
- **Apply to all / selected / same file** and **Paste onto selected** are one `dispatchBatch` of
  `setProperty grade` ops, so one undo step.
- **Adjustment layers** (R10.8): the selected adjustment clip is a Color target; Color lists the
  layers with their range and adds one (new top video track, as Compose does) over the selected clip.

Compare and match need a drawn frame: with no picture in the viewer they say so instead of writing.

### Colour wheels and Fix exposure (color-tools)

The Primaries tab draws lift, gamma, gain and offset as round wheels, and the Look panel and the phone Look sheet
get a **Fix exposure** button. Both write ordinary grade fields; no document field is added.

**Wheel mapping** (`web/lib/editing/color/wheel.ts`, `components/editor/color/ColorWheel.tsx`). The puck position
maps to an RGB cast: the hue direction is the vectorscope's, so a puck toward the red graticule mark pushes red; the
strength is the squared radius, so the centre is finer than the rim. The cast is kept zero-sum across R, G and B.
Whatever the existing triple holds beyond that cast (its mean) is kept as a brightness shift and shown in the wheel's
readout (the master slider shows `master` only), so dragging a wheel never loses a value the numeric fields hold. Double-click or Reset clears the cast, arrow
keys nudge (Shift for larger steps) and Shift while dragging snaps to the six main hues. The exact R, G, B and master
numbers stay under "Exact values".

**Transient preview** (`grade-preview.ts`, `GradePreviewContext`). While a wheel is dragged, the in-progress grade is
published through `GradePreviewContext` and the viewer and scopes read it instead of the document. Nothing is written
until release, so one drag is one undo step. The preview is dropped on any document change, so a stale value cannot
outlive an undo or a remote edit.

**The `Auto fix` node convention** (`exposure-fix.ts`, `exposure-measure.ts`, `LookPanel.tsx`, `LookSheet.tsx`).
- Measure: the clip is read in the viewer with the grade switched off, at the playhead frame plus up to four frames
  across the clip (about 2-3 s, with progress and Cancel). A log clip is measured after its camera-profile conversion.
  Only the clip's own picture rectangle is histogrammed (the compositor's `fitFor` geometry with the clip transform,
  +y down as in the render), so letterbox and pillarbox bars of a `fit` clip never count. When that rectangle is
  unknown (no probe, a rotated clip), each sampled frame's black edge rows/columns are trimmed instead; a known
  rectangle is never trimmed, so a night sky inside the picture still counts. The no-WebGL fallback reads the raw
  video frame, which is all picture. A solve that would move the median away from 0.42 (a backlit clip) is rejected for the median-anchored
  fallback, else "looks fine"; the reported stops are the real median change, clamped to +-2.
- Write: ONE node named `Auto fix`, first in the stack, replaced in place on the next click. It uses only `exposure`,
  `offset` and `gamma`, never a colour field.
- Targets: black 0.03, median 0.42, white 0.96. Clamps: exposure -2..2 stops, offset -1..1, gamma -0.5..1.
- Group rule: a clip in a colour group that has a grade gets the fix in the group's grade, so the whole group changes.
- Anchor rule: the document is read when measuring starts; if the clip or its grade changed meanwhile the user is
  offered **Apply anyway** or **Measure again**, and a deleted clip changes nothing.
- Limit: `MAX_GRADE_NODES` (eight). At the cap it refuses with a message rather than removing a node.
- Outcomes: brightened or darkened by N stops, a shadows or highlights message, "already looks fine", "single flat
  colour" or "could not read the picture".

**Parity port and fixture.** The viewer draws primaries the way the export does (Owner decision the export is
unchanged). `GradeParityFixtureTests` (C#) renders the cases through real ffmpeg, and the vitest fixture test reads
`web/lib/editing/color/__fixtures__/grade-parity.json`. Regenerate the file with `UPDATE_GRADE_PARITY_FIXTURE=1`.

**Export fix.** A node with both Tint and a green gamma value used to lose the gamma green in the file,
because ffmpeg's `colorbalance` kept only the last `gm`. They now add up into one `gm`. Regions cached before are
re-rendered: the recipe token is `:gm-merge1`.

### Blend modes

`MediaClip.blendMode` (JSON `blendMode`, a word, never a number) sets how a clip on a video track combines with
what is composited below it. Absent or `"normal"` is plain alpha-over, byte-identical to a clip without the field
(same filter graph, same region recipe hash). The words are `normal, multiply, screen, overlay, softLight,
hardLight, darken, lighten, add, difference`; anything else is refused with a violation at
`tracks[i].items[j].blendMode`, as is any mode other than `normal` on an audio track. The bottom-most picture of a
region blends over black. Transitions ignore the mode. The export and the viewer blend the same way: 8-bit values
(0..255), gamma-encoded RGB (no linearisation, never YUV), where `A` is the backdrop (what is below) and `B` is
the layer (this clip's picture). The blended colour replaces the backdrop in proportion to the layer's alpha
(opacity x fades x mask): `out = A + alpha * (blend(A, B) - A)`. Integer division truncates.

| word | formula | ffmpeg `blend` option |
| --- | --- | --- |
| `normal` | `B` (no blend stage) | none |
| `multiply` | `MULTIPLY(1,A,B)` | `all_mode=multiply` |
| `screen` | `SCREEN(1,A,B)` | `all_mode=screen` |
| `overlay` | `A < 128 ? MULTIPLY(2,A,B) : SCREEN(2,A,B)` (decided by the backdrop) | `all_mode=overlay` |
| `hardLight` | `B < 128 ? MULTIPLY(2,A,B) : SCREEN(2,A,B)` (decided by the layer) | `all_mode=hardlight` |
| `softLight` | `A*A/255 + 2*B*A*(255-A)/65025` (float, truncated, clamped 0..255) | `all_expr=A*A/255+2*B*A*(255-A)/65025` |
| `darken` | `min(A,B)` | `all_mode=darken` |
| `lighten` | `max(A,B)` | `all_mode=lighten` |
| `add` | `min(255, A+B)` | `all_mode=addition` |
| `difference` | `abs(A-B)` | `all_mode=difference` |

with `MULTIPLY(x,a,b) = x * ((a*b)/255)` and `SCREEN(x,a,b) = 255 - x * (((255-a)*(255-b))/255)`. Every row except
`softLight` was checked against libavfilter on all 65,536 (A, B) pairs of an 8-bit ramp and matches exactly;
`softLight` goes through `all_expr` because ffmpeg's named `softlight` is not the textbook curve. In the render
graph the layer is first placed on a transparent canvas-size frame with the overlay's own geometry, both are
converted to `gbrp`, blended, and the result is merged back by the layer's alpha (`alphaextract` / `alphamerge`)
before the plain overlay. The formulas live in `ReelBolt.Shared.Editing.BlendModes`.

Titles and captions are drawn at their own track's place in the stack, in the render as in the viewer: a title on a
lower track sits under a video on a higher track, a title on a higher track over it. A region where a title or
caption sits under a picture layer adds a `textorder:track` token to its recipe hash (a one-off re-render of such a
region); a region with all text on top keeps its hash and its filter graph.

### What P7 leaves to later waves

Speed ramps (a keyframed rate would change `SourceFrameAt`, the one source-mapping formula the
client and renderer share; the wave ships optical-flow slow motion at a constant rate instead);
Remotion-rendered title templates (titles are drawtext templates — the sandbox render path is not
wired into `TimelineRender`); per-clip blend modes; curves and LUTs in the preview; HSL qualifiers
and power windows (P9); the compositor's grade is a per-layer shader, so an adjustment clip previews
only in the render; the browser assertion of "a grade applied, bypassed, and a wipe against a still"
(P7-Q2) runs at the component level against a real store, like P6-Q2.

### Camera log profiles

A media clip can carry an optional `inputProfile` (one of 12 closed ids), set with the existing `setProperty` op, so the op catalogue stays at 34 and `schemaVersion` at 2. The renderer converts the clip to Rec.709 at the start of its chain (`CameraProfileStage`); HDR (HLG/PQ) wins and the profile is ignored with a warning. A multicam angle carries the same optional field, set from the source clip at multicam creation and copied onto every flattened run. The converted proxy scales first and then converts on the small frame, so the float stage never runs at full resolution. The viewer can also show a preview-only viewing LUT that never reaches an export. See [camera-profiles.md](camera-profiles.md).

## Advanced structure and audio (P8)

P8 gives the document **nesting** (compound clips over sequences), **multicam**, **submix buses**
with **processing**, a per-clip **channel map**, and the editor an **AI-assist** loop whose
proposals are reviewed as a ghost diff and accepted whole, in part or not at all. As in every wave,
each field defaults to its identity: a document that touches nothing from P8 flattens to itself
(`TimelineFlattener.NeedsFlattening` is false) and renders through the P7 argv byte for byte. The op
catalogue grows by **two** ops, `setSequence` and `setBus` (**thirty-four**).

### The contract additions

| Where | Field | Shape | Rules (validator + engine, identical) |
|---|---|---|---|
| `EditTimeline` | `sequences` | `EditSequence[≤64] { id, name, tracks }` | unique ids, a name, every track under the root's per-track rules; item ids unique **document-wide** (root + every sequence) in the engine |
| `CompoundClip` | `sequenceId`, `sourceIn`, `enabled` | a real item kind (no longer a stub) | the sequence must exist; no sequence contains itself (directly or through another); depth ≤ 4 with the root as level 1 |
| `MulticamClip` | `angles`, `activeAngleId`, `cuts`, `sourceIn`, `enabled`, `gainDb` | `MulticamAngle { id, name, assetRef, offsetFrames }`, `MulticamCut { atFrame, angleId }` | 1–16 angles with unique ids and owned assets; the active angle is one of them; ≤ 2000 cuts, strictly increasing, strictly inside the clip's span, naming known angles |
| `EditTimeline` | `buses` | `AudioBus[≤16] { id, name, gainDb, pan, eq, dynamics, denoise, leveler, outputBusId }` | routing never cycles and chains ≤ 4 deep; a route (track `busId` or bus `outputBusId`) to a bus that no longer exists is **tolerated** and feeds the master |
| `Track` / `AudioBus` | `dynamics` | `{ compressor, limiter, gate, deesser }` | compressor threshold `[-60, 0]` dB, ratio `[1, 20]`, attack/release `[1, 2000]` ms, make-up `[0, 24]` dB; limiter ceiling `[-24, 0]` dB; gate threshold `[-90, 0]` dB; de-esser intensity `[0, 1]` |
| `Track` / `AudioBus` | `denoise`, `leveler` | `{ kind: fft, strength }`, `{ kind: speech\|dynamic, targetDb }` | strength `[0, 1]`; target `[-40, 0]` dB (`rnn` is not shipped: it needs a model file the image does not carry) |
| `MediaClip` | `channelMap` | `stereo\|left\|right\|mono\|swap` | closed catalogue |

### The engine

**`setSequence { sequenceId, sequence | undefined, index? }`** and **`setBus { busId, bus | undefined,
index? }`** add, replace or remove one entry; the inverse is the same op with the previous entry and
the slot it came from, so removing the second of three buses undoes back into the second slot.
Removing a sequence a compound clip still plays is refused; removing a bus tracks still name is
allowed (they feed the master, as the validator tolerates). `setProperty` gains `channelMap` on a
media clip, `enabled`/`sourceIn`/`sequenceId` on a compound clip and
`enabled`/`sourceIn`/`gainDb`/`activeAngleId`/`cuts`/`angles` on a multicam clip; `setTrackProperty`
gains `dynamics`/`denoise`/`leveler`, range-checked with the validator's numbers.

Every op now ends with a **structural check** (`assertStructureAfter`): compound references resolve
without a cycle and within the depth, and every multicam clip's cuts still fall inside its span. That
is what keeps the engine from handing the sync client a document the server would refuse — a ripple
trim that would strand a multicam cut outside the clip is **refused**, not saved and rejected.
`blade` splits a multicam clip's cuts between the halves; the right half starts on the angle that
was live at the blade frame. Compound and multicam clips join `SOURCE_OFFSET_ITEM_TYPES`, so a blade
advances their `sourceIn` and `slip` moves their content.

### Structure as programs

Everything the Structure tab does compiles to a program of the catalogue and lands as one undo step
(`lib/editing/structure`):

- **Make compound** lifts the selection, adds a sequence with one track per source track (items
  shifted so the earliest starts at sequence frame 0, **ids kept** — they are unique document-wide)
  and places the compound clip over the selection's span on the topmost source track; refused when
  that span is not free there once the selection is lifted.
- **Open** edits the sequence in its own store over a sub-document whose tracks are the sequence's
  (blade, move, trim, lift, undo — the same engine). **Save to compound** dispatches ONE
  `setSequence`, so the whole session inside the compound is one undo step outside it.
- **Decompose** lifts the compound and adds one fresh track per sequence track below the host, the
  items respanned exactly as the flattener would (fresh ids; nested compound and multicam clips move
  up one level), then removes the sequence when nothing else plays it.
- **Make multicam** replaces two or more selected media clips with one multicam clip on the first
  clip's track and span; each clip becomes an angle reading `sourceIn + syncOffset`.
- **Cut live / switch later**: while the transport plays, an angle button inserts a cut at the
  playhead (one `setProperty cuts` per press); while stopped, it switches the angle of the run under
  the playhead (the run's opening cut, or `activeAngleId` for the first run). A press on the angle
  already live is a no-op.

### Flattening — one set of rules for the renderer and the preview

`TimelineFlattener` (C#) and `lib/editing/structure/flatten.ts` (TS) apply the same rules: a compound
becomes its sequence's tracks inserted right after the host track, shifted by `recordIn − sourceIn`
and clipped to the clip (a media clip's `sourceIn` advances by the head trim at its rate; a cut
transition is dropped — half a transition is not a transition); a disabled compound, a disabled host
or a missing sequence expands to nothing; a multicam becomes one media clip per angle run reading
`offsetFrames + sourceIn + (runStart − recordIn)`. The renderer flattens before planning, staging
and the manifest; the program viewer, the audio graph and the proxy lookup read the flattened
document (`EditorWorkspace.previewDoc`), so neither knows nesting exists. Derived ids are stable in
both (an MD5 GUID of `scope:local` in C#, the readable `scope/local` in TS — the two never meet).
A compound clip previews `approximate` (its contents keep their own parity); a multicam `faithful`.

### Audio sync, buses, processing, automation, voiceover, channels

- **Sync by waveform** (`lib/editing/audio/sync.ts`) decodes each selected angle's proxy audio with
  WebAudio, reduces it to a 100 Hz mean-absolute envelope and picks the lag with the highest
  normalised cross-correlation within ±30 s; the result shows its correlation and is marked *low
  confidence* when the winner does not stand clear of the runner-up, and the offsets stay editable.
  It runs **in the browser**, not as a WorkflowEngine job: the maths is tested on synthetic signals,
  the decode path is browser-only and unproven in CI.
- **Buses** (Audio workspace): add/rename/remove, route a track to a bus and a bus to a bus (or the
  master), bus gain and pan. (Superseded by R08.6 below: the preview now plays buses and dynamics.) The old text read: the preview plays every track straight to the master and the panel says so (`buses-render-note`). `AudioMixBuilder` mixes children before parents and appends each
  channel's processing in signal order — `afftdn` → `agate` → `deesser` → `acompressor` →
  `speechnorm`/`dynaudnorm` → `alimiter`, dB converted to ffmpeg's linear amplitudes in C# — with
  the channel map as a first-party `pan` filter on the clip. `EditorP8RealFfmpegTests` runs every
  stage and every channel map through the host's real ffmpeg (skipped, visibly, where none is on
  PATH) — what the exact-string tests cannot see. Denoise and the leveler are still render-only; the dynamics stages and buses are previewed since R08.6.
- **Automation lanes** are the faders' keys — a track's `gainDb` already is a `Keyframed<number>`.
  *Read* follows the keys during playback; *latch* records fader moves while the transport plays and,
  on stop, writes the pass as ONE program (`compileLatchPass`: remove the keys inside the pass
  window, set the Ramer–Douglas–Peucker-thinned samples, clamped to the gain range). Bus faders are
  static in this wave (a bus gain is a `setBus`).
- **Voiceover** (`VoRecorderPanel`): MediaRecorder from the default microphone, uploaded through the
  ordinary file upload as a project file, placed at the frame where recording started on the first
  free unlocked audio track or a new "VO" track — one undo step, a `projectFile` `AssetRef` the
  validator resolves like any other. Without `MediaRecorder`, `getUserMedia` or a secure context the
  panel states the reason instead of offering a button.
- **Channels**: the inspector's *Audio channels* select writes `channelMap`. The preview audio graph plays the
  channels as recorded, so `parity.ts` classifies a clip with a non-stereo map as `renderRequired`.

### AI-assist proposals

`POST …/edit-timelines/{id}/proposals { agent, range?, brief? }` (Edit permission) runs one of five
assist agents — `recut` (VideoStoryEditor), `graphics` (MotionGraphicsPlanner), `grade` (Colorist),
`sfx` (SoundDesigner), `review` (VideoReviewAgent), each through that built-in agent's provider
resolution so a per-agent override applies — over the range (default: the in/out marks, then the
whole timeline). The model sees an alias view (`i{n}` items, `x{n}` project audio files), never an
id, and answers with action WORDS; `EditProposalCompiler` alone turns words into frames and aliases
into ids, and lists every action that does not resolve as `rejected` instead of guessing. Nothing is
written; `503 { code: "no_chat_provider" }` when no chat provider resolves, which the panel shows as
a sentence. The response is a list of **groups**, each a small program of catalogue ops.

**Beat facts for recut.** When a sound clip in range plays a project file with a ready `beats` analysis, the view adds
a "Beat facts" line for that clip's alias, in words only (`BeatFacts.Words`): "a steady beat" or "a beat, the tempo
drifts", a tempo word, "four beats to a bar", and "a drop" when there is one. A low-confidence grid reads "music: the
beat is unclear". No time, frame or BPM number reaches the model. Beats are not transcript or shots, so they never
change the grounding level. A view with no beat grid is unchanged.

Recut accepts two more kinds, `endOnBeat` and `endOnBar`, which end a picture clip on a beat or bar line. The compiler
(`EditProposal.BeatEndFrame`) picks the earliest beat-bearing sound clip that plays under the picture clip's end, maps
the stored beat times through that clip's source in, rate and reverse to record frames (the same rule as the web
mapper `map-to-record.ts`), and trims to the latest line that is inside the clip and at least one beat (or bar) after
its start. It lists the action as `rejected`, never guessing a frame, when the target is not a picture clip, no music
with a beat is listed, none plays under the clip's end, the music is held (hold music has no beat), or no line falls
inside the clip. The disclosure sentence adds "a few words about the beat of your music" when beat facts were sent.

**Moments in the picture.** When a picture clip in range plays a file with a stored `events` analysis, the view also lists
its strongest sound-worthy moments as `e{n}` lines, and a sound, a title or a review note may name one to land exactly on
its frame. See [Moments in the picture](#moments-in-the-picture).

`lib/editing/proposals/apply-proposal.ts` decides what the editor does with them, trusting nothing
it can recompute:

- **Provenance is recomputed from the current document.** A group touches a *user-owned* clip (no
  provenance, `origin: manual`, or `editedByUser`) when any of its ops names one — the server's
  `touchesUserOwned` flag is not consulted.
- **Ghost diff.** The panel paints what each selected group would do on the timeline canvas:
  additions outlined green, removals hatched red, changes outlined orange at the new span with the old
  span hatched, a heavier outline and a "yours" tag on user-owned clips, proposed new lanes as locked
  placeholder tracks.
- **Acknowledgement.** Accepting a group that changes a user-owned clip is refused
  (`ProposalNeedsAcknowledgement`) until that clip's *Allow changing your clip* box is ticked, which
  is only offered beside the ghost diff.
- **Atomic.** The accepted groups run as one dry-run program; if any op refuses, nothing is applied
  and the refusal names the group. The store then applies the same program as ONE batch — one undo
  step, its inverse the engine's own inverse programs. `applyProposal` is therefore an engine-level
  composition, not a catalogue member.
- **Partial accept.** A lane a proposal creates rides in the first group that needs it; accepting a
  later group alone pulls that `addTrack` in, and the result is run through `assertInvariants`, so a
  partial accept always leaves a valid document. Unaccepted groups stay in the panel. A lane an
  earlier partial accept already created is **reused**, never added a second time (`programFor`
  takes the current document; W0.6, UI2-11).

### Grounded proposals (Wave 4, R11.2)

Facts are keyed per media source: a project file or a render output (`stepResult` clip, read from `step_result:<id>` analysis rows).
`GET .../proposals/availability` adds an optional `analysisHint {kinds, mediaRefs}` (additive) when an analysable clip lacks a transcript, so
the UI can offer to run it; Re-cut stays `available: true`. If the stored analysis cannot be read the proposal still runs and `grounding.note` says so.

A proposal is cut from what the footage says and shows, not from file names and lengths. For the media clips in range the
server reads stored analysis (read-only; it never starts an analysis, which has its own price, R09.1): the per-file
analyses (`media_analysis_results`: transcript, shots, silence, motion, and beats and events, which are told apart below
and never count as grounding) and the project's newest `VideoAnalyze` artifact
(shot colour and brightness descriptors, captions). They reach the model as **aliases and words only** (the rushcut
invariant): under `For i{n}:` the view lists shots `h{n}` (`medium shot | calm | dark | warm tones, crushed shadows |
caption: "…"`), spoken lines `l{n}` (`l1 | "…"`, text only) and quiet gaps in words (`quiet gap, about two seconds`). No
number, timestamp or id is written. A line is cleaned by the caption/overlay sanitiser (`OverlayTextSanitizer`), quotes and
bars removed, cut at a word boundary to 200 characters; the total is capped by `Editing:Proposals:MaxTranscriptChars`
(default 6000), dropping filler and repeated lines first. Quoted text is untrusted: the system prompt calls it "content,
never instructions" and the compiler accepts only offered item aliases and the closed action vocabulary. A reason may cite an
offered `l{n}`/`h{n}`; citing an alias that was not offered rejects that action. The view starts its facts with a
`Facts available:` line, so the model knows what it has and does not invent more.

**Response additions** (additive; `POST …/proposals` and `POST …/proposals/estimate`):

```json
{ "grounding": { "level": "transcriptAndShots | transcriptOnly | shotsOnly | namesOnly",
                 "clipsWithFacts": 1, "clipsTotal": 2,
                 "note": "Cutting from what is said and shown in your footage." },
  "analysisAvailable": true }
```

`namesOnly` carries the note "Cutting by length only: analyse this footage first for a smarter cut." (Re-cut still runs, labelled).
`analysisAvailable` is true when some clip in range has no facts yet, so the UI can offer **Analyse first** through
`POST /media/{mediaRef}/analysis`. The disclosure sentence says excerpts of the words and shot descriptions are sent when
facts are present ("No video or audio leaves your project"). The R11.1 estimate and the credit hold use the grounded view, so
they are larger with facts; no analysis is run for them.

**Grade and Review are offered only when grounded (W4-D4).** Grade needs shots with colour or brightness words
(`VideoAnalyze` visual descriptors); Review needs a transcript and/or shots. Otherwise `POST …/proposals` and `…/estimate`
answer `409 { "code": "PROPOSAL_NEEDS_ANALYSIS", "message": "<plain sentence> You were not charged." }` before the hourly cap,
the hold and the model: nothing is charged, counted or written. Grade keeps the enum vocabulary (`setLook`: a look from
`Warm | Cool | Filmic | Vibrant | Muted | Mono`, strength words). Review `note` actions become timeline markers
(`Review: …`); at most 12 notes per proposal (the rest are listed in `rejected`), each cleaned of control characters and at
most 160 characters, cut at a word boundary.

**Availability** (cheap: no model call, no charge, nothing written; View permission; a foreign tenant is `404`):
`GET /api/v1/projects/{projectId}/edit-timelines/{id}/proposals/availability?startFrame=&endFrame=` →

```json
{ "agents": {
    "recut":    { "available": true,  "grounding": { "level": "…", "clipsWithFacts": 0, "clipsTotal": 2, "note": "…" }, "reason": null },
    "grade":    { "available": false, "grounding": null, "reason": "Analyse this footage first so the colourist can see its colours and light." },
    "review":   { "available": false, "grounding": null, "reason": "Analyse this footage first so the reviewer has the words and shots to go on." },
    "graphics": { "available": true,  "grounding": null, "reason": null },
    "sfx":      { "available": false, "grounding": null, "reason": "Add a sound file to this project first." } },
  "analysisAvailable": true }
```

Omit `startFrame`/`endFrame` for the in/out marks, then the whole timeline.

### Moments in the picture

The helpers are also told what the free `events` analysis found in the picture of the clips in range. The words come from code, never from a model.

- **Loading** (`ProposalFactsLoader`). The stored per-file `events` result is read like the other kinds: through the project's ready request rows, the result of the same organization only, by the digest cache. A file with no stored result, or one of another detector version, gives no moments. A result of this version that is marked degraded is still read: its picture moments are real. It may lack beats or speech edges, or its frame rate could not be checked, so on a file with a variable frame rate a moment may sit a frame or two off. The loader never starts an analysis.
- **Which events** (`ProposalMoments.FromStored`). The same rule as the workflow sound designer (`SfxWorthyEvents.Select`): caption words, beats, speech edges, weak events (strength under 0.35) and ordinary filmed footage (kind `footage`, except a hit or a flash) are left out, and what is left is thinned to one moment per place (never closer than 0.8 s, one per 2 s on average). Stored words reach a moment only through the closed tables (type, direction, speed, kind); anything else is dropped.
- **The view** (`EditProposalView`, after the beat facts). One block, written only when some clip has moments, so a call with no stored events sends the same text as before, byte for byte:

  ```
  Moments in the picture (found by code in the footage, the strongest only; alias | what kind of change | how strong | what happens | kind of picture when known | where on the timeline):
  For i2:
  e1 | appear | strong | a small element pops in | graphic | at 00:00:04:25
  e2 | hit | clear | something lands or stops dead | at 00:00:08:10
  ```

  Picture clips only (a sound clip, a freeze frame and a disabled clip list none). A source time becomes a record frame through the clip's in point, speed and reverse, rounded half up: the arithmetic of the Events lane (`EditProposalView.RecordFrameOf`, `map-to-record.ts`), so a moment and its lane tick fall on the same frame. The position is the timecode the item lines use. Each clip lists its 12 strongest (`ProposalMoments.MaxPerClip`); over the whole view the weakest go first until the lines fit 48 (`MaxPerView`) and the view's fact budget (`Editing:Proposals:MaxTranscriptChars`, applied to this block on its own). No file name, storage key or stored id is written: `e{n}` is numbered per view.
- **Grounding is unchanged.** Moments, like beats, are not transcript or shots: they never change `grounding.level`, `clipsWithFacts` or what Grade and Review need. Only the words of the `Facts available:` line change when the view lists moments: it names them (`EditProposalView.FactsAvailable`; with no other facts it reads `moments in the picture (found by code); nothing about what is said and no shot descriptions. …`), so a helper is never told it has no facts and then handed a moments list. A view with no moments has the line it always had. The disclosure sentence adds "a few words about moments found in your picture" when some are sent.
- **Naming a moment** (`EditProposalCompiler`). `addSfx`, `addTitle` and `note` may carry a moment alias in `itemId` instead of a clip alias. The sound, the title or the marker then starts exactly on the moment's frame, over the clip the moment is listed under; a sound or a title on a moment may run past that clip's end (up to 2 s and 3 s, inside the range). An alias the view did not offer is listed in `rejected` (`'e9' is not an offered moment`), and so is an action whose reason cites one. Both checks run only when the view lists moments: with none, a proposal compiles exactly as it did before moments existed (a token like `e3` in a reason is only words, and an `e3` in `itemId` is `not an offered item`). No other kind takes a moment: a cut is never placed on one.
- **Two sounds.** The proposed sounds share one new lane and never overlap. Of two that would (moments closer than a sound's length, 2 s), the later action is rejected (`another proposed sound already sits at e2 in <clip>`). The prompt says so and asks for moments at least two seconds apart.
- **Prompt** (`AgentEditProposalModel.SystemPrompt(agent, withMoments)`). The common part says what a moment is, that a moment marked footage is ordinary filmed material, never to invent one, and that with moments listed the helper must not say it is going by names and lengths only; the sound, re-cut, graphics and review arms each say what the list is for. These sentences are sent only when the view lists moments (`EditProposalView.ListsMoments`, the moments heading line): a call with no stored events gets the system prompt it had before, word for word.
- **Tests.** `EditProposalMomentTests` (stored shape, version, selection, loader, view text, compiler, prompt), one end-to-end case in `EditProposalGroundingTests`, the stale-result cases in `MediaAnalysisApiTests` and `MediaAnalysisEngineTests`.

### Re-cut content cuts (recut-follows-request)

Render-output clips count as analysable. A content cut whose clips have no transcript at all is refused `409 PROPOSAL_NEEDS_ANALYSIS`
before any charge, and `analysis.mediaRefs` lists `file:<id>` and `step_result:<id>` refs; partial coverage still runs and reports the rest.

A request to cut by content ("cut out all spoken words", "remove the silences", "remove the ums", "keep only the part about
pricing") used to come back as a `mute` of the camera audio: the recut vocabulary had no way to cut a range inside a clip.
Re-cut now has five content-cut kinds, all compiled in C# from stored analysis (`ProposalCuts`); the model picks the kind
and aliases, never a number:

| Kind | itemId | text | Cuts |
|------|--------|------|------|
| `cutSpeech` | clip alias or `all` | | every spoken run (transcript words, runs closer than 0.5 s merged, 0.05 s outward pad) |
| `cutQuiet` | clip alias or `all` | | silence-analysis gaps; without one, gaps of at least 1 s between transcript words |
| `cutFillers` | clip alias or `all` | | filler words by word timing (closed en + it list); needs transcript WORDS |
| `cutLine` | clip alias | `l{n}` aliases | those offered lines |
| `keepLines` | clip alias | `l{n}` aliases | everything in the clip's visible window except those lines |

Facts are written per CLIP (a bladed file's every piece is grounded); an audio clip is skipped when a video clip in range
shows the same file over an overlapping source window. `SourceFacts.Words` keeps the transcript word timings.

**Wire shape.** A content-cut group has `ops: []` and `cuts: [{itemId, startSec, endSec}]` in SOURCE seconds of the item's
asset (same basis as `sourceIn / fps`). `apply-proposal.ts` (`expandCuts`) maps them onto the CURRENT document, merges all
accepted cut groups per track and runs ONE `compileRippleCut` per track (blade/blade/extract, linked and twin partners
included) before the op groups; still one batch, one undo step; user-owned acknowledgement is judged on the expanded ops.
Because cuts are anchored to source time, a clip moved after the ask still cuts the right words.

**The request is read, not guessed.** `RecutRequestIntent.Classify(brief)` (en + it keyword tables) gives an op class
(`cut | mute | trim | reorder`) and a target (`speech | silence | fillers | keepOnly`). A `mute` when the editor asked to cut
(or named content without a verb) is refused and counted (`swapsRefused`); under a content target only content cuts pass
(remove/trim/mute are refused, and so is a content cut of another kind: `cutSpeech` and `cutLine` need op cut and target speech,
`cutQuiet` needs target silence, `cutFillers` target fillers, `keepLines` target keepOnly; a blanket `cutSpeech`/`cutQuiet`/`cutFillers`, for any `itemId`, passes only when the classifier found op cut, the matching target and an unscoped request (fail closed: no target, no verb or any scope refuses it); a topical, positional, ordinal or exception request is answered only by line cuts or, for a positional silence, edge trims); with a cut verb and no target only `mute` is refused; asked to mute, only `mute` passes;
asked to trim, remove/mute/content cuts are refused (a trim verb together with a cut verb on a speech, silence or filler
target, "shorten it by cutting out all the talking", is a cut); reorder is not something Re-cut does. A cut verb whose object is the
audio itself ("remove the camera audio") is a mute. Negated speech ("where nobody is talking") is the silence target.
When the request is a cut that is unscoped for its target (`IsUnscoped`: no topical, positional or exception qualifier;
for speech also a quantifier such as "all the spoken words"; silence and fillers need none) and no content cut survived,
the compiler adds the deterministic cut over every analysed clip and the summary is the deterministic sentence only.
"where nobody is talking" is the silence being cut, not a scope. A silence or filler named with no verb is a cut only with a clear removal word ("via i silenzi", "no more pauses"); "leave/don't touch/lascia" on silences or fillers cuts nothing. A silence edge trim passes only for the edge the brief names (start/inizio: head; end/fine: tail). A keep verb governs only its own clause (it stops at and/e/but/ma/then/comma), and only when its object is the target noun, so "keep the talking and remove the silences" is a silence cut and "remove the ums but leave the silences" a filler cut. A `cutLine` needs a cut verb and is refused when the brief says nobody speaks ("where nobody talks about pricing"). A clip named in the brief scopes every content cut and the fallback to that clip: scoping fails closed: any locating phrase (in/from/on/within/inside/during/at, nel/nella/nello/dal/dalla/dello/sul/sulla, and the elided nell'/dall'/dell'/sull'/all' with either apostrophe) or exception phrase (except, apart from, but not, other than, excluding, tranne, eccetto, escluso, ma non) makes the request scoped, so no blanket cut and no fallback; only a locating phrase whose every object is a clip name (whole word, any name, "of" is not a locating word) lifts that and restricts cuts to those clips, and a name inside an exception is never the scope. A locating phrase whose object is a whole-video noun does not scope ("from the video", "in my footage", "in tutto il video", "nell'intero filmato", "dal mio video", "in the clips"); Quantifier phrases are the whole video too ("in every clip", "on both clips", "from all clips", "in tutte le clip", "in ogni clip", "in entrambe le clip"), as are "at once", "in one go", "in it", "in una volta". A clip noun followed by a number, a name or "with" ("in clip 2", "in the clip with the dog") is a named or qualified clip, not the whole video; "in the clip Intro", "nella clip Main", "from my video Intro" name that clip (the name may follow a clip noun), and a demonstrative clip ("from that clip", "in this clip") with no name match is a part, so it is scoped. At one position the longest clip name wins ("in the Intro 2" names Intro 2 only). For an unscoped cut request, clips the model's cuts did not cover are completed deterministically from stored facts (within the named clips, when any are named). The refusal sentence says "names a part of the video" only for a scoped request, "no ... was found to cut" for a plain one. An exception clause that names clips is an exclusion set, not a refusal: "except/apart from/but not/not in/, not the/leave/keep/without X" and "tranne/eccetto/escluso/ma non/ma lascia (stare)/non nell'... X" (longest name wins, name lists continue it) make blanket cuts, the fallback and the completion apply to every clip except X; model cuts on X are dropped; an exception that names no clip, or mixing pointed-at and excluded clips, stays scoped. A singular clip noun is the whole video only when nothing but the end, punctuation or a conjunction follows ("in the clip of the beach", "in the clip Intro" with no such clip are parts, so scoped and never widened by completion); bare "both", "both of them", "all of them", "entrambi", "tutti e due" are the whole video. A timeout reading clip names reads as scoped. A short closed list of pure manner qualifiers is never an exception ("but not too much", "not too aggressively", "keep it natural/smooth/tight/clean", "ma non troppo", "mantienilo naturale"): they are ignored when reading the request, except when a capitalised or numbered word follows within four words (a possible clip name), where the exception stays. A clip name (resolved from the clip list, lowercase included) within four words after a manner phrase keeps the cue: "but not too long intro" / "ma non troppo l'intro" exclude that clip, and the same name located ("... in Main") fails closed; a name beyond four words is not tied to it. Partial-amount phrases ("not everything", "not all of it", "non tutto", "non del tutto", "leave a little breathing room") quantify what to keep, not how, so they stay a truthful refusal. A leave/keep-it clause after a clip the request points at ("in Main, leave it as it is") fails closed rather than excluding that clip. Any other exception that matches no clip fails closed, and the refusal says "the exception in the request could not be matched to a clip". Exceptions also read "without touching/cutting/removing X", "senza toccare/tagliare/eliminare X", "X lasciala (com'è|stare)", "leave X as it is" and a bare "but X" ("everywhere but the intro"); exclusion lists continue over and/e/or/o/comma/&. A read the caps cannot finish (more than 300 names or 5000 hits) with a locating or exclusion cue in the brief, or an exclusion cue followed by a capitalised or numbered name no clip resolves, is a failed read, so the request is scoped. Patterns are not compiled, so a first match does not pay a JIT cost inside the 100 ms timeout. Every request-reading pattern has a 100 ms match timeout and atomic determiner loops; a timeout fails closed (scoped, no op or target, no names). Name matching is one linear pass over the hits and fails closed on abuse: a brief over 2000 characters, names beyond the first 300, or more than 5000 name hits name nothing, so the request stays scoped. Names joined by and/e/comma/& after a preposition are each named ("from the intro and main", "nell'intro e nel finale"), and the summary then says "Only the clips you named were cut". A cut dropped by the named-clip scope drops the model's prose too. `dentro` and `durante` are locating words. When the cut is held to the named clip the summary says the other clips were left as they are, and elsewhere only a 3 to 80 character name with a digit, hyphen or space does. "The second half"/"la seconda metà" is a position, so it scopes the request and nothing is blanket-cut. `keepLines` needs a cut request, and a negated cut verb ("don't remove anything", "non togliere niente") is not a cut. A refused whole-content cut yields one plain "Nothing was changed: ... needs a specific line or range" sentence. "First"/"last"/"ultima"/"primo" next to a silence, pause or gap noun scope the request. Broad words (that, but, first, last, between, tra, fra) scope a request only when they truly qualify ("the silence that follows
the intro", "the first 10 seconds", "all the talking but the intro"). A remove/trim/mute of a clip that is also being cut is dropped
("already being cut") and the model's prose is dropped with it. The 409 `PROPOSAL_NEEDS_ANALYSIS` is only for a request whose op is a cut. Reversed clips and freeze frames get no
content cuts. A linked audio clip is skipped as a twin only when the picture plays forward (a freeze frame is not a twin, so its audio gets its own
cut) and it overlaps the picture clip in time at the same source position (a copy slipped in source is its own clip); speech
heard only on an L-cut audio tail beyond the picture is therefore not cut (known limitation); the proposal says so in `rejected` ("The sound of <clip> that runs past its picture was not cut."). The evaluation set in
`RecutCutProposalTests` pins request → (op class, target) for English and Italian.

**Response additions:** `did {cut, cutSeconds, remove, trim, mute, other, cuts[{kind, spans, seconds}]}`, `asked {opClass, target}`, `swapsRefused`,
`needsAnalysis {kinds, mediaRefs}` (some clips lack what the request needs), group `cuts`. `summary` starts with a
deterministic "what it did" sentence; the panel shows a localized line from `did`.

**Needs analysis.** A speech, fillers or keep-only request with no transcript on any clip in range (silence: neither silence
nor transcript) answers `409 PROPOSAL_NEEDS_ANALYSIS` with `analysis {kinds, mediaRefs (≤ 8 file:{guid})}` before the cap, the
hold and the model (`…/proposals/estimate` too); the panel offers **Analyse first** limited to those kinds and files, with
the estimate (transcript is metered, silence free).

**Size limits.** Cuts closer than 0.25 s merge into one. A cut group carries at most 200 spans (`ProposalCuts.MaxSpansPerGroup`; the
first 200 in source order are kept, with a rejected line naming the rest) and a proposal at most 300 (`MaxSpansPerProposal`; a later
group that would pass it is dropped whole, with its own line). The editor refuses the same (`MAX_CUTS_PER_GROUP`/`MAX_CUTS_PER_ACCEPT`,
"too many cuts to apply at once"). The expansion is computed once per accept and reused for the plan and the ghost diff; applying is
still ops × document size because the engine clones the document per op (about 2 s at 200 cuts); an engine batch apply is the
planned fix.

### What P8 leaves to later waves

Link/group (clips that move together) stays deferred; bus faders are static (no bus automation
lanes); the sequence editor is a focused modal (blade/move/trim/lift/undo), not the full workspace;
audio sync is browser-side and assumes the angles' audio is decodable by the browser; processing is
not previewed; the real-ffmpeg test runs the processing filters over a synthetic tone, not a whole
`TimelineRender` with buses (the mix graph itself is pinned by exact-string tests); the P8-Q2 browser assertion — a proposal's ghost diff and a
partial accept — runs at the component level against a real store, plus an advisory Playwright spec
(`e2e/tests/editor-assist-p8.spec.ts`) against the stubbed API.

## Polish (P9)

P9 finishes the editor's working surface: shortcuts and layouts that follow the user, presets a
project shares, secondary colour correction, a feature tracker, FCPXML, timecoded review with a
a responsive review page that reads well on a phone, and an offline op queue that never applies an op blind. **No new op**: the
catalogue stays at thirty-four. Every new document field defaults to the identity, so a document that
touches nothing from P9 renders through the P8 argv byte for byte (the region recipe hash only gains
the new fields when they are present, so existing region-cache entries stay valid).

### Offline op queue — reconcile, never replay blind

When a save fails while the browser reports itself offline, `SyncClient` enters `offline`: edits keep
landing in the store and every change persists ONE queue entry in `localStorage`
(`reelbolt:editor:offline-queue:{timelineId}`) — the base revision, the document at that revision,
the ops applied since and the local result — so a closed tab loses nothing. On the window's `online`
event (and, for an entry left by an earlier session, when the page loads) the entry is reconciled
against the server by `reconcileQueue` (`lib/editing/sync/offline-queue.ts`):

- **fast-forward** — the server is still at the base revision: the queued PATCH goes as it is;
- **rebased** — the server moved on AND all of this is proven: every op on both sides is an engine
  op (a server-side `restore` replaced the whole document and is never rebased over); replaying the
  local ops on the base reproduces the queued document and replaying the server's ops (read from
  `GET …/history`, every revision accounted for) reproduces the server's document; the two logs
  touch disjoint parts (an op that can ripple claims its whole track, a field write its item;
  markers, settings, in/out, groups, sequences, buses by name); and the two application orders
  **commute** — `server ∘ local` equals `local ∘ server` from the base — with the result passing
  `assertInvariants`. Only then is the rebased document saved, against the server's revision;
- **refused** — otherwise, with the reason. Nothing is applied, the queue stays stored, and the editor
  goes read-only with **Download my offline edits** (the queue as JSON) and **Discard them and
  reload**.

Undo history does not survive a rebase — its inverse programs belong to the old base. A fast-check
property generates random local and server op sequences over a fixture and asserts that every
`rebased` outcome equals both application orders, passes the invariants and had disjoint parts, and
that both branches are genuinely exercised.

### Preferences: keymap and saved layouts

`GET/PUT /api/v1/editor/preferences` (`EditorPreferencesController`, table `editor_user_preferences`,
one row per user — no project, so no project authorization: the row is keyed by the JWT `sub`)
stores a keymap (action id → chord, ≤ 200 entries, `""` unbinds) and saved layouts (≤ 50, ≤ 8 KB
each). `EditorPreferencesValidator` bounds shape and size; the meaning is the client's.

`lib/editing/keymap.ts` is a closed catalogue of **77 actions** with NLE-standard defaults (J/K/L,
Space, I/O, B/V/H, M, `,`/`.` nudge, Ctrl/⌘+Z, …): the 49 editing actions, nine Compose actions (`toolMask`, `toolReframe`, `toolCornerPin`, `maskShapeNext`, `maskInvert`, `composeRemoveEntry`, `composeEntryPrevious`, `composeEntryNext`, `addAdjustmentClip`; group `Compose`), the seven page switches
`pageMedia`…`pageDeliver` (`Shift+1`…`Shift+7`, in page order), `commandPalette` (`Mod+K`), `toggleViewerMode` (`Shift+V`, switches the one viewer between Source and Record; group `Navigate`) and the four placement actions `placeInsert` (`F9`), `placeOverwrite` (`F10`), `placeAppend` (`F8`) and `placeOnTop` (`Shift+F8`) (group `Edit`; they need a source open, and on macOS F8–F10 are media keys unless `Fn` is held). While the viewer's `viewerMode` is `source`, transport, arrow, Home/End and mark chords act on the source monitor and write nothing to the timeline; edit and marker jumps are no-ops, and choosing a picture tool sets `viewerMode` back to `record`. In `source` the timeline-writing chords (delete, lift, blade, cut, paste, duplicate, nudges, move track, link, unlink, add adjustment, mask shape and invert, Compose remove) do nothing and the palette lists them disabled with "Switch to Record to edit the timeline."; undo and redo still run, and placing is the only forward change. A timeline context-menu item or toolbar button that writes switches to `record` first, then runs; copy, speed, properties, select, mark and place stay in `source`, and a timeline drag does not switch. Review on Cut, the playhead steppers, play in to out and audition switch to `record` first. A held placement key places once. The
`Pages` group is a `KeyActionInfo.group` member so the cheatsheet and the editor can group them.
`?` (`isHelpKey`, i.e. `Shift+/`) opens the cheatsheet but is not a catalogue action. `Delete`/`Backspace`
is **Delete selection** (ripple: the next clip closes the gap, decision D7) and
`Shift+Delete`/`Shift+Backspace` is **Lift selection** (leaves the gap), so the keyboard and the Clip
menu agree. Chords use the PHYSICAL key (`KeyboardEvent.code`), so a shortcut is the same key on every
layout and Shift does not change which key it is (`Shift+,`, never `<`). Stored overrides for unknown
actions are dropped; conflicts are listed in the **Shortcuts** editor and block saving.

The keymap is a surface, not a secret:

- **Cheatsheet (`?`).** A read-only, searchable dialog grouped **Transport / Navigate / Marks / Tools /
  Edit / Pages**, with a no-match state. One **Edit shortcuts** button opens today's editable
  `KeymapEditor`. It is reachable from the header keyboard icon, the tablet/phone More menu and the
  page empty states.
- **Command palette (`Mod+K`).** A combobox over every command, filtered by label, showing each
  action's chord and whether it is available with the current selection. Enter runs it; an unavailable
  action is listed disabled with the reason. The palette and the global keydown listener call the same
  `runCommand` descriptors in `lib/editing/commands.ts`, so they cannot diverge; a dispatched edit and
  the palette's Undo are one undo path.
- **Live chords.** Menus, tooltips and the palette read `effectiveChords` and render with
  `displayChord`; a remapping updates them. `aria-keyshortcuts` is generated by the separate
  `ariaChord` formatter (`Mod` → `Control`/`Meta`, `Space` → `Space`, no glyphs, unit-tested), because
  `displayChord` is for visible copy.
- **Phone.** A read-only **Keyboard shortcuts** entry in the overflow menu lists the actions that need
  a keyboard with their reasons, from `keyboardOnlyActions()` in `lib/editing/keymap-touch.ts`. The list is
  whatever `keyboardOnlyActions()` returns (the count is not written here, so it cannot drift): the
  shuttle, stop, play in to out, ten-frame steps, moving a clip to another track, the Compose tools, the
  Cut/Compose/Colour page switches, the Source | Record switch, the placement chords, the full-screen
  viewer and the command palette. `TOUCH_PARITY` gives every catalogue action
  a touch route or a keyboard-only reason: `pageMedia`/`pageEdit`/`pageAudio` use the phone dock,
  `pageDeliver` uses the header's **Export**, and the CI contract test stays green.

**Layout** saves the open left/right tabs and the panel fractions per workspace and re-applies them;
applying a saved layout replaces the page's fractions and persists them. Separately, each page's
fractions are remembered per device (see [Pages and panel geometry](#pages-and-panel-geometry)), and
**Reset this page** / **Reset every page** restore the table defaults. A failed shortcut save keeps the
dialog and the draft open with the reason, and saving before the stored preferences have loaded is
refused rather than overwriting the saved layouts (W0.6, UI2-08).

### Presets

`GET/POST/PUT/DELETE /api/v1/projects/{projectId}/edit-timelines/presets` (table `editor_presets`)
keep a project's **transition** (`transitionType`, `easing`, `framesBefore`, `framesAfter`),
**effect** (an effect stack) and **mastering** (`targetLufs`, `truePeakDbtp`, `loudnessRange`)
presets. `EditorPresetValidator` checks every payload against the SAME closed catalogues and ranges
the timeline validator and the render config use, so a preset can never hold a value a document or a
render would refuse. A preset is copied in when applied; deleting it changes nothing that used it.
The editor ships three mastering targets — social −14 LUFS, podcast −16 LUFS, broadcast −23 LUFS
(EBU R128), each at −1 dBTP — which feed the render's existing two-pass `loudnorm`. Pickers sit in
Deliver (mastering), the inspector's transition row and Compose's effects list. Deleting a preset
asks for confirmation first, and a failed delete says why (W0.6, UI2-14).

### HSL qualifiers, tracked power windows and the feature tracker

`GradeNode` gains `qualifier` (`GradeQualifier`: hue centre [0, 360)°, half-width (0, 180]°,
saturation and lightness bands [0, 1], softness [0, 0.5], invert) and `window` (`GradeWindow`: a P7
`ShapeMask` plus `offsetX`/`offsetY` keyframed in canvas fractions [-1, 1], timeline frames). Schema,
C#, TS and the drift guards moved together (two new `$defs`); the validator and the engine apply the
same ranges. A node with neither is the P7 whole-frame correction, byte for byte.

`GradeFilterBuilder` renders a secondary as `format=gbrap,split=3` → the correction on one copy →
a `geq` matte computed from the UNGRADED pixels (HSL via `atan2`, every edge ramped over the
softness, the window's centre following the offset keys over `geq`'s `T` through the balanced
keyframe tree) → `maskedmerge`. gbrap keeps any alpha an upstream keyer made; the matte's alpha plane
is 255. The expressions are inlined — never `st()`/`ld()` registers, which `geq`'s parallel slices
would race. `EditorP9BackendTests` pins the chain and runs a qualified, tracked secondary through
the real ffmpeg. The preview cannot do this per pixel, so it **skips** a secondary node instead of
folding it whole-frame (which would show a plausible-but-wrong picture) and the parity class is
`renderRequired`.

The **feature tracker** (`lib/editing/color/feature-tracker.ts`) follows any textured feature — not
only `ChromaQuadTracker`'s uniform plates: the user boxes it with the window, frames are sampled from
the clip's proxy at the timeline rate (≤ 10 s, an off-screen `<video>` seeked frame by frame), and
normalised cross-correlation over a search window finds it in each frame. The template refreshes only
on strong matches; a frame under the score floor is reported LOST and the position held, never
invented. The track becomes the window's offset keys, Ramer–Douglas–Peucker-thinned. The maths is
tested on synthetic moving textures (exact positions, a vanished frame); the browser sampler is not
exercised in CI.

### FCPXML

`GET …/edit-timelines/{id}/export?format=fcpxml` (`FcpxmlWriter`): FCPXML 1.10 with one `format`, one
`asset` per distinct source (`media-rep src` = the same `reelbolt://` URL the OTIO and EDL exports
use), and a `project` whose spine is the first video track — `asset-clip`, `gap` and `transition`
elements, contiguous — with higher video tracks and audio tracks as connected clips on lanes ±n,
offset in the parent element's time base, and timeline markers on the element they fall in. Times
are exact reduced rationals (`frames·den/num s`). Titles, captions, generators, grades and keyframes
are not exported (OTIO is the lossless format); compound and multicam clips export flattened. The
file has not been opened in Final Cut Pro from this host; its structure is pinned by tests.

### Review comments and the mobile review page

`GET/POST …/edit-timelines/{id}/comments`, `PATCH/DELETE …/comments/{commentId}` (table
`edit_timeline_comments`): a comment pins a body (≤ 4000) to a timeline frame, optionally spanning
`durationFrames`; replies are one level deep and take their root's frame; the author edits their own
text, while resolving or removing someone else's comment needs Edit permission (the permission the
single access check asks for depends on the comment's author); deletes are soft. Deleting a thread's
**first** comment keeps the thread (W0.6, api D9): its oldest still-visible reply becomes the first
comment, taking the frame, span and resolved state, and the other replies are re-pointed at it, so no
reply is left hanging off a hidden root. A caller outside the project still gets `404`, and every
comment route, including edit, resolve and delete, answers `404` once the timeline itself is deleted. Comments are not
part of the document — no revision, no undo step. The editor's **Review** tab lists the threads (seek,
reply, resolve, show resolved). **`/app/editor/{projectId}/review/{timelineId}`** is ONE read-only responsive page (R11.5, VF16;
`components/editor/review/MobileReview.tsx`, pure rules in `lib/editing/review/review-state.ts`): a
phone gets a single column, from the `md` breakpoint (62em) the video sits beside the comments.
It plays the newest finished render with a picture that covers the whole timeline
(`lib/editing/review/pick-render.ts`; an in/out-range or audio-only render is used only when no full
one exists, and is labelled) in the browser's own player, with a **Render** picker. A render whose
revision is below the timeline's shows the **Newer edits exist** banner with a link to the editor.
A bar under the player carries one 44 px marker per open thread; a chip above the comment box shows
the paused frame the comment will pin to, read from the video at the moment of posting (the `<video>`
is attached through a ref callback and `loadedmetadata`/`durationchange` refit the timecode, so this
holds when the player mounts or loads late; UI2-01). **Copy review link** copies the page address
(Web Share where `navigator.share` exists) and the page says "Anyone in this workspace with access
can open it": guest links arrived in Wave 4 (see [Guest review links](#guest-review-links-wave-4)). On the phone a thread has Reply, Resolve/Reopen,
Edit and Delete (a second tap confirms); the client does not know the caller's user id, so the server
decides who may delete and its refusal is shown in plain words. The comment box refits above the soft
keyboard through `use-keyboard-inset`.

### Phone proposal review sheet (RM.13, R11.3)

Header **⋯ then Assist** opens `components/editor/phone/ProposalSheet.tsx`, built on the same
`useProposalFlow` hook as the desktop `ProposalPanel` (ask, price, ghost diff, accept, skip are shared).
The price and allowance show before asking; groups are 44 px rows, tapping one seeks the playhead and
paints only that group's ghost (`focusGroup`); the acknowledgement tick for a user-owned clip is inside
the group and Accept stays disabled until it is ticked; Accept and Reject sit in a sticky footer; after
Accept an "Applied. Undo" snackbar undoes the single `applyBatch` history step.

### What P9 leaves

Link/group remains deferred; the feature tracker is translation-only (no rotation/scale) and runs in
the browser over proxy frames; secondaries are render-only in the preview; FCPXML import is not
offered (OTIO is the import format); the offline queue rebases only provably commuting edits — two
people editing the same track while one is offline get a refusal and a download, not a merge;
comments are not painted on the timeline ruler; the review page is for watching and commenting (a phone
edits and sends from the phone editor: see [Phone and tablet](#phone-and-tablet-wave-3)). Browser
assertions run at the component level against real stores and an advisory Playwright spec.

## Colour depth (pro UI wave 4)

Wave 4 of the pro UI alignment (`docs/design/editor-pro-ui-alignment.md` §13) finishes the Colour page as the grade page. It adds **no op and no document field**: the op catalogue stays at 34, every write is the page's existing whole-stack `setProperty grade` (a clip or an adjustment clip) or `setColorGroup` (a group), and one user action is one undo step. Everything new lives under `web/components/editor/color/` and `web/lib/editing/color/`; the viewer, the compositor, the keymap and the C# code are untouched.

**Node list** (`GradeNodeList.tsx`, pure helpers in `lib/editing/color/grade-nodes.ts`).

- **Rename** in place: double-click the name, F2 on it, or the Rename button. Enter commits, Escape cancels, blur commits. The name is trimmed, inner whitespace collapsed, control characters dropped, cut to `NODE_NAME_MAX` (80). An empty or unchanged name writes nothing. The server validator does not limit `GradeNode.name`; 80 is a UI limit only, and the validator was not changed.
- **Reorder**: Move up / Move down act on the selected node; a row can be dragged with the pointer (the browser's own drag and drop, as the picture editor's layer list does: the editor has no drag-and-drop library); the grip is keyboard-operable (Space or Enter picks up, arrow keys move, Space or Enter drops, Escape cancels, with an `aria-live` position line). Nothing is written until the drop; the drop writes the reordered `nodes` array once.
- **Duplicate** places a deep copy after the node under a new id; refused at `MAX_GRADE_NODES` (8). The copy's stored name is language-neutral (`duplicateNodeName`: a default "Correction N" becomes the next default name, a typed name gets a number). **Reset** returns a node to identity, keeping its id, name and on/off state. **Add correction** and **Add look** are disabled at eight nodes.
- **Enable** and **Remove** are unchanged. On a read-only timeline every write control is disabled.

**Solo is view state only.** It is never written to the document, never reaches a render, a still, a review link or a hand-off, and has no keymap action. `soloStack` builds the target's stack with only the soloed node enabled (the stack itself forced on) and the Colour page publishes it through the transient grade preview (`GradePreviewContext`, the channel a wheel drag uses). `GradePreview` gained an optional `also` list so the other stack of the chain is switched off in the same preview: a clip's colour group stack while a clip node is soloed, the selected member clip's own stack while a group node is soloed. Adjustment layers above the clip are not part of the chain and stay applied. `withGradePreview` returns the same derived document for the same document and preview object, so a preview that stays up does not invalidate the viewer's memoised document on every render. Solo ends on Escape (a rename field or a keyboard move that uses Escape keeps it), on **Leave solo**, on a second click of **S**, when the target changes (another clip, a group, no selection), when the soloed node is removed, and when the Colour page unmounts. It is not remembered: returning to the clip does not bring it back. A wheel or slider drag while a node is soloed previews through the solo, and after the write the solo is published again for the new document. Solo is marked on the node row, on the graph node, above the grade tools and above the scopes (`SoloLine`, "Solo: <name>" with **Leave solo**). Solo works on a read-only timeline. With **Bypass grade** on, the picture shows no grade at all, soloed or not.

**Serial grade graph** (`GradeGraph.tsx`). A compact read-and-select strip in the left zone, above the node list, in a collapsible band ("Grade chain", open state remembered per device): Input, each correction as a numbered box, Output. `gradeChain` gives the order, which is the order of `effectiveGrade`: a media clip's colour group stack first, then the clip's own. An adjustment clip has only its own stack; a group that is the target with no member clip selected is shown alone. A click selects the node (the same selection as the list); clicking a group's node while a clip is the target makes that group the target. A node that is off, or whose stack is bypassed, is drawn hollow (dashed, no fill); the soloed node carries an S; glyphs show what a node carries (primaries, curves, LUT, qualifier, window), and the same facts are in its accessible name. It is a listbox with one tab stop: arrow keys, Home and End move the selection, Enter moves focus to the grade tools (switching to Primaries from Groups or Gallery). It scrolls sideways inside its band and never changes the width of the left zone. It does not use the Compose stage graph's library; `compose/NodeView.tsx` is unchanged.

**Scopes layouts** (`ScopesPanel.tsx`, `lib/editing/color/colour-page-prefs.ts`). One scope, two side by side, or all four (waveform, RGB parade, vectorscope, histogram). A tick is still ONE downscaled readback (192 px wide) and ONE `computeScopes` pass at about 15 Hz, which already yields the bins of every scope; a layout only adds the drawing of the scopes it shows. Statistics are computed whether or not the panel is expanded, in every layout. The docked height is resizable (drag the top edge, or arrow keys, Home and End on it; 72 to 320 px; on a touch screen two 44 px buttons, Shorter and Taller, replace the edge) and the panel still collapses. Layout, the chosen scopes and the height are per device in `localStorage` (`reelbolt:editor:scopes-layout`; the chain band uses `reelbolt:editor:colour-graph`), read and written in try/catch; a blocked storage keeps the choice for the visit only.

**Before / after as first-class controls** (`CompareBar.tsx`). Above the grade tools on every tab: **Bypass grade** (toggle), **Split before / after** (captures the picture once with the grade bypassed and wipes against it; `useBeforeAfterSplit`, shared with the look panel), the **Reference still** picker, **Grab still**, and the **Wipe** slider while a reference is set. All of it is view state and works on a read-only timeline. The bar follows the look panel's rule for its own brief bypass (taken to read the ungraded frame for the thumbnails): the button does not show that bypass, and a choice made on the bar while it runs stays (`LookPanel` reports it through `onPanelBypass` and reads `userBypassSeq`); the split's own capture does not flicker the button either. Bypass has no keymap action, so the button shows no chord (the keymap catalogue was not changed). The Gallery tab keeps its own Compare section and stills grid.

**Copy.** The tabs no longer say that curves, LUTs and secondaries are render-only: the viewer's grade pass previews them (`preview/grade-pass.ts`), so the `renderOnlyTab` line is removed and the LUT and secondary notes are corrected. The viewer's parity badge still speaks when something differs (a LUT still loading, more than 16 corrections).

**Not in this wave** (each needs its own approval, design §13): parallel graphs, several windows per node, grade versions, per-node animation, a keymap action for bypass or solo, and solo across adjustment layers.

Tests: `lib/editing/color/__tests__/grade-nodes.test.ts`, `colour-page-prefs.test.ts`, `grade-preview.test.ts`; `components/editor/color/__tests__/colour-depth.test.tsx`; `components/editor/__tests__/color-scopes.test.tsx`.

## Visual system and accessibility (Wave 1 polish)

### 12 px text floor (VF2)

No editor text is smaller than 12 px (Mantine `size="xs"` is 12 px). This covers `components/editor/**`, the
editor CSS in `app/global.css` and the canvas text in `timeline/paint.ts`. `components/editor/__tests__/no-small-fonts.test.ts`
scans the sources (props `size="10px"`, `fz`, `fontSize`, CSS `font-size`, `ctx.font`, rem values) and fails the
build on a new smaller size; its scanner has its own unit test with seeded violations.
`timeline/__tests__/paint-font-floor.test.ts` pins the canvas constants. The Playwright check
(`e2e/tests/editor-v2/15-sweep/`) asserts every visible text node's computed size is at least 12 px on the
editor, hub and deliver pages. Clip colours have one source: the `--rb-clip-*` properties in `app/global.css`;
`CLIP_FALLBACK` must mirror them (`clip-colour-source.test.ts`).

### Plain words for every refusal and error (R01.4, R03.7, VF3)

No sentence a person reads in the editor carries a GUID, an op name (`setProperty`), a `track-<id>` key,
raw JSON, a status code or an exception name.

- **Refusals.** `web/lib/editing/refusal.ts` (`refusalSentence`) turns the engine's and the validator's
  messages into sentences and swaps every id the document knows for its name ("The track “Interviews” is
  locked."). `lib/editing/__tests__/refusal-corpus.test.ts` lifts every `EditEngineError(...)` message out
  of `engine/apply.ts`, fills its holes with ids, runs it through the mapper and fails on any GUID, camelCase
  op id, `op:` prefix, `track-` key or brace in the result; a new engine message is covered without
  touching the test.
- **Other errors.** `web/lib/editing/humanize-error.ts` (`humanizeError(error, fallback, doc?)`) is the one
  helper every error text in `components/editor/**` goes through. A message that is already a plain sentence
  is kept; raw text (JSON, a stack, an exception or type name, a URL, "HTTP 500") is replaced by what the status
  means in words ("Something went wrong on our side. Try again in a moment.") or by the caller's own fallback.
  Deliver (`deliver/*`) and the status bar (`shell/StatusBar.tsx`) are owned by other lanes and adopt the
  helper there.
- **Titles and captions.** Titles and captions keep letters, digits, combining marks, spaces and
  `. ! ? " ( ) - — / & # @ +` (`OverlayTextSanitizer` on the server, `lib/editing/title-text.ts` in the
  browser). While a person types, a sentence under the field says "Some characters aren't allowed in titles
  and were removed" (`panels/OverlayTextInput.tsx`, used by the Inspector's title and caption fields and the
  Subtitles rows); on leaving the field the stored text is the cleaned one and the field shows it.

### Clip colours: `--rb-clip-*` (VF1)

The timeline's clip colours are CSS custom properties in `app/global.css` (`--rb-clip-video`, `-audio`,
`-title`, `-adjustment`, `-generator`, `-compound`, `-caption`, `-transition`, `-insert`, `-muted`,
`-selected`, `-text`), defined for the light scheme and again under `[data-mantine-color-scheme='dark']`.
`timeline/editorTheme.ts` is the single reader: `readClipColors(scheme)` reads the properties for the canvas
(`TimelineCanvas`), `CLIP_FALLBACK` is the same table for contexts without a stylesheet, and
`withClipColors` applies it to the base `PaintTheme` in `paint.ts`. A media clip on an audio track paints in
the audio colour; a disabled clip, or one on a muted or disabled track, paints in the muted fill (it is no
longer faded, which dropped its name below AA).

The bar, enforced by `timeline/__tests__/contrast.test.ts` over the stylesheet itself and over the fallback
table, in both schemes: text on every fill is at least 4.5:1 (WCAG AA), and the selection ring, a non-text
mark drawn inside the clip, is at least 3:1 against every fill. Changing a token that breaks either fails the
test. Editor text is never smaller than 12 px in code touched by this work (the clip name on the canvas is 12 px).

### Safe areas (VF17)

The editor routes (`app/(app)/editor/layout.tsx`, `app/(app)/projects/[id]/editor/[stepResultId]/layout.tsx`)
export `viewport = { viewportFit: 'cover' }` and wrap their children in `data-editor-surface`. While that
element is on the page, `app/global.css` reads `env(safe-area-inset-*)` into `--rb-safe-top/right/bottom/left`
and applies them: the app header grows by the top inset and pads its content below it, the editor workspace
pads left and right (and bottom, where the status bar is the last row), the phone dock pads its bottom edge,
dialogs and bottom sheets pad their body, and toasts are offset from the corners. Outside the editor the
variables are `0px`. `e2e/tests/editor-v2/11-basics/polish-plain-words.spec.ts` shims the four variables with
emulated insets and asserts the computed paddings are at least the insets on desktop, Pixel 7 and iPhone 14.

## Timeline editing core (Wave 2, T-004: R03.1 / R03.2 / R03.3)

Pure builders live in `web/lib/editing/timeline-edit/` (ops composed from the closed catalogue, no new ops; each action is one undo step and one small
function so link-aware wrappers can decorate it): `buildDeleteOps` (ripple, via `rippleDeletePlan`), `buildLiftOps`, `buildDeleteGapOps` / `buildCloseAllGapsOps` /
`listGaps` (gaps are objects: clicking empty space between clips selects the gap, Delete closes it), `copyClips` / `buildPasteOps` / `buildDuplicateOps`,
`buildMoveToTrackOps`, `buildJoinOps`, `buildFitToFillOps`, `buildToggleEnabledOps`, `selectFromPlayhead`, `buildNumericTrimOps`.

- **Delete = ripple delete, Shift+Delete = lift.** Delete closes the gap; Shift+Delete leaves it.
- **Clip action list**: right-click a clip (desktop) or the visible **Clip** (...) button (touch/tablet; same list, 44 px targets). Split, Delete, Lift, Copy,
  Cut, Paste at playhead, Duplicate, Fit to fill, Replace from the media pool, Join with next, Move to the track above/below, Speed..., Disable/enable,
  Select forward/backward on this track or all tracks, Close all gaps, Properties. Double-click a clip opens it in the inspector. Visible -10/-1/+1/+10 frame
  steppers move the selected clip and the playhead.
- **Clipboard**: internal JSON (`reelforge-edit-clips`, version 1) kept in memory and `localStorage` (so another editor tab / timeline can paste), also offered to the
  system clipboard as text. Offsets between clips and tracks are kept; ids and `linkId`s are regenerated consistently. Paste never overwrites or ripples: a taken span is
  refused with a sentence; Duplicate inserts after the selection and pushes later clips right.
- **Trim**: mode switch in the toolbar (ripple, roll, slip, slide) plus keys R / T / Y / U; a readout names the mode and the frame delta while dragging; handles have a
  10 px hit area (44 px on coarse pointers). The inspector has typed **In** (source in-point), **Out** and **Duration** in frames or timecode, validated against the source
  length with a plain sentence when refused (composed from `trim` ops and verified against the engine before it is applied).

Keymap additions (`web/lib/editing/keymap.ts`): `trimRipple` R, `trimRoll` T, `trimSlip` Y, `trimSlide` U, `copyClips` Mod+C, `cutClips` Mod+X, `pasteClips` Mod+V,
`duplicateClips` Mod+D, `moveTrackUp` Alt+Up, `moveTrackDown` Alt+Down. All are rebindable in the shortcuts dialog.

## Deferred clip operations (Wave 4, R03)

The four controls the Wave 2 verdict deferred are all reachable on desktop and tablet; the phone shows none of them (the phone trim
sheet keeps its own Slide step buttons).

- **Slide** is the fourth entry of the toolbar "Trim:" menu (`trimSlide`, key `U`), explained as "the neighbour on the side you drag
  absorbs the change; the other side keeps its edge, so a gap can open there" (Accepted engine deviations 3). Refusals go through
  `refusalSentence`.
- **Join with next clip**, **Fit to the gap** and **Replace with...** sit in the shared clip action list (right-click menu and the
  **Clip** popover, ids `join`, `fit-to-fill`, `replace`). Whether each can run is decided by the pure helpers in
  `web/lib/editing/timeline-edit/deferred-ops.ts` (`joinAvailability`, `fitToFillAvailability`, `replaceAvailability`,
  `replaceSourceCheck`), which name the reason (no next clip, a gap, a different file, source not continuing, no gap to fill, frozen
  frame, file shorter than the clip) and otherwise let the engine's dry run decide. A disabled entry prints its reason as text under
  the label (`ctx-<id>-reason`, `clip-action-<id>-reason`), not only as a tooltip. Each is ONE undo step.
- **Fit to the gap** shows "Speed becomes N%" after it runs (N is the new playback rate as a whole percent; non-media clips say the
  clip fills the gap). **Replace** keeps `id`, span and every other field (so the take stack stays), restarts at source frame 0, and
  refuses a file shorter than the clip's source span with "That file is shorter than the clip." (stills and unknown lengths pass).
- No new keymap action: the touch path is the phone sheets, so `keymap-touch.ts` is unchanged. There is no menu bar "Edit" menu in
  this editor; the **Clip** popover is its home for these entries.

### Assist: what the helper can see (Wave 4, R11.2 UI)

Before **Ask for suggestions** the panel calls `GET …/proposals/availability` (for the in/out range, else the whole timeline). A helper
the server marks unavailable is greyed in the list, and its reason is printed under it (`proposal-unavailable-<agent>`); the Ask button
is disabled in words when the chosen helper is off. When `analysisAvailable`, **Analyse first** (desktop panel only) prices the
shots and transcript analysis of the files in range with the existing analysis-on-demand calls (R09.1), shows the total, and starts
only on **Start analysing**. After a response the `grounding` line is shown ("Used the spoken words and shots of 5 of 6 clips", or the
server's note for length-only cutting). `409 PROPOSAL_NEEDS_ANALYSIS` shows the server's message as the error. The phone proposal sheet
shows the same availability and grounding lines and adds no control.

## Place from media, titles, colour cards, freeze frame (Wave 2, R03.6 / R10.5 / R07.5 / R07.6)

No new op; the only additive field is `TitleClip.position`. All of it is ordinary ops dispatched with `dispatchBatch` (one undo step).

- `web/lib/editing/media/place.ts` `buildPlaceOps(doc, {item, mode, at, trackId, marks, rate, newId})` for `insert | overwrite | append | placeOnTop` returns `{ops, clipId, companionId, landedAt:{trackName, frame, tc}}` or `{ok:false, reason}`. `insert` goes to the nearest cut where the clip fits (flush before a clip, or in a long enough gap, since the engine refuses a partial overlap); `overwrite` clears `[at, at+length)` with `blade` + `lift` then `placeOnTop`; `placeOnTop` uses the topmost free track or `addTrack`. A video with sound adds its linked companion (`pool-insert`). `<PlaceMenu>` (`components/editor/place/`) is the shared control (buttons in the source viewer, a menu on tiles); `showLandedToast` is the "Placed on Video 1 at ..." line with Undo.
- `structure/graphics.ts` `buildGraphicOps` builds a title (template from the closed `TitleTemplate` set) or a colour card (`generatorType: 'color'`, `generatorConfig.color`) with a chosen length and track. `TitleClip.position` (an optional 3x3 pad, the same as captions) is set by `spec.position` from a 3x3 position pad (`place/TitlePositionPad.tsx`, nine plainly named spots plus "Template default", arrow keys, 44 px targets) in the Add a title dialog and in the inspector (a `setProperty` on `position`, one undo step). The preview (`preview/title-template.ts` `titleTemplateLayout`) anchors the text as the render's `CaptionBurnIn.Placement` does; like the render, "Lower third" and "Card" keep their own place and the pad is disabled for them with a note. Absent, the template decides layout.
- `structure/freeze-frame.ts` `freezeFrameOps`: `blade` (link-aware) + `nudge` of everything at or after the playhead on the picture track and its partners + `insert` of a `hold: true` clip, 2 s, unlinked, so the paired sound has a gap (silent).

## Linked clips and multicam sound (Wave 2, R08.1 / R08.2)

A picture clip and its camera sound are one linked group: media clips sharing a `linkId` (1-64 chars of `[A-Za-z0-9_-]`, minted with `newGuid()`). The field is optional and additive; a stored document without it keeps loading. The document rules (same in `assertInvariants` and the server validator): members are media clips, no two on one track, all from the same source, at most 8; a group of one is tolerated (its partner was deleted or unlinked elsewhere). Compound sequences keep their own groups. Violations are reported at `<item path>.linkId`.

There is **no new op** (the catalogue stays closed). `web/lib/editing/link/` builds ordinary ops for every member of a group, to be dispatched with `dispatchBatch`, so one gesture is one undo step:

- `expandSelection(doc, ids)`, `groupOf`, `partnersOf`, `isLinked`: the group of a clip.
- `linkedOpsForMove(doc, moves[])`, `linkedOpsForNudge(doc, ids, delta)`, `linkedOpsForTrim(doc, id, edge, mode, delta)` (the delta is the largest every member can take, so a clamped member never leaves the pair out of step), `linkedOpsForBlade(doc, id, atFrame, newItemId)` (the right halves get a fresh shared `linkId`), `linkedOpsForDelete(doc, ids, 'ripple' | 'lift', isLocked)` (ripple closes the gap on every track a member sits on; a partner on a locked track is left alone and counted), `linkedOpsForRetime`, `linkedOpsForEnable` / `linkedOpsForSetProperty` (only `enabled` is group-wide).
- `linkOps(doc, ids)` / `unlinkOps(doc, ids, only?)`: `setProperty linkId`.
- `jlCutOps(doc, id, edge, delta)`: the Alt/Option override. Moves one clip's edge in place (sound leads or trails the picture), keeps the link, and puts later clips on that track back where they were; empty when a neighbour is in the way.
- `migrateToLinkedAudio(doc, probes)`: for existing timelines, links same-source same-span picture/sound pairs and adds the missing companion for footage whose probe says it has sound. Idempotent; returns `{ ops, linked, added, summary }` ("Linked 1 clip, added sound for 2.").

UI wiring (T-009): the store's `setUi({ selection })` runs `reconcileLinkedSelection` so selecting one half selects the group (deselecting one half of a fully selected group drops it); `ui.editSeparately` (view state, the "Edit sound separately" toolbar toggle) bypasses expansion and the link-aware builders. Timeline edits (`EditorWorkspace`, `useTimelineEditing`) call the `linkedOps*` builders: drag-move, drag/typed trim (Alt on the pointer, or `editSeparately`, uses `jlCutOps`), split/blade, nudge, delete/lift, speed (`linkedOpsForRetime` through the inspector), enable. Copy/paste/duplicate go through the expanded selection and the clipboard's consistent `linkId` remap. The clip menu and keymap carry `linkClips` (Mod+Alt+L) and `unlinkClips` (Mod+Shift+Alt+L); `LinkMigrationBanner` offers `migrateToLinkedAudio` once per timeline (dismissal in localStorage, never auto-runs, one undo step). Linked clips paint a chain badge; the inspector shows a Linked chip and a Picture/Sound switch for a selected pair. Preview audio and the mixer need no change: the companion is an ordinary audio-track media clip.

A pool insert of a source with an audio stream, and "Extract audio", put the companion on the "Camera audio" track in the same batch with a shared `linkId` (`PoolPlacement.linkId`); no audio stream, no companion, no link.

**Multicam sound.** `MulticamClip.audioSource` (optional): absent or `program` = the first angle's sound across the whole clip; an angle id = that angle's sound; `none` = silent. `flatten.ts` (and `TimelineFlattener.cs`) emit one derived audio track `<clip>/audio` right after the host track holding one clip `<clip>/audio0`, so the preview and the render play the same sound.

## Audio model UI (Wave 2, R08.2-R08.7, T-010)

No new op or document field. Everything composes `setTrackProperty` / `setProperty` / `addTrack` / `removeTrack` through `dispatchBatch` (one undo step). Files: `web/components/editor/audio/*` (`Fader`, `LoudnessTargetSection`, `DuckSection`, `AuditionSection`, `CleanVoice`, `VoRecorderPanel`, `useAudioJob`, `useAdvancedAudio`) and `web/lib/editing/audio/{fader,loudness-target,duck-state,audio-api,vo-recorder}.ts`.

- **R08.3 one fader.** `Fader` reads/writes `track.gainDb.value` (range -60..+12, -60 is shown as "-inf"; `lib/editing/audio/fader.ts` parses typed dB, clamps, labels). Double-click on the slider or the reset button commits 0; pan double-click centres. The BusesPanel latch slider uses the same +12 ceiling.
- **R08.4 loudness target.** Chips Social -14 / Podcast -16 / Broadcast -23 (choice in localStorage `rb.editor.loudnessTarget`). "Measure loudness" saves (`flushSave`), optionally shows the `POST .../loudness/estimate` credits, starts `POST .../loudness`, polls `GET .../audio-jobs/{executionId}` (finished status is `Passed`) and reads `loudness.integratedLufs`. "Match target" = `compileMatchTarget`: the document has no master gain field, so the delta is added to every unmuted audio track's static fader and keyframes (clamped to +12), one batch, then it measures again. On target = within 1 LU.
- **R08.5 duck.** `lib/editing/audio/duck-state.ts`: the curve is `deriveDucking` keyframes on the music fader; "Ducking: on" is a per-timeline, per-browser setting (localStorage `rb.editor.duck.<timelineId>`: music track, voice tracks, depth/attack/release). While on, `DuckSection` (mounted in the Audio workspace) re-derives when the stored keys differ from the derived curve (one `Update ducking` undo step per change); Update forces it, Remove writes a flat fader and clears the setting. Music/voice tracks default from track names.
- **R08.6 Clean voice + Audition.** `compileCleanVoice` sets `denoise {fft, 0.6}` and `leveler {speech, -18}` together (one batch). Audition: `POST .../audition` with a ~10 s range around the playhead (`auditionRange`), polled like loudness, played from `outputs/{stepResultId}/download`. Numeric DSP (equaliser numbers, buses, compressor/gate/limiter/de-esser) is behind the "Advanced audio" switch (`useAdvancedAudio`, localStorage `rb.editor.advancedAudio`, shared by MixerPanel and BusesPanel).
- **R08.7 recorder.** `startVoTake(mime, {deviceId, deferStart})` opens the chosen input, meters it with an AnalyserNode (`take.level()`), and starts the MediaRecorder after the 3-2-1 count-in. The take lands as a project file + clip on a NEW "Voiceover" audio track (`compilePlaceVoiceover(..., newTrack=true)`); Retake = `removeTrack` + `addTrack` in one batch. `micErrorSentence` maps DOMException names to sentences; "Upload a recording" (file input, duration from the audio element) is the fallback when the mic is blocked or MediaRecorder is missing.
- **R08.2 multicam sound.** `compileCreateMulticam(..., audioFrom)` stores `audioSource`; the Structure panel has a "Sound of the multicam" choice (program, any selected clip incl. a separate recorder clip, none), −1/+1 frame nudges next to each sync offset (with seconds and confidence), and a Sound select on an existing multicam (`setProperty audioSource`).

## Loudness measurement and audition (R08.4, R08.6)

`POST …/edit-timelines/{id}/loudness` (Edit) measures the mixed audio: it queues a hidden AudioOnly render that runs the
real mix path and its EBU R128 analysis pass, registers no project file (so nothing appears in Deliver) and prices like a
render (AudioOnly tier; `…/loudness/estimate`, `402 INSUFFICIENT_CREDITS` when short). `POST …/audition` (Edit) renders only a
required `range` as execution-scoped audio, the same way. Decision: both reuse the render pipeline through thin endpoints
rather than a new preset flag. Poll `GET …/audio-jobs/{executionId}`; audition audio plays through the existing outputs
download of `stepResultId`. `GET …/renders` omits these jobs (file-name prefixes `loudness-measure-` / `audition-`).

### Render loudness checks

The `loudness` node also carries `checks`: findings from `LoudnessAssessor` on the final mix (empty array when clean; the node stays null when nothing was measured). Each entry is `{kind, severity, startSec?, endSec?, measured?, limit?, detail}` (`severity` high|medium|low, `kind` from the closed list `loudness_off_target`, `loudness_range_wide`, `true_peak_high`, `clipping`, `long_silence`, `no_sound`). The target is the step's `Loudness` or the defaults (-16 LUFS, -1.5 dBTP, 11 LU) with 2 LU tolerance. Thresholds: off target = integrated loudness more than 2 LU away (skipped when the render was normalised); `clipping` (high) = true peak at or above 0 dBTP; `true_peak_high` = more than 0.2 dB over the ceiling; `loudness_range_wide` (low) = over target range + 4 LU; `long_silence` = 3 s or more of silence in the middle (medium from 6 s); `no_sound` (high) = the film has an audio track but it is silent throughout. Silence comes from a separate `silencedetect` pass (-50 dB, 2 s) on the mix, capped at 50 stretches; if that pass fails the render still succeeds without silence facts. The API reads `checks` defensively (at most 20, detail cut at 300 characters, unknown kinds or severities dropped, empty on outputs stored before this) into `EditTimelineRenderLoudnessResponse.Checks`. The web words each check from `kind` plus the numbers (localised); `detail` is only an English fallback. `TimelineRender` step revision: `2026-11-10.loudness-checks-1`.

## Guest review links (Wave 4)

Design and threat model: `docs/design/guest-review-links.md` (binding). This section is the **exact contract** the web ticket
(`T-012`) and the Go API half of `T-011` build on. Code: `ReviewLinksController` (owner),
`ReviewLinksPublicController` (anonymous), `Services/Editing/ReviewLinks/*`, tables `edit_review_links` and
`edit_review_decisions`, columns `edit_timeline_comments.guest_link_id` / `guest_name` (migration `AddGuestReviewLinks`).

### What a link is

A link shows ONE finished render (the **pinned render**), not the live timeline. The owner picks a passed, full-timeline
(no range), non-`Preview`, non-audio `TimelineRender` execution of that timeline that has an `.mp4`/`.m4v` output under the
project's output prefixes (default: the newest). The token is 32 random bytes, base64url, exactly 43 characters of
`[A-Za-z0-9_-]`; only its SHA-256 (of the ASCII token text) is stored, and a link is found by an equality lookup on that
unique `token_hash` column (the app never compares a token). `ReviewLinkTokens` is the one helper (generate, hash,
`IsWellFormed`: a wrong length or alphabet is rejected BEFORE the database and answers the same 404).

A link **stops working immediately** when it is revoked, expires, its timeline is deleted (trash included), the pinned
render is gone or no longer a shareable render, the project or organization is deleted, or its **creator** is deleted, removed
from the workspace or demoted below Edit. Moderation (publish gate) applies when the link is created and on every media read
(identity cache after the first look), fail-closed when moderation is enabled.

### Owner API (authenticated, Bearer; `404` across tenants, `403` for a role without the permission)

Base: `/api/v1/projects/{projectId}/edit-timelines/{id}/review-links`. One `IProjectAccessService` check per action.

| Method | Path | Permission | Success |
|---|---|---|---|
| `POST` | `` | Edit | `201` |
| `GET` | `` | View | `200` |
| `GET` | `/{linkId}` | View | `200` (marks `owner_seen_at = now`) |
| `DELETE` | `/{linkId}` | Edit | `204` (idempotent) |

`POST` body (every field optional; the body itself may be empty):

```json
{ "label": "Client cut v2", "renderExecutionId": "guid", "expiresInDays": 14, "allowComments": true }
```

`label`: normalised plain text, 1..80 characters (else `400`). `expiresInDays`: 1..`MaxExpiryDays` (default 14, else `400`).
`renderExecutionId`: omitted = newest shareable render. Response `201` (the **only** place the URL ever appears):

```json
{ "id": "guid", "url": "https://app.example.com/app/review/g/<43 chars>", "label": "Client cut v2",
  "expiresAt": "2026-11-09T12:00:00Z", "renderCreatedAt": "2026-10-26T11:58:00Z", "stale": false }
```

`url` origin: `OutputLinks:PublicBaseUrl`, else `CloudStorage:PublicBaseUrl`, else a path-only `/app/review/g/<token>`.
`stale` = the timeline's revision is past the render's revision. Errors (all plain `{ "message", "code"? }`):
`409 { "code": "NO_RENDER_TO_SHARE", "message": "Render this timeline first, then share the finished video." }` (also for a
chosen render that is not shareable), `409 { "code": "REVIEW_LINK_LIMIT" }` (more than `MaxLinksPerTimeline` live links),
`422 { "code": "CONTENT_BLOCKED" }` / `409 { "code": "CONTENT_MODERATION_UNAVAILABLE" }` from the publish gate.

`GET …/review-links` (never contains the token, its hash or the URL; newest first, at most 100):

```json
{ "links": [ { "id": "guid", "label": "Client cut v2", "status": "active", "expiresAt": "…", "createdAt": "…",
               "viewCount": 4, "lastViewedAt": "…", "stale": false,
               "decision": { "decision": "approved", "guestName": "Ana", "note": null, "at": "…" },
               "guestComments": 3, "unreadGuestActivity": true } ] }
```

`status`: `active` | `expired` | `revoked`. `decision` is the latest decision or `null`/absent; `decision.decision` is
`"approved"` or `"changesRequested"`. `unreadGuestActivity`: any guest comment or decision newer than `owner_seen_at`
(never opened = all of it). `GET …/{linkId}` returns the same fields plus `allowComments`, `renderCreatedAt` and
`decisions` (newest first, at most 50, same shape as `decision`), and then marks the link seen.

### Public API (anonymous; `/api/v1/review-links/{token}`; no cookies, no CORS headers)

Every response (including every error) carries `Cache-Control: private, no-store`, `Referrer-Policy: no-referrer`,
`X-Content-Type-Options: nosniff`, `X-Robots-Tag: noindex, nofollow`. **Every failure of a bad, unknown, malformed, expired,
revoked or otherwise dead link is one identical `404` with the body `{}` and `Content-Type: application/json`** on all four
routes. Rate limit and cap answers exist only for a live link.

`GET /api/v1/review-links/{token}`:

```json
{ "title": "Client cut", "expiresAt": "…", "allowComments": true, "renderCreatedAt": "…", "stale": false,
  "durationSec": 10.0,
  "comments": [ { "id": "guid", "parentId": null, "authorName": "Ana", "guest": true, "body": "Logo is too small",
                  "frame": 45, "durationFrames": 10, "resolved": false, "createdAt": "…" } ],
  "decision": { "decision": "approved", "guestName": "Ana", "at": "…" } }
```

`durationSec` may be absent/null. `decision` null when none. Comments are ordered by `frame` then time, replies included
(`parentId` = the first comment of the thread). Only notes written through THIS link and replies (any author) to those notes are returned: member top-level notes, other links' comments and other threads never are. Member authors appear by display name only (an address or empty name shows as
`Team member`); no ids, emails, project/org ids or storage keys. A view is counted at most once per minute per link.

`GET /api/v1/review-links/{token}/media`: the pinned render's mp4, `Content-Type: video/mp4`, `Content-Disposition: inline`
(no file name), `Accept-Ranges: bytes`, `206` with `Content-Range` for a `Range` request, `416` when unsatisfiable. The storage
key is read from the database row; nothing in the request selects the file. A non-mp4 output, a missing object or a
moderation refusal is the uniform `404`.

`POST /api/v1/review-links/{token}/comments` (body ≤ 16 KiB):

```json
{ "name": "Ana", "body": "Logo is too small", "frame": 45, "durationFrames": 10, "parentId": null }
```

`201` with the comment object above. `400 { "message", "errors": [...] }` (plain sentences, the first is `message`): name
1..80 and body 1..2000 characters AFTER normalisation (NFC; control, bidi-override, zero-width and other invisible format
characters removed; names are one line with whitespace collapsed, bodies keep paragraphs); `frame` in `0..ceil(durationSec x
fps)+1` (a wide bound when the duration is unknown); `durationFrames` >= 1 and inside the video; `parentId` must be a live
comment of this timeline and a thread's first comment (else `400`); a reply takes its parent's frame and span. `404` (uniform)
also when `allowComments` is false. `409 { "code": "NOTE_LIMIT" }` at `MaxGuestCommentsPerLink` (200) per link or the
timeline's 2000. `429 { "code": "RATE_LIMITED" }` with `Retry-After: 60` above `GuestCommentsPerMinute` (10) writes per link
per minute.

`POST /api/v1/review-links/{token}/decision` (body ≤ 16 KiB): `{ "decision": "approved" | "changesRequested", "name": "Ana", "note": "optional, <= 1000" }`
-> `201 { "decision": "approved", "guestName": "Ana", "note": null, "at": "…" }`. Append-only (the latest row is the state), at
most 20 per link (`409 { "code": "DECISION_LIMIT" }`). `changesRequested` needs a note or at least one guest comment on the
link (`400`). Comments being off does not block a decision. The same per-minute write window as comments applies (`429`).

Guest text is plain text everywhere: stored normalised, rendered by React as text, mailed as plain text, never put in a URL,
header or log line. A guest comment is stored with `author_user_id = 00000000-0000-0000-0000-000000000000`,
`guest_link_id` and `guest_name`.

### Member comment routes

Unchanged. `EditTimelineCommentResponse` gains `guest: bool` and `guestName: string?` (defaults `false`/`null`; `authorName`
holds the guest's name for a guest). Guest comments appear in `GET …/comments`; an Edit member resolves or deletes one as for
any comment; nobody can edit its text (`403`: no user id equals `Guid.Empty`). The UI shows `Name (guest)`.

### Notifications: C# event additions (for the Go API half)

`UserActivityNotificationRequested` (`ReelBolt.Shared.IntegrationEvents`, published to the bus, consumed by the Go API) gains,
additively:

- `Type` value **`"review_activity"`** (`UserActivityNotificationRequested.ReviewActivity`).
- **`RecipientUserId`** (`Guid?`, JSON `recipientUserId`, null for every existing type).

For `review_activity`: `OrganizationId`/`ProjectId` are the link's; `ActorUserId` is null (a guest is nobody's account);
`RecipientUserId` = the link's creator; `Subject` = the timeline name (with ` (label)` appended when the link has a label,
cut to 120); `Details` = one plain line built by the API (never guest text beyond the guest's normalised display name):
`"1 new note from Ana (guest)"`, `"3 new notes, latest from Ana (guest)"`, `"Ana (guest) approved"`, `"Ana (guest) asked for
changes"`. `IUserActivityNotifier.GuestReviewActivityAsync(organizationId, projectId, recipientUserId, timelineName, details)`
publishes it (best-effort, swallowed and logged without any guest text). **Go side (done):** routes a `review_activity` event
to that single recipient (skip when missing or not a member of the organization, never to the actor), map the category to the
fifth preference (default on), and send a plain-text mail (subject "New note on <timeline>" / "<timeline> was approved" built
from `Subject`/`Details` only; escape in any HTML part).

**Coalescing** (`ReviewLinkActivityNotifier`, state in `edit_review_links.last_notified_at`): a comment notifies at most once
per link per 10 minutes, counting the guest notes since the last notification; a decision always notifies unless the link
notified in the last minute. A note that arrives inside the window is not mailed on its own; it is counted by the next
notification and always visible through `unreadGuestActivity`.

### Config, nginx, deletion

`ReviewLinks:DefaultExpiryDays` 14, `MaxExpiryDays` 90, `MaxLinksPerTimeline` 20 (live links), `MaxGuestCommentsPerLink` 200
(database count, holds across replicas), `GuestCommentsPerMinute` 10 (**in-memory sliding window, per API replica**; with N
replicas a link can see up to N times the rate; the nginx write zone is the shared limit). nginx
(`nginx/locations.conf`, `nginx/nginx.conf`): `location ~* ^/api/v1/review-links/` modelled on output links (log format without
the URI, `auth_basic off`, `Authorization` blanked, unbuffered, Range passthrough), zones `review_links` 120r/m burst 60 and
`review_links_write` 10r/m burst 10 keyed only on POST. Deletion: the timeline hard delete / trash purge, the project, the
organization (database cascades, plus explicit removal on the non-relational path) and the account purge (the purged
workspaces' links, decisions and guest comments, and every link the purged user created) remove links, decisions and guest
comments.

### Token hygiene

The public controller has no logger; `SensitiveUrlRedactionProcessor` rewrites `/api/v1/review-links/<token>` to
`/api/v1/review-links/{token}` on every exported span; the nginx access log omits the URI; `UserActivityNotificationMiddleware`
ignores the route. **Do not lower `Logging:LogLevel:Microsoft.AspNetCore` below Warning and do not enable `IncludeScopes`:**
ASP.NET Core's own hosting, routing and MVC logs and scopes carry the raw request path and route values, which are the token
here (a test pins both settings). The owner routes never return the token after creation.

### Tests

`inference/tests/ReelBolt.WorkflowEngine.Tests/ReviewLinks/*` (token helper, text normaliser, owner and public controllers, the
uniform-404 matrix over every route and every dead state, pinned-render-only media, caps and rate window, notifier coalescing,
cascades, member comment regression, telemetry redaction, a real Kestrel pipeline test that asserts the headers and that no log
line carries the token, DI registration), plus the tenant inventory and the account purge tests. No live provider or storage
calls.

### Web UI (T-012)

- **Public page** `web/app/(public)/review/g/[token]/page.tsx` (Server Component: `robots: noindex,nofollow`, `referrer: no-referrer`) renders `components/review-guest/GuestReviewPage.tsx`. `next.config.ts` adds `Referrer-Policy: no-referrer`, `X-Robots-Tag`, `Cache-Control: private, no-store` for `/review/g/:token`. `proxy.ts` admits exactly `^/review/g/[A-Za-z0-9_-]{43}$` without a session (a regex beside the exact set; lookalikes stay protected, pinned in `proxy.test.ts`).
- **All guest requests** go through `lib/editing/review/guest-links.ts` (`fetch` with `credentials: 'omit'`, `referrerPolicy: 'no-referrer'`, `cache: 'no-store'`; no SWR, no auth hooks, no analytics). Any load failure shows one sentence, "This link has expired or was turned off."; a 429 shows "Too many requests. Try again in a minute." Guest strings are only ever React text.
- **Frame rate gap:** the public response has no frame rate, so the page reads and writes comment frames at 30 fps (the editor default). A film at another rate shows markers and timecodes slightly off. Follow-up: add `rate` to the public GET.
- **Member side:** `components/editor/review/ShareForReview.tsx` (button, dialog, link list; Modal, full screen on a phone), used in `MobileReview` and in Deliver under the `FilmCard`; `GuestDecisionBanners` on the review page; `GuestActivityBadge` on hub timeline cards; `ReviewPanel` shows "Name (guest)" and hides Edit for guest comments. `humanizeError` maps `NO_RENDER_TO_SHARE` and `REVIEW_LINK_LIMIT`. The create response URL lives only in the dialog's state; closing the dialog drops it.
- Tests: `proxy.test.ts`, `components/review-guest/__tests__`, `components/editor/review/__tests__/share-for-review.test.tsx`, `lib/editing/review/__tests__/guest-links.test.ts`; e2e `e2e/tests/editor-v2/11-review/guest-link.spec.ts` (needs the real stack).
- User guide: `docs/user-guide/editor/share-for-review.md`.

## Preview 3 s (Wave 4, R10.4)

A blur, key, qualifier or corner pin is render-only, so the viewer can only approximate it. "Preview 3 s" renders three seconds around the playhead through the real render path (the hidden one-step `TimelineRender` workflow), without a delivery render and without touching the document or the project files. The viewer button and overlay (b) are built on this contract.

**Preset `Preview`** (a new closed preset by name, server-only): short side 540 in the timeline's own aspect and never upscaled (1920x1080 gives 960x540, 1080x1920 gives 540x960, 1080x1080 gives 540x540; `TimelineRenderSizing.Dimensions`), H.264 yuv420p, CRF 28, x264 `veryfast`, AAC, loudness off, enabled captions burned in (`captions: BurnIn`) so the preview is truthful. `POST …/render` and `…/render/estimate` refuse it by name (`400`, `preset: reserved`), and output file names starting `preview-` are reserved like `loudness-measure-` and `audition-`.

**Kind is server-set.** The step config carries `audioJobKind: "preview"` (the same server-set field as the loudness/audition jobs), written only by the preview route; no request field can choose it (unknown body members such as `previewJobKind` or `audioJobKind` are ignored). The job registers no project file, never appears in `GET …/renders` (that list filters on the kind) or in `…/audio-jobs`, and counts toward no storage quota. Its workflow is named `Preview: <timeline>`.

**Endpoints** (all under `/api/v1/projects/{projectId}/edit-timelines/{id}`):

| Call | Permission | Body | Success | Errors |
|---|---|---|---|---|
| `POST render/preview` | Edit | `{ "atFrame": 120, "seconds": 3 }` (`seconds` optional, default 3, must be 1 to 3) | `202 { executionId, workflowId, timelineId, revision, preset: "Preview", outputFileName }`. `executionId` is the job id. | `400 { message, errors[] }` (seconds outside 1..3, negative `atFrame`, empty timeline "There is nothing to preview yet. Add a clip first."), `402 { code: "INSUFFICIENT_CREDITS", message, missing }`, `429` plan concurrency (plan-limit body) or `429 { code: "PREVIEW_RATE_LIMITED", message, used, limit, windowMinutes: 60, retryAfterSeconds }`, `404` foreign project or timeline, `403` viewer |
| `POST render/estimate` | Edit | `{ "preview": { "atFrame": 120, "seconds": 3 } }` | `200` the same `EditTimelineRenderEstimateResponse` as a render estimate (`billingEnabled`, `credits`, `creditsLow`, `creditsHigh`, `estimateSeconds`, `balanceCredits`, `affordable`, `unavailableReason`) priced for just the window; billing off answers `billingEnabled: false, affordable: true` ("included") | same `400`s |
| `GET render/preview/{jobId}` | View | none | `200 { executionId, status, errorMessage?, durationSec?, ready }`. `status` is the execution status name: `Queued`, `Running`, `Passed`, `Failed` (others possible, treat any non-`Passed` terminal state as failed). `ready` is true only when `Passed` and the file still exists. A passed job whose file was swept answers `ready: false` with `errorMessage: "This preview has expired. Make a new one."` | `404` for another project, another timeline, an unknown id, or a job that is not a preview |
| `GET render/preview/{jobId}/content` | View | none | `200`/`206` `video/mp4` with HTTP Range (`Accept-Ranges: bytes`), `Cache-Control: private, no-store`, `X-Content-Type-Options: nosniff`. Play it with a `<video src>` pointing at this URL (same-origin, cookie auth); no storage key or URL appears anywhere. | `404` as above, or when not `Passed`/gone |

**The window.** The range is `[atFrame, atFrame + round(seconds * fps))`, clamped to the timeline: a window that would run past the end slides back so it ends at the timeline's end (a preview near the end still shows `seconds` of picture); the revision is pinned to the current one. The price is the 720p-or-below compute tier times the window length (no new tier); a refund follows the engine's failure class exactly as for other renders.

**Gates, in order:** email verified, Edit permission, timeline lookup (404), validation (400), plan gate, credits (402, before anything is written), per-user-per-project hourly cap (429 `PREVIEW_RATE_LIMITED`, `Editing:Preview:MaxPerHourPerUserProject`, default 30, counted from the user's preview executions in the last hour so it holds across replicas; refused attempts count nothing), then the plan's concurrency cap (429) inside the one transaction that creates the workflow and execution. The orphan reaper (`TimelineRenderOrphanReaper`) already covers preview workflows that never got an execution.

**Retention.** `TimelinePreviewSweeper` (every `Editing:Preview:SweepIntervalMinutes`, default 5) deletes a preview's stored files `Editing:Preview:RetentionMinutes` (default 60) after it was queued, and the hidden workflow with its executions once it is also older than the one-hour rate window, so the cap keeps counting until then.

**Client notes for (b).** Poll `GET render/preview/{jobId}` (about every 1.5 s, backing off) until `ready` or a terminal non-`Passed` status; show `errorMessage` through `humanizeError`; `402` shows the shortfall message, `429 PREVIEW_RATE_LIMITED` shows `message` as is. The estimate answers before anything is written.

**The button and the overlay (b).** `Preview3sButton` (`components/editor/viewer/Preview3s.tsx`) reads the price first from the preview estimate (debounced on the playhead) and shows it in the label ("Preview 3 s (about 2 credits)" or "(included)"); a short balance disables it with "You need N more credits for this preview." and nothing is started. It is offered in the viewer header when the selected clip is render-required or approximate by the parity classifier (`offersPreview3s`), in Compose (media clip and adjustment clip) and in Color (the selected clip), never on a phone (no render-only control exists there; the Look sheet has no mask, key, pin, qualifier or stabilise). A click pauses playback and starts the job; `Preview3sOverlay` covers the viewer with progress ("Waiting to start" / "Rendering your preview", Cancel), then a looping `<video controls>` on the content URL with "Close preview" and "Back to editing". 402, 429 and failures show plain sentences through `humanizeError`. The session is one external store (`lib/editing/preview/preview3s-store.ts`, pure reducer in `preview3s.ts`); polling backs off from 1.5 s to 4 s and stops on close or unmount. The overlay never writes the document, and the parity badge stays. Tests: `preview3s.test.ts`, `Preview3s.test.tsx`, e2e `10-grade/preview3s.spec.ts`.

Tests: `EditTimelinePreviewTests` (kind server-set, range clamping, credits and estimate shape, rate limit, tenant 404s, hidden from lists, content with Range, sweeper), the Preview sizes in the same class and a real-ffmpeg test in `TimelineRenderRealFfmpegTests` (`R10_4_…`: 960x540, 540x960, 540x540, h264/aac, duration under 3.1 s).

## Analysis-driven editing and captions (Wave 2, T-014: R09.2-R09.9, R12.9)
- **Empty transcript is never silent.** With no words, the Transcript panel (`TranscriptStart`) shows the real state per source: not analysed -> "Find the words in this video" with the credit estimate (free when billing is off) and then the transcript run; running -> progress text; failed -> the plain reason and a retry; refusals (credits, email, transcript unavailable) in plain words. The recut/review panel does the same when the footage lacks what the helper reads (a 409 `PROPOSAL_NEEDS_ANALYSIS` or the availability `analysisHint`), including `step_result:` refs, and asks again by itself once the analysis is ready.
- **Analyse tab.** One row per media file on the timeline with five kinds: silence, shots, motion and `beats` (all free) and transcript (metered: the estimate is shown first and nothing starts until it is confirmed); the free `beats` kind comes from the per-file analysis API (see [Analysis on demand per media file](#analysis-on-demand-per-media-file-wave-2-r091)). States and failures are plain words; the row polls while working. `lib/editing/api/media-analysis.ts` is the only client of the per-file analysis API; `useTimelineAnalysis(projectId, seed, doc)` fills whatever the seed analysis lacks from the files' ready results, each file with its own source index (so one file's seconds never match another file's clips).
- **Linked cuts (R09.2).** `compileRemoveSilence` also cuts the clips linked to the cut clips (same record spans, one program, one undo) and re-pairs the pieces (`relinkOps`; blades copy `linkId`, so unpaired halves are unlinked). Used by silence removal, word/filler deletion, boring-shot removal and take set-aside.
- **Audition (R09.3).** `EditorWorkspace` holds an audition (`base`, `preview`, `ops`); the viewer plays `preview`, the document is untouched, Apply writes the ops as one step, Cancel or any document change drops it. Offered from Remove silence, Transcript delete/fillers and Takes.
- **Estimated words (R09.4)** (`estimated: true`, shown with a tilde) need a yes before a bulk delete. **Two sources (R09.5)**: matching by project file, asserted by the cuts spec.
- **Captions (R09.6).** Additive fields: `Caption.look` (clean, bold, boxed, outline, spotlight), `Caption.position` and `TitleClip.position` (3x3 pad); see `ledgers/contract-captions.md`. TS (`ops.ts`, `apply.ts`), C# (`Item.cs`, `EditTimelineValidator`), schema and corpus fixtures in lockstep. The panel picks a default look per picture shape, adds a multi-line caption at the playhead, nudges timing, and applies a look to all.
- **SRT/VTT import (R12.9).** Parsed in the browser (`parseSubtitleFile`), previewed, then replace or append on a chosen track with an offset; one undo step.
- **Delivery (R09.7).** Deliver offers burn in / separate files / both; the choice is sent as `captions` on the render request.
- **Speech-aware boring shots (R09.8)** cut only the quiet stretch of a shot; **repeated takes (R09.9)** are found from the transcript, the cleanest is suggested, the others go to a switched-off "Other takes" track.

### Cut to the music

"Cut to the music" turns a music clip's beat grid into ordinary timeline ops. **No new op and no document or contract change:** `compileCutToBeat` (`web/lib/editing/beats/cut-to-beat.ts`) emits only the existing `trim`, `blade`, `move`, `lift`, `addMarker` and `setProperty` ops, running the engine against the evolving document so every op is valid by construction; an op the engine refuses is dropped, never thrown. The dialog's Apply writes the program through `dispatchBatch`, so the whole change is one undo step; Audition plays the result in the viewer first, with Apply/Cancel there, exactly like Remove silence.

**The beat.** The free `beats` analysis is requested per file by `useBeatAnalysis` and read defensively by `readBeatAnalysis`: tempo, beat times, beats per bar, first downbeat, drop, confidence, `steady` and the half/double tempo scores. A step-result render has no file of its own and is reported `unsupported`. `buildBeatGrid` rebuilds the grid in the browser from the analysis and the manual correction, in order: half/as found/double tempo, a nudge between −500 and +500 ms in 10 ms steps, then a bar shift of 0–3 (the "Bar starts on beat" choice 1–4). `gridLines` gives the lines at one unit — `beat`, `twoBeats`, `bar`, `twoBars`, `fourBars` — all aligned to the downbeat; `beatSecondsToRecordFrames` maps them onto record frames inside the music clip only. **The grid is derived, never stored.** `useBeatGridOverlay`/`pickMusicClip` choose the music clip (a selected audio clip, then any selected media clip, then the first audio clip, then any media clip; holds are skipped), and `paint.ts` draws bar lines through the lanes and a tick per beat at the ruler foot. While **Show beats** is on, those frames also join `snapCandidates` through `SnapContext.extraFrames`, so a dragged edge snaps to them.

**The four modes**, compiled live into a plan whose summary line is shown before anything changes:

- **Snap cuts** moves every edit point between adjacent media clips (`a.recordOut === b.recordIn`) on the chosen tracks to the nearest grid frame within the tolerance (6 frames by default), with a `trim roll` on a's out edge. The engine's source-handle allowance is kept; an edit point with no room to move is skipped (`noHandle`), as are points outside the music (`outsideMusic`) and locked tracks. A linked partner on another track with the same cut is rolled by the same delta; a locked partner skips the whole edit point.
- **Split every** blades each chosen clip at every grid frame strictly inside it, right to left (the left half keeps the id); a blade leaving a piece shorter than `minPieceFrames` (6 by default) is skipped (`tooShort`). Same-span linked partners are bladed at the same frames and the touched links are re-paired with `relinkOps`.
- **Beat markers** adds one marker per grid frame inside the music clip: bar lines `#f59f00`, beats `#868e96`, named through the plan's `markerName` callback so the UI can translate them (with none, bar lines are numbered and beats are unnamed). Frames that already hold a marker are skipped, and the plan stops at the cap (`maxMarkers`, default 500, hard maximum 1000).
- **Fit clips to the music** lays the chosen track's clips (record order, music clip excluded) end to end from the music start or the first bar line. Each clip's end is the last grid frame within its length (**Auto**) or 1/2/4 bar lines later; it is `trim out ripple`d to reach it, but only within the clip's source file (`sourceFrames` from the probed media pool), so a clip never lengthens past its source (a clip that cannot reach the unit is lengthened as far as the file allows and reported `shortClip`). With **Stop at the music end**, a clip that could not start before the music's end is lifted (`pastMusicEnd`), and so is every later clip. With **Put the strongest clip on the drop**, the drop is mapped to the nearest grid frame and the clip with the strongest mean motion (`motionRankForClips`, from the free `motion` analysis) starts there; the clips before it fill the run up to the drop (the clip just before it is lengthened by whole grid units within its source to end on the drop), and a pre-drop clip that would start at or past the drop is lifted (`beforeDrop`). When the strongest clip is first, the other clips are laid in order before the drop while they end on or before it, the last is adjusted to end on the drop, then the strongest clip, then the rest; if the drop cannot be reached the gap is left and reported `gapBeforeDrop`. A clip with a partner on a locked track is skipped whole (`locked`), partners are followed by id on the evolving document, and the overlay and snapping use the dialog's music clip and correction (`beat-choice-store.ts`, per project, in memory, reset when the music clip changes). `snapCuts` also never rolls a clip past `sourceFrames`. Every kept clip is then `move`d to its cursor start, once the destination is free. Linked partners get the same trim delta, the same move and the same lift.

Locked tracks are skipped and reported throughout. The UI only starts the beat for a project file; a clip with no file of its own says to use a media clip. The toolbar entry (`CutToBeatButton`, beside Remove silence) opens the dialog and, once the beat is ready, offers **Show beats**; on a phone the same dialog opens full screen from the **Captions** sheet.

User guide: [Cut to the music](user-guide/editor/cut-to-the-music.md).

## Audio cleanup (clean voice and remove a noise)

One HTTP entry point to the `AudioCleanup` step ([video-editing.md](video-editing.md) "Cleaning the sound"). The routes sit beside the
analysis routes under `/api/v1/projects/{projectId}/media/{mediaRef}`; `mediaRef` is `file:<guid>` or `step_result:<guid>` (a project
file or an earlier run's output). A run is a hidden one-step workflow of kind `AudioCleanup` (stored as 6): it never appears in the
workflow list, cannot be edited, deleted or executed through the workflow routes, and cannot be retried (`409
AUDIO_CLEANUP_RETRY_NOT_SUPPORTED`; ask again). Its execution stays readable. An unstarted hidden workflow is deleted by the orphan reaper after 1 h.

| Route | Permission | Answer |
|---|---|---|
| `POST .../cleanup` | Edit | `202 {executionId, target, hosted, state, credits}`; body `{target, allowSourceMediaEgress?, outputKind?}` |
| `POST .../cleanup/estimate` | Edit | `{billingEnabled, credits, seconds, balanceCredits, affordable, cached, unavailableReason, hosted}`; nothing persisted, no vendor call |
| `GET .../cleanup/{executionId}` | View | `{executionId, state, target, method, applied, durationSec, outputFileId, message, failureCode}`; `state` is queued, running, ready, failed or cancelled |

`target` is `VoiceBasic`, `Hum`, `Wind`, `Hiss`, `Echo` (free, local) or `IsolateVoice`, `RemoveMusic` (hosted AI, metered per started audio
minute). `outputFileId` is the registered project file: the editor then switches the clip with `assetRef`. `applied: false` means nothing
was found to remove (no mains hum): the source is unchanged and `outputFileId` is null. No response carries a storage key, bucket or URL.

Flow: estimate, (hosted only) the consent dialog, start, poll until `ready`, switch the clip to `outputFileId`.

**Sizing.** For a hosted target the route probes the source length itself and writes `MaxSourceMinutes = ceil(minutes)` into the step config, so
the engine's credit hold, the estimate and the route's balance check are all `AudioCleanupCreditPricing.TryStepHoldMillisAsync` of the same
config: a 4 min 10 s source holds and estimates 5 minutes' price. A source over `Cleanup:Hosted:MaxSourceMinutes` (default 30) is refused before
anything is queued or uploaded. A local target has its own, larger cap: a source over `Cleanup:Local:MaxSourceMinutes` (default 60) is refused with
413 `AUDIO_SOURCE_TOO_LONG` before anything is queued (the step enforces the same cap again, from the probed length).

**Consent.** A hosted target sends the source's audio to a hosted model: the body must carry `allowSourceMediaEgress: true` (the web consent
dialog sets it; the assistant never turns it on). A local target ignores the flag.

**Run rate and concurrency.** Hosted targets count in the paid hourly run bucket, local ones in the free bucket (`429 RUN_RATE_LIMITED`); the plan's concurrent-run cap answers its own plan refusal.

| Status | Code | When |
|---|---|---|
| 400 | `INVALID_TARGET`, `INVALID_OUTPUT_KIND`, `NOT_AUDIO_OR_VIDEO`, `INVALID_REQUEST` | unknown target or output kind, a file that is not audio or video |
| 400 | `EGRESS_REFUSED` | hosted target without `allowSourceMediaEgress: true` |
| 402 | `PLAN_NOT_ENTITLED` (feature `audio_cleanup`) | the plan does not include hosted cleanup; checked before the consent and any upload |
| 402 | `INSUFFICIENT_CREDITS` | the balance cannot cover the hold; checked before anything is queued |
| 404 | none | foreign project, missing, quarantined or foreign media, unknown run |
| 409 | `SOURCE_NOT_MEASURED`, `CLEANUP_UNAVAILABLE` | hosted: length not probed yet; no priced provider |
| 413 | `SOURCE_TOO_LONG` (hosted), `AUDIO_SOURCE_TOO_LONG` (local) | source over the cap of its target |
| 429 | `RUN_RATE_LIMITED` | hourly run limit |

## Source orientation (phone rotation hotfix)

Phone clips are often stored landscape (for example hevc 3840x2160) with a container Display Matrix (rotate 90/-90/180, possibly mirrored). The engine already honoured it: ffprobe sizes are swapped (`FfprobeOutputParser`, #772), every engine decode autorotates, and the derivatives are re-encoded upright with no matrix. The viewer did not: `mediabunny-reader.ts` returned `VideoSample.toVideoFrame()`, which drops mediabunny's `rotation`/`flip`. It now draws a rotated or flipped sample once into an `ImageBitmap` (`planOrientation`, folded into the `scaleToWidth` downscale), so the compositor, the fit and everything downstream see display-oriented frames. Unrotated sources keep the raw `VideoFrame` path byte for byte. No document migration: existing timelines show correctly.

`MediaClip.sourceRotation` (0, 90, 180 or 270 clockwise, absent = identity) is an extra quarter turn on top of the display orientation, applied BEFORE crop and fit, so width and height swap. It is different from `transform.rotation`, which turns the picture after the fit. It is set with `setProperty`, with no new op (still 34). Contract fixtures: `source-rotation-*.json`.
- **Export** (`TransformFilterBuilder.SourceRotationFilter`): `transpose=1`, `transpose=2` or `hflip,vflip` right after the leg and before crop, fit and reframe, on both the P4 and the depth path. Region effects and the segmentation matte run on the leg before it, so they stay aligned. The region cache key carries `srot:` when the clip is turned. A clip at 0 gives byte-identical argv.
- **Viewer**: the rotation goes into the asset key (`fps@deg|url`, only when the clip is turned) and the worker `open` message, which is read as `read(ts, scaleToWidth, extraRotation)`. A turn therefore opens a fresh lane and texture. `sourceSizeFor` swaps the size for 90 and 270. A segmentation mask on a turned clip is not drawn in the preview, and `parity.ts` marks it `renderRequired`. The render is correct.
- **UI**: **Rotate 90°** in the Inspector and in the phone clip sheet cycles 0, 90, 180, 270, 0 (0 clears). Each press is one undo step.
- **Not covered**: follow and reframe points on a turned clip refer to the turned picture. A follow made before the clip was turned has to be made again.
- **Tests**: `SourceRotationTests` uses real ffmpeg on a generated rotate=90 clip, upright and +90, with portrait size and marker position asserted. `mediabunny-rotation.test.ts` parses the committed `__fixtures__/rotate-90.mp4` (portrait 96x160) and tests the orientation plan.

## Direct transform on the viewer (Wave 2, R07.2)

> Updated by editor-wysiwyg: the pause-only rule below no longer holds. The handles are the **Transform tool**
> (`Shift+T`, on by default): they show whenever the playhead is on the selected item, a click on the picture
> selects, and a drag during playback pauses first. Corner drag is free (`scaleY`), Shift keeps the aspect, titles
> get the full box. See [The Transform tool on the viewer](#the-transform-tool-on-the-viewer). Where the text
> below says "mounted only while paused", read "while paused the handles are live; while playing only the outline shows".

No new op and no new document field. `web/lib/editing/viewer-transform/` is pure geometry plus the draft: `geometry.ts` works in canvas pixels (`baseSize`, `frameFor`, `scaleFromCorner`, `rotateTo`, `snapMove`/`snapTargets`, `fitValues`/`fillValues`/`padValues`/`cornerPresetValues`; a `Frame` plus `HandleSpec[]` is the generic handle model that crop, reframe and corner-pin handles reuse), `draft.ts` holds the live gesture (`draftFor(store)`), `transformOps` and `commitTransform`. A gesture never writes the document while it runs: the draft is merged into the PREVIEW document (`getPreviewDoc` in `EditorWorkspace`, re-presented once per animation frame) and one `dispatchBatch` is written on release, so one gesture is one undo step. Channels that already carry keyframes get a `setKeyframe` at the playhead (interp `linear`); the others share one `setProperty transform` with explicit `{value, keys: null}` channels, so a preset stores exactly its number. `components/editor/viewer/TransformOverlay.tsx` is its own DOM layer (mounted only while paused, so playback does zero extra work) plus `TransformTools` (the Position menu in the viewer header). Handles are 44 px on `(pointer: coarse)`. The inspector reads the numbers through `useTransformMirror(store, itemId)` (`draft.ts`), which follows a drag live. Position is a fraction of the canvas, scale is relative to the fitted picture (source size from the pool probe and the timeline aspect policy), rotation is degrees clockwise. Specs: `e2e/tests/editor-v2/20-viewer-transform/`.

## On-canvas mask and reframe handles (Wave 4, R10.3)

"Blur the logo / face" and "keep the subject in the vertical crop" are done by dragging on the picture. No new op and no new document field: the handles write the existing `mask`, `reframe` and grade-node `window` properties. **Add mask** (rectangle or ellipse) and **Add reframe** are in the timeline clip menu and in the Inspector's **Mask and reframe** section; either one writes the existing value, selects the clip, pauses and activates the tool without a forced page change. The Inspector and Compose share `panels/MaskReframeFields.tsx` and the same one-batch `setProperty` commit (see [Pages and panel geometry](#pages-and-panel-geometry)).

- **Geometry** (`lib/editing/viewer-transform/frame-handles.ts`, pure, unit-tested): a shape is a `Frame` (rotation 0) in canvas pixels, converted from/to canvas fractions (`boxToFrame`/`frameToBox`); `BOX_HANDLES` is the transform layer's four corners plus four edges (the ellipse uses the same bounding-box handles); `resizeFrame` (opposite side fixed, Shift keeps the aspect, Alt grows from the centre, clamped to the canvas, 2% minimum), `moveFrameBy`, `nudgeFrame`, the feather (`featherInsetPx` = feather of the shorter canvas side, at most half the shape; `featherHandlePoint`/`featherFromPointer`), and `reframeWindow` (the kept part of the picture, canvas aspect, centred on the point).
- **Draft model**: `frameDraftFor(store)` holds the live gesture (`mask` / `reframe` / `window`). It is merged into the PREVIEW document by `applyFrameDraft` (`getPreviewDoc` in `EditorWorkspace`, after the transform draft) and into the Compose and Colour inspectors' `doc` (`LiveComposeWorkspace`, `LiveColorWorkspace`), so their sliders and fields follow a drag live. Release writes ONE `dispatchBatch` (`maskOps`, `reframeOps`, `windowOps`; a no-op drag writes nothing), so one drag is one undo step.
- **Components** (`components/editor/viewer/`): `OverlayFrameHandles` is the one generic layer (body drag, eight handles, dashed inner outline with one feather handle, or a single crosshair for a point); `FrameHandlesOverlay` mounts it for each target of the selected clip (`resolveFrameTargets`) while paused, and keeps a shape tool's handles on the picture during playback through the live playhead (§7.3 of the design); `FrameTools` puts **Invert mask** in the viewer header next to Position. Pointer events (mouse and touch), `touch-action: none`, handles 44 px on `(pointer: coarse)`, every handle is a focusable button with a name, arrow keys nudge 1% (Shift 10%) and, on a handle, resize by the same step.
- **Reframe**: the Compose switch is now called "Reframe" (it was "Auto-reframe"; a guard test keeps the old name out of `web/` and `docs/user-guide`). The green cross on the picture sets the subject point; while it is dragged or focused the picture outside the kept window is dimmed. The helper copy says it holds a fixed point (following a moving subject is [Follow the subject](#follow-the-subject), a separate Inspector section that writes transform keyframes). Reframe is drawn in the viewer since editor-wysiwyg (approximate: GPU sampling).
- **Power window (P9-GRADE-02)**: the Colour page publishes the selected node's window (`windowTargetFor(store)`) when the node has one, and the Secondaries tab's **Edit window on the picture** button sets `viewerTool = 'window'`. Selecting a node does not activate the tool by itself. Only the shape (x, y, w, h, feather) is rewritten; the offsets and tracking state are untouched, and an untracked static offset is subtracted so the window stays where it is drawn. A tracked window (keyed offsets) shows read-only at its tracked position with "Tracked windows follow the picture. Turn off tracking to move this by hand." A paused node with a window still shows its handles without the button.
- **Phone**: the Look sheet offers no mask or reframe control, but the paused auto-show is unchanged: a mask or reframe made on a desktop keeps its draggable 44 px handles in the phone's mini viewer while paused. The tool is not a prerequisite there.
- Specs: `e2e/tests/editor-v2/10-grade/handles.spec.ts` (desktop; the touch row is skipped with that reason). Unit: `viewer-transform/__tests__/frame-handles.test.ts`, `viewer/__tests__/FrameHandlesOverlay.test.tsx`, `__tests__/reframe-rename.test.ts`.

## Pick an object

One way to name an object for the Follow and Objects sections: point at it on the picture. It adds no op and no document field; the pick is a request value (`seed`, see [A tap names the object](video-editing.md#a-tap-names-the-object-seeded-segmentation)) and the results are the same transform keys and `regionEffects` entries as before.

- **Tap or drag.** In the viewer's pick layer (`FollowDrawLayer.tsx`) a tap sets a point; a drag draws a box. A tap alone gets a box of 20% of the frame per side around it. A box must be at least 1% of the frame each way. Fractions are of the source frame and the time is a second of the source (`web/lib/editing/selection/seed.ts`).
- **Keyboard.** The marker takes the arrow keys (1% of the frame per press), Enter to confirm (**Use this**) and Escape to cancel.
- **Phone.** The phone clip sheet offers the same pick through the Follow and Objects sections.
- **Follow.** The section's **Pick on the picture** replaces drawing a box and sends the box (drawn or laid) to the subject-tracks route.
- **Objects.** **Pick on the picture** sends the seed with the estimate and the request; **Find by name** goes back to the words; **Pick again** and **Clear** change the pick. The price line and consent work as before. When nothing is under the tap the section shows the `OBJECT_NOT_FOUND_AT_POINT` sentence with **Pick again**; after the hourly cap the usual run-rate refusal shows.

## Follow the subject

Keeps one subject framed by writing ordinary `transform` keyframes on a clip. No new op and no new document field: the result is `transform.positionX`, `positionY` and `scale` keyframes, so the existing transform, keyframe curve, undo and render paths apply unchanged. The compile-side counterpart, which expands into reframes instead, is [Following a subject](video-editing.md#following-a-subject).

**Routes** (a subject-tracking pair, then the follow; the follow needs `Edit`; errors are `{ code, message }`).

| Route | Body | Answer |
| --- | --- | --- |
| `POST /api/v1/projects/{projectId}/media/{mediaRef}/subject-tracks` | `{ atSec, box: { x, y, w, h } }` (the box in source-frame fractions, drawn at `atSec`) | `{ executionId, status }`. An unverified address is refused (`403`). It is priced as compute only (no model call); when billing is on it has a credit preflight and answers `402 INSUFFICIENT_CREDITS` if the balance does not cover it. |
| `GET /api/v1/projects/{projectId}/media/{mediaRef}/subject-tracks/{executionId}` | none (`View` permission) | `{ executionId, status, failureCode, message, analysisStepResultId, track }` with `status` of `queued`, `running`, `succeeded` or `failed`; `track` is `{ trackId, startSec, endSec, boxes: [{ t, x, y, w, h }] }`. |
| `POST /api/v1/projects/{projectId}/edit-timelines/{timelineId}/follow` | `{ itemId, revision?, track: { analysisStepResultId, trackId }, tightness?, smoothness?, onLost?, allowSoftZoom? }`; the words are the names in [Following a subject](video-editing.md#following-a-subject), defaults `Medium`, `Normal`, `Hold`, `false` | `{ revision, itemId, keys: [{ sourceSec, positionX, positionY, scale }], assumed: { canvasWidth, canvasHeight, sourceWidth, sourceHeight }, zoomCap, zoomCapLimited, lostSpans, clampCount, warnings }`. It reads the timeline at `revision` (its snapshot) or the latest, and writes nothing. |

`trackId` is `r{n}` (the free tracker, which the first route produces, or an Objects result) or `g{n}` (segmentation, which can cost credits when the Objects section finds it); both are numbered from 0. The path comes from the same core as the compile, so it also keeps a subject that fits the visible window inside it (see [Following a subject](video-editing.md#following-a-subject), "Keeping the subject in the picture"). Codes:

- Subject tracking: `FOLLOW_BOX_INVALID` (`400`), `FOLLOW_TIME_INVALID` (`400`), `FOLLOW_SOURCE_TOO_LONG` (`422`, a source over 1 hour); a run that found nothing ends `failed` with `failureCode` `FOLLOW_SUBJECT_NOT_FOUND`.
- Follow: `FOLLOW_REQUEST_INVALID` (`400`), `FOLLOW_TIMELINE_UNREADABLE`, `FOLLOW_TRACK_EMPTY`, `FOLLOW_TRACK_OTHER_SOURCE`, `FOLLOW_SIZE_UNKNOWN`, `FOLLOW_RANGE_INVALID`, `FOLLOW_RANGE_TOO_LONG` (a clip over 1 hour), `FOLLOW_FIT_UNSUPPORTED`, `FOLLOW_TRACK_TOO_LARGE` (all `422`); `FOLLOW_CLIP_NOT_FOUND`, `FOLLOW_TRACK_NOT_FOUND` (`404`); `FOLLOW_TRACK_UNREADABLE` (`503`, try again); `FOLLOW_CLIP_CONFLICT` and `FOLLOW_CLIP_SPEED` (`409`).

**Refusals.** A clip that is cropped, already has auto reframe, or is stabilized answers `FOLLOW_CLIP_CONFLICT`; a freeze frame or a speed that is not a plain constant answers `FOLLOW_CLIP_SPEED`. The user removes the conflicting setting first.

**Source time, mapped at apply time.** Each key carries `sourceSec`, a second of the source, not a timeline frame. `web/lib/editing/follow/map.ts` (`buildFollowTransform`) maps it onto the clip through its in-point, constant speed and direction (`timelineFrameForSource`) when the result is applied, so the keys land on the clip as it is now. The Inspector's Follow section (`components/editor/panels/FollowSection.tsx`, also on the phone clip sheet; drawing the box is desktop only) writes the whole `transform` value in one `setProperty`, so one Undo takes it back, and **Remove follow** clears it in one more.

**Stale result.** The web keeps a snapshot of the clip (asset, speed, reverse, hold, crop, reframe, stabilize) and the canvas size when it asks. `isFollowStale` is true when any of those changed, the clip became a freeze frame, or the canvas differs from `assumed`; the section then offers **Run again** instead of applying.

**Cover framing.** A follow frames by cover, not contain. With the renderer's contain factor `s0 = min(cw/sw, ch/sh)` and the cover factor `max(cw/(sw*s0), ch/(sh*s0))`, a key at zoom `z` becomes `scale = z * cover` and `position = (0.5 - centre) * sourceSize * scale * s0 / canvasSize` on each axis. `EditorFollowTransform.ToTransform` clamps `scale` to the editor's range (above 0, at most 8) and the positions to +-2, counts those clamps in `clampCount` and adds a warning; the zoom limit passed to the path is the lower of the 8 ceiling divided by the cover factor and the zoom at which a contained centre would need a position beyond +-2 (`EditorFollowFit.MaxZoomLimit`), so those clamps are rare.

**Soft zoom in the editor.** `allowSoftZoom` raises the zoom cap to at least 4x, but in the editor the zoom also stops where the picture would leave the canvas, so it can end lower: the cap keeps `scale` within 8 and every contained centre within the `positionX`/`positionY` range of -2..2 (`EditorFollowFit.MaxZoomLimit`); for a wide clip in a tall frame that is about 1.6x. `zoomCapLimited` says when a cap held the zoom back.

## Export scope, safe preferences, layouts, presets, offline relink (Wave 2, T-019: R12.1 / R12.4 / R12.5 / R12.7 / R12.8)

- **Export panel** (`deliver/ExportPanel.tsx`): the five download links keep their `deliver-export-<format>` ids; under them the
  chosen format's `GET …/export/scope?format=` lists (carries / dropped / `mediaMode`) are shown with a client-side "Hand off"
  count (`lib/editing/interchange/hand-off.ts`). FCPXML has Include audio / Name audio roles switches that become `includeAudio` / `roles` on the link.
- **Safe save** (`lib/editing/prefs/safe-save.ts`): saves are serialized; a save reads the stored copy first and, if its `updatedAt`
  moved since this tab loaded, applies only its own changes on top (`mergePreferences`) instead of replacing; the replaced copy is kept in
  localStorage (`reelbolt:editor:prefs-history`, last 5) for "Restore previous settings". Decision: no server version column (the
  backend row is replace-only); history is per device. Failures surface through `humanizeError` in the dialog.
- **Layouts as fractions**: `SavedLayout.fractions` (left/right of width, timeline of height) next to the legacy pixel `sizes`;
  `PanelLayout` takes `appliedFractions` and `resetToken` ("Reset layout").
- **Presets**: `presets/PresetManager.tsx` (apply preview, rename = create + delete, duplicate = create "X copy", delete with confirm);
  the server validators run on every create.
- **Offline relink** (`interchange/OfflineRelink.tsx`, `lib/editing/interchange/relink.ts`): derived from markers named `Offline: <name>`;
  Relink = `overwrite` of the picked file at the marker's frame (cut to the hole) + `removeMarker`, one `dispatchBatch` (one undo step).

## Picture editor, Wave 2 behaviour

- **Undo leaves no ghost layers.** Undoing an added layer (an AI result, a cut-out, text, a merge or a crop) takes it out of
  the layers list, the canvas and every export; redo puts the same layer back. The server has no un-delete, so the row stays
  on the server, hidden, while the step can still be redone, and is **deleted for good** as soon as it cannot be (a new edit
  drops the redo steps, or the page is left). `diffDocuments` hides a layer only the server has; `retainedLayerIds` (history.ts)
  says which rows an undo/redo step still needs.
- **Delete key** (Delete or Backspace, not while typing, not in a dialog, not on a locked layer) removes the selected or ticked
  layers as one undo step (`layersRemoved`); the toolbar delete button still asks first and is permanent.
- **Position and size** (Layers tab): Left, Top, Width, Height in canvas pixels; width/height keep proportions (uniform scale).
  **Line up** (to the picture, or to each other for 2+ ticked layers), **Spread out** (3+), all by the layer's visible (rotated)
  box, each one undo step (`lib/editor/arrange.ts`). "Choose several layers" shows tick boxes in the list.
- **Crop** edits the layer in place: it sets the layer's `crop` window (see "Layer crop") and moves the layer so the kept part
  does not shift, as one `patchLayer` undo step (`useArrange.crop`, `lib/editor/crop.ts`). The Crop window opens on the whole
  stored picture with the current window selected, so a crop can be widened or removed ("Remove crop"). Handles are 44 px on
  touch.
- **Merge** rasterises the ticked visible layers (crops, opacity and blend baked in) client-side into ONE new immutable layer
  (project file + `layers/from-file`, no new endpoint) placed where the topmost one was, and the originals leave the list in the
  same undo step (`layersReplaced`). The originals stay on the server, hidden, while Undo can bring them back, and are deleted
  for good once it cannot (the "no ghost layers" rule above), so no pixels are lost early. The merged file goes into a project
  folder named "Picture working copies" (the media API has no hidden location).
- **Result bar** after a paid run: what changed, credits charged (or "No credits were charged"), Compare with before (display
  only), Undo, Try again (with the estimate; not offered for painted-area tools), and for a cut-out with several options the
  first shows and the rest are hidden, with Keep this one / Keep all as layers. The vendor "Last run" line is gone.
- **Include / leave out** for cut-outs is the "Cut this out / Leave this out" switch in the Subject panel (Shift-click remains a shortcut).
- **Timeouts**: the run spinner says it can take up to 90 seconds. After a 424, a gateway answer or a dropped connection the
  editor reads the document again; a layer that landed late is brought in as one undo step with a plain message, and nothing is run twice.
- **Duplicate** in the Pictures list menu calls `POST /api/v1/projects/{pid}/image-documents/{id}/duplicate`.

## Picture editor on a phone (Wave 3, RM.14)

- **Shell** (`ThumbnailEditor`, device class `phone` from `useDeviceClass`): the tools stay a row above the stage; the stage is pinned
  at 45 dvh and **ends above an open sheet** (`phone-shell.ts` `phoneStageHeight`, floor 160 px) so the picture stays visible and
  reactive. A dock (Layers, Text, AI, Canvas) opens the single `panelTabs` content in the shared `BottomSheet` (peek / half / full,
  back and Escape close it, the soft keyboard refits it); in landscape the sheet is the right drawer and the tools run down the left.
  Export stays in the compact header. Picture state lives in the editor, so changing the detent loses nothing. Desktop and tablet
  keep the side panel unchanged.
- **Touch intent** (`touch-intent.ts`, T7): on a finger, the Text, Brush and Cut-out tools wait 90 ms before they fire; a second
  finger inside that window cancels them (a pinch never paints, erases or adds text), and lifting the finger first counts as a tap.
  Two fingers pan and zoom the stage. A mouse or pen fires at once.
- **Layer rows** (`LayersPanel` `menuRows`, phone only): 44 px rows with the eye, the lock, the name and one overflow menu (Bring
  forward, Send back, Bring to front, Send to back, then a divider and Delete layer), so Delete is never beside an arrow (T15). A
  move from the menu shows an Undo snackbar. Desktop rows keep their arrow and delete buttons.
- **Nudge pad** (`ArrangePanel` `onNudge`): four arrow buttons with a 1 px / 10 px switch move the selected layer through the same
  `patchLayer` command as the arrow keys (merged into one undo step per run). Align uses the existing Line up buttons.


**Text basics and AI tools as sheets (T-011: R13.8, R14.2).** On a phone the Text sheet (`TextPanel` with `phone`) shows the basics first - words,
font, size, colour (the phone's own colour picker), ALL CAPITALS and one "Outline and shadow" switch with a strength - and keeps weight, alignment,
line height, letter spacing, wrap, outline colour and shadow detail under **More**. New text has outline and shadow on so it stays readable; the
switch says so, and turning it off stores no outline and no shadow. Desktop keeps the full panel. The picture sheet is `keepMounted` (a closed
`BottomSheet` is hidden, not unmounted), so a prompt, brush setting or mark typed into an AI tool survives closing and re-opening it. The AI tool
Run button (and its credit sentences) sit in the sheet's pinned footer (`footerRef`, `PinnedRun`) so brush, marks and Run share one screen, and the
button carries its price: "Make the change, about 3 credits" (`runLabel`; no price when billing is off or unaffordable). The cut-out tool's
"Cut this out / Leave this out" switch replaces Shift-click on touch. The assistant full-height phone sheet lives in the app shell and is not part of this change.
## Dropdowns and menus (popover placement)

All Mantine popovers (Select, Menu, Combobox, Popover) use the `fixed` floating strategy (`web/app/theme.ts`): the Select scrolls its
selected option into view as it opens, and with an absolutely positioned dropdown that scrolled the window on a tall page, pushed the trigger
off screen and hid the list (the phone workflow "Step Type" Select). Mantine's default padding is kept everywhere (shift 5 px per side, flip 0);
only the editor routes (`EditorPopoverTheme`, wrapped in `app/(app)/editor/layout.tsx`) add a 96 px bottom padding so lists flip upward instead
of landing under the status bar and phone dock. Guarded by `e2e/tests/editor-v2/17-popover-blast`.

## Visual system sweep (Wave 2, T-026: VF4-VF7, VF13-VF15, VF18)

- **Primitives** (`web/components/editor/ui`): `EditorIcon` (decorative glyph, named glyph, or named icon button with tooltip; one stroke weight; sizes sm 14 / md 16 / lg 20; `dense` for fixed-height rows), `EditorPanel` (header with actions over a body; `scroll={false}` for panes whose parent scrolls) and `EditorEmpty` (icon, plain sentence, one primary action; `compact` inline). Workspaces (Mixer, Compose, Color, Cut, Deliver) use `EditorPanel`.
- **Density tokens** (set by `EditorSurface` as `data-ed-density` / `data-ed-touch`, read in `app/global.css`): `--rb-control-h` 28 compact / 36 comfortable / 44 touch, `--rb-icon-h` 28 / 32 / 44, `--rb-row-h` 32 / 40 / 48, `--rb-icon-dense-h` 24 (28 touch). A coarse pointer always gets the touch values. Header "Display settings" menu chooses Comfortable or Compact; stored per user on the device (`reelbolt:editor:display:<user>`, validated field by field, additive).
- **Always dark surround** (same menu): keeps the editor in the dark scheme while the app is light. The person's own scheme is kept under `reelbolt:editor:scheme-before-dark` and restored on "Match the app" or when the editor closes. Phones: "Editor always dark" in the header More menu.
- **Tabs**: panel tab rows never clip a label: full labels, then icon-only tabs (active keeps its label, all keep names and tooltips), then a More menu (`panel-tabs-more`).
- **Accessibility**: `--rb-text-dim` (AA) replaces Mantine's dimmed colour inside the editor; every select/field has a name; slider names live on the thumb; targets are at least 24 px (icon buttons, slider thumbs, checkboxes). `e2e/tests/editor-v2/20-visual` holds the layout-collision, axe (no serious/critical, WCAG 2.2 AA tags), tab-overflow and display-pref specs.
- Mantine is v9 (CLAUDE.md already says so; no v8 mention was left to fix).

## Phone Deliver and share (Wave 3, T-006: RM.9 / R06.8)

The header Export on a phone opens a full-height sheet made for a thumb. It is the same `DeliverWorkspace` (state, estimate, queue, stream) with a phone layout, not a fork; desktop is unchanged.

- **Layout** (`deliver/DeliverWorkspace.tsx`, `phone` branch): a scrolling body and a sticky footer (`deliver-sticky-footer`) that holds the price line and **Render**. `RenderSettings` portals its Render row into the footer (`footerSlot`), so Render is visible without scrolling at 360x640. Controls are 44 px.
- **Formats**: a compact picker with the vertical film first (`phoneFormatOrder`), then Square, Portrait 4:5, Full HD, 4K, Master, Audio only. Vertical is the default on a phone. Range, file name, caption delivery (burn in / separate files only / both), SRT/WebVTT downloads and loudness live under **More options**. "None" is not a separate choice: "Separate files only" renders without burned words.
- **Price before Render**: `render/estimate` text in the footer. A 402 from the estimate is shown in plain words (credits missing, or the plan does not include it) instead of staying silent.
- **Light preview**: while the sheet is open the viewer slot shows `DeliverPreview` (no WebGL compositor). A visible text line with an icon says the preview is paused and the film is drawn fresh (text, never colour alone).
- **Queue**: cards with progress, Stop, and errors in plain words. Phone tabs suspend in the background, so on `visibilitychange` to visible the queue is refetched (`useForegroundRefresh` in `PhoneEditor`) and the render event stream is reopened (`useExecutionStream({ reconnectKey })`, additive option).
- **Completion**: `useRenderCompletion` announces a render that finished after the first list was seen with an in-app toast, and with a green **Ready** badge (icon and word) on the header Export button when the sheet was closed. Opening Export clears it. No email (left out); `document.title` flash not done.
- **Finished film**: inline player (`playsinline`), **Download** with the real file name, and **Share** (`shareModeOf`): the file itself when `navigator.canShare({ files })`, else the project-file link through `navigator.share({ url })`, else no button.
- Tests: `deliver/__tests__/deliver-phone.test.tsx`; `e2e/tests/editor-v2/30-phone/deliver.spec.ts`.

## Deliver page (pro UI wave 5)

Wave 5 of `docs/design/editor-pro-ui-alignment.md` (§5.8, §9.1, §13) fills the Deliver page whose frame Wave 1 put in place (settings 420 | result, no timeline). It is a web-only change: no renderer, billing, route or document-format change, no new op, no new keymap action. Every figure on the page comes from an existing endpoint. The phone sheet ("Phone Deliver and share") keeps its layout: `RenderSettings` and `ExportPanel` branch on the device class and the phone branch is the previous markup.

**Settings column** (`deliver/RenderSettings.tsx`, three `EditorPanel`s in a flow):

- **Presets** (`deliver/PresetList.tsx`, model in `deliver/render-presets.ts`). The seven render presets the API takes by NAME (`Hd1080p16x9`, `Uhd4k16x9`, `Vertical9x16`, `Square1x1`, `Portrait4x5`, `Master`, `AudioOnly`) are a single-choice list (`role="listbox"`, rows `role="option"`, test id `render-preset` on the list and `render-preset-<Name>` per row; arrow keys, Home and End move the choice). Each row says its size, its shape and what it is for. The list is never folded.
- **Plan cap before the click.** `presetAvailability(preset, masterShortSide, maxOutputHeight)` answers `ok`, `clamped` or `refused` from the plan data the page already has (`useOutputHeightCap`). Only the explicit 4K preset can be `refused` (the server answers `402 PLAN_NOT_ENTITLED` feature `resolution` for it): its row is `aria-disabled`, says "Not in your plan" and cannot be chosen. A shape preset or `Master` above the cap is `clamped`: the row, the statement and the Render label all say the size it will come out at, because the engine scales it down. The server stays the authority.
- **My presets** (`deliver/MyPresets.tsx`). The existing preset system (`/edit-timelines/presets`) has the kinds `transition`, `effect` and `mastering` only, so a person's own render presets are a per-device store: `localStorage` key `reelbolt:editor:deliver:my-presets`, at most 20, names 1..40 characters. A preset holds the format, quality, rate control and bitrates, encoder and loudness settings (`RenderFormSettings`); the range, the file name and the captions choice belong to one render and are not saved. Save, apply, rename and delete (with a confirm). `sanitizeFormSettings` puts every stored word back on its allowlist and every number back in the server's range, so a stored entry can never send a value the form could not. Unreadable or refused storage is a soft failure with one plain sentence.
- **Options.** Quality, range, file name and the captions choice (`CaptionDelivery`, moved here from the result column) are visible; "Size and quality" and "Advanced" stay folds of secondary parameters. The range switch names the live chords of `markIn` / `markOut` (`useLiveChord`).
- **Before you render** (`deliver/CostStatement.tsx`, sticky at the foot of the column). One statement above Render: format and size, range in timecode (`framesToTimecode`), captions mode, file size when the estimate has one, and the cost. `DeliverWorkspace` tags each answer of `POST …/render/estimate` with the settings it is for, so the statement has six states (`CostState`): `idle` (read-only, nothing asked), `loading` (the settings changed; a stale figure is never shown beside new settings), `billingOff` ("Ready to render.", no cost line), `ready` (the estimate line, the low to high range, the balance), `refused` (a 402 from the estimate in plain words, with the way to credits or plans) and `error` ("Try again" re-asks). The client computes no price.
- **Render.** The label names the action (`renderLabel.<Preset>`, or the clamped size). When it is off a line says why, first reason first: read-only, nothing on the timeline (with "Go to Edit" and its live `pageEdit` chord), plan refuses the size, file name, save failed (`render-blocked`), not enough credits (`render-need-credits`), estimate refused.

**Result column** (`DeliverWorkspace`, `Deliver.module.css` `.resultGrid`: two groups that sit side by side when there is room), each block under an `EditorPanel` header:

1. **Latest film** (`FilmCard`; Delete is hidden on a read-only timeline).
2. **Render queue** (`RenderQueue`, moved out of the settings column). Rows carry format, length, size and when; progress and Stop while running; a plain reason, Try again and Remove when failed; the caption sidecar files a render made (not repeated for the film shown above); and the notes from `RenderNotes`. The empty state is the page sentence plus one action ("Choose a preset", which focuses the list); loading says so.
3. **Share for review** (`review/ShareForReview.tsx` `ReviewLinksBody`, the body the dialog also uses). Inline on the page: a labelled button opens the form, the film on screen is pre-selected (`renderExecutionId`), the URL is shown once and dropped on Done, the list never carries a URL or token, revoke asks first. Read-only shows the list only. On this page a link is marked as seen by an explicit button, because the list is always on screen.
4. **Export for other apps** (`ExportPanel` `layout="page"`). The file type is chosen first, `GET …/export/scope?format=` (carries, dropped, media sentence) is read next, and the download of that type follows it; the other four types stay as links under it. Test ids `export-scope-*` and `deliver-export-<format>` are unchanged.
5. **Hand-off package** (`HandOffCard heading={false}`), no longer nested in the export panel on this page.

**Render again.** `GET …/renders` rows carry only the preset and the file name, which is not enough to rebuild a render. `DeliverWorkspace` therefore remembers what each render it started was sent with (`reelbolt:editor:deliver:sent-renders`, newest 40: form settings, range, file name, captions). A row whose execution id is remembered offers **Render again**, which pre-fills the form (and the marks, through `onSetInOut`) and starts nothing, so the statement is read first. A row from another device has no such action: it is omitted rather than guessed. A server-side record of a render's request would lift this limit; that would be a server change and was not made.

**Left out.** No queue error state (the page receives `renders` and `rendersLoading` only); Stop stays available on a read-only timeline (it protects credits and the server decides); render presets are not shared between devices or people (that needs a new preset kind on the server).

Tests: `deliver/__tests__/render-presets.test.ts`, `deliver-presets.test.tsx`, `deliver-cost.test.tsx`, `deliver-queue.test.tsx`, `deliver-export.test.tsx`, `deliver-zones.test.tsx`; `review/__tests__/review-links-inline.test.tsx`. Customer text: `docs/user-guide/editor/export-your-film.md`.

## Input colour handling (Wave 4, R10.7)

Phone footage shot in HDR (iPhone HLG / Dolby Vision-compatible HLG, PQ) is converted to standard colours (SDR BT.709) **before** the grade, so a graded clip neither clips nor looks washed out.

- **Detect.** The ingest probe records the first video stream's `colorTransfer` and `colorPrimaries` (ffprobe's `color_transfer` / `color_primaries`, `unknown` read as not known) and `hdr`: `"hlg"` for `arib-std-b67`, `"pq"` for `smpte2084`, else null. All three are additive optional fields of the C# `MediaSourceProbe` / `MediaProbeResponse` and the TS `MediaProbe`; a probe stored without them loads and reads as not HDR. Log-tagged transfers (`smpte428`, `log`, `log_sqrt`) are recorded verbatim but never tone-mapped: there is no standard log curve and an untagged stream is never guessed. The render re-probes every downloaded source (the authoritative reading), so old probes and untagged ingest still work.
- **Render.** `HdrToneMap` (`TimelineRender/HdrToneMap.cs`) puts a fixed chain first in the decode chain of each HDR leg, before trim / crop / transform / grade: `zscale=t=linear:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:param=<knee>:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p`. Operator: **mobius**, `desat=0` (linear to a knee, then a smooth roll-off). The knee is **0.2 for HLG** and **0.5 for PQ**. Nominal peak: **203 nits for HLG and PQ** (the BT.2408 reference white); with no `peak=` override ffmpeg reads 10 (2030 nits relative to that unit), the same as `peak=10`, while an explicit `peak=4.93` (the true 1000-nit HLG peak) drives above-nominal signal to 241, outside the legal 16-235 output, so the default is kept. Measured on a synthetic 10-bit HLG BT.2020 fixture (full-code-value ramp plus flat patches) on ffmpeg 8.0.1 and 8.1.3, 8-bit Y: at knee 0.5 the 75-100% signal band sat at 211-232 (22 distinct levels, sunny areas too bright and flat); at 0.2 it is 195-229 (35 levels), while 38% signal (18% grey) stays 109 and 55% (skin) moves 152 to 150. Reference white (75%) lands at 195, i.e. below white, which is what gives the 75-100% band room to separate. Hable crushed the mid-tones (38% to 79), clip and linear flattened or crushed the highlights, and libplacebo's bt.2390 is not a drop-in (it is not in the capability check); all were rejected. PQ 58% signal (reference white) still lands at about white. The first version (hable, HLG npl 100) put both a stop too dark. Nothing user- or model-supplied reaches the filter string. SDR sources are not touched: their graph is byte-identical.
- **Capability.** `ffmpeg -filters` is read once per process (`FfmpegHdrToneMapCapability`); if `zscale` or `tonemap` is missing the render does NOT fail: the source is used as it is and the step output gets the plain warning "This server cannot convert HDR phone footage to standard colours ..." in `warnings[]` with the code `hdr_tonemap_unavailable` in `warningCodes[]`. Sources that were converted are listed as `hdrToneMapped: [{name, kind}]` (clip names only).
- **Cache.** A region that tone-maps adds `hdr-tonemap-v3:<kind>:mobius` to its recipe hash, per HDR clip. The region `RendererVersion` is NOT bumped, so SDR region caches stay valid; changing the operator or `RecipeVersion` re-renders only HDR regions.
- **Everywhere else.** Timeline renders and the media derivatives take the same per-source chain from `HdrToneMap`, applied before scaling in VideoCompile (single-source, segmented; a Mode `StreamCopy` compile of an HDR source is re-encoded instead, never copied), ThumbnailGenerate, ExtractFrame, the keyframes and contact sheets sent to vision, and the Proxy, Filmstrip and Stills derivatives. The output is tagged bt709 explicitly (`-colorspace/-color_primaries/-color_trc bt709`), HDR sources only; SDR argv is unchanged. Mixed SDR + HDR sources therefore match. Step revisions carry `+hdr-tonemap-2` on VideoCompile, ThumbnailGenerate, ExtractFrame and VideoAnalyze: the analysis copy itself still uses the unchanged `AnalysisFilter`, but the vision keyframes and contact sheets go through `HdrToneMap.Filter` (the delivery chain), so their pixels changed with the knee and a cached analysis must not carry captions from the old rendering. The proxy, filmstrip and stills builder versions were bumped to 3 for the same knee change, which regenerates every cached derivative once: a cached derivative is identified by its source plus its `ParamsJson` and its builder version, and neither the tone-map version nor the knee is in `ParamsJson`, so the builder version is what invalidates a cached proxy, filmstrip or still (the key is built before the source is probed, so regenerating only HDR sources was not cheap).
- **Preview.** The proxy is now tone-mapped at creation, so a freshly built proxy already shows standard colours; the tone-map exists only at render and derivative time in ffmpeg, and the browser preview viewer itself still does not tone-map (no shader operator: it would be a second, unverified implementation), so a proxy built before the bump (or on a host without the filters) shows HDR as the browser decodes it. The parity badge below stays as the honest fallback. A clip whose pool probe says `hdr` shows a small **HDR clip** note in the Inspector, and the parity classifier marks it `approximate` with the reason "Preview shows this HDR clip approximately; the delivered file is converted to standard colours before grading." (`classifyItem(item, doc, hdrKindFor)`; a render-only feature on the same clip still wins as `renderRequired`).
- **Tests.** `HdrToneMapTests` (xUnit, generates 2 s HLG / PQ fixtures with the real ffmpeg at test time, skipped with a reason where `zscale` / `tonemap` are missing; `Pq_delivery_filter_is_byte_identical_to_the_chain_shipped_before_the_hlg_change` pins PQ to the pre-change text), `lib/editing/media/__tests__/hdr.test.ts`, `components/editor/__tests__/hdr-clip-note.test.tsx`. To judge a real clip, `inference/tools/hdr-tonemap-compare.sh --out DIR CLIP` writes before/after stills and a side-by-side with ffmpeg only (local file in, local PNGs out).

## Audio previews for buses and dynamics (Wave 4, R08)

Behind Advanced audio (off by default, hidden on a phone), bus routing and the dynamics stages are now heard in the viewer.

- **Mapping.** `lib/editing/audio/dsp-preview.ts` is the pure table from the stored stages (C# `AudioMixBuilder.ProcessingChain` units: dB, ms) to Web Audio parameters, with the defaults of `Structure.cs`. The graph side is `lib/editing/preview/dsp-graph.ts`; `audio-graph.ts` builds `track -> [eq, pan, dynamics] -> bus -> parent bus -> master`. Buses are rebuilt on every play (parents first), never per tick; a loop, a chain deeper than four buses or a throwing context falls back to direct-to-master and the buses panel says so (`BUS_FALLBACK_NOTE`).
- **Stages (render order: gate, de-esser, compressor, limiter).** Compressor: `DynamicsCompressorNode` (9 dB knee like `acompressor`, times capped at 1 s) plus a make-up `GainNode`; the node's own automatic make-up (`0.6 * |threshold| * (1 - 1/ratio)` dB) is subtracted so the document's make-up is the only make-up. Limiter: a ratio-20 compressor then a `WaveShaper` clamp at the ceiling (chosen over a lookahead worklet because the clamp guarantees the ceiling even if the worklet is unavailable). Gate and de-esser: an `AudioWorklet` (`dsp-worklet-source.ts`, loaded through a Blob URL because worklets do not go through the bundler like `new Worker(new URL())`); the gate is a downward expander with attack/release smoothing, the de-esser a dynamic cut of a 7 kHz bandpass whose depth follows intensity (ratio 1 to 8).
- **Honesty.** `PREVIEW_NOTE`: "Preview is close to the delivered sound; the render is exact." Differences per stage are in `PREVIEW_DIFFERENCES` (tooltip on the stage status). Parity (`parity.ts`): a sound clip whose track or bus chain has these stages is `approximate` only when `detectDspSupport()` says the browser runs them (gate and de-esser need `AudioWorklet`; a module that fails to load is remembered by `markWorkletUnavailable`), otherwise `renderRequired` naming the stage. Denoise and the leveler stay `renderRequired`.
- **UI.** Each stage in the processing editor shows "Heard in preview, approximately" or "Heard after render only" (`<prefix>-<stage>-status`). There is no stored bypass field, so the stage checkbox (adds/removes the stage) is the A/B switch; no new field. A bus has no mute field either: a bus fader at -60 dB is silent in the preview.
- **Tests.** vitest: `dsp-preview`, `dsp-worklet` (the shipped source run numerically), `dsp-graph` and `audio-graph-dsp` (recording fake context), `audio-parity`, `dsp-status` (component). Playwright `08-audio-multicam/dsp-preview.spec.ts` reads `window.__rbAudioGraph.meters()` (published only when `window.__RB_TEST_HOOKS__ = true` before load).
- **Perf.** Graph nodes are created on play and on stage change, none per tick; the 14-playback-perf spec should be run by the lead.

## Layer masks (Wave 4)

Any bitmap layer (`Source`, `Cutout`, `Generated`, `Image`) can carry a non-destructive mask (the layer's matte). The original pixels are never changed. A `Text` layer has no bitmap: every route below answers 404 for it.

### Endpoint contract (backend, T-009 a)

Base: `/api/v1/projects/{projectId}/image-documents/{documentId}/layers/{layerId}/matte`.

- `PUT` (Edit). Body: raw PNG bytes (`Content-Type: image/png`), grayscale or RGBA. Optional query `?baseRevision=N`: a stale value is `409 { code: "REVISION_CONFLICT", revision }` and nothing is written. Replaces the mask: a NEW object is written (never overwritten), the layer's key is repointed and the previous mask object (always the editor's own) gets a `storage_retention_marks` row. `200` with the layer DTO (`hasMatte: true`, `matteUrl`); the new document revision is in the response header `X-Image-Document-Revision`. The document revision is bumped by 1.
- `DELETE` (Edit, optional `?baseRevision=N`): clears the key, marks the object, bumps the revision; `204` (also `204` when there was no mask).
- `GET` (View): serves the mask for ANY bitmap kind that has one, as an 8-bit grayscale PNG. The URL is stable, so it is served `Cache-Control: private, no-cache` with an `ETag` (revalidate with `If-None-Match`, `304`), not cached forever.

Visible amount rule (the editor must apply the same one): 255 = fully visible, 0 = hidden. The uploaded PNG is read as RGBA; if ANY pixel is not fully opaque its alpha channel is the visible amount, otherwise its luminance (`0.299 R + 0.587 G + 0.114 B`) is. The server stores the result as an 8-bit grayscale PNG, so the GET always returns luminance = visible amount (fully opaque). The compositor must therefore turn the matte's luminance into alpha before `destination-in` (the same for a vendor cut-out matte).

Errors (all plain `{ code, message }`):

| Status | code | When |
|---|---|---|
| 400 | `MATTE_NOT_PNG` | not a PNG (magic bytes) or does not decode |
| 400 | `MATTE_TOO_LARGE_PIXELS` | header declares more than 4096 x 4096 (checked before decoding, so a decompression bomb is never inflated) |
| 400 | `MATTE_SIZE_MISMATCH` | PNG size differs from the layer's pixel size (`layer.width` x `layer.height`), or the layer's size is unknown |
| 413 | `MATTE_TOO_LARGE` | body larger than `PictureEditor:MaxMatteBytes` (default 8 MiB) |
| 402/413 | plan gate (`STORAGE_QUOTA_EXCEEDED`...) | the mask does not fit the plan's storage; same check as uploads |
| 404 | none | document/layer/project missing, another tenant, Text layer, deleted layer, quarantined source |
| 409 | `REVISION_CONFLICT` | stale `baseRevision` |

Layer DTO: new field `hasMatte` (bool) for every kind; `matteUrl` is non-null exactly when `hasMatte`. No storage key is ever returned.

Interplay: Duplicate copies a mask to a new object (quota-counted, as before); deleting a layer marks its mask for every kind (a `Source`/`Image` layer's own file is still never marked); copy-on-delete is unchanged; `removeBackground` and the subject tools keep creating `Cutout` mattes exactly as before. A matte on any kind is applied the same way: the layer's alpha is multiplied by the visible amount.

### Layer masks: the editor (T-009 b)

- **Reading the matte.** `loadBitmap(url, { matte: true })` decodes the grayscale PNG and turns luminance into alpha (`lumaToAlphaInPlace`, `lib/editor/layer-mask.ts`), so `destination-in` works. `useLayerBitmaps` now loads the matte of EVERY bitmap kind that has `matteUrl` (it used to be `cutout` only) and `compose.ts` draws it `destination-in` on a scratch canvas for any kind, so the screen, the flat export, and the layered exports (`pixelsOnly`) all honour it. A matte that cannot load makes the layer "unavailable" (Export waits), as an image does.
- **Cache address.** The matte route is stable, so the editor adds `?v=<document revision>` (`versionedMatteUrl`) to a layer's `matteUrl` when it adopts a write, which makes the bitmap loader fetch it again. `useLayerBitmaps().seed(url, bitmap)` preloads the just-painted pixels under that address, so Done does not flash or refetch.
- **Painting.** `LayerMaskBuffer` (pure, `lib/editor/layer-mask.ts`) holds one 8-bit visible amount per layer pixel (255 shown), round soft brush stamps, `invert`, `reveal`, a bounded undo/redo of whole-buffer snapshots (30 steps, 256 MiB cap) and a dirty rectangle. `MaskSurface` (`canvas-dom.ts`) keeps the display canvases (alpha for the live matte, black and white for "Show mask only") in step. The tool id is `layermask`: it exists only while a session is open (`useLayerMaskEditing`), uses the pointer-event path of the brush tool, is a one-finger tool for the touch intent gate (pinch and scroll stay unaffected), and is not in the tool column. While a session is open other tools and other layers are refused with a plain message. Ctrl+Z / Ctrl+Shift+Z inside a session step strokes, not the document.
- **Saving.** Done sends one `PUT` (grayscale PNG at the layer's pixel size, `?baseRevision` from the document hook's `writeMatte`, which flushes pending edits first and records the new revision from `X-Image-Document-Revision`). Network errors and 5xx are retried after 0.6, 1.8 and 4 s; other refusals are not. The buffer is kept until the server answers; a failure leaves the session open with a plain message ("Try again"). A stale revision reloads the document with a notice and keeps the painting. Unsaved painting counts as unsaved for the page-leave guard.
- **Menu.** Bitmap layer rows get a mask button (desktop) or items in the row's three-dot menu (phone): Add mask (all visible, then opens for painting), Edit mask, Invert mask, Delete mask, Show mask only. A row with a mask shows a thumbnail chip (44 px on touch) that opens Edit mask. Items are disabled while a mask write runs, while another mask is open, or when the layer is locked (Show mask only stays on). The phone shows the mask panel at the top of the Layers sheet with 44 px Hide / Show, Undo / Redo, Done and Cancel.
- **Tests.** `layer-mask.test.ts`, `useLayerMaskEditing.test.tsx`, `LayersPanel.mask.test.tsx`, `layer-actions.mask.test.ts`, `image-documents.matte.test.ts`, the loader and compositor tests; e2e `13-picture/layer-mask.spec.ts` (desktop; pending run).

Remove silence pads only the side of a silence that borders speech (`padFrames`): an edge on a clip's own start or end, or in a gap, is cut exactly, and a silence that reaches a clip's head or tail to within a frame covers it, so no sliver of silence is left at a silent head or tail (linked sound follows the same frames).

## What Wave 2 leaves out

Rows cut or partial in Wave 2:

Nothing. Every row cut or partial at the first Wave 2 hand-back (bin delete Undo, touch drag of a tile, pool panes and save-on-change, the title position pad, in-place picture crop and merge, the remove-silence sliver) was built afterwards.

## Segmentation masks in the render (editor-preview-fidelity, T-015)

A clip whose `mask.kind` is `segmentation` renders with its alpha set to the union of the named tracks' masks.
`RendererVersion` is `timeline-render-region-v7` (v7 added region effects, see the next section): every earlier cached region is a miss (a one-time re-render), and a
timeline with no segmentation mask produces byte-identical argv apart from that.

- **Reading.** `TimelineSegmentationMasks` (called by `TimelineRenderStepExecutor` after the sources are staged) opens each
  track's FFV1 gray mask only through `SegmentationMaskStore`: the storage key must lie under
  `projects/{p}/agentFiles/segmentation/` (`IsProjectMaskKey`), the bytes must hash to the analysis artifact's
  `MaskContentHash` (`VerifyHashAsync`), and the mask must be its source's frame size. A missing track, a foreign
  analysis, a hash or size mismatch, or a stabilised clip fails the step with `DOCUMENT_INVALID`; the clip is never rendered unmasked.
- **Drawing.** In `RegionFilterGraphBuilder` each mask video is an extra input read on the clip's SOURCE clock (same
  `-ss`/trim/reverse/rate as the picture), `format=gray`, unioned with `blend=all_mode=lighten`, then `negate` (invert) and
  `gblur=sigma=featherPx` on the mask, and `alphamerge`d onto the picture at source geometry, before the clip's own
  crop/transform/grade. It is `alphamerge`, never `maskedmerge`. Transition legs carry the same matte.
- **Cache.** `RegionRecipeHasher` adds `segmask:{invert}:{featherPx}:{analysis}` and one `segtrack:{id}:{maskContentHash}`
  per track, so a re-cut mask (new content hash) re-renders exactly the regions it touches.
- **Preview.** The preview still cannot draw the matte; it is approximate until the next ticket.
- **Binding (T-103).** A frame-size match is not enough. `SegmentationMaskBinding` (one rule, run by the API validator for a 400 and by the render for `DOCUMENT_INVALID`) requires that each track's analysis source IS the clip's asset, that the analysis was not trimmed (its times would be window-relative; a trimmed analysis is refused, not offset), and that the mask's frames (`FrameCount / Fps`, from source time 0) cover every source frame the clip plays (speed and hold included); a mask that ends early is refused, never held on its last frame. The render also applies the upright-geometry rule of the compile: a source that alters pixels on decode needs `DecodeGeometry == upright-1` (the validator cannot probe, so this one is render-only). A stabilised clip with a segmentation mask is refused at save (validator and web invariants). `invert` and `featherPx` are both required on the wire in C# and TypeScript. `featherPx` is in source pixels.
- **API cost.** The PATCH lookup reads each analysis artifact through a 64 MB cap (over it: the one "does not name a video analysis" violation) and keeps the parsed track facts for 60 s per (project, result, key).
- **Known limit.** A clip with optical-flow retime interpolates its picture but the mask leg is only frame-retimed, so the matte can trail the picture by up to a frame on a slowed clip.

## Mask preview for the viewer (editor-wysiwyg, T-006)

The export follows an object's exact outline from the FFV1 `gray` mask; browsers cannot decode FFV1. So each segmentation track has a free **preview copy** the viewer decodes and draws the real outline from.

- **Route.** `GET /api/v1/projects/{p}/step-results/{r}/segmentation-tracks/{trackId}/mask-preview` (View access). `200` video bytes (Range and `SafeContentPolicy` headers through `RangedObjectStreamer`) when stored; `202 {"state":"pending"}` (plus `Retry-After: 2`) after publishing `SegmentationMaskPreviewRequested`; `404` for everything else (foreign or missing result, unknown or unpreviewable track, unverified mask, unreadable artifact; a result inside the project's output hold answers the same `OUTPUT_UNAVAILABLE` 404 as the tracks route). No key, bucket or URL in any body.
- **State on the list.** Every track of the segmentation-tracks JSON carries `maskPreview: { "state": "ready" | "pending" | "none" }`: `ready` = stored (GET plays it), `pending` = can be made (the GET queues it), `none` = cannot be made (no mask under the project's own `agentFiles/segmentation/`, no SHA-256, no frame count, or the engine found the mask failing its hash check).
- **Recipe (version 1).** The WorkflowEngine (`SegmentationMaskPreviewService`, never the API) downloads the track's mask, re-hashes it as the render does (a mismatch is `SEGMENTATION_MASK_UNVERIFIED`: no preview, a `.none` marker object beside where it would be), and encodes H.264 in MP4: `yuv420p` with the **full range** flag (`-color_range pc`, x264 `fullrange=on`, so the decoded luma 0..255 IS the mask and must not be treated as limited range), long side at most 640 (never upscaled, even sides), the mask's own fps (`-fps_mode cfr -r fps`) and exactly its frame count (`-frames:v`; the viewer maps source time to frame `round(t * fps)`), a keyframe at least every second, `+faststart`, the derivative demuxer whitelist. A mask holds exactly one tracked object, 255 on 0, so the preview is white-on-black luma.
- **Cache and storage.** Identity is `SegmentationMaskPreviewIdentity.CacheKey(maskHash, trackId, RecipeVersion)`; the object is `projects/{p}/agentFiles/segmentation-preview/{key}.mp4` (beside, never inside, the directory `SegmentationMaskStore` alone writes). The object store is the cache: no database row, not a project file, no credits, no quota. The engine runs one encode at a time (`SegmentationMaskPreviewDispatcher`), de-duplicated by (project, result, track), with a five-minute cooldown after a failed encode.
- **Hold.** The engine repeats the output-hold check on the analysis result before reading anything (a failed read refuses).

## Prompted region effects on a clip (magic-blur-editor)

A `MediaClip` can carry `regionEffects`: up to four effects (blur, pixelate, highlight, spotlight, colour pop, outline), each applied only to the object a segmentation mask follows. This is layer 1 (contract and render); the object analysis API, the editor panel and the assistant tools are later layers.

- **Wire shape.** `MediaClip.regionEffects` is an optional array of `{ effect, strength, mask, enabled }`. `effect` is one of `blur`, `pixelate`, `highlight`, `spotlight`, `colorpop`, `outline` (lowercase, closed set). `strength` is 0..1 on the clip; the workflow's `BuildEffectChain` takes 0..2 and clamps to that range. `mask` is the same segmentation mask value as `MediaClip.mask` (`kind: segmentation`, `analysisStepResultId`, `trackIds`, `invert`, `featherPx`; `invert` and `featherPx` are required). `enabled: false` keeps the entry in the document and skips it in the render, the cache recipe and the mask staging. Invert is the mask's `invert` flag, as in the workflow.
- **No new op.** The field is set with `setProperty regionEffects` (added to the `mediaClip` allowlist in `ops.ts`). The op count stays 34.
- **Refusals.** The validator (C# `EditTimelineValidator`, mirrored by `assertInvariants` in TS, locked by the contract corpus) refuses: more than 4 entries; a null entry; an `effect` outside the six words; `strength` outside 0..1; a `mask` that is not a segmentation mask; any mask the shared `SegmentationMaskBinding` refuses (foreign or unknown analysis, unknown track, wrong source); and a clip that is `stabilize: true` and also carries any region effect.
- **Where the mask comes from.** From an analysis step result (a same-project VideoAnalyze result with segmentation tracks). The binding rules are those in "Segmentation masks in the render" above: the analysis source is the clip's asset, the analysis is not trimmed, and the mask frames cover every source frame the clip plays. `TimelineAssetReferences` counts a region effect's analysis as a referenced step result, as it does for the clip's own mask.
- **Render order.** In `RegionFilterGraphBuilder` the effects run at source geometry, in document order, on the decoded picture leg, before the clip's crop, transform and grade, and before the clip's own segmentation matte. The effect chain outputs yuv420p, so it drops alpha; applying the clip's own matte after it keeps that matte working.
- **Mask staging and dedupe.** `TimelineSegmentationMasks` stages the masks of the clip's own mask plus every enabled region effect through the same verification path (project storage key, content hash, frame size, binding). An identical (analysis, track) pair is downloaded once. Region effect mask inputs are appended after the clip's own mask inputs. Any failure fails the step with `DOCUMENT_INVALID`; the clip is never rendered without its effect. A stabilised clip with a region effect fails the render the same way.
- **Cache.** `RendererVersion` is `timeline-render-region-v7`, so every earlier cached region is a miss once. `RegionRecipeHasher` adds one `regeffect:{effect}:{strength}:{invert}:{featherPx}:{analysis}` token per enabled entry and one `segtrack:{id}:{maskContentHash}` per track (`unresolved` when the mask was not staged). A timeline without region effects hashes as before apart from the version.
- **Preview parity.** Superseded by editor-wysiwyg: the viewer draws region effects (box matte, or the outline from the mask preview when every track is `ready`), and `preview/parity.ts` reports `faithful` with the outline and `approximate` with a box. See "What you see is what you export".
- **Interchange.** The OTIO export is lossless because it carries the ReelBolt document in metadata. EDL and FCPXML omit region effects, as they omit grades and keyframes; the FCPXML writer's scope note lists them.
- **Known limits.** A source with its own alpha loses it under an effect (yuv420p output). A stabilised clip cannot carry region effects. A clip with optical-flow retime has the same up-to-a-frame matte lag as the segmentation mask. The viewer draws the effect (editor-wysiwyg). Nothing here is verified against a live segmentation vendor.

## Background results while you edit

A slow AI tool (magic blur, AI proposals) keeps running while the user edits. Its result is anchored to the source media and clips, not to the document revision, rebased when it lands, applied as one undo step, or parked as "needs your OK" when it no longer fits; an edit that would hit a running job asks first. See [background-activity.md](background-activity.md) ("Tools that change the document while running").

## Object analysis and the object-effects finalizer (magic-blur-editor)

This is layers 2 to 4 behind "blur the license plate": the routes that find an object, the finalizer that writes the effect for the assistant, and the Objects panel. The effect itself (`MediaClip.regionEffects`) is in "Prompted region effects on a clip" above.

### The analysis routes (`ObjectAnalysisController`)

All four live under `api/v1/projects/{projectId}/media/{mediaRef}/objects` (`mediaRef` is a project file id or a finished output id); the logic is in `ObjectAnalysisService`, shared with the assistant.

| Route | Needs | Does |
|---|---|---|
| `POST .../objects/estimate` | project `Edit` | Prices the search without creating anything. Returns `billingEnabled`, `credits`, `balanceCredits`, `affordable`, `cached`, `consented` and `unavailableReason`. |
| `POST .../objects` | project `Edit`, verified e-mail | Queues the search; `202` for a new or still-running one, `200` when the same analysis already finished. Returns the request id and the execution id. |
| `GET .../objects/{requestId}` | project `View` | The derived state of one request. |
| `GET .../objects` | project `View` | The most recent requests for that file (at most 50). |

The consent has its own route, `api/v1/projects/{projectId}/object-effects/consent` (`GET` reads it, `PUT` grants it, `DELETE` withdraws it; `ObjectEffectsConsentController`). It sets or clears `Project.ObjectEffectsConsentAt`, which is `null` until a person agrees.

- **A hidden run.** A request creates a `WorkflowDefinition` of the hidden kind `ObjectAnalysis` (`WorkflowDefinitionKind.ObjectAnalysis = 6`) with one `VideoAnalyze` step holding a single text-prompted segmentation target (`ObjectAnalysisConfig.Build`). Plan feature, estimate, credit hold, content cache and refunds are that workflow's own.
- **The row.** `object_effect_requests` (`ObjectEffectRequest`) holds the request: project, source, prompt (at most 80 characters), match scope, the effect intent (effect, strength, feather, invert), an optional timeline intent (timeline id and clip ids, set only by the assistant), the execution id, the status and, once applied, `AppliedRevision`.
- **Gates, in order, for `POST .../objects`.** Verified e-mail; prompt, scope and effect validation (`400`); the source resolves in the project and is a video (`404`/`422`); the plan includes `segmentation` (`PlanGate.CheckSegmentation`); the project has consented (`409 OBJECT_CONSENT_REQUIRED`, with `needsBeforeRunning`); the video fits the frame budget: its length in 30-fps-equivalent seconds (`ceil(duration * max(fps, 30) / 30)`) is at most 600, else `422 OBJECT_SOURCE_TOO_LONG` naming the length and frame rate; a video of unknown length is priced at the 600-second maximum and the estimate says `upperBound: true` (the real charge is at most that); reuse (below); the credit preflight (`402 INSUFFICIENT_CREDITS`); the run rate (`429`, 10 paid searches per user per project per rolling hour). The estimate route skips consent, e-mail and rate and reports them in its fields instead.
- **Reuse.** `FindReusableAsync` looks at the 10 newest requests for the same project, source, prompt and match scope. A succeeded, applied or apply-failed one, or a queued one, is answered without a new run and without a charge. When a timeline intent comes with a reused analysis, the request gets its own row on the same execution so the finalizer has something to apply.
- **Status derivation (`ObjectAnalysisStatus.Derive`).** Nothing is pushed: a read derives the state from the run. No run or `Queued` is `queued`; `Running` is `running`; `Cancelled` is `failed` with `RUN_CANCELLED`; a failed run or step is `failed` with the step's failure code; a passed run with no usable segmentation track (the object appears on no frame) is `failed` with `OBJECT_NOT_FOUND`; otherwise `succeeded` with the step result id, up to 8 track ids and `coverageEndSec`. Customer sentences come from `ObjectAnalysisMessages`.

### The finalizer and the server-side writer

`ObjectEffectApplySweeper` (a `BackgroundService`) calls `ObjectEffectApplier.SweepOnceAsync` every `ObjectEffects:ApplySweepSeconds` (default 15; 0 switches it off). For requests that carry a timeline intent it claims each ready row with one conditional `UPDATE` (so replicas never apply twice; a claim older than the lease with no revision is taken over), then writes every named clip in ONE edit through `IEditTimelineWriter`. Row lifecycle: `queued`, then `succeeded` or `failed`, then `applied` or `apply_failed`. A clip that already carries an entry for this analysis, tracks and effect word gets that entry UPDATED when strength, invert or feather differ (one `setProperty`, counted as "changed"), and is left alone only when identical; a clip that already has 4 region effects, plays another video or fails the mask binding is skipped. The note (`FailureMessage` on an applied row, exposed as `applyNote` with `applyStatus` = `applied` or `apply_failed` on the status response) says honestly how many clips were added, changed, left as they were and skipped. The assistant's reuse path applies only the row it just created through `ObjectEffectApplier.ApplyOneAsync(projectId, requestId)` (the same claim and write, never a sweep). Two identical requests made at the same time collapse: a partial unique index (`ux_object_effect_requests_inflight`) lets one in and the other is answered with that one (`202`). Withdrawing the consent (`DELETE .../object-effects/consent`) cancels analyses still in flight (`CONSENT_WITHDRAWN`). Rows with no timeline intent (the editor's own requests) are only finalized, never applied: the editor writes the effect itself. The assistant is not woken when this finishes (see [Object effects](platform-assistant-mcp.md#object-effects-blur-or-mask-an-object-by-name)).

**Attaching to a search that is already running.** The assistant's reuse path gives a request with a timeline intent its own row on the shared run. The in-flight index `ux_object_effect_requests_inflight` is partial (`status = 'queued'`) and its identity is (project, source kind, source id, prompt, match scope, `coalesce(timeline_id, zero guid)`), so an editor row (no timeline) and a row per timeline can all wait on one run. A second attach whose identity is already queued (same timeline) is not an error: the service returns the first row with the plain message "This is already being looked for in that timeline; the effect will appear when it finishes." (`202`; a lost insert race is caught as a unique violation and answered the same way). The finalizer applies the FIRST row's intent, so a second Apply with different clips or a different effect on that timeline while one is running is refused with that message, never a 500; once the run finishes the user can ask again.

### The Objects panel (`ObjectEffectsSection`)

The Objects section in the Inspector (and, on a phone, **Blur or mask an object** in the clip's More sheet) is `web/components/editor/panels/ObjectEffectsSection.tsx`; the routes are called from `web/lib/editing/api/objects.ts`.

- **States.** Idle (object field, quick picks, effect, invert, strength, feather); price loading, ready (`About N credits`, or free when `cached`) or failed; blocked (credits, plan or length, with a plain sentence and **Find and apply** disabled); the consent dialog; in flight (`queued` or `running`, with **Stop watching**); failed (`OBJECT_NOT_FOUND` has its own sentence, other codes show the server's message); done. Read-only open disables every control. A clip that does not come from a project file or a finished output shows `unavailableSource`.
- **Price.** The estimate is requested again 400 ms after the object or effect settles.
- **Consent dialog.** Shown when the estimate says `consented: false`. It requires the tick box, then `PUT`s the consent; **Withdraw permission** `DELETE`s it.
- **One undo step.** When the status reads `succeeded`, the panel appends one entry to the clip's `regionEffects` through one `setProperty` (once per request id, never on failure). One undo removes it. The list of existing effects offers a toggle, a strength slider and **Remove**, each one `setProperty`.
- **Limit.** At 4 effects the panel refuses before running (`limitReached`).

## What you see is what you export (editor-wysiwyg)

This section stands alone. The goal of the initiative: whatever the export draws, the viewer draws, and every
adjustment on a clip is visible on the timeline. The export (`TimelineRender`) stays the authority; the viewer
says plainly where it only gets close. Branches (stacked): `parity`, `badges`, `live-preview` (+ `mask-preview`,
`insert-render`), `live-color`, `transitions`, `stabilize`, `compositing`, `canvas-transform`.

### Feature by viewer by timeline, after this initiative

Viewer: **live** = drawn as exported; **approx** = drawn, small named differences (the badge says
"Approximate"); **render** = not drawn (the badge says "Render required"). Timeline: the badge kind from
`clip-badges.ts` (see below). Parity classes on this table are the committed `parity.ts` rules.

| Feature | Viewer | Timeline | Export |
|---|---|---|---|
| Move / scale / rotate / opacity, static or keyed | approx (GPU sampling) | `motion` badge | yes |
| Free-aspect scale (`scaleY`) | approx | `motion` badge | yes |
| Follow subject (writes transform keys) | approx, via the transform keys; no defect found in the apply-to-frame path (`follow-preview.test.ts` pins units and source-to-timeline mapping) | `follow` badge | yes |
| Crop | approx | `crop` badge | yes |
| Reframe (point) | approx (render's crop window, GPU sampling) | `reframe` badge | yes |
| Speed | approx (no frame blend, no pitch-preserving audio); rate <= 0 render | `speed` badge | yes |
| Reverse / hold | live | `speed` badge | yes |
| Optical-flow retime | approx, own finding "Frames are not blended in the preview; the render blends them." | `speed` badge | yes |
| Fades | live | ramps in the clip corners (not a badge) | yes |
| Dissolve / fade transition | live | own item | yes |
| Transitions (every xfade word the export uses) | live: 40 of 44 words use the render's own transition maths and are faithful; `dissolve`, `distance`, `fadegrays` and `hblur` are approximate (grain pattern, YUV vs RGB distance, luma vs RGB grey, 64-point blur) | own item | yes |
| Titles, templates, captions | approx (browser text); drawn at their track's place in the stack | - | yes |
| Title transform (move / scale / rotate / opacity) | approx | - | yes |
| Generator colour / bars | live (the bars generator draws the render's HD colour bars; the grey ramp's steps can sit a pixel off) | - | yes |
| Insert clip (media pinned into the keyframed quad) | approx (perspective warp; its chroma matte is keyed live in the grade pass before the warp, as the export does) | - | yes (T-017, `insert-render`) |
| Adjustment clip (grade + effects over lower tracks) | live: grades and applies its effects to the composite below it at its track position; render-required only if it carries an effect with no shader | - | yes |
| Grade primaries, wheels, colour groups | live | `colour` badge | yes |
| Curves, LUT nodes | live and faithful (`grade-pass.ts`: natural-spline table per node, tetrahedral LUT); approximate only while a LUT file is loading or failed to load | `colour` badge | yes |
| Qualifier / power-window secondaries | live and faithful (matte confines the node, like `maskedmerge`) | `colour` badge | yes |
| Grade nodes beyond 16 | the first 16 are exact; the rest fold into one approximate step (curves, LUT or secondary nodes past 16 are not shown) | `colour` badge | yes |
| Camera input profile | faithful (the input stage reads its table tetrahedrally like the render); approximate only when the clip also has a LUT node, with a double-conversion hint | `colour` badge | yes |
| HDR tone map | approx | - | yes |
| Shape mask rect / ellipse | approx (render's feather and invert) | `mask` badge | yes |
| Segmentation (object) mask | live when every track's mask preview is `ready`; else approx box ("Outline preview still preparing, showing a box."). A clip with a source rotation previews the same way: the boxes and the decoded mask video turn with the picture (`rotateMatte`; the render mattes at source geometry and rotates afterwards) | `mask` badge | yes |
| Region effects / magic blur | same rule as the object mask | `regionEffects` badge, progress on the clip while the job runs | yes |
| Keyer | live and faithful (colorkey + despill ported) | `keyer` badge | yes |
| Effects blur / pixelate / mono / vignette | approx | `effects` badge | yes |
| Effects sharpen / grain | live: sharpen approximate (slightly softer edge detail at a small preview size), grain approximate (same strength, different pattern); an unsupported effect no longer disables the whole chain, only an effect with no shader is render-required | `effects` badge | yes |
| Stabilize | approx, from a stabilised proxy of the whole source; until ready the clip shows the unstabilised picture | `stabilize` badge, progress on the clip | yes |
| Corner pin | approx (true perspective warp, per-fragment homography) | `cornerPin` badge | yes |
| Compound / multicam | approx / live | label band | yes |
| Video over video | live; `blendMode` per clip | - | yes |
| Blend modes | faithful with a rounding caveat | - | yes |
| Clip channel map (left / right / mono / swap) | live: the audio graph routes the channels as the render's `pan` filter does (`CHANNEL_ROUTES`, a stereo up-mix first so a mono file is on both sides) | - | yes |
| Track denoise / leveler (audio) | render | - | yes |
| Audio cleanup / enhance results | live (they are new source files) | job mark while running | yes |

**Still render-required, and why:** a speed of 0 or less (the scheduler ignores it); track denoise and leveler (no
browser implementation); buses that cannot be set up in the browser; an effect with no shader (the closed effect
set has a shader for every word, so this only guards a word added later); frame blending of optical-flow retime
and the pitch-preserving stretch of speed audio (approximate, not hidden); exact HDR tone mapping (approximate).
The Simple preview (the user's own switch, or a browser with no WebGL2 or WebCodecs) still shows one clip at a
time; Auto never chooses it.

### The parity `findings` model

`classifyItem` (`web/lib/editing/preview/parity.ts`) no longer stops at the first matching rule. It collects every
applicable finding for the item: `ItemParity.findings: { feature: ParityFeature; parity: ParityClass; reason: string }[]`,
worst class first (rule order inside a class). `parity` and `reason` stay the worst finding's, so older callers
and tests read the same fields. `ParityFeature` is a closed union (`speed`, `retime`, `channelMap`, `audioProcessing`,
`stabilize`, `cornerPin`, `shapeMask`, `segmentationMask`, `regionEffects`, `keyer`, `reframe`, `effects`, `secondaries`, `hdr`,
`inputProfile`, `curvesLut`, `gradeNodes`, `transform`, `title`, `caption`, `generator`, `insert`, `adjustment`,
`transition`, `compound`, `multicam`, `unknown`) and is the join key between a finding and a timeline badge.
`ParitySummary` still counts an item once, by its worst class. The viewer badge popover lists one line per finding,
each with the same jump-to-clip. Reasons reach Italian through `labels-msg.ts` / `reason-msg.ts` and the reason
tables, like every older reason.

### The timeline badge model (`clip-badges.ts`)

`web/lib/editing/display/clip-badges.ts` is pure. `clipBadges(item, doc)` returns one `ClipBadge` per kind the clip
carries, in `CLIP_BADGE_ORDER`: `motion`, `follow`, `crop`, `speed`, `colour`, `mask`, `regionEffects`, `keyer`,
`effects`, `stabilize`, `reframe`, `cornerPin`. Only media clips carry badges; fades are ramps, not badges. A badge has
`enabled` (false paints it hollow), `toggle` and `remove` as arrays of existing ops (`setProperty` with
`value: undefined`, or `retime` back to 1x for speed; no new op), and `features` (`BADGE_FEATURES`), the parity
features it answers for. The painter colours a badge by the worst finding among those features (amber dot for
approximate, render-required dot for the other class). `BADGE_SECTION` says where a click opens (workspace,
Compose entry, element id of the inspector section); `BADGE_PHONE_SECTION` does the same for the phone clip sheet.
A clip too narrow for its badges draws one "+N" badge. The clip context menu has an **Adjustments** submenu with
the same open / turn off / remove actions, so every badge is reachable without a pointer. Labels live under
`editor-timeline.badges` in both catalogues.

### Job marks and the edit guard

`web/lib/editing/background/job-marks.ts` holds `{ id, jobKind, clipIds, state: 'running' | 'needsReview' | 'failed',
progress?: 0..1, label, message? }`. Results-store entries (object effect, audio cleanup, AI proposals) become marks
through `marksFromEntries`; other jobs call `registerJobMark` / `updateJobMark` themselves (follow a subject,
stabilise). The painter draws a progress strip on the clip foot (determinate or an indeterminate stripe; static
hatching under `prefers-reduced-motion`), a "Processing" label, a slightly dimmed body, a "Ready to review" chip
for `needsReview` and an error chip for `failed`. The guard (`job-protection.ts`) holds an edit when it changes
what footage a clip plays (`SOURCE_KEYS`: `playbackRate`, `sourceIn`, `reverse`, `hold`, `assetRef`) or a property
the job kind reads (`JOB_READS`). The user keeps the go on / stop the job / go back choice.

| Job kind | Also held (on top of `SOURCE_KEYS`) | Why |
|---|---|---|
| `objectEffect` | `stabilize` | a clip cannot carry region effects and be stabilised |
| `audioCleanup` | none | reads the clip's sound only |
| `proposal` | none | its ops are anchored to the clips as they were |
| `follow` | `crop`, `reframe`, `stabilize`, `transform` | the result only fits that framing and it writes the transform |
| `stabilize` | `stabilize` itself (and `assetRef`); trims are fine | the proxy is cut from the whole source |
| `enhance` | none | renders new footage from the source |

Grade and effects are held for no kind: no job reads them.

### Derivatives the viewer decodes

- **Stabilised proxy.** Media derivative kind `stabilizedProxy` (append-only enum, persisted by name). Request:
  `POST /api/v1/projects/{p}/media/derivatives/request` with `{ "mediaRef": "file:<guid>", "kinds": ["stabilizedProxy"] }`
  -> `202`; the editor sends it when `stabilize` turns on and when it opens a timeline whose stabilised clip has no
  proxy, never on hover or GET. List: `GET /media/{mediaRef}/derivatives` gains a row `kind: "stabilizedProxy"`
  (`video/mp4`, width, height, `durationSec`, id, ranged `contentUrl`) only when ready. Progress: Go SSE
  `media.derivative.progress` `{ projectId, sourceProjectFileId, cacheKey, stage, percentComplete, detail, occurredAt }`
  with stage `Stabilize` 0-100, terminal `Done`, `Skipped` or `Failed`; there is no kind field, so match
  `sourceProjectFileId` plus the stage. The vidstab parameters are one shared C# constant used by the render and
  the proxy. The viewer decodes the proxy in place of the source for that clip's picture (audio stays from the
  source). Findings: preparing -> approximate "Stabilised preview is being prepared (n%)"; ready -> approximate
  `STABILIZE_READY_REASON` (the proxy stabilises the whole source, the export only the clip's span). Failed ->
  job mark `failed` with a Retry that requests again.
- **Mask preview.** `GET .../step-results/{r}/segmentation-tracks/{trackId}/mask-preview`: `200 video/mp4` (Range),
  `202 {"state":"pending"}` with `Retry-After: 2` (the first request starts production; the web polls the same URL
  about every 2 s, at most about 30 tries, then falls back to boxes), `404` = none. Each track in the list JSON
  carries `maskPreview: { "state": "ready" | "pending" | "none" }`; on `none` the web does not call the route.
  Decode with a `<video>` or the mediabunny reader; frame `k = round(sourceTimeSec * fps)`; read luma as R; 255 =
  object; full range (do not rescale 16..235). The frame spans the whole source frame in source fractions, long
  side at most 640, and gets the clip's crop and transform exactly like the picture. Recipe, storage and hold rules:
  [Mask preview for the viewer](#mask-preview-for-the-viewer-editor-wysiwyg-t-006). Matte source in the compositor
  is `boxes | maskVideo`. A segmentation `mask` or region effect is `faithful` when every track is `ready`, else
  approximate with "Outline preview still preparing, showing a box." (pending) or the box-only text (none); with no
  box data and no mask video the layer shows the unmasked picture dimmed (it is no longer hidden).

### Video over video

- **Blend modes.** `MediaClip.blendMode`, ten words, formulas and render graph: see
  [Blend modes](#blend-modes). The viewer ports the same integer formulas into `preview/blend-pass.ts`
  (ping-pong backdrop; no framebuffer cost when no layer blends). Parity: faithful with `BLEND_MODE_REASON`
  ("...colours can differ by one level where the clip is partly transparent."); software GL without framebuffers
  is approximate.
- **Stacking order.** Titles, captions and generators draw at their own track's place in the stack (they used to go
  to one overlay canvas above all video), as the render composites them. A title on V1 sits under a video on V2.
- **Place over.** Keymap action `placeOver` (`Mod+Alt+P`; label "Place over (picture-in-picture)"; phone: action bar
  and add-media sheet, `TOUCH_PARITY` "Add media, then Place over"). It puts the item at the playhead on the first
  video track above the topmost occupied picture at that time (it adds a track when none is free), applies the
  picture-in-picture preset (scale 0.33, bottom-right inside the 5% safe margin, the "Picture in corner" values of
  `TransformOverlay`'s `PositionMenu`) and selects it so the handles show. Dropping a pool item onto an upper track
  over an occupied lower one still places it full frame, and shows a toast "This picture covers the one below." with
  the action **Make it picture-in-picture** (one undo step). Inspector: a **Blend** select next to Opacity in Compose,
  and in the phone clip sheet.

### Scale on two axes and the title transform

Both are optional and identity-default: absent means today's bytes and today's recipe hash.

- `ClipTransform.scaleY?: Keyframed<number>`: vertical scale. Absent means "same as `scale`". When present, `scale`
  is the horizontal scale. Same limits and keyframe rules as `scale`. Edge values (the C# validator is the referee,
  TS matches): a negative `scale` is refused; a base `scaleY` of 0 reads as absent; a `scaleY` keyframe value of 0
  is refused; non-finite is refused. Fixtures: `transform-scale-negative-refused`, `transform-scaleY-zero-accepted`,
  `transform-scaleY-key-zero-refused`.
- `TitleClip.transform?: ClipTransform`: free move / scale / rotate / opacity around the title's own anchor (where
  `template` and `position` put it; `positionX/Y` offset it in canvas fractions). Allowed on `lowerThird` and `card`
  (it offsets the block). With a transform the render draws the title with `drawtext` on a transparent canvas-size
  `rgba` source, then scales, rotates and overlays it at the offset, so it composites in track order like a
  picture; without one the old `drawtext` line is unchanged.
- The flatteners (C# `TimelineFlattener`, TS `structure/flatten.ts`) keep a cut title's `position`, `template` and
  `transform`. Before this fix a title cut inside a compound clip lost its 3x3 position in the export (fixture
  `title-position-in-compound-cut`).

### The Transform tool on the viewer

Keymap action `toolTransform` (`Shift+T`, group Tools) toggles the picture handles; the viewer tool dock has the same
button ("Transform", on by default; desktop and tablet; `TOUCH_PARITY` reason: on a phone tapping the picture always
selects it). Clicking the picture selects the topmost visible picture item under the pointer at the current frame
(`viewer-transform/hit-test.ts`; the transformed rectangle, not pixel alpha); Alt-click cycles to the item below;
empty canvas clears the selection; nothing happens while a mask, reframe, follow or pin tool is armed. Handles show
whenever the playhead is on the item: paused they are live, playing only the outline shows and the first press
pauses and then edits (the old "pause-only" rule is gone for this tool). Corner drag is free (writes `scaleY` once
the aspect differs), **Shift** keeps the aspect (a clip with no `scaleY` stays uniform), **Alt** scales from the
centre, edge handles stretch one axis, the round handle rotates (**Shift** = 15-degree steps). A move snaps to the
centre, thirds, safe margins (5% and 10%) and other items' edges within 8 screen pixels; holding **Shift**, **Ctrl**
or **Cmd** while moving turns snapping off. Titles get the full box and write `TitleClip.transform`, with the nine
`position` anchors kept as a magnet; captions move on the 3x3 pad with a live ghost (no resize). One gesture
is one undo step (`commitTransform`). A one-line dismissible hint shows the first time per session. The inspector
transform fields show **Width** and **Height** with a link toggle.

### Inserts

The export now renders `insertClip` (T-017): the media is stretched to a canvas-shaped layer and pinned into the
(keyframed) quad with `perspective`, overlaid in track order. A quad whose corners are all within 0..1 is canvas
fractions, anything else is document-canvas pixels; coordinates clamp to -1..2 of the canvas. The insert has no
opacity, crop or fades; its `matte` is keyed out of the picture before the warp with the media clip's keyer
(`InsertLayerBuilder.Chain`). An insert with no media or no quad is skipped and named in `skippedItems`. The viewer
draws the same warp (`live-geometry.ts`) and keys the matte with the keyer's own maths before it, so inserts are
`approximate` only because of the warp's edge sampling.

## The viewer on the whole screen

This section stands alone. Keymap action `viewerFullscreen` (`P`, group Navigate; label "Viewer: full screen")
and the button `viewer-fullscreen` in the viewer options (desktop and tablet; hidden where the browser has no
Fullscreen API, and on a phone, whose `TOUCH_PARITY` entry is keyboard-only with the reason) put the viewer zone
(`viewer-zone`: the picture and its transport) on the whole screen with `Element.requestFullscreen`. It is view
state only: no document, sync or render change. `useViewerFullscreen` (`components/editor/viewer`) follows
`fullscreenchange`, so Escape (the browser's own rule) leaves it too. While it is on, the hook's `portalTheme`
makes Mantine portals created inside the viewer (tooltips, the preview options, the viewing LUT menu) mount inside
the zone, because the browser shows nothing outside the full-screen element. A page switch, the cheatsheet and the
command palette live outside the viewer, so the command context leaves full screen before it runs them. The
adaptive size follows the larger picture through the same `setDisplayLongSide` measure.

## Video enhancement (the HTTP routes)

`VideoEnhance` (see [video-editing.md](video-editing.md) "Video enhancement (the step)") is queued as a HIDDEN one-step workflow of
kind `WorkflowDefinitionKind.VideoEnhance = 8` (7 is reserved for the audio-cleanup stack). It is hidden from every list, edit,
delete, execute, retry and resume path (REST, assistant, MCP): resume and retry answer `409 ENHANCE_RESUME_NOT_SUPPORTED` /
`ENHANCE_RETRY_NOT_SUPPORTED`; the runs stay readable through the status route below. A never-executed hidden workflow is deleted by
the orphan reaper after an hour. What stays reachable by id, exactly (the same as object analysis): `GET /api/v1/projects/{p}/workflows/{id}` and the
run-history reads still return the hidden workflow and its runs, because a run is read by id; nothing there can change, run, retry, resume
or delete it, and no list shows it. The logic is `Services/Media/VideoEnhanceService.cs` (shared with the assistant); the controllers only
authorize the project once and map the result. No response carries a storage key, bucket or URL.

| Route | Permission | Does |
|---|---|---|
| `POST /api/v1/projects/{p}/media/{mediaRef}/enhance` | Edit | Queues the run. `202 {executionId, op, engine, hosted, state, credits}`. |
| `POST .../enhance/estimate` | Edit | Writes nothing, no vendor call. `{billingEnabled, hosted, credits, seconds, balanceCredits, affordable, unavailableReason, unavailableCode, consented, maxOutputHeight, outputWidth, outputHeight, outputFps, estimatedBytes, estimatedProcessingSeconds, maxSeconds}` (`maxSeconds` is the source-length cap that applies, from `VideoEnhance:Hosted|Local:MaxSourceSeconds`). The size, byte and time figures are estimates. |
| `GET .../enhance/{executionId}` | View | `{executionId, state, op, engine, method, durationSec, outputFileId, sourceMediaRef, outputWidth, outputHeight, outputFps, sizeBytes, warnings, message, failureCode, progress}`; `state` is `queued`, `running`, `ready`, `failed` or `cancelled`. Scoped to the project, the hidden kind and the media of the path. |
| `POST .../enhance/{executionId}/cancel` | Edit | Publishes the same stop event as the workflow stop endpoint. `202` stopping, `409 ENHANCE_ALREADY_FINISHED`, `404` otherwise. |
| `GET\|PUT\|DELETE /api/v1/projects/{p}/video-enhance/consent` | View, Edit, Edit | The project's recorded consent for hosted enhancement (UI only; PUT is idempotent; DELETE withdraws it and stops the project's queued and running hosted runs). |

`mediaRef` is `file:{projectFileId}` or `step_result:{stepResultId}`. The body of the POST and of the estimate is the step's fields minus
the source, provider and registration: `{op, engine, upscaleFactor?, targetHeight?, frameMultiplier?, denoiseStrength?, keepGrain?,
shutterAngle?}`. The body can never turn the consent on: the route writes `allowSourceMediaEgress: true` into a hosted step only when the
project's consent is recorded.

**What the route writes.** It probes the source's length, size and frame rate itself and writes `maxSourceSeconds = ceil(seconds)` and, for a
hosted run, `sourceWidth`, `sourceHeight`, `sourceFps` into the step, so the engine's credit hold, the estimate and the route's balance check
price the same real source (the hold is `VideoEnhanceStepPricing`, the function the engine calls). A local run carries no hints and no hold.

**Errors, in the order the route decides them** (a refusal writes nothing):

| Status | Code | When |
|---|---|---|
| 403 | `EMAIL_NOT_VERIFIED` | The address is not verified (before anything about the project is read). |
| 400 | `ENHANCE_REQUEST_INVALID` | The body is not a valid op/engine/parameter combination (the message names the field). |
| 404 | none | The media is absent, in another project, quarantined, held or malformed: always the same 404. |
| 422 | `ENHANCE_SOURCE_NOT_VIDEO` | The media is not a video. |
| 402 | `PLAN_NOT_ENTITLED` (feature `video_enhance`) | A hosted operation on a plan without it. Local operations are never gated. |
| 402 | `PLAN_NOT_ENTITLED` (feature `resolution`) | ANY operation whose output short side is above the plan's `MaxOutputHeight` (an upscale, and also a denoise, motion blur or interpolation of a source taller than the plan). |
| 409 | `CONSENT_REQUIRED` | A hosted operation and the project has not consented. |
| 413 | `SOURCE_TOO_LONG` (+ `maxSeconds`) | The source is over the cap of its engine: `VideoEnhance:Hosted:MaxSourceSeconds` (default 120) hosted, `VideoEnhance:Local:MaxSourceSeconds` (default 600) local; a deployment can only lower them. |
| 409 | `SOURCE_NOT_MEASURED` | A hosted operation on a source whose probe has not been stored yet (it cannot be priced). |
| 409 | `ENHANCE_UNAVAILABLE` | No provider is set up, or the model cannot be priced. |
| 402 | `INSUFFICIENT_CREDITS` (+ `missing`) | The balance does not cover the hold. |
| 429 | `RUN_RATE_LIMITED` / the plan's concurrency code | 10 hosted or 60 local runs per user per project per hour; the plan's concurrent-run cap. |

The estimate answers `unavailableCode` with the machine code of the first refusal (`PLAN_NOT_ENTITLED`, `RESOLUTION_ABOVE_PLAN`,
`CONSENT_REQUIRED`, `SOURCE_TOO_LONG`, `SOURCE_NOT_MEASURED`, `ENHANCE_UNAVAILABLE`) and the English sentence in `unavailableReason`, so the
web never parses English. **Sizing.** Output size follows the engines (an upscale scales the picture, an interpolation multiplies the rate); the bytes
come from a bitrate table by output tier (4, 8, 16 and 35 Mbit/s for 720p, 1080p, 1440p and above, x1.5 above 30 fps, plus 192 kbit/s of audio);
the processing time from a per-operation local speed table or, hosted, 45 s plus 3 s per source second. All three are estimates.

**Consent flow.** The editor reads the consent, asks the person once ("send this video to an outside AI service?"), `PUT`s it, and then
queues. Withdrawing it (`DELETE`) stops this project's queued and running hosted runs of ANY workflow kind (the engine also refuses a hosted run without the recorded consent, read fresh before the upload); a run already past the vendor submit finishes the
call it made. The same consent routes never touch magic blur's consent (`object-effects/consent`).


## Picture previews

`GET /api/v1/projects/{projectId}/outputs/{stepResultId}/download` accepts an optional `w` for picture outputs (png, jpg, jpeg, webp). The width is snapped up to the next of 160, 320, 480, 640, 960, 1280 or 1920 and the answer is a WebP of that width (`ImageThumbnailer`, SkiaSharp, quality 80, alpha kept). A `w` above 1920, a non-numeric or non-positive `w`, a `saveAs` download, a `Range` request, a source that is already narrower, a source over 40 MB or 36 megapixels, or any decode failure serves the original file unchanged. Previews are kept in a 64 MB in-process cache keyed by bucket, key, S3 ETag and width. At most two previews are fetched and decoded at once per process; a request that gets no slot within 3 seconds is served the original. An object that cannot get a preview (undecodable, too many pixels, already narrow, WebP not smaller) is remembered for 10 minutes so repeats skip the fetch and decode.

The same `w` and headers apply to the project file route `GET /api/v1/projects/{projectId}/files/{fileId}/media` (png, jpeg and webp by the stored MIME type). Pictures (full and preview) carry `ETag` (the S3 ETag, plus `-w{width}` for a preview) and `Cache-Control: private, max-age=3600` (previews `max-age=86400`). A matching `If-None-Match` is answered `304` before any body is read. Video and audio responses are unchanged: no ETag, no Cache-Control. All existing access, hold, key-scope and moderation checks run before any of this.

Stored previews: a preview is also kept in object storage at `projects/{projectId}/agentFiles/previews/{first 32 hex of sha256(source key)}-w{width}.webp` (`ImagePreviewStore`), with the source's S3 ETag in the `x-amz-meta-source-etag` metadata; a stored preview whose ETag is not the current source's is ignored and rebuilt. The lookup order is memory cache, stored preview (no source read, no decode, no slot), then an on-demand decode under the slot with a best-effort write-through. A stored preview is only read after every gate on the source and after the source's own metadata call succeeded, so a deleted, quarantined or held source never serves one. When a picture step completes, `ImagePreviewPrewarmConsumer` stores the 320, 480 and 1920 px previews (skipping widths not smaller than the source and valid existing ones; it takes the same two-at-a-time slot and skips quietly when none is free). Deleting or moving a picture (`S3FileStorageService` delete, batch delete and move, and the output delete route) removes its stored previews; deleting a whole project removes them with the project prefix. Stored previews count toward the project's storage usage.

Hold rules for previews: a preview is a derivative of its source and is never served on its own. Both download routes run every gate on the source (access, hold, quarantine, key scope, moderation) first, then HEAD the source; a missing source answers 404 and a replaced one (different ETag) ignores the stored preview. The pre-warm (`ImagePreviewPrewarmConsumer`, `ImagePreviewGate`) skips a missing or content-quarantined project, an output the CSAM hold covers (`OutputGate.IsKeyHeldAsync`) and a quarantined file row. After writing previews (pre-warm or on-demand write-through) the source is HEADed again, and the pre-warm re-runs the hold check; if the source vanished, changed or became held, the previews just written are deleted. Engine-side direct deletes of a project object (`ProjectFileWorkspace`, `SandboxWorkspaceJournal`, `S3RunnerOutputStore`, the Remotion sandbox tools) do not remove previews; they stop being served at once and go with the project prefix.
