---
title: Sfoca le targhe e altri oggetti (traccia e censura)
sidebar_position: 11
description: Ricetta per trovare oggetti come le targhe nel tuo girato, seguirli mentre si muovono e sfocarli, pixelarli o coprirli di nero.
i18n_source_sha: b843f509c25928023efc7c02067caa410be1548a
---

# Sfoca le targhe e altri oggetti (traccia e censura) {#blur-licence-plates-and-other-objects-track-and-censor}

ReelBolt può **trovare un oggetto nel tuo girato, seguirlo mentre si muove e oscurarlo**: l'uso
tipico è sfocare le targhe, ma funziona con qualsiasi cosa piatta che sai nominare: un'insegna di negozio,
un badge con un nome, uno schermo che mostra qualcosa di privato. Non serve un green screen.

Servono due passaggi:

1. **`VideoAnalyze`** trova e segue gli oggetti. Scrivi cosa cercare ("targa");
   un'AI di visione trova ogni istanza su fotogrammi distribuiti lungo il clip, e poi un tracker segue
   ciascuna fotogramma per fotogramma, anche con movimenti di camera, zoom e inclinazioni.
2. **`VideoCompile`** oscura ogni oggetto tracciato il cui nome indichi nell'elenco, con sfocatura, pixelatura o
   nero pieno, e continua a seguirlo nel video finito.

## Cosa serve per censurare gli oggetti {#what-you-need-to-censor-objects}

Prima di iniziare:

- **Un provider di AI di visione** configurato dal tuo amministratore (qualsiasi provider con la capacità
  **Vision**). Serve solo a *trovare* gli oggetti; seguirli non usa l'AI.
- **Le tue riprese**, caricato nel progetto.

Costo: ogni "occhiata" a un fotogramma equivale a una o due chiamate al provider di visione. Per impostazione predefinita ReelBolt guarda
una volta al secondo, al massimo 12 volte per clip.

## Costruisci il flusso di lavoro di traccia e censura {#build-the-track-and-censor-workflow}

**Passaggio 1: `VideoAnalyze`.** Scegli il tuo video come origine. Sotto **Segui gli oggetti**, scrivi cosa
cercare, per esempio `licence plate`, e premi Invio. In JSON:

```json
{
  "version": 1,
  "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"},
  "transcription": "Off",
  "trackObjects": [{"label": "licence plate"}],
  "objectDetectEverySec": 1.0,
  "maxObjectDetectFrames": 12
}
```

- `trackObjects`: cosa trovare. Usa una descrizione breve e semplice; puoi elencarne diverse.
- `objectDetectEverySec`: ogni quanto ReelBolt guarda di nuovo, così vengono prese anche le auto che entrano
  più tardi. Un valore più basso è più accurato e costa più occhiate.
- `maxObjectDetectFrames`: il massimo di occhiate per clip. Su un clip lungo sono distribuite uniformemente su
  tutto il clip.
- `objectTrackMaxSide` (predefinito 960): la risoluzione a cui lavora il tracker. Alzala (fino a 1920)
  per targhe piccole e lontane.

**Passaggio 2: `VideoCompile`.** Attiva **Mantieni intere tutte le origini (nessun montaggio)** se non vuoi che il
video venga tagliato, e sotto **Censura gli oggetti tracciati** scrivi la stessa etichetta. In JSON:

```json
{
  "version": 1,
  "decision": {"from": "Previous"},
  "analysisStepOrder": 1,
  "keepWholeSources": true,
  "censorLabels": ["licence plate"],
  "censorStyle": "Blur",
  "censorPadding": 0.25,
  "censorHoldSec": 0.4
}
```

- `censorLabels`: quali oggetti tracciati oscurare, con il nome che hai dato loro nel passaggio 1. `"*"`
  oscura tutto ciò che è stato tracciato.
- `censorStyle`: `"Blur"`, `"Pixelate"` oppure `"Fill"` (nero pieno; l'unico stile che non lascia
  nulla dell'originale).
- `censorPadding`: copertura extra su ogni lato, come frazione del lato più lungo dell'oggetto
  (0,25 = un quarto della larghezza di una targa sopra, sotto e su entrambi i lati), per coprire piccoli errori di rilevamento e
  di tracciamento.
- `censorHoldSec`: secondi extra di copertura prima che un oggetto venga tracciato per la prima volta e dopo l'ultima volta
  in cui è stato tracciato.

Puoi anche inserire un montaggio nel mezzo: tieni un passaggio `VideoStoryEditor` come passaggio 2 e punta il campo `decision` del
passaggio di compilazione su di esso. La censura segue l'oggetto in **ogni** parte del video finito
in cui compare, comunque il montaggio l'abbia tagliato.

## Controlla il risultato censurato {#check-the-censored-result}

- **Il risultato del passaggio 1** mostra `meta.objectTracking`: quanti oggetti sono stati tracciati (`tracks`), quante
  occhiate di visione sono state fatte e, se qualcosa è andato storto, `degraded: true` con un `reason`
  (per esempio "no Vision provider is configured"). I messaggi di avanzamento nominano ogni oggetto mentre viene
  tracciato, le impostazioni che il tracker ha scelto per lui e la sua affidabilità.
- **Il risultato del passaggio 2** ha una sezione `censor` che elenca ogni tratto oscurato e un elenco
  `unmatchedLabels`. **Tutto ciò che è in `unmatchedLabels` non è stato oscurato da nessuna parte**: nessun oggetto
  con quel nome è stato tracciato. Controlla questo elenco ogni volta.
- **Se la ricerca degli oggetti è andata storto** (nessun provider di visione, un'occhiata fallita, troppi oggetti per
  `maxObjectTracks`), il passaggio 2 **si ferma con un errore** invece di creare un video, perché il risultato
  potrebbe mostrare ciò che volevi nascondere. Risolvi la causa e avvia di nuovo.
- **L'editor** mostra ogni oggetto censurato come un contorno rosso tratteggiato sopra il video, con il suo nome
  e la sua affidabilità, su una traccia **Censurati** dedicata nella timeline.

## Limiti della censura automatica {#limits-of-automatic-censoring}

Leggi questa parte prima di pubblicare qualsiasi cosa in cui la privacy conti:

- **L'AI di visione può non vedere alcuni oggetti**, soprattutto se piccoli, sfocati o parzialmente nascosti. Guarda sempre
  il risultato.
- **Tra un'occhiata e l'altra, un oggetto che appare viene trovato solo alla successiva.** Con il valore
  predefinito di un'occhiata al secondo, una targa che entra nell'inquadratura può restare visibile fino a un secondo prima di essere
  oscurata. Abbassa `objectDetectEverySec` per le scene affollate.
- **Un oggetto tracciato che viene coperto del tutto** (una persona gli passa davanti) interrompe la sua traccia; l'occhiata
  successiva ne avvia una nuova. `censorHoldSec` copre le brevi interruzioni.
- **Sfocatura e pixelatura non sono irreversibili.** Per qualsiasi cosa sensibile, usa `"censorStyle": "Fill"`.
- Il tracker segue meglio le cose **piatte**. Un volto che si gira o un oggetto che si piega vengono
  seguiti con meno precisione; alza `censorPadding` per compensare.

## Posizionare un'area a mano invece di cercare {#placing-a-region-by-hand-instead-of-searching}

Quando sai esattamente dove si trova l'oggetto in un dato momento, puoi saltare l'AI di visione: dai al
bersaglio una `region` (i suoi quattro angoli come frazioni del fotogramma: in alto a sinistra, in alto a destra, in basso a sinistra,
in basso a destra) e il momento in cui si trova lì. Il tracker lo segue da quel momento, in avanti e
all'indietro, senza nessuna chiamata AI:

```json
"trackObjects": [
  {
    "label": "shop sign",
    "atSec": 2.0,
    "region": {"x0": 0.62, "y0": 0.18, "x1": 0.80, "y1": 0.18,
               "x2": 0.62, "y2": 0.27, "x3": 0.80, "y3": 0.27}
  }
]
```

L'assistente può compilarlo per te: dagli cosa oscurare e più o meno dove.
