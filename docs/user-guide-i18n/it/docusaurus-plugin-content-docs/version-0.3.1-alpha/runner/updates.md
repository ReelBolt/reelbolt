---
title: Aggiornamenti
sidebar_position: 5
description: Come si aggiorna da solo il ReelBolt Runner, i canali `stable` e `beta`, e cosa succede quando un aggiornamento fallisce.
i18n_source_sha: a8519253fd89cca55cc3ba3d71e7e878c7252e68
---

# Aggiornamenti {#updates}

Il Runner cerca una nuova versione ogni poche ore (ogni sei di default) e la installa da solo. Non interrompe mai un job: se un video è in elaborazione, l'aggiornamento aspetta.

## Canali {#channels}

- **`stable`** è il canale predefinito e quello che quasi tutti dovrebbero usare.
- **`beta`** riceve le nuove versioni prima e può avere qualche spigolo in più.

Controlla il tuo canale e cerca gli aggiornamenti a mano:

```sh
reelbolt-runner update --check
```

L'impostazione degli aggiornamenti si trova nel file delle impostazioni del Runner, `runner.json`, nella tua cartella di configurazione utente (`%AppData%\ReelBolt` su Windows, `~/.config/ReelBolt` su Linux). Ha un valore `updateChannel` (`stable` o `beta`) e un valore `autoUpdate` che puoi impostare a `false` per disattivare il controllo automatico.

## Cosa viene controllato {#what-is-checked}

Prima di installare qualsiasi cosa, il Runner verifica che l'aggiornamento sia stato firmato da ReelBolt e che il file scaricato sia esattamente quello che è stato firmato. Su Windows controlla anche la firma digitale del file. Un file che non supera un controllo viene scartato.

## Se un aggiornamento fallisce {#if-an-update-fails}

Il Runner continua a funzionare con la versione che hai. Un errore (nessuna connessione, firma non valida, download non completato) viene registrato nel log e ritentato più tardi. Nulla viene installato a metà.

Se il Runner ha sostituito il proprio file (l'AppImage e l'archivio tar.gz su Linux), conserva la versione precedente accanto a quella nuova. Per tornare a quella:

```sh
reelbolt-runner update --rollback
```

L'installer di Windows e il pacchetto `.deb` non conservano una copia precedente, quindi `--rollback` segnala che non c'è nulla da ripristinare. Per tornare indietro, disinstalla e installa l'installer più vecchio da [reelbolt.ai/runner](https://reelbolt.ai/runner) (Windows rifiuta di installare una versione più vecchia sopra una più nuova, quindi disinstalla prima). I pacchetti installati con `apt` non vengono sostituiti dal Runner; vedi [Installare su Linux](install-linux.md).
