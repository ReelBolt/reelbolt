---
title: Leggere la stima
sidebar_position: 8
description: Cosa significa la riga del costo prima dell'esecuzione di un flusso di lavoro, da dove vengono le cifre, come vengono mostrati il video generato e un intervallo ampio, cosa serve per avviare un'esecuzione, cosa è gratis e cosa succede quando il saldo si abbassa.
i18n_source_sha: cc6f38c047aa8ef097bcbd7dfe71af695c81c1c4
---

# Leggere la stima {#reading-the-estimate}

Questa pagina spiega la riga del costo che ReelBolt mostra prima di avviare l'esecuzione di un flusso di lavoro: cosa significano il costo tipico e l'intervallo, da dove vengono le cifre, come viene mostrato il video generato e cosa deve esserci nel tuo saldo prima che l'esecuzione possa partire. Leggila prima di avviare un'esecuzione per decidere se ricaricare o alleggerire il flusso di lavoro. Per sapere come i crediti vengono assegnati e spesi, vedi [Piani e crediti](./plans-and-credits.md).

## Leggere la stima {#read-the-estimate}

Prima di avviare un flusso di lavoro, ReelBolt mostra quanto dovrebbe costare l'esecuzione, per esempio **Questa esecuzione dovrebbe costare ≈ 40 crediti (di solito 30–52)**.

- **≈ 40 crediti** è il costo tipico: quello a cui di solito arriva un'esecuzione come questa.
- **di solito 30–52** è l'intervallo in cui rientra la maggior parte delle esecuzioni. Alcune costano meno o più, perché un modello può scrivere più o meno testo, o un passaggio di revisione può chiedere un altro ciclo.

Se la cifra più bassa e quella più alta coincidono, viene mostrata solo la cifra tipica.

## Da dove vengono le cifre {#where-the-figures-come-from}

Una riga sotto la stima dice su cosa si basa:

- **"Prima esecuzione, quindi questa è una stima approssimativa basata sulle cifre tipiche per questi passaggi."** Il tuo spazio di lavoro non ha ancora abbastanza esecuzioni completate di questo flusso di lavoro o di questi passaggi, quindi ReelBolt usa quanto costa di solito ciascun tipo di passaggio. Considerala un'indicazione.
- **"Basata su quanto sono costati questi passaggi in questo spazio di lavoro."** Passaggi come questi sono già stati eseguiti nel tuo spazio di lavoro e viene usato il loro costo reale. Per i passaggi che generano il video, le esecuzioni a ciascuna risoluzione sono contate separatamente, quindi una stima in 4K usa i render in 4K precedenti non appena ce ne sono alcuni.
- **"Basata su N esecuzioni passate di questo flusso di lavoro."** Quando il flusso di lavoro è terminato con successo alcune volte, alla risoluzione che hai scelto, dall'ultima volta che lo hai modificato, la stima deriva da quanto sono costate davvero quelle esecuzioni. Modificare il flusso di lavoro fa ripartire il conteggio, perché le esecuzioni più vecchie non lo descrivono più.

## Il video generato è mostrato a parte {#generated-video-is-shown-separately}

Un flusso di lavoro che genera clip video aggiunge una riga come **Più fino a 300 crediti per il video generato, calcolati sul numero massimo di clip che può ordinare.** Il video generato ha un prezzo fisso per clip, quindi ReelBolt indica il massimo che può costare invece di tirare a indovinare. Se un passaggio di revisione può rimandare indietro il lavoro per un altro ciclo, la riga dice anche che il video può essere ordinato di nuovo, e l'importo include già quei cicli aggiuntivi.

## Perché un intervallo può essere ampio {#why-a-range-can-be-wide}

Quando il limite superiore dell'intervallo è almeno il doppio di quello inferiore, la stima elenca i motivi sotto **Perché l'intervallo è così ampio:**. I motivi sono:

- il passaggio di revisione può rimandare indietro il montaggio per un altro ciclo;
- analizzare e tagliare i filmati costa di più per le clip più lunghe;
- un passaggio ripetuto viene eseguito una volta per elemento, e il numero di elementi si conosce solo durante l'esecuzione;
- i passaggi che usano il tuo provider AI non vengono addebitati.

## Cosa serve per avviare un'esecuzione {#what-must-be-available-to-start-a-run}

Per avviare un'esecuzione servono abbastanza crediti disponibili da coprire quanto ReelBolt trattiene per ogni passaggio prima che quel passaggio venga eseguito. Questo importo è calcolato separatamente dalla stima, quindi può essere più alto o più basso. Alla prima esecuzione è almeno il limite inferiore dell'intervallo abituale, e spesso di più, perché ogni passaggio trattiene almeno un minimo standard per il suo tipo.

Una risoluzione più alta aumenta questo importo per i passaggi che generano il video. Una risoluzione più bassa non lo riduce mai. Lo mostra anche la stima, per esempio **Il tuo spazio di lavoro ha 500 crediti disponibili, che coprono i 120 trattenuti da questa esecuzione prima di partire.**

Se lo spazio di lavoro non ne ha abbastanza, vedi **Crediti insufficienti per questa esecuzione** con l'importo necessario. A quel punto ricarica oppure alleggerisci il flusso di lavoro.

## Quando l'esecuzione potrebbe richiedere più crediti di quelli disponibili {#when-the-run-may-need-more-credits-than-it-has}

Se il tuo spazio di lavoro ha abbastanza crediti per avviare l'esecuzione ma meno di quanto l'esecuzione costa di solito, compreso quanto indicato con "Più fino a", vedi **Potresti restare senza crediti a metà di questa esecuzione.** Le esecuzioni spesso costano più dell'importo necessario per avviarle, quindi ricarica prima per evitare che l'esecuzione si fermi a metà.

## Cosa è gratis {#what-is-free}

Alcune attività non costano nulla, altre consumano crediti. Paghi per ciò che un'esecuzione usa davvero, non per quanto è stato trattenuto prima che partisse.

- **Gratis:** i riepiloghi dei file, l'indicizzazione e la ricerca, l'archiviazione (fino al limite di archiviazione del tuo piano), il lavoro svolto con un provider AI che il tuo spazio di lavoro ha collegato da sé e il lavoro svolto su un computer che il tuo spazio di lavoro ha collegato da sé.
- **Consuma crediti:** i passaggi dei flussi di lavoro che usano i provider di ReelBolt, gli strumenti AI per le immagini, i suggerimenti per la timeline, l'assistente dopo che hai esaurito l'allocazione mensile, le grafiche progettate e il video generato.

## Se un'esecuzione fallisce {#if-a-run-fails}

Se un passaggio fallisce per colpa della piattaforma, i crediti che stava trattenendo vengono rimborsati, quindi non paghi un passaggio andato storto per colpa di ReelBolt. Se fallisce per un tuo input, perché l'hai fermato tu o perché ha fallito un provider che il tuo spazio di lavoro ha collegato da sé, i crediti effettivamente usati restano addebitati. ReelBolt lo decide in base a ciò che ha osservato.

## Cosa succede quando il saldo si abbassa {#what-happens-when-the-balance-runs-low}

- **Il lavoro si ferma invece di spendere troppo.** Un passaggio che richiede più crediti di quelli disponibili viene rifiutato prima di partire, quindi non ti viene mai addebitato più del tuo saldo senza che tu ricarichi.
- **Ricarica** da **Impostazioni → Piano e fatturazione**, sotto **Acquista crediti**. Solo il Proprietario dello spazio di lavoro e i membri con il ruolo Fatturazione possono acquistare crediti.
- **Passa a un piano più grande** i cui crediti mensili si adattano al tuo utilizzo. Vedi [Gestire il tuo piano](./managing-your-plan.md).

## Pagine correlate {#related-pages}

- [Piani e crediti](./plans-and-credits.md): come i crediti vengono assegnati, spesi e ricaricati.
- [Scegliere la risoluzione](./choosing-resolution.md): come la risoluzione scelta cambia la stima.
- [Quanto costa l'assistente](./what-the-assistant-costs.md): il costo della chat e del lavoro con Claude Code.
