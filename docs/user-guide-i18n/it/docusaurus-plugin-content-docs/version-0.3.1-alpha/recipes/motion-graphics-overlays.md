---
title: Aggiungi titoli, sottopancia e callout (motion graphics)
sidebar_position: 6
description: Ricetta per aggiungere titoli animati, didascalie con i nomi e callout al girato, progettati e generati dall'AI di motion graphics di ReelBolt.
i18n_source_sha: 69eb5eed5870c3d8a0ffc6568ad59b9c95919bef
---

# Aggiungi titoli, sottopancia e callout (motion graphics) {#add-titles-lower-thirds-and-callouts-motion-graphics}

**Obiettivo:** aggiungere grafiche a schermo a un video montato: un titolo all'inizio, una didascalia
"sottopancia" con il nome quando qualcuno viene presentato, callout per i punti chiave, posizionati dove non coprono volti
né parti affollate dell'immagine, nei momenti che corrispondono a ciò che viene detto.

L'AI di motion graphics di ReelBolt (`MotionGraphicsPlanner`) progetta ogni grafica come una piccola **animazione
Remotion che scrive e genera da sé**, con sfondo trasparente, e il passaggio
`VideoCompile` la sovrappone al girato. Un semplice testo su un riquadro semitrasparente è la soluzione di ripiego
quando un'animazione non è adatta o non riesce a essere generata.

## Prerequisiti per le motion graphics {#prerequisites-for-motion-graphics}

- Il video caricato nel progetto.
- Un provider di chat AI e il servizio sandbox di Remotion in esecuzione (installazione standard).
- Consigliato: un provider di trascrizione, così le grafiche si allineano a ciò che viene detto (per esempio un nome
  compare quando la persona viene presentata).

## Il flusso di lavoro delle motion graphics {#the-motion-graphics-workflow}

Aggiungi il template **Montaggio del girato con titoli e callout** (`video-derush-edit-graphics`) e seleziona il tuo
video nel passaggio 1.

| # | Etichetta | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analizza il video sorgente | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decidi quali tratti tenere | `Agent` | `VideoStoryEditor` |
| 3 | Pianifica le grafiche in sovrimpressione | `Agent` (contesto `FullWorkflow`) | `MotionGraphicsPlanner` |
| 4 | Compila il video montato con le grafiche | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 5 | Verifica la qualità del montaggio | `ReviewLoop` | `VideoReviewAgent` |

Il **passaggio 1** trova i punti adatti per le grafiche:

```json
{"version": 1, "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
 "emitOverlayPlacements": true}
```

Ogni punto viene offerto all'AI come un id (`p0`, `p1`, ...) con la sua area dello schermo (`LowerThird`,
`UpperThird` o `CenterBand`), un punteggio di idoneità, un suggerimento per testo chiaro o scuro e il momento a cui
appartiene. Le inquadrature lunghe e ferme ricevono più punti in momenti diversi (`maxTimeSlicesPerRegion`,
predefinito 3). Limiti: `maxPlacementsPerShot` 2, `maxPlacements` 40.

Il **passaggio 3** deve vedere sia il passaggio 1 (i punti) sia il passaggio 2 (cosa è stato tenuto), quindi il suo contesto è
`FullWorkflow`. Ogni punto è contrassegnato in base al fatto che il suo momento sia sopravvissuto al montaggio, e l'AI preferisce
quelli.

**Passaggio 4**:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableGraphics": true, "graphicsPlan": {"from": "Step", "stepOrder": 3},
 "transitionPolicy": "Auto", "programFadeInMs": 500, "programFadeOutMs": 800,
 "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900, "minSegmentMs": 800}
```

`graphicsPlan` deve puntare al passaggio del pianificatore tramite il suo numero. Anche "Previous" funzionerebbe qui, ma la
forma numerata continua a funzionare se aggiungi dei passaggi.

## Come avviare e guidare le grafiche {#how-to-run-and-steer-the-graphics}

Quando fai clic su **Avvia**, descrivi quello che vuoi in **Il tuo brief**:

> Add a title card "Spring Launch 2026" at the start, a lower-third "Maria Lopez, Head of Product"
> when Maria introduces herself, and at most two callouts for the key numbers. Bold, modern style,
> white text with a purple glow.

L'AI sceglie solo tra i punti offerti e sceglie per ogni grafica una parola di durata: `Short` (1,5 s),
`Medium` (3 s) oppure `Hold` (6 s) per impostazione predefinita. Non sceglie mai da sé tempi o posizioni; ReelBolt
colloca la grafica esattamente a partire dal punto scelto.

Il passaggio 3 è il più lento: per ogni grafica l'AI scrive un'animazione Remotion, verifica che si compili
e la genera.

## Cosa controllare dopo aver aggiunto le grafiche {#what-to-check-after-adding-graphics}

- Risultato del **passaggio 1**: `meta.offeredPlacementIdCount` sopra 0 e un elenco `view.placements`.
- Risultato del **passaggio 3**: l'elenco `overlays`. Una grafica con un `renderedAssetStorageKey` è un'animazione
  generata; una con solo `text`/`subtext` è un semplice riquadro di testo (il suo `reason` dovrebbe dire perché).
- Risultato del **passaggio 4**: la sezione delle grafiche riporta quante grafiche sono state applicate ed elenca quelle
  scartate con un motivo (per esempio un punto il cui momento è stato tagliato dal montaggio).

## Impostazioni per le grafiche di solo testo {#settings-for-plain-text-graphics}

Queste impostazioni di `VideoCompile` riguardano solo i riquadri di testo di ripiego (valori predefiniti tra parentesi):
`overlayFontSizePct` (5), `overlayBoxHeightPct` (16), `overlayBoxWidthPct` (82),
`overlayFontColor` (`"white"`), `overlayBoxColor` (`"black@0.45"`), `maxOverlayTextChars` (80),
`maxOverlaySubtextChars` (60). Durate: `overlayShortMs` (1500), `overlayMediumMs` (3000),
`overlayHoldMs` (6000), con una dissolvenza di `overlayFadeMs` (300). `maxOverlays` (20) limita quante
grafiche vengono applicate.

## Varianti delle motion graphics {#variations-of-motion-graphics}

- **Gruppo di artisti:** il template **Montaggio del girato con titoli, progettato da un team di grafici**
  (`video-derush-edit-graphics-room`) usa un passaggio `GraphicsRoom` al posto del passaggio 3. Un
  artista del layout, uno dei tempi, uno dei testi e uno degli inserti discutono le opzioni, poi un
  `MotionGraphicsDirector` scrive il piano e genera le grafiche. Il suo `view` deve puntare al passaggio di
  analisi: `{"version": 1, "view": {"from": "Step", "stepOrder": 1}}`.
- **Grafiche dentro un green screen:** la stessa AI può anche riempire lo schermo verde di un telefono o di un portatile con
  una scena generata che segue lo schermo. Vedi
  [Ricetta inserimento su schermo tracciato](tracked-screen-insert.md).
- **Nessun taglio, solo grafiche:** di' all'editor nella richiesta di tenere l'intero clip.

## Insidie delle motion graphics {#pitfalls-with-motion-graphics}

- **Nessuna grafica:** nel passaggio 1 mancava `"emitOverlayPlacements": true`, nel passaggio 4 mancava
  `"enableGraphics": true`, oppure `graphicsPlan` puntava al passaggio sbagliato. Inoltre è ammesso scegliere zero
  grafiche: all'AI viene detto di preferire nessuna grafica al disordine, quindi chiedile in modo esplicito.
- **Solo riquadri di testo semplice:** la generazione è fallita o la sandbox non era disponibile. Il
  `reason` della sovrimpressione di solito dice quale dei due.
- **Una grafica compare nel momento sbagliato rispetto alle parole:** senza trascrizione l'AI non può sapere
  cosa viene detto e quando. Aggiungi un provider di trascrizione.
- **`GRAPHICS_REQUIRE_REENCODE`:** le grafiche richiedono `"mode": "Reencode"` (il valore predefinito).
- **Passaggio 3 lungo:** è previsto. Ogni grafica generata richiede diversi giri di strumenti; calcola minuti per
  ogni grafica.
