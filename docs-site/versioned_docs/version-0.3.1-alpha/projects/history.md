---
title: Project history and restore
sidebar_position: 5
description: See earlier versions of a project, save a named version, compare it with now, restore one item or the whole project, and erase a file's old copies for good.
---
# Project history and restore

ReelBolt keeps earlier versions of your project so you can see what changed and go back. Open the **History** tab on the project page to see them.

## What a version contains

A version is a snapshot of the project at one moment. It holds:

- your files and the files the assistant wrote
- your workflows
- your timelines
- the project's design system
- the pronunciation list
- the project's name and description

A version does not hold rendered outputs, run results, credits or usage, voices, or links to a folder, cloud drive or GitHub repository. Voices are left out on purpose: a voice's consent is never rolled back. A file that was blocked by a safety check is never kept.

## Automatic and saved versions

ReelBolt saves a version for you at useful moments: before a workflow runs, before the assistant changes something, before a restore, and after a project has been left alone for a while after you edited it. If nothing changed since the last version, no new one is made.

- **Automatic versions are kept for 30 days.** The list shows how many days are left. A project keeps up to 200 of them; the oldest go first.
- **Saved versions are kept until you delete them.** Choose **Save version**, give it a name of 1 to 80 characters, and it stays. A project can hold up to 100 saved versions.
- You can rename an automatic version to keep it. Clear the name of a saved version and it becomes an automatic one again, with 30 days from that moment.

Old copies of your files count toward your workspace's storage. The History tab shows how much a project's history uses. Deleting a version frees that space shortly afterwards, not instantly.

## Compare a version with now

Open a version to see what changed against the project as it is now, or against another version. Each item is marked as added, removed, changed or moved. Open an item to see the difference:

- Text files show the changed lines.
- Workflows show added, removed and changed steps.
- Timelines show how the tracks, clips and length differ.
- Design system, pronunciation and project details show what changed.
- Binary files, and text files that are very large, show only their sizes.

## Restore

You can restore one item or the whole project.

| What | Who can do it |
|---|---|
| Restore one or more items | Anyone who can edit the project |
| Restore the whole project | The person who created the project, and workspace owners and admins |

Before anything changes, ReelBolt shows a preview of what will be restored and removed, and which items cannot be restored and why. Every restore first saves a version of the current state, so you can undo it by restoring that version.

Restoring the whole project also removes files, workflows and timelines that the version did not have. Those stay in the safety version.

Some things to know:

- **Items can be skipped.** The result lists each skipped item with a reason, for example another file now uses the same path, a workflow no longer passes today's checks, or you are over your storage limit. The rest are restored.
- **Sending source media to a provider is switched off.** If a restored workflow step had that permission and the current workflow does not, the step comes back with it off. Allow it again in the workflow.
- **A restore waits for running work.** If a workflow of the project is running, the restore is refused until it finishes. Two people cannot restore the same project at the same time.
- **Some files cannot be restored.** A file whose old copy is no longer available is marked as not restorable. A file that was recorded without a check can be restored only while the file in the project is still unchanged.
- **Assistant.** You can ask the assistant to list versions, show changes, save a version or restore. It always asks you first, even in auto mode. Over MCP it can restore single items, not a whole project.
- **Synced folders.** If the project syncs with a folder on your computer, the next sync can replace a restored file with the copy on your computer. Restore the file there too, or pause the sync first. See [Copying a folder from your computer into a project](./local-folder-sync.md).

## Delete forever

Old versions keep old copies of a file. To erase one, open the file in a version and choose **Delete forever**. That removes the file's content from every version, and no version can restore it. Only people who can delete in the project can do this. It cannot be undone.

Files that ReelBolt blocks for safety reasons are erased from history automatically.

## Delete a version

Choose **Delete version** on a version you no longer need. Anyone who can delete in the project can do this.
