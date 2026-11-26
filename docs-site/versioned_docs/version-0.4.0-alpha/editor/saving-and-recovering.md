---
title: Saving, recovering and sharing a timeline
sidebar_position: 4
description: How the editor saves your work, what the save message at the bottom means, what happens if your connection drops, you are signed out, or someone else edits the same timeline, and what "You can look but not change" means.
---

# Saving, recovering and sharing a timeline

The editor saves for you, a moment after every change. There is no Save button. The message at the bottom left of
the editor always says where your work stands.

## What the message says

| You see | It means | What to do |
|---|---|---|
| **Saved** | Everything you did is stored. | Nothing. |
| **Unsaved changes** / **Saving...** | A change is on its way. | Nothing; it takes a moment. |
| **Can't reach the server. Trying again...** | The save did not get through. The editor keeps trying. | Nothing yet. |
| **You are offline. 2 changes will save when you are back** | Your connection is gone. Your changes are kept on this device. | Keep working, or reconnect. They save by themselves. |
| **Sign in again to keep saving** | Your session ended. Nothing is lost. | Sign in in the window that opens; your edit is saved right after. You stay where you are. |
| **Not saved. The server had a problem.** | The server could not store the change. | Press **Retry**. |
| **Not saved. This timeline is too large to save.** | The timeline is bigger than a single save can hold. | Remove some clips or media, then press **Retry**. |
| **Not saved. You no longer have permission to change this timeline.** | Your access changed. | Ask the project owner. |
| **Not saved. This timeline was deleted.** | Someone deleted it. | Your edits stay on screen; copy what you need. |
| **Not saved. The server refused this change.** followed by a reason | The change breaks a rule of the timeline. The reason is in plain words. | Undo the change or fix what the reason names, then press **Retry**. |
| **Not saved. This timeline was changed somewhere else.** | Another tab, device or person saved first. | Choose what to keep (below). |
| **Open in another tab** | The timeline is already open in another tab of this browser. | Use that tab, or press **Edit here instead**. |
| **View only** | You can look but not change this timeline. | Ask the project owner for edit access. |

On a phone, **Retry** and the other buttons are large enough to tap with a thumb.

If your browser cannot keep anything on the device (a locked-down private window), the message adds **Edits are held in
memory only**: closing the tab before the change is saved would lose it.

## If the tab closes before your last change was saved

The editor keeps a copy of unsaved changes on your device. Next time you open the timeline it says **We recovered edits
from your last visit** with two buttons:

- **Restore** puts them back and saves them. If someone else changed the timeline meanwhile, the editor merges your
  changes with theirs when they do not touch the same clips.
- **Discard** forgets them.

Nothing is written until you choose.

## If someone else edits the same timeline

When your change and a change saved elsewhere do not touch the same clips, the editor merges them quietly and shows
**Your edits were merged with changes from another tab or device.**

When both of you changed the same clip, the editor stops and asks, and **nothing is lost either way**:

- **Keep my copy** saves your edits as a new timeline called "*name* (my edits)" and lets you open it.
- **Use the latest** loads what is saved now. First it saves your edits to a file in your downloads, so you can
  look at them later.

## Two tabs, one timeline

If you open a timeline that is already open in another tab of the same browser, the new tab is read-only and says
**Open in another tab**. Press **Edit here instead** to take over: the other tab saves what it has, becomes read-only,
and this tab continues from there.

## You can look but not change

If your role lets you open a timeline but not change it, the editor opens it read-only with **You can look but not
change this timeline.** Nothing you press can change or save anything: editing buttons are switched off and keys do
nothing.
