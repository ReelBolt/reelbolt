---
title: Aggiungi clip di b-roll generati dall'AI
sidebar_position: 10
description: Ricetta per aggiungere a un video montato cutaway, un'apertura a freddo o una schermata finale generati dall'AI a pagamento, con limiti di spesa e impostazioni sulla privacy spiegati.
i18n_source_sha: c202d2556c6ee8ffc5d33be8e3d914554fe53a0a
---

# Aggiungi clip di b-roll generati dall'AI {#add-ai-generated-b-roll-clips}

:::note
Questa app ora si apre come parte di [Edit my video](edit-my-video.md): scegli lì il campo corrispondente. Questa pagina resta come riferimento su ciò che fa ogni passaggio.
:::

**Obiettivo:** aggiungere a un video montato brevi clip video generati dall'AI ("b-roll"): un **cutaway** mostrato
mentre qualcuno continua a parlare, un'**apertura a freddo** prima che il video inizi, o una **schermata finale** dopo che
è finito. Un'AI regista delle inquadrature pianifica i clip, un passaggio di generazione **paga un servizio di generazione video** per
crearli e il passaggio di compilazione li colloca.

**I clip generati usano crediti.** Ogni clip viene addebitato sul saldo del tuo spazio di lavoro, e ReelBolt
controlla i limiti descritti sotto prima di acquistare qualsiasi cosa, quindi non acquista mai un clip che farebbe superare
un limite. Il video generato è disponibile solo nei piani che lo includono (vedi [Piani e crediti](../account/plans-and-credits.md)).

## Prerequisiti per il b-roll generato {#prerequisites-for-generated-b-roll}

- Il video caricato nel progetto.
- Un provider di chat AI.
- **Un provider di generazione video**, collegato da un amministratore per il tuo spazio di lavoro. Se un passaggio non
  ne indica uno, viene usato il provider video predefinito dello spazio di lavoro. Vedi
  [Provider di inferenza](../admin/inference-providers.md) per sapere come un amministratore ne collega uno.
- Crediti sufficienti per i clip che prevedi di creare. La pagina dell'esecuzione mostra la stima prima che tu avvii.

## Quanto costano i clip generati {#what-generated-clips-cost}

Il video generato ha un prezzo per clip e viene mostrato come tetto massimo prima che tu avvii. Quando avvii un'esecuzione,
la riga del costo nella pagina dell'esecuzione dice **Più fino a** seguito dal massimo che i clip generati possono
costare, e un'esecuzione che rigenera i clip mostra la stessa cifra nella sua finestra. Vedi
[Leggere la stima](../account/reading-the-estimate.md) per capire cosa significano le cifre.

## Limiti di spesa per i clip generati {#spending-caps-for-generated-clips}

Tre limiti ti proteggono; un acquisto deve rientrare in **tutti**:

1. **Per passaggio:** `maxSpendCredits` sul passaggio `VideoGenerate` (obbligatorio, deve essere maggiore di 0). Il costo
   stimato dell'intero piano viene confrontato con questo valore **prima** di acquistare qualsiasi cosa.
2. **Per progetto al giorno:** un limite giornaliero impostato dal tuo amministratore.
3. **Per l'intera piattaforma al giorno:** un limite giornaliero impostato dal tuo amministratore, su tutti i progetti.

Se un limite verrebbe superato, **non viene acquistato nulla** e il passaggio fallisce con
`BUDGET_EXCEEDED`. Non esistono acquisti parziali. Anche `maxClips` (da 1 a 64, decidi tu) limita quanti clip può creare un solo
passaggio. Conta ogni take, non ogni inquadratura: con `takes: 2` e `maxClips: 4` vengono create solo due
inquadrature; le inquadrature pianificate oltre il limite vengono saltate, non acquistate.

## Il flusso di lavoro del b-roll generato {#the-generated-b-roll-workflow}

Aggiungi l'app **Montaggio del girato con b-roll generato dall'AI** (`video-derush-edit-broll`) e seleziona il tuo video
nel passaggio 1.

| # | Etichetta | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analizza il video sorgente | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decidi quali tratti tenere | `Agent` | `VideoStoryEditor` |
| 3 | Pianifica le inquadrature di b-roll generato | `Agent` (contesto `FullWorkflow`) | `ShotDirector` |
| 4 | Genera i clip di b-roll pianificati | `VideoGenerate` | `VideoTransform` (chiama il servizio a pagamento) |
| 5 | Compila il video montato con i clip generati | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 6 | Verifica la qualità del montaggio | `ReviewLoop` | `VideoReviewAgent` |

**Passaggio 4** com'è fornito. Il modello e la risoluzione sono lasciati ai valori predefiniti:

```json
{"planSource": "StepRef", "plan": {"from": "Step", "stepOrder": 3},
 "analysis": {"from": "Step", "stepOrder": 1},
 "durationSeconds": 6, "ratio": "16:9",
 "takes": 1, "maxClips": 4, "maxSpendCredits": 5.0,
 "pollIntervalSeconds": 10, "timeoutSeconds": 1800}
```

- `"planSource": "StepRef"` crea un clip per ogni inquadratura pianificata dal passaggio 3.
- `takes`: versioni per inquadratura (da 1 a 64, mai più di `maxClips`).
- `ratio`: uno tra `21:9`, `16:9`, `4:3`, `1:1`, `3:4`, `9:16`; fallo corrispondere al tuo girato.
- `timeoutSeconds`: quanto attendere ogni clip (da 60 a 7200).

**Passaggio 5**:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableGeneratedClips": true, "generatedClips": {"from": "Step", "stepOrder": 4},
 "maxGeneratedClips": 4, "transitionPolicy": "Auto", "programFadeInMs": 500,
 "programFadeOutMs": 800, "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900,
 "minSegmentMs": 800}
```

## Cosa pianifica il regista delle inquadrature {#what-the-shot-director-plans}

Lo `ShotDirector` pianifica zero o più inquadrature. Per ciascuna sceglie:

- uno **scopo**: `Cutaway` (sostituisce l'immagine per un momento mentre l'audio continua), `ColdOpen`
  (aggiunto prima dell'inizio), `EndCard` (aggiunto dopo la fine), `SeamBridge` (un clip generato tra
  due tagli, solo quando il `transitionPolicy` del passaggio di compilazione è `"Generated"`), `Extend` (allunga
  un'inquadratura più breve della sua voce narrante) oppure `ScreenContent` (messo **su un green screen tracciato** nel
  tuo girato: serve `"enableInserts": true` nel passaggio di compilazione e un green screen trovato dal
  passaggio di analisi; senza queste condizioni il clip viene segnalato come `inserts_disabled` oppure
  `no_insert_region_at_anchor` e saltato);
- **dove** va: uno degli id di inquadratura, pausa o frase offerti;
- una parola per la **camera** e una parola per la **lunghezza** (`Short`, `Medium`, `Long`), che ReelBolt trasforma in
  impostazioni reali;
- un **prompt** scritto che descrive il clip. È l'unico testo inviato al provider, dopo la
  pulizia.

Il regista delle inquadrature non può spendere denaro; può farlo solo il passaggio 4, e solo entro i limiti. Guidalo tramite
il brief dell'esecuzione (il riquadro **Il tuo brief** quando fai clic su **Avvia**), per esempio "Un cutaway di
un caffè affollato quando nomino il locale, e una schermata finale con un tramonto sulla città".

## Avviare e riavviare il b-roll generato {#running-and-re-running-generated-b-roll}

Quando avvii o riprovi questa app, ReelBolt chiede:

- **Avvia e riusa i clip precedenti** (predefinito, gratis): qualsiasi clip con esattamente lo stesso prompt e le stesse
  impostazioni che sia già stato generato in questo progetto, e che esista ancora, viene riutilizzato.
- **Rigenera (costa denaro)**: paghi clip nuovi. La finestra mostra il massimo che potrebbe costare.

Quando il passaggio di revisione rimanda l'esecuzione al passaggio 2, anche i clip invariati vengono riutilizzati; imposta
`"loopBackBehavior": "Regenerate"` nel passaggio 4 per pagare invece clip nuovi a ogni giro.

**Cosa controllare:** il risultato del passaggio 4 elenca ogni clip, se è stato consegnato, riutilizzato o saltato,
e il suo costo. Il suo record dettagliato (vista dati) è l'elenco completo dei clip. Il risultato del passaggio 5 indica quali
clip sono stati collocati e quali scartati, con un motivo.

## Privacy: inviare il tuo girato al provider {#privacy-sending-your-own-footage-to-the-provider}

Il regista delle inquadrature può chiedere che un clip inizi o finisca su un fotogramma del tuo girato, così il clip
corrisponde visivamente. Quel fotogramma **è il tuo girato**, e una volta inviato a un provider non può essere
richiamato. Quindi:

- `allowSourceMediaEgress` nel passaggio 4 è `false` per impostazione predefinita. **Aggiungere tu stesso questa app dal
  selettore delle app lo imposta su `true`**: è il permesso dell'app stessa perché il regista delle inquadrature
  generi un'inquadratura a partire da un fotogramma del tuo girato, che è tutto il senso di un b-roll che si
  accorda con il montaggio. **Un flusso di lavoro creato per te dall'assistente parte con il valore disattivato**: il
  permesso di inviare il tuo girato spetta a te: chiedi all'assistente di attivarlo e approva la sua scheda, oppure attiva **Consenti l'invio dei tuoi file ai servizi
  AI** (Allow sending your media to AI services) sopra **Avvia** nella pagina dell'app per l'esecuzione che avvii, oppure attivalo nel passaggio per consentire ogni esecuzione. Su un
  passaggio tuo, lasciarlo su `false` significa che qualsiasi inquadratura pianificata che richieda un fotogramma ferma
  **l'intero passaggio** con `EGRESS_REFUSED` prima di acquistare qualsiasi cosa: o lo imposti su `true` (il tuo
  permesso esplicito) o pianifichi inquadrature senza fotogrammi.
- Con il permesso, il fotogramma **viene inviato**: ReelBolt ritaglia un fotogramma dall'inquadratura indicata nel piano (il
  primo momento dell'inquadratura per un fotogramma iniziale, l'ultimo per uno finale) e lo invia al
  servizio di generazione video come parte della richiesta di quel clip. Il record di ogni clip indica se il fotogramma
  è stato inviato. Un'immagine statica aggiunta al progetto può essere usata allo stesso modo: viene analizzata come
  origine, quindi i suoi momenti sono normali id di inquadratura che il piano può nominare.
- Un servizio che non può accettare un'immagine viene rifiutato subito, con `KEYFRAME_NOT_SUPPORTED`,
  prima di acquistare qualsiasi cosa.

## Variante: due riprese per clip e una stanza dailies {#variation-two-takes-per-clip-and-a-dailies-room}

Aggiungi invece l'app **Montaggio del girato con b-roll AI, ripresa migliore scelta da un gruppo** (`video-derush-edit-dailies`).
Il suo passaggio di generazione crea **due riprese** di ogni clip pianificato e attiva l'**abbinamento dell'aspetto** (a ogni
clip viene chiesto di corrispondere al colore e al contrasto dell'inquadratura reale accanto) e la **continuità di camera**
(una parola della camera vuota copia la sensazione di camera ferma o a mano dell'inquadratura reale). Un secondo passaggio di analisi
misura ogni ripresa (`"source": {"kind": "GeneratedClips", "stepOrder": 4}`), un gruppo `DailiesRoom`
sceglie la ripresa migliore di ogni inquadratura tramite il suo id, e il
`"takeSelection": {"from": "Step", "stepOrder": 6}` del passaggio di compilazione colloca solo quelle. Un'inquadratura che il gruppo lascia
indecisa mantiene la prima ripresa. Aspettati più o meno il doppio del costo di generazione.

## Movimenti dinamici con un clip generato {#dynamic-moves-with-a-generated-clip}

Quando un'esecuzione ha un **ritmo degli effetti** (lo stile Montaggio dinamico, oppure Calmo, Medio o Veloce scelto accanto allo stile), entrambe le app
di b-roll aggiungono un passaggio `DynamicsPlanner` subito prima della compilazione. Gira dopo che i clip (e, nell'app dailies, le riprese scelte)
esistono, quindi, a differenza dell'editor della storia, può vederne gli id. Riceve una breve vista delle tue inquadrature e dei clip generati
utilizzabili, ciascuno con poche parole su cosa mostra, e della musica (il tempo, i battiti per battuta e se ha un drop), e risponde solo con
movimenti del catalogo del Montaggio dinamico: per esempio un'inquadratura ferma del tuo girato che si alterna a tempo con un'inquadratura notturna
generata per qualche battito, e poi vince quella notturna. Non può scrivere un tempo né un numero; la compilazione trova i battiti e scarta ogni
movimento che nomina un id che non gli è stato offerto. I suoi movimenti si uniscono a quelli dell'editor della storia, e quelli del pianificatore
prevalgono quando entrambi scelgono la stessa inquadratura e lo stesso movimento. È un normale passaggio AI, quindi usa pochi token come gli altri pianificatori;
senza un ritmo degli effetti non cambia nulla.

## Variante: un clip dal tuo prompt {#variation-one-clip-from-your-own-prompt}

Per un singolo clip descritto da te, senza il regista delle inquadrature, usa un passaggio `VideoGenerate` da
solo con `"planSource": "Inline"`:

```json
{"planSource": "Inline", "inlinePrompt": "Slow dolly shot through a sunlit greenhouse full of ferns",
 "durationSeconds": 6, "ratio": "16:9",
 "takes": 1, "maxClips": 1, "maxSpendCredits": 1.0}
```

Il primo clip diventa il video di output del passaggio (visibile in Video). Un passaggio `VideoAnalyze` successivo può
usarlo come origine con `{"kind": "PreviousStepOutput"}` oppure
`{"kind": "StepOutput", "stepOrder": 1}` (il numero del passaggio di generazione), così può essere montato come
qualsiasi altro girato.

## Insidie del b-roll generato {#pitfalls-with-generated-b-roll}

- **`CONFIG_INVALID` prima che accada qualsiasi cosa:** `maxSpendCredits` mancante o 0, `takes` sopra
  `maxClips`, una risoluzione o un rapporto non supportati dal modello, oppure `planSource` `"StepRef"` senza
  `plan`.
- **`BUDGET_EXCEEDED`:** il piano costa più di `maxSpendCredits`, oppure è stato raggiunto il tuo limite giornaliero di video. Non è stato acquistato
  nulla. Alza il limite del passaggio, alza o rimuovi il tuo limite in
  Impostazioni → Utilizzo, chiedi meno clip o clip più brevi, oppure aspetta il prossimo giorno UTC.
- **`EGRESS_REFUSED`:** vedi la sezione sulla privacy qui sopra.
- **`INVALID_PROMPT`:** un prompt vuoto o più lungo di 7000 caratteri (modalità inline).
- **`POLL_TIMEOUT`:** il provider ha impiegato più di `timeoutSeconds`. Il clip potrebbe comunque essere addebitato.
- **Clip che non compaiono nel video:** il passaggio 5 richiede `"enableGeneratedClips": true`, `generatedClips` che punti
  al passaggio 4 e `"mode": "Reencode"` (altrimenti `GENERATED_CLIPS_REQUIRE_REENCODE`). Un cutaway
  il cui momento è stato tagliato dal montaggio viene scartato.
- **Spesa ripetuta inattesa:** scegliere **Rigenera**, impostare `loopBackBehavior` su `"Regenerate"`,
  o cambiare il prompt o le impostazioni di un clip (anche se il regista delle inquadrature pianifica inquadrature diverse)
  comporta l'acquisto di nuovi clip.

## Ricetta: Montaggio del girato con b-roll generato dall'AI {#recipe-footage-edit-with-ai-generated-b-roll}

App: **Montaggio del girato con b-roll generato dall'AI**. Un montaggio dei momenti migliori più inquadrature extra generate
dall'AI: un'apertura a freddo, cutaway o una schermata finale.

**Da impostare prima di avviare.** I tuoi clip, la lingua parlata, la durata, il formato, lo stile e, nelle note,
le inquadrature extra che vuoi, in parole semplici. Il significato delle impostazioni è in [Impostazioni di esecuzione](run-settings.md).

**Quanto costa.** Usa crediti e inquadrature AI. La generazione è una spesa reale ed è limitata per ogni
esecuzione. La pagina dell'esecuzione mostra il prezzo e il limite di generazione prima che tu avvii, e il tuo piano
deve consentire le inquadrature AI.

**Cosa ottieni.** Un video montato con le inquadrature generate collocate dove le mette il piano delle inquadrature.

**Problemi comuni.**

- L'esecuzione si ferma al passaggio di generazione: non è collegato nessun provider di generazione video, oppure il tuo piano
  non include le inquadrature AI. Chiedi al tuo amministratore.
- Un'inquadratura non è come l'avevi descritta: indica soggetto, ambientazione e atmosfera nelle note, poi usa
  l'opzione di rigenerazione nell'esecuzione.
- Persone o girato privato nei clip: leggi la sezione sulla privacy di questa pagina prima di avviare.
