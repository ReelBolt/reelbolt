---
title: Pages and panels
sidebar_position: 6
description: What the seven editor pages are for, how to move between them, how the address remembers the page, and how to reset panel sizes.
---

# Pages and panels

The editor is one window with seven pages. Each page is a place for one part of the work, with its own
tools and its own panel sizes. Moving between pages does not change your edit: the timeline and the
viewer stay as they were.

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
at a glance over the detailed timeline. **Insert** places a source at the nearest cut and pushes what
follows along; **Review** plays a clip at a speed scaled to its length; **Boring shots** lists long,
still shots the analysis found.

### Edit

**Edit** is the full editing page and the one you land on first. The left panel holds **Media**,
**Markers**, **Edits**, **Analyse**, **Transcript**, **Subtitles**, **Takes**, **Structure**,
**Comments** and **History**; the right panel holds **Inspector**, **Assist**, **Settings**,
**Versions** and **Compare**. The viewer is in the middle and the timeline runs across the bottom. Use
it for trimming, moving, titles, transitions, animation, versions and comments. The timeline toolbar
across the bottom holds the everyday tools, including **Remove silence** and [Cut to the music](cut-to-the-music.md).

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
takes the rest of the page, with no timeline below. Choose a format and the range, name the file if you
want, and press **Render**. The queue shows each render's progress, the download when it is finished
and the reason if it failed.

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
