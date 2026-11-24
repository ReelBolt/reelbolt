---
title: Cos'è ReelBolt
sidebar_position: 1
description: ReelBolt trasforma il tuo codice e i tuoi filmati in video promozionali finiti, usando team di agenti AI che organizzi in flussi di lavoro.
i18n_source_sha: 0f9f76b2e9def92f1f0d356136aecc827bea429e
---

:::note Versione alpha

ReelBolt 0.3.0-alpha — alpha pubblica. Aspettati qualche spigolo: le cose potrebbero cambiare.

L'accesso anticipato significa che le funzioni possono cambiare, che non c'è alcuna garanzia di livello di servizio e che il tuo feedback è benvenuto.

:::

# Cos'è ReelBolt {#what-is-reelbolt}

ReelBolt è un'app web per creare video promozionali con l'aiuto di agenti AI. Inserisci il tuo materiale in un progetto, scegli o costruisci un flusso di lavoro (una ricetta fatta di passaggi), premi **Avvia** e ReelBolt esegue i passaggi uno dopo l'altro finché dall'altra parte non esce un video finito.

Apri ReelBolt nel browser. Tutto ciò che trovi in questa guida si trova nella dashboard, all'indirizzo `/app` (per esempio `/app/projects`).

## Cosa può creare ReelBolt per te {#what-reelbolt-can-make-for-you}

ReelBolt copre due grandi tipi di lavoro sul video, e puoi combinarli.

**Video promo creati da un codebase.** Carica il codice sorgente di una web app o di un prodotto. Una catena di agenti legge il codice, capisce cosa fa il prodotto e che aspetto ha (componenti, pagine, colori e font), scrive uno script, pianifica le inquadrature, poi costruisce e genera un video animato che mette in mostra il prodotto. L'animazione è scritta come codice e trasformata in un vero file video, quindi il risultato assomiglia al tuo prodotto reale e non a un template generico. Un agente revisore assegna un punteggio al risultato e può rimandarlo indietro per un altro passaggio se il punteggio è troppo basso.

**Montaggio di filmati reali.** Carica video grezzi (per esempio una lunga registrazione dello schermo o una ripresa in cui parli in camera). ReelBolt può:

- **Fare il derush e tagliare**: trovare le pause, i cambi di scena e quello che viene detto, poi decidere quali parti tenere e montarle con precisione.
- **Aggiungere motion graphics**: titoli, sottopancia e callout posizionati nei momenti giusti e nelle zone libere dell'inquadratura.
- **Inserire schermi tracciati**: se nel filmato compare un telefono o un laptop con uno schermo verde (o di un altro colore uniforme), ReelBolt può seguire quello schermo mentre si muove e inserirci i tuoi contenuti, lasciando davanti mani e bordi del dispositivo.
- **Sfocare o evidenziare un oggetto**: scrivi "sfoca il tachimetro" e ReelBolt segue quell'oggetto lungo l'inquadratura e applica l'effetto solo a quella parte dell'immagine. Vedi [Sfocare o evidenziare un oggetto](./recipes/prompted-region-effects.md).
- **Aggiungere musica di sottofondo ed effetti sonori**: scegli una traccia tra i tuoi upload, tienila più bassa sotto il parlato e aggiungi whoosh o click sui tagli.
- **Fare la color correction del montaggio**: applicare un unico look (caldo, freddo, cinematografico e così via) a tutto il video.
- **Generare b-roll**: creare brevi clip aggiuntive (stacchi, un'inquadratura di apertura, una schermata finale) con un servizio di generazione video, entro i limiti di spesa impostati dal tuo team.
- **Aggiungere una voce narrante**: trasformare il testo dello script in narrazione con un servizio di sintesi vocale.

Alcune di queste funzioni richiedono prima che un amministratore configuri il servizio AI corrispondente (per esempio il riconoscimento vocale per le trascrizioni, o un servizio di generazione video per il b-roll). Vedi [Provider di inferenza](./admin/inference-providers.md).

## A chi è rivolto ReelBolt {#who-reelbolt-is-for}

- **Fondatori, marketer e team di prodotto** che vogliono un video di lancio o di presentazione di una funzione senza assumere un animatore o un montatore per ogni versione.
- **Creator e sviluppatori** che registrano demo o tutorial e vogliono eliminare in fretta i tempi morti e ripulire il montaggio.
- **Amministratori** che gestiscono un'installazione di ReelBolt per un team: creano gli account e collegano i servizi AI usati dagli agenti.

Non serve saper programmare per usare ReelBolt. I flussi di lavoro si costruiscono a partire da template già pronti, e l'assistente integrato può spiegarti, abbozzare e modificare i flussi di lavoro con una semplice conversazione.

## I blocchi principali {#the-main-building-blocks}

Sono i pezzi che incontrerai ovunque in ReelBolt. Ognuno è spiegato più a fondo in [Concetti](./concepts.md).

- **Spazio di lavoro**: il contenitore che raccoglie i tuoi progetti e le persone con cui li condividi. Ne hai uno personale e puoi crearne altri per i team. Vedi [Spazi di lavoro e team](./workspaces.md).
- **Progetto**: una cartella per un singolo lavoro su un video. Contiene i tuoi file, i tuoi flussi di lavoro e tutti i video che questi producono.
- **File**: ciò che carichi (codice, filmati, musica, immagini, documenti) più ciò che gli agenti creano lungo il percorso e i render finali.
- **Flusso di lavoro**: un elenco ordinato di passaggi che trasforma i tuoi file in un video. Ogni nuovo progetto parte con un flusso di lavoro già pronto, chiamato **Promo video from your code v1**.
- **Passaggio**: un singolo compito di un flusso di lavoro, come "analizza il codice", "decidi quali inquadrature tenere" o "taglia il video finale".
- **Agente**: un lavoratore AI con un ruolo preciso (sceneggiatore, regista, story editor, revisore e così via). ReelBolt include tanti agenti integrati e puoi crearne di tuoi.
- **Esecuzione**: un'esecuzione di un flusso di lavoro dall'inizio alla fine. Puoi seguirla in diretta, fermarla e rilanciarla.
- **Render**: un video finito prodotto da un'esecuzione, che puoi riprodurre, scaricare e condividere dalla scheda **Render** del progetto.
- **Assistente**: un aiutante in chat, disponibile in ogni pagina, che può rispondere alle domande e fare modifiche per te dopo la tua approvazione.

## Come è organizzata questa guida {#how-this-guide-is-organised}

- [Le basi](./basics/index.md): dieci guide brevi e illustrate, dalla registrazione al tuo primo video. Parti da qui.
- [Per iniziare](./getting-started.md): accedi, crea un progetto, carica i file, avvia il tuo primo flusso di lavoro e trova il video.
- [Concetti](./concepts.md): cosa significano in pratica spazi di lavoro, progetti, file, flussi di lavoro, passaggi, agenti, esecuzioni, risultati, cache e costi.
- [Spazi di lavoro e team](./workspaces.md): il tuo spazio di lavoro personale, gli spazi di lavoro dei team, i quattro ruoli dei membri, gli inviti e il passaggio da uno spazio all'altro.
- [I tuoi dati, le esportazioni e la chiusura dell'account](./your-data.md): scarica tutto ciò che ReelBolt conserva su di te, chiudi il tuo account e scopri cosa viene conservato dopo.
- [Sincronizzare i file da GitHub](./projects/github-repository.md): collega un progetto a un repository GitHub, scegli quali file e cartelle sincronizzare e tienili aggiornati a ogni push.
- [Sincronizzare i file da Google Drive, OneDrive o Dropbox](./projects/cloud-drive-sync.md): collega un progetto a una cartella nel cloud, scegli quali file sincronizzare e tienili aggiornati in automatico o con Sincronizza ora.
- [Copiare una cartella del tuo computer in un progetto](./projects/local-folder-sync.md): mantieni una cartella del tuo computer (anche su iCloud Drive) copiata in un progetto con l'app desktop, e cosa non succede mai.
- [L'assistente](./assistant/index.md): cosa sa fare l'assistente integrato, come funzionano le approvazioni e come chiedergli le cose.
- Flussi di lavoro: i [template](./workflows/templates.md) integrati, i [tipi di passaggio](./workflows/step-types.md), gli [agenti](./workflows/agents.md) e le [stanze](./workflows/rooms.md), dove un gruppo di specialisti AI prende una decisione insieme.
- Piani, crediti e fatturazione: [Piani e crediti](./account/plans-and-credits.md), con [Gestire il tuo piano](./account/managing-your-plan.md), [Codici promo e crediti](./account/promo-codes-and-credits.md), [Leggere la stima](./account/reading-the-estimate.md), [Quanto costa l'assistente](./account/what-the-assistant-costs.md) e [Scegliere la risoluzione](./account/choosing-resolution.md).
- [L'editor timeline](./editor/index.md): apri un montaggio come timeline, riproducilo, taglia e sposta le clip, aggiungi marcatori, annulla e torna a versioni precedenti. L'editor video e l'[editor di immagini](./editor/thumbnails.md) sono disponibili come anteprima anticipata.
- Ricette: guide passo passo, come [un promo dal tuo codice](./recipes/promo-from-code.md), [derush e montaggio di filmati](./recipes/derush-edit.md), [un inserto di schermo tracciato](./recipes/tracked-screen-insert.md) e [i sottotitoli per un video](./recipes/subtitles.md).
- Amministrazione: [utenti](./admin/users.md), [provider di inferenza](./admin/inference-providers.md), [ricerca semantica](./admin/semantic-search.md) e [modelli decisionali](./admin/decision-models.md).
- [Runner desktop](./account/desktop-runner.md): collegare un computer tuo per generare i video di ReelBolt, installarlo e avviarlo, le sue impostazioni, cosa gira dove e quali dati lasciano la macchina.
- [Risoluzione dei problemi](./troubleshooting.md): messaggi di errore comuni e cosa fare.
