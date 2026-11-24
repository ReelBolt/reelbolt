---
title: Updates
sidebar_position: 5
description: How the ReelBolt Runner updates itself, the stable and beta channels, and what happens when an update fails.
---

# Updates

The Runner checks for a new version every few hours (every six by default) and installs it by itself. It never interrupts a job: if a video is being processed, the update waits.

## Channels

- **stable** is the default and is what almost everyone should use.
- **beta** gets new versions earlier and may have more rough edges.

Check your channel and look for updates by hand:

```sh
reelbolt-runner update --check
```

The update setting lives in the Runner's settings file, `runner.json`, in your user config folder (`%AppData%\ReelBolt` on Windows, `~/.config/ReelBolt` on Linux). It has an `updateChannel` value (`stable` or `beta`) and an `autoUpdate` value you can set to `false` to turn the automatic check off.

## What is checked

Before it installs anything, the Runner verifies that the update was signed by ReelBolt and that the downloaded file is exactly the one that was signed. On Windows it also checks the file's digital signature. A file that fails any check is thrown away.

## If an update fails

The Runner keeps working on the version you have. A failure (no internet, a bad signature, a download that did not complete) is logged and tried again later. Nothing is half-installed.

If the Runner replaced its own file (the AppImage and the tar.gz archive on Linux), it keeps the previous version next to it. To go back to it:

```sh
reelbolt-runner update --rollback
```

The Windows installer and the `.deb` package keep no previous copy, so `--rollback` reports that there is nothing to restore. To go back, uninstall and install the older installer from [reelbolt.ai/runner](https://reelbolt.ai/runner) (Windows refuses to install an older version over a newer one, so uninstall first). Packages installed with `apt` are not replaced by the Runner; see [Install on Linux](install-linux.md).
