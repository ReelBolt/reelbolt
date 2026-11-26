---
title: Uninstall
sidebar_position: 8
description: Remove the ReelBolt Runner from Windows or Linux and disconnect the computer from your workspace.
---

# Uninstall

1. Disconnect the computer from your workspace. In ReelBolt open **Runners** and remove the computer, or run `reelbolt-runner unpair` on the computer.
2. Remove the app:
   - **Windows:** open **Settings → Apps**, find **ReelBolt Runner** and choose **Uninstall**.
   - **Linux, .deb:** `sudo apt remove reelbolt-runner`.
   - **Linux, AppImage or archive:** delete the file or the folder.
3. Optional: delete the Runner's settings folder (`%AppData%\ReelBolt` on Windows, `~/.config/ReelBolt` on Linux).

Removing the computer from the workspace stops it from receiving work, even if the app is still installed.
