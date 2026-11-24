# A simple editor for a first video, the full one a click away

**Status:** approved and built, 2026-11-11. The text below is the proposal as approved; §13 lists what was built
differently and what is still open. What shipped is described in `docs/editor.md` "The Simple shell".
**Scope:** the video editor on desktop and tablet (`web/components/editor/**`). The phone surface is not changed.
No document field, op, sync, render, billing or route change.
**Naming rule:** as in `editor-pro-ui-alignment.md`: the professional product the full editor is measured against
is never named. This document says "the full editor" and "professional editing conventions".

---

## 1. What is asked

One approval with four parts:

1. **Two shells over one editor** (§4): *Simple* and *Full*. Same store, same document, same preview, same
   render. The mode is how the editor is laid out on this device, never something saved in the timeline.
2. **Who starts where** (§5): a new user starts in Simple; someone who already customised the full editor stays
   in Full.
3. **What Simple shows** (§6): a viewer, a timeline, one panel that follows the selection, three places
   (Edit, Look, Export), and one box to ask for a change in words.
4. **One standing rule relaxed in Simple only** (§7): the dropped-frame counter is shown when frames are being
   dropped, not always.

§10 lists the decisions that are open. §11 is how we learn whether it worked.

## 2. The problem

The full editor follows professional editing conventions, on purpose, and that is the right target for someone
who edits for a living. It is the wrong first screen for the customer ReelBolt mostly sells to: someone who wants
a promo video and has never opened an editing suite.

What a first-time user meets today on the Edit page, counted from the code:

| What | Count | Where |
| --- | --- | --- |
| Pages in the workspace bar | 7 (Media, Cut, Edit, Compose, Colour, Audio, Deliver) | `shell/workspaces.ts` `WORKSPACES` |
| Tabs in the Edit page's left group | 10 (media, markers, index, analyse, transcript, subtitles, takes, structure, review, history) | `workspace/view/useSidePanelsView.tsx` |
| Tabs in the right group | 5 (inspector, assist, settings, revisions, compare) | same |
| Keyboard actions | 77 | `lib/editing/keymap.ts` |
| Preview status surfaces | parity badge, quality menu, adaptive-size pill, readiness chip on the viewer; playback details and the dropped-frame counter in the status bar | `viewer/ProgramViewer.tsx`, `viewer/ViewerStatus.tsx`, `shell/StatusBar.tsx` |

None of this is wrong. All of it is on screen at once, before the user has put one clip on the timeline. The
phone editor already proves a smaller model works: five dock items (media, edit, audio, look, captions), sheets
that follow the selection, one export button, over the same store and the same 34 ops.

Two things the full editor does well must not be lost:

- **The preview is the film.** Every layer, mask, grade and effect is live at an adaptive size. Simple mode keeps
  the same session; it shows less *about* the preview, not less *of* it.
- **Nothing is hidden from the render.** A timeline graded and masked in Full opens in Simple and exports the
  same film.

## 3. Principles

1. **One editor, two layouts.** No second store, no second op set, no "simple document". A shell is a view of
   `useEditorController` (§9).
2. **Tasks before tools.** Simple offers what the user wants done ("remove the silences", "add captions"), each
   backed by a feature that already exists. Tools stay in Full.
3. **Never a dead end.** Anything Simple does not show has a visible way to it: "Open in the full editor",
   which switches the shell and opens the right panel on the same selection.
4. **No lie by omission.** If the timeline holds work Simple cannot edit (colour nodes, masks, multicam, buses),
   the panel says so in one line and the preview still shows it.
5. **Quiet when fine.** Status appears when something needs the user, not as a permanent instrument panel.

## 4. Two shells

| | Simple | Full |
| --- | --- | --- |
| Places | Edit, Look, Export | the seven pages |
| Side panels | Media on the left; one panel on the right that follows the selection | 10 + 5 tabs |
| Timeline | the same canvas; headers show name, mute and lock; tools: select and split | everything |
| Viewer | the same `ProgramViewer` and session | the same, plus Source or Record, scopes, tools |
| Keyboard | the same keymap; an action that needs a hidden panel offers the full editor | everything |
| Phone | not offered (the phone surface already is the simple one) | not offered |

The switch is a two-segment control in the header, "Simple | Full", with the chord shown next to it. It is view
state:

- stored per device under `reelbolt:editor:shell` (like `reelbolt:editor:touch-drag`), not in the document and
  not in a review link, export or hand-off;
- switching keeps the timeline, the selection, the playhead, undo history and unsaved edits (they live in the
  store and the controller, which do not unmount);
- switching rebuilds the markup, so the viewer restarts its session: about as costly as changing page today.

The "Early preview" label stays on both shells until the owner lifts it.

## 5. Who starts where

With no stored choice on this device:

| Signal | Shell |
| --- | --- |
| Tablet (`useDeviceClass`) | Simple |
| Desktop, editor preferences empty (no remapped key, no saved layout) | Simple |
| Desktop, editor preferences not empty | Full |

`GET /api/v1/editor/preferences` already tells us whether someone customised the full editor, so no new field is
needed to keep existing power users where they are. The first time Simple opens, one dismissible line says what
it is and where the full editor is. After that the stored choice wins.

## 6. What Simple shows

### 6.1 Frame

```
┌────────────────────────────────────────────────────────────────────────────┐
│ ← Timelines   Spring promo · Saved      ↶ ↷     [Simple | Full]   Export   │
├────────┬───────────────────────────────────────────────┬───────────────────┤
│ Edit   │                                               │                   │
│ Look   │                    viewer                     │   panel that      │
│ Export │                                               │   follows the     │
├────────┤   ▶  00:12 / 00:45        [Ask for a change…] │   selection       │
│ Media  ├───────────────────────────────────────────────┤                   │
│ tiles  │                   timeline                    │                   │
└────────┴───────────────────────────────────────────────┴───────────────────┘
```

### 6.2 Edit

- **Left: Media.** The pool as tiles with "Add media" and drag to the timeline. Text (titles, captions) and
  Sounds are the two other entries of the same list, not tabs. Everything else in today's left group is reached
  through a task or the full editor.
- **Right: one panel.**
  - *Nothing selected:* a short task list. Each row is an existing feature with its existing cost statement:

    | Task | What it runs today |
    | --- | --- |
    | Remove the silences | the remove-silence dialog (free analysis, one undo step) |
    | Add captions | transcript analysis (metered, estimate first) then the caption look picker |
    | Cut to the music | beat analysis (free) then cut to beat |
    | Clean up the sound | audio cleanup (local targets free; hosted ones keep their consent step) |
    | Make it vertical | the timeline's format setting, then fit or fill per clip (follow-the-subject where a subject track exists) |
    | Tighten the edit | the Re-cut helper (`POST …/proposals`, estimate first, one undo step) |

  - *A clip selected:* the phone clip sheet's model in a panel (`phone/ClipSheet.tsx`, `phone/clip-sheet-actions.ts`): fit or fill,
    position, size, rotate, opacity, fades, volume, speed, split, delete. Advanced sections of the Inspector are
    not shown.
  - *A title or caption selected:* text, look, position (the 3x3 pad).
  - *Work from the full editor on this clip:* one line, for example "This clip has 3 colour steps and a mask",
    with "Open in the full editor".
- **Timeline.** The same `TimelineCanvas`. Snapping on, linked audio moves with its picture, Delete ripples (as
  today). Markers lane, Events lane, track height controls, trim-mode tools and the blade tool's variants are not
  shown; their chords still work.

### 6.3 Look

The viewer with what the phone's Look sheet already offers (`phone/look-actions.ts`, `lib/editing/color/look-gallery.ts`):
the gallery of looks with a strength, five quick fixes (brightness, contrast, saturation, warmth, tint), "apply to
every clip", plus Fix exposure from the Colour page. Each change is one `setProperty grade`, the same write the
Colour page makes, so the Colour page shows the result as ordinary nodes. No wheels, scopes or node list here.

### 6.4 Export

The Deliver page reduced to its first decision: where the video is going (the preset list), what it costs (the
`CostStatement`, only what `render/estimate` returned), the Export button, and the latest film when there is
one. Captions burned in or as a file is one switch. Queue history, encode settings, hand-off packages and review
links stay in Full, behind "More export options".

### 6.5 Asking for a change in words

A single text box under the viewer: "Ask for a change…". It is the proposals flow that exists today, not a new
model surface and not the dashboard assistant:

- The dashboard assistant has no tool that edits a timeline, so it is not the right back end; the editor's
  helpers (`recut`, `graphics`, `grade`, `sfx`, `review`) are, and they already return a diff the user accepts
  as one undo step, with an estimate, a disclosure and a rate limit.
- The request needs a helper. First version: the box opens with five chips (Edit, Titles, Colour, Sound, Check
  my video) and the text is the helper's `brief`. Picking the helper from the words alone is an open decision
  (§10): it is a new model call and needs its own measurement.
- Refusals keep their meaning: `PROPOSAL_NEEDS_ANALYSIS` becomes "This needs the transcript first (about N
  credits)", with the button that runs it.

### 6.6 What Simple never does

- Change what is rendered. Solo, compare and the viewing LUT stay view state as they are; Simple has none of
  them.
- Remove or flatten work made in Full.
- Hide a charge. Every metered task shows the same estimate and the same `402` path as in Full.

## 7. A quieter viewer

The preview is now live on every device, so most of its status is good news that does not need saying.

| Surface | Full (today) | Simple |
| --- | --- | --- |
| Parity badge | as today | only when something on screen is `approximate` or `renderRequired` |
| Dropped-frame counter | always, in the status bar (standing rule) | no status bar; a count on the viewer while frames were dropped in the last few seconds |
| Quality menu | in the viewer bar | inside a "Preview" menu with Simple preview and fullscreen |
| Adaptive-size pill | when reduced | when reduced (unchanged: it explains a soft picture) |
| Readiness chip | while preparing | unchanged |
| Dropping-frames offer of the Simple preview | at the adaptive floor | unchanged |

The dropped-frame counter row changes a rule recorded in `CLAUDE.md` ("dropped-frame counter always shown"). It
is relaxed for the Simple shell only and needs the owner's yes.

## 8. Keyboard and the command palette

The keymap stays one catalogue. A new exhaustive record in `lib/editing/commands.ts`, in the manner of
`SOURCE_DECISIONS` and `TOUCH_PARITY`:

```ts
export const SIMPLE_DECISIONS: Record<KeyAction, 'works' | 'opensFull' | 'off'> = { … };
```

A new keymap action does not compile until someone decides what it does in Simple. `opensFull` switches the
shell, opens the panel the action needs, then runs it. The palette lists every action in both shells and marks
the ones that open the full editor.

## 9. How it is built

### 9.1 Done: the workspace is split

`EditorWorkspace.tsx` was one 3,100-line component. It is now a 20-line component over two halves, with no change
in behaviour (same hooks in the same order, same element tree):

```
components/editor/EditorWorkspace.tsx        joins the two halves
components/editor/workspace/
  types.ts                                   props and the controller's input
  useEditorController.ts                     calls the domain hooks in order
  useWorkspaceCore.ts … useShellState.ts     16 domain hooks; no markup except two notification bodies
  helpers.ts, live-panels.tsx                module-level helpers and the playhead-following panel wrappers
  view/useEditorView.tsx                     builds the full workspace's markup from the controller
  view/use*View.tsx                          side panels, viewer zone, timeline zone, pages, chrome, shell
```

Each hook takes the values earlier hooks produced and returns the ones later code reads. The view hooks return
elements and keep no state; the only hook they call is `useT`, because the catalogue scanners read translator
bindings per file.

What the split did not do, said plainly: it was mechanical. The context passed between pieces is wide (a view
hook reads 50 to 90 values), and the phone surface is still assembled inside the full shell's last view hook.
Narrowing those is part of 9.2, where a second consumer makes the right boundaries visible.

### 9.2 To build

| Step | What | Server |
| --- | --- | --- |
| A | `EditorWorkspace` renders `<FullWorkspace controller>` or `<SimpleWorkspace controller>` (components, so the choice is not a conditional hook); the phone branch moves up beside them; the shell switch and its storage | none |
| B | Simple Edit: frame, Media list, the selection panel (shares `phone/clip-sheet-actions.ts` and `usePhoneActions`), the reduced timeline header | none |
| C | Task list and the ask box over the existing proposals and analysis routes | none |
| D | Look and Export places | none |
| E | Quiet viewer (§7), `SIMPLE_DECISIONS`, palette marks | none |
| F | Catalogues in `en` and `it`, user guide pages, e2e (`editor-v2` suite gains a Simple project) | none |

One viewer host: today the viewer is built twice with different parents (Edit and Colour), so `ProgramViewer`
remounts when the page changes. Step A gives both shells a single viewer host, which also removes that remount
in Full.

A test in the manner of `no-small-fonts.test.ts` counts the interactive controls the Simple shell shows at rest
and pins a ceiling, so the shell cannot quietly grow back into the full one.

## 10. Open decisions

| # | Question | Recommendation |
| --- | --- | --- |
| 1 | Names of the two shells in the interface | "Simple" and "Full" (Italian: "Semplice" and "Completo"); never "Pro", which reads as a paid plan |
| 2 | Default for existing users with empty preferences | Simple, with the one-line note; they are one click from Full and the choice sticks |
| 3 | Store the choice on the account instead of the device | Later. It needs a field in the preferences contract and its validator; the device key is enough to learn from |
| 4 | Pick the helper from the user's words | Not in the first version. Chips first; measure how often the chip and the words disagree before adding a model call |
| 5 | Dropped-frame counter hidden when zero in Simple | Yes (§7) |
| 6 | Offer Simple on the phone as a named mode | No. The phone surface stays the only phone layout |

## 11. How we will know

The dashboard sends one page view per route and no events (pinned by `components/analytics/ga-event-inventory.test.ts`),
and this proposal adds none. Two sources:

1. **Five first-time users, one task.** "Here are three clips and a logo. Make a 30-second video with captions
   and export it." Measured by hand: time to first clip on the timeline, time to export, number of times they
   ask what something is, whether they open Full and why. Run once on Full now (the baseline) and once on Simple
   before it becomes the default.
2. **What the database already holds.** For timelines created after the change: the share that reach a first
   render within 24 hours, and the time from `created_at` to that render. Splitting those by shell needs the
   shell recorded somewhere; the cheapest honest way is one additive, optional field on the render request
   (decision 3's cousin, and not needed for the first comparison, which is before versus after).

Simple becomes the default only if the five-user run is clearly better on time to export and nobody is blocked
by something that is missing without finding "Open in the full editor".

## 12. Not in this proposal

- A template or "start from a recipe" gallery inside the editor. Workflow apps already cover making a first
  video without editing; the editor is where the user adjusts one.
- New AI abilities. Every task in §6 calls something that exists.
- Any change to the full editor's layout, the phone surface, or the render.

## 13. As built (2026-11-11)

Steps A to F of §9.2 are built. Differences from the text above, and what is still open:

| Proposal | As built |
| --- | --- |
| §6.1: undo and redo in the header | They are in the timeline's toolbar, next to the tools they undo. The header carries the save state instead (Simple has no status bar). |
| §6.2: Media, Text and Sounds as entries of one list | The left column is the media pool (the same node as the Edit page's Media tab, drag to the timeline included). Titles are a task ("Add a title") and a selected title's own panel. A separate Sounds entry is not built. |
| §6.2: "Make it vertical" | The task is "Change the shape": it opens the timeline's settings (shape and frame rate). Fit or fill is on each clip's panel. It does not run follow-the-subject by itself. |
| §6.2: "Clean up the sound" | The task is "Fix the sound": the phone's Audio body (track volume, music that lowers under a voice, a cleaner voice, clip clean-up with its own prices and consent, voice-over). |
| §6.2: captions through the caption look picker | The captions panel is the full editor's Subtitles panel, unchanged. |
| §6.5: five chips | A select of the five helpers beside the text field, on one line under the viewer. |
| §7: quality menu inside a "Preview" menu | The existing preview options menu and the whole-screen button stay on the transport row. |
| §9.2 A: `<FullWorkspace>` and `<SimpleWorkspace>` components chosen in `EditorWorkspace` | The last view hook picks the surface (phone, Simple, Full). `SimpleWorkspace` is a component; the full editor's markup is still returned by the view hooks. The single viewer host for Edit and Colour in the full editor is not built. |
| §9.2: a test that counts controls | `SIMPLE_CONTROL_CEILING` in `simple-shell.test.tsx` (45, measured with the viewer and the canvas mocked and an empty pool). In a real browser the same screen with two tracks shows about 54 controls; the full editor's Edit page shows about 106. |
| §5: the choice waits for the saved preferences | A desktop with no stored choice shows an empty frame until the preferences answer, fail, or 2 seconds pass; then Simple. A tablet does not wait. |

Decisions taken as recommended in §10: the names are "Simple" and "Full"; an account with empty preferences
starts in Simple; the choice is stored on the device; the helper is picked by the user, not from the words; the
dropped-frame counter is quiet in Simple; the phone has no shell choice.

Not done, and why:

- **§11, the five first-time users.** That is a study with people; nothing in the code can stand in for it. Simple
  is the default for accounts that never customised the editor from this change on, so the before-and-after
  comparison of first renders starts now. If the study says otherwise, `defaultShell` in
  `web/lib/editing/shell-choice.ts` is the one place that decides.
- **The e2e spec has not run.** `e2e/tests/editor-v2/31-simple` needs the Docker stack. The shell was checked in a
  real browser against a mocked API (layout at 1440 px and 1000 px, the three places, a selected clip, the ask
  line, the full editor), which is how the first layout bug was found.
- A layout bug found on the way and fixed: the full editor's header left its actions in the middle of the row
  (a third, empty grid column since the page switcher moved to its own band).

