---
title: Keyboard shortcuts
sidebar_position: 7
description: Every editor shortcut grouped by what it does, the searchable shortcut list, the command palette, and how to change a shortcut.
---

# Keyboard shortcuts

The editor has one set of shortcuts, and you can see all of them without leaving the page. Press `?`
to open the **Keyboard shortcuts** list, press `Ctrl/Cmd+K` to search every action, or open the same
list from the keyboard icon at the top right. Menus and tooltips show the key that is set right now.

## The shortcut list

Press `?` (or choose the keyboard icon) to open the list. It is read-only and grouped under
**Transport**, **Navigate**, **Marks**, **Tools**, **Edit**, **Compose** and **Pages**. **Search** narrows it by
action or key as you type, and it says so when nothing matches. **Edit shortcuts** opens the editor
described in [Changing a shortcut](#changing-a-shortcut).

## The command palette

Press `Ctrl/Cmd+K`. Type part of an action's name: the palette lists the matching actions, each with
its key and whether it can run with the current selection. Press Enter to run the highlighted action,
or click it. An action that cannot run right now is listed but disabled, with the reason. Escape
closes the palette without changing anything.

## Transport

| Action | Keys |
|---|---|
| Play / pause | `Space` |
| Play in to out | `Shift+Space` |
| Shuttle reverse | `J` |
| Stop | `K` |
| Shuttle forward | `L` |

Press `J` or `L` again to shuttle faster (1x, 2x, 4x, 8x).

## Navigate

| Action | Keys |
|---|---|
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
| Viewer: switch between Source and Record | `Shift+V` |
| Viewer: full screen (picture and transport fill the screen; `Esc` leaves it) | `P` |

## Marks

| Action | Keys |
|---|---|
| Mark in | `I` |
| Mark out | `O` |
| Clear in and out | `Alt+X` |
| Add marker | `M` |

## Tools

| Action | Keys |
|---|---|
| Blade tool | `B` |
| Select tool | `V` |
| Picture handles on the viewer (move, resize, rotate) | `Shift+T` |
| Hand tool | `H` |
| Trim mode: ripple (default) | `R` |
| Trim mode: roll | `T` |
| Trim mode: slip | `Y` |
| Trim mode: slide | `U` |
| Snapping on/off | `N` |

## Edit

| Action | Keys |
|---|---|
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
| Place over (picture-in-picture) | `Ctrl/Cmd+Alt+P` |
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
| Place the source: insert | `F9` |
| Place the source: overwrite | `F10` |
| Place the source: add to end | `F8` |
| Place the source: place on top | `Shift+F8` |

The four placement keys need a source open in the viewer (see [From Cut to Edit](pages-and-panels.md#from-cut-to-edit)). On a Mac, `F8`, `F9` and `F10` are media keys unless you hold `Fn`, so press `Fn+F9` and so on, or set the key you prefer in [Changing a shortcut](#changing-a-shortcut).

While the viewer shows **Source**, `Space`, `J`, `K`, `L`, the arrow keys, `Home`, `End`, `I`, `O` and **Clear in and out** act on the source and change nothing in the film. Previous and next edit and marker do nothing there.

In **Source**, the shortcuts that change the timeline do nothing: delete, lift, blade, cut, paste,
duplicate, the nudges, move to the track above or below, link, unlink, add adjustment clip, mask shape
and invert, and Compose remove. The command palette lists them as disabled with "Switch to Record to
edit the timeline." **Undo** and **Redo** still work, and placing is the only change that goes forward
from Source. Holding a placement key places once. See
[What Source and Record do](pages-and-panels.md#what-source-and-record-do).

`Escape` cancels any drag with one press, ordinary clip moves included, and nothing is written. When
no drag is running, the next press leaves the tool and the one after deselects.

Menus show the key that is set now. `ContextMenu` or `Shift+F10` opens the clip menu on a selected
clip; a shortcut you set on `Shift+F10` wins.

## Compose

These work on the selected video clip. Pressing a tool shortcut on another page with one media clip
selected opens Compose and starts the tool. Editor shortcuts keep working while an entry in the Compose
list has focus; the list keeps its own arrow keys, `Home`, `End` and `Enter`. A shortcut you set yourself
wins over a default.

| Action | Keys |
|---|---|
| Mask tool | `Shift+M` |
| Reframe tool | `Shift+F` |
| Corner pin tool | `Shift+P` |
| Mask: switch between rectangle and ellipse | `Shift+S` |
| Mask: invert | `Shift+I` |
| Compose: remove the selected entry | `Alt+Backspace` |
| Compose: previous entry | `[` |
| Compose: next entry | `]` |
| Add adjustment clip | `Ctrl/Cmd+Alt+A` |

## Pages

| Action | Keys |
|---|---|
| Go to Media | `Shift+1` |
| Go to Cut | `Shift+2` |
| Go to Edit | `Shift+3` |
| Go to Compose | `Shift+4` |
| Go to Colour | `Shift+5` |
| Go to Audio | `Shift+6` |
| Go to Deliver | `Shift+7` |
| Command palette | `Ctrl/Cmd+K` |

## On a phone

A phone has no page row and no keyboard, so a few actions have no button there. The phone's overflow
menu has a read-only **Keyboard shortcuts** entry that lists them with the reason:

| Action | Why it has no button on the phone |
|---|---|
| Play in to out | Loop in to out is the Loop button; the shifted play shortcut has no separate touch control. |
| Shuttle reverse | Shuttle (J, K, L) is for a keyboard; Play and Pause cover the phone. |
| Stop | Pause stops playback. |
| Shuttle forward | Shuttle (J, K, L) is for a keyboard; Play and Pause cover the phone. |
| Back ten frames | The transport steps one frame; drag the timeline strip to move the playhead further. |
| Forward ten frames | The transport steps one frame; drag the timeline strip to move the playhead further. |
| Move clip to the track above | Moving a clip to another track needs a larger screen; Move earlier and Move later keep a clip on its track. |
| Move clip to the track below | Moving a clip to another track needs a larger screen; Move earlier and Move later keep a clip on its track. |
| Picture handles on the viewer (move, resize, rotate) | The Transform button in the viewer's tool strip turns the handles on and off on a larger screen; on a phone, tapping the picture always selects it. |
| Go to Cut | Cut, Compose and Colour are desktop pages; the phone's dock has Media, Edit, Audio, Look and fix, Captions. |
| Go to Compose | Cut, Compose and Colour are desktop pages; the phone's dock has Media, Edit, Audio, Look and fix, Captions. |
| Go to Colour | Cut, Compose and Colour are desktop pages; the phone's dock has Media, Edit, Audio, Look and fix, Captions. |
| Mask tool | Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page. |
| Reframe tool | Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page. |
| Corner pin tool | Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page. |
| Mask: switch between rectangle and ellipse | Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page. |
| Mask: invert | Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page. |
| Compose: remove the selected entry | Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page. |
| Compose: previous entry | Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page. |
| Compose: next entry | Compose tools and entry shortcuts are for a keyboard on a larger screen; the phone has no Compose page. |
| Add adjustment clip | Adjustment clips are added from the Compose page on a larger screen. |
| Command palette | A command palette needs a keyboard; the phone's More sheet lists actions, it does not search commands. |

Every other action on this page has a button on the phone.

## Changing a shortcut

Open the list with `?` and choose **Edit shortcuts** (or choose the keyboard icon at the top right).
Click **Change** next to an action, then press the keys you want. If two actions end up on the same
key, both are flagged and you must resolve the clash before saving. **Find a shortcut** searches by
action or key, **Export JSON** and **Import JSON** keep your setup in a file, and **Restore previous
settings** brings back what was there before your last save. A change appears at once in the list, in
menus and in tooltips.
