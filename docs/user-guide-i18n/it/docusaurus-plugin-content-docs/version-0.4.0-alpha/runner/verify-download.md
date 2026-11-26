---
title: Verificare un download
sidebar_position: 7
description: Controlla che un installer del ReelBolt Runner sia il file pubblicato da ReelBolt, usando il suo checksum SHA-256 e la sua firma di Windows.
i18n_source_sha: 80ac30f8d7104183c219cff94b9e66e9750a139d
---

# Verificare un download {#verify-a-download}

Ogni download su [reelbolt.ai/runner](https://reelbolt.ai/runner) è accompagnato da un checksum SHA-256. Confrontalo con il checksum del file che hai scaricato. Se sono diversi, elimina il file e scaricalo di nuovo.

## Windows (PowerShell) {#windows-powershell}

```powershell
Get-FileHash .\reelbolt-runner-<version>-windows-amd64.msi -Algorithm SHA256
```

## Linux {#linux}

```sh
sha256sum reelbolt-runner-<version>-linux-amd64.AppImage
```

Il lungo valore esadecimale deve coincidere esattamente con quello nella pagina di download.

## Firma di Windows {#windows-signature}

Clicca con il tasto destro sul file `.msi`, scegli **Proprietà**, poi **Firme digitali**. La firma deve essere presente e valida, e il firmatario deve essere l'editore ReelBolt. Una firma valida di qualcun altro non è di ReelBolt. Vedi [Installare su Windows](install-windows.md) per l'avviso di SmartScreen.
