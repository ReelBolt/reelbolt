---
title: Installare su Linux
sidebar_position: 3
description: Installa il ReelBolt Runner su Linux da un pacchetto .deb, da un AppImage o da un archivio tar.gz.
i18n_source_sha: b17e11650e42414765b90b750f237867f2c8402c
---

# Installare su Linux {#install-on-linux}

Scegli un formato da [reelbolt.ai/runner](https://reelbolt.ai/runner). Sostituisci `<version>` qui sotto con la versione che compare nel nome del file scaricato.

## .deb (Ubuntu 24.04 o successivo, e release Debian equivalenti) {#deb-ubuntu-2404-or-newer-and-equivalent-debian-releases}

Il pacchetto richiede GTK 4 e WebKitGTK 6.0 (`libgtk-4-1`, `libwebkitgtk-6.0-4`), che Ubuntu 22.04 non fornisce. Su un sistema più vecchio usa invece l'AppImage o l'archivio.

```sh
sudo apt install ./reelbolt-runner_<version>_amd64.deb
reelbolt-runner pair
```

Il pacchetto installa il Runner in `/opt/reelbolt/runner`. Questi file sono gestiti dal tuo gestore di pacchetti, quindi il Runner non li sostituisce da sé. Quando è disponibile una nuova versione la controlla e ti dice come installarla; aggiorna con lo stesso comando `apt install`.

## AppImage {#appimage}

```sh
chmod +x reelbolt-runner-<version>-linux-amd64.AppImage
./reelbolt-runner-<version>-linux-amd64.AppImage
```

Un AppImage funziona senza installare nulla. Si aggiorna sul posto: il Runner sostituisce il file con la nuova versione. Tienilo in una cartella tua, come `~/Applications`.

## Archivio tar.gz {#targz-archive}

```sh
tar -xzf reelbolt-runner-<version>-linux-amd64.tar.gz
cd reelbolt-runner-<version>-linux-amd64
./reelbolt-runner pair
./reelbolt-runner run
```

Metti la cartella estratta in un posto tuo, come `~/.local/opt/reelbolt-runner`, e aggiungila al `PATH` se vuoi eseguire `reelbolt-runner` da qualsiasi punto.

## Dopo l'installazione {#next}

[Collega il Runner](pairing.md) al tuo spazio di lavoro. Se vuoi che il Runner parta da solo, eseguilo come servizio utente (per esempio un'unità **utente** di systemd, così può raggiungere il tuo portachiavi). Prima verifica il download con [Verificare un download](verify-download.md).
