---
title: Install on Linux
sidebar_position: 3
description: Install the ReelBolt Runner on Linux from a .deb package, an AppImage or a tar.gz archive.
---

# Install on Linux

Pick one format from [reelbolt.ai/runner](https://reelbolt.ai/runner). Replace `<version>` below with the version in the file name you downloaded.

## .deb (Ubuntu 24.04 or newer, and equivalent Debian releases)

The package needs GTK 4 and WebKitGTK 6.0 (`libgtk-4-1`, `libwebkitgtk-6.0-4`), which Ubuntu 22.04 does not provide. On an older system use the AppImage or the archive instead.

```sh
sudo apt install ./reelbolt-runner_<version>_amd64.deb
reelbolt-runner pair
```

The package installs the Runner under `/opt/reelbolt/runner`. Your package manager owns these files, so the Runner does not replace them itself. When a new version is available it checks it and tells you how to install it; update with the same `apt install` command.

## AppImage

```sh
chmod +x reelbolt-runner-<version>-linux-amd64.AppImage
./reelbolt-runner-<version>-linux-amd64.AppImage
```

An AppImage runs without installing anything. It updates in place: the Runner replaces the file with the new version. Keep it in a folder you own, such as `~/Applications`.

## tar.gz archive

```sh
tar -xzf reelbolt-runner-<version>-linux-amd64.tar.gz
cd reelbolt-runner-<version>-linux-amd64
./reelbolt-runner pair
./reelbolt-runner run
```

Put the extracted folder somewhere you own, such as `~/.local/opt/reelbolt-runner`, and add it to your `PATH` if you want to run `reelbolt-runner` from anywhere.

## Next

[Pair the Runner](pairing.md) with your workspace. If you want the Runner to start on its own, run it as a user service (for example a systemd **user** unit, so it can reach your keyring). Verify the download first with [Verify a download](verify-download.md).
