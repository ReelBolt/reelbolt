---
title: ReelBolt Runner
sidebar_position: 1
description: Cos'è il ReelBolt Runner, quando usarlo, cosa serve al tuo computer e dove andare per installarlo, collegarlo, aggiornarlo e rimuoverlo.
i18n_source_sha: c67d450f7039addd347e64fd64dcdcab627cf3fd
---

# ReelBolt Runner {#reelbolt-runner}

Il **ReelBolt Runner** è una piccola app che installi sul tuo computer Windows o Linux. Una volta collegato al tuo spazio di lavoro, il lavoro video che può girare su quel computer gira lì invece che nel cloud. Il lavoro svolto sul tuo computer **non consuma crediti ReelBolt**.

## Quando usarlo {#when-to-use-it}

- Generi molti video e vuoi smettere di spendere crediti per la parte più pesante.
- Hai un computer che resta inattivo per gran parte della giornata e può fare il lavoro.
- Vuoi che i file video vengano prodotti su una macchina che controlli tu.

Il ragionamento AI, le chiamate ai modelli, la trascrizione e la fatturazione restano sempre su ReelBolt. Il Runner si occupa solo del lavoro sui media: render video, compilazione dei video e analisi video. Se il tuo computer è spento, il tuo spazio di lavoro può ripiegare sul cloud, a seconda dell'impostazione di calcolo descritta nella pagina [Runner desktop](../account/desktop-runner.md).

## Cosa ti serve {#what-you-need}

- Windows 10 o 11 (64 bit), oppure un desktop Linux a 64 bit.
- Un account ReelBolt e uno spazio di lavoro in cui sei Proprietario o Amministratore, oppure il tuo spazio di lavoro Personale.
- Una connessione a internet. Il Runner fa solo connessioni in uscita verso ReelBolt; non devi aprire nessuna porta.
- Per i render video (Remotion), Docker o Podman. Senza uno dei due, il Runner accetta comunque il lavoro video più semplice e comunica a ReelBolt che non può accettare render. Vedi [Risoluzione dei problemi](troubleshooting.md).

## I passaggi {#the-steps}

1. Scarica l'installer da [reelbolt.ai/runner](https://reelbolt.ai/runner).
2. Installalo: [Windows](install-windows.md) o [Linux](install-linux.md).
3. [Collegalo](pairing.md) al tuo spazio di lavoro.
4. Lascialo in esecuzione. Si [aggiorna da solo](updates.md).

Altre pagine: [verificare un download](verify-download.md), [privacy](privacy.md), [risoluzione dei problemi](troubleshooting.md) e [disinstallazione](uninstall.md).
