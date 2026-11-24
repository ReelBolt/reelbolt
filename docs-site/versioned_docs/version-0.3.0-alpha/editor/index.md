---
title: The timeline editor
sidebar_position: 1
description: How to open a ReelBolt timeline in the editor, play it, cut and move clips, add markers, undo, and understand what the preview can and cannot show you.
---

# The timeline editor

:::note Early preview

The video editor and the [picture editor](./thumbnails.md) are both available as an **Early preview**:
every editor page shows an **Early preview** label. Click or tap it to read what that means: the editor
is still being finished, your work is saved, but some tools may change.

:::

The editor lets you open a video edit as a **timeline**, tracks of clips laid out in time, and
change it by hand: trim a cut, move a clip, split one, add a title or a marker, undo a change, or go
back to an earlier version. It works on the edit itself, not on a finished video file, so every
change is cheap and reversible until you render.

The **picture editor** is part of the same early preview, so its page shows the same **Early preview**
label.

Each section below stands on its own. To learn how the editor is laid out, start with
[Pages and panels](pages-and-panels.md); the full list of keys is on
[Keyboard shortcuts](shortcuts.md).

## Opening a timeline

Go to **Editor** in the left menu. The page is one project at a time (switch it with the project
picker at the top right) and has two tabs: **Videos** and **Pictures**.

**Videos** lists every video timeline in the project, newest edit first, with when it was last changed
and what it started from.

- **New timeline** creates one. The name starts as "Untitled video", already selected, so you can type over it. For a blank
  timeline pick a **Format** (Landscape 16:9 1080p, 4K landscape, Vertical 9:16, Square 1:1 or Portrait 4:5) and a **Frame rate**
  (24, 25, 30, 50 or 60 frames per second, or **Match my first clip**, which uses the frame rate of the first video in your project
  and says so if none is known yet). **Advanced** holds the pixel shape and the **Start timecode**. **Start from** can pick a finished
  workflow video instead: its cuts, titles, music and the agents' reasons become the starting point, and it keeps that video's own size
  and frame rate.
- Above the list, **Search** narrows it by name as you type, and **Sort** orders it by Recently edited, Name or Created. The browser
  remembers the order.
- A timecode field takes digits from the right: type 1215 and it reads 00:00:12:15. Enter keeps it, Esc cancels, and anything that is not
  a real time goes back to what it was with a short reason underneath. On a phone the number keypad opens.
- In the editor's **Timeline settings**, **Format** and **Frame rate** use the same words. Once there are clips on the timeline the frame rate
  is locked, with the reason shown: "The frame rate can't be changed once there are clips on the timeline. Start a new timeline at the
  frame rate you want."
- **From your workflow runs** lists finished videos you have not opened yet. **Edit** turns one
  into a timeline and opens it. Opening the same video again (from here or from **Open in editor**
  on a workflow run) brings you back to the timeline you already made from it, never a second copy.
- If something from the run cannot be placed, the editor tells you how many items were left out.
- **Import** reads an OpenTimelineIO (`.otio`) file from ReelBolt or another editor.
- The **…** menu on a row can **Rename** the timeline, **Duplicate** it, open its **review page**, copy the review link, or **Delete** it. Delete asks first and tells you what happens: the timeline moves to **Recently deleted** for 30 days, its review page and
  comments stop working while it is there, and they come back when you restore it. Videos you already rendered stay in your project. Right after
  deleting, a message with **Undo** stays for a few seconds. **Recently deleted** (under the list) shows how long ago each one went and how many days
  are left; **Restore** brings it back with all its edits. After 30 days it is removed for good.
- The **⋯** menu on a timeline duplicates it, opens its review page, or copies the review link to
  send to someone who only needs to watch and comment.

Click a timeline to open it. Its address is `/app/editor/<project>/timeline/<timeline>`; you can
bookmark it. The back arrow at the top left of the editor returns to the list.

## What you see

The editor takes the whole window (the app menu folds away; the menu button at the top left brings
it back). Panels can be resized by dragging the thin lines between them, or with the arrow keys once a
line has keyboard focus.

- **Top bar**: the timeline's name and revision, the **pages** (Media, Cut, Edit, Compose,
  Colour, Audio, Deliver) in the middle, and on the right the review page, saved layouts, keyboard
  shortcuts and **Export** (which opens Deliver). Each page has its own address, so a link can open
  the page you mean (see [Pages and panels](pages-and-panels.md)).
- **Viewer** (centre): plays the timeline. Under it is the transport: mark in / mark out, previous
  and next cut, frame steps, play/pause, a timecode box, marker jumps and loop. The icons on the right
  save the current frame as a picture, show the picture without colour grades, and open the preview
  options (playback resolution, single-clip mode).
- **Timeline** (bottom): a toolbar (select, blade and hand tools, split at the playhead, snapping,
  **Remove silence**, and zoom with a **fit** button), then the ruler, a lane for markers, and the
  tracks. Each clip is a coloured block with its name and length, a thumbnail or a filmstrip for
  pictures and a waveform for sound; a hatched edge means the clip is trimmed and there is more
  of the source behind it. The selected clip shows trim handles at its edges.
- **Track headers** (left of the tracks): the target, lock, eye (video) or mute and solo (audio)
  buttons, and a **Track options** button. Its menu renames the track (or double-click the name),
  colours it, makes the row Small, Medium or Large, moves it up or down (or drag the handle beside
  the row), adds a video or audio track above or below it, and removes it. Removing a track that
  has clips asks first, and **Undo** brings it back. **Show waveforms**, **Show thumbnails** and
  **Show clip names** only change what you see, not your edit.
- **Left panel**: **Markers**, **Edits** (a table of every cut with its in and out times),
  **Transcript**, **Subtitles**, **Takes** (alternate takes), **Structure**, **Comments** and
  **History** (your undo list). Tabs that do not fit scroll sideways.
- **Right panel**: **Inspector** (the selected clip's name, times, enabled flag, and its speed,
  transform, crop, fades and keyframes), **Assist**, **Settings** (the timeline's frame size and
  frame rate), **Versions** (saved versions you can go back to) and **Compare**.
- **Status bar**: the save state, how many frames the preview had to skip, the kind of preview you
  are getting (see below), the render cache and the current time.

**Versions** lists the points you can go back to, grouped by editing session and newest first (for example "Today 14:02-14:40"). Each one says why it
was saved: an auto-saved checkpoint, before you restored, before a render, before an AI suggestion, or a version you named. Auto-saved checkpoints are
kept for 30 days and the row says how many days are left; name a version to keep it forever. Press **Save a named version now** to name where you are,
or use **Name this version**, **Rename** and **Remove the name** on any row. **Restore** asks first and keeps your current state as "Before you
restored", so you can always come back. Accepting an AI suggestion saves a "Before an AI suggestion" version first. On a phone the panel is a sheet at the
bottom of the screen.

Every page keeps the viewer and the timeline (Media shows the timeline on request, and Deliver has
none) and gives its side panels to the tools that page needs.
**Media** puts the bins on the left, the pool of clips in the middle, and the selected clip's viewer
and details on the right (the timeline stays hidden until you choose **Show timeline**): select a clip
and press **Insert at
playhead** (it goes in at the nearest cut that fits and pushes the rest along), **Overwrite** (it
goes in exactly at the playhead and replaces whatever is under it; nothing moves), **Add to end**, or
**Place on top** (exactly at the playhead on the topmost free track, or on a new track, without
touching other clips). After each one a message tells you where it landed ("Placed on Video 1 at
00:00:12:04") with an **Undo** button. The clip goes
in at its full length, or only the part between the **In** and **Out** marks you set in the source
viewer; **Create subclip** adds that marked part to the end of the timeline. A video with sound
brings its sound along on a **Camera audio** track under the picture, and one undo removes both.
If the file has not been prepared yet its length is unknown: it goes in at 5 seconds and the
editor tells you so, so you can trim it once the file is ready. The pane follows the preparation
by itself; there is no need to press **Check again**. A sound file plays in a simple audio player
and a picture is shown as a picture.

To give a clip already on the timeline its own sound, select it and press **Extract audio** in the
Inspector: the sound goes on the **Camera audio** track at the same place, linked to the picture.

### Picture and sound travel together (linked clips)

A clip and its sound are **linked**: a small chain mark sits on both, the Inspector shows a **Linked**
note, and clicking one selects both. Moving, nudging, trimming, splitting, deleting, copying, pasting,
duplicating, changing the speed and disabling a clip all act on the pair, and one undo reverts the
whole change. Delete closes the gap on both tracks.

To cut the sound on its own (a J or L cut, where the sound starts before or ends after the picture),
hold **Alt** (Option on a Mac) while you drag a trim handle or a clip, or turn on **Edit sound
separately** in the toolbar and select just the picture or just the sound. The two stay linked. To
break a pair apart, select it and choose **Unlink** in the clip menu (**Ctrl+Alt+Shift+L**); **Link
selected clips** (**Ctrl+Alt+L**) joins one picture clip and one sound clip from the same source again.

In the Inspector, a selected pair shows a **Picture / Sound** switch to choose which of the two you are
editing. Timelines made before linked clips show a banner, **Add sound tracks to my clips**: it links
the pairs you already have and adds missing sound, tells you in plain words what it did, is one undo
step, and never runs by itself. **Not now** hides it for that timeline.

## On a phone or a narrow window

On a phone (a screen narrower than 600 pixels) the editor shows a phone view with the whole screen to itself. The top
bar has **Back**, the timeline's name and whether it is saved, **Undo**, **Redo**, **Export** and a **⋯** menu (Assistant,
Review, History, **Drag clips with your finger (beta)** and **Open full view**). The viewer sits under it with play,
previous and next edit, and marker buttons; the timeline strip is below, and the bar at the bottom has **Media**, **Edit**,
**Audio**, **Look** and **Captions**. Each opens a panel that slides up: drag its handle down to make it smaller or close it,
tap the handle to make it taller, or press the phone's back button. While a panel is open the viewer shrinks so the timeline
stays in sight. Turned sideways, the viewer moves to the left, the timeline to the right and the panels open from the right edge.
A note says "Phone view. Some tools need a larger screen."; **Copy link** lets you open the editor elsewhere and **Open full
view** loads the tablet layout and remembers the choice on that device. A tablet (600 to 1100 pixels) keeps the full layout with bigger touch targets: the workspaces sit in a narrow column on the left, and on a narrow tablet the tools and the inspector slide in from the sides with the buttons above the picture. The tablet works, but is not certified yet, and the editor says so in the header's More menu.
Everything is the same editor, so a change made on a phone is saved like any other. For watching a render and leaving
comments, the timeline's **review page** is simpler still.

Where to find things on a phone:

| You want to | Tap |
|---|---|
| Add photos, videos or sound | **Media** in the bottom bar, or **Add media** in the bar above it |
| Cut, remove, copy or move a clip | Tap the clip, then the bar above the bottom bar (**Split**, **Duplicate**, **Trim**, **Nudge**, **More**, **Delete**) |
| Fix the sound | **Audio** (**Duck music**, **Clean voice**, volume, voiceover) |
| Change how it looks | **Look** (looks, brightness, contrast, titles) |
| Fix a caption or cut the silences | **Captions** |
| Make the film and send it | **Export** in the top bar, then **Render**, **Download** or **Share film** (see [Export your film](export-your-film.md)) |
| Watch it and read comments | **⋯** then **Review** (the review page works on a phone) |
| Work on a picture or thumbnail | **Pictures** on the Editor page (see [Pictures on a phone](thumbnails.md#on-a-phone)) |
| Use the whole editor | **⋯** then **Open full view** (it is not tuned for phones) |

On a touch screen:

- Tap a clip to select it. The bar above the bottom bar then shows **Split** (at the playhead), **Duplicate**, **Trim**,
  **Nudge**, **More** and, set apart from the rest, **Delete** (it closes the gap, on the linked sound too). Nothing
  asks "are you sure": **Delete** and **Lift** show **Undo** for six seconds, and Undo in the top bar still brings anything back.
- With nothing selected the same bar offers **Add media**, **Add text** (a title at the playhead), **Marker** and **Select**.
- **Select** switches on Select mode: tap clips one by one to build a group (a picture and its linked sound come together),
  then **Duplicate**, **Nudge**, **Lift** or **Delete** them all at once. **Clear** empties the group; **Done** leaves the mode.
- **Trim** opens a sheet with the clip's In, Out and Duration (type a frame count or a timecode), **-10 / -1 / +1 / +10**
  frame buttons for the start and the end, and the trim mode (Shift everything after, Move the cut, Change the part shown, Move the clip). Changes show straight away and
  one **Undo** takes back everything you did in that sheet. If a change cannot be made, the sheet says why.
- **Clip** (a button on the timeline strip when a video or sound clip is selected, also in **More**) opens the clip sheet: **Fit**
  and **Fill**, a 3 by 3 pad to place the picture, **Across the frame**, **Up and down**, **Size** and **Opacity**, **Speed**
  (0.5x, 1x, 1.5x, 2x, or any speed), **Volume** with **Mute**, and **Fade in** and **Fade out**. Every slider has a number box
  next to it for typing a value; each change is one **Undo**. Anything animated on desktop is shown but not changed here
  ("Animated on desktop"), and rotation, crop, reverse and transitions stay on desktop; a clip that already has them says so.
- **Look** opens **Look and fix**: pick a look by name (or **Original**), set **How strong** it is, and adjust Brightness, Contrast,
  Saturation, Warmth and Tint. **Show the original** (or hold **Hold to compare**) shows the picture without its colour. **Apply to
  all clips** gives every video clip the same look and settings in one step, so one **Undo** takes it back. **Titles** adds a title
  at the playhead (type the words, pick Plain, Lower third, Boxed, Outline or Card) and edits the words of the title you selected.
  Colour wheels, curves, keying, masks and reframing are on desktop; a clip that already has them keeps them.
  Footage shot in HDR on a phone is converted to standard colours before your grade is applied, so it does not wash out or blow out. The preview shows an HDR clip approximately (the clip is marked **HDR clip**); the finished film is exact.
- **More** has **Select several clips**, **Fit, position and fades**, **Speed**, **Volume**, **Move earlier** and **Move later**
  (swap places with the neighbouring clip), **Extract audio**, **Freeze frame**, **Link picture and sound** and
  **Unlink**, **Copy**, **Cut** and **Paste at the playhead**, **Previous marker** and **Next marker**,
  **Clear in and out**, **Snapping**, and **Lift (leave a gap)**.
- The phone has no timeline toolbar: **Undo** and **Redo** are in the top bar, and the timeline strip has **Fit** (the whole timeline across the screen); pinch to zoom.
- Drag a selected clip's yellow edge to trim it, drag an empty part of the timeline to scroll it, pinch to zoom, and drag two fingers
  to scroll without changing what is selected. Double tap a clip to zoom to it. Every one of these has a button too (**Trim**, **Fit**,
  the previous and next clip buttons). Moving a clip by pressing and dragging it is experimental and off until you switch it on in
  **⋯** (**Drag clips with your finger**); until then, press and hold a clip for its menu, or use **Nudge** and **Move earlier / later**.
- **Media** (and **Add media** in the bar) opens the Add media sheet. **Choose photos, videos or sound** lets you pick files from your
  camera roll or files (several at once), **Take a photo or video** uses the camera, and each file shows how far it has got ("Uploading 40%", "Added",
  or "Did not upload" with **Try again**). Each tile says whether its preview is **Ready**, **Processing preview**, or "Preview not ready - you can
  still add it". Tap **Actions** on a tile for **Insert at playhead**, **Add to end**, **Add to bin**, **Remove from bin**, **Keywords and notes** and
  **Preview**. After you add a clip the sheet shrinks so you can see the timeline, and **Undo** is offered for six seconds. The bin chips at the top
  filter the tiles, and **New bin** makes one from a name. Drag a finger sideways over a tile's picture to look through the clip.
- **Export** in the top bar opens the export sheet: **Render** stays at the bottom, and a finished film can be downloaded or shared right there.
- The timecode box opens the number pad: type digits only, so `1215` goes to 12 seconds 15 frames.
- **Audio** opens the audio panel. **Duck music under the voice** lowers the music wherever someone talks, and **Clean voice** removes
  background noise and evens out the volume on the voice tracks; each is one tap and one **Undo**. Every audio track has **Mute**, **Solo** and a
  volume slider (**Back to 0 dB** resets it), and the selected clip's volume is there too. **Voiceover** records from the microphone at the
  playhead; if the browser cannot record it says "Recording is not available in this browser. Upload a voice file instead." and lets you pick a file.
- **Captions** opens the captions panel: each caption can be edited (a full-width box, saved when you tap **Done**), **Split** at the playhead,
  **Merge with next** or **Delete**; **Add at the playhead** and **Generate from speech** make new ones. **Remove silence** shows the quiet stretches
  and what cutting them will do before anything changes. **Delete words** shows the spoken words: press and hold a word, or tap **Select words** and
  tap the first and last word, drag the two round handles (or use the arrow buttons) to change the range, then **Delete** cuts them out and closes the gap.
  **More tools** at the bottom keeps the analysis, transcript, markers and caption looks.
- If the timeline was changed somewhere else, a short bar above the bottom bar says so: **Keep my copy** saves your edits
  as a new timeline, **Use the latest** loads what is saved. Hiding the app or switching apps does not lose an edit.

A few tools need a larger screen and say so on a phone instead of showing a control that would not
work: compound clips, buses and automation, curves, tracking a power window through a clip,
moving bins inside other bins, dragging transcript paragraphs (the arrows still reorder them) and
selecting by drawing a box (use **Select** in the bar to tap several clips instead; the **Clip** menu says so).

A few keyboard shortcuts have no button on a phone: the J, K and L shuttle keys, Stop, play in to out,
ten-frame playhead steps, moving a clip to another track, the Cut, Compose and Colour pages (they need a
larger screen; the phone has Media, Edit, Audio, Look and fix, Captions) and the command palette. The
phone's overflow menu lists them with the reason; the full list is on
[Keyboard shortcuts](shortcuts.md#on-a-phone).

## Playing and moving around

| Action | Keys |
|---|---|
| Play / pause | `Space` |
| Play in to out | `Shift+Space` |
| Shuttle reverse | `J` |
| Stop | `K` |
| Shuttle forward | `L` |
| Back one frame | `←` |
| Forward one frame | `→` |
| Back ten frames | `Shift+←` |
| Forward ten frames | `Shift+→` |
| Previous edit | `↑` |
| Next edit | `↓` |
| Previous marker | `Shift+↑` |
| Next marker | `Shift+↓` |
| Go to start | `Home` |
| Go to end | `End` |
| Mark in | `I` |
| Mark out | `O` |
| Clear in and out | `Alt+X` |
| Add marker | `M` |
| Blade tool | `B` |
| Select tool | `V` |
| Hand tool | `H` |
| Trim mode: ripple (default) | `R` |
| Trim mode: roll | `T` |
| Trim mode: slip | `Y` |
| Trim mode: slide | `U` |
| Snapping on/off | `N` |
| Delete selection (closes the gap) | `Delete` or `Backspace` |
| Lift selection (leaves a gap) | `Shift+Delete` or `Shift+Backspace` |
| Cut at playhead | `Ctrl/Cmd+B` |
| Nudge left one frame | `,` |
| Nudge right one frame | `.` |
| Nudge left ten frames | `Shift+,` |
| Nudge right ten frames | `Shift+.` |
| Copy clips | `Ctrl/Cmd+C` |
| Cut clips (copy, then delete) | `Ctrl/Cmd+X` |
| Paste clips at the playhead | `Ctrl/Cmd+V` |
| Duplicate clips | `Ctrl/Cmd+D` |
| Move clip to the track above | `Alt+↑` |
| Move clip to the track below | `Alt+↓` |
| Link selected clips (picture and sound) | `Ctrl/Cmd+Alt+L` |
| Unlink selected clips | `Ctrl/Cmd+Shift+Alt+L` |
| Deselect | `Escape` |
| Undo | `Ctrl/Cmd+Z` |
| Redo | `Ctrl/Cmd+Shift+Z` or `Ctrl/Cmd+Y` |
| Select all | `Ctrl/Cmd+A` |
| Select forward from playhead | `A` |
| Select backward from playhead | `Shift+A` |
| Select all on track | `Alt+A` |
| Mask tool | `Shift+M` |
| Reframe tool | `Shift+F` |
| Corner pin tool | `Shift+P` |
| Mask: switch between rectangle and ellipse | `Shift+S` |
| Mask: invert | `Shift+I` |
| Compose: remove the selected entry | `Alt+Backspace` |
| Compose: previous entry | `[` |
| Compose: next entry | `]` |
| Add adjustment clip | `Ctrl/Cmd+Alt+A` |
| Go to Media | `Shift+1` |
| Go to Cut | `Shift+2` |
| Go to Edit | `Shift+3` |
| Go to Compose | `Shift+4` |
| Go to Colour | `Shift+5` |
| Go to Audio | `Shift+6` |
| Go to Deliver | `Shift+7` |
| Open the command palette | `Ctrl/Cmd+K` |

Press `J` or `L` again to shuttle faster (1x, 2x, 4x, 8x). Press `?` (or choose the keyboard icon at
the top right) for a searchable list of every shortcut, and `Ctrl/Cmd+K` for the command palette; see
[Keyboard shortcuts](shortcuts.md). **Shortcuts** (top right) lets you change them.

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
- **Trim**: drag the handle at either end of the selected clip. The **Trim:** menu in the toolbar picks
  what the drag does: **Ripple** (default: the clips after it move with the edge), **Roll** (moves the cut
  between two clips), **Slip** (changes which part of the source shows, nothing moves) or **Slide**
  (moves the clip; the neighbour on the side you drag absorbs the change, and the other side keeps its edge, so a gap can open there).
- **Join, Fit to the gap, Replace** (in the **Clip** menu and the right-click menu, on a larger screen): **Join with next clip** puts two
  halves of one split back together. **Fit to the gap** stretches or squeezes a clip to fill the gap after it and tells you the new speed
  ("Speed becomes 83%"). **Replace with...** plays another file from the media pool in the same place and length. When one cannot run, the
  reason is written under it.
- **Delete**: `Delete` (or `Backspace`) removes the selected clips and closes the gap, on the clip's paired sound too (the matching clip on
  another track, same source and same place). `Shift+Delete`
  lifts them instead and leaves a gap; **Close gap before this clip** in the **Clip** menu closes it later.
- **Undo / Redo**: the arrow buttons at the left of the timeline toolbar (or `Ctrl+Z` / `Ctrl+Shift+Z`).
- **Snapping** (`N`) makes a dragged edge jump to nearby cuts, markers, the playhead and the in/out
  marks. Turn it off when you need to place something between them.
- **Select tool / hand tool** (`V` / `H`): the hand tool pans the timeline instead of editing it.

A dragged clip only becomes a change when you let go, so one drag is one undo step.

The **Add** menu above the timeline places a three-second **Title** or **Colour card** at the
playhead, on the top video track that has room there (or on a new video track above the others).
**Title with style and length...** opens one dialog: pick a style (Plain, Lower third, Boxed, Outline,
Card) with a live preview, type the text, set the length and choose the track. **Colour card with a
chosen colour...** offers named colour swatches or a custom #RRGGBB. Both say where the item landed.
The new item is selected so you can change it in the Inspector straight away (a colour card's colour
has the same swatches there), and one undo removes it.

**Freeze frame here (2 s)** is in the clip menu (and the "..." Clip button): it splits the clip under
the playhead, holds that frame for two seconds and pushes the rest of that track later. The hold is
silent, and one undo puts everything back.

## Markers

Press `M` to add a marker at the playhead, or type a name in the **Markers** panel and press
**Add**. Click a marker to jump to it; the panel lets you rename or remove it.

Timelines that came from an agent run also show **reasoning markers**: for each cut the agent made,
the reason it gave, at the frame it applies to. They are labelled with the agent and the AI service
that made the decision. You can read them but not edit them, they are the agents' notes, not yours.

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
  discarded, never merged automatically, this is deliberate, so two people can never silently
  overwrite each other's work.

## Going back to an earlier version

ReelBolt keeps restore points for you. It saves one when you start editing after a pause of a couple of
minutes, before a restore, and before each render, plus the very first version of every timeline. You can
also save the current state yourself and give it a name. **Restore points ReelBolt saves for you are kept
for 30 days. A version you have named, and the first version, are kept until you remove them.** A deleted
timeline can be brought back from the trash for 30 days, then it is gone for good.

The version list shows which points can be restored. **Restore** brings that copy back as a *new* revision, so nothing is ever
lost, you can restore the restore. Restore asks for confirmation first, because it replaces the whole
timeline and clears this session's Undo history.

## What the preview shows you, and what it cannot

The viewer plays the timeline itself, not a rendered file. To do that quickly it uses proxy
(low-resolution) copies of your clips, so:

- A badge over the viewer says how faithful the current frame is. **Faithful** means what you see
  is what the render will do (cuts, which clip is on top, volume). **Approximate** means the render
  will differ in detail (titles and captions are drawn with a stand-in font, animated values are
  interpolated by the browser, a slow-motion or fast-motion clip shows the right timing but not the
  render's sound stretch). **Render required** means the effect is not previewed at all.
  **Preview not ready** means part of the picture is missing or still being prepared.
- **Tap or click the badge** to see why, in plain words, for example "Clip "Interview": Slow motion
  (0.5x) ...". Each line about a clip is a button: it selects that clip and moves the playhead to it,
  so you can go straight to what the badge is talking about. Lines about clips that are not on screen
  right now are listed under "Elsewhere in this timeline". The badge is never "Faithful" while the
  preview is the Simple preview, runs at reduced quality, or a picture is missing.
- **Preparing the preview.** When you open a timeline, ReelBolt asks for a small, fast copy (a
  proxy) of every clip it uses. While a clip's copy is not ready the viewer says so instead of showing
  black: "Preparing preview 42%" with a progress bar, "Source offline" when the file cannot be
  reached, or "Preview failed: ..." with a **Try again** button. A line above the viewer ("Preparing
  preview 42%", "Preview failed for 2 clips") opens the list of clips with their state, **Try again**
  for each failed one and **Prepare all**. When everything is ready the line disappears.
- **Preview quality** (the sliders button under the viewer) is **Auto**, **Full**, **Half** or
  **Quarter**. Auto looks at your device: a phone, a browser that draws with software graphics, or a
  computer with four or fewer processor cores or about 4 GB of memory or less starts on the **Simple
  preview** at a smaller picture. The Simple preview plays one clip at a time from its small copy and
  is labelled **Simple preview** above the viewer. Your edits are still exact: the finished video is
  always made by the render. If playback keeps dropping frames, the viewer offers **Switch to simple
  preview**. Your pick is remembered on this device; the **Simple preview** switch in the same menu
  turns it on or off yourself.
- The status bar counts **dropped frames**. If the number climbs, your computer cannot decode and
  draw fast enough; lower the preview quality or switch to the Simple preview. The count is never
  hidden, so you always know when the preview is cheating.
- The status bar also says which **kind of preview** you have: **live** (the full compositor),
  **simple** (one clip at a time over its small copy), or **unavailable** (no copy exists for the clip
  yet; the Media workspace shows what has been generated).
- While the viewer starts it shows a neutral grey frame at the size of your picture, so the layout
  does not jump and there is no black flash.

The finished video is always produced by a **render**, never by the preview. You start one from the
**Deliver** workspace; see [Rendering a finished video (Deliver)](#rendering-a-finished-video-deliver).

## Tracks and timeline settings

The small **+V** and **+A** buttons above the track names add a video or an audio track at the
bottom of the stack; the bin icon on a track removes it, with everything on it, as one undoable
step. The lock, eye, mute and solo buttons on a track are saved with the timeline now, so they are
the same when you reopen it. The **Settings** tab on the right edits the frame size, frame rate,
sample rate, pixel aspect, aspect policy and start timecode: change what you need and press
**Apply**, one undoable change for all of it.

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

Open the **Deliver** workspace (or press **Export** at the top right). Choose a format, HD 1080p, 4K,
vertical, square, portrait, the timeline's own size at master quality, or audio only, decide whether
to render the whole timeline or only the part between your in and out marks, give the file a name
(or leave it blank to name it after the timeline), optionally normalise the loudness, and press
**Render**. The render runs like any other job: the queue below shows it queued, running with its
progress, and then finished with a download button (it saves the file under the name you gave it), or failed with the reason. **Stop** halts a
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
anything and the strip disappears until the next render, it never shows the cache of a version that
is no longer on screen.

## Moving, scaling, rotating and fading a clip

Select a clip and open the **Inspector**. Everything is in plain units. **Opacity** is a percent (a field
and a slider). **Fade in** and **Fade out** are in seconds; the selected clip also shows a round handle
at each top corner, so you can drag a fade by eye (or focus the handle and use the arrow keys for one frame
at a time). **Auto fade** puts a half-second fade at both ends. The **Transform** section moves the picture
(Position X and Y are fractions of the frame, so 0.25 is a quarter of the width to the right), scales and
rotates it; **Crop** trims each edge. Each section has a **Reset**. A value that is out of range is refused
with a sentence that says what the range is, and the field shows the stored value again; nothing is
silently changed.

The Inspector shows the controls that apply to where the clip sits. A clip on a picture track offers
the picture controls (speed, reverse, freeze frame, Animate, Transform, Crop, Fades) and no sound controls:
the sound of a clip on a picture track is not played or rendered, so use **Extract audio** to put it on a
sound track. A clip on a sound track offers speed, reverse, Audio channels, Fades and **Volume**, and none of
the picture controls. **Volume** is a percent with a **Mute** switch (0% is the quietest setting; unmuting
brings back the volume you had). The dB box and its keyframe diamond are under **Advanced**. A number you type
counts as one change when you press Enter, leave the field or pause typing, so one Undo takes it back.

### Move the picture right on the viewer

With a picture clip selected and the playhead on it, the viewer shows handles around the picture
while it is paused. Drag the picture to move it, drag a corner to scale it (the shape is kept; hold
**Alt** to scale from the centre), and drag the round handle to rotate (hold **Shift** to turn in
15-degree steps). The picture sticks to the middle, the thirds, the safe margins and the edges of
other pictures as you pass them; hold **Ctrl** to move freely. With the picture focused, the arrow
keys nudge it by one pixel (**Shift** for ten). The **Position** menu above the viewer has **Fit**,
**Fill**, a 3 x 3 grid that parks the picture against an edge or corner, and four "picture in corner"
presets. If a value already has keyframes, the change is stored as a keyframe at the playhead.
One gesture is one Undo, and the Inspector numbers follow the drag.

## Animating a clip

Select a picture clip and look at **Animate**. Tap **Zoom in**, **Zoom out**, **Pop**, **Slide from the left,
right, top or bottom**, **Fade** or **Ken Burns**. Each tap sets all the keyframes the motion needs as one
change, so one Undo takes the whole motion back, and the playhead jumps to the start of the clip so you can
press play. Each preset has a small looping preview. Under the chips you can set how long the motion takes,
how strong it is, and for slides and fades whether the clip **comes in** or **goes out**. Tapping another
preset replaces the first one; **Remove animation** clears the motion; an **Animated** badge shows that the
clip carries keyframes.

**Fine-tune keyframes** is for exact control. Beside each number is a small diamond. Put the playhead where
you want the change to start and click the diamond: that sets a **keyframe** there with the current value.
Move the playhead, change the number: with keyframes on, the field sets a keyframe at the playhead instead of
changing the whole clip. The button showing the count opens the **curve**: click a point to select it, drag it
left or right to move it in time, pick how it eases into the next point, or delete it. Everything is
undoable, one step per change.

## Follow a subject

To keep a moving person or object in frame, select a picture clip and open **Follow subject** in the Inspector. On a phone, it is in the clip's sheet.

1. Choose how to point at the subject. **Draw a box** is free: drag a box around the subject on the picture, at a moment when it is clearly visible. (Drawing is done on the larger editor, not on a phone.) **Use a found object** lists the objects already found in this video. If the list is empty, the **Objects** section can find one, and that may cost credits.
2. Wait for **Subject found**. The line shows the stretch of the video where the subject was found.
3. Set the look. **Tightness** (Loose, Medium, Tight) is how much of the picture the subject fills. **Smoothness** (Gentle, Normal, Snappy) is how quickly the picture moves after the subject. **When the subject is lost** is **Hold** (keep the last framing) or **Widen** (slowly show the whole picture). **Allow softer zoom** lets the picture zoom in further, up to 4x, and it can look less sharp. In the editor the zoom also stops where the picture would leave the frame, so it can end lower: for a wide clip in a tall frame that is about 1.6x.
4. Press **Follow**.

The picture now zooms and moves with the subject. Under the hood it is ordinary position and scale keyframes, so you can fine-tune them as in [Animating a clip](#animating-a-clip). One Undo takes the whole follow back, and **Remove follow** clears it. Where the subject was lost, the section lists the stretch. If the zoom stopped early to keep the picture sharp, it says so.

Follow does not work on a clip that is cropped, uses a reframe, is stabilized, is a freeze frame, or has a speed that changes over time. The editor says which one and what to turn off first. If you change the clip after pressing Follow (its speed, its crop, the timeline's size), the editor asks you to **Run again**.

## Speed, reverse and freeze frame

**Speed** has chips (0.25x, 0.5x, 1x, 1.5x, 2x, 4x), a stepper for any other value, and a line that says
**Clip is now N s**, because the clip's length changes to match and everything after it moves along. If the
new length would be shorter than the fades, the fades are shortened and a message says so. **Reset** puts the
speed back to 1x. **Reverse** plays it backwards (up to a minute of footage). **Freeze frame** holds the
clip's first frame for its whole length, silently.

## Transitions

Every cut between two clips on the same track shows a round **+** (a 44 pixel target on a touch screen).
Click it to open the transition picker: about eight labelled words (Dissolve, Dip to black, Dip to white,
Wipe left, Wipe right, Slide left, Slide right, Circle open), each with a looping preview, **More** for the
rest, and a length (0.25, 0.5, 1 or 1.5 seconds). A line beside the picker says how close the preview is to
the export: a dissolve is **faithful**, every other word is **approximate** (the viewer shows a dissolve; the
render uses the real wipe or slide). **Add** places the transition centred on the cut. **Apply to all cuts**
adds the chosen word to every cut that can hold it in one step, and tells you how many cuts had no spare
footage. A clip that is selected shows its trim handles at its edges instead of the +; the same picker is in
the Inspector's Transition section.

The clip after the cut needs some footage before its first frame to show during the blend. When it has
none, the picker says so and offers **Make room**: it trims the start of the next clip (the clips after it
move up, and its linked sound follows) so the blend has footage to reach into. If the clip is too short to
give any, the picker says that instead. An existing transition shows a small glyph on the cut: click it to
change the word (the item's name follows) or **Remove transition** (back to a hard cut). The + and the glyph are for
larger screens; on a phone use the Inspector's Transition section. For frame-exact control and saved transition presets,
open **Advanced** in the Inspector. The render always uses a linear ease, whatever easing you pick.

## The mixer (Audio workspace)

Open the **Audio** workspace. Each audio track has one **Volume** fader from silence (−∞) up to +12 dB,
with a scale under it. Drag it, or type a number in the dB box (Enter to apply; "-inf" for silence).
Double-click the fader, or press the small reset button beside the box, to go back to 0 dB. Below it
are pan (double-click to centre), mute and solo, a **Clean voice** switch, a **Cut low rumble**
switch and a level meter. The expert controls (the exact equaliser numbers, buses, compressor, gate,
limiter, de-esser and automation recording) sit behind the **Advanced audio** switch at the top.

**Clean voice** removes background noise and evens out the volume of that track in one switch. Like
all sound processing it is fully heard in the rendered film, so press **Audition around the
playhead**: the editor renders about ten seconds around the playhead the same way the final render
does and plays it right there. The sample is a throw-away preview, not a file in your project.

## Loudness against a target

Under **Loudness** choose where the film will be heard: **Social** (−14 LUFS), **Podcast** (−16) or
**Broadcast** (−23). **Measure loudness** plays the whole mix through once and tells you how far it is
from the target ("6.0 dB too quiet for Social"); a result within 1 dB counts as on target. **Match
target** moves every unmuted audio track's volume by that difference in one Undo and measures again.
The live meter under it is only a preview. If your plan charges credits for the measurement you are
told how many before it runs.

## Ducking music under speech

In the mixer's **Ducking** section choose the music track and the voice track(s) (they are picked for
you when the tracks are named like *Music* and *Voice*), choose how far the music drops, and press
**Duck music under voice**. The badge reads **Ducking: on**, and the music track's volume gets points
that dip wherever someone talks. While ducking is on, the points follow the voice clips when you move
or trim them (each update is one Undo). **Update ducking** does it on demand, **Remove ducking**
restores a flat volume and turns it off. The points are ordinary keyframes you can still edit in the
track's automation lane.

## Editing from the transcript (Text)

If the timeline was seeded from an analysed run with transcription on, the **Text** tab in the left
panel shows what was said, as paragraphs of words. Click a word to jump to it; shift-click another to
select the run between them; **Delete words** cuts exactly those frames out of the track and closes
the gap, one undo step, like any cut. **Find fillers…** lists every "um", "uh" and stutter it can
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
timeline at once, click it to jump; the detail timeline follows. Under it, the **source tape** lists
the project's media: **Insert** drops a source at the nearest cut to the playhead and pushes what
follows along. **Review** plays each clip at a speed scaled to its length, so a long shot does not take
long to check. **Boring shots** lists the shots the analysis measured as long and still, **Show**
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
a **CMX3600 EDL**, or **SRT**/**WebVTT** captions (a timeline with no captions says so instead of offering an empty file). OpenTimelineIO keeps everything, so **Import OTIO**
on the **Videos** tab brings a timeline back whole, or imports the cuts of a file another editor
made. When some clips' media is not in this project the timeline still opens: each missing clip is a red
**Offline: name** marker where it was, a message lists them, and **Relink** lets you pick a project file
that goes at that marker (one undo step). Under the export links, **What is in the file?** says what each format carries and leaves
out, which media it points at, and a **Hand off** line counting what will be in the file.
**Still** above the viewer saves the current frame as a PNG at preview quality, handy for a reference,
not a finished frame. With one clip selected, **Export only "…"** in Deliver sets the in and out
marks to that clip and switches on **Only the marked in/out range**, so the next render covers just it.

## Keywords, labels and smart bins

In the **Media** workspace, selecting a clip shows **Notes and keywords**: add keywords, a note, a
colour label, and mark it a favourite or flag it, then **Save**. In the bin tree, **New smart bin…** (see below) creates a bin that always shows every source matching a filter, keywords, kind, label,
favourite, flag, part of the name, instead of what you drop in it. Tag the clips and the smart bins
fill themselves.

To put a clip in an ordinary bin, drag its tile onto the bin, or open the **⋯** menu on the tile and
choose **Add to bin** (smart bins are never offered: they fill themselves).

### Finding, starring and sorting media

The pool shows a picture for each clip (a video shows its first frame), its length and size when known, and one quiet line, **Not analysed yet**,
when nothing has measured it. The **star** and **flag** on a tile save the moment you tap them; a coloured dot shows the clip's colour label and small
chips name the bins it is in. **Search** matches file names and keywords, **Sort** orders by when added, newest, name, length or size, and **Filter** narrows
by kind (video, audio, pictures) or by Starred, Flagged or Not analysed yet. **Clear filters** brings everything back.

### Bins without dragging

Every bin has a **⋯** menu: **Rename**, **New sub-bin** (the next bin you add goes inside it), **Move to** another bin or the top level (desktop), **Edit rule**
(smart bins) and **Delete bin**, which asks first. A clip's row inside a bin has its own **⋯ > Move to** menu.

### Smart bins

**New smart bin…** opens a short form in plain words: the kinds of media, starred or flagged, a colour label, keywords, and part of the name. A line
under it says what the rule means and how many sources match right now ("Video, starred: 3 sources match now"). A rule with nothing chosen is refused,
because it would only repeat All media. Choose **Edit rule** on a smart bin's menu to change it later; the tree shows each smart bin's match count.

### Putting media on the timeline

Drag a tile onto a track in the timeline: a blue line shows exactly where it will land, and a track the clip cannot go on (a sound on a video track) shows
a red line and refuses the drop. The clip covers whatever is under it, nothing else moves, the marked **In/Out** range of the source viewer is used when you
set one, and a message says where it landed with **Undo**. Without a mouse, use the tile's **⋯ > Put it on the timeline** menu, or the buttons beside the
source viewer. The **Edit** workspace opens with the media pool on the left on a computer or tablet, so a tile and the timeline are on screen together.

### The source viewer

There is one player: it uses the lightest copy that is ready (labelled **Simple preview**) and the original otherwise, with no switch to think about.

**Upload** (at the top of the Media workspace, and in its empty state) adds video, audio or images from your device; you can also drop files
onto the Media area. Each file shows its progress, and one that fails says why and offers **Try again**. When it finishes it appears in the
pool. On a phone, **Upload** opens the file picker. Deleting a bin asks first, naming the bin and how many sub-bins and clips it holds:
clips stay in your project, only the grouping goes.

## Grading a clip (Colour workspace)

**Colour is the grade page.** With a clip selected, the node stack and the target are on the left, the
picture with the scopes under it is in the middle, and the grade tools are on the right. The tools are
tabs: **Primaries**, **Curves**, **LUT**, **Secondaries**, **Groups** and **Gallery**. Everything is
visible when the page opens; there is no extra switch to turn on.

- **Gallery** shows your current frame with each look applied: Warm, Cool, Filmic, Vibrant, Muted, Mono
  and Teal & orange. Click one to grade the clip with it; **Strength** (0 to 150 percent, 100 is the look
  as written) makes it subtler or stronger, and **Original** takes it off. Under **Fix**, five plain
  sliders (Brightness, Contrast, Saturation, Warmth, Tint) fine-tune the picture.
- **Primaries** gives you lift, gamma, gain and offset for red, green, blue and all together, plus
  exposure, contrast (around a pivot), saturation, hue, temperature and tint. **Add correction** adds a
  correction at the end of the node stack.
- **Curves** lets you click points onto a tone curve for luma or one channel.
- **LUT** applies a `.cube` file from your project at the strength you choose.
- **Secondaries** holds the qualifier and the power window. **Edit window on the picture** puts the
  window handles on the picture; selecting a node on its own does not.
- **Groups** holds the colour groups described below.

**Show the original (before)** flips the viewer between before and after without changing the clip;
**Split before / after** draws the original on the left of the viewer with a slider for the dividing
line. **Apply to all clips** gives every clip the same look and fixes in one step (one Undo takes it all
back); with several clips selected you can apply to just those, or to the clips from the same file.
**Copy grade** and **Paste onto selected** move a whole grade, and **Match this clip to…** picks another
clip and nudges this one toward its colour at the amount you choose (a first-order match: refine or
remove the Match correction afterwards). **Adjustment layers** lists the layers that grade everything
below them for a range; add one over the selected clip, select it, and give it a look like any clip.

Each correction has its own on/off switch, and the switch at the top bypasses the whole grade. **Add
look** turns the words a Colorist agent uses, Warm, Cool, Filmic, Vibrant, Muted, Mono, at a strength,
with lifted or deepened shadows and softened or brightened highlights, into an ordinary correction you
can keep, tweak or delete. **Grade on/off** above the viewer compares before and after in the preview
only: renders and stills always use the grade as it is switched. Curves, LUTs and secondaries are
applied by the render; the preview cannot show them, and each tab says so.

## Colour groups

Clips graded together belong to a **colour group**: select them and press **Group selected clips**,
or **Groups from look groups** to make one group per set of similar-looking shots the analysis
found. A group's grade is applied first, then each clip's own. Pick the group in the Colour page's
target menu (or the **Groups** tab) to grade it; rename it, add the selected clips, or remove members
in the list.

## Stills, wipe, scopes and shot match

**Grab still** keeps a picture of the current frame in the gallery; click one to make it the
reference and drag **Wipe** to compare it against the live picture in the viewer. The **scopes**
(waveform, parade, vectorscope, histogram) are always docked under the picture on the Colour page, with
a collapse control, and read the composited preview, grade included. Because they are always there,
**Match to reference** works from any tab: it adds a correction that moves the current clip's
brightness, balance and contrast toward the reference still, a starting point to refine, not a
guarantee.

## Masks, keys, pins and effects (Compose workspace)

You do not have to start on Compose. Select a clip and open its clip menu, or the Inspector's **Mask
and reframe** section, and choose **Add mask** (rectangle or ellipse) or **Add reframe**: the editor
selects the clip, pauses playback and puts the handles on the picture, without changing page. Adding a
mask or reframe is one undo step.

Open **Compose** with a clip selected for the deeper view. On the left is a list of what is on the
clip, and on the right the properties of the entry you pick. A video clip lists Mask, Reframe, Keyer,
Corner pin, Effects, Motion and Node view; an adjustment clip lists Grade, Effects and Node view; an insert
lists Matte and Corners. Press `[` and `]` to move to the previous or next entry. Arrow keys select an entry and bring up its tool while focus stays on the list; click it or press `Enter` to move focus to the picture. Editor shortcuts work while an entry has focus; the list keeps its arrow keys, `Home`, `End` and `Enter`. A **shape mask** shows the clip only inside
(or outside) a feathered rectangle or ellipse; a **chroma key** makes a colour transparent, with spill
removal; a **corner pin** places the whole picture onto four corners; **effects** (blur, sharpen,
vignette, grain, pixelate, mono) stack in order with an amount each; **Stabilise** steadies shaky
footage in the render; **Reframe** crops to the timeline's aspect around a subject point; **Slow
motion** can interpolate frames for smoother slow motion. The Inspector and Compose show the same mask
and reframe fields, so a change in one is a change in the other.

To draw a mask on the picture, select one video clip, move the playhead onto it and press `Shift+M`
(or choose **Draw on the picture** in Compose, the first button in the mask entry; it reads **Redraw** once a mask exists, and **Add mask** and **Add ellipse mask** are still there). Drag on the picture to draw the box; hold `Shift` for a
square and `Alt` to draw from the centre. During a drag, one `Escape` cancels the drag only; the next leaves the tool; the next deselects. `Enter` adds the default mask
without dragging. The tool bar sits in one row above the picture and has the mask shape, **Feather mask**, **Invert**, **Redraw**, **Remove mask**,
**Add mask** and **Done**. `Shift+S` switches between rectangle and ellipse, `Shift+I` inverts, and
`Alt+Backspace` removes the selected entry.

To place a corner pin, press `Shift+P`. Pin handles show only while the pin tool is on. With no pin, the tool says so and offers **Add corner pin**, which also starts the tool. Four handles mark where the corners
land; drag one, or use the arrow keys to move the focused handle 1% (`Shift` for 10%). On a clip that is scaled, moved, rotated or not filling the frame, a note says the handles mark the frame's corners, not the picture's edges. `Shift+F` opens
the reframe tool; with no point it offers **Add reframe**, and nothing is written just by entering the tool. A mask, reframe or pin tool ends when you change page. Pressing a tool shortcut on another page with one media clip selected opens Compose and starts the tool; **Add mask** from Edit's Inspector or the clip menu starts the tool without changing page. A shortcut you set yourself wins over a default.
An insert has no handles on the picture: its corners stay number fields in Compose.

With a mask or Reframe tool on, the shape is on the picture: drag the yellow shape (drag
inside to move it, the square handles to resize it, the round handle to soften the edge) or the green
cross for Reframe, which holds a fixed point. **Invert mask** sits above the picture, the arrow keys
nudge by 1% (Shift for 10%), and one undo reverts a whole drag; the sliders stay for fine-tuning. The
power window in the Colour page's **Secondaries** tab can be moved the same way (purple) when **Edit
window on the picture** is on, unless it follows the picture: then turn off tracking to move it by
hand. None of these show in the preview, the badge above the viewer says so, the render applies them.
To see the real result before you deliver, press **Preview 3 s** in the Properties header: it shows the price first (or
"included"), then renders three seconds around the playhead and plays them over the viewer. **Close
preview** or **Back to editing** returns to your timeline, which is unchanged. For an **insert**,
Compose edits its matte and the **Corners** of its tracked quad at the playhead (**Corner keyframes (n)** counts them). With nothing selected,
**Add adjustment clip** (`Ctrl/Cmd+Alt+A`) puts a clip on a new top track whose grade and effects apply to everything
beneath it for its length.

## Blur or mask an object

To blur a license plate or a face, or highlight a product, in a clip, see [Blur or mask an object](blur-or-mask-an-object.md).

## Title templates

Select a title: the inspector's **Template** offers a lower third, a boxed title, an outlined one
and a card, in addition to the plain centred title. The viewer draws the template's position, box and
outline; the text itself is drawn by the browser, so its exact size can differ slightly from the render.

## Nesting clips (compound clips)

Select some clips and open the **Structure** tab: **Make compound** turns them into one clip that
behaves like any other, move it, trim it, cut it. **Open** shows what is inside so you can edit it
(cut, move, trim, delete, undo); **Save to compound** puts your changes back, and one Undo outside
takes the whole session back. **Decompose** puts the clips back on the timeline as they were.

## Several cameras of one moment (multicam)

Select two or more clips recorded at the same time (the first one is the reference) and press
**Sync by waveform**: the editor listens to each clip's sound and lines them up. Each camera shows
its offset in seconds and a confidence badge; a *low confidence* badge means the sound did not match
clearly. Fine-tune an offset with **−1 frame** and **+1 frame** or by typing it. Under **Sound of the
multicam** choose which recording supplies the audio: the first camera (program), any selected clip
(a separate recorder clip is one of them), or no sound. **Make multicam** replaces them with one
clip; change the sound later from the multicam's panel. Play it and click a camera's button to cut
to it at that moment; stop and click a button to change the camera of the section under the
playhead. Each cut is one Undo.

## Buses, compression and clean-up (Audio workspace)

Under the mixer, **Buses and processing** lets you group tracks: add a bus (say, *Dialogue*), route
tracks to it, and route buses into each other or to the master. Each track and bus can have a
compressor, limiter, noise gate, de-esser, noise reduction and a leveler that evens out speech. These
apply in the rendered video. Buses, the compressor, limiter, noise gate and de-esser are also heard in the preview, approximately (each shows "Heard in preview, approximately" or "Heard after render only"); denoise and the leveler are heard after the render only.

**Automation**: set a track to **Latch**, play, and move its fader, when you stop, your moves are
written as the track's volume curve (one Undo). **Read** plays the curve back on the fader.

**Channels**: in the inspector, *Audio channels* fixes a clip recorded on one side only, folds it to
mono or swaps left and right. The preview plays the channels as recorded, so a clip with a channel
map is marked "Render required"; the render applies it.

## Recording a voiceover

In the Audio workspace pick your microphone (if you have more than one) and press **Record at
playhead**. A 3-2-1 count-in runs while the level meter shows what the microphone hears; **Stop and
place** saves the take to the project's files and puts it on a new *Voiceover* track where the
playhead was. **Retake** records again and replaces that track in one Undo. If the browser blocks the
microphone, has none, or cannot record, the panel says so in plain words and **Upload a recording**
puts a file you already have on the timeline instead.

## Asking the AI for suggestions (Assist)

On a phone, **⋯ then Assist** opens a sheet over the strip with the same helpers. The price and the hour's
allowance show before you ask. Each suggested change is a row: tap it to jump to that moment and see only its
outline on the strip. A change to a clip you made has a tick inside it that you must tick first. **Accept**
and **Reject** stay at the bottom; after Accept an **Applied. Undo** bar undoes the whole suggestion in one step.

Open the **Assist** tab, pick a **helper**, *Re-cut*, *Graphics*, *Grade*, *Sound effects* or
*Review* (each one's job is written under the picker), optionally mark in and out to limit it to
part of the timeline, write what you want in the one prompt box, and press **Ask for suggestions**.

Before you ask, the panel tells you what it will cost ("Asking costs about 2.5 credits"; nothing is
shown when billing is off, and it says *Included* when your workspace uses its own AI provider),
how many suggestions you have left this hour ("12 of 20 suggestions left this hour") and exactly
what is sent to the AI: counts of clips and seconds, your brief, never video, audio or file names.
Looking at this costs nothing. If you are out of credits the button is disabled and says how many
you need; if you used up the hour's suggestions it says how long to wait. If the AI gives an
unusable answer, the panel says so and how many credits that attempt used.

The answer is a list of changes, each with its reason, and the timeline shows what would change:
green outlines for additions, red hatching for removals, orange for changes. Press **Accept** on a
change to apply just that one, **Skip** to drop it, or tick several and press **Accept checked
changes**, they apply in one step (one Undo) and the rest stay listed. A change to a clip **you**
made is marked, and you must tick *Allow changing your clip* first. If no AI provider is set up,
the panel says so; an admin adds one under Admin → Inference providers.

## Working offline

If your connection drops, keep editing: the status bar says **Offline** and your edits are kept on
this device (even if you close the tab). When the connection returns they are saved. If someone else
changed the same part of the timeline in the meantime, the editor will not guess: it keeps your
edits, shows **Download my offline edits**, and asks you to reload.

## Shortcuts and layouts

Press `?` (or choose the keyboard icon at the top right) to open the **Keyboard shortcuts** list: it
groups every action under Transport, Navigate, Marks, Tools, Edit and Pages, and **Search** narrows it.
The list is for reading; **Edit shortcuts** opens the editor. There, click **Change** and press the keys
you want. Two actions on one key are flagged and must be fixed before saving. **Find a shortcut**
searches by action or key, **Export JSON** / **Import JSON** keep your shortcuts in a file, and
**Restore previous settings** puts back what was there before your last save (kept on this device). If
you save from two devices, a save keeps the other device's changes. Menus and tooltips show the keys
from your current setup, so a change appears everywhere at once. `Ctrl/Cmd+K` opens the **command
palette**, where you can search every action, see its key and run it. See
[Keyboard shortcuts](shortcuts.md).

Each page remembers the sizes of its panels on this device, as fractions of the window, so they fit a
smaller screen. **Layout** saves named layouts (the open tabs and their sizes) for the page you are in,
and the bin next to a saved layout deletes it. **Reset this page** returns the page you are on to its
default sizes; **Reset every page** returns them all and asks first. All of it follows you to every
project.

## Display settings

The **Display settings** menu in the editor header sets how roomy the controls are: **Comfortable** or
**Compact**. A touch screen always gets the larger touch sizes. **Always dark** keeps the editor dark
while the rest of the app is light; choose **Match the app** (or close the editor) to go back to your own
setting. On a phone, **Editor always dark** is in the header's **More** menu. Both choices are kept per
person on this device.

## Presets

Save the settings you reuse as project presets: a transition (in the inspector, between two clips),
an effect stack (Compose), or a loudness target (Deliver, the editor also offers Social −14 LUFS,
Podcast −16 LUFS and Broadcast −23 LUFS). Pick a preset to apply it; deleting a preset does not
change anything you already applied it to. **Manage** next to the picker shows what each preset does and
lets you apply, rename, duplicate or delete it (deleting asks first).

## Correcting one colour or one area (Colour workspace)

In the **Secondaries** tab, **HSL qualifier** limits a correction to a range of colours (hue,
saturation, lightness), and **Power window** limits it to an ellipse or rectangle. **Track through
clip** follows what is inside the window as it moves. These show in the rendered video; the preview
shows the clip without them.

## Review and comments

The **Review** tab holds timecoded comments: type, press **Comment** at the playhead (or over the
marked range), reply, and resolve. Each comment card has buttons to **jump to its time**,
**edit** the text, **mark it resolved** (or reopen it) and **delete** it, on the editor tab and on
the review page. **Review link** opens one page that works on a phone and on a computer: the
newest finished render of the whole timeline, with its picture. On a computer the video sits beside the
comments; on a phone they stack. Markers on a bar under the video show where the comments are, and a chip
above the comment box shows the time a new comment will pin to (the frame the video is paused on). If you
edited the timeline after that render was made, a banner says **Newer edits exist** and links to the
editor; the **Render** picker lets you watch an older render. **Copy review link** (and **Share** where
the phone offers it) gives the link; the page says "Anyone in this workspace with access can open it".
To let someone without an account watch and comment, see [Share a film for review](share-for-review.md). On a phone you can reply, resolve and delete a comment.

## Final Cut Pro

**Deliver → Export the timeline → Final Cut Pro XML** downloads the cut (clips, gaps, transitions,
markers, extra tracks as connected clips) for Final Cut Pro. Titles, grades and effects are not
included.

## What the editor does not do

- It does not automate a bus's volume.
- The preview does not play compression or noise reduction and does not show colour secondaries; the
  render applies all three.
- It has no blend modes and no speed ramps, and it does not preview grades made with curves or LUTs.

### Editing on the timeline

**What changed:** `Delete` used to leave a gap, the same as lift. It now closes the gap, and `Shift+Delete` lifts the clip and leaves the gap.

- **Delete** closes the gap; **Shift+Delete** (or **Lift** in the **Clip** menu) leaves a gap. Click an empty space between two clips to select that gap, then press
  Delete (or **Close selected gap**) to close it. **Close all gaps** closes every gap on the selected clip's track.
- **Right-click a clip** (or tap **Clip** on a touch screen) for the full list: split, delete, lift, copy, cut, paste at the playhead, duplicate, fit to fill, replace
  from the media pool, join, move to the track above or below, speed, disable, select forward or backward, properties. Double-click a clip to open it in the inspector.
- **Copy / Paste** (Ctrl or Cmd with C, X, V; Ctrl/Cmd+D duplicates) keeps the spacing between several clips and works across tracks and timelines.
- The **-10 / -1 / +1 / +10** buttons move the selected clip or the playhead by frames.
- **Trim**: choose ripple, roll, slip or slide in the **Trim** menu (keys R, T, Y, U). While you drag an edge, a label shows the frames. In the inspector, type the
  clip's **In**, **Out** or **Duration** in frames or timecode and press Enter; if it cannot be done, a sentence says why.


### With your fingers

- **Tap** a clip to select it. **Drag** with one finger to slide the timeline sideways. **Pinch** with two fingers to zoom, and move both fingers together to slide.
- **Double-tap** a clip to zoom in on it.
- **Press and hold** a clip for half a second to open its actions (move, split, delete). Every action is also in the **Clip** menu.
- Drag the yellow handles on the selected clip to trim it. The handles are large enough for a finger.
- Moving a clip by dragging it with a finger is off by default. A device setting turns it on; then a press and hold picks the clip up.
- Touches in the first few millimetres of the screen edge are ignored so the browser's back swipe keeps working.
- In **Media**, press a clip and drag sideways to scrub through it; a label shows where you are. Drag up or down to scroll the list as usual.

## Analyse your footage, then cut by what it contains

Open the **Analyse** tab and press **Analyse** on a file to find its quiet stretches, shots and motion. **Transcribe speech** shows what it will cost first and only starts when you confirm. Then:

- **Remove silence** and **Delete words** cut the picture and its linked sound together. **Play the result** shows the cut in the viewer before anything is saved; **Apply** keeps it, **Cancel** drops it.
- Words marked with a tilde have only rough timing; you are asked before cutting them.
- **Subtitles** makes captions from the transcript, lets you pick a look and position, add a caption by hand (two lines are fine), nudge timing, or **Import SRT or WebVTT** (preview first, then add or replace).
- In **Deliver**, choose to burn captions into the video, get separate SRT and WebVTT files, or both.
- **Takes** groups lines you said more than once, suggests the cleanest, and sets the others aside on a switched-off track.
