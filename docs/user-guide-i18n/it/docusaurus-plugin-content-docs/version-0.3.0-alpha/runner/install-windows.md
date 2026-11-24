---
title: Installare su Windows
sidebar_position: 2
description: Installa il ReelBolt Runner su Windows 10 o 11 e cosa aspettarti dall'avviso di Windows SmartScreen.
i18n_source_sha: 4b1131afc41aec3f16998fda90b45643f1c2e16d
---

# Installare su Windows {#install-on-windows}

1. Scarica l'installer per Windows (un file che termina in `.msi`) da [reelbolt.ai/runner](https://reelbolt.ai/runner).
2. Facoltativo ma consigliato: [controlla il download](verify-download.md).
3. Apri il file e segui le indicazioni. Il Runner si installa solo per il tuo utente, in `%LOCALAPPDATA%\ReelBolt\Runner`. Non chiede i diritti di amministratore e non installa un servizio in background.
4. Avvia **ReelBolt Runner** dal menu Start, poi [collegalo](pairing.md).

## L'avviso di SmartScreen {#the-smartscreen-warning}

L'installer è firmato digitalmente. Windows SmartScreen decide se avvisarti in base sia alla firma sia a quante persone hanno già scaricato un file, e un programma nuovo ha poca storia di download. Nelle prime release potresti quindi vedere **"Windows ha protetto il PC"** anche se il file è firmato.

Se lo vedi:

1. Clicca **Ulteriori informazioni**. Dovrebbe comparire il nome dell'editore. Se è vuoto o non è ReelBolt, fermati e non installare.
2. Clicca **Esegui comunque**.

Puoi anche controllare tu stesso la firma: clicca con il tasto destro sul file, scegli **Proprietà**, apri la scheda **Firme digitali** e conferma che la firma risulti valida. L'avviso dovrebbe scomparire man mano che più persone scaricano il file. Controllare il [checksum](verify-download.md) ti dice che il file è quello pubblicato da ReelBolt.
