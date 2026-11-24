---
title: Syncing files from GitHub
sidebar_position: 1
description: Linking a project to a GitHub repository — connecting your GitHub account, choosing the repository, the branch and exactly which files and folders to sync, what the linked view shows, Sync now (which runs in the background), changing the files, unlinking, and what each message means.
---

# Syncing files from GitHub

A project can be linked to one of your GitHub repositories. The files you choose are copied into the project, and every time someone pushes to the branch you picked, they are updated automatically. This page covers connecting your GitHub account, choosing the repository, the branch and the files, reading the linked view, syncing on demand, changing which files are synced, unlinking, and what the messages on the **Repository** tab mean.

## What linking a GitHub repository does

Linking a repository brings its files into your project and keeps them up to date. You choose a repository, a branch and the files or folders you want; ReelBolt copies them into the project's **Files**, and from then on every push to that branch updates them — new files arrive, changed files are updated, and files deleted in the repository (or that you stop choosing) are removed. ReelBolt **only reads** your repository: it never changes anything on GitHub. It also never touches files you or an agent added to the project yourselves, even if one has the same name as a file in the repository. A project can be linked to one repository, and a repository to one project.

## Connecting your GitHub account

Open the project, go to the **Repository** tab and choose **Connect GitHub account**. GitHub asks you to sign in (if you are not already) and to approve ReelBolt; then you come straight back to the tab. There is no password or token to copy anywhere. You connect once per workspace, and you can disconnect from the same tab or from **Settings → GitHub**, or remove ReelBolt's access from your GitHub settings at any time. Linking, browsing a repository's files, changing which files are synced and **Sync now** all need your own connected account: ReelBolt only shows and syncs repositories your GitHub account can open. If the tab says linking GitHub repositories "is not set up on this server yet", your administrator has to turn the feature on first.

## Choosing the repository

After connecting, the tab lists the repositories ReelBolt can read, grouped by the GitHub account or organisation they belong to; type in **Find a repository** to narrow the list. A repository already linked to another project in the same workspace is shown but cannot be chosen. **If the repository you want is missing**, ReelBolt has not been given access to it on GitHub: choose **Give ReelBolt access to more repositories on GitHub**, pick the repositories on the GitHub page that opens (all of them, or only some), then come back and choose **Reload the list**. Where an account shares only some repositories, a **Change which ones on GitHub** link takes you to the same choice for that account.

## Choosing the branch and the files to sync

Once a repository is chosen, pick the **Branch** to follow (the default branch is listed first and marked "default"). Below it, the branch's files are shown as a tree with a checkbox on every file and folder:

- Ticking a **folder** ticks everything inside it, and a ticked folder also brings in files added to it later. A folder shows a dash when only some of its files are ticked; clicking it then ticks the whole folder.
- **Search files and folders** narrows the tree to what matches. With a search active, **Select N matches** and **Clear matches** act only on what the search shows; without one, **Select all** and **Clear** act on everything.
- Under the tree, a running count shows how many files you have chosen, how much they add up to, and the limit one project can sync. If the choice is over a limit, the reason is shown with the numbers and the **Link** button stays disabled until you untick enough — build output, tests, documentation or raw footage are usually the first things to leave out.
- Folders named `node_modules` and `.git` are never synced, and a single file larger than the per-file limit is skipped (the count tells you how many). Empty files, such as the `.gitkeep` files often used to keep an empty folder, are copied like any other file.

A new link starts with everything ticked when the whole repository fits within the limits, and with nothing ticked when it does not, so you choose. **Link and sync** links the repository at once and starts copying the chosen files in the background; the tab shows the linked view with **Syncing…** until the copy is done, then tells you how many files arrived. A large choice can take a few minutes, and you can leave the page meanwhile. If that first copy cannot finish (for example because GitHub is briefly unavailable), the repository is still linked and the linked view shows why.

## Reading the linked view

Once linked, the **Repository** tab shows the repository name (a link to it on GitHub) and a status: **Syncing…** while files are being copied, then **Synced**, **Last sync failed** or **Not synced yet**. Below it you see:

- **Branch** — the branch being followed, linked to its files on GitHub.
- **Files included** — the files and folders you chose, or "Every file in the repository".
- **Last synced** — how long ago the last successful sync finished and the commit it copied, linked to that commit on GitHub.
- **Files in this project** — how many files the link keeps in the project.
- **Linked** — when the repository was linked.

The project's header also shows a small **Linked to GitHub** label on every tab; it turns red as **GitHub sync failed** when the last sync did not finish, and clicking it opens the **Repository** tab.

## Syncing now and changing the files

**Sync now** copies the branch as it is right now, without waiting for a push. The copy runs in the background: the button and the status change to **Syncing…**, the tab checks every few seconds, and when the sync is done a message tells you how many files the project now has from the repository — or, if it did not finish, why. You can leave the page while it runs; the sync carries on. If a sync is already running, **Sync now** simply waits for that one. To avoid repeating the same work, a project can be synced once every 20 seconds; asking sooner tells you how long to wait.

**Change files** opens the same tree with your current choice ticked, and lets you change the branch too; **Save and sync** saves the new choice and starts a sync in the background, so files you unticked leave the project and newly ticked ones arrive. Saving a choice that is the same as before changes nothing.

A sync keeps its progress as it goes. If it is interrupted part-way — the server restarts, or GitHub stops answering — the files copied so far stay in the project, and the next sync carries on from there instead of starting again. Files are only removed from the project after a sync has gone through the whole choice.

## When a sync does not finish

If a sync fails, the tab shows **The last sync did not finish** with the reason in plain words, and the project keeps the files from the last successful sync, plus any files the failed sync had already copied — nothing is deleted. A single file GitHub will not hand over does not stop a sync: it is skipped and the rest are copied. The common reasons:

- **Too many files are chosen** (or **too large to sync together**): the chosen files are over what one project can sync; the message gives both numbers. Choose **Choose fewer files** and untick folders you do not need.
- **The branch could not be found**: it was renamed or deleted on GitHub; choose another branch under **Change files**.
- **GitHub refused ReelBolt access**: ReelBolt's access to the repository was removed on GitHub; give it access again, then sync.
- **GitHub had a problem answering**: usually temporary; try the sync again in a few minutes.
- **ReelBolt could not reach GitHub**: the connection failed or took too long; try again in a few minutes.
- **A problem inside ReelBolt, not on GitHub**: something went wrong on ReelBolt's side. Try again in a few minutes, and contact support if it keeps happening.
- **Connect your GitHub account first**: your GitHub connection in this workspace is missing or stopped working; the message offers **Connect GitHub account**.

A later push to the branch also repairs the project on its own once the cause is fixed.

## Unlinking a repository

**Unlink repository** (at the bottom of the tab) asks for confirmation and then stops the link: pushes no longer update the project. The files the repository brought in **stay in the project** — nothing is deleted. You can link the same or another repository again afterwards. Disconnecting your GitHub account does not unlink anything; it only means you cannot change the link or sync on demand until you connect again.
