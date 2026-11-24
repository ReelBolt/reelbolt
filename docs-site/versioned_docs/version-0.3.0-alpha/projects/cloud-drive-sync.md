---
title: Syncing files from Google Drive, OneDrive or Dropbox
sidebar_position: 2
description: Linking a project to a folder on Google Drive, OneDrive/SharePoint or Dropbox — connecting your account, choosing the folder and exactly which files to sync, Sync now (which runs in the background), what happens to your files and what never happens, how Google Docs, Sheets, Slides and Drawings arrive, disconnecting, and what each message means.
---

# Syncing files from Google Drive, OneDrive or Dropbox

A project can be linked to a folder on Google Drive, OneDrive (including SharePoint) or Dropbox. The files you choose are copied into the project and kept up to date: ReelBolt checks the folder regularly, and you can sync on demand. This page covers connecting your account, choosing the folder and the files, syncing, what a sync does to your files, disconnecting, and what the messages on the **Cloud storage** tab mean.

## What linking a cloud folder does

Linking a folder on Google Drive, OneDrive or Dropbox brings its files into your project and keeps them the same as the folder. New files in the folder arrive in the project, changed files are updated, and files deleted from the folder (or that you stop choosing) are removed from the project. ReelBolt **only reads** your folder: it never adds, changes, moves or deletes anything on Google Drive, OneDrive or Dropbox. It also never touches files you or an agent added to the project, or files that came from GitHub or another service, even if one has the same name. A project can be linked to one folder on each of the three services, alongside a GitHub repository.

## Connecting your Google Drive, OneDrive or Dropbox account

Open the project and go to the **Cloud storage** tab. Each service your administrator has turned on has its own card. Choose **Connect** on the card (for example **Connect Google Drive**): the service asks you to sign in, if you are not already, and to allow ReelBolt to **view** your files; then you come straight back to the tab. There is no password or key to copy anywhere. Finish the sign-in in the same browser where you use ReelBolt: if you are signed out of ReelBolt meanwhile, the connection is refused and you can simply start again. You can connect more than one account of the same service, and each connection belongs to you, in the current workspace. If no **Cloud storage** tab or card appears, your administrator has not turned on syncing with that service yet.

## Choosing the folder and the files to sync

After connecting, the card asks you to choose:

1. **The account**, if you have connected more than one.
2. **Where the folder lives**: your own space (My Drive, your OneDrive, your Dropbox) or, when you have them, a shared drive, a SharePoint site's document library, or your Dropbox team space.
3. **The folder**: open folders one level at a time and choose the one to link. Choosing the top level links everything in that space.

You can then narrow the folder to a choice of files and sub-folders: ticking a sub-folder brings in everything inside it, including files added to it later. Choosing nothing in particular means every file in the folder. **Link** links the folder at once and starts copying the files in the background; the card shows **Syncing…** until the copy is done. A large folder can take a few minutes, and you can leave the page meanwhile.

## What happens to your files during a cloud sync

Each sync makes the project's files from that folder match the folder:

- **New files** in the folder are added to the project.
- **Changed files** are updated in place, so anything that uses them keeps working.
- **Unchanged files** are left alone and are not downloaded again.
- **Files removed** from the folder, or that you stop choosing, are removed from the project — but only after a sync has gone through the whole folder. A sync that stops part-way removes nothing.
- **Empty files** are copied like any other file.
- **System clutter** — `.DS_Store`, `Thumbs.db`, `desktop.ini` and the `~$` temporary files Office creates while a document is open — is never copied.
- A file larger than the per-file limit, or one that becomes unavailable while it is being copied, is skipped and the rest are still copied.
- If a file in the folder has the same name and place as a file you uploaded, an agent made, or GitHub or another service brought in, the existing file is kept and the folder's copy is skipped.

What **never** happens: nothing on Google Drive, OneDrive or Dropbox is ever changed, moved or deleted by ReelBolt, and synced files are not summarised or analysed automatically.

## How Google Docs, Sheets, Slides and Drawings arrive

Google's own documents are converted when they are copied, and the new file type is added to the name: a Google **Doc** arrives as a Markdown text file (`.md`), a **Sheet** as a CSV spreadsheet of its first sheet (`.csv`), a **Slides** presentation as a PDF (`.pdf`) and a **Drawing** as a PNG image (`.png`). So a Doc named "Script" appears in the project as `Script.md`. Google Forms, Sites, My Maps and shortcuts cannot be copied and are left out. In the same way, OneNote notebooks and "shared with you" shortcuts on OneDrive, and Dropbox Paper documents, are left out.

## Syncing now and keeping the folder up to date

ReelBolt checks every linked folder by itself regularly (every half hour unless your administrator chose otherwise), so changes arrive without you doing anything. **Sync now** copies the folder as it is right now. The sync runs in the background: the card shows **Syncing…**, checks every few seconds, and then shows when it last synced and how many files the project has from the folder — or, if the sync did not finish, why. You can leave the page while it runs. If a sync is already running, **Sync now** waits for that one. A folder can be synced on demand once every 20 seconds; asking sooner tells you to wait a moment.

A sync keeps its progress as it goes: if it is interrupted part-way, the files copied so far stay in the project and the next sync carries on from there.

## Changing which files are synced

**Change files** opens the folder with your current choice ticked. Saving a new choice starts a sync in the background, so files you unticked leave the project and newly ticked ones arrive. Saving the same choice as before changes nothing. To link a different folder, unlink the current one first, then link the new one.

## Unlinking a folder and disconnecting an account

**Unlink** stops syncing that folder. The files it brought in **stay in the project** — nothing is deleted. You can link the same or another folder again afterwards.

Your connected accounts are listed under **Settings → Connected accounts**, where **Disconnect** removes one: ReelBolt deletes its access and, where the service allows it, tells the service to cancel it. Folders linked with that account stay linked and keep their files, but stop syncing until you link them again with a connected account. You can also remove ReelBolt's access from your Google, Microsoft or Dropbox account settings at any time.

## When a cloud sync does not finish

If a sync fails, the card shows the reason in plain words and the project keeps its files — nothing is deleted. The common messages and what to do:

- **"… no longer accepts ReelBolt's access …"**: the service stopped accepting ReelBolt's access to your account (it was removed, it expired, or your password changed). Choose **Reconnect**, sign in to the same account, and sync again; the folder stays linked.
- **"… is no longer allowed to open it …"**: the folder's owner stopped sharing it with your account. Ask them to share it again, or link another folder.
- **"The linked folder could not be found …"**: the folder was deleted, moved to the bin, or is no longer shared with you. Unlink it and link another folder.
- **"… is limiting how many requests ReelBolt may make right now"** or **"… had a problem answering"**: usually temporary; try again in a few minutes. ReelBolt also retries on its own later.
- **"The linked folder holds more than … files"**, **"Too many files are chosen"** or **"The chosen files are too large to sync together"**: the folder or your choice is over what one project can sync; the message gives the numbers. Choose **Choose fewer files** and untick what you do not need, or link a smaller folder.
- **"… has been disconnected. Link the folder again."**: the account used for this folder was disconnected. Connect an account and link the folder again.
- **"… is not set up on this server"**: your administrator turned syncing with that service off. Contact them.
- **"The sync stopped because of a problem on ReelBolt's side"**: try again in a few minutes, and contact support if it keeps happening.

## When connecting an account does not work

If you come back to the tab with a message that the connection did not finish, it is usually one of these:

- **You cancelled** on the service's page: choose **Connect** again when you are ready.
- **The sign-in took too long** (more than ten minutes) or was already used: start again.
- **You were not signed in to ReelBolt** in that browser, or were signed in as someone else: sign in to ReelBolt and start again from the same browser.
- **Your organisation needs an administrator's approval** (Microsoft says "Need admin approval"): ask your IT administrator to approve the app, then connect again.
- **Google says the app is not verified or blocks you**: during testing only listed people can connect; ask your ReelBolt administrator.
