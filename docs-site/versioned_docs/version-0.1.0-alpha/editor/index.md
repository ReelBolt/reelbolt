---
title: The timeline editor
sidebar_position: 1
description: How to open a ReelBolt timeline in the editor, play it, cut and move clips, add markers, undo, and understand what the preview can and cannot show you.
---

# The timeline editor

:::note Availability in this release

The video editor is **switched off** in ReelBolt 0.1.0-alpha: its pages show **Coming soon** and do not
open a timeline. The [picture editor](./thumbnails.md) is available. The rest of this page describes the
timeline editor for releases where it is enabled.

:::

The editor lets you open a video edit as a **timeline** — tracks of clips laid out in time — and
change it by hand: trim a cut, move a clip, split one, add a title or a marker, undo a change, or go
back to an earlier version. It works on the edit itself, not on a finished video file, so every
change is cheap and reversible until you render.

The **picture editor** is the editor part available in this release, and it is an **early preview**: its
page shows an **Early preview** label. Click or tap it to read what that means — it is still being
finished; your work is saved, but some tools may change.

Each section below stands on its own.

## Opening a timeline

Go to **Editor** in the left menu. The page is one project at a time (switch it with the project
picker at the top right) and has three tabs: **Timelines**, **Media** and **Pictures**.

**Timelines** lists every timeline in the project, newest edit first, with when it was last changed
and what it started from.

- **New timeline** creates one. In the dialog, **Start from** can pick a finished workflow video: its
  cuts, titles, music and the agents' reasons become the starting point, so you tidy up what the
  agents did instead of starting over. Leave it empty for a blank timeline.
- **From your workflow runs** lists finished videos you have not opened yet. **Edit** turns one
  into a timeline and opens it. Opening the same video again (from here or from **Open in editor**
  on a workflow run) brings you back to the timeline you already made from it, never a second copy.
- **Import** reads an OpenTimelineIO (`.otio`) file from ReelBolt or another editor.
- The **⋯** menu on a timeline duplicates it, opens its review page, or copies the review link to
  send to someone who only needs to watch and comment.

Click a timeline to open it. Its address is `/app/editor/<project>/timeline/<timeline>`; you can
bookmark it. The back arrow at the top left of the editor returns to the list.

## What you see

The editor takes the whole window (the app menu folds away; the menu button at the top left brings
it back). Panels can be resized by dragging the thin lines between them, or with the arrow keys once a
line has keyboard focus.

- **Top bar**: the timeline's name and revision, the **workspaces** (Media, Cut, Edit, Compose,
  Color, Audio, Deliver) in the middle, and on the right the review page, saved layouts, keyboard
  shortcuts and **Export** (which opens Deliver).
- **Viewer** (centre): plays the timeline. Under it is the transport: mark in / mark out, previous
  and next cut, frame steps, play/pause, a timecode box, marker jumps and loop. The icons on the right
  save the current frame as a picture, show the picture without colour grades, and open the preview
  options (playback resolution, single-clip mode).
- **Timeline** (bottom): a toolbar (select, blade and hand tools, split at the playhead, snapping,
  **Remove silence**, and zoom with a **fit** button), then the ruler, a lane for markers, and the
  tracks. Each clip is a coloured block; the selected one shows trim handles at its edges.
- **Left panel**: **Markers**, **Edits** (a table of every cut with its in and out times),
  **Transcript**, **Subtitles**, **Takes** (alternate takes), **Structure**, **Comments** and
  **History** (your undo list). Tabs that do not fit scroll sideways.
- **Right panel**: **Inspector** (the selected clip's name, times, enabled flag, and its speed,
  transform, crop, fades and keyframes), **Assist**, **Settings** (the timeline's frame size and
  frame rate), **Versions** (saved versions you can go back to) and **Compare**.
- **Status bar**: the save state, how many frames the preview had to skip, the kind of preview you
  are getting (see below), the render cache and the current time.

Every workspace keeps the viewer and the timeline; it swaps the side panel for its own tools.
**Media** gives the media pool the whole centre of the screen: select a clip and press **Insert at
playhead** (it goes in at the nearest cut and pushes the rest along) or **Add to end**. The clip goes
in at its full length, or only the part between the **In** and **Out** marks you set in the source
viewer; **Create subclip** adds that marked part to the end of the timeline. A video with sound
brings its sound along on a **Camera audio** track under the picture, and one undo removes both.
If the file has not been prepared yet its length is unknown: it goes in at 5 seconds and the
editor tells you so, so you can trim it once the file is ready. The pane follows the preparation
by itself; there is no need to press **Check again**. A sound file plays in a simple audio player
and a picture is shown as a picture.

To give a clip already on the timeline its own sound, select it and press **Extract audio** in the
Inspector: the sound goes on the **Camera audio** track at the same place. Picture and sound are not
linked yet, so moving one does not move the other.

## On a phone or a narrow window

Below tablet width the editor rearranges itself: the viewer sits at the top, a switch underneath
picks **Timeline**, **Inspector** or the current workspace's tools, and the workspaces move to a bar
along the bottom edge. Everything is the same editor, so a change made on a phone is saved like any
other. For watching a render and leaving comments, the timeline's **review page** is simpler still.

On a touch screen:

- Tap a clip to select it, then use **Clip** in the timeline toolbar: **Delete** (closes the gap),
  **Lift (leave gap)**, **Split at playhead**, and the two **1 frame** buttons to nudge it earlier or
  later. Undo brings anything back.
- Drag a selected clip's yellow edge to trim it, drag the clip to move it, drag the ruler to scrub,
  and drag an empty part of the timeline to scroll it.
- To put a clip in a bin, open the **⋯** menu on its tile in **Media** and choose **Add to bin**.
- **More > Export** opens Deliver.
- The timecode box opens the number pad: type digits only, so `1215` goes to 12 seconds 15 frames.
- If the timeline toolbar has more tools than fit, swipe it sideways to reach the rest.
- If the timeline was changed somewhere else, a one-line bar says so; tap **Reload** to continue
  from the latest version.

A few tools need a larger screen and say so on a phone instead of showing a control that would not
work: compound clips, buses and automation, curves, tracking a power window through a clip,
moving bins inside other bins, dragging transcript paragraphs (the arrows still reorder them) and
selecting several clips at once (the **Clip** menu says so).

## Playing and moving around

| Action | Keys |
|---|---|
| Play / pause | `Space` |
| Shuttle backwards / stop / forwards (press again for faster: 1x, 2x, 4x, 8x) | `J` / `K` / `L` |
| Step one frame | `←` / `→` |
| Jump to the previous / next cut | `↑` / `↓` |
| Jump to the start / end | `Home` / `End` |
| Mark in / mark out at the playhead; clear both | `I` / `O` / `Alt+X` |
| Play from the in mark | `Shift+Space` |

Your in and out marks are saved with the timeline: they are still there after you reload the page,
and undo and redo bring back earlier marks too. A mark is only saved once both are set and the out
mark comes after the in mark. When both are set, **Deliver** starts with the render limited to that
range.

You can also click or drag on the ruler to move the playhead, and type a time into the timecode box.
Typing digits fills the box from the right, so `1215` means 12 seconds and 15 frames. Typing `+15`
moves 15 frames forward, `-2s` two seconds back, `+1:00` one second forward.

## Selecting, cutting, moving and trimming

- **Click** a clip to select it; **Shift-click** another clip on the same track to select everything
  between them; **drag on an empty part of a track** to draw a box around several clips;
  `Ctrl/Cmd+A` selects everything.
- The **Select** menu above the timeline offers **Select all**, **Select forward from the
  playhead**, **Select backward from the playhead**, **Select all on this track** and **Deselect**.
  The keys `A` and `Shift+A` select every clip from the playhead forward or backward (a clip the
  playhead is in counts both ways); `Alt+A` selects every clip on the track of the selected clip.
- **Blade** (`B` or the scissors button) splits a clip where you click. **Blade at playhead** splits
  every clip under the playhead.
- **Move**: drag a clip along its track or onto another track of the same kind.
- **Trim**: drag the handle at either end of the selected clip.
- **Lift**: `Delete` removes the selected clips and leaves a gap where they were.
- **Snapping** (`N`) makes a dragged edge jump to nearby cuts, markers, the playhead and the in/out
  marks. Turn it off when you need to place something between them.
- **Select tool / hand tool** (`V` / `H`): the hand tool pans the timeline instead of editing it.

A dragged clip only becomes a change when you let go, so one drag is one undo step.

The **Add** menu above the timeline places a three-second **Title** or **Colour card** at the
playhead, on the top video track that has room there (or on a new video track above the others).
The new item is selected so you can change it in the Inspector straight away, and one undo removes
it.

## Markers

Press `M` to add a marker at the playhead, or type a name in the **Markers** panel and press
**Add**. Click a marker to jump to it; the panel lets you rename or remove it.

Timelines that came from an agent run also show **reasoning markers**: for each cut the agent made,
the reason it gave, at the frame it applies to. They are labelled with the agent and the AI service
that made the decision. You can read them but not edit them — they are the agents' notes, not yours.

## Undo, redo and saving

- `Ctrl/Cmd+Z` undoes, `Ctrl/Cmd+Shift+Z` (or `Ctrl/Cmd+Y`) redoes. The **History** panel lists every change
  in the session; click any entry to jump back to that point.
- Saving is automatic. The status bar shows **Saving…**, then **Saved · rev N**. You never need to
  press a save button.
- If the editor says **Invalid** it found a problem with your last change and did not send it; undo
  the change.
- If it says **Read-only** with a banner, someone else (or you in another tab) saved a newer version
  while you were editing. Your last changes stay on screen so you can see them, but they are not
  saved. Press **Reload latest** to continue from the newest version; your unsaved changes are
  discarded, never merged automatically — this is deliberate, so two people can never silently
  overwrite each other's work.

## Going back to an earlier version

The **Revisions** panel lists saved versions. Not every revision can be restored: ReelBolt keeps a
full copy of the first version, of every twentieth, and of the current one, and the panel marks
which ones have a copy. **Restore** brings that copy back as a *new* revision, so nothing is ever
lost — you can restore the restore.

## What the preview shows you, and what it cannot

The viewer plays the timeline itself, not a rendered file. To do that quickly it uses proxy
(low-resolution) copies of your clips, so:

- A badge over the viewer says how faithful the current frame is. **Faithful** means what you see
  is what the render will do (cuts, which clip is on top, volume). **Approximate** means the render
  will differ in detail (titles and captions are drawn with a stand-in font, animated values are
  interpolated by the browser). **Render required** means the effect is not previewed at all.
- The status bar counts **dropped frames**. If the number climbs, your computer cannot decode and
  draw fast enough; the **playback resolution** toggle (full / half / quarter) trades sharpness for
  smoothness. The count is never hidden, so you always know when the preview is cheating.
- The status bar also says which **kind of preview** you have: **live** (the full compositor),
  **fallback** (a simple video player — your browser lacks the features the compositor needs, and
  cuts between clips are approximate), or **unavailable** (no proxy exists for the clip yet — the
  Media workspace shows what has been generated).
- A clip with no proxy yet shows a placeholder until the proxy is ready.

The finished video is always produced by a **render**, never by the preview. You start one from the
**Deliver** workspace; see [Rendering a finished video (Deliver)](#rendering-a-finished-video-deliver).

## Tracks and timeline settings

The small **+V** and **+A** buttons above the track names add a video or an audio track at the
bottom of the stack; the bin icon on a track removes it, with everything on it, as one undoable
step. The lock, eye, mute and solo buttons on a track are saved with the timeline now, so they are
the same when you reopen it. The **Settings** tab on the right edits the frame size, frame rate,
sample rate, pixel aspect, aspect policy and start timecode: change what you need and press
**Apply** — one undoable change for all of it.

## Nudging clips

With one or more clips selected, press `,` to move them one frame earlier and `.` one frame later;
hold Shift for ten frames. A nudge that would overlap another clip or start before the beginning is
refused and nothing moves.

## Removing silence

**Remove silence** (in the timeline toolbar) lists the pauses the footage analysis measured, mapped onto the
clips of the chosen track, each with its timecode and length. Untick the ones you want to keep, choose
how many frames to leave on each side of a cut and how short a pause to ignore, read the summary
("3 cuts, 212 frames removed"), and press **Remove**. All the cuts land as one step, so one Undo
brings everything back. The dialog can only offer what was measured: a timeline that was not seeded
from an analysed run has no pauses to list, and the dialog says so.

## Rendering a finished video (Deliver)

Open the **Deliver** workspace (or press **Export** at the top right). Choose a format — HD 1080p, 4K,
vertical, square, portrait, the timeline's own size at master quality, or audio only — decide whether
to render the whole timeline or only the part between your in and out marks, give the file a name
(or leave it blank to name it after the timeline), optionally normalise the loudness, and press
**Render**. The render runs like any other job: the queue below shows it queued, running with its
progress, and then finished with a download button, or failed with the reason. **Stop** halts a
running render. Unsaved edits are not in a render until they save, and the button says so.

If a finished render had to leave something out (a screen insert, which rendered videos do not include yet, for example) or has
something to tell you, its row says so in a line such as "2 items were left out of this render · 1
note". Tap or click **Show details** to read each one; an item is named the way it is named on your
timeline.

Your plan sets the highest resolution an export can have, measured on the short side of the picture
(see [Export resolution on each ReelBolt plan](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan)).
HD 1080p, vertical, square, portrait and master quality are always available: if the format is
bigger than your plan allows, the export is scaled down to your plan's limit with the same shape and
the same quality settings, and the form says "Exports at {cap}p on your plan." (for example, a
vertical export on Trial comes out at 720×1280). 4K is a size you choose on purpose, so it is
greyed out when your plan does not include it, with a note naming your plan's limit and a link to
the plans.

Each render remembers which parts of the timeline it could reuse from earlier renders: after a
render of the version you are looking at, a thin strip at the foot of the ruler turns blue where a
part was reused and red where it was rendered afresh, and the status bar shows the count. Edit
anything and the strip disappears until the next render — it never shows the cache of a version that
is no longer on screen.

## Moving, scaling, rotating and fading a clip

Select a clip and open the **Inspector**. The **Transform** section moves the picture (Position X and
Y are fractions of the frame, so 0.25 is a quarter of the width to the right), scales it, rotates it
and sets its opacity; **Crop** trims each edge; **Fades** fade the picture and the sound in at the
start and out at the end over the number of frames you give. Every one of these is saved with the
timeline, shown in the viewer and used by the render.

## Animating a value (keyframes)

Beside each number in the Transform and Audio sections is a small diamond. Put the playhead where you
want the change to start and click the diamond: that sets a **keyframe** there with the current value.
Move the playhead, change the number — with keyframes on, the field now sets a keyframe at the
playhead instead of changing the whole clip. The button showing the count opens the **curve**: click a
point to select it, drag it left or right to move it in time, pick how it eases into the next point,
or delete it. Everything is undoable, one step per change.

## Speed, reverse and freeze frame

**Speed** in the Retime section plays the clip faster or slower; the clip's length changes to match
and everything after it moves along. **Reverse** plays it backwards (up to a minute of footage).
**Freeze frame** holds the clip's first frame for its whole length, silently.

## Transitions

Select a clip that is directly followed by another one on the same track. The inspector offers
**Add transition**: choose the type (a dissolve, wipes, slides, zooms…) and the length in frames, and
it is placed centred on the cut. The clip before gives up half its length to the transition and the
clip after starts half a transition later — which means the second clip needs some footage *before*
its first frame to show during the blend; the inspector tells you how much it has. Select the
transition itself to change its type or easing, or to remove it and go back to a hard cut. The viewer
shows every transition as a dissolve; the render uses the type you chose. For now the render always
uses a linear ease, whatever easing you pick.

## The mixer (Audio workspace)

Open the **Audio** workspace. Each audio track has a fader, a pan control, mute and
solo, a simple equaliser (low cut, high cut, one band) and a level meter. The **Master** section
shows the loudness of what is playing (momentary, short-term and integrated, in LUFS, plus the peak)
and, beside it, the loudness the **last render** measured — that second number is the one to trust,
because the live meter samples the sound rather than hearing every bit of it.

## Ducking music under speech

In the mixer's **Ducking** section pick the music track and the track(s) with the speech, choose how
far the music drops and how quickly, and press **Derive ducking from dialogue**. The music track's
fader gets a set of points that dip wherever speech plays. They are ordinary keyframes: open the
track's automation lane to drag, delete or re-time them, or re-derive after you re-cut the speech.

## Editing from the transcript (Text)

If the timeline was seeded from an analysed run with transcription on, the **Text** tab in the left
panel shows what was said, as paragraphs of words. Click a word to jump to it; shift-click another to
select the run between them; **Delete words** cuts exactly those frames out of the track and closes
the gap — one undo step, like any cut. **Find fillers…** lists every "um", "uh" and stutter it can
hear; untick the ones you want to keep and remove the rest in one go. Drag a paragraph onto another
(or use its arrows) to move that part of the recording earlier or later. Words shown faded are no
longer on the track; a word marked ≈ has estimated timing, so a cut on it lands within its sentence
rather than on the word's own edge.

## Subtitles

The **Subs** tab generates captions from the transcript: choose how many characters fit on a line and
how many lines a caption may have, pick the track to put them on and press **Generate captions**. By
default the captions go on the timeline's existing captions track; the first time, when there is
none, the editor adds a "Subtitles" track. Generating again fills only the gaps: a caption that would
overlap one already on the track is skipped, so you never get a second copy. Each caption is an ordinary clip: edit its text in the list, switch
it off, **split** it where the playhead sits or **merge** it with the next one. Captions on an
enabled track are **burned into** the render; for a separate subtitle file instead, disable the
subtitle track and download **SRT** or **WebVTT** from the same tab (they carry what is saved, so
save first).

## The Cut workspace

The **Cut** workspace is for assembling fast. The strip at the top shows the whole
timeline at once — click it to jump; the detail timeline follows. Under it, the **source tape** lists
the project's media: **Insert** drops a source at the nearest cut to the playhead and pushes what
follows along. **Review** plays each clip at a speed scaled to its length, so a long shot does not take
long to check. **Boring shots** lists the shots the analysis measured as long and still — **Show**
selects one, **Remove** cuts it out and closes the gap.

## Alternate takes

Where the analysis found near-identical shots, the **Takes** tab stacks them: for each clip with
alternatives it shows the take you are using and the others, marking the one the analysis rated best
and marking a take shorter than the clip as **short**. **Use this take** swaps the footage while
keeping the clip's place and length. It refuses a take shorter than the clip, because the clip would
run on into the footage after that take, and tells you how long the take is and how long the clip
needs; shorten the clip first or pick a longer take.

## Comparing two timelines

The **Compare** tab in the right panel lists the differences between another timeline of the project
(a duplicate, an earlier cut) and the one you are editing: clips added, removed, moved, trimmed or
changed, plus track and settings changes. Click a time to jump there.

## Exporting, importing, stills and single clips

In **Deliver**, the **Export the timeline** links download the saved timeline as **OpenTimelineIO**,
a **CMX3600 EDL**, or **SRT**/**WebVTT** captions. OpenTimelineIO keeps everything, so **Import OTIO**
on the Timelines page brings a timeline back whole — or imports the cuts of a file another editor
made, as long as every clip's media is in this project (it tells you which are missing otherwise).
**Still** above the viewer saves the current frame as a PNG at preview quality — handy for a reference,
not a finished frame. With one clip selected, **Export only "…"** in Deliver sets the in and out
marks to that clip so the next render covers just it.

## Keywords, labels and smart bins

In the **Media** workspace, selecting a clip shows **Notes and keywords**: add keywords, a note, a
colour label, and mark it a favourite or flag it, then **Save**. In the bin tree, **Smart bin (saved
filter)…** creates a bin that always shows every source matching a filter — keywords, kind, label,
favourite, flag, part of the name — instead of what you drop in it. Tag the clips and the smart bins
fill themselves.

To put a clip in an ordinary bin, drag its tile onto the bin, or open the **⋯** menu on the tile and
choose **Add to bin**.

## Grading a clip (Color workspace)

Open the **Color** workspace with a clip selected. **Add correction** gives you lift,
gamma, gain and offset for red, green, blue and all together, plus exposure, contrast (around a
pivot), saturation, hue, temperature and tint; **Curves** lets you click points onto a tone curve
for luma or one channel; **LUT** applies a `.cube` file from your project at the strength you
choose. Each correction has its own on/off switch, and the switch at the top bypasses the whole
grade. **Add look** turns the words a Colorist agent uses — Warm, Cool, Filmic, Vibrant, Muted,
Mono, at a strength, with lifted or deepened shadows and softened or brightened highlights — into an
ordinary correction you can keep, tweak or delete. **Grade on/off** above the viewer compares before
and after in the preview only: renders and stills always use the grade as it is switched.

## Colour groups

Clips graded together belong to a **colour group**: select them and press **Group selected clips**,
or **Groups from look groups** to make one group per set of similar-looking shots the analysis
found. A group's grade is applied first, then each clip's own. Pick the group in the Color
workspace's target menu to grade it; rename it, add the selected clips, or remove members in the
list.

## Stills, wipe, scopes and shot match

**Grab still** keeps a picture of the current frame in the gallery; click one to make it the
reference and drag **Wipe** to compare it against the live picture in the viewer. The **scopes**
(waveform, parade, vectorscope, histogram) read the composited preview, grade included. **Match to
reference** adds a correction that moves the current clip's brightness, balance and contrast toward
the reference still — a starting point to refine, not a guarantee.

## Masks, keys, pins and effects (Compose workspace)

Open **Compose** with a clip selected. A **shape mask** shows the clip only inside (or outside) a
feathered rectangle or ellipse; a **chroma key** makes a colour transparent, with spill removal;
a **corner pin** places the whole picture onto four corners; **effects** (blur, sharpen, vignette,
grain, pixelate, mono) stack in order with an amount each; **Stabilise** steadies shaky footage in
the render; **Auto-reframe** crops to the timeline's aspect around a subject point; **Slow motion**
can interpolate frames for smoother slow motion. None of these show in the preview — the badge above
the viewer says so — the render applies them. For an **insert**, Compose edits its matte and the
corners of its tracked quad at the playhead. With nothing selected, **Add adjustment clip** puts a
clip on a new top track whose grade and effects apply to everything beneath it for its length.

## Title templates

Select a title: the inspector's **Template** offers a lower third, a boxed title, an outlined one
and a card, in addition to the plain centred title.

## Nesting clips (compound clips)

Select some clips and open the **Structure** tab: **Make compound** turns them into one clip that
behaves like any other — move it, trim it, cut it. **Open** shows what is inside so you can edit it
(cut, move, trim, delete, undo); **Save to compound** puts your changes back, and one Undo outside
takes the whole session back. **Decompose** puts the clips back on the timeline as they were.

## Several cameras of one moment (multicam)

Select two or more clips recorded at the same time (the first one is the reference) and press
**Sync by waveform**: the editor listens to each clip's sound and lines them up. Check the result —
a *low confidence* badge means the sound did not match clearly, and you can type the offset
yourself. **Make multicam** replaces them with one clip. Play it and click a camera's button to cut
to it at that moment; stop and click a button to change the camera of the section under the
playhead. Each cut is one Undo.

## Buses, compression and clean-up (Audio workspace)

Under the mixer, **Buses and processing** lets you group tracks: add a bus (say, *Dialogue*), route
tracks to it, and route buses into each other or to the master. Each track and bus can have a
compressor, limiter, noise gate, de-esser, noise reduction and a leveler that evens out speech. These
apply in the rendered video; the preview plays without them.

**Automation**: set a track to **Latch**, play, and move its fader — when you stop, your moves are
written as the track's volume curve (one Undo). **Read** plays the curve back on the fader.

**Channels**: in the inspector, *Audio channels* fixes a clip recorded on one side only, folds it to
mono or swaps left and right.

## Recording a voiceover

In the Audio workspace, **Record at playhead** records from your microphone; **Stop and place**
saves the take to the project's files and puts it on a free audio track (or a new *VO* track) where
you started. If your browser cannot record, the panel says why.

## Asking the AI for suggestions (Assist)

Open the **Assist** tab, pick an assistant — *Re-cut*, *Graphics*, *Grade*, *Sound effects* or
*Review* — optionally mark in and out to limit it to part of the timeline, add a short brief, and
press **Propose**. Each suggestion is listed with its reason, and the timeline shows what would
change: green outlines for additions, red hatching for removals, orange for changes. Suggestions
that would change a clip **you** made are marked; tick *Allow changing your clip* to accept them.
Untick anything you do not want and press **Accept selected** — it applies in one step (one Undo),
and the rest stay listed. If no AI provider is set up, the panel says so; an admin adds one under
Admin → Inference providers.

## Working offline

If your connection drops, keep editing: the status bar says **Offline** and your edits are kept on
this device (even if you close the tab). When the connection returns they are saved. If someone else
changed the same part of the timeline in the meantime, the editor will not guess: it keeps your
edits, shows **Download my offline edits**, and asks you to reload.

## Shortcuts and layouts

**Shortcuts** (top right) lists every keyboard shortcut; click **Change** and press the keys you
want. Two actions on one key are flagged and must be fixed before saving. **Layout** saves the panel
sizes and open tabs of the workspace you are in, so you can switch back to them later. Both follow
you to every project.

## Presets

Save the settings you reuse as project presets: a transition (in the inspector, between two clips),
an effect stack (Compose), or a loudness target (Deliver — the editor also offers Social −14 LUFS,
Podcast −16 LUFS and Broadcast −23 LUFS). Pick a preset to apply it; deleting a preset does not
change anything you already applied it to.

## Correcting one colour or one area (Color workspace)

In a correction, **HSL qualifier** limits it to a range of colours (hue, saturation, lightness), and
**Power window** limits it to an ellipse or rectangle. **Track through clip** follows what is inside
the window as it moves. These show in the rendered video; the preview shows the clip without them.

## Review and comments

The **Review** tab holds timecoded comments: type, press **Comment** at the playhead (or over the
marked range), reply, and resolve. **Review link** opens a simple page that works on a phone: the
latest render, its timecode, and the comments — share it with anyone who can open the project.

## Final Cut Pro

**Deliver → Export the timeline → Final Cut Pro XML** downloads the cut (clips, gaps, transitions,
markers, extra tracks as connected clips) for Final Cut Pro. Titles, grades and effects are not
included.

## What the editor does not do

- It does not group or link clips so that they move together.
- It does not automate a bus's volume.
- The preview does not play compression or noise reduction and does not show colour secondaries; the
  render applies all three.
- It has no blend modes and no speed ramps, and it does not preview grades made with curves or LUTs.
