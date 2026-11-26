---
title: Pages and panels
sidebar_position: 6
description: What the seven editor pages are for, how to move between them, how the address remembers the page, and how to reset panel sizes.
---

# Pages and panels

The editor is one window with seven pages. Each page is a place for one part of the work, with its own
tools and its own panel sizes. Moving between pages does not change your edit: the timeline and the
viewer stay as they were.

This page is about the **Full** layout. The **Simple** layout has three places instead of seven pages;
see [The simple editor and the full editor](simple-and-full.md).

## The seven pages

The pages are in the order you usually work: **Media**, **Cut**, **Edit**, **Compose**, **Colour**,
**Audio** and **Deliver**. Their names are always written: on a computer they sit in a row under the
top bar, and on a tablet they sit in a column on the left.

### Media

**Media** is where your footage lives. The bins and the search are on the left, the pool of clips is in
the middle, and the selected clip's viewer, probe details, tags and notes are on the right. From here
you place a clip on the timeline with **Insert**, **Overwrite**, **Add to end**, **Place on top** or **Place over**. The
timeline is hidden until you choose **Show timeline**, which opens a short strip for context.

### Cut

**Cut** is for assembling quickly. The **source tape** on the left lists the project's media, the
viewer is in the middle and the inspector is on the right. Under them, a strip shows the whole timeline
at a glance over the detailed timeline. **Insert at playhead** places a source at the nearest cut and pushes what
follows along; **Review** plays a clip at a speed scaled to its length; **Boring shots** lists long,
still shots the analysis found. Each row in the tape has **Watch**, which opens that clip in the viewer
as a source. [From Cut to Edit](#from-cut-to-edit) walks through a first cut.

### Edit

**Edit** is the full editing page and the one you land on first. The left panel holds **Media**,
**Markers**, **Edits**, **Analyse**, **Transcript**, **Subtitles**, **Takes**, **Structure**,
**Comments** and **History**; the right panel holds **Inspector**, **Assist**, **Settings**,
**Versions** and **Compare**. The viewer is in the middle and the timeline runs across the bottom. Use
it for trimming, moving, titles, transitions, animation, versions and comments. The timeline toolbar across the bottom holds the everyday tools, including **Remove silence** and [Cut to the music](cut-to-the-music.md). Selecting a clip in the
**Media** tab opens it in the viewer as a source, the same as **Watch** on Cut.

## From Cut to Edit

This is a first cut, start to finish. Each step names the page it happens on.

1. **Load a source.** On **Cut**, choose **Watch** on a row of the source tape. On **Edit**, select a
   clip in the **Media** tab. The viewer switches to **Source** and plays that file, not your film.
2. **Mark the part you want.** Press `Space` to play, or `J`, `K` and `L` to shuttle, and use the
   arrow keys to step. Press `I` for the in point and `O` for the out point. **Clear in and out**
   (`Alt+X`) removes both. A sound or a still has no frames to mark, so it is placed whole.
3. **Place it.** Put the timeline playhead where it goes, then press the button under the viewer
   (**Insert**, **Overwrite**, **Add to end** or **Place on top**) or its key: `F9` Insert, `F10`
   Overwrite, `F8` Add to end, `Shift+F8` Place on top. The marks decide which part of the source is
   used and the timeline playhead decides where. The viewer stays on **Source**, so you can mark the
   next piece straight away. On a Mac, `F8`, `F9` and `F10` are media keys unless you hold `Fn`; you can
   [set other keys](shortcuts.md#changing-a-shortcut).
4. **Switch to Record.** Press `Shift+V` or choose **Record** in the viewer's switch. The viewer now
   shows your film. Choosing a picture tool (for example Mask) switches to **Record** for you.
5. **Trim.** Select a clip and drag an end, using the trim control on the timeline toolbar (see
   [Trim modes](#trim-modes)). Go on to **Edit** (`Shift+3`) when you need titles, transitions,
   animation or comments.

### What Source and Record do

**Source** and **Record** are two views in the one viewer, not two viewers. In **Source**, `Space`, `J`,
`K`, `L`, the arrow keys, `Home`, `End`, `I`, `O` and **Clear in and out** act on the source and change
nothing in the film. Previous and next edit and marker do nothing there. Placing is the only change
that goes forward from **Source**. The switch is `Shift+V`, or **Source** and **Record** in the
viewer. A source stays open while you switch back and forth. The placement keys need a source open;
without one they do nothing and the buttons say to open a clip first. Source is a desktop feature; the
phone has no switch.

The Source monitor is named after the open file, for example **Source: clipA.mp4**. It has a scrub bar
for the source position, **Go to start of source** and **Go to end of source**, and one **Place** menu
in the transport with **Insert at playhead**, **Overwrite**, **Add to end** and **Place on top**. Your
in and out marks show as a caption over the bottom of the Source picture. Holding a placement key
places once; it does not repeat.

#### Editing from Source

In **Source**, a shortcut that changes the timeline does nothing: delete, lift, blade, cut, paste,
duplicate, the nudges, move to the track above or below, link, unlink, add adjustment clip, mask shape
and invert, and Compose remove. The command palette lists them as disabled with "Switch to Record to
edit the timeline." **Undo** and **Redo** still work.

A timeline right-click menu item or a toolbar button that writes to the timeline works differently: it
switches to **Record** first, then runs. Copy, speed, properties, select, mark and place do not switch;
the viewer stays on **Source**. Dragging on the timeline does not switch either.

Some actions that look like viewing also switch to **Record** first: **Review** on **Cut**, the **Clip**
and **Playhead** steppers, **Play in to out** and audition.

### Trim modes

The timeline toolbar has a four-way control: **Ripple**, **Roll**, **Slip** and **Slide**. Hover a
button to see what it does and its key (`R`, `T`, `Y`, `U`); on a narrow pane the four fold into one
menu. The control is one `Tab` stop; move between the four with the arrow keys.

When the toolbar is short of room, the trim control, the **Clip** and **Playhead** steppers (the
**Step** button) and the zoom slider fold into menus. The zoom buttons stay.

- **Ripple** (default): the edge you drag moves, and everything after it moves with it.
- **Roll**: the cut between two clips moves; the film keeps its length.
- **Slip**: the clip keeps its place and length, and a different part of the source shows.
- **Slide**: the clip moves along the track; the neighbour on the side you drag takes up the change.

In **Slip** or **Slide**, with a mouse or pen, dragging the body of a clip slips or slides it. Hold
`Shift`, `Ctrl` or `Cmd` while you drag for an ordinary move, or switch mode. A clip that cannot slip
or slide (a slip needs a source to move through, a slide needs a neighbour) moves normally, and a hint
says why: "This clip has no source to slip, so the drag moves it."

`Escape` cancels any drag with one press, ordinary clip moves included, and nothing is written.

While you drag, a readout says what will happen in words, with frames and timecode, for
example "Ripple: the end moves 12 frames (00:00:00:12) later, everything after it shifts with it". When
the edge stops, the line says why: the start or end of the source, a clip must keep at least one frame,
or the neighbouring clip has no more to give. When the source's length is known, a trim drag stops at the
end of the source and the readout says "Stopped: the end of the source." The slip readout reads, for
example, "Slip: same length, shows the source 12 frames later."

During a ripple, dashed ghosts show where the clips after the edge will land, and the line reads
"Moves N clips by T". A move onto another clip says "Won't fit here" and leaves both clips as they
were: nothing is overwritten or pushed.

### Snapping

With snapping on (`N`), a dragged edge jumps to a nearby point, and a label names what it caught:
**Snapped to playhead**, **Snapped to marker**, **Snapped to In point**, **Snapped to Out point**,
**Snapped to clip edge** on a named track, or **Snapped to the start**. The snapping button shows
**Snapping on** or **Snapping off** with its key.

### Right-click menus

Menus show the keys that are set now. Right-click an empty part of a lane or the ruler for its own
menu: paste here, close this gap, set marks, add a marker and, when a source is open, the placement
actions. Press the `ContextMenu` key or `Shift+F10` on a selected clip to open its menu; a shortcut you set
on `Shift+F10` wins over this. The "here" items (paste here, add a marker here and the like) move the
playhead to where you clicked first, and a marker stays inside the film. The clip menu
groups **Arrange**, **Link**, **Select and gaps** and **Picture** into submenus.

### Compose

**Compose** is for what happens to a clip's picture. Open it with a clip selected. The left side lists
what is on the clip (a video clip: Mask, Reframe, Keyer, Corner pin, Effects, Motion, Node view; an
adjustment clip: Grade, Effects, Node view; an insert: Matte, Corners) and the right side shows the
properties of the entry you pick. The viewer shows a mask, reframe or corner pin's handles on the
picture while its tool is on (pin handles show only while the pin tool is on). Tool bars sit in one row above the picture, not over it. Arrow keys in the list select an entry and bring up its tool while focus stays on the list; click or press `Enter` to move focus to the picture. A mask, reframe or pin tool ends when you change page. Mask, keyer, corner pin and reframe show on the
picture as outlines. The viewer shows their effect on the picture, and the badge above the viewer tells you if it only gets close to the finished video. You can also reach **Add mask** (rectangle or ellipse) and **Add reframe** from the
clip menu and from the Inspector's **Mask and reframe** section without coming here.

### Colour

**Colour** is the grade page. The node stack and the target are on the left, the picture with the scopes
under it is in the middle, and the grade tools are on the right as tabs: **Primaries**, **Curves**,
**LUT**, **Secondaries**, **Groups** and **Gallery**. Everything is visible when the page opens; there
is no extra switch to turn on. Curves, LUTs and secondaries show in the viewer as they do in the finished video; the badge above the viewer says when a colour is still being prepared.

On **Primaries**, lift, gamma and gain are round colour wheels, with an offset wheel beside them. **Fix
exposure** sits in the Look area and brightens or darkens the clip for you. See [Fix colour and
exposure](./fix-colour-and-exposure.md).

### Audio

**Audio** is the mixer page. The mixer is on the left, the picture in the middle and the buses and the
voice-over recorder on the right. Each track has a volume fader, pan, mute and solo, and a meter; the
master shows the mix. **Loudness**, **Ducking** and the expert controls for buses, compression and
clean-up are here too.

### Deliver

**Deliver** is where you make the finished film. Render settings take the left column and the result
takes the rest of the page, with no timeline below. Choose a preset and the range, name the file if you
want, read the statement of what will be rendered and what it costs, and press **Render**. On the right,
the latest film comes first, then the queue with each render's progress, its download when it is
finished and the reason if it failed, then review links, export for other apps and the hand-off
package. See [Export your film](export-your-film.md).

## Badges on the timeline

A clip that has been adjusted shows small badges on the timeline, in a row along the right side of the clip, under its name. Each badge stands for one kind of change:

| Badge | What it means |
| --- | --- |
| **Motion** | The clip is moved, scaled, rotated or animated. |
| **Follows a subject** | The clip follows a subject you picked. |
| **Crop** | The clip is cropped. |
| **Speed** | The clip is slowed, sped up, reversed or frozen. |
| **Colour** | The clip has a grade, a LUT, a colour group or a camera profile. |
| **Mask** | The clip has a shape mask or an object mask. |
| **Object effects** | The clip has an effect on an object, such as a blur. |
| **Green screen** | The clip has a colour key. |
| **Effects** | The clip has effects such as blur or grain. |
| **Stabilise** | The clip is stabilised. |
| **Reframe** | The clip is reframed around a subject. |
| **Corner pin** | The clip is pinned to four corners. |

Fades do not have a badge. A fade shows as a thin line in the corner of the clip instead.

A hollow badge means the change is there but switched off. A small dot on a badge means the viewer only gets close to the finished video for that change: amber for close, and a second colour for a change the viewer cannot show. Point at a badge to see its name and, when it applies, the reason. If the clip is too narrow for all the badges, you see one badge with a number; open the clip menu to see the list.

- **Open it.** Click or tap a badge to select the clip and go to the place where that change lives.
- **Turn it off or remove it.** Right-click a badge (press and hold on a touch screen) to open its menu: **Open**, **Turn off** (or **Turn on**) and **Remove**. **Remove** is one step, so **Undo** brings it back.
- **From the keyboard.** Open the clip menu and choose **Adjustments**. It lists the same badges with the same actions.

## A clip that is being processed

While a background job works on a clip, the clip shows it. A strip runs along the bottom of the clip, filling as the job goes, or sliding when the job cannot say how far along it is. The clip is slightly dimmed and its label says **Processing**. If you have asked the editor to reduce motion, the strip is a still pattern.

- **Ready to review** appears on the clip when the job finished and needs your OK. Click it to open the result.
- **Did not finish** appears with a red mark when the job failed. Point at it to read why.

The clip is protected while the job runs. If you try to change something the job depends on, such as the clip's footage, its speed or, for some jobs, its crop or position, the editor asks first. You can go on, stop the job or go back. See [See what is running in the background](./background-activity.md).

## Moving between pages

Click or tap a page name. With a keyboard you can also press `Shift+1` for Media, `Shift+2` for Cut,
`Shift+3` for Edit, `Shift+4` for Compose, `Shift+5` for Colour, `Shift+6` for Audio and `Shift+7` for
Deliver. `Ctrl/Cmd+K` opens the command palette, where you can type a page name and run it. The
browser's back and forward buttons move between the pages you visited, like any other link.

## The address remembers the page

The page you are on is part of the address, for example `?page=colour`. Reload the page and you stay
where you were, and an address you send to someone opens the page it names. An address with no page
opens the last page you used on this device; if you have never opened the editor before, it opens
**Edit**.

## Panel sizes and resets

Drag the thin line between two panels to make one wider or narrower, or focus the line and use the
arrow keys. Each page remembers its own sizes on this device, and they adapt when the window changes
size. **Reset this page** puts the page you are on back to its default sizes. **Reset every page** puts
every page back and asks first. A saved **Layout** keeps a set of panel sizes and open tabs that you can
apply again.

## On a phone

A phone keeps its own layout: a top bar, the picture, the timeline strip, and a bar at the bottom with
**Media**, **Edit**, **Audio**, **Look** and **Captions**. It has no page row. **Cut**, **Compose** and
**Colour** need a larger screen, and the phone says so where those tools would be. **Fix exposure** is
still there, in the **Look** sheet. Everything else is
the same editor, so a change made on a phone is saved with the timeline.
