---
title: Disinstallare
sidebar_position: 8
description: Rimuovi il ReelBolt Runner da Windows o Linux e scollega il computer dal tuo spazio di lavoro.
i18n_source_sha: b6a64528753cfa52892302595f0b4cafb3b0f448
---

# Disinstallare {#uninstall}

1. Scollega il computer dal tuo spazio di lavoro. In ReelBolt apri **Runner** e rimuovi il computer, oppure esegui `reelbolt-runner unpair` sul computer.
2. Rimuovi l'app:
   - **Windows:** apri **Impostazioni → App**, trova **ReelBolt Runner** e scegli **Disinstalla**.
   - **Linux, .deb:** `sudo apt remove reelbolt-runner`.
   - **Linux, AppImage o archivio:** elimina il file o la cartella.
3. Facoltativo: elimina la cartella delle impostazioni del Runner (`%AppData%\ReelBolt` su Windows, `~/.config/ReelBolt` su Linux).

Rimuovere il computer dallo spazio di lavoro gli impedisce di ricevere lavoro, anche se l'app è ancora installata.
