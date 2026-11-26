---
title: Agenti
sidebar_position: 3
description: Tutti gli agenti AI integrati di ReelBolt, cosa fanno, quali passaggi li usano, cosa possono fare e se possono fare il render di video Remotion.
i18n_source_sha: fac9e1dc381116913bca1637a7f45058f5f17e40
---

# Agenti {#agents}

Un **agente** è un lavoratore AI con un solo compito, istruzioni fisse e un insieme fisso di
**strumenti** (cose che può fare oltre a scrivere testo). Inserisci un agente in un flusso di lavoro con
un passaggio `Agent` (oppure `ReviewLoop` o `ForEach`); i passaggi di tipo stanza usano più agenti insieme.

In questa pagina gli strumenti sono descritti a parole semplici:

- **Leggere i file del progetto** — guardare, cercare e leggere i file caricati nel progetto.
- **Scrivere i file del progetto** — salvare un nuovo file nel progetto.
- **Scrivere codice** — lavorare in uno spazio privato e isolato (la "sandbox"): creare e modificare
  codice Remotion, installare pacchetti e verificare che il codice si compili.
- **Fare il render con Remotion** — trasformare il codice Remotion in un vero file video e salvarlo.
- **Fermare l'esecuzione** — terminare l'intero flusso di lavoro con una spiegazione quando non può proseguire.

**Può fare il render con Remotion** significa che l'agente può produrre da solo un file video dal codice Remotion. Solo
tre agenti ci riescono: `AuthorAgent`, `MotionGraphicsPlanner` e `MotionGraphicsDirector`. Nessun altro
agente integrato e nessun agente personalizzato può farlo. Non esiste un tipo di passaggio "render" a parte:
il render avviene sempre all'interno dei passaggi di uno di questi agenti.

## Come la pagina Agenti mostra ogni agente {#how-the-agents-page-shows-each-agent}

La pagina **Agenti** elenca ogni agente in base a ciò che fa per il tuo video — per esempio "Legge come
è costruita la tua app" con la riga "Impara le schermate della tua app e come si collegano tra loro." — raggruppati
sotto titoli come **Capire la tua app** e **Montare i tuoi filmati**. Gli stessi nomi semplici dei compiti
compaiono sui passaggi del flusso di lavoro e nella pagina dell'esecuzione mentre un passaggio è in corso. Un agente personalizzato mantiene
il nome che gli hai dato.

I dettagli interni — il nome interno dell'agente (come `CodeStructureAnalyzer`, il nome che questa
guida usa più avanti), il suo tipo, i suoi strumenti e la forma di ciò che restituisce — si trovano sotto **Dettagli**
su ogni scheda e sotto **Dettagli tecnici** nella pagina dell'agente. Non ti servono per
creare o eseguire un flusso di lavoro.

## Quali agenti possono fare il render con Remotion {#which-agents-can-render-remotion}

L'elenco completo degli agenti che possono trasformare il codice Remotion in un video:

| Agente | Cosa genera | Dove va il suo render |
|---|---|---|
| `AuthorAgent` (mostrato come "Author") | L'intero video promozionale | Diventa il promo finale dell'esecuzione (il passaggio fallisce se non produce il render) |
| `MotionGraphicsPlanner` | Piccole grafiche animate (titoli, terzi inferiori, callout, sfondo trasparente) **e** le scene mostrate dentro gli inserti schermo tracciati (video normale, non trasparente) | Composte nei tuoi filmati dal passaggio `VideoCompile` |
| `MotionGraphicsDirector` | Come `MotionGraphicsPlanner`, dalla chiamata finale di un passaggio `GraphicsRoom` | Composte dal passaggio `VideoCompile` |

`RemotionComponentTranslator` scrive codice Remotion e verifica che si compili, ma **non può fare il render**.
Ogni altro agente, e ogni agente personalizzato, non può né scrivere codice né fare il render.

## Agenti di analisi del codice {#code-analysis-agents}

Questi cinque agenti leggono il codice sorgente di un'app web e lo descrivono per la pipeline dei promo. Tutti e cinque
possono solo **leggere i file del progetto** e **fermare l'esecuzione**; **possono fare il render con Remotion: no**. Sono usati nei
quattro template promo (`quick-win-promo`, `lean-context-promo`, `cinematic-feature-spotlight`,
`story-arc-launch-trailer`), non tutti in ogni template.

| Agente | Compito | Forma della risposta |
|---|---|---|
| `CodeStructureAnalyzer` | Mappa cartelle, moduli e punti di ingresso. | `CodeStructureOutput` |
| `DependencyAnalyzer` | Elenca framework e librerie. | `DependencyAnalysisOutput` |
| `ComponentInventoryAnalyzer` | Elenca i componenti dell'interfaccia, i loro input e il loro scopo. | `ComponentInventoryOutput` |
| `RouteAndApiAnalyzer` | Elenca pagine, navigazione ed endpoint API. | `RouteAndApiOutput` |
| `StyleAndThemeExtractor` | Estrae colori, font, spaziature e identità del brand. | `StyleAndThemeOutput` |

Poiché si limitano a leggere, un'esecuzione successiva identica può riutilizzare i loro risultati invece di interrogare di nuovo
l'AI.

## Agenti di produzione del promo {#promo-production-agents}

Questi agenti trasformano l'analisi del codice in un video promozionale generato. Sono usati in tutti e quattro i
template promo.

| Agente (nome mostrato) | Compito | Forma della risposta | Strumenti | Può fare il render con Remotion |
|---|---|---|---|---|
| `RemotionComponentTranslator` | Costruisce il progetto Remotion: scrive il codice delle scene nella sandbox e verifica che si compili. | `RemotionProjectBuildOutput` | Leggere e scrivere i file del progetto, scrivere codice (nessun render) | **No** |
| `AnimationStrategyAgent` ("AnimationStrategy") | Pianifica ordine, tempi e transizioni delle scene. | `AnimationStrategyOutput` | Leggere i file del progetto, guardare dentro la sandbox | No |
| `ScriptwriterAgent` ("Scriptwriter") | Scrive la voce narrante e i sottotitoli per ogni scena. | `ScriptwriterOutput` | Leggere i file del progetto, guardare dentro la sandbox | No |
| `DirectorAgent` ("Director") | Scrive la regia inquadratura per inquadratura. | `DirectorOutput` | Leggere i file del progetto, guardare dentro la sandbox | No |
| `AuthorAgent` ("Author") | Assembla tutto, genera il promo finale e lo salva. Se è stato eseguito un passaggio `Voiceover`, inserisce anche l'audio della voce narrante nelle scene; se è stato eseguito un passaggio `VideoGenerate`, può usare le clip generate come sfondi. | `RenderManifestOutput` | Leggere e scrivere i file del progetto, scrivere codice, fare il render | **Sì** |
| `ReviewAgent` ("Review") | Dà al promo un voto su 10 ed elenca i miglioramenti; usato dai passaggi `ReviewLoop`. | `ReviewOutput` | Leggere i file del progetto, guardare dentro la sandbox, controllare il codice | No |

Un passaggio `AuthorAgent` **fallisce** se termina senza aver generato un video.

## Agenti decisionali per il montaggio video {#video-editing-decision-agents}

Questi agenti lavorano sui tuoi filmati girati. Scelgono tra gli id brevi che un passaggio `VideoAnalyze`
ha proposto (inquadrature `s0`, pause `g0`, parlato `t0`, ...) e non scrivono mai un tempo, una posizione o un
numero di loro invenzione. Possono tutti solo **leggere i file del progetto** e **fermare l'esecuzione**; **possono fare il render
Remotion: no**.

`VideoStoryEditor` e `SoundDesigner` hanno uno strumento in più: possono **chiedere che cosa mostra un momento**.
Quando il passaggio di analisi descrive i momenti (`describeEvents`) e un momento non ha ancora una descrizione,
l'agente può chiedere al modello di visione che cosa mostra quel solo momento e riceve poche parole. Ogni
domanda usa alcuni crediti, quindi un agente può chiedere al massimo 6 volte in un passaggio e 20 volte in
un'esecuzione. La risposta è letta come una descrizione del tuo video, mai come un'istruzione.

| Agente | Compito | Forma della risposta | Usato da |
|---|---|---|---|
| `VideoStoryEditor` | Decide quali inquadrature, pause e frasi parlate tenere. | `VideoEditDecisionOutput` | Passaggio 2 di ogni template per filmati tranne `video-derush-edit-room`; ripiego per `EditRoom` |
| `VideoEditDirector` | Modera un panel `EditRoom` e scrive la sua decisione finale su cosa tenere. | `VideoEditDecisionOutput` | Passaggi `EditRoom` |
| `MusicSupervisor` | Sceglie al massimo una traccia musicale caricata (`m0`, ...) più il volume (`Quiet`, `Balanced`, `Feature`), quanto abbassarla sotto il parlato (`Off`, `Light`, `Normal`, `Heavy`) e se va in loop (`LoopToFit`, `PlayOnce`). | `MusicPlanOutput` | `video-derush-edit-music` |
| `SoundDesigner` | Pianifica gli effetti sonori: quale clip caricata (`x0`, ...) parte quando inizia quale inquadratura, pausa o frase, con tempistica (`OnCut`, `Lead`, `Lag`) e volume (`Subtle`, `Normal`, `Strong`). Nessun effetto è una risposta valida. | `SfxPlanOutput` | `video-derush-edit-sfx` |
| `Colorist` | Sceglie un unico look di colore per tutto il video, a parole: look (`None`, `Warm`, `Cool`, `Filmic`, `Vibrant`, `Muted`, `Mono`), intensità, parole per ombre e luci. | `ColorGradePlanOutput` | Ripiego per `ColorGradeRoom`; utilizzabile come passaggio a sé |
| `ColorGradeDirector` | Modera un panel `ColorGradeRoom` e scrive il look finale. | `ColorGradePlanOutput` | Passaggi `ColorGradeRoom` |
| `ShotDirector` | Pianifica le clip generate dall'AI: scopo (`Cutaway`, `ColdOpen`, `EndCard`, `SeamBridge`, `Extend`, `ScreenContent`), dove vanno, parole per camera e durata, e un prompt di descrizione. Non può spendere denaro da solo. | `GeneratedShotPlanOutput` | `video-derush-edit-broll`, `video-derush-edit-dailies` |
| `TakeSelector` | Un giudice in un panel `DailiesRoom`: confronta le take generate dall'AI per ogni inquadratura pianificata usando i dati misurati su di esse. È anche il ripiego quando il panel fallisce. | `TakeSelectionOutput` | Passaggi `DailiesRoom` |
| `DailiesDirector` | Modera un panel `DailiesRoom` e scrive la sua scelta finale: al massimo una take per inquadratura, tramite l'id della take. | `TakeSelectionOutput` | Passaggi `DailiesRoom` |
| `DynamicsPlanner` | Pianifica i movimenti a tempo del Montaggio dinamico dopo la generazione: vede gli id delle inquadrature sorgente e dei clip generati utilizzabili (`v1t1`, ...), così può alternare un'inquadratura con un clip generato a tempo. Solo parole e id offerti, mai un tempo. Zero movimenti è una risposta valida. | `DynamicsPlanOutput` | `video-derush-edit-broll`, `video-derush-edit-dailies` |
| `NarrationWriter` | Scrive le battute di voce narrante per il montaggio, ognuna legata a un'inquadratura, una pausa o una frase parlata (`s0`, `g0`, `t0`, ...), mai a un tempo. Zero battute è una risposta valida. | `NarrationPlanOutput` | `video-derush-edit-voiceover`, `video-derush-edit-dub` |
| `NarrationTranslator` | Riscrive un piano di voce narrante nella lingua indicata nella richiesta dell'esecuzione: stesse battute, stessi ancoraggi, parole tradotte. | `NarrationPlanOutput` | `video-derush-edit-dub` |
| `PickupPlanner` | Indica le frasi parlate (`t0`, ...) in cui chi presenta ha sbagliato e la frase corretta per ciascuna. Zero pickup è una risposta valida. | `PickupPlanOutput` | `video-derush-edit-pickups` |
| `VideoReviewAgent` (mostrato come "VideoReview") | Dà un voto a un montaggio compilato usando controlli che ReelBolt ha misurato (frasi non tagliate a metà, raccordi fluidi, ritmo, copertura delle grafiche, margine per la musica, e quanto forte e quanto chiaramente si sentiva la voce narrante). | `VideoReviewOutput` | Il passaggio `ReviewLoop` di ogni template per filmati |

## Agenti di motion graphics (questi generano) {#motion-graphics-agents-these-render}

I due agenti di motion graphics aggiungono grafiche progettate e inserti schermo ai filmati girati. Entrambi
possono **leggere i file del progetto, scrivere codice, fare il render con Remotion e fermare l'esecuzione** (non "scrivere i file
del progetto"). **Possono fare il render con Remotion: sì.**

**`MotionGraphicsPlanner`** (forma della risposta `MotionGraphicsPlanOutput`). Usato dal passaggio 3 di
`video-derush-edit-graphics` e come ripiego per un passaggio `GraphicsRoom`. La sua risposta ha due
elenchi:

- `overlays` — titoli, terzi inferiori, callout e tag, ciascuno su uno spazio grafico proposto
  (`placementId` come `p2`), con una parola di durata (`Short`, `Medium`, `Hold`). Per impostazione predefinita ogni
  overlay è una vera animazione che l'agente scrive e genera da sé con sfondo trasparente,
  e restituisce come `renderedAssetStorageKey`; il semplice testo su un riquadro è il ripiego.
- `inserts` — **inserti schermo tracciati**: per ogni regione green screen proposta (`regionId` come
  `r0`), una scena che l'agente scrive e genera da sé come video normale e opaco, restituita come
  `renderedAssetStorageKey`, più un `reason`. Il passaggio `VideoCompile` poi fissa quella scena nello
  schermo in movimento fotogramma per fotogramma. Un insert **deve** avere una scena generata della stessa esecuzione;
  non c'è un ripiego testuale.

**`MotionGraphicsDirector`** (stessa forma della risposta). Modera un panel `GraphicsRoom`. Durante la
discussione del panel può solo leggere; nella chiamata finale scrive il piano e genera le
grafiche e le scene degli insert, esattamente come il planner.

Di' a questi agenti cosa mostrare tramite il brief dell'esecuzione (il riquadro **Il tuo brief** quando fai clic su
**Avvia**), per esempio "Sullo schermo del telefono, mostra la schermata di onboarding della nostra app
con il logo che compare in animazione". Vedi [Ricetta: inserto schermo tracciato](../recipes/tracked-screen-insert.md).

Questi agenti sono i più lenti della pipeline video, perché scrivere, controllare e fare il render del codice
richiede molti passaggi. Non vengono mai riutilizzati da un'esecuzione precedente.

## Agenti segnaposto (non AI) {#placeholder-agents-not-ai}

Due agenti integrati esistono solo per occupare lo spazio dell'agente nei passaggi non AI e **non vengono mai eseguiti come
AI**:

- `ExtractTransform` — usato dai passaggi `Extract`.
- `VideoTransform` — usato dai passaggi `VideoAnalyze`, `VideoCompile`, `VideoGenerate`, `Voiceover`,
  `EditRoom`, `GraphicsRoom`, `ColorGradeRoom` e `DailiesRoom`. Per i quattro passaggi stanza,
  i veri membri AI del panel vengono dalle impostazioni della stanza, non da questo spazio.

Non hanno strumenti. **Possono fare il render con Remotion: no.**

## Competenze: conoscenza di Remotion e gusto del design {#skills-remotion-knowledge-and-design-taste}

Una **competenza** è un documento di riferimento che un agente può aprire mentre lavora. È conoscenza, non uno strumento:
non permette all'agente di fare nulla di nuovo. A ogni agente vengono dati i nomi delle competenze che ha e
ne apre una solo quando il compito lo richiede. Gli amministratori possono vedere ogni competenza, quali agenti la usano e
da dove viene nella pagina **Competenze** sotto **Piattaforma**.

Esistono due tipi di competenze integrate:

- Le **competenze Remotion** (`remotion-create`, `remotion-markup`, `remotion-render`, `remotion-captions`,
  `remotion-multimedia`) sono guide di lavoro per scrivere e fare il render di codice video Remotion: composizioni,
  tempi, audio, sottotitoli, media e impostazioni di render.
- Le **competenze di gusto del design** (`taste-skill`, `minimalist-skill`, `brutalist-skill`) aiutano gli agenti che
  decidono o scrivono l'aspetto di un video a evitare il look generico da "template AI": scelta di font e colori,
  un linguaggio di forme coerente, testi a schermo semplici e movimento che ha una ragione. Vengono dalla
  raccolta open source Taste Skill (licenza MIT, github.com/Leonxlnx/taste-skill), che è
  scritta per le pagine web. Ognuna si apre con una breve nota di ReelBolt che dice quali parti valgono per il
  video, seguita dal testo originale invariato. `taste-skill` è quella generale; le altre due
  descrivono un look specifico e sono usate solo quando il tuo brief chiede uno stile minimalista o brutalista.

Quali agenti integrati hanno le competenze di gusto del design:

| Agente | Competenze che riceve | Perché |
|---|---|---|
| `DirectorAgent` | tutte e tre le competenze di gusto | Definisce l'aspetto e il tono del video e trasforma uno stile indicato nel brief in regia. |
| `AuthorAgent` | tutte e tre le competenze di gusto, più le cinque competenze Remotion | Scrive il codice del video finale, quindi applica il look. |
| `RemotionComponentTranslator` | `taste-skill`, più due competenze Remotion | Sceglie caratteri e colori dei componenti che scrive. Il brand della tua app ha sempre la precedenza. |
| `AnimationStrategyAgent` | `taste-skill`, più una competenza Remotion | Movimento con una ragione e ritmo sensato. |
| `MotionGraphicsPlanner`, `MotionGraphicsDirector` | `taste-skill`, più due competenze Remotion | Carattere, colore e parole di titoli e terzi inferiori. |

Il tuo brief, le tue scelte di stile e i colori e i font del tuo brand hanno sempre la priorità su una competenza.
Gli agenti che prendono solo decisioni di montaggio, suono, color grading o copione non hanno le competenze
di gusto del design.

## Agenti personalizzati {#custom-agents}

Un **agente personalizzato** è un agente che crei tu nella pagina **Agenti**: gli dai un nome, una
descrizione e un **prompt di sistema** (le sue istruzioni permanenti). Gli agenti integrati sono di sola lettura;
quelli personalizzati si possono modificare ed eliminare. Un agente personalizzato usato da un'esecuzione passata non si può eliminare finché
quell'esecuzione esiste, perché l'esecuzione conserva i passaggi con cui è stata eseguita: la pagina **Agenti** ti dice quante esecuzioni
lo usano ancora.

**Limitazione importante: gli agenti personalizzati non vengono eseguiti nei flussi di lavoro.** Puoi
creare, modificare e aggiungere un agente personalizzato a un passaggio `Agent`, `ForEach` o `Parallel`, ma il motore
dei flussi di lavoro non ha un runner per gli agenti personalizzati, quindi quando l'esecuzione arriva a quel passaggio viene contrassegnata come
**Saltato** ("No agent registered ...") e passa al passaggio successivo senza fare
nulla. Usa un agente integrato per qualsiasi lavoro che ti serve e guidalo con il testo di richiesta dell'esecuzione
(vedi sotto).

Cosa include un agente personalizzato:

- **Competenze:** puoi collegargli qualsiasi competenza integrata descritta sopra — le cinque di Remotion
  e le tre di gusto del design. Una competenza è
  conoscenza di riferimento, non uno strumento, e non concede nessuna nuova capacità. (Poiché gli agenti personalizzati non
  vengono eseguiti nei flussi di lavoro, le competenze collegate lì non hanno effetto.)
- **Quale modello AI usa:** un amministratore può dare a qualsiasi agente, integrato o personalizzato, un proprio provider AI
  (la sostituzione del provider per agente nella pagina dell'agente). Altrimenti si usa il provider di chat
  predefinito della piattaforma. Per gli agenti integrati questa sostituzione ha effetto.
- **Gli strumenti che ha:** solo "leggere i file del progetto" e "fermare l'esecuzione". Un agente personalizzato non può mai
  scrivere codice, scrivere file del progetto o **fare il render con Remotion**, qualunque cosa dica il suo prompt.
- **Forma della risposta:** testo libero, quindi un agente personalizzato non può sostituire un
  agente integrato la cui risposta è letta da un altro passaggio (per esempio il `decision` di un passaggio di compilazione o
  `graphicsPlan`).

## Cambiare il comportamento di un agente integrato {#changing-a-built-in-agents-behaviour}

Non puoi modificare il prompt di un agente integrato dalla pagina Agenti. Modi per guidarne uno:

- Dai istruzioni nel brief dell'esecuzione (il riquadro **Il tuo brief** che si apre quando fai clic su **Avvia**).
  Ogni agente AI dell'esecuzione lo legge.
- Scegli una modalità di contesto diversa sul suo passaggio (quali risultati precedenti vede).
- Chiedi a un amministratore di assegnargli un provider AI diverso.
- Un operatore può sostituire il prompt di qualsiasi agente integrato nella configurazione del server
  (`Agents:<AgentName>:SystemPrompt`).
