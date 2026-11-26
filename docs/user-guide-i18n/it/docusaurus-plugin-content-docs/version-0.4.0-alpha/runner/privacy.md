---
title: Privacy e report di arresto anomalo
sidebar_position: 6
description: Cosa invia il ReelBolt Runner se attivi i report di arresto anomalo, e come vederlo prima di decidere.
i18n_source_sha: 422bac1b6d2a572ae955dd0f5d95aafc90d5be54
---

# Privacy e report di arresto anomalo {#privacy-and-crash-reports}

**Non viene inviato nulla se non attivi i report di arresto anomalo.** Il Runner lo chiede una sola volta. Un "no" viene ricordato.

## Cosa contiene un report {#what-a-report-contains}

Un report è costruito a partire da un elenco fisso di campi:

- il tuo sistema operativo e il tipo di CPU;
- la versione del Runner e la versione di Go con cui è stato compilato;
- se viene usato Docker o Podman, e in quale modalità;
- il tipo di evento (un arresto anomalo, oppure un job che il Runner ha rifiutato di avviare), il tipo di job, una classe e un codice di errore e la fase raggiunta;
- per un arresto anomalo, il tipo di errore e i nomi delle funzioni del Runner, dei file sorgente e dei numeri di riga in cui si è verificato;
- un identificativo casuale del report e l'ora in cui è stato creato.

Non contiene mai i tuoi file o cartelle, un progetto, uno spazio di lavoro, il tuo nome, il nome del tuo dispositivo o il testo di un messaggio di errore. Nel report non esiste alcun campo che possa contenerli. Come ogni richiesta internet, il server che riceve un report può vedere l'indirizzo da cui è stato inviato; ReelBolt conserva solo una riga di log e un contatore, non il report.

## Guardalo prima {#see-it-first}

Apri **Impostazioni → Privacy** nel Runner e scegli **Vedi cosa viene inviato**. Mostra il report esatto, costruito sul tuo computer, prima che venga inviato qualsiasi cosa. Puoi disattivare di nuovo i report in qualsiasi momento nello stesso punto.

## Esportazione della diagnostica {#diagnostics-export}

Se contatti il supporto, il Runner può scrivere un file di diagnostica sul tuo computer. Nulla lo carica: viene salvato in una cartella fissa (una cartella `ReelBolt/diagnostics` dentro la tua cartella cache utente) e decidi tu se allegarlo. Nel testo del log sono stati rimossi percorsi, nomi e indirizzi, ma leggilo prima di condividerlo.

Il lavoro che esegui sul tuo computer è descritto nella pagina [Runner desktop](../account/desktop-runner.md), compreso ciò che lascia la macchina.
