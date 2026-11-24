---
title: Copying a folder from your computer into a project
sidebar_position: 3
description: Using the ReelBolt desktop app to keep a folder on your own computer copied into a project — choosing the folder, what is copied and what never happens, files that are only in iCloud, OneDrive or Dropbox, pausing and resuming, and what each message means.
---

# Copying a folder from your computer into a project

You can point the ReelBolt desktop app at a folder on your computer — your Desktop, a camera-card folder, or
a folder in iCloud Drive, OneDrive or Dropbox — and ReelBolt keeps a copy of everything in it inside one of
your projects. It is one-way (your computer to ReelBolt) and it is live: change a file while the app is
running and the project catches up within seconds, and anything you changed while the app was closed is
copied when it starts again.

This is how iCloud Drive works with ReelBolt. Apple does not let a website read your iCloud Drive directly,
but on Windows the iCloud app shows iCloud Drive as an ordinary folder in File Explorer, so ReelBolt can
copy from it like any other folder. OneDrive, Dropbox, Box, a NAS share and a plain folder on your disk work
the same way.

**Windows and Linux only.** There is no Mac version of the desktop app, so this feature is not available
on a Mac. On Windows, iCloud Drive is covered through Apple's own iCloud for Windows.

## Installing the ReelBolt desktop app

Computer folder sync is part of the **ReelBolt desktop app** (also called the desktop runner). It is the same
app that can render your videos on your own machine; folder sync is one of its screens.

1. Build the app from the ReelBolt source tree for **Windows** or **Linux**; ReelBolt does not publish an
   installer. [Desktop runner](../account/desktop-runner.md#installing-and-running-a-reelbolt-desktop-runner)
   gives the commands.
2. Sign it in by pairing it with your workspace: the app shows a short code, and you approve it under
   **Runners → Pair a device** (see [Desktop runner](../account/desktop-runner.md) for the pairing steps).
3. Leave the app running. Folder sync only works while the app is running.

If the app's **Folders** screen says it cannot open a folder chooser, the app was started from a terminal
without a window; there is a command to add a folder from the terminal shown on that screen.

## Adding a folder to copy

1. In the desktop app, open **Folders** (on the app's window, it is the section titled **Folders on this
   computer**).
2. Choose **Add a folder**. Pick the folder you want to copy, for example `C:\Users\you\iCloudDrive\Footage`.
3. Choose the project to copy it into, and the folder name to use inside that project. ReelBolt fills in the
   folder's own name (so `Footage` becomes the project folder `Footage`), and you can change it.
4. Confirm. The folder appears in the list with its status, and copying starts.

You can add several folders, into the same project or into different ones. Each one is copied into its own
folder inside the project, so two folders can never overwrite each other. You can also sync from more than
one computer — each computer's folders land in their own project folders.

## What gets copied, and what never happens

While the app runs, the project is kept the same as the folder you chose:

- **New files** in the folder are added to the project.
- **Changed files** are updated in the project in place, so anything that uses them keeps working. The file
  keeps its identity — a workflow that refers to it still finds it.
- **Files you delete from the folder** are removed from the project too. Before removing a large part of a
  folder, ReelBolt stops and asks (see "Pausing and resuming").
- **Unchanged files** are left alone and are not uploaded again.
- **Empty files** are copied like any other file.

What never happens:

- **Nothing is ever written to your computer.** ReelBolt only reads the folder. It never edits, moves,
  renames or deletes anything on your disk or in your cloud storage.
- **Files you uploaded yourself are never touched**, and neither are files an agent wrote or files that came
  from GitHub or a cloud drive — even if one has the same name. In that case the folder's copy is skipped
  and the app says so.
- **Removing a folder does not delete anything.** The files already copied stay in the project.

Some files are never copied, and each one is counted in the app's status so you can see what was left out:
hidden files and folders (names starting with a dot), system clutter such as `.DS_Store`, `Thumbs.db` and
`desktop.ini`, temporary files such as `~$…`, `.tmp`, `.part` and `.crdownload`, and shortcuts (symlinks).
A single file that cannot be read is skipped and the rest are still copied.

## Files that are only in iCloud, OneDrive or Dropbox

iCloud, OneDrive and Dropbox can keep some files **only in the cloud** to save space on your PC. Those files
have no contents on your disk until something opens them. ReelBolt will not open them on its own, because
that would silently download gigabytes you may not want downloaded — so they are skipped and the app lists
them as "only in the cloud, not downloaded to this PC".

To copy them, tell the service to keep them on this PC:

- **iCloud Drive**: right-click the folder in File Explorer and choose **Always keep on this device** (in
  older versions of iCloud for Windows the option is called **Keep Downloaded**). You can do this on one
  file or on the whole folder.
- **OneDrive**: right-click the folder and choose **Always keep on this device**.
- **Dropbox**: right-click the folder and choose **Make available offline**.

After the files are downloaded, ReelBolt copies them the next time it checks the folder — immediately if the
app is running. The same rule applies to any cloud folder that uses "online-only" files.

## Pausing and resuming a folder

You can pause a folder in the desktop app so nothing is copied from it for a while; **Resume** starts it
again. Pausing does not delete anything in the project.

ReelBolt also pauses a folder **by itself** when the folder suddenly looks empty or mostly gone — for
example a drive that was not plugged in, or a cloud folder that has not finished loading. This is a safety
measure so a folder that is temporarily unavailable cannot wipe your project. When this happens the app
shows:

> "The folder on **this computer** looks empty or mostly gone — sync is paused so nothing is deleted. If you
> really removed those files, resume it from the desktop app."

What to do:

- If you did **not** delete those files, leave it paused, fix the folder (plug the drive back in, let the
  cloud folder finish loading), and resume it. Nothing was deleted.
- If you **did** delete them on purpose, choose **Resume and remove deleted files** — the button the app
  shows for exactly this pause. It confirms the removals, and the project catches up.

A folder paused in the app can also be resumed from the project's page in ReelBolt, where its computers are
listed. A pause made in ReelBolt is resumed in ReelBolt.

## What each message means

The **Folders** screen shows one status per folder. The common ones:

- **Up to date** — everything in the folder is in the project. Nothing to do.
- **Syncing N files** — the app is copying files right now.
- **Paused** — nothing is being copied. Resume it when you want it to continue.
- **Needs attention** — something stopped the sync; the line underneath says what. The common reasons:
  - *"This computer is no longer connected to ReelBolt — pair it again."* The app was unpaired or revoked
    from the workspace. Pair the computer again.
  - *"The person who connected this computer can no longer change projects in this workspace."* The account
    that paired this computer left the workspace or lost permission to edit. Connect the computer again with
    an account that can.
  - *"This folder is no longer linked to a ReelBolt project."* The folder was unlinked in ReelBolt. Add it
    again if you still want it copied.
  - *"Storage is full (X of Y used)."* The workspace has no storage left. Delete files or upgrade the plan,
    then it continues on its own.
  - *"A file from another source already has this name in the project, so this one is skipped."* A file you
    uploaded, an agent wrote, or GitHub or a cloud drive brought in already has that name and place. Rename
    one of them if you want the folder's copy.
  - *"Only in iCloud/OneDrive/Dropbox, not downloaded to this PC…"* See the section above; use **Always keep
    on this device**.
- **Some files were not copied** — a yellow note listing how many and why, with a few example names. The
  reasons are the list under "What gets copied, and what never happens".

## Removing a folder

Choose **Remove** on a folder in the desktop app and confirm. ReelBolt stops copying from it, and **the files
already copied stay in the project** — nothing is deleted. If you want it copied again later, add it again.
(You can also unlink a folder from the project's page in ReelBolt, with the same result.)

## Storage: how much your plan includes

Everything in your workspace counts against your plan's storage: files you upload, files synced from GitHub,
a cloud drive or a computer folder, files agents write, and the videos and other outputs a run produces. The
plans include:

| Plan | Storage |
|---|---|
| Trial | 1 GB |
| Creator | 15 GB |
| Pro | 50 GB |
| Team | 100 GB |
| Enterprise | unlimited |

When the storage is full, new files stop being added — an upload is refused, and a sync leaves the files that
did not fit and tells you. **Nothing is deleted automatically**, even if you move to a smaller plan: you free
space by deleting files, or you upgrade.

See how much you are using on **Settings → Plan and billing** ("4.2 GB of 15 GB used") or on the **Usage**
page, and see [Plans and credits](../account/plans-and-credits.md) for what counts and what to do when it is
full.

## Related pages in ReelBolt

- [Desktop runner](../account/desktop-runner.md) — installing the app, pairing a computer, and what data
  leaves your machine.
- [Plans and credits](../account/plans-and-credits.md) — the storage each plan includes, and what counts
  toward it.
- [Syncing files from Google Drive, OneDrive or Dropbox](./cloud-drive-sync.md) — the other way to bring a
  cloud folder into a project, which reads the cloud directly instead of going through the desktop app.
- [Syncing files from GitHub](./github-repository.md) — keeping a project in step with a repository.
- [Troubleshooting](../troubleshooting.md) — the full list of messages and what to do about them.
