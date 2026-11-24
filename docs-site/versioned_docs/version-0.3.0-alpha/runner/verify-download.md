---
title: Verify a download
sidebar_position: 7
description: Check that a ReelBolt Runner installer is the file ReelBolt published, using its SHA-256 checksum and its Windows signature.
---

# Verify a download

Each download on [reelbolt.ai/runner](https://reelbolt.ai/runner) comes with a SHA-256 checksum. Compare it with the checksum of the file you downloaded. If they differ, delete the file and download it again.

## Windows (PowerShell)

```powershell
Get-FileHash .\reelbolt-runner-<version>-windows-amd64.msi -Algorithm SHA256
```

## Linux

```sh
sha256sum reelbolt-runner-<version>-linux-amd64.AppImage
```

The long hexadecimal value must match the one on the download page exactly.

## Windows signature

Right-click the `.msi`, choose **Properties**, then **Digital Signatures**. The signature must be listed and valid, and the signer must be the ReelBolt publisher. A valid signature from anyone else is not ReelBolt's. See [Install on Windows](install-windows.md) for the SmartScreen warning.
