---
title: Install on Windows
sidebar_position: 2
description: Install the ReelBolt Runner on Windows 10 or 11, and what to expect from the Windows SmartScreen warning.
---

# Install on Windows

1. Download the Windows installer (a file ending in `.msi`) from [reelbolt.ai/runner](https://reelbolt.ai/runner).
2. Optional but recommended: [check the download](verify-download.md).
3. Open the file and follow the prompts. The Runner installs for your user only, in `%LOCALAPPDATA%\ReelBolt\Runner`. It does not ask for administrator rights and does not install a background service.
4. Start **ReelBolt Runner** from the Start menu, then [pair it](pairing.md).

## The SmartScreen warning

The installer is digitally signed. Windows SmartScreen decides whether to warn you based on both the signature and how many people have downloaded a file before, and a new program has little download history. For the first releases you may therefore see **"Windows protected your PC"** even though the file is signed.

If you see it:

1. Click **More info**. The publisher name should be shown. If it is blank or is not ReelBolt, stop and do not install.
2. Click **Run anyway**.

You can also check the signature yourself: right-click the file, choose **Properties**, open the **Digital Signatures** tab and confirm the signature is listed as valid. The warning is expected to fade as more people download the file. Checking the [checksum](verify-download.md) tells you the file is the one ReelBolt published.
