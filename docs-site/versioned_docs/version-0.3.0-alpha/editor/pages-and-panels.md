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
you place a clip on the timeline with **Insert**, **Overwrite**, **Add to end** or **Place on top**. The
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
it for trimming, moving, titles, transitions, animation, versions and comments.

### Compose

**Compose** is for what happens to a clip's picture. Open it with a clip selected. The left side lists
what is on the clip (a video clip: Mask, Reframe, Keyer, Corner pin, Effects, Motion, Node view; an
adjustment clip: Grade, Effects, Node view; an insert: Matte, Corners) and the right side shows the
properties of the entry you pick. The viewer shows a mask, reframe or corner pin's handles on the
picture while its tool is on (pin handles show only while the pin tool is on). Tool bars sit in one row above the picture, not over it. Arrow keys in the list select an entry and bring up its tool while focus stays on the list; click or press `Enter` to move focus to the picture. A mask, reframe or pin tool ends when you change page. Mask, keyer, corner pin and reframe show on the
picture as outlines and appear in the render; the preview does not composite them. You can also reach **Add mask** (rectangle or ellipse) and **Add reframe** from the
clip menu and from the Inspector's **Mask and reframe** section without coming here.

### Colour

**Colour** is the grade page. The node stack and the target are on the left, the picture with the scopes
under it is in the middle, and the grade tools are on the right as tabs: **Primaries**, **Curves**,
**LUT**, **Secondaries**, **Groups** and **Gallery**. Everything is visible when the page opens; there
is no extra switch to turn on. Curves, LUTs and secondaries are applied by the render, and the page says
so where they are.

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
