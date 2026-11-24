---
title: Sfoca o evidenzia un oggetto (effetti su un'area da prompt)
sidebar_position: 13
description: "Segui un oggetto nel tuo girato a partire da un prompt di testo e applica un effetto solo a quella parte dell'immagine: sfoca un tachimetro, evidenzia l'etichetta di un prodotto, attenua tutto tranne un volto."
i18n_source_sha: 7b0d5fd3a520c6e9816b78fba0e92bac75308f8d
---

# Sfoca o evidenzia un oggetto (effetti su un'area da prompt) {#blur-or-highlight-one-object-prompted-region-effects}

ReelBolt può **seguire un oggetto in un'inquadratura a partire da un prompt di testo e modificare solo quella parte
dell'immagine**. Sfoca il tachimetro in un'inquadratura di guida, illumina l'etichetta di un prodotto, attenua tutto
tranne la persona che parla. Il resto del fotogramma resta com'è; l'effetto segue la forma dell'oggetto
mentre si muove, cambia dimensione e ruota, e mentre l'oggetto è completamente nascosto non viene disegnato
alcun effetto.

Servono due passaggi:

1. **`VideoAnalyze`** trova e segue l'oggetto. Scrivi cos'è ("tachimetro"); un modello di
   segmentazione ospitato su fal.ai restituisce una maschera per ogni fotogramma: la forma di quel solo oggetto.
2. **`VideoCompile`** applica un effetto attraverso quelle maschere (sfocatura, pixelatura, evidenziazione, faro,
   colore selettivo o contorno) e lo disegna nel video finito.

Nessuno dei due passaggi lo fa se non lo chiedi. Un flusso di lavoro senza un bersaglio di segmentazione e senza
una voce di effetto viene compilato esattamente come prima.

## Cosa serve {#what-you-need}

- **Una connessione fal.ai con la capacità Segmentazione video**, aggiunta da un amministratore dello spazio di lavoro
  (vedi [Provider di inferenza](../admin/inference-providers.md)). Il modello è
  `fal-ai/sam-3-1/video-rle`; è l'unico servizio che oggi segue gli oggetti nei video.
- **Le tue riprese**, caricato nel progetto.
- **Un piano che include la segmentazione.** I piani di prova no; Creator e superiori sì. Un'esecuzione di prova si ferma
  con `PLAN_NOT_ENTITLED` e lo dice prima di spendere qualsiasi cosa.

### Aggiungi il provider (amministratore) {#add-the-provider-administrator}

1. Apri **Provider di inferenza** nella dashboard e premi **Nuovo provider**.
2. **Tipo**: **fal.ai**. **Capacità**: **Segmentazione video**. **Modello**:
   `fal-ai/sam-3-1/video-rle`. Incolla la chiave di fal.ai.
3. Premi **Prova la connessione**, poi **Crea**. Se è l'unica connessione di segmentazione, spunta
   **Imposta come provider di segmentazione predefinito** così i passaggi la usano senza nominarla.

Il passaggio ha anche un campo **Provider di segmentazione**, quindi un flusso di lavoro può nominare una connessione specifica
quando uno spazio di lavoro ne ha più di una.

## Passaggio 1: segui l'oggetto in `VideoAnalyze` {#step-1--follow-the-object-in-videoanalyze}

Sotto **Segui gli oggetti**, ogni riga è un oggetto. Per una riga che deve usare la segmentazione:

- **Tracciamento**: scegli **Segmentazione AI (a pagamento)** invece del tracker gratuito. Il tracker gratuito
  segue bene le cose piatte e rigide (insegne, targhe, schermi); la segmentazione serve per tutto il resto: un
  indicatore, un volto, un logo su una superficie curva o deformabile.
- **Cosa cercare**: parole semplici: `speedometer`, `product label`, `the red mug`. È
  il prompt del modello e anche il nome che userai nel passaggio di compilazione. Scrivilo breve e specifico;
  "the small round dial on the left" è un prompt peggiore di "speedometer".
- **Facoltativo: segnalalo a mano.** Un'area che hai disegnato su un fotogramma (o un'area segnata dall'assistente)
  diventa un suggerimento a riquadro, e il centro del riquadro diventa un suggerimento a clic, su quel fotogramma. Usalo quando il clip
  contiene due oggetti simili e le parole non bastano a distinguerli.

In JSON:

```json
{
  "version": 1,
  "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
  "transcription": "Off",
  "trackObjects": [{"label": "speedometer", "method": "Segmentation"}],
  "allowSourceMediaEgress": true,
  "maxSegmentationSeconds": 60,
  "segmentationDetectionThreshold": 0.5,
  "maxSegmentationObjects": 4
}
```

### La casella del consenso e cosa significa {#the-consent-checkbox-and-what-it-means}

Un modello ospitato può seguire l'oggetto solo se può **vedere il clip**, quindi questa modalità invia l'immagine
a fal.ai. È una cosa diversa da ogni altro passaggio di ReelBolt, che tiene il tuo girato sul
server, perciò viene chiesta a parte:

> **Invia questa clip al provider configurato**: obbligatorio per la segmentazione. Finché non lo spunti, un'
> esecuzione si ferma con `EGRESS_REFUSED` e nomina il clip; non viene caricato nulla.

Spuntalo (`allowSourceMediaEgress: true`) e viene inviato solo questo: il clip stesso, tramite un
link che scade entro pochi minuti, e le parole che hai scritto. ReelBolt chiede al provider di non conservarne
una copia (la richiesta porta l'intestazione `X-Fal-Store-IO: 0` di fal.ai) e non invia nient'altro sul
progetto: nessun altro file, nessuna trascrizione, nessun nome. Ciò che torna indietro è una maschera per fotogramma, che
ReelBolt conserva nel progetto.

Lasciala non spuntata e un bersaglio di segmentazione non può essere eseguito: è voluto, non è un errore, e il
messaggio nomina il clip che sarebbe stato inviato.

### Limiti del tracciamento {#limits-on-following}

| Impostazione | Predefinito | Consentito | Cosa succede fuori dai limiti |
|---|---|---|---|
| `maxSegmentationSeconds`: **Clip analizzata più lunga (secondi)** | 60 | da 1 a 600 | Un'origine più lunga viene rifiutata con `SEGMENTATION_SOURCE_TOO_LONG`, indicando la propria lunghezza e questo limite, prima che venga inviato qualsiasi cosa (la frase è citata più sotto). |
| `segmentationDetectionThreshold`: **Soglia di rilevamento** | 0,5 | da 0,01 a 1 | Più bassa trova più oggetti, anche sbagliati; più alta ne trova meno. Inviata al modello così com'è. |
| `maxSegmentationObjects`: **Massimo di oggetti per clip** | 4 | da 1 a 16 | Il massimo di oggetti che una chiamata può restituire. I candidati in più vengono scartati dal modello, non da ReelBolt. |
| `segmentationTimeoutSeconds`: **Timeout del provider** | 900 | da 60 a 3600 | Una chiamata viene abbandonata dopo questo tempo e il passaggio fallisce con `SEGMENTATION_PROVIDER_ERROR`. |

Il valore predefinito di 60 secondi è una protezione dei costi, non un limite tecnico: chi ha bisogno di un clip più lungo
alza **Clip più lungo analizzato** fino a 600 secondi e paga i fotogrammi.

## Passaggio 2: aggiungi l'effetto in `VideoCompile` {#step-2--add-the-effect-in-videocompile}

Sotto **Effetto su un oggetto**, premi **Aggiungi un effetto**. Ogni voce ha:

- **Oggetto**: un id di traccia come `g1`, oppure le parole con cui l'oggetto è stato seguito
  ("speedometer"). Confrontato senza distinguere maiuscole e minuscole. L'id è la scelta precisa quando due oggetti
  condividono un prompt.
- **Effetto**: uno dei sei qui sotto.
- **Intensità**: da 0 a 2, predefinito 1. Più alta è più forte; 0 non cambia nulla.
- **Bordo morbido (px)**: da 0 a 64, predefinito 0. Ammorbidisce il bordo della maschera, mai l'immagine.
- **Opacità**: da 0 a 1, predefinito 1. Quanta parte dell'effetto viene disegnata dentro la forma.
- **Tutto tranne questo oggetto**: l'interruttore di inversione, vedi sotto.

In JSON:

```json
{
  "version": 1,
  "mode": "Reencode",
  "keepWholeSources": true,
  "regionEffects": [
    {"target": "speedometer", "effect": "Blur", "strength": 1.0, "featherPx": 4}
  ]
}
```

**La modalità deve essere Reencode.** Un effetto modifica i pixel, quindi una compilazione `StreamCopy` viene rifiutata con
`EFFECT_REQUIRES_REENCODE` invece di pubblicare un video senza l'effetto.

### I sei effetti {#the-six-effects}

| Effetto | Cosa fa | Aspetto con intensità 1 |
|---|---|---|
| **Sfocatura** | Ammorbidisce l'oggetto; l'immagine intorno resta intatta. | Raggio di sfocatura pari all'1,5% del lato corto del fotogramma |
| **Pixel** | Sostituisce l'oggetto con blocchi grossolani. | Blocchi pari al 2% del lato corto |
| **Evidenzialo e oscura il resto** | Illumina l'oggetto e scurisce leggermente tutto il resto, così l'occhio va su di esso. | +10% luminosità, +10% contrasto, resto attenuato del 15% |
| **Spotlight: oscura tutto il resto** | Scurisce il resto del fotogramma e lascia l'oggetto com'è. | Resto attenuato del 60% |
| **Color pop: mantieni il colore solo su di esso** | Mantiene l'oggetto a colori e rende tutto il resto in bianco e nero. | Desaturazione completa all'esterno |
| **Contorno** | Disegna una banda bianca lungo la silhouette dell'oggetto. | Banda pari allo 0,4% del lato corto, al massimo 16 px |

### Inversione: tutto tranne l'oggetto {#invert-everything-except-the-object}

L'ultimo interruttore applica l'effetto a **tutta l'immagine tranne l'oggetto**: per esempio
`Spotlight` invertito è una morbida vignettatura che risparmia il prodotto, e `Blur` invertito sfoca lo
sfondo mentre l'etichetta resta nitida. L'inversione è esatta: ReelBolt capovolge la stessa maschera invece di
ingrandire la forma, quindi il bordo che vedi è il bordo che ha dato il modello.

Due voci possono puntare allo stesso oggetto con impostazioni diverse; la maschera dell'oggetto viene scaricata
e adattata una sola volta e distribuita a entrambe.

### Limiti sugli effetti {#limits-on-effects}

| Limite | Valore | Rifiuto |
|---|---|---|
| Effetti per compilazione | 8 | `EFFECT_LIMIT_EXCEEDED`: il nono viene rifiutato, mai scartato in silenzio |
| Lunghezza del nome dell'oggetto | 120 caratteri | `EFFECT_TARGET_INVALID` |
| Intensità | da 0 a 2 | `EFFECT_STRENGTH_OUT_OF_RANGE` |
| Bordo morbido | da 0 a 64 px | `EFFECT_FEATHER_OUT_OF_RANGE` |
| Opacità | da 0 a 1 | `EFFECT_OPACITY_OUT_OF_RANGE` |
| Modalità di compilazione | deve essere `Reencode` | `EFFECT_REQUIRES_REENCODE` |

Gli effetti vengono disegnati sull'immagine dopo gli inserimenti e le grafiche, e **prima** del passaggio che nasconde gli oggetti
e della dissolvenza del programma. Quindi un oggetto che evidenzi e censuri insieme viene censurato: vince il nascondere.

## Quanto costa {#what-it-costs}

Seguire un oggetto è una chiamata a pagamento, fatturata da fal.ai **ogni 16 fotogrammi di video in ingresso, arrotondati per eccesso**,
a **$0,01 ogni 16 fotogrammi**:

```text
units  = ceil(frames in the clip / 16)
cost   = units × $0.01
```

- **Un clip di 5 secondi costa circa $0,08**: 5 s a 24 fps sono 120 fotogrammi, cioè 8 unità.
- 5 s a 30 fps sono 150 fotogrammi, 10 unità, $0,10.
- Un clip di 60 secondi a 30 fps sono 1.800 fotogrammi, 113 unità, $1,13.
- Ogni oggetto che segui è una chiamata a sé, quindi tre prompt su un clip costano tre volte tanto.
- Generare l'effetto è una normale codifica; non c'è alcun costo extra per ogni voce di effetto.
- **I crediti riservati per il passaggio sono il caso peggiore al tuo limite**, ipotizzando 30 fotogrammi al
  secondo, non il numero di fotogrammi di questo clip. Un clip che porta davvero più fotogrammi di quanti
  ne copra la riserva (girato a 60 o 120 fps al limite) viene rifiutato prima di acquistare qualsiasi cosa, con
  `SEGMENTATION_FRAME_BUDGET_EXCEEDED`: non viene mai troncato e non si chiude mai oltre il blocco.
  Un clip più breve o un valore più basso di **Clip più lungo analizzato** lo fa rientrare.

**Lo stesso clip con lo stesso prompt, modello e impostazioni è gratis la seconda volta.** ReelBolt conserva
le maschere ricevute, identificate dal contenuto del clip, dal prompt, dal modello, dalle impostazioni di rilevamento
e dal provider; un'esecuzione che le trova non fa nessuna chiamata e non fattura nulla. Cambia il prompt o la
soglia e diventa una nuova chiamata.

## Quando un'esecuzione si ferma {#when-a-run-stops}

Ogni messaggio di rifiuto inizia con il suo codice, e la formulazione è quella dell'esecuzione stessa: le frasi qui sotto sono
quelle che scrive il passaggio, con il valore che inserisce mostrato come `{…}`. La prima tabella ferma l'esecuzione prima che
venga acquistato qualsiasi cosa; la seconda rifiuta la compilazione prima che venga codificato un fotogramma. I messaggi sono riportati in inglese, così come li scrive il passaggio.

### Prima di acquistare qualsiasi cosa (il passaggio di analisi) {#before-anything-is-bought-the-analyze-step}

Controllati in quest'ordine, che è anche l'ordine in cui compaiono le righe:

| Codice | Il messaggio, parola per parola | Soluzione |
|---|---|---|
| `INSUFFICIENT_CREDITS` | "This run needs about {required} credits and the workspace has {balance} available. Top up or use a smaller workflow." | Ricarica, segui meno oggetti o accorcia il clip. |
| `PLAN_NOT_ENTITLED` | "The {tier} plan does not include text-prompted object segmentation (an object followed through the video, so an effect can be applied to just that part of the picture). Upgrade your plan to use it." | Passa a un piano superiore, oppure rimuovi il bersaglio di segmentazione. |
| `EGRESS_REFUSED` | "Text-prompted segmentation sends the source clip to a hosted model, so it needs consent. "{label}" was configured for "{clip}" but allowSourceMediaEgress is false. Turn it on for this step to accept that the clip leaves this host." | Spunta **Invia questa clip al provider configurato**, oppure reimposta quell'oggetto sul tracker gratuito. Non è stato caricato nulla. |
| `SEGMENTATION_NO_PROVIDER` | "No default VideoSegmentation provider is configured, so this step's segmentation targets cannot run. Add a fal provider for video segmentation and mark it the default." — when the step names a connection that no longer resolves it says "No enabled provider with the VideoSegmentation capability matches the id {id} this step names, and no default VideoSegmentation provider is configured. Add one under Inference providers." | Aggiungi una connessione fal.ai con quella capacità, oppure imposta un provider predefinito. |
| `SEGMENTATION_SOURCE_TOO_LONG` | "This clip is {duration}s long but one segmentation pass is capped at {cap}s (maxSegmentationSeconds). Raise the cap — the vendor bills per 16 frames of input — or analyse a shorter clip. The clip was not truncated." | Alza **Clip più lungo analizzato** fino a 600, oppure segui l'oggetto su un taglio più breve. |
| `SEGMENTATION_FRAME_BUDGET_EXCEEDED` | "This clip is {frames} frames at {fps} fps, but one segmentation pass holds credit for at most {pricedFrames} frames: the {cap}s cap prices 30 fps, and the vendor bills every frame at the clip's own rate. Lower maxSegmentationSeconds or trim the clip (a lower frame rate or a shorter clip); the clip was not truncated." | Il clip rientra nel limite ma porta più fotogrammi di quelli coperti dai crediti riservati. Il limite *determina* quella riserva, quindi abbassarlo riduce la copertura invece del clip: accorcia il clip, oppure segui l'oggetto a 30 fotogrammi al secondo o meno. |
| `SEGMENTATION_EGRESS_UNAVAILABLE` | "This deployment cannot mint a temporary download URL for the source clip (no presigner is configured, which is the case for local-disk storage), so a hosted model cannot read it. Configure S3-compatible object storage with presigning, or run the step without segmentation targets." | Esegui la segmentazione su un'installazione con archiviazione a oggetti, oppure rimuovi il bersaglio di segmentazione. |
| `SEGMENTATION_PROVIDER_ERROR` | "The segmentation provider "{provider}" did not answer the prompt "{prompt}" ({detail})." | Riprova il passaggio; se si ripete, controlla la chiave e lo stato del provider, poi alza **Timeout del provider**. |
| `SEGMENTATION_BAD_RESPONSE` | "The segmentation provider "{provider}" returned an answer for "{prompt}" that does not fit a {width}x{height} frame ({detail})." — or, when the masks could not be written, "The masks the provider returned for "{prompt}" could not be turned into a mask video ({detail})." | Riprova; se si ripete, segnalalo: ReelBolt rifiuta invece di disegnare una maschera di cui non può fidarsi. |

### Mentre gli effetti vengono generati (il passaggio di compilazione) {#while-the-effects-are-rendered-the-compile-step}

Tutti questi controlli vengono eseguiti quando l'elenco viene salvato **e** di nuovo quando verrebbe generato, quindi un elenco sbagliato
ferma la compilazione prima che venga codificato un solo fotogramma.

| Codice | Il messaggio, parola per parola | Soluzione |
|---|---|---|
| `EFFECT_TARGET_INVALID` | "RegionEffects[{i}].target is blank; name a g{n} track id or the prompt label it was tracked under." — or "RegionEffects[{i}].target is {n} characters; at most 120 are allowed." | Nomina l'oggetto: un id di traccia o le parole con cui è stato seguito, al massimo 120 caratteri. |
| `EFFECT_LIMIT_EXCEEDED` | "RegionEffects has {n} entries; at most 8 are allowed (each one is a mask input and a filter chain). Remove {k}." | Rimuovi voci finché ne restano 8; quella in più non viene mai scartata in silenzio. |
| `EFFECT_INVALID` | "RegionEffects[{i}].effect is {value}, which is not a defined effect. Use one of: Blur, Pixelate, Highlight, Spotlight, ColorPop, Outline." | Scegli di nuovo un effetto dall'elenco. |
| `EFFECT_STRENGTH_OUT_OF_RANGE` | "RegionEffects[{i}].strength is {value}; it must be between 0 and 2." | Impostalo nell'intervallo. |
| `EFFECT_FEATHER_OUT_OF_RANGE` | "RegionEffects[{i}].featherPx is {value}; it must be between 0 and 64 pixels." | Impostalo nell'intervallo. |
| `EFFECT_OPACITY_OUT_OF_RANGE` | "RegionEffects[{i}].opacity is {value}; it must be between 0 and 1." | Impostalo nell'intervallo. |
| `EFFECT_REQUIRES_REENCODE` | "RegionEffects requires Mode=Reencode — masking an effect onto the picture has no stream-copy equivalent ({n} entr{ies} configured)." | Imposta la compilazione su `Reencode`. |
| `EFFECT_TARGET_UNVERIFIED` | "Region effect target "{target}" matches no segmentation track. The analyze step tracked "{label}" ({id}). Use a track id or the exact prompt the analyze step was given." | Usa uno dei nomi elencati nel messaggio, riavvia il passaggio di analisi con quel prompt, oppure correggi il taglio in modo che l'oggetto ci sia. |
| `EFFECT_TARGET_AMBIGUOUS` | "Region effect target "{target}" matches {n} segmentation tracks ("{id}", "{id}"). Name the track id instead of the shared prompt." | Nomina l'id di traccia (`g1`, `g2`) invece del prompt. |
| `SEGMENTATION_MASK_UNVERIFIED` | "The mask for track "{id}" (prompt "{prompt}") does not match the hash the analyze step recorded (expected {expected}, got {actual}). Refusing to render through a mask that may belong to another clip." | Riavvia il passaggio di analisi; ReelBolt non genera attraverso una maschera che non può verificare. |

## Esempio svolto: sfoca il tachimetro {#worked-example-blur-the-speedometer}

Un'inquadratura a mano del cruscotto di un'auto, tachimetro visibile a sinistra, camera in movimento.

**`VideoAnalyze`**

```json
{
  "version": 1,
  "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
  "transcription": "Off",
  "trackObjects": [{"label": "speedometer", "method": "Segmentation"}],
  "allowSourceMediaEgress": true,
  "maxSegmentationSeconds": 60
}
```

Avvialo. L'analisi registra una traccia `g` per ogni oggetto seguito, con il prompt ricevuto e
il tratto di clip in cui è stato trovato. Un prompt che non corrisponde a nulla non produce alcuna traccia, e
il passaggio di compilazione la rifiuta, invece di produrre un'immagine senza l'effetto.

**`VideoCompile`**

```json
{
  "version": 1,
  "mode": "Reencode",
  "keepWholeSources": true,
  "regionEffects": [
    {"target": "speedometer", "effect": "Blur", "strength": 1.0, "featherPx": 4}
  ]
}
```

Un bordo morbido di 4 px evita che la sfocatura finisca su un rettangolo netto, che sembra un intervento di montaggio
e non una messa a fuoco ridotta. Alza **Intensità** a 1,5 o 2 se le cifre sono ancora intuibili. Se
l'oggetto copre in media meno della metà del fotogramma, la compilazione lo segnala mentre procede ("the effect
will be small and easy to miss"): un risultato debole su un quadrante piccolo dipende dalla geometria, non è un errore.

## Esempio svolto: evidenzia l'etichetta del prodotto {#worked-example-highlight-the-product-label}

Un prodotto che gira tra le mani di qualcuno; vuoi che l'etichetta sia ciò su cui cade l'occhio, senza
nascondere il prodotto.

**`VideoAnalyze`**: stessa struttura, prompt `product label`:

```json
{
  "version": 1,
  "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
  "transcription": "Off",
  "trackObjects": [{"label": "product label", "method": "Segmentation"}],
  "allowSourceMediaEgress": true,
  "maxSegmentationSeconds": 60,
  "segmentationDetectionThreshold": 0.4
}
```

Una soglia leggermente più bassa è la correzione abituale per un'etichetta piccola, inclinata o in parte
girata di lato nel primo secondo.

**`VideoCompile`**

```json
{
  "version": 1,
  "mode": "Reencode",
  "keepWholeSources": true,
  "regionEffects": [
    {"target": "product label", "effect": "Highlight", "strength": 1.0, "featherPx": 8},
    {"target": "product label", "effect": "Outline", "strength": 0.6}
  ]
}
```

Due voci su un oggetto: l'evidenziazione solleva l'etichetta e attenua il resto, il contorno disegna una banda
lungo il suo bordo. Entrambe usano la stessa maschera, quindi combaciano esattamente. Se il contorno risulta pesante, abbassa la sua
**Intensità**: riduce la banda, non l'oggetto.

## Cosa non fa (ancora) {#what-it-does-not-do-yet}

- **Nessun effetto nella timeline dell'editor.** Funziona nei passaggi di analisi e compilazione di un flusso di lavoro. Applicare un
  effetto su un oggetto dalla timeline dell'editor arriverà in una fase successiva.
- **Nessun ritaglio che segue l'oggetto (`zoomFollow`).** I dati per farlo sono registrati, ma il ritaglio stesso
  non è stato realizzato.
- **La maschera è buona quanto il modello.** Un oggetto completamente nascosto ha una maschera vuota, e ReelBolt non
  disegna alcun effetto su quel fotogramma: l'alternativa sarebbe spalmare l'ultima forma nota su
  qualsiasi cosa lo copra. Se un oggetto molto occluso sfarfalla troppo, valuta di seguirne
  un altro.
