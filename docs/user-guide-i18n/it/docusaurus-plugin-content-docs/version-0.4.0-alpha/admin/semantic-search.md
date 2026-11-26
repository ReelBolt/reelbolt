---
title: Ricerca semantica
sidebar_position: 3
description: Cosa sblocca un provider di embedding in ReelBolt - la ricerca nei file di progetto per significato e la ricerca nella documentazione dell'assistente - e come reindicizzare dopo averne configurato uno o aver cambiato modello.
i18n_source_sha: 0f15b71b22fd2e8066dd23c414fa0a7374696dc8
---

# Ricerca semantica {#semantic-search}

La **ricerca semantica** è la ricerca per significato invece che per parole esatte: cercando "schermata di login" puoi trovare un file che dice sempre e solo "pagina di accesso". ReelBolt la usa in due punti, ed entrambi dipendono da una cosa che configura un amministratore: un **provider di embedding**.

## Cosa fa un provider di embedding in ReelBolt {#what-an-embedding-provider-does-in-reelbolt}

Un provider di embedding è un servizio AI che trasforma un testo in un elenco di numeri che ne cattura il significato, in modo che testi dal significato simile abbiano numeri simili. ReelBolt conserva questi numeri per i file dei tuoi progetti e per questa documentazione, e li confronta con la tua domanda per trovare le corrispondenze più vicine.

Senza un provider di embedding non si rompe nulla: il filtro per nome file, i caricamenti, i riepiloghi e i flussi di lavoro continuano a funzionare. Non è disponibile solo la ricerca per significato. Per configurarne uno, vedi "Configurare la ricerca locale" in [Provider di inferenza](./inference-providers.md), oppure collega un modello di embedding Azure OpenAI o compatibile con OpenAI. Solo questi due tipi possono fornire la capacità `Embedding`; Anthropic, Google Gemini e DeepSeek no.

## Cercare dentro i file di progetto in ReelBolt {#searching-inside-project-files-in-reelbolt}

Nella scheda **File** di un progetto, scrivi almeno tre caratteri nella casella del filtro. Oltre a filtrare per nome file, ReelBolt offre un pulsante **Cerca nei file "…"**. Cliccalo per cercare per significato nel contenuto dei file di testo del progetto; i passaggi corrispondenti compaiono sotto **Corrispondenze nei contenuti**, e cliccandone uno si apre il file.

Se vedi **La ricerca nel contenuto non è ancora disponibile** ("La ricerca per significato richiede un provider di embedding e file indicizzati"), o non è configurato alcun provider di embedding oppure i file del progetto non sono ancora stati indicizzati.

Gli agenti usano la stessa ricerca mentre un flusso di lavoro è in esecuzione, quindi un provider di embedding funzionante aiuta anche gli agenti di analisi del codice a trovare le parti rilevanti di una grande base di codice.

Si possono cercare i file di testo e i PDF con testo selezionabile (fino a 20 MB e alle prime 50 pagine). Un PDF scansionato non ha testo da leggere, quindi non viene indicizzato. I file video e audio non vengono mai indicizzati.

## Come ReelBolt indicizza i file di progetto {#how-reelbolt-indexes-project-files}

L'**indicizzazione** è il processo che legge un file, lo divide in passaggi e ne conserva il significato in modo che si possano cercare. Avviene automaticamente in background ogni volta che carichi un file di testo. Ogni file ha uno stato di indice, mostrato nella scheda **Dettagli** sotto **Indice di ricerca**:

| Stato | Significato |
|---|---|
| `Pending` | In attesa di essere indicizzato. |
| `Processing` | In corso di indicizzazione. |
| `Indexed` | Ricercabile. |
| `Failed` | L'indicizzazione non è riuscita, per esempio perché al momento del caricamento del file non esisteva alcun provider di embedding. |
| `NotIndexed` | Non indicizzato di proposito (file video e audio, e PDF senza testo selezionabile). |

## Reindicizzare i file di progetto dopo aver aggiunto un provider di embedding {#reindexing-project-files-after-adding-an-embedding-provider}

I file caricati prima che esistesse un provider di embedding finiscono in `Failed` e non sono ricercabili. Dopo aver configurato un provider:

- **Intero progetto**: nella scheda **File**, apri il menu **⋮** (Altre azioni sui file) e scegli **Reindicizza per la ricerca**, poi **Metti in coda**. Vengono messi in coda fino a 25 file non ancora indicizzati (compresi quelli falliti); eseguilo di nuovo per il lotto successivo. La dimensione del lotto mantiene tranquilli i limiti di frequenza del servizio AI.
- **Un solo file**: apri il menu **⋮** del file (o la sua scheda **Dettagli**) e scegli **Reindicizza**.

## Reindicizzare dopo il passaggio a un altro modello di embedding {#reindexing-after-switching-to-a-different-embedding-model}

ReelBolt tiene un indice di ricerca separato per ogni modello di embedding, perché i numeri di due modelli non si possono confrontare tra loro. Quando imposti come predefinito un provider di embedding diverso (o ne cambi il modello), il vecchio indice resta dov'è e semplicemente non viene più usato, mentre il nuovo indice parte **vuoto**. I risultati di ricerca mancheranno finché i file non vengono reindicizzati con il nuovo modello.

Tieni presente che **Reindicizza per la ricerca** nella scheda File prende solo i file che non sono già `Indexed`. I file indicizzati con il vecchio modello mostrano ancora `Indexed`, quindi dopo un cambio di modello devi:

- usare **Reindicizza** su ogni file importante singolarmente, oppure
- far chiamare a qualcuno con accesso API l'endpoint di reindicizzazione del progetto con `includeIndexed` impostato a `true` (`POST /api/v1/projects/{projectId}/files/reindex` con corpo `{"includeIndexed": true, "maxFiles": 200}`; al massimo 200 file per chiamata).

L'indice della documentazione dell'assistente (sezione successiva) gestisce da solo un cambio di modello.

## Come l'assistente ReelBolt cerca in questa documentazione {#how-the-reelbolt-assistant-searches-this-documentation}

L'[assistente](../assistant/index.md) risponde alle domande su come fare cercando nella documentazione di ReelBolt, usando lo stesso provider di embedding predefinito. Lo fa in due modi: può cercare di proposito quando deve consultare qualcosa, e per ogni domanda che fai recupera automaticamente le poche sezioni più rilevanti.

L'indice della documentazione si costruisce da solo in background. Ogni pochi minuti (ogni 10 minuti per impostazione predefinita) controlla se l'indice corrisponde alla documentazione attuale e al modello di embedding attuale, e lo ricostruisce quando uno dei due è cambiato. Quindi, dopo che aggiungi o cambi un provider di embedding, la ricerca nella documentazione inizia a funzionare, o si allinea al nuovo modello, entro pochi minuti senza che tu faccia nulla. Fino ad allora l'assistente risponde in base a ciò che già sa e ai dati in tempo reale della piattaforma.
