---
title: Cos'è ReelBolt
sidebar_position: 1
description: Scegli cosa vuoi creare con ReelBolt, da un video promozionale al taglio dei tuoi filmati, ai sottotitoli, a una voce, a un doppiaggio o a una miniatura, e trova la pagina che ti mostra come fare.
i18n_source_sha: 74f4a4a462f9ceab26a8a9a1c55411bc3828290e
---

:::note Versione alpha

ReelBolt 0.4.0-alpha — alpha pubblica. Aspettati qualche spigolo: le cose potrebbero cambiare.

L'accesso anticipato significa che le funzioni possono cambiare, che non c'è alcuna garanzia di livello di servizio e che il tuo feedback è benvenuto.

:::

# Cos'è ReelBolt {#what-is-reelbolt}

## Cosa vuoi fare? {#what-do-you-want-to-make}

Scegli l'obiettivo che fa per te. Ogni link apre la pagina che ti mostra come fare.

- **Un video promozionale per il mio prodotto o la mia app:** [Video promozionale dal tuo codice](./recipes/promo-from-code.md), oppure [Promo di prodotto, a modo tuo](./recipes/product-promo-your-way.md).
- **Tagliare i miei filmati:** [Modifica il mio video](./recipes/edit-my-video.md) toglie i tempi morti e i ciak ripetuti da una clip che hai girato.
- **Sottotitoli:** [Aggiungi i sottotitoli a un video](./recipes/subtitles.md).
- **Una voce o una narrazione:** [Voce narrante](./recipes/voiceover.md), oppure [Leggi il mio testo ad alta voce](./recipes/read-text-aloud.md).
- **Doppiare in un'altra lingua:** [Doppia un video in un'altra lingua](./recipes/dub-a-video.md).
- **Sfocare o evidenziare qualcosa:** [Sfoca o evidenzia un oggetto](./recipes/prompted-region-effects.md).
- **Green screen:** [Un inserto di schermo tracciato](./recipes/tracked-screen-insert.md) mette i tuoi contenuti su un green screen in una clip.
- **Una miniatura:** [Miniatura e testo di copertina](./recipes/thumbnail-and-cover-text.md).
- **Cambiare un video finito:** [Chiedi modifiche](./basics/ask-for-changes.md).
- **Non ancora supportato:** avatar parlanti più lunghi di circa 15 secondi, caroselli di immagini e riformattazione di un video finito. Vedi [Cosa ReelBolt non sa ancora fare](./recipes/choose-a-recipe.md#what-reelbolt-cannot-do-yet).
- **Qualcos'altro:** chiedi all'[assistente](./basics/assistant-basics.md). Ti dice quale app va bene, oppure che nessuna va bene.

Tutte le app in un unico elenco, con ciò di cui ha bisogno ciascuna: [Scegli un'app](./recipes/choose-a-recipe.md).

## Sei nuovo? Parti da Le basi {#new-here-start-with-the-basics}

[Le basi](./basics/index.md) sono guide brevi e illustrate che ti portano dalla registrazione al tuo primo video in circa dieci minuti. Le guide da 1 a 4 sono tutto ciò che ti serve.

## Cos'è ReelBolt in breve {#what-reelbolt-is}

ReelBolt è un'app web per creare video con l'aiuto dell'AI. Inserisci il tuo materiale in un progetto, scegli un'app, premi **Avvia** e ReelBolt fa il lavoro e ti consegna un video finito. Un'**esecuzione** è un tentativo.

Apri ReelBolt nel browser. Tutto ciò che trovi in questa guida si trova nella dashboard, all'indirizzo `/app` (per esempio `/app/projects`).

Due parole che incontrerai: un **derush** è il primo montaggio del girato grezzo, senza i tempi morti e i ciak ripetuti. Il **B-roll** è filmato aggiuntivo mostrato sopra l'inquadratura principale.

## Cosa può creare ReelBolt per te {#what-reelbolt-can-make-for-you}

**Video dal tuo codice.** Carica il codice sorgente di un'app web o di un prodotto. ReelBolt capisce cosa fa il prodotto e che aspetto ha, scrive una sceneggiatura, pianifica le inquadrature e costruisce un video animato che lo mette in mostra, così il risultato somiglia al tuo prodotto vero.

**Video dai tuoi filmati.** Carica video grezzo, come una lunga registrazione dello schermo o una ripresa a mezzo busto. ReelBolt può:

- **Tagliarlo**: togliere le pause e i ciak ripetuti e tenere le parti buone.
- **Aggiungere titoli e didascalie**: titoli, terzi inferiori e didascalie nei momenti giusti.
- **Mettere contenuti su un green screen**: seguire lo schermo di un telefono o di un portatile mentre si muove e inserirci i tuoi contenuti.
- **Sfocare o evidenziare un oggetto**: di' "sfoca il tachimetro" e ReelBolt segue quell'oggetto lungo l'inquadratura. Vedi [Sfoca o evidenzia un oggetto](./recipes/prompted-region-effects.md).
- **Aggiungere musica ed effetti sonori**, e dare al video un look di colore coerente.
- **Aggiungere altre clip**: generare brevi stacchi, un'inquadratura di apertura o una scheda finale, entro i limiti di spesa impostati dal tuo team.
- **Aggiungere una voce narrante** dal tuo testo, oppure **doppiare** il video in un'altra lingua.

Alcune di queste funzioni richiedono un servizio AI che un amministratore deve configurare prima (per esempio il riconoscimento vocale per i sottotitoli, o la generazione video per le clip aggiuntive). Vedi [Provider di inferenza](./admin/inference-providers.md).

## A chi è rivolto ReelBolt {#who-reelbolt-is-for}

- **Fondatori, marketer e team di prodotto** che vogliono un video di lancio o di presentazione di una funzione senza assumere un animatore o un montatore per ogni versione.
- **Creator e sviluppatori** che registrano demo o tutorial e vogliono eliminare in fretta i tempi morti e ripulire il montaggio.
- **Amministratori** che gestiscono un'installazione di ReelBolt per un team: creano gli account e collegano i servizi AI usati dalle app.

Non serve saper programmare per usare ReelBolt. Le app già pronte fanno il lavoro, e l'assistente integrato può spiegarti, suggerirti e cambiare le cose al posto tuo con una semplice conversazione.

## I blocchi principali {#the-main-building-blocks}

Sono i pezzi che incontrerai ovunque in ReelBolt. Ognuno è spiegato più a fondo in [Concetti](./concepts.md).

- **Spazio di lavoro**: il contenitore che raccoglie i tuoi progetti e le persone con cui li condividi. Ne hai uno personale e puoi crearne altri per i team. Vedi [Spazi di lavoro e team](./workspaces.md).
- **Progetto**: una cartella per un singolo lavoro su un video. Contiene i tuoi file, le impostazioni delle tue app e tutti i video che producono.
- **File**: ciò che carichi (codice, filmati, musica, immagini, documenti) più ciò che ReelBolt crea lungo il percorso e i video finiti.
- **App**: un modo già pronto per creare un tipo di video, come montare i tuoi filmati o aggiungere i sottotitoli. La scegli, compili qualche impostazione e premi **Avvia**.
- **Esecuzione**: un tentativo di un'app, dall'inizio alla fine. Puoi seguirla in diretta, fermarla e riprovare.
- **Video**: ciò che produce un'esecuzione. Puoi riprodurlo, scaricarlo e condividerlo dalla scheda **Video** del progetto.
- **Assistente**: un aiutante in chat, disponibile in ogni pagina, che può rispondere alle domande e fare modifiche per te dopo la tua approvazione.

## Come è organizzata questa guida {#how-this-guide-is-organised}

- [Le basi](./basics/index.md): guide brevi e illustrate, dalla registrazione al tuo primo video.
- [Per iniziare](./getting-started.md): accedi, crea un progetto, carica i file, avvia la tua prima app e trova il video.
- [Concetti](./concepts.md): cosa significano in pratica spazi di lavoro, progetti, file, app, esecuzioni, video, cache e costi.
- [Spazi di lavoro e team](./workspaces.md): il tuo spazio di lavoro personale, gli spazi di lavoro dei team, i quattro ruoli dei membri, gli inviti e il passaggio da uno spazio all'altro.
- [I tuoi dati, le esportazioni e la chiusura dell'account](./your-data.md): scarica tutto ciò che ReelBolt conserva su di te, chiudi il tuo account e scopri cosa viene conservato dopo.
- [Sincronizzare i file da GitHub](./projects/github-repository.md): collega un progetto a un repository GitHub, scegli quali file e cartelle sincronizzare e tienili aggiornati a ogni push.
- [Sincronizzare i file da Google Drive, OneDrive o Dropbox](./projects/cloud-drive-sync.md): collega un progetto a una cartella nel cloud, scegli quali file sincronizzare e tienili aggiornati in automatico o con Sincronizza ora.
- [Copiare una cartella del tuo computer in un progetto](./projects/local-folder-sync.md): mantieni una cartella del tuo computer (anche su iCloud Drive) copiata in un progetto con l'app desktop, e cosa non succede mai.
- [L'assistente](./assistant/index.md): cosa sa fare l'assistente integrato, come funzionano le approvazioni e come chiedergli le cose.
- Avanzato, per costruire le tue app: i [template](./workflows/templates.md) integrati, i [tipi di passaggio](./workflows/step-types.md), gli [agenti](./workflows/agents.md) e le [stanze](./workflows/rooms.md), dove un gruppo di specialisti AI prende una decisione insieme.
- Piani, crediti e fatturazione: [Piani e crediti](./account/plans-and-credits.md), con [Gestire il tuo piano](./account/managing-your-plan.md), [Codici promo e crediti](./account/promo-codes-and-credits.md), [Leggere la stima](./account/reading-the-estimate.md), [Quanto costa l'assistente](./account/what-the-assistant-costs.md) e [Scegliere la risoluzione](./account/choosing-resolution.md).
- [L'editor timeline](./editor/index.md): apri un montaggio come timeline, riproducilo, taglia e sposta le clip, aggiungi marcatori, annulla e torna a versioni precedenti. L'editor video e l'[editor di immagini](./editor/thumbnails.md) sono disponibili come anteprima anticipata.
- App: guide passo passo, come [scegliere un'app](./recipes/choose-a-recipe.md), [un promo dal tuo codice](./recipes/promo-from-code.md), [derush e montaggio di filmati](./recipes/derush-edit.md), [un inserto di schermo tracciato](./recipes/tracked-screen-insert.md) e [i sottotitoli per un video](./recipes/subtitles.md).
- Amministrazione: [utenti](./admin/users.md), [provider di inferenza](./admin/inference-providers.md), [ricerca semantica](./admin/semantic-search.md) e [modelli decisionali](./admin/decision-models.md).
- [Runner desktop](./account/desktop-runner.md): collegare un computer tuo per creare i video di ReelBolt, installarlo e avviarlo, le sue impostazioni, cosa gira dove e quali dati lasciano la macchina.
- [Risoluzione dei problemi](./troubleshooting.md): messaggi di errore comuni e cosa fare.
