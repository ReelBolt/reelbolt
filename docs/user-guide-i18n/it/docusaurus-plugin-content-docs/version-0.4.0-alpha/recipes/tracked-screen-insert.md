---
title: Metti un'animazione su un green screen (inserimento su schermo tracciato)
sidebar_position: 12
description: Ricetta passo dopo passo per riempire lo schermo green screen di un telefono o di un portatile nel tuo girato con un'animazione creata da ReelBolt, che segue lo schermo mentre si muove.
i18n_source_sha: 9c7a392d76f6fe28666dd44348816b8fa3ddca1a
---

# Metti un'animazione su un green screen (inserimento su schermo tracciato) {#put-an-animation-on-a-green-screen-tracked-screen-insert}

:::note
Questa ricetta ora si apre come parte di [Effetti su una clip](effects-on-a-clip.md): scegli lì il campo corrispondente. Questa pagina resta come riferimento su ciò che fa ogni passaggio.
:::

Un **inserimento su schermo tracciato** riempie uno schermo colorato nel tuo girato (tipicamente un telefono o un portatile
che mostra uno schermo verde uniforme mentre qualcuno lo tiene in mano o lo usa) con un'animazione, in modo che
l'animazione si muova, ruoti e si inclini con il dispositivo come se fosse davvero sullo schermo. Gli angoli
arrotondati, il notch della camera e una mano che passa davanti allo schermo vengono mantenuti.

**Sì, ReelBolt può farlo.** Crea l'animazione e la fissa per te sullo schermo tracciato.

:::note Come funziona
Non c'è un passaggio di "render" separato: l'AI di motion graphics (`MotionGraphicsPlanner`, o il direttore di un
gruppo `GraphicsRoom`) crea l'animazione, la genera e la nomina nella sua risposta. Il passaggio
`VideoCompile` la fissa poi sullo schermo tracciato.
:::

## Metti sullo schermo uno dei tuoi video (non serve l'AI) {#put-one-of-your-own-videos-on-the-screen-no-ai-needed}

Quando hai già il clip che deve essere riprodotto sullo schermo (un promo, una registrazione dello schermo, un'
immagine) non ti serve alcun passaggio AI. Carica entrambi i video nel progetto e costruisci un
flusso di lavoro in due passaggi:

1. **`VideoAnalyze`** con il girato su green screen come origine e **Rileva le aree di inserimento su schermo
   tracciato** attivo. Scegli qui solo il girato su green screen, **non** il clip
   che vuoi sullo schermo. Ogni origine di questo passaggio è girato per il montaggio; un clip elencato qui
   finisce nel video finito come una scena a sé.
2. **`VideoCompile`** con **Mantieni intere tutte le origini (nessun montaggio)** attivo, **Attiva gli inserimenti su
   schermo tracciato** attivo e il tuo clip scelto sotto **Contenuto da inserire (il tuo file)**.

In JSON, il passaggio 2 si presenta così:

```json
{
  "version": 1,
  "decision": {"from": "Previous"},
  "analysisStepOrder": 1,
  "keepWholeSources": true,
  "enableInserts": true,
  "insertContentProjectFileId": "PUT-YOUR-CLIP-FILE-ID-HERE",
  "insertFit": "Auto"
}
```

- `keepWholeSources` mantiene il girato intero, quindi non serve alcun agente editor (`decision` viene allora
  ignorato).
- `insertContentProjectFileId` è il clip mostrato sullo schermo. Deve essere un video o un'immagine di questo
  progetto.
- `insertFit` decide cosa succede quando la forma del clip è diversa da quella dello schermo: `"Auto"`
  (predefinito) mantiene la forma del tuo clip e aggiunge bande nere quando è molto diversa (un video orizzontale
  su un telefono in verticale), `"Cover"` ritaglia per riempire lo schermo, `"Stretch"` lo deforma per adattarlo.

Il tracker si adatta a ogni video: prova diverse impostazioni sul tuo girato e tiene quella
il cui contorno si posa esattamente sul bordo dello schermo da un fotogramma all'altro. Apri il risultato nell'
**editor** per vedere lo schermo tracciato disegnato sopra il video, la sua affidabilità, se è stato ritagliato sulla
forma esatta dello schermo ("matted") e quali impostazioni ha scelto il tracker.

## Il template pronto, oppure chiedi all'assistente {#the-ready-made-template-or-ask-the-assistant}

I due passaggi qui sopra sono anche un template, **Inserimento su green screen** (`green-screen-insert`; vedi
[Template dei flussi di lavoro](../workflows/templates.md)). Non usa nessun modello AI; viene addebitato solo il tempo di elaborazione. Il modo più rapido
per usarlo è chiedere all'assistente a parole, nell'app o da Claude Code:

> Put the screen recording `app-demo.mp4` onto the green screen in `cat-and-phone.mp4`.

L'assistente controlla entrambi i file, ti mostra cosa sta per salvare e crea l'app (o
aggiorna quello con lo stesso nome) con i file giusti già scelti. Non avvia l'esecuzione:
chiedigli di avviare l'app quando sei pronto. Per mostrare un video di un'esecuzione precedente invece di un file
caricato, diglielo: usa il risultato di quell'esecuzione (deve essere un'esecuzione dello stesso progetto).

Per il JSON, il passaggio 1 è `{"version":1,"source":{"kind":"ProjectFile","projectFileId":"THE-GREEN-SCREEN-CLIP-ID"},"detectInsertRegions":true,"transcription":"Off","vision":"Off"}`
e il passaggio 2 è il JSON qui sopra con `insertContentRef` al posto di `insertContentProjectFileId`:
`"insertContentRef": {"from": "ProjectFile", "projectFileId": "THE-CONTENT-FILE-ID"}`
(`{"from": "StepResult", "stepResultId": "…"}` indica un render di un'esecuzione precedente).

## Video diversi su schermi diversi {#different-videos-on-different-screens}

Un montaggio può mostrare file diversi su schermi diversi, per esempio una registrazione da telefono sui
telefoni e una registrazione da desktop sul portatile. Nel passaggio di compilazione, sotto **Inserimenti su schermo**, fai clic su
**Aggiungi contenuto per altri schermi** e, per ogni file in più, indica per quali schermi è:

- **Solo sugli schermi nella clip numero**: la posizione del clip nel primo passaggio (1 = il primo clip).
- **Solo sullo schermo**: l'id di un solo schermo (come `r2`), mostrato nel risultato del primo passaggio.
- Lascia vuoti entrambi per usare il file su ogni schermo non coperto altrimenti.

Uno schermo che non corrisponde a nessun file in più mostra il file scelto sotto **Contenuto da inserire (il tuo file)**. In
JSON è `"insertContents": [{"projectFileId": "…", "sourceIndex": 1, "fit": "Contain"}]`
(`sourceIndex` conta da 0). Ogni file in più può avere un proprio adattamento.

## Metti sugli schermi il video di un passaggio precedente {#put-an-earlier-steps-video-on-the-screens}

Per mostrare qualcosa che il flusso di lavoro ha creato da sé (per esempio un'animazione che un agente Author ha generato
prima nell'esecuzione, o l'output di un'esecuzione precedente), aggiungi un passaggio **Metti sugli schermi** dopo il passaggio di
analisi e il passaggio che crea il contenuto. Punta **Schermi trovati da** al passaggio di analisi,
scegli il contenuto sotto **Contenuto**, e nel passaggio di compilazione imposta il piano grafico sul passaggio **Metti sugli
schermi** con gli inserimenti su schermo attivi. Nella scelta di cosa va dove non interviene nessuna AI. Vedi
[Passaggio Metti sugli schermi](../workflows/step-types.md#put-on-screens-step-pininsert).

## Quando la tua registrazione dello schermo sfuma al nero {#when-your-screen-recording-fades-to-black}

Le registrazioni dello schermo spesso diventano nere per un momento tra una schermata e l'altra. Su un telefono ripreso sembra che
il telefono si sia spento. ReelBolt trova questi momenti neri nel tuo file e, per impostazione predefinita, continua a
mostrare l'ultima immagine finché la registrazione riprende, così lo schermo non diventa mai scuro e
i tempi della registrazione restano gli stessi. Sotto **Quando il tuo video sfuma al nero** puoi invece scegliere di
**Salta la parte nera** (la registrazione diventa un po' più corta) o **Lascia com'è**
(`"insertContentDips": "Freeze"`, `"Cut"` o `"Keep"`). Il risultato del passaggio di compilazione elenca i momenti
neri trovati in `inserts.appliedInserts[].contentDips`. I video creati dall'AI per lo
schermo, e le immagini, non vengono mai modificati.

## Come funziona un inserimento su schermo tracciato, in un quadro {#how-a-tracked-screen-insert-works-in-one-picture}

Un inserimento su schermo tracciato richiede quattro passaggi, ciascuno con un compito:

1. **`VideoAnalyze`** trova lo schermo colorato e ne segue i quattro angoli in ogni fotogramma
   (`"detectInsertRegions": true`). Ogni schermo che segue viene offerto all'AI con un breve id:
   `r0`, `r1`, ... con suggerimenti semplici (quanto è affidabile, quanto è grande, in movimento o fermo, la sua forma, piatto o
   curvo).
2. **`VideoStoryEditor`** (un passaggio `Agent`) decide quali parti del girato tenere. Il passaggio di compilazione
   non può essere eseguito senza questa decisione. Per tenere l'intero clip, scrivilo nel testo della richiesta.
3. **`MotionGraphicsPlanner`** (un passaggio `Agent`), oppure un passaggio **`GraphicsRoom`**, sceglie quale
   schermo offerto riempire, **crea un'animazione con la forma di quello schermo e la genera**, e
   la restituisce nell'elenco `inserts` della sua risposta: `regionId` (per esempio `r0`), il file dell'animazione generata
   (salvato dall'agente stesso) e `reason`.
4. **`VideoCompile`** legge quella risposta tramite l'impostazione `graphicsPlan` e, con
   `"enableInserts": true` e `"mode": "Reencode"`, deforma l'animazione sullo schermo in movimento
   fotogramma per fotogramma e la ritaglia sulla forma esatta dello schermo.

La parte che dice **cosa riempie lo schermo** è la voce nell'elenco `inserts` del piano, prodotta dal
passaggio 3: un'animazione che l'agente ha creato e generato oppure, quando la tua richiesta nomina uno dei tuoi
file di progetto, quel file. Per saltare del tutto l'AI, vedi [Metti sullo schermo uno dei tuoi video](#put-one-of-your-own-videos-on-the-screen-no-ai-needed).

## Prerequisiti per un inserimento su schermo tracciato {#prerequisites-for-a-tracked-screen-insert}

Prima di costruire un flusso di lavoro di inserimento su schermo tracciato, ti serve:

- **Girato con uno schermo uniforme e di un solo colore**, caricato nel progetto. Il verde funziona meglio;
  sono supportati anche blu e magenta. Lo schermo dovrebbe occupare almeno una piccola parte dell'inquadratura ed essere
  visibile per almeno 1 secondo. Un vero telefono o portatile che mostra un'immagine verde a tutto schermo è l'ideale.
- **Un provider di chat AI funzionante** (per l'editor e per l'AI di motion graphics). L'AI di motion graphics
  pianifica e crea ogni animazione, quindi un modello capace aiuta.
- Facoltativo: un provider di trascrizione, se il girato ha del parlato che il montaggio deve seguire.

**Questa ricetta usa crediti.** Avviarla viene addebitato come qualsiasi esecuzione di un flusso di lavoro: ogni passaggio viene addebitato per il
lavoro che svolge, e la riga del costo nella pagina dell'esecuzione mostra il costo previsto prima che tu avvii. Il lavoro
svolto tramite un provider che il tuo spazio di lavoro ha collegato da sé costa 0 crediti. Vedi
[Leggere la stima](../account/reading-the-estimate.md) e [Piani e crediti](../account/plans-and-credits.md).

## Costruisci il flusso di lavoro di inserimento su schermo tracciato {#build-the-tracked-screen-insert-workflow}

Il modo più rapido è partire dal template **Montaggio del girato con titoli e callout**
(`video-derush-edit-graphics`), che ha già i quattro passaggi nell'ordine giusto più un passaggio di
revisione, e cambiare tre impostazioni. Puoi anche chiedere all'assistente: "Set up a tracked screen insert
workflow for my green-screen phone video".

Il flusso di lavoro completo, passaggio per passaggio:

| # | Etichetta | Tipo di passaggio | Agente | Contesto |
|---|---|---|---|---|
| 1 | Analizza il video sorgente | `VideoAnalyze` | `VideoTransform` | — |
| 2 | Decidi quali tratti tenere | `Agent` | `VideoStoryEditor` | `PreviousStepOnly` |
| 3 | Pianifica grafiche e inserimenti su schermo | `Agent` | `MotionGraphicsPlanner` | `FullWorkflow` |
| 4 | Compila il video montato con l'inserimento | `VideoCompile` | `VideoTransform` | — |
| 5 (facoltativo) | Verifica la qualità del montaggio | `ReviewLoop` | `VideoReviewAgent` | `FullWorkflow`, ritorno al passaggio 2, `minScore` 8, `maxIterations` 3 |

Imposta i passaggi 2, 3 e 5 su `"cacheMode": "Never"` così a ogni esecuzione si prendono decisioni nuove (il passaggio 3 non viene mai
riutilizzato comunque, perché genera).

**Impostazioni del passaggio 1** (`videoAnalyzeConfigJson`): scegli il tuo video caricato come origine:

```json
{
  "version": 1,
  "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
  "detectInsertRegions": true,
  "insertRegionColor": "green",
  "maxInsertPlatesPerFrame": 1,
  "measureInsertCurvature": true,
  "solveInsertChromaKey": true
}
```

- `detectInsertRegions` è l'interruttore che conta; è disattivato per impostazione predefinita.
- `insertRegionColor`: `"green"` (predefinito), `"blue"` o `"magenta"`.
- `maxInsertPlatesPerFrame`: alzalo a 2 o più quando due schermi compaiono nello stesso momento.
- `measureInsertCurvature` e `solveInsertChromaKey` sono già attivi per impostazione predefinita; rendono gli
  angoli precisi, gestiscono gli schermi curvi e mantengono angoli arrotondati, notch e mani davanti.
- Aggiungi `"emitOverlayPlacements": true` solo se vuoi anche titoli e sottopancia.

**Impostazioni del passaggio 4** (`videoCompileConfigJson`):

```json
{
  "version": 1,
  "decision": {"from": "Step", "stepOrder": 2},
  "analysisStepOrder": 1,
  "mode": "Reencode",
  "graphicsPlan": {"from": "Step", "stepOrder": 3},
  "enableInserts": true,
  "insertSurface": "Auto",
  "enableInsertMatte": true,
  "transitionPolicy": "Off"
}
```

- `graphicsPlan` **deve** puntare al passaggio del pianificatore (o della stanza grafica). Gli inserimenti vengono letti da quello
  stesso piano; senza, non viene inserito nulla.
- `enableInserts` attiva la funzione. `enableGraphics` **non** serve per gli inserimenti; aggiungi
  `"enableGraphics": true` solo se vuoi anche i titoli e i sottopancia del pianificatore.
- `mode` deve essere `"Reencode"` (il valore predefinito). `"StreamCopy"` fa fallire il passaggio con
  `INSERTS_REQUIRE_REENCODE`.
- `insertSurface`: `"Auto"` (predefinito) sceglie una deformazione piatta o curva in base a ciò che è stato misurato;
  `"Planar"` sempre piatta; `"Mesh"` sempre curva (solo per prove).
- `enableInsertMatte` (predefinito `true`) ritaglia l'animazione sulla forma esatta dello schermo.
- `"transitionPolicy": "Off"` mantiene i tagli netti. Il template usa `"Auto"`, che può aggiungere dissolvenze
  incrociate tra i tagli; un inserimento viene tolto da qualsiasi dissolvenza, quindi con `"Auto"` l'inserimento può iniziare
  in ritardo o sparire vicino a un taglio.

Se parti dal template: nel passaggio 1 aggiungi `"detectInsertRegions": true` (e mantieni
`"emitOverlayPlacements": true` solo se vuoi anche i titoli); nel passaggio 4 aggiungi `"enableInserts": true`,
se vuoi rimuovi `"enableGraphics": true`, e valuta `"transitionPolicy": "Off"`. Il suo passaggio 4
ha già `"graphicsPlan": {"from": "Step", "stepOrder": 3}`.

## Di' all'AI cosa mostrare sullo schermo tracciato {#tell-the-ai-what-to-show-on-the-tracked-screen}

L'AI di motion graphics decide cosa mostra lo schermo, quindi diglielo. Quando fai clic su **Avvia**, scrivi un
brief come:

> Keep the whole clip. In the phone screen (r0), show an animated mockup of our app's home screen:
> dark background, our logo fading in at the top, then three cards sliding up. Brand colour #6C5CE7.
> No titles or lower-thirds.

Buone richieste:

- Di' di **tenere l'intero clip** (o cosa tagliare). Questa istruzione è per l'editor nel passaggio 2.
- Descrivi il **contenuto** dello schermo: cosa compare, colori, testo, movimento.
- Di' se vuoi anche titoli o sottopancia.
- Cita lo schermo con il suo ruolo ("lo schermo del telefono") se potrebbero essercene più di uno.

Cosa ne fa l'AI: crea una piccola animazione in una sandbox, con le proporzioni misurate dello schermo (per esempio
un'animazione verticale alta per un telefono tenuto dritto), la genera come un normale video pieno e mette il
risultato nell'elenco `inserts` della sua risposta. Può anche leggere i file del tuo progetto, quindi un brief caricato nel progetto può essere citato per nome.

## Avvia il flusso di lavoro di inserimento su schermo tracciato e controlla il risultato {#run-the-tracked-screen-insert-workflow-and-check-the-result}

Avvia il flusso di lavoro e seguilo nella pagina dell'esecuzione. Cosa controllare, passaggio per passaggio:

- **Passaggio 1**: i messaggi di avanzamento dicono "Tracking insert regions" e, per ogni schermo trovato, qualcosa
  come "Insert region r0: flat, silhouette key ... (agreement 92%)". Nel risultato del passaggio, il conteggio
  `meta.insertTracking.regions` dovrebbe essere almeno 1 e `meta.offeredInsertRegionIdCount`
  dovrebbe essere lo stesso numero. L'elenco `view.insertRegions` mostra per ogni schermo `conf`
  (`high`/`medium`/`low`), `size`, `motion`, `aspect` e `surface` (`Flat`/`Curved`).
- **Passaggio 2**: l'elenco `keep` dell'editor dovrebbe coprire la parte del clip in cui lo schermo è
  visibile.
- **Passaggio 3**: l'elenco `inserts` del risultato dovrebbe contenere una voce con `regionId` e un file di animazione
  generata. Il lettore **Video finale** del passaggio mostra l'animazione generata dall'agente.
  Questo passaggio è lento: scrivere, controllare e generare l'animazione può richiedere molti minuti.
- **Passaggio 4**: la sezione `inserts` del risultato riporta `appliedInsertCount` e, per ogni inserimento,
  se era piatto o curvo (`surface`, dimensione della mesh) e `matted`. Tutto ciò che è stato saltato è elencato in
  `droppedInserts` con un motivo. Guarda il **Video finale**.

## Perché un inserimento su schermo tracciato non è stato applicato {#why-a-tracked-screen-insert-was-not-applied}

Quando lo schermo mostra ancora il verde uniforme nel video finale, il risultato del passaggio `VideoCompile` dice
perché. I motivi, in parole semplici:

| Motivo mostrato | Cosa significa | Cosa fare |
|---|---|---|
| "No GraphicsPlan configured" | Il passaggio 4 non ha né `graphicsPlan` né `insertContentProjectFileId`. | Imposta `"graphicsPlan": {"from": "Step", "stepOrder": 3}`, oppure scegli un tuo file come contenuto da inserire. |
| `content_not_in_project` / `content_not_video_or_image` | Il contenuto da inserire scelto non è un video o un'immagine di questo progetto. | Scegli un video o un'immagine caricati in questo progetto. |
| `inserts` vuoto, nessuna voce scartata | Il pianificatore non ha restituito inserimenti (nessuno schermo offerto, o non ne ha scelto nessuno). | Controlla che il passaggio 1 abbia trovato uno schermo; chiedi l'inserimento in modo esplicito nella richiesta. |
| `unknown_region_id` | Il pianificatore ha nominato l'id di uno schermo che il passaggio 1 non ha offerto in questa esecuzione. | Controlla `offeredInsertRegionIdCount` del passaggio 1; vedi la sezione successiva sulle viste grandi. |
| `confidence_below_threshold` | Lo schermo è stato tracciato con bassa affidabilità (sotto `minInsertConfidence`, predefinito 0,5). | Usa girato con uno schermo più uniforme e meglio illuminato, oppure abbassa `minInsertConfidence` con cautela. |
| `invalid_asset_storage_key` | L'animazione del pianificatore manca, è inventata o proviene da un'altra esecuzione. | Avvia di nuovo; l'animazione deve essere generata nella stessa esecuzione. |
| `asset_download_or_probe_failed` | Non è stato possibile leggere l'animazione generata. | Avvia di nuovo. |
| `cut_away` | La parte del clip con lo schermo è stata tagliata dal montaggio. | Di' all'editor di tenere quella parte. |
| `window_consumed_by_transition_overlap` | Una dissolvenza ha coperto l'intero inserimento. | Usa `"transitionPolicy": "Off"`. |
| `max_inserts_exceeded` | Più inserimenti di `maxInserts` (predefinito 3). | Alza `maxInserts`. |
| `duplicate_region_id` | Lo stesso schermo è stato scelto due volte. | Viene usato solo il primo; non c'è nulla da fare. |
| `unavailable: true` | Agli strumenti video sul server manca una funzione necessaria. | Chiedi al tuo amministratore. |

Quando un **Ciclo di revisione** controlla una compilazione a cui erano stati richiesti inserimenti su schermo e non ne è stato applicato nessuno, il
punteggio di revisione è limitato a 6, quindi il ciclo torna indietro per un altro tentativo. Il feedback nomina ogni schermo
lasciato fuori e il motivo preso dalla tabella qui sopra.

## Insidie degli inserimenti su schermo tracciato {#pitfalls-with-tracked-screen-inserts}

Motivi comuni per cui un inserimento su schermo tracciato va male:

- **L'origine del passaggio 1 è vuota.** I template lasciano il file non selezionato; l'esecuzione fallisce con
  `SOURCE_UNRESOLVED` finché non scegli il video.
- **L'elenco degli schermi è stato escluso perché la descrizione era troppo grande.** Il passaggio 1 mantiene la
  descrizione che passa all'AI entro `maxOutputChars` (24000 caratteri). Su girato lungo o ricco
  può escludere l'intero elenco `insertRegions` per rientrare nel limite; allora il passaggio 3 non vede nessuno schermo. Controlla
  che `meta.offeredInsertRegionIdCount` sia sopra 0. Se è 0 mentre `meta.insertTracking.regions`
  è sopra 0, alza `maxOutputChars` (per esempio a 40000) oppure imposta `"visualDetail": "None"` nel
  passaggio 1.
- **L'editor ha tagliato lo schermo.** Chiedigli di tenere l'intero clip, o la parte con lo schermo.
- **Nessun brief.** Senza un brief nella finestra di conferma dell'esecuzione, il pianificatore tira a indovinare cosa
  mostrare (o non mostra nulla). Descrivi il contenuto.
- **Schermi non uniformi o riflettenti.** Un forte riflesso, un verde molto scuro o oggetti verdi altrove nell'
  inquadratura abbassano l'affidabilità del tracciamento. Uno schermo più luminoso e illuminato in modo uniforme si traccia meglio.
- **Correzione colore e maschera insieme.** Il colore esatto dello schermo viene misurato sul girato originale,
  ma una correzione colore (`enableColorGrade`) viene applicata prima dell'inserimento. Una correzione forte può
  spostare il colore dello schermo così la sagoma ritagliata risulta meno esatta. Lascia disattivata la correzione colore per le
  inquadrature con inserimento, oppure usa un aspetto leggero.
- **Un inserimento per ogni tratto di schermo.** Se il montaggio taglia la comparsa dello schermo in due pezzi separati,
  l'inserimento compare solo nel primo pezzo.
- **Cose davanti allo schermo.** Una mano, un braccio o un animale domestico possono coprire un angolo dello
  schermo, stare lungo un suo intero bordo o attraversarlo: lo schermo resta al suo posto e l'animazione resta dietro
  a ciò che sta davanti. Perché funzioni, tre dei quattro bordi dello schermo devono essere visibili per buona parte
  dell'inquadratura. Uno schermo tagliato dal bordo dell'immagine, o visto quasi di taglio, non è protetto in questo
  modo, e lì un angolo coperto può ancora trascinare con sé l'animazione.
- **Cosa non viene gestito:** rilluminare la scena per adattarla alla stanza, la sfocatura di movimento sui movimenti veloci e
  gli schermi che non sono di un solo colore (uno schermo che mostra contenuti reali non può essere tracciato).

## Varianti del flusso di lavoro di inserimento su schermo tracciato {#variations-of-the-tracked-screen-insert-workflow}

Modi per adattare un flusso di lavoro di inserimento su schermo tracciato:

- **Un gruppo di artisti al posto di un pianificatore.** Sostituisci il passaggio 3 con un passaggio `GraphicsRoom`:
  `{"version": 1, "view": {"from": "Step", "stepOrder": 1}}`. Il suo posto `InsertArtist` discute di
  quali schermi riempire e cosa mostrare; il direttore genera le animazioni nella sua chiamata finale. Il `graphicsPlan`
  del passaggio 4 punta allora al passaggio della stanza. Punta il `view` della stanza al passaggio 1, non a
  `Previous`.
- **Anche i titoli.** Aggiungi `"emitOverlayPlacements": true` al passaggio 1 e `"enableGraphics": true` al
  passaggio 4. Lo stesso piano contiene sia i titoli (`overlays`) sia gli inserimenti su schermo (`inserts`).
- **Due schermi insieme.** Imposta `"maxInsertPlatesPerFrame": 2` nel passaggio 1; ogni schermo ha un proprio
  id (`r0`, `r1`) e una propria animazione.
- **Schermo blu o magenta.** Imposta `"insertRegionColor": "blue"` oppure `"magenta"`.
- **Più clip.** `VideoAnalyze` può analizzare più video insieme (elenco `sources`); gli inserimenti
  funzionano quando il montaggio combina più clip.

## Controllare solo il tracciamento del green screen (nessun inserimento) {#checking-green-screen-tracking-only-no-insert}

Per vedere se ReelBolt riesce a tracciare il tuo schermo prima di costruire l'intero flusso di lavoro, crea un flusso di lavoro
con il solo passaggio `VideoAnalyze` descritto sopra e avvialo. Il passaggio non usa nessuna chat AI; il riconoscimento vocale
gira comunque se esiste un provider di trascrizione (aggiungi `"transcription": "Off"` per saltarlo). Nel
risultato del passaggio:

- I messaggi di avanzamento elencano ogni schermo trovato, se risulta piatto o curvo e quanto bene è stato trovato il suo
  colore esatto (percentuale di "agreement").
- `meta.insertTracking` mostra se il tracciamento è stato eseguito (`applied`), se ha incontrato un problema
  (`degraded`) e quanti schermi sono stati trovati (`regions`).
- `view.insertRegions` mostra per ogni schermo id, affidabilità, dimensione, movimento, forma (`aspect`) e
  superficie.
- Il tracciamento completo angolo per angolo è nel record dettagliato del passaggio (vista dati).

Per vedere un composito senza alcuna AI, aggiungi il `VideoCompile` in due passaggi da [Metti sullo schermo uno dei tuoi
video](#put-one-of-your-own-videos-on-the-screen-no-ai-needed) con un qualsiasi clip caricato come contenuto da inserire. Per la risoluzione dei problemi, `insertSurface`
`"Planar"` oppure `"Mesh"` nel passaggio di compilazione forza la deformazione piatta o curva, e
`"enableInsertMatte": false` disattiva il ritaglio sulla forma esatta.

## Ricetta: Inserimento su green screen {#recipe-green-screen-insert}

Template: **Inserimento su green screen**. Trova il green screen nel tuo clip e ci mette sopra una registrazione dello schermo,
un video o un'immagine, seguendo lo schermo mentre si muove, anche quando una mano passa
davanti.

**Da impostare prima di avviare.** Nella pagina dell'app, scegli il clip che ha il green screen e, nel
secondo riquadro ("Cosa va sullo schermo"), l'immagine o il video da collocarci sopra. Il secondo riquadro
parte vuoto, così lo scegli tu. Non serve altro: non ci sono lingua, durata, stile o
voce narrante da impostare. Il significato delle impostazioni per le altre ricette è in [Impostazioni di esecuzione](run-settings.md).

**Quanto costa.** Non viene usato nessun modello AI e non vengono spesi crediti per un modello. Paghi
solo il tempo di elaborazione. La pagina dell'esecuzione mostra l'importo.

**Cosa ottieni.** Il tuo clip con il contenuto fissato allo schermo, che si muove e ruota con esso.

### Quale clip funziona {#which-clip-works}

La ricetta cerca uno schermo o un pannello di un solo colore uniforme, come verde o blu. Deve
restare in vista per almeno un secondo ed essere quasi del tutto libero. Una mano che passa davanti non è un problema.

Una clip che funziona: qualcuno tiene in mano un telefono o un portatile con tutto lo schermo verde, in
buona luce, senza riflessi sopra. Una clip che non funziona: uno schermo con un'immagine movimentata,
una parete verde che non è uno schermo, oppure uno schermo quasi tutto nascosto o in ombra.

Se non viene trovato nessuno schermo, l'esecuzione si ferma, non cambia nulla e puoi riprovare con un'altra clip.

Gli schermi piccoli nell'inquadratura, tenuti in mano e molto mossi, o coperti da segni o da un motivo per
gran parte della clip sono i più difficili da seguire. Se la clip non si riesce a seguire, chiedi all'assistente
di mettere il tuo video in un angolo o a schermo intero, cosa che non dipende dal trovare uno schermo.

Se l'esecuzione dice che la scansione della tua clip non è finita, è un problema nostro e non dice nulla
sulla tua clip: riavviala. Solo il passaggio di scansione può essere addebitato; il passaggio che mette il tuo
video non addebita nulla se non riesce, e la pagina dell'esecuzione mostra quanto è stato addebitato.

**Problemi comuni.**

- L'esecuzione si ferma e dice di non aver trovato uno schermo: il clip non ha un green screen chiaro, o lo
  schermo è per lo più nascosto. Usa un clip in cui lo schermo è ben visibile e illuminato in modo
  uniforme. L'esecuzione non finisce mai lasciando il tuo clip invariato e dichiarandolo un successo.
- L'esecuzione si ferma e dice che non è stato scelto nulla per lo schermo: scegli l'immagine o il video in
  "Cosa va sullo schermo" e avviala di nuovo.
- La registrazione sfuma al nero alla fine: vedi la sezione dedicata in questa pagina.

Gli schermi piccoli nell'inquadratura, tenuti in mano e molto in movimento, o coperti da marcatori o da un motivo
per la maggior parte del clip sono i più difficili da seguire. Se il clip non si riesce a seguire, chiedi
all'assistente di mettere invece il tuo video in un angolo o a schermo intero, cosa che non dipende dal trovare uno schermo.

Se l'esecuzione dice che la scansione del tuo clip non è terminata, è un problema nostro e non dice nulla sul tuo
clip: avviala di nuovo. Il passaggio di scansione è l'unico che può essere addebitato; il passaggio che mette il tuo
video non addebita nulla quando fallisce, e la pagina dell'esecuzione mostra cosa è stato addebitato.
