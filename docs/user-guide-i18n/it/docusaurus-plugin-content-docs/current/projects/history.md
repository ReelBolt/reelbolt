---
title: Cronologia del progetto e ripristino
sidebar_position: 5
description: Guarda le versioni precedenti di un progetto, salva una versione con un nome, confrontala con lo stato attuale, ripristina un elemento o l'intero progetto ed elimina per sempre le vecchie copie di un file.
i18n_source_sha: 1d831f0a267b915fcd12f7a0ad53d3925902442b
---
# Cronologia del progetto e ripristino {#project-history-and-restore}

ReelBolt conserva le versioni precedenti del tuo progetto, così puoi vedere cosa è cambiato e tornare indietro. Apri la scheda **Cronologia** nella pagina del progetto per vederle.

## Cosa contiene una versione {#what-a-version-contains}

Una versione è un'istantanea del progetto in un preciso momento. Contiene:

- i tuoi file e quelli scritti dall'assistente
- i tuoi flussi di lavoro
- le tue timeline
- il design system del progetto
- l'elenco delle pronunce
- il nome e la descrizione del progetto

Una versione non contiene gli output renderizzati, i risultati delle esecuzioni, crediti e consumi, le voci, né i collegamenti a una cartella, a un cloud drive o a un repository GitHub. Le voci sono escluse di proposito: il consenso di una voce non viene mai annullato. Un file bloccato da un controllo di sicurezza non viene mai conservato.

## Versioni automatiche e salvate {#automatic-and-saved-versions}

ReelBolt salva una versione per te nei momenti utili: prima che un flusso di lavoro venga eseguito, prima che l'assistente cambi qualcosa, prima di un ripristino e dopo che hai modificato un progetto e poi l'hai lasciato fermo per un po'. Se non è cambiato nulla dall'ultima versione, non ne viene creata una nuova.

- **Le versioni automatiche vengono conservate per 30 giorni.** L'elenco mostra quanti giorni restano. Un progetto ne conserva fino a 200; le più vecchie vengono eliminate per prime.
- **Le versioni salvate restano finché non le elimini.** Scegli **Salva versione**, dai un nome di 1-80 caratteri e la versione resta. Un progetto può avere fino a 100 versioni salvate.
- Puoi rinominare una versione automatica per conservarla. Cancella il nome di una versione salvata e torna automatica, con 30 giorni da quel momento.

Le vecchie copie dei tuoi file contano nello spazio di archiviazione del tuo spazio di lavoro. La scheda Cronologia mostra quanto ne usa la cronologia di un progetto. Eliminare una versione libera lo spazio poco dopo, non subito.

## Confronta una versione con lo stato attuale {#compare-a-version-with-now}

Apri una versione per vedere cosa è cambiato rispetto al progetto com'è adesso, o rispetto a un'altra versione. Ogni elemento è segnato come aggiunto, rimosso, modificato o spostato. Apri un elemento per vedere la differenza:

- I file di testo mostrano le righe modificate.
- I flussi di lavoro mostrano i passaggi aggiunti, rimossi e modificati.
- Le timeline mostrano come cambiano tracce, clip e durata.
- Design system, pronunce e dettagli del progetto mostrano cosa è cambiato.
- I file binari, e i file di testo molto grandi, mostrano solo le dimensioni.

## Ripristina {#restore}

Puoi ripristinare un elemento o l'intero progetto.

| Cosa | Chi può farlo |
|---|---|
| Ripristinare uno o più elementi | Chiunque possa modificare il progetto |
| Ripristinare l'intero progetto | Chi ha creato il progetto, e i proprietari e gli amministratori dello spazio di lavoro |

Prima che cambi qualcosa, ReelBolt mostra un'anteprima di ciò che verrà ripristinato e rimosso, e di quali elementi non si possono ripristinare e perché. Ogni ripristino salva prima una versione dello stato attuale, quindi puoi annullarlo ripristinando quella versione.

Ripristinare l'intero progetto rimuove anche i file, i flussi di lavoro e le timeline che la versione non aveva. Restano nella versione di sicurezza.

Da sapere:

- **Alcuni elementi possono essere saltati.** Il risultato elenca ogni elemento saltato con il motivo, per esempio un altro file usa ora lo stesso percorso, un flusso di lavoro non supera più i controlli di oggi, oppure hai superato il limite di archiviazione. Gli altri vengono ripristinati.
- **L'invio dei contenuti originali a un provider viene disattivato.** Se un passaggio ripristinato aveva questo permesso e il flusso di lavoro attuale no, il passaggio torna con il permesso disattivato. Consentilo di nuovo nel flusso di lavoro.
- **Il ripristino aspetta le esecuzioni in corso.** Se un flusso di lavoro del progetto è in esecuzione, il ripristino viene rifiutato finché non finisce. Due persone non possono ripristinare lo stesso progetto contemporaneamente.
- **Alcuni file non si possono ripristinare.** Un file la cui vecchia copia non è più disponibile è segnato come non ripristinabile. Un file registrato senza verifica si può ripristinare solo finché il file nel progetto è ancora invariato.
- **Assistente.** Puoi chiedere all'assistente di elencare le versioni, mostrare le modifiche, salvare una versione o ripristinare. Ti chiede sempre conferma prima, anche in modalità automatica. Tramite MCP può ripristinare singoli elementi, non un intero progetto.
- **Cartelle sincronizzate.** Se il progetto è sincronizzato con una cartella sul tuo computer, la sincronizzazione successiva può sostituire un file ripristinato con la copia sul computer. Ripristina il file anche lì, oppure metti in pausa la sincronizzazione prima. Vedi [Copiare una cartella del tuo computer in un progetto](./local-folder-sync.md).

## Elimina per sempre {#delete-forever}

Le vecchie versioni conservano vecchie copie di un file. Per cancellarle, apri il file in una versione e scegli **Elimina per sempre**. Il contenuto del file viene rimosso da ogni versione e nessuna versione potrà più ripristinarlo. Possono farlo solo le persone che possono eliminare nel progetto. Non si può annullare.

I file che ReelBolt blocca per motivi di sicurezza vengono cancellati automaticamente dalla cronologia.

## Elimina una versione {#delete-a-version}

Scegli **Elimina versione** su una versione che non ti serve più. Può farlo chiunque possa eliminare nel progetto.
