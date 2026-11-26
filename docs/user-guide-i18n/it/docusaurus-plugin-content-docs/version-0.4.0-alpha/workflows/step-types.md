---
title: Tipi di passaggio
sidebar_position: 2
description: Cosa fa ogni tipo di passaggio di un flusso di lavoro, se usa l'AI, le impostazioni importanti con i valori predefiniti e come i passaggi si richiamano a vicenda.
i18n_source_sha: 5f813972c22ade8d13aad0c8146aaf1804fd563f
---

# Tipi di passaggio {#step-types}

Un flusso di lavoro è un elenco numerato di **passaggi**. Ogni passaggio ha un **tipo di passaggio** che stabilisce che tipo di
lavoro svolge. Alcuni tipi di passaggio eseguono un agente AI; altri sono elaborazioni semplici e prevedibili che danno
sempre lo stesso risultato a parità di input (in questa guida li chiamiamo passaggi **non AI**).

In questa tabella ci sono diciotto tipi di passaggio. I loro nomi esatti (usati dall'assistente e nei flussi di lavoro salvati)
e le etichette mostrate nel costruttore di flussi di lavoro sono:

| Tipo di passaggio | Etichetta nel costruttore | Usa l'AI? | In una frase |
|---|---|---|---|
| `Agent` | Agente | Sì | Esegue un agente AI. |
| `ReviewLoop` | Ciclo di revisione | Sì | Dà un voto al lavoro precedente e lo rimanda indietro per un altro tentativo se il voto è troppo basso. |
| `Conditional` | Ramo condizionale | No (modalità AI facoltativa) | Salta a un passaggio o a un altro a seconda di una condizione. |
| `ForEach` | Per ogni elemento | Sì | Esegue un agente una volta per ogni elemento di un elenco. |
| `Parallel` | In parallelo | Sì | Esegue più agenti nello stesso momento. |
| `Extract` | Estrai | No | Riduce o rimodella il risultato di un passaggio precedente, così i passaggi successivi leggono meno. |
| `VideoAnalyze` | Analizza video | No (AI facoltativa per parlato e immagini) | Studia un video: inquadrature, pause, parlato, colore, green screen. |
| `VideoCompile` | Monta video | No | Taglia il video e aggiunge grafiche, inserti, musica, effetti, color grading, voce narrante. |
| `EditRoom` | Stanza di montaggio | Sì | Un panel di montatori AI decide cosa tenere. |
| `GraphicsRoom` | Stanza grafica | Sì | Un panel di artisti AI pianifica grafiche e inserti schermo. |
| `ColorGradeRoom` | Stanza del colore | Sì | Un panel di coloristi AI sceglie un unico look di colore. |
| `DailiesRoom` | Stanza dailies | Sì | Un panel di giudici AI sceglie una take generata dall'AI per ogni inquadratura pianificata. |
| `VideoGenerate` | Genera video | No (chiama un servizio video a pagamento) | Paga un servizio di generazione video per creare nuove clip. |
| `Voiceover` | Voce narrante | No (chiama un servizio vocale) | Trasforma righe scritte in audio di voce narrante parlata. |
| `ThumbnailGenerate` | Miniatura | No | Sceglie un fotogramma del tuo video come immagine di copertina, con una didascalia incorporata se vuoi. |
| `TimelineRender` | Esporta la timeline | No | Trasforma una delle timeline dell'editor del progetto in un file video finito. |
| `ExtractFrame` | Cattura un fotogramma | No | Salva un fotogramma di un video come immagine. |
| `ImageGenerate` | Genera immagini | No (usa un servizio di immagini a pagamento) | Crea immagini da una descrizione scritta, oppure modifica un'immagine come richiesto. |
| `AudioGenerate` | Genera musica o suoni | No (chiama un servizio audio a pagamento) | Crea musica o un effetto sonoro da una descrizione scritta. |
| `PinInsert` | Metti sugli schermi | No | Mette un video o un'immagine sugli schermi verdi, blu o magenta tracciati da un passaggio Analizza video. |

**Non esiste un tipo di passaggio "render Remotion".** Le animazioni Remotion (i video animati basati su codice che
ReelBolt produce) sono scritte e generate da alcuni agenti AI **dentro i passaggi `Agent`** (e
dentro la chiamata finale del regista del passaggio `GraphicsRoom`). L'`AuthorAgent` genera un intero video
promozionale; il `MotionGraphicsPlanner` e il `MotionGraphicsDirector` generano piccole grafiche e le
scene mostrate dentro gli inserti schermo tracciati. Vedi [Agenti](agents.md).

## Modificare i passaggi nel costruttore di flussi di lavoro {#editing-steps-in-the-workflow-builder}

Su computer la pagina del flusso di lavoro mostra i passaggi come schede su una tela, uno sotto l'altro con
frecce in mezzo. Ogni scheda dice cosa fa il passaggio a parole semplici ("Guarda i tuoi filmati",
"Sceglie le clip migliori", "Scrive la voce narrante") e alcuni chip che riassumono le scelte principali (quante
clip, la forma del risultato, se musica o voce narrante sono attive). Una scheda non diventa mai un modulo:

- **Fai clic su una scheda, o sul suo pulsante Impostazioni,** per aprire le impostazioni di quel passaggio in un pannello a destra.
  Le scelte di tutti i giorni vengono per prime; le impostazioni tecniche (codec, limiti, soglie, opzioni del modello
  decisionale, l'esatto formato dei dati che un passaggio passa avanti) stanno sotto **Avanzate**, chiuso per impostazione predefinita. Si
  apre da solo quando l'assistente propone una modifica lì o un salvataggio è stato rifiutato a causa di quelle impostazioni.
- **Scorri con la rotellina del mouse o il trackpad per muoverti sulla tela.** Per fare zoom, tieni premuto Ctrl (Cmd su
  Mac) mentre scorri, pizzica sul trackpad o usa i pulsanti + e − nell'angolo. La tela
  riempie la finestra e l'intero flusso di lavoro viene inquadrato quando si apre, così gli ultimi passaggi di un lungo
  flusso di lavoro sono facili da raggiungere. **Disposizione automatica** rimette le schede trascinate in una colonna ordinata.
- Una scheda di passaggio ti avvisa di una scelta mancante, per esempio un passaggio musicale in un progetto senza
  file audio, o un passaggio sui filmati senza nessun video scelto.

Su telefono i passaggi sono invece un semplice elenco, con le stesse impostazioni.

## Impostazioni che ogni passaggio ha {#settings-every-step-has}

Ogni passaggio, di qualunque tipo, ha queste impostazioni (i nomi tra apici inversi sono come li scrivono l'assistente
e i flussi di lavoro salvati):

- `stepOrder` — il numero del passaggio, a partire da 1. Gli altri passaggi lo richiamano con questo numero.
- `label` — un nome leggibile mostrato nel costruttore e nelle pagine delle esecuzioni.
- `stepType` — uno dei quindici tipi qui sopra. Il valore predefinito è `Agent`.
- Un **agente**. I passaggi `Agent`, `ReviewLoop` e `ForEach` eseguono l'agente che scegli. I passaggi non AI e
  i tre passaggi stanza hanno comunque bisogno di uno spazio agente riempito e usano un segnaposto integrato:
  `ExtractTransform` per `Extract` e `VideoTransform` per `VideoAnalyze`, `VideoCompile`,
  `VideoGenerate`, `Voiceover`, `ThumbnailGenerate`, `TimelineRender`, `EditRoom`, `GraphicsRoom`
  e `ColorGradeRoom`. Il segnaposto non viene
  mai eseguito come AI; i veri membri AI di una stanza vengono dalle impostazioni della stanza stessa.
- `cacheMode` — se un'esecuzione successiva può riutilizzare il risultato precedente di questo passaggio invece di rifare il
  lavoro: `Default` (decide ReelBolt), `Always` o `Never`. Vedi
  [Esecuzione e risultati](execution-and-outputs.md).
- Un **blocco di impostazioni** per i tipi che ne hanno bisogno, memorizzato come testo JSON in un campo che prende il nome dal
  tipo: `videoAnalyzeConfigJson`, `videoCompileConfigJson`, `extractConfigJson`,
  `editRoomConfigJson`, `graphicsRoomConfigJson`, `colorGradeRoomConfigJson`,
  `videoGenerateConfigJson`, `voiceoverConfigJson`, `thumbnailGenerateConfigJson`,
  `conditionalConfigJson`, `reviewLoopConfigJson`.

In ogni blocco di impostazioni, i nomi sono scritti in camelCase (`detectInsertRegions`, non
`DetectInsertRegions`) e le scelte sono scritte come parole tra virgolette (`"mode": "Reencode"`). Tutto ciò che
ometti prende il suo valore predefinito.

## Come i passaggi si richiamano a vicenda {#how-steps-refer-to-each-other}

I passaggi di ReelBolt si passano il lavoro in avanti. La maggior parte dei blocchi di impostazioni punta a un passaggio precedente in una di queste
forme.

**Puntare al risultato di un passaggio precedente** (usato da `VideoCompile`, dalle stanze, da `VideoGenerate` e da
`Extract`):

```json
{"from": "Previous"}
```

significa "il passaggio eseguito subito prima di questo", e

```json
{"from": "Step", "stepOrder": 2}
```

significa "il passaggio numero 2". Preferisci la forma numerata ogni volta che c'è più di un passaggio in mezzo.
I template per filmati la usano sempre, perché "Previous" da un passaggio di montaggio di solito indicherebbe il passaggio
di grafiche, musica o effetti, non la decisione di montaggio. (I passaggi `Extract` accettano inoltre
`{"from": "Accumulated"}` e `{"from": "ProjectFiles"}`.)

**Puntare a un video** (usato dal `source` di `VideoAnalyze`):

```json
{"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"}
```

usa un video che hai caricato;

```json
{"kind": "StepOutput", "stepOrder": 3}
```

usa il video prodotto dal passaggio 3 (per esempio un render o una clip generata); e

```json
{"kind": "PreviousStepOutput"}
```

usa il video più recente prodotto da un passaggio precedente di questa esecuzione.

```json
{"kind": "GeneratedClips", "stepOrder": 4}
```

analizza **ogni clip** consegnata da un passaggio `VideoGenerate` (qui il passaggio 4), come sorgenti separate, ciascuna
con il proprio id di clip (`v1t1`, `v1t2`, ...). È così che un passaggio `DailiesRoom` ottiene qualcosa tra cui
scegliere.

**Cosa vede un agente AI.** L'`agentInputContextMode` di un passaggio `Agent` decide quali risultati precedenti
legge l'agente:

- `FullWorkflow` — i risultati di tutti i passaggi precedenti (quelli più vecchi possono essere accorciati per mantenere il prompt di una
  dimensione ragionevole).
- `PreviousStepOnly` — solo il passaggio immediatamente prima.
- `SelectedPriorSteps` — solo i passaggi che elenchi (per esempio i passaggi 1, 4 e 5).
- `CustomMappedSubset` — una mappatura personalizzata che scrivi tu.

Un agente che ha bisogno di due risultati precedenti (per esempio un pianificatore di grafiche che ha bisogno sia dell'analisi video
sia della decisione di montaggio) deve usare `FullWorkflow` o `SelectedPriorSteps`.

**Il tuo brief.** Il testo che scrivi in **Il tuo brief** quando fai clic su **Avvia** viene passato a ogni agente AI
come istruzioni aggiuntive. È facoltativo, a meno che per il flusso di lavoro non sia attiva l'opzione **Richiedi un brief prima di ogni esecuzione**.

## Passaggio Agent (`Agent`) {#agent-step-agent}

Un passaggio `Agent` esegue un agente AI integrato (come `VideoStoryEditor` o `AuthorAgent`). L'
agente legge i risultati precedenti (vedi `agentInputContextMode` sopra), può usare i suoi strumenti (leggere i file del progetto,
scrivere codice, fare il render) e restituisce la sua risposta. Gli agenti integrati restituiscono una forma di risposta
fissa su cui si basano i passaggi successivi.

Un agente personalizzato può essere inserito in un passaggio `Agent`, ma il passaggio viene contrassegnato come **Saltato**
quando l'esecuzione lo raggiunge, perché gli agenti personalizzati non possono essere eseguiti dentro i flussi di lavoro (vedi
[Agenti](agents.md#custom-agents)).

- **Usa l'AI:** sì.
- **Nuovi tentativi:** se l'agente fallisce o restituisce una risposta della forma sbagliata, ReelBolt riprova,
  dicendo all'agente cosa è andato storto, fino a 3 tentativi per impostazione predefinita. Un agente che esaurisce il tempo
  non viene riprovato.
- **Riutilizzo nelle esecuzioni successive:** gli agenti che si limitano a leggere (agenti di analisi, `VideoStoryEditor`,
  `MusicSupervisor`, `SoundDesigner`, `ShotDirector`, `Colorist`, `VideoReviewAgent`) possono essere riutilizzati
  da un'esecuzione identica precedente, a meno che il passaggio non sia impostato su `"cacheMode": "Never"` (i template impostano
  ogni passaggio creativo su `Never`). Gli agenti che scrivono codice, scrivono file o generano
  (`RemotionComponentTranslator`, `AuthorAgent`, `MotionGraphicsPlanner`) non vengono mai riutilizzati.

Passaggio minimo (come lo scriverebbe l'assistente):

```json
{"stepOrder": 2, "label": "Decide which spans to keep", "stepType": "Agent",
 "agentDefinitionId": "ID-OF-THE-VideoStoryEditor-AGENT",
 "agentInputContextMode": "PreviousStepOnly", "cacheMode": "Never"}
```

## Passaggio Ciclo di revisione (`ReviewLoop`) {#review-loop-step-reviewloop}

Un passaggio `ReviewLoop` esegue un agente revisore (di solito `ReviewAgent` per i promo o `VideoReviewAgent`
per i filmati montati), legge il suo voto su 10 e decide se tornare indietro a rifare il lavoro.

- `minScore` — il punteggio di superamento. Se il voto è inferiore, l'esecuzione torna indietro. Predefinito 9 se non impostato (i
  template usano 8 o 9).
- `loopTargetStepOrder` — il passaggio a cui tornare (per i template per filmati, la decisione di montaggio,
  passaggio 2; per i promo, il passaggio `AuthorAgent`).
- `maxIterations` — il numero massimo di cicli di revisione (i template usano 2 o 3). Quando l'ultimo ciclo
  dà ancora un voto troppo basso, l'esecuzione **prosegue e termina** con ciò che ha; non fallisce.

Quando l'esecuzione torna indietro, i commenti del revisore vengono passati ai passaggi che vengono rifatti, così possono
correggere ciò che è stato criticato.

Un `reviewLoopConfigJson` facoltativo (`{"enabled": true, ...}`) permette a un veloce "modello decisionale" di saltare il
revisore quando è molto sicuro; è disattivato per impostazione predefinita e richiede un provider decisionale configurato da un amministratore.

**Controllo visivo.** In **Avanzate: controllo visivo**, attiva **Controlla l'immagine prima di ogni
revisione** e il ciclo guarda i fotogrammi del video che sta rivedendo prima di ogni ciclo. Segnala il testo
tagliato, che esce dal suo riquadro, sta troppo vicino al bordo o è troppo piccolo per essere letto, e passa
al revisore un elenco come "Fotogramma 3 a 12,4 s: il testo esce dal riquadro". Se un problema è segnato come
grave, il voto del ciclo viene tenuto al **Punteggio massimo quando viene trovato un problema grave**
(predefinito 6), così il ciclo torna indietro e i passaggi che vengono rifatti ricevono la stessa segnalazione
di fotogramma e tempo. Le altre impostazioni sono **Fotogrammi da guardare** (da 2 a 16, predefinito 8), **Margine di sicurezza (% del
fotogramma)** (da 0 a 20, predefinito 5), **Testo leggibile minimo (% dell'altezza del fotogramma)** (da 0,5 a 20,
predefinito 2,5) e **Modello di visione** (vuoto usa il tuo predefinito). Il controllo è disattivato per impostazione predefinita e, quando
è disattivato, non cambia nulla. Richiede un modello di visione configurato per il tuo account; senza, o se il
controllo fallisce per qualsiasi motivo, la revisione procede senza. Ogni ciclo aggiunge una piccola chiamata di visione
e i risultati sono un consiglio per il revisore, non un verdetto.

**Il revisore può correggere da solo.** Con `"allowAdjustments": true` in `reviewLoopConfigJson`,
il revisore può cambiare un breve elenco di impostazioni dei passaggi precedenti solo per questa esecuzione (la
dimensione dei titoli, quanto durano gli effetti sonori, il volume della musica, la durata obiettivo entro un quarto
del tuo brief, quanto conserva l'analisi del girato e simili) e chiedere le stesse modifiche che puoi chiedere tu su
un'esecuzione terminata: ripetere una riga della voce narrante con parole nuove, un'altra velocità o una pronuncia
corretta, rifare un'inquadratura generata, un take o un take musicale, cambiare il testo di una miniatura o
riscrivere una scena del copione. Il flusso di lavoro salvato non cambia mai. L'esecuzione torna al primo passaggio
modificato e rifà ciò che ne dipende, dentro la stessa esecuzione; ogni passaggio rifatto viene addebitato come
qualsiasi altro passaggio (una riga della voce narrante o un'inquadratura generata costano quanto costa produrle).
Il revisore non modifica mai i tuoi prompt, non sceglie mai un altro modello o provider, non alza mai un limite di
spesa o il numero di take e non permette mai che il tuo girato venga inviato a un servizio esterno; una voce che
indica deve essere una che puoi usare. Ogni modifica, applicata o rifiutata, compare nella pagina dell'esecuzione con
il motivo e il costo del rifacimento. Se i crediti finiscono durante un rifacimento, la revisione si ferma lì e
l'esecuzione termina con la sua versione migliore, segnata come **Richiede attenzione**. La stima dei costi include
nella cifra "fino a" il massimo che questi rifacimenti possono aggiungere.

Ogni template fornito da ReelBolt lo attiva, insieme al controllo visivo, ai dati della timeline e del suono che la
piattaforma misura sul film e agli sguardi ravvicinati sui fotogrammi che il revisore chiede.

- **Usa l'AI:** sì. **Riutilizzo nelle esecuzioni successive:** mai.

## Passaggio Condizionale (`Conditional`) {#conditional-step-conditional}

Un passaggio `Conditional` guarda il risultato del passaggio precedente e salta a uno di due passaggi.

- `conditionExpression` — un test scritto sui campi del risultato precedente, per esempio
  `$.overallScore >= 8` dopo un passaggio `ReviewAgent`. I nomi dei campi devono corrispondere al risultato precedente
  esattamente come appare nella pagina dell'esecuzione. Puoi combinare i test con `&&` e `||`.
- `trueBranchStepOrder` / `falseBranchStepOrder` — il numero del passaggio a cui andare in ciascun caso. Se il
  ramo non è impostato, l'esecuzione prosegue semplicemente con il passaggio successivo.
- Un'espressione che non si può calcolare conta come **falsa**.

Un `conditionalConfigJson` facoltativo con `"mode": "Decision"` pone invece a un modello decisionale una domanda
sì/no; il valore predefinito è `"mode": "Expression"`.

- **Usa l'AI:** no (a meno che non sia in modalità Decision). **Riutilizzo nelle esecuzioni successive:** mai.

## Passaggio Per ogni (`ForEach`) {#for-each-step-foreach}

Un passaggio `ForEach` prende un elenco dal risultato del passaggio precedente ed esegue l'agente scelto una volta per
ogni elemento, più elementi alla volta.

- `loopSourceExpression` — dove si trova l'elenco, per esempio `$.components` (oppure `$` quando l'intero
  risultato è un elenco).
- `maxIterations` — il massimo di elementi da elaborare; 0 significa tutti.

Il risultato del passaggio è `{"results": [...], "sourceCount": N}`. Se l'elenco è vuoto il passaggio
termina senza eseguire l'agente.

- **Usa l'AI:** sì. **Riutilizzo nelle esecuzioni successive:** mai.

## Passaggio In parallelo (`Parallel`) {#parallel-step-parallel}

Un passaggio `Parallel` esegue più agenti nello stesso momento sullo stesso input e raccoglie tutte le loro
risposte in un unico elenco, ciascuna con il nome dell'agente. Elenca gli agenti in `parallelAgentIdsJson`
(un elenco JSON di id di agenti). I cinque agenti di analisi del codice sono buoni candidati, perché non
dipendono l'uno dall'altro.

Un passaggio `Conditional` dopo un passaggio `Parallel` può verificare la risposta di un agente con
`$.AgentName.field`, per esempio `$.Review.overallScore > 8`.

- **Usa l'AI:** sì. **Riutilizzo nelle esecuzioni successive:** mai.

## Passaggio Estrai (`Extract`) {#extract-step-extract}

Un passaggio `Extract` è un passaggio non AI che **riduce** un risultato precedente, così i passaggi AI successivi leggono meno
(e costano meno). Può isolare un elenco e tenere solo alcuni campi (`"operation": "Project"`),
cercare id scelti in un elenco precedente (`"operation": "Resolve"`) o elencare i file del progetto
(`"operation": "Files"`).

Esempio dal template `lean-context-promo` (tieni al massimo 40 componenti, tre campi ciascuno):

```json
{"version": 1, "operation": "Project",
 "inputs": {"source": {"from": "Step", "stepOrder": 3}},
 "path": "$.components", "fields": ["name", "filePath", "responsibility"],
 "take": 40, "maxOutputChars": 8000, "expect": {"minItems": 1}}
```

- **Usa l'AI:** no. **Nuovi tentativi:** nessuno (fallirebbe di nuovo allo stesso modo). **Riutilizzo nelle esecuzioni successive:**
  sì.

## Passaggio Analizza video (`VideoAnalyze`) {#analyze-video-step-videoanalyze}

Un passaggio `VideoAnalyze` studia un video e produce una descrizione compatta che gli agenti AI possono leggere,
più un registro completo e dettagliato che il passaggio di montaggio usa in seguito. Trova:

- **inquadrature** (stacchi di camera), con id `s0`, `s1`, ...;
- **pause / silenzi**, id `g0`, `g1`, ...;
- **parlato** come testo, id `t0`, `t1`, ... (richiede un provider di trascrizione);
- dati su **aspetto e suono** di ogni inquadratura (movimento, luminosità, colore, volume, take ripetute);
- facoltativamente, buoni **punti per le grafiche** (id `p0`, ...), **scelte musicali** (`m0`, ...),
  **scelte di effetti sonori** (`x0`, ...) e **regioni green screen** tracciate (`r0`, ...).

Non taglia mai nulla. È non AI, tranne per la trascrizione del parlato facoltativa e le descrizioni delle immagini
facoltative, che chiamano provider configurati da un amministratore.

Impostazioni importanti (valori predefiniti tra parentesi):

| Impostazione | Predefinito | Cosa fa |
|---|---|---|
| `source` | obbligatorio | Quale video analizzare (vedi "Puntare a un video" sopra). |
| `sources` | nessuno | Un elenco di più video (take, angolazioni) analizzati insieme in un unico insieme di id. Se impostato, `source` viene ignorato. |
| `transcription` | `"Optional"` | Trascrizione del parlato: `"Off"`, `"Optional"` (usala se esiste un provider), `"Required"` (fallisce senza). |
| `language` | auto | Indicazione della lingua per la trascrizione, per esempio `"en"`. |
| `vision` | `"Off"` | Descrizioni AI di ciò che c'è in ogni inquadratura: `"Off"`, `"Optional"`, `"Required"`. Costa una chiamata AI per ogni inquadratura descritta, fino a `maxCaptionedShots` (50). |
| `locateSubjects` | `false` | Trova dove si trova il soggetto principale di ogni inquadratura, così un montaggio verticale o quadrato con `outputFit: "Subject"` lo tiene nel fotogramma. Usa l'AI per le immagini (due chiamate per inquadratura, fino a `maxSubjectShots`, 40). `subjectLabel` indica cosa cercare, per esempio `"the phone"`; se vuoto trova il soggetto principale. Le inquadrature con un green screen tracciato non ne hanno bisogno. |
| `emitOverlayPlacements` | `false` | Trova buoni punti per titoli e terzi inferiori (id `p`). Necessario per le grafiche. |
| `offerMusicTracks` | `false` | Propone ogni file audio caricato come scelta musicale (id `m`). |
| `offerSfxClips` | `false` | Propone ogni file audio caricato come scelta di effetto sonoro (id `x`). |
| `detectInsertRegions` | `false` | Trova e segue gli schermi colorati (green screen) nei filmati (id `r`). Necessario per gli inserti schermo tracciati. |
| `insertRegionColor` | `"green"` | Colore dello schermo da cercare: `"green"`, `"blue"` o `"magenta"`. |
| `maxInsertPlatesPerFrame` | `1` | Quanti schermi colorati si possono seguire nello stesso momento (da 1 a 16). Aumentalo per due telefoni nella stessa inquadratura. |
| `measureInsertCurvature` | `true` | Misura con più precisione gli angoli e la curvatura di ogni schermo. |
| `solveInsertChromaKey` | `true` | Calcola il colore esatto dello schermo, così l'inserto può essere ritagliato sulla vera forma dello schermo (angoli arrotondati, notch, una mano davanti). |
| `subShotMode` | `"Off"` | Punti di taglio dentro le inquadrature lunghe: `"Off"` (nessuno), `"Even"` (a intervalli uguali) o `"Content"` (dove l'immagine si ferma o cambia). L'editor può tenere solo una parte di un'inquadratura tagliata, oppure ignorare i punti di taglio. |
| `silenceThresholdDb` / `minSilenceMs` | `-34` / `350` | Quanto silenziosa e quanto lunga deve essere una pausa per contare. |
| `sceneThreshold` | `0.30` | Quanto devono essere diversi due fotogrammi per contare come stacco. |
| `maxOutputChars` | `24000` | Limite di dimensione della descrizione che leggono gli agenti AI. |
| `maxDurationSeconds` | non impostato | Può solo abbassare il limite della piattaforma (1 ora per impostazione predefinita); un vecchio `1800` salvato equivale a non impostato. I video più lunghi vengono rifiutati con `SOURCE_TOO_LONG`. |
| `derushPrePass` | `"Off"` | Suggerimento facoltativo del modello decisionale per ogni inquadratura; `"Fast"` si comporta esattamente come `"Prior"`. |

Esempio minimo (il predefinito del template; devi aggiungere l'id del file):

```json
{"version": 1, "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"}}
```

**Le foto funzionano come clip.** Un'immagine PNG, JPEG, WebP, BMP, TIFF o AVIF scelta come sorgente viene trasformata
in una breve inquadratura in movimento: un lento avvicinamento, un allontanamento e una panoramica, ciascuno in due brevi tempi. L'
editor sceglie quale movimento si adatta e se resta per un tempo o due, così le foto di un sito web o
di un account Instagram possono essere montate in un reel insieme ai video.

**Scegliere le clip nel costruttore.** Nelle impostazioni del passaggio, sotto **Le tue riprese**, un primo passaggio
propone direttamente i tuoi video caricati (non c'è un passaggio precedente da cui prendere un video). Per montare
più clip in un solo video, attiva **Usa più clip** e sceglile tutte insieme in
**Scegli le clip…**; vengono usate nell'ordine dell'elenco, che puoi regolare riga per riga, e
**Aggiungi un'altra clip** aggiunge una riga per un altro file caricato. Se attivi **Offri le tue tracce musicali**
o **Offri le tue clip di effetti sonori** in un progetto senza file audio, compare un promemoria che ti dice di caricarne
prima alcuni.

Se la descrizione supera `maxOutputChars`, ReelBolt prima accorcia i dettagli di ogni inquadratura,
poi esclude interi elenchi facoltativi (scelte musicali, scelte di effetti, regioni green screen) e solo
dopo esclude le inquadrature. Un elenco escluso non può essere usato dai passaggi successivi di quell'esecuzione, quindi per il lavoro con
il green screen controlla che il riepilogo dell'esecuzione mostri che le regioni sono state proposte (vedi la
[ricetta sull'inserto schermo tracciato](../recipes/tracked-screen-insert.md)).

- **Nuovi tentativi:** nessuno (rieseguire dà lo stesso risultato; la trascrizione ha un suo piccolo nuovo tentativo).
- **Riutilizzo nelle esecuzioni successive:** sì, un'analisi identica viene riutilizzata.
- **Errori comuni:** `SOURCE_UNRESOLVED` (nessun file selezionato, oppure il passaggio indicato non ha creato alcun
  video), `SOURCE_TOO_LONG` (oltre il limite di durata), `SOURCE_TOO_LARGE` (oltre il limite di dimensione, 30 GiB per impostazione predefinita), `SOURCE_TOO_DEMANDING` (una clip molto pesante non è stata analizzata nel tempo concesso; nulla viene addebitato; prova una clip più corta o un'esportazione più piccola, ad esempio in 1080p), `TRANSCRIPTION_UNAVAILABLE` o
  `TRANSCRIPTION_FAILED` (solo con `"Required"`), `VISION_UNAVAILABLE` / `VISION_FAILED` (solo con
  `"Required"`).

## Passaggio Monta video (`VideoCompile`) {#compile-video-step-videocompile}

Un passaggio `VideoCompile` produce il file video montato finale. Prende la decisione di montaggio (quali
id tenere), cerca i loro tempi esatti nell'analisi, taglia il video con precisione al fotogramma e, nello
stesso passaggio, aggiunge gli extra che hai attivato. È non AI: ogni numero viene da
ReelBolt, mai da un'AI.

Ha sempre bisogno di:

- `decision` — il passaggio la cui risposta dice cosa tenere: un passaggio agente `VideoStoryEditor` o un
  passaggio `EditRoom`, per esempio `{"from": "Step", "stepOrder": 2}`.
- `analysisStepOrder` — il numero del passaggio `VideoAnalyze`, per esempio `1`.

Impostazioni principali (valori predefiniti tra parentesi):

| Impostazione | Predefinito | Cosa fa |
|---|---|---|
| `mode` | `"Reencode"` | `"Reencode"` taglia in modo esatto e consente ogni extra. `"StreamCopy"` copia un taglio continuo unico senza perdita e in fretta, tagliando solo sui keyframe; un taglio con più parti viene ricodificato. Richiede `"allowKeyframeSnapping": true` e **non** consente extra. |
| `outputFileName` | `"edited.mp4"` | Nome del risultato. Se lasciato su `"edited.mp4"`, il file aggiunto al tuo progetto prende invece il nome del flusso di lavoro e dell'esecuzione, per esempio `Spring teaser - run 3.mp4`. |
| `outputFormat` | `"Source"` | Forma del video: `"Source"` (la dimensione della tua clip più grande), `"Vertical"` (1080x1920, per Reels, Shorts e TikTok), `"Square"` (1080x1080), `"Landscape"` (1920x1080) o `"Portrait"` (1080x1350). Richiede `"Reencode"`. Queste dimensioni valgono per un'esecuzione in Auto o 1080p: un'esecuzione avviata a 720p le rende 720x1280, 720x720, 1280x720 e 720x900, e una avviata a 4K le rende 2160x3840, 2160x2160, 3840x2160 e 2160x2700. Ogni dimensione resta entro il limite del tuo piano (vedi [Risoluzione di esportazione in ogni piano ReelBolt](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan)). |
| `outputFit` | `"Crop"` | Quando una clip ha una forma diversa da `outputFormat`: `"Crop"` riempie il fotogramma e taglia i lati (un'inquadratura larga mantiene il centro), `"Subject"` riempie il fotogramma ma tiene il soggetto in inquadratura (uno schermo tracciato, o il soggetto trovato dal passaggio di analisi con `locateSubjects`) — una clip il cui soggetto è troppo largo per qualsiasi ritaglio, come il titolo di una schermata finale, viene invece mostrata intera, `"Pad"` mostra l'intera inquadratura con bande, riempite secondo `canvasFill` (`"Black"` o `"Blur"`). |
| `targetDurationSec` | `0` | La durata che vuoi, in secondi (`0` = nessun obiettivo). Un montaggio più lungo viene accorciato fino a quella durata, tagliando le estremità delle inquadrature più lunghe e mai l'ultima né la voce narrante. ReelBolt non inventa filmati; se il video risulta più corto o più lungo di oltre il 20%, l'esecuzione te lo dice. |
| `loudnessTarget` | `"Off"` | Quanto è forte il video finale: `"Social"` corrisponde a Instagram, TikTok e YouTube, `"Web"` ai siti web, `"Broadcast"` alla TV. L'equilibrio tra voce e musica resta lo stesso. Sotto **Volume** nel costruttore. |
| `registerProjectFile` | `true` | Aggiunge il risultato anche ai file del progetto, così può essere modificato di nuovo. |
| `rateControl` + `videoBitrateKbps` | spento / nessuno | Imposta il bitrate del video: `"Capped"` (vicino o sotto quel valore) o `"Average"` (punta a quel valore), 200–100000 kbps. `"Quality"` non accetta un bitrate. Se omessi, la codifica è quella di qualità. |
| `minSegmentMs` | `250` | Scarta i pezzi tenuti più corti di questo valore. |
| `transitionPolicy` | `"Off"` | Raccordi tra i tagli: `"Off"` (tagli netti), `"AudioOnly"`, `"Auto"`, `"Expressive"` (dissolvenze e immersioni visibili scelte automaticamente) o `"Editor"`: l'AI di montaggio sceglie ogni raccordo tra 56 transizioni (scorrimenti, tendine, zoom, flash, immersioni, iris e altro) e la loro velocità. |
| `programFadeInMs` / `programFadeOutMs` | `0` | Dissolvenza in entrata dell'immagine all'inizio / in uscita alla fine. |
| `enableGraphics` + `graphicsPlan` | off / nessuno | Aggiunge titoli e terzi inferiori da un passaggio `MotionGraphicsPlanner` o `GraphicsRoom`. |
| `enableInserts` (+ `graphicsPlan`) | off | Mette le scene generate negli schermi verdi tracciati. Legge **lo stesso** `graphicsPlan`. |
| `enableMusic` + `musicPlan` o `musicTrackProjectFileId` | off | Aggiunge una base musicale scelta da `MusicSupervisor`, o un file specifico che indichi. |
| `enableSfx` + `sfxPlan` | off | Aggiunge gli effetti sonori pianificati da `SoundDesigner`. |
| `enableColorGrade` + `colorGradePlan` | off | Applica un look di colore da `Colorist` o da un passaggio `ColorGradeRoom`. |
| `enableGeneratedClips` + `generatedClips` | off | Inserisce le clip generate dall'AI da un passaggio `VideoGenerate`. Una clip pianificata come `ScreenContent` viene invece messa su uno schermo verde tracciato, il che richiede anche `enableInserts`. |
| `reframes` | nessuno | Zoom e panoramica su un tratto di una clip, con transizione morbida tra i keyframe (per esempio un'inquadratura stretta sulla tastiera che si allarga dolcemente a tutto lo schermo). Ogni reframe indica una clip e un tempo di inizio e di fine; ogni keyframe dà un tempo, uno zoom (1 = fotogramma intero, fino a 8), un punto centrale e un'attenuazione. Fino a 32 keyframe per reframe. Fuori dal tratto la clip viene mostrata intera. Si inserisce come JSON sotto **Avanzate** (**Reframe (JSON)**); il passaggio lo controlla quando salvi e dice cosa correggere. |
| `sourceFits` | nessuno | Sceglie un adattamento diverso per una clip rispetto alle altre (**Numero della clip** e **Adattamento**, sotto **Avanzate**). |
| `subjectSafeArea` | `0.05` | Con **Riempi il fotogramma attorno al soggetto**: una clip che ha testo a schermo viene mostrata intera con bande invece di essere ritagliata, e il testo resta a questa distanza dal bordo (da 0 a 0,25; 0 lo disattiva). |
| `insertContentRef` | nessuno | Mette il video o l'immagine di un passaggio precedente, o il risultato di un'esecuzione precedente, in uno schermo tracciato, invece di un file che hai caricato (**Contenuto dell'inserto per riferimento**). |
| `musicGainDb` | nessuno | Il livello esatto della musica, da -40 a +6 dB. `0` riproduce la tua traccia al suo volume pieno; se vuoto, la musica resta piano sotto l'immagine. Il parlato la abbassa comunque quando il ducking è attivo. |
| `generatedClipAudio` | `Auto` | Cosa succede al suono che arriva con una clip generata: `Auto` (viene riprodotto, come il parlato), `Keep` (viene riprodotto come sottofondo e la musica non viene abbassata sotto di esso) o `Mute`. Il risultato dice come è stato trattato il suono di ogni clip. |
| `takeSelection` | nessuno | Quando il passaggio di generazione ha creato più take per inquadratura, inserisce solo la take scelta da un passaggio `DailiesRoom` (altrimenti la prima take consegnata di ogni inquadratura). |
| `enableVoiceover` + `voiceoverStepOrder` | off / nessuno | Mixa la voce narrante di un passaggio `Voiceover` (un numero di passaggio, non un riferimento `from`). |
| `voiceoverMode` | `OverMusic` | Cosa succede al parlato originale sotto ogni battuta di voce narrante: lasciarlo (`OverMusic`), abbassarlo (`DuckDialogue`, di `voiceoverDuckDb`, predefinito -12) o rimuoverlo (`ReplaceDialogue`, per i doppiaggi). |
| `enableCaptions` + `captionStyle` | off / `Subtitle` | Incorpora la voce narrante nell'immagine mentre viene pronunciata: `Subtitle` (poche parole alla volta), `Karaoke` (parole pronunciate evidenziate), `Punch` (una parola alla volta) o `Bold` (parole grandi, due o tre alla volta, il look di Reels/TikTok). Karaoke e Punch richiedono un provider di trascrizione per i tempi delle parole. |
| `enablePickups` + `pickupVoiceoverStepOrder` | off / nessuno | Sostituisce le frasi parlate sbagliate con battute registrate di nuovo da un passaggio `Voiceover` la cui sorgente è `PickupPlan`. L'immagine non viene cambiata (nessun lip sync). |
| `expect.minRetainedRatio` | `0.15` | Rifiuta un montaggio che tiene meno del 15% dei filmati. |

**Nel costruttore** compaiono sotto **Formato e durata** in alto nelle impostazioni del passaggio:
**Forma** (**Dimensione originale**, **Verticale 9:16 — Reels/TikTok/Shorts**, **Quadrato 1:1 — feed**,
**Orizzontale 16:9 — YouTube**), **Quando una clip ha una forma diversa** (**Riempi il fotogramma (ritaglia il
centro)**, **Riempi il fotogramma, tieni il soggetto in inquadratura** o **Adatta l'intera clip (bande)**, mostrato solo per una
forma fissa) e **Durata desiderata (secondi)**. Gli extra
si chiamano **Aggiungi musica di sottofondo**, **Aggiungi effetti sonori**, **Aggiungi titoli e grafica** e così via; attivare
musica o effetti sonori in un progetto senza file audio mostra un promemoria per caricarne alcuni. Codec,
preset, qualità (CRF), margine di taglio, tempi delle transizioni e i controlli prima della codifica sono sotto
**Avanzate**.

Ogni interruttore "enable" è disattivato per impostazione predefinita e, quando è disattivato, il risultato è esattamente quello che sarebbe senza
quella funzione. Ogni extra **richiede `"mode": "Reencode"`**. Quando un extra non può essere applicato (un
piano mancante, un id sconosciuto, un file non valido), viene saltato e il motivo viene scritto nel risultato del
passaggio; il montaggio stesso si completa comunque.

Quando il video finito non è proprio quello che avevi chiesto, il risultato del passaggio elenca degli **avvisi** a
parole semplici, e la pagina dell'esecuzione li mostra: una parte o la maggior parte della voce narrante non è stato possibile collocarla, musica
o effetti sonori sono attivi ma il progetto non ha file audio tra cui scegliere (caricane alcuni nei
file del progetto), oppure il video è risultato molto più corto o più lungo di `targetDurationSec`. Se
le tue clip non hanno un suono proprio, voce narrante, musica ed effetti sonori si sentono comunque: ReelBolt
aggiunge una traccia audio silenziosa su cui farli suonare.

Esempio minimo (da `video-derush-edit`):

```json
{"version": 1, "decision": {"from": "Previous"}, "analysisStepOrder": 1}
```

- **Nuovi tentativi:** nessuno. **Riutilizzo nelle esecuzioni successive:** sì, quando tutto ciò che legge è identico.
- **Errori comuni:** `DECISION_UNRESOLVED` (il passaggio di decisione è sbagliato o manca),
  `ANALYSIS_NOT_FOUND` (`analysisStepOrder` sbagliato), `EMPTY_KEEP` (l'editor non ha tenuto nulla),
  `UNKNOWN_ID`, `EXPECT_FAILED` (tenuto troppo poco), `GRAPHICS_REQUIRE_REENCODE`,
  `INSERTS_REQUIRE_REENCODE`, `MUSIC_REQUIRES_REENCODE`, `SFX_REQUIRES_REENCODE`,
  `COLOR_GRADE_REQUIRES_REENCODE`, `GENERATED_CLIPS_REQUIRE_REENCODE`,
  `TRANSITIONS_REQUIRE_REENCODE`, `OUTPUT_FORMAT_REQUIRES_REENCODE` (un extra o un
  `outputFormat` fisso è stato attivato con `"StreamCopy"`), `CODEC_NOT_ALLOWED`,
  `ENCODE_FAILED`. Anche `reframes` fallisce con `REFRAME_REQUIRES_REENCODE` con `"StreamCopy"`.

## Passaggio Stanza di montaggio (`EditRoom`) {#edit-room-step-editroom}

Un passaggio `EditRoom` sostituisce un singolo `VideoStoryEditor` con un **panel di montatori AI**. Per impostazione predefinita
tre posti — `PacingEditor`, `StoryEditor` e `CraftEditor` — discutono a turno dei filmati analizzati,
un `VideoEditDirector` modera e il regista poi scrive un'unica decisione finale su "cosa tenere"
nella stessa identica forma di un singolo montatore, così il passaggio di montaggio la tratta allo stesso modo.

| Impostazione | Predefinito | Cosa fa |
|---|---|---|
| `view` | nessuno | L'analisi da discutere, per esempio `{"from": "Previous"}` o `{"from": "Step", "stepOrder": 1}`. |
| `seats` | i tre sopra | I tuoi posti: un elenco di `{"name": ..., "persona": ...}`. |
| `rounds` | `2` | Giri completi attorno al tavolo prima che parli il regista. |
| `maxTurns` | `8` | Limite massimo di turni (da 2 a 20). |
| `fallbackToSoloEditor` | `true` | Se il panel fallisce, lascia decidere un solo `VideoStoryEditor`. |
| `persistTranscript` | `true` | Conserva la discussione così puoi leggerla (solo a scopo informativo). |

```json
{"version": 1, "view": {"from": "Previous"}}
```

- **Usa l'AI:** sì, molte chiamate. **Nuovi tentativi:** non riprovato per intero (la stesura finale del regista
  ha i propri nuovi tentativi). **Riutilizzo nelle esecuzioni successive:** possibile per impostazione predefinita; il template imposta `Never`.

## Passaggio Stanza grafica (`GraphicsRoom`) {#graphics-room-step-graphicsroom}

Un passaggio `GraphicsRoom` sostituisce un singolo `MotionGraphicsPlanner` con un **panel di artisti AI**. Per
impostazione predefinita quattro posti — `LayoutArtist`, `TimingArtist`, `CopyArtist` e `InsertArtist` — discutono dei
punti grafici e delle regioni green screen proposti; un `MotionGraphicsDirector` modera e poi, in
un'unica chiamata finale, scrive il piano grafico **e genera le grafiche e le scene degli inserti con
Remotion**. Durante la discussione nessuno può fare il render; può farlo solo la chiamata finale del regista.
Il suo piano ha la stessa forma di quello del singolo planner, quindi il `graphicsPlan` del passaggio di montaggio può puntare
a lui.

Le impostazioni sono le stesse della stanza di montaggio (`view`, `seats`, `rounds`, `maxTurns` predefinito `10`, ...),
con `fallbackToSoloPlanner` (predefinito `true`) al posto di `fallbackToSoloEditor`. Punta `view` al
**passaggio di analisi**, non alla decisione di montaggio:

```json
{"version": 1, "view": {"from": "Step", "stepOrder": 1}}
```

Un piano vuoto (senza grafiche) è un risultato valido. **Riutilizzo nelle esecuzioni successive:** mai (può fare il render).
**Nuovi tentativi:** non riprovato per intero.

## Passaggio Stanza del colore (`ColorGradeRoom`) {#color-grade-room-step-colorgraderoom}

Un passaggio `ColorGradeRoom` ha un **panel di coloristi AI** — `ToneArtist`, `PaletteArtist` e
`ContinuityArtist` per impostazione predefinita — che discutono del colore misurato di ogni inquadratura; un `ColorGradeDirector`
sceglie poi un unico look per tutto il video solo a parole: `look` (`None`, `Warm`, `Cool`, `Filmic`,
`Vibrant`, `Muted`, `Mono`), `strength` (`Subtle`, `Normal`, `Strong`), `shadowTone` e
`highlightTone`. ReelBolt trasforma le parole nella vera modifica di colore al momento del montaggio. Un singolo
passaggio agente `Colorist` produce la stessa forma e può essere usato al suo posto.

```json
{"version": 1, "view": {"from": "Step", "stepOrder": 1}}
```

`fallbackToSoloColorist` è `true` per impostazione predefinita. **Nuovi tentativi:** fino al normale numero di tentativi degli agenti.
**Riutilizzo nelle esecuzioni successive:** possibile per impostazione predefinita; il template imposta `Never`.

## Passaggio Stanza dailies (`DailiesRoom`) {#dailies-room-step-dailiesroom}

Un passaggio `DailiesRoom` serve per i flussi di lavoro in cui il passaggio di generazione ha creato **più versioni ("take")
di ogni clip generata dall'AI** (`"takes": 2` o più). Un panel di giudici AI — `ContinuityJudge`,
`MotionJudge` e `ImageJudge` per impostazione predefinita — confronta le take di ogni inquadratura pianificata usando i dati
che un passaggio `VideoAnalyze` ha misurato su di esse; un `DailiesDirector` scrive poi la scelta finale: al massimo
una take per inquadratura, indicata solo dal suo id (`v1t2`). Non scrive mai un punteggio né un numero.

Punta `view` a un passaggio `VideoAnalyze` la cui sorgente sono le clip del passaggio di generazione:

```json
{"version": 1, "view": {"from": "Step", "stepOrder": 5}}
```

dove il passaggio 5 è `{"version": 1, "source": {"kind": "GeneratedClips", "stepOrder": 4}, "transcription": "Off"}`.
Poi dai al passaggio `VideoCompile` `"takeSelection": {"from": "Step", "stepOrder": 6}` così inserisce
solo le take scelte. Lasciare un'inquadratura non selezionata è una risposta valida: quell'inquadratura tiene la sua prima
take consegnata. Le altre impostazioni sono quelle della stanza di montaggio (`seats`, `rounds`, `maxTurns`,
`persistTranscript`), con `fallbackToSoloSelector` (predefinito `true`).

- **Usa l'AI:** sì, molte chiamate. **Riutilizzo nelle esecuzioni successive:** possibile per impostazione predefinita; il template imposta `Never`.

## Passaggio Genera video (`VideoGenerate`) {#generate-video-step-videogenerate}

Un passaggio `VideoGenerate` **paga un servizio di generazione video** (MiniMax o Higgsfield, configurati da un
amministratore) — oppure usa un **server ComfyUI che gestisci tu**, che non costa denaro ma tempo di GPU — per
creare nuove clip video. È non AI nel senso che non ragiona; invia prompt e
raccoglie clip. Funziona in due modi:

- `"planSource": "Inline"` (predefinito) — scrivi un prompt in `inlinePrompt` (fino a 7000
  caratteri) e ne ricava `takes` clip. La prima clip diventa il video di output del passaggio,
  così un passaggio `VideoAnalyze` successivo può usarla con `{"kind": "PreviousStepOutput"}`.
- `"planSource": "StepRef"` — crea una clip per ogni inquadratura pianificata da un passaggio `ShotDirector`
  (`plan`), che il passaggio di montaggio inserisce (`generatedClips`).

| Impostazione | Predefinito | Cosa fa |
|---|---|---|
| `maxSpendCredits` | `0` (da impostare) | Tetto di spesa rigido per questo passaggio, in crediti. Deve essere superiore a 0 per un servizio a pagamento. |
| `maxGpuMinutes` | `0` | Tetto rigido al tempo di GPU per un provider ComfyUI self-hosted, che non fattura nulla. Deve essere superiore a 0 per ComfyUI; ignorato dai servizi a pagamento. |
| `model` | `"MiniMax-H3"` | Oppure `"MiniMax-H3-Max"`; per ComfyUI esattamente `"wan2.2-ti2v-5b-t2v"` o `"wan2.2-ti2v-5b-i2v"`. |
| `resolution` | `"768P"` | `MiniMax-H3`: `768P` o `2K`. `MiniMax-H3-Max`: `480P` o `768P`. |
| `durationSeconds` | `6` | Durata della clip per i prompt inline. |
| `ratio` | `"16:9"` | Uno tra `21:9`, `16:9`, `4:3`, `1:1`, `3:4`, `9:16`. |
| `takes` / `maxClips` | `1` / `1` | Clip per prompt o inquadratura / massimo di clip in totale (entrambi da 1 a 64; decidi tu, i tuoi crediti e `maxSpendCredits` sono il vero limite). |
| `allowSourceMediaEgress` | `false` | Permesso di inviare al servizio fotogrammi dei tuoi filmati. Senza, un piano di inquadrature che indica un `firstFrame`/`lastFrame` viene rifiutato (`EGRESS_REFUSED`); con il permesso, ReelBolt ritaglia quel singolo fotogramma dall'inquadratura e lo invia con la richiesta della clip. |
| `loopBackBehavior` | `"Reuse"` | Nei nuovi tentativi del ciclo di revisione, riutilizza le clip (`"Reuse"`) o ne paga di nuove (`"Regenerate"`). |
| `lookMatching` | `false` | Solo in modalità piano: dice al servizio di adattarsi al colore e al contrasto che ReelBolt ha misurato nell'inquadratura reale accanto a cui sta ogni clip. |
| `cameraContinuity` | `false` | Solo in modalità piano: quando il regista delle inquadrature ha lasciato vuota la parola per la camera, copia il movimento di camera misurato nell'inquadratura reale (fisso o a mano). |
| `firstFrame` / `lastFrame` | nessuno | Fa iniziare (e facoltativamente finire) la clip da un'immagine creata da un passaggio precedente, come **Cattura un fotogramma** (**Primo fotogramma dal passaggio** / **Ultimo fotogramma dal passaggio**, sotto **Avanzate**). Solo per prompt scritti, solo per i modelli che accettano un'immagine e solo con `allowSourceMediaEgress` attivo. |
| `conformFps` / `conformHeight` | `0` / `0` | Porta una clip più lenta o più piccola alla frequenza di fotogrammi / altezza del tuo programma dopo la consegna. `0` tiene la clip com'è stata consegnata; una clip già pari o superiore all'obiettivo non viene toccata. |
| `avatarId` | nessuno | Solo per prompt scritti: mostra in ogni clip uno degli [avatar](../projects/avatars.md#making-a-clip-with-an-avatar) del progetto, una persona o un oggetto (la sezione **Avatar** del passaggio). |
| `subjects` / `subjectMode` | nessuno / `"Auto"` | Solo per prompt scritti: mostra fino a tre avatar del progetto, al massimo una persona (`[{"avatarId": "..."}]`, mai insieme ad `avatarId` o a un primo o ultimo fotogramma). `"References"` invia le loro immagini a un modello che accetta immagini di riferimento, `"FirstFrame"` fa partire la clip da un'immagine che li mostra nella scena, `"Auto"` sceglie ciò che il modello supporta. Richiede `allowSourceMediaEgress`. |
| `plannedSubjects` / `plannedSubjectIds` | `"Auto"` / nessuno | Solo in modalità piano: se il regista delle inquadrature può mostrare gli avatar del progetto nelle inquadrature che pianifica (`"Off"` mai), e quali (nessuno = ogni avatar pronto). Solo con `allowSourceMediaEgress` attivo; vedi le [inquadrature pianificate](../projects/avatars.md#avatars-and-objects-in-planned-shots). |

```json
{"planSource": "Inline", "inlinePrompt": "Slow aerial shot over a misty pine forest at sunrise",
 "durationSeconds": 6, "takes": 1, "maxClips": 1, "maxSpendCredits": 1.0}
```

Prima che venga acquistato qualcosa, ReelBolt controlla questo tetto più i tetti giornalieri per progetto e per l'intera
piattaforma; se ne verrebbe superato uno, non viene acquistato nulla e il passaggio fallisce con
`BUDGET_EXCEEDED`. Altri errori: `CONFIG_INVALID`, `INVALID_PROMPT`, `EGRESS_REFUSED`,
`POLL_TIMEOUT`, e `AVATAR_NOT_SUPPORTED_BY_MODEL` quando il passaggio mostra un avatar su un modello che
non accetta immagini (il messaggio indica un modello della stessa famiglia che le accetta). Le clip identiche precedenti vengono riutilizzate gratis, a meno che tu non scelga di rigenerarle. Vedi
la [ricetta sul b-roll generato](../recipes/generated-b-roll.md).

## Passaggio Cattura un fotogramma (`ExtractFrame`) {#grab-a-frame-step-extractframe}

Un passaggio **Cattura un fotogramma** salva un fotogramma di un video come immagine. Usalo per tenere l'ultimo fotogramma di una
clip come inizio della successiva, per creare un'immagine di copertina o per riutilizzare un fermo immagine altrove nel
flusso di lavoro. Non usa l'AI ed è gratuito.

Nel costruttore:

- **Prendi il fotogramma da** — **Un file video caricato**, **Il risultato del passaggio precedente**, **Il
  risultato di un passaggio specifico** o **Il risultato di un'esecuzione precedente (ID del risultato del passaggio)**. L'ID è mostrato
  nella pagina dell'esecuzione e deve appartenere a questo progetto.
- **Fotogramma** — **L'ultimo fotogramma**, **Il primo fotogramma** o **Un momento a scelta** (poi **Momento
  (secondi)**, contato dall'inizio del video).
- **Formato** — **PNG** (nitido, file più grande) o **JPEG** (file più piccolo).
- **Lato più lungo (pixel)** — da 16 a 8192; se vuoto mantiene la dimensione del video. Un numero più piccolo
  riduce soltanto l'immagine, non la ingrandisce mai.
- **Salva nei file del progetto** — attivo per impostazione predefinita; elenca l'immagine in File così puoi scaricarla e
  riutilizzarla nelle esecuzioni successive.

L'immagine è il risultato del passaggio, quindi un passaggio `VideoGenerate` successivo può partire da essa (vedi
`firstFrame` sopra). Errori: `FRAME_OUT_OF_RANGE` (il momento è oltre la fine del video),
`SOURCE_NOT_FOUND` o `SOURCE_UNRESOLVED` (il video non si trova), `FRAME_EXTRACTION_FAILED`.

- **Usa l'AI:** no. **Nuovi tentativi:** nessuno. **Riutilizzo nelle esecuzioni successive:** sì, quando il video è lo stesso.

## Passaggio Genera musica o suoni (`AudioGenerate`) {#generate-music-or-sound-step-audiogenerate}

Un passaggio **Genera musica o suoni** crea nuova musica o un effetto sonoro da una descrizione scritta e
aggiunge ogni risultato ai file del progetto come file audio. Il passaggio di montaggio lo propone poi come qualsiasi
file audio che hai caricato (vedi [Aggiungere musica, effetti sonori e color grading](../recipes/music-sfx-and-grade.md)).
Non ragiona da sé; invia la tua descrizione a un servizio audio e raccoglie il
risultato. Dove il tuo piano include i contenuti generati, è a pagamento per ogni risultato e conta negli stessi
limiti giornalieri di **Genera video**.

Nel costruttore:

- **Tipo** — **Musica** o **Effetto sonoro**.
- **Descrizione** — fino a 4000 caratteri. Per la musica: stile, atmosfera, strumenti e ritmo. Per un
  effetto sonoro: il suono stesso, per esempio "pesante porta di legno che si apre cigolando, poi un tonfo leggero".
- **Durata (secondi)** — musica da 3 a 300, effetti sonori da 0,5 a 30. Il provider che scegli può consentire
  un intervallo più stretto, e il passaggio te lo dice prima che venga speso qualsiasi cosa.
- **Solo strumentale (senza voce)** per la musica; **Rendilo un loop continuo** e **Quanto seguire
  la descrizione (da 0 a 1)** per gli effetti sonori, dove il provider li supporta.
- **Provider** — se vuoto usa il tuo provider predefinito di musica o effetti sonori.
- **Take** — da 1 a 64 versioni separate tra cui scegliere; ognuna si paga. Il costo e il blocco anticipato crescono con il numero, e molte clip richiedono più tempo (8 clip, 2 alla volta, sono circa 20-40 minuti).
- **Limite di spesa (crediti)** — ferma il passaggio prima che spenda più di questo valore. Se vuoto, consente il prezzo più un margine per un nuovo tentativo. Un'immagine persa prima di raggiungerti non ti viene addebitata, ma conta per questo limite. Se vuoto si applicano solo
  i tuoi limiti giornalieri.
- **Salva nei file del progetto** — attivo per impostazione predefinita, così puoi ascoltare, scaricare e riutilizzare ogni take.
- Sotto **Avanzate**: uno specifico **Modello**, un **Piano di composizione (JSON)** per la musica al posto di una
  descrizione e un **Prefisso del nome file**.

Prima che venga acquistato qualcosa, ReelBolt calcola il prezzo della richiesta e controlla il tuo limite e i limiti giornalieri;
se ne verrebbe superato uno, non viene acquistato nulla e il passaggio fallisce con `BUDGET_EXCEEDED`. Altri
errori: `NO_AUDIO_PROVIDER` (nessun provider è configurato per questo tipo di audio), `CONFIG_INVALID`,
`PLAN_NOT_ENTITLED` e `AUDIO_BAD_REQUEST` (il provider ha rifiutato la descrizione; il suo suggerimento
è nel risultato del passaggio).

- **Usa l'AI:** no. **Nuovi tentativi:** un passaggio fallito non acquista mai due volte la stessa take. **Riutilizzo nelle
  esecuzioni successive:** una richiesta identica nello stesso progetto viene servita gratis dal risultato precedente.

## Passaggio Genera immagini (`ImageGenerate`) {#generate-pictures-step-imagegenerate}

Un passaggio **Genera immagini** crea nuove immagini da una descrizione scritta, oppure modifica
un'immagine che hai già seguendo un'istruzione. Aggiunge ogni risultato ai file del progetto come
immagine. Non ragiona da solo: invia il tuo testo a un servizio di immagini e raccoglie il risultato.
Si paga per ogni immagine.

Nel builder:

- **Modalità** — **Genera** crea nuove immagini da una descrizione. **Modifica** cambia un'immagine e può crearne fino a quattro versioni.
- **Descrizione** (Genera) o **Istruzione** (Modifica) — fino a 4000 caratteri.
- **Immagine da modificare** (Modifica) — un file del progetto, oppure un'immagine di un passaggio
  precedente.
- **Consenti l'invio dell'immagine al servizio AI** (Modifica) — deve essere attivo. Modificare carica la
  tua immagine su un servizio esterno, quindi il passaggio si ferma senza inviare nulla finché è spento.
- **Numero di immagini** — da 1 a 4, per Genera e Modifica. Ognuna si paga.
- **Dimensione** — una forma come 16:9 o 1:1, oppure larghezza e altezza esatte in pixel (non entrambe).
- **Dimensione esatta** — larghezza e altezza finali in pixel (da 64 a 8192 ciascuna), per Genera e Modifica, per esempio 2000 x 2000. ReelBolt chiede al modello il livello di risoluzione più piccolo e la forma più vicina che la coprono, poi ridimensiona e ritaglia al centro l'immagine esattamente a quella dimensione, quindi il risultato ha sempre la dimensione che hai scelto. Sostituisce Dimensione (non entrambe). Se l'immagine del modello torna più piccola della dimensione richiesta, viene comunque ingrandita e il passaggio mostra un avviso `image_upscaled`; se il ridimensionamento stesso fallisce ricevi l'immagine originale del modello e un avviso `resize_failed`. Il prezzo segue il livello usato.
- **Risoluzione** — 1K, 2K o 4K, per i modelli con prezzo a risoluzione (Nano Banana). Lasciala vuota per far scegliere alla dimensione esatta il livello più piccolo che la copre. Un livello troppo piccolo per la dimensione esatta viene rifiutato quando salvi.
- **Provider** — se vuoto usa il provider di immagini predefinito.
- **Limite di spesa (crediti)** — ferma il passaggio prima che spenda più di questo valore. Se vuoto, consente il prezzo più un margine per un nuovo tentativo. Un'immagine che si perde prima di arrivare a te non ti viene addebitata, ma conta per questo limite.
- **Salva nei file del progetto** — attivo per impostazione predefinita.
- In **Avanzate**: un **Prefisso del nome file**.

Prima di acquistare qualsiasi cosa, ReelBolt calcola il prezzo della richiesta e controlla il tuo limite.
Errori che puoi vedere: `NO_IMAGE_PROVIDER` (nessun provider di immagini configurato), `EGRESS_REFUSED`
(una modifica senza il permesso di inviare l'immagine), `CONTENT_BLOCKED` (il testo è stato rifiutato dal
controllo di sicurezza), `BUDGET_EXCEEDED` e `CONFIG_INVALID`.

- **Usa l'AI:** no. **Nuovi tentativi:** un passaggio fallito non acquista mai due volte la stessa
  immagine. **Riutilizzo nelle esecuzioni successive:** una richiesta identica nello stesso progetto riusa
  gratuitamente l'immagine precedente, ma il passaggio in sé non viene messo in cache.

## Passaggio Metti sugli schermi (`PinInsert`) {#put-on-screens-step-pininsert}

Un passaggio **Metti sugli schermi** decide cosa appare sugli schermi tracciati dei tuoi filmati (un telefono o un
laptop che mostra uno schermo verde, blu o magenta), senza chiedere a un'AI di farlo. Legge gli
schermi trovati da un passaggio **Analizza video** e passa il piano a un passaggio **Monta video** successivo, che
disegna il contenuto sugli schermi. Non usa l'AI ed è gratuito.

Nel costruttore:

- **Schermi trovati da** — il passaggio **Analizza video** che ha tracciato gli schermi, oppure **Il passaggio
  precedente**.
- Per ogni contenuto:
  - **Schermo** — l'id di uno schermo, come `r0`, come elencato nel risultato del passaggio di analisi. Lascialo
    vuoto per usare il contenuto su ogni schermo che nessun'altra voce nomina.
  - **Contenuto** — **Il video o l'immagine di un passaggio precedente** (per esempio l'animazione che un agente Author
    ha generato), **Un video o un'immagine caricati** oppure **Il risultato di un'esecuzione precedente (ID del risultato
    del passaggio)**, che deve appartenere a questo progetto.
- **Aggiungi contenuto per un altro schermo** — fino a 16 voci. Solo una voce può lasciare **Schermo** vuoto.
- **Fermati se qualcosa non può essere messo su uno schermo** — attivo per impostazione predefinita: il passaggio si ferma con
  `PIN_INSERT_UNRESOLVED` e dice quale voce è fallita e perché (uno schermo che l'analisi non ha trovato,
  un contenuto che non si trova o non è un video o un'immagine). Disattivalo per usare ciò che funziona; il
  risultato del passaggio elenca il resto sotto `pinInsert.skipped`.

Poi, nel passaggio **Monta video**, attiva gli inserti schermo e imposta il suo piano grafico su questo passaggio
(`"graphicsPlan": {"from": "Step", "stepOrder": N}`, `"enableInserts": true`). Se l'analisi non ha trovato
schermi, il passaggio si ferma con `PIN_INSERT_NO_REGIONS`: attiva il rilevamento del green screen nel
passaggio di analisi e controlla che lo schermo sia ben visibile.

- **Usa l'AI:** no. **Nuovi tentativi:** nessuno. **Riutilizzo nelle esecuzioni successive:** sì, quando i suoi input sono gli stessi.

## Passaggio Voiceover (`Voiceover`) {#voiceover-step-voiceover}

Un passaggio `Voiceover` trasforma righe scritte in audio parlato con un servizio vocale (Fish Audio, o un
server vocale locale compatibile con OpenAI, configurato da un amministratore). Non decide cosa dire.

- `source` — uno di quattro tipi:
  - `{"kind": "Inline", "lines": [...]}` con righe che scrivi tu, ciascuna con `text`, `startSec`
    (secondi sull'orologio del video **originale**) e un `voiceId` facoltativo;
  - `{"kind": "ScriptScenes", "scriptwriterStepOrder": 8}` per leggere la voce narrante che un
    passaggio `ScriptwriterAgent` ha scritto, scena per scena;
  - `{"kind": "NarrationPlan", "narrationWriterStepOrder": 3}` per leggere le battute che un
    passaggio `NarrationWriter` (o `NarrationTranslator`) ha pianificato sul montaggio;
  - `{"kind": "PickupPlan", "pickupPlannerStepOrder": 3}` per registrare di nuovo le frasi che un
    passaggio `PickupPlanner` ha indicato.
- `defaultVoiceId` — la voce del provider da usare quando una riga non ne ha una.
- `projectVoiceId` — una voce della scheda **Voci** del progetto, al suo posto. Una voce clonata è utilizzabile
  solo finché il suo consenso registrato è valido; altrimenti il passaggio **fallisce** invece di parlare
  con un'altra voce.
- `voiceCasting` — `Off` (predefinito) o `Decision`: lascia che il modello decisionale scelga una delle voci utilizzabili del progetto
  in base alle loro descrizioni.
- `pronunciationDictionary` — `Off` (predefinito) o `Auto`: dice a Fish Audio come pronunciare i nomi di prodotti
  e librerie trovati dall'analisi del codice (da un elenco fisso che ReelBolt sa come sillabare).
- `language` — una nota registrata sul risultato (il servizio vocale capisce la lingua dal
  testo).
- `providerId`, `model` — sostituzioni facoltative.

```json
{"source": {"kind": "Inline", "lines": [{"text": "Welcome to the tour.", "startSec": 0}]},
 "defaultVoiceId": "YOUR-FISH-AUDIO-VOICE-ID"}
```

**Avvisi nel costruttore.** Le impostazioni del passaggio controllano il servizio vocale che un'esecuzione userebbe in
questo momento. Se ha fallito l'ultimo test di connessione, un riquadro rosso dice **La voce narrante probabilmente fallirà**; se al suo posto verrà usato
un servizio di riserva funzionante, un riquadro giallo dice **Uso di un servizio vocale di riserva**. Sistemare il
servizio è un compito da amministratore (vedi [Provider di inferenza](../admin/inference-providers.md)). Sotto
**Voce**, scegli una delle voci del progetto, oppure segui il link alla scheda **Voci** del progetto per
aggiungerne una; il modello, l'ID della voce, il limite di dimensione, l'aiuto per la pronuncia e l'assegnazione automatica delle voci sono sotto
**Avanzate**.

Per sentirla in un video montato, attiva `enableVoiceover` e imposta `voiceoverStepOrder` nel passaggio
`VideoCompile`; aggiungi `enableCaptions` per vederla anche scritta. In un promo, l'`AuthorAgent` prende
l'audio da solo e dimensiona ogni scena narrata sulla durata misurata della sua battuta. Vedi la
[ricetta sulla voce narrante](../recipes/voiceover.md).

- **Nuovi tentativi:** fino al normale numero di tentativi. **Riutilizzo nelle esecuzioni successive:** sì.

## Passaggio Esporta la timeline (`TimelineRender`) {#render-timeline-step-timelinerender}

Un passaggio `TimelineRender` trasforma una delle **timeline dell'editor** del progetto (vedi
[L'editor timeline](../editor/index.md)) in un file video finito. È un'elaborazione semplice: il montaggio,
i livelli, il testo e i livelli audio vengono tutti dal documento della timeline, e il passaggio sceglie solo
il formato di consegna. Nulla della timeline cambia. L'area di lavoro **Consegna** dell'editor
crea ed esegue questo passaggio per te; lo aggiungi a un flusso di lavoro tuo quando un render deve far parte
di una pipeline più lunga. L'editor video è disponibile come anteprima anticipata, quindi le timeline dell'editor di un progetto si possono esportare con
questo passaggio.

- `editTimelineId` — la timeline da esportare. Obbligatorio. Il costruttore offre un selettore tra le
  timeline del progetto.
- `revision` — quale versione salvata esportare; se vuoto esporta l'ultima quando il passaggio parte, e l'
  esecuzione registra la versione usata.
- `range` — `{"inFrame": 0, "outFrame": 300}` per esportare una parte della timeline, in fotogrammi; se vuoto
  la esporta tutta.
- `preset` — `Hd1080p16x9` (predefinito), `Uhd4k16x9`, `Vertical9x16`, `Square1x1`, `Portrait4x5`,
  `Master` (la dimensione della timeline alla massima qualità) o `AudioOnly`. Il render resta entro
  la risoluzione massima del tuo piano: un preset più grande di quanto il tuo piano consente viene ridotto fino al
  limite, con la stessa forma e qualità. Nell'area di lavoro Consegna, `Uhd4k16x9` (o un
  `width`/`height` esplicito oltre il tuo piano) viene rifiutato quando il tuo piano non include quella dimensione.
- `encode` — rifiniture facoltative sul preset: `codec` (`H264`, `H265`), `crf` (0 migliore – 51
  più piccolo), `speed` (`Fast`, `Medium`, `Slow`), `audioBitrateKbps` (64–320), una coppia
  `width`/`height` pari e un controllo del bitrate (qui sotto). Qualsiasi valore fuori da questi intervalli viene rifiutato quando salvi.
  - `rateControl` — `Quality` (predefinito, aspetto costante; la dimensione del file dipende dal video), `Capped`
    (stesso aspetto, con il file vicino o sotto `videoBitrateKbps`) o `Average` (punta a `videoBitrateKbps`, quindi il file
    resta vicino a una dimensione prevedibile). `videoBitrateKbps` va da 200 a 100000 (100 Mbps) ed è
    obbligatorio con `Capped` e `Average`; `Quality` e un'esportazione solo audio lo rifiutano. Se lo ometti,
    l'esportazione è la stessa codifica di qualità di prima. Il prezzo di un'esportazione non cambia con il bitrate.
- `outputFileName` — un semplice nome di file (lettere, cifre, spazi, punti, trattini, trattini bassi).
- `registerProjectFile` — elenca il file anche in File del progetto (predefinito attivo).
- `loudness` — `{"targetLufs": -16, "truePeakDbtp": -1.5, "loudnessRange": 11}` per normalizzare il volume del
  programma; se vuoto lo misura senza cambiarlo.

```json
{"editTimelineId": "1b2c3d4e-5f60-4711-8a92-3c4d5e6f7001", "preset": "Vertical9x16",
 "outputFileName": "reel.mp4", "loudness": {"targetLufs": -16, "truePeakDbtp": -1.5, "loudnessRange": 11}}
```

Il passaggio esporta tutto ciò che l'editor sa esprimere: tagli e livelli, titoli, posizione di ogni clip,
scala, rotazione, opacità, ritaglio, dissolvenze, velocità, inversione e fermo immagine, valori animati, transizioni,
generatori di colore/barre/test, e i fader, il pan e l'equalizzatore del mixer — esattamente come li mostra l'editor.
Il passaggio esporta a parti e ricorda ogni parte in base a ciò che contiene, così esportare di nuovo la stessa timeline
dopo una piccola modifica rifà solo le parti cambiate. Il suo risultato elenca quante parti sono state
riutilizzate, il volume misurato e il file finito, che compare sotto **Risultati** come un video
montato.

- **Nuovi tentativi:** al massimo uno. **Riutilizzo nelle esecuzioni successive:** no — il passaggio mantiene una propria cache delle parti
  esportate, che è più fine del riutilizzo dell'intero risultato.
