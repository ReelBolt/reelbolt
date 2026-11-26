---
title: Crea un video promo dal codice della tua app
sidebar_position: 2
description: Ricetta per trasformare il codice sorgente di un'app web in un video promozionale animato con le app promo di ReelBolt.
i18n_source_sha: 8f613b77be6684f1c5cdce63ab2d71d8214c816c
---

# Crea un video promo dal codice della tua app {#make-a-promo-video-from-your-apps-code}

**Obiettivo:** caricare il codice sorgente di un'app web e ottenere un video promozionale animato, con
scene basate sulle schermate reali, sui colori e sulle funzioni dell'app.

ReelBolt legge il codice con cinque agenti di analisi, trasforma l'interfaccia dell'app in scene di **animazione**
(l'animazione è lo strumento di animazione basato sul codice che ReelBolt usa per costruire e generare i video), pianifica
animazione, voce narrante e inquadrature, e poi l'`AuthorAgent` **genera da sé il video finale** all'interno
di un passaggio `Agent`. Non c'è un passaggio di render separato. Un passaggio di revisione dà un punteggio al risultato e chiede
all'`AuthorAgent` di riprovare se non è all'altezza.

## Prerequisiti per un promo dal codice {#prerequisites-for-a-promo-from-code}

- **Il codice sorgente dell'app caricato nel progetto.** Nella scheda File puoi caricare un'intera cartella o uno `.zip`
  (comprimilo senza `node_modules`; si apre da solo). Vedi [Dai il tuo codice a ReelBolt](../projects/give-your-code.md).
  Includi il codice dell'interfaccia (componenti, pagine, stili); puoi escludere i risultati della build,
  le cartelle delle dipendenze (come `node_modules`) e i file binari di grandi dimensioni.
- **Un provider di chat AI.** La pipeline del promo fa molte chiamate AI; un modello capace dà
  risultati migliori.
- **Il servizio sandbox di animazione in esecuzione** (fa parte dell'installazione standard). È dove gli
  agenti scrivono ed eseguono il codice di animazione, isolato da tutto il resto.

## Scegliere un'app promo {#choosing-a-promo-template}

| Chiave dell'app | Nome visualizzato | Con voce narrante? | Ideale per |
|---|---|---|---|
| `quick-win-promo` | Video promo dal tuo codice | No (senza audio) | Un primo promo. Già aggiunto a un progetto che parte dal codice. |
| `lean-context-promo` | Video promo dal tuo codice (più leggero) | No (senza audio) | Lo stesso, con meno token AI; adatto alle app grandi. |
| `cinematic-feature-spotlight` | Reel verticale veloce | Sì | Un reel social breve e incisivo con una forte call to action. |
| `story-arc-launch-trailer` | Trailer di lancio con voce narrante | Sì | Un trailer di lancio costruito come una storia. |

Tutti e quattro chiedono il tuo brief quando li avvii. Il brief decide **formato e durata**: una richiesta di reel,
short, storia, TikTok o verticale crea un video 1080x1920 (9:16), "quadrato" crea 1080x1080,
qualsiasi altra cosa è orizzontale 1920x1080, e una durata indicata ("15 s") è la durata del video. Le due
app con voce narrante richiedono un provider vocale.

Tutti e quattro seguono lo stesso schema. Video promo dal tuo codice:

| # | Etichetta | Tipo di passaggio | Agente | Genera il video? |
|---|---|---|---|---|
| 1 | Analizza la struttura del codice | `Agent` | `CodeStructureAnalyzer` | No |
| 2 | Analizza le dipendenze dell'interfaccia | `Agent` | `DependencyAnalyzer` | No |
| 3 | Inventaria i componenti | `Agent` | `ComponentInventoryAnalyzer` | No |
| 4 | Mappa rotte e API | `Agent` | `RouteAndApiAnalyzer` | No |
| 5 | Estrai i token di stile | `Agent` | `StyleAndThemeExtractor` | No |
| 6 | Traduci in scene di animazione | `Agent` | `RemotionComponentTranslator` | No (scrive solo codice) |
| 7 | Crea la strategia di animazione | `Agent` | `AnimationStrategyAgent` | No |
| 8 | Scrivi voce narrante e sottotitoli | `Agent` | `ScriptwriterAgent` | No |
| 9 | Costruisci la regia delle inquadrature | `Agent` | `DirectorAgent` | No |
| 10 | Assembla e genera la composizione principale | `Agent` | `AuthorAgent` | **Sì** |
| 11 | Verifica qualità e conformità | `ReviewLoop` (passaggio 8, ritorno al 10, 2 giri) | `ReviewAgent` | No |

Gli elenchi dei passaggi delle altre tre app sono in [App dei flussi di lavoro](../workflows/templates.md).

## Come avviare un promo dal codice {#how-to-run-a-promo-from-code}

1. Assicurati che il codice sia caricato.
2. Apri l'app (scheda Flussi di lavoro). Per Video promo dal tuo codice esiste già; per le
   altre, aggiungi l'app da **Nuovo flusso di lavoro**.
3. Avvialo e scrivi il tuo brief quando richiesto, per esempio: "Promuovi la nuova dashboard del team ai
   responsabili di ingegneria. Tono vivace, da 30 a 45 secondi, chiudi con 'Inizia la prova gratuita'." oppure "Reel
   verticale di 15 s sull'editor timeline." (Il riquadro del brief compare sempre quando fai clic su **Avvia**; **Richiedi un brief prima di ogni esecuzione** lo
   rende solo obbligatorio.)
4. Segui l'esecuzione. I passaggi 6 e 10 sono i più lunghi, perché gli agenti scrivono, controllano ed eseguono il codice.

## Cosa controllare dopo un promo {#what-to-check-after-a-promo-run}

- Il **passaggio 10 (`AuthorAgent`)** mostra il video finito nel suo lettore **Video finale**; compare anche
  nella scheda **Render** del progetto. Se questo passaggio non ha un video, fallisce con "Author step did
  not produce a rendered media artifact", e ReelBolt lo ripete.
- **Il passaggio di revisione** mostra il punteggio e l'elenco dei miglioramenti. Un punteggio sotto la soglia
  rimanda l'esecuzione al passaggio 10 con i commenti; dopo l'ultimo giro l'esecuzione termina con l'ultimo
  video.
- Il **passaggio 8 (`ScriptwriterAgent`)** contiene voce narrante e sottotitoli per ogni scena. Il testo a schermo
  è semplice: qualsiasi markdown come `*word*` viene rimosso automaticamente, perché il video
  disegnerebbe gli asterischi. La voce narrante parlata richiede un passaggio `Voiceover` (sotto); l'output dell'Author indica
  in `metadata.audio` se il video ha la voce narrante o è senza audio.
- **Niente di inventato:** nomi, citazioni, valutazioni e numeri a schermo vengono solo dal tuo codice,
  dai tuoi file o dal tuo brief. Se vedi un nome o una cifra inventati, scrivilo nel brief e avvia di nuovo.

## Varianti di un promo dal codice {#variations-of-a-promo-from-code}

- **Voce narrante parlata:** aggiungi un passaggio `Voiceover` dopo lo sceneggiatore con
  `{"source": {"kind": "ScriptScenes", "scriptwriterStepOrder": 8}, "defaultVoiceId": "..."}`; l'
  `AuthorAgent` colloca ogni battuta nella sua scena. Richiede un provider vocale Fish Audio. Vedi
  [Ricetta voce narrante](voiceover.md).
- **Analisi più rapida ed economica:** unisci i cinque agenti di analisi in un unico passaggio `Parallel` così
  funzionano contemporaneamente.
- **Meno token:** usa Video promo dal tuo codice (più leggero), che riduce l'elenco dei componenti con un passaggio `Extract` e
  dà a ogni agente successivo solo i risultati precedenti che gli servono.
- **Soglia di qualità più severa:** alza `minScore` del passaggio di revisione (per esempio a 9) e
  `maxIterations` (per esempio a 3). Ogni giro in più genera di nuovo il video.

## Insidie di un promo dal codice {#pitfalls-with-a-promo-from-code}

- **Codice assente o scarso:** gli agenti di analisi non hanno nulla da descrivere e il promo sarà
  generico. Carica il sorgente dell'interfaccia.
- **Repository molto grandi:** più lenti e più costosi. Escludi i file generati e le cartelle delle dipendenze,
  oppure usa Video promo dal tuo codice (più leggero).
- **Un passaggio va in timeout:** gli agenti che scrivono codice possono raggiungere il limite di tempo con backend AI lenti. Un
  passaggio andato in timeout non viene ripetuto automaticamente; un operatore può alzare il limite di tempo di quell'agente. Poi
  usa **Riprova**: i passaggi di sola lettura precedenti possono essere riutilizzati dalla cache.
- **Le riesecuzioni ripetono l'analisi in fretta:** gli agenti di analisi che si limitano a leggere vengono riutilizzati da un'esecuzione
  identica precedente; traduzione, sceneggiatura, regia, authoring e revisione girano sempre da zero.
- **Chiedere all'agente sbagliato di generare il video:** solo l'`AuthorAgent` genera il promo. Il
  `RemotionComponentTranslator` costruisce e controlla il codice delle scene ma non può generare il video.

## Ricetta: Video promo dal tuo codice {#recipe-promo-video-from-your-code}

App: **Video promo dal tuo codice**. Viene aggiunto a un nuovo progetto quando dici che il video parte da un'app o da un sito che hai creato.

**Da impostare prima di avviare.** Lingua, durata (sono proposti 15, 30 o 60 secondi), formato, stile e una
nota su chi è il destinatario del promo e cosa deve mostrare. Lo stile suggerito è un reel social veloce.
Non c'è un'impostazione della voce narrante: questa app è senza audio. Per aggiungere una voce, vedi [Aggiungi una voce narrante](voiceover.md).
Il significato delle impostazioni è in [Impostazioni di esecuzione](run-settings.md).

**Quanto costa.** Usa crediti, perché diversi agenti AI leggono il tuo codice e costruiscono il video.
Non sono inclusi né voce né inquadrature generate dall'AI. Il prezzo compare nella pagina dell'esecuzione prima che tu avvii.

**Cosa ottieni.** Un video promo delle schermate reali della tua app, con testo a schermo e sottotitoli
scritti per te.

**Problemi comuni.**

- Il promo è generico: è stato caricato poco o nessun codice. Carica il sorgente dell'interfaccia e avvia di nuovo.
- L'esecuzione si ferma a un passaggio che scrive codice: usa **Riprova**; i passaggi di sola lettura precedenti vengono riutilizzati.
- Il video ha il formato o la durata sbagliati: imposta **Formato** e **Durata** nella pagina dell'esecuzione invece di
  chiederli nelle note.

## Ricetta: Video promo dal tuo codice (più leggero) {#recipe-promo-video-from-your-code-lighter}

App: **Video promo dal tuo codice (più leggero)**. Lo stesso promo, ma ogni passaggio successivo legge meno
dell'analisi del tuo codice.

**Da impostare prima di avviare.** Le stesse impostazioni del promo standard: lingua, durata, formato, stile
e note. È senza audio.

**Quanto costa.** Usa crediti, meno del promo standard su un'app grande. Su un'app piccola
la differenza è minima. La pagina dell'esecuzione mostra il prezzo.

**Cosa ottieni.** Un video promo delle schermate reali della tua app.

**Problemi comuni.**

- Il video lascia fuori una funzione che ti sta a cuore: con meno contesto per passaggio, nomina quella funzione nelle
  note, oppure avvia il promo standard.
- Archivio molto grande: escludi i file generati e le cartelle delle dipendenze prima di caricare.
