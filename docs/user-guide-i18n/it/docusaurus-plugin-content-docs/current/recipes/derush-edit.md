---
title: Asciuga un video con una persona che parla (montaggio derush)
sidebar_position: 5
description: Ricetta per eliminare tempi morti, false partenze e ciak ripetuti dal girato, con un singolo editor AI o un gruppo di editor AI.
i18n_source_sha: 88b7918a51fd83541414bacde89445d7028af28b
---

# Asciuga un video con una persona che parla (montaggio derush) {#tighten-a-talking-head-video-derush-edit}

:::note
Questa app ora si apre come parte di [Edit my video](edit-my-video.md): scegli lì il campo corrispondente. Questa pagina resta come riferimento su ciò che fa ogni passaggio.
:::

**Obiettivo:** prendere una registrazione grezza (un video con una persona che parla, un'intervista, un vlog o una registrazione dello schermo con
voce narrante) e ottenere un montaggio più asciutto, senza pause lunghe, false partenze e riprese ripetute. Il
risultato è un nuovo MP4 (`edited.mp4`).

"Derush" è la parola dei montatori per indicare lo spoglio del girato grezzo ("rushes") per tenere solo le parti
buone. In ReelBolt un editor AI sceglie quali parti tenere, e il taglio preciso lo esegue ReelBolt stesso.

## Prerequisiti per un montaggio derush {#prerequisites-for-a-derush-edit}

- **Il video caricato nel progetto** (scheda File). Fino a un'ora e 30 GB per video. Un video più lungo o
  più grande viene rifiutato prima che venga addebitato qualcosa. Il file deve anche stare nello spazio di
  archiviazione del tuo piano, che nella prova gratuita è di 1 GB (vedi
  [Archiviazione in uno spazio di lavoro ReelBolt](../account/plans-and-credits.md#storage-in-a-reelbolt-workspace)).
- **Un provider di chat AI** per l'editor.
- **Fortemente consigliato: un provider di trascrizione**, così l'editor sa cosa viene detto e
  tiene le frasi intere. Un amministratore ne aggiunge uno in Amministrazione, Provider di inferenza, con la capacità
  `Transcription`. Opzioni:
  - il servizio locale `whisper` incluso in ReelBolt (gratuito, gira sul tuo server; tipo
    `OpenAICompatible`, endpoint `http://whisper:8000`);
  - un servizio di riconoscimento vocale Azure OpenAI o altro compatibile con OpenAI.

  I provider Anthropic (Claude), Gemini e DeepSeek non possono trascrivere. Senza alcun provider di
  trascrizione il montaggio funziona comunque, ma l'editor vede solo pause e cambi di inquadratura, non le parole.

## Il flusso di lavoro derush {#the-derush-workflow}

Aggiungi l'app **Montaggio dei momenti migliori dal girato** (`video-derush-edit`) da **Nuovo flusso di lavoro**. I suoi passaggi:

| # | Etichetta | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analizza il video sorgente | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decidi quali tratti tenere | `Agent` | `VideoStoryEditor` |
| 3 | Compila il video montato | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 4 | Verifica la qualità del montaggio | `ReviewLoop` | `VideoReviewAgent` (ritorno al passaggio 2 sotto 8, fino a 3 giri) |

**Passaggio 1**: aprilo e seleziona il tuo video come origine. Le sue impostazioni appaiono così:

```json
{"version": 1, "source": {"kind": "ProjectFile", "projectFileId": "PUT-THE-FILE-ID-HERE"}}
```

Il riconoscimento vocale viene usato automaticamente quando esiste un provider (`"transcription": "Optional"`). Usa
`"transcription": "Required"` per far fallire l'esecuzione in modo chiaro invece di montare senza parole, e
`"language": "en"` (o la tua lingua) se il rilevamento sbaglia.

**Passaggio 3**: com'è fornito:

```json
{"version": 1, "decision": {"from": "Previous"}, "analysisStepOrder": 1,
 "transitionPolicy": "Auto", "programFadeInMs": 500, "programFadeOutMs": 800,
 "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900, "minSegmentMs": 800}
```

Crea giunzioni morbide e automatiche tra i tagli, applica la dissolvenza all'inizio e alla fine, ed elimina i pezzi tenuti
più brevi di 0,8 secondi.

I tagli sono posizionati in modo da non cadere mai in mezzo a una parola. L'editor vede uno schema dell'intero video prima di scegliere cosa tenere, gli effetti sonori vengono messi solo su momenti reali dell'immagine e chi parla è etichettato in ciò che l'editor legge.

## Come avviare un montaggio derush {#how-to-run-a-derush-edit}

1. Facoltativo ma utile: quando fai clic su **Avvia**, scrivi un brief nella finestra di conferma per dire
   all'editor cosa vuoi, per esempio "Taglia a circa 90 secondi, tieni la storia
   del lancio, elimina la digressione fuori tema verso la fine".
2. Avvialo. Il passaggio 1 dura circa quanto il video più il tempo del riconoscimento vocale; il passaggio 2 è una
   decisione dell'AI; il passaggio 3 codifica il video.
3. Guarda il risultato in **Video finale** del passaggio 3, oppure nella scheda Video del progetto. Il montaggio viene anche
   aggiunto ai File del progetto.

## Cosa controllare dopo un montaggio derush {#what-to-check-after-a-derush-edit}

- Risultato del **passaggio 1**: `meta.transcription.applied` dovrebbe essere `true` se ti aspettavi delle parole. Se indica
  `degraded`, il riconoscimento vocale è fallito o non è stato trovato nessun provider. Se mostra `droppedNoSpeech`, quel
  numero di "parole" è stato scartato perché sotto non c'era parlato: il riconoscimento vocale tende a
  inventare frasi come "Grazie per la visione." su clip con sola musica o silenziose, e ReelBolt
  le scarta così l'editor non le scambia mai per dialogo. I clip senza alcun suono non vengono
  trascritti.
- Risultato del **passaggio 2**: l'elenco dei tratti tenuti (quali id sono stati tenuti) e la spiegazione dell'editor del
  perché.
- Risultato del **passaggio 3**: quanto dura il risultato e la timeline **Apri nell'editor** per vedere ogni taglio.
- Risultato del **passaggio 4**: il punteggio della revisione e i commenti. Un punteggio basso rimanda l'esecuzione al passaggio 2 con i
  commenti.

## Movimenti sui momenti del tuo video {#moves-on-the-moments-of-your-video}

Ogni app sul girato, questa e le sue varianti (musica, effetti sonori, voce narrante, b-roll, doppiaggio, riprese di recupero, grafica e le versioni con il team), cerca i momenti che accadono nel tuo girato, come qualcosa che compare, un colpo, un movimento di camera o un cambio di scena. Non costa nulla: gira sul tuo server e non chiama alcuna AI. Un passaggio **Plan motion on events** lascia poi che un'AI scelga, per alcuni di quei momenti, uno zoom lieve in avanti, un rallentatore, un lampo o una breve scossa, e il passaggio di compilazione li applica. L'editor può anche iniziare o finire un pezzo tenuto su uno di quei momenti. Niente viene scelto a tempo o a numero: l'AI indica i momenti e parole come Subtle o Strong, e ReelBolt fa il lavoro esatto. Il piano è limitato a pochi movimenti, e un movimento che cadrebbe su una parte tagliata viene scartato. Il passaggio di revisione può chiedere modifiche a queste impostazioni come a qualsiasi altra.

I flussi di lavoro che hai già non cambiano; una nuova app creata da questa ottiene questi passaggi. Per disattivarli in una nuova app, elimina il passaggio **Plan motion on events** e disattiva **Cut on events** e **Motion** nel passaggio di compilazione.

## Variante: un gruppo di editor (stanza di montaggio) {#variation-a-panel-of-editors-edit-room}

L'app **Montaggio dei momenti migliori, deciso da un team di montaggio** (`video-derush-edit-room`) sostituisce il passaggio 2 con un
passaggio `EditRoom`: tre editor AI (ritmo, storia, mestiere) discutono il girato e un direttore scrive
la decisione finale. È più lento e usa più chiamate AI, ma tende a fare scelte più ponderate
su girato lungo o narrativo. Se il gruppo fallisce, decide un singolo editor
(`"fallbackToSoloEditor": true`).

```json
{"version": 1, "view": {"from": "Previous"}}
```

Puoi dare alla stanza i tuoi posti, per esempio:

```json
{"version": 1, "view": {"from": "Previous"},
 "seats": [
   {"name": "BrandEditor", "persona": "argues for keeping every product mention and the call to action"},
   {"name": "PacingEditor", "persona": "argues for rhythm and removing dead air"}
 ]}
```

La discussione della stanza viene conservata nel risultato del passaggio per essere letta; conta solo la decisione
finale del direttore.

## Altre varianti del montaggio derush {#other-variations-of-the-derush-edit}

- **Più riprese o angolazioni:** nel passaggio 1 usa `"sources": [ ... ]` con più video al posto di
  `source`. Tutti i clip vengono analizzati insieme e l'editor può scegliere le parti migliori da uno qualsiasi di essi.
- **Aprire con l'inquadratura migliore, non con la prima:** per impostazione predefinita le parti di un video vengono riprodotte nell'ordine
  in cui sono state filmate. Imposta **Ordine delle clip** su **Nell'ordine dell'editor** nel passaggio 3
  (`"keepOrder": "AsListed"`) e di' cosa vuoi nel brief, per esempio "apri sul momento
  in cui il prodotto viene lanciato, poi racconta come ci siamo arrivati". L'editor può allora spostare in testa un momento
  successivo. Non mostra mai due volte lo stesso momento. Serve il taglio normale (ricodificato).
- **Usare solo una parte di un video:** nel passaggio 1, compila **Usa da** e **Usa fino a** (secondi) su un clip
  per analizzare e montare solo quel tratto. Tutto ciò che sta
  fuori viene lasciato fuori dal montaggio (`"inSec": 12, "outSec": 48` sull'origine).
- **Tenere una parte di un'inquadratura lunga:** nel passaggio 1, imposta **Tagli nelle inquadrature lunghe** su **Dove l'immagine
  si ferma o cambia** (`"subShotMode": "Content"`). A un'inquadratura lunga vengono allora offerti punti di taglio dove
  l'immagine si ferma o cambia, e l'editor può tenere solo la parte buona. Scegli **Uniformi** per tagliare
  a intervalli uguali, oppure **Nessuno** per non avere punti di taglio. Le app di montaggio del girato, tranne quello dei
  giornalieri, partono da **Dove l'immagine si ferma o cambia**.
- **Tagli netti:** imposta `"transitionPolicy": "Off"` nel passaggio 3 (e le dissolvenze a 0).
- **Taglio veloce e senza perdita:** `"mode": "StreamCopy"` con `"allowKeyframeSnapping": true` nel passaggio 3.
  Un taglio continuo unico è una copia senza perdita: cade sul keyframe più vicino (meno preciso). Un taglio con
  più parti viene ricodificato, quindi non è senza perdita. Non sono possibili extra (grafiche, musica...).
- **Descrivere le inquadrature con l'AI:** `"vision": "Optional"` nel passaggio 1 aggiunge una breve descrizione AI di cosa
  c'è in ogni inquadratura (serve un provider `Vision`; costa una chiamata AI per ogni inquadratura descritta, al massimo
  `maxCaptionedShots`, predefinito 50).
- **Aggiungere extra:** grafiche, musica, effetti sonori, correzione colore, b-roll e voce narrante hanno ciascuna
  la propria app in questa sezione.

## Insidie di un montaggio derush {#pitfalls-of-a-derush-edit}

- **L'esecuzione fallisce al passaggio 1 con `SOURCE_UNRESOLVED`:** nessun video selezionato nel passaggio 1.
- **`EMPTY_KEEP` o `EXPECT_FAILED` al passaggio 3:** l'editor non ha tenuto nulla, o meno del 15% del
  girato. Dai istruzioni più chiare, oppure abbassa `expect.minRetainedRatio` nel passaggio 3 se è voluto un taglio
  molto breve.
- **Frasi tagliate a metà parola:** di solito manca la trascrizione. Aggiungi un provider di trascrizione.
- **Le riesecuzioni danno lo stesso montaggio:** l'app imposta l'editor in modo che non riutilizzi mai i risultati, ma se hai costruito una
  tua app, imposta `cacheMode` del passaggio dell'editor su `"Never"`.
- **Se aggiungi passaggi tra l'editor e il passaggio di compilazione,** cambia il `decision` del passaggio 3 da
  `{"from": "Previous"}` a `{"from": "Step", "stepOrder": 2}`: "Previous" punterebbe altrimenti al
  passaggio sbagliato.

## Ricetta: Montaggio dei momenti migliori dal girato {#recipe-highlight-edit-from-footage}

App: **Montaggio dei momenti migliori dal girato**.

**Da impostare prima di avviare.** I tuoi clip (da uno a venti), la lingua parlata nel girato, la durata
(sono proposti 30, 60 o 90 secondi), il formato, lo stile e una nota su cosa tenere. Non c'è
un'impostazione per voce narrante o design: il montaggio usa il tuo audio. Il significato delle impostazioni è in
[Impostazioni di esecuzione](run-settings.md).

**Quanto costa.** Usa crediti per l'editor AI e per il revisore. Niente voce e niente inquadrature
generate. Il prezzo compare nella pagina dell'esecuzione.

**Cosa ottieni.** Un video montato (`edited.mp4`) senza tempi morti, false partenze e ciak ripetuti,
e un controllo del revisore sul risultato.

**Problemi comuni.**

- L'esecuzione si ferma senza nessun clip scelto: scegli i tuoi clip nello slot dei file prima di avviare.
- Le parole sono tagliate a metà frase: il parlato non è stato trascritto. Chiedi al tuo amministratore di collegare un
  provider di trascrizione.
- Il montaggio non tiene quasi nulla, o non supera il controllo: scrivi nelle note cosa deve restare, oppure chiedi una
  durata maggiore.

## Ricetta: Montaggio dei momenti migliori, deciso da un team di montaggio {#recipe-highlight-edit-decided-by-an-editing-team}

App: **Montaggio dei momenti migliori, deciso da un team di montaggio**. Tre editor AI (ritmo, storia, mestiere)
discutono il taglio e un editor capo decide.

**Da impostare prima di avviare.** Le stesse impostazioni del montaggio con un solo editor: clip, lingua parlata,
durata, formato, stile, note.

**Quanto costa.** Usa più crediti del montaggio con un solo editor e richiede più tempo, in
cambio di un taglio più ponderato. La pagina dell'esecuzione mostra il prezzo.

**Cosa ottieni.** Un video montato, come per il montaggio standard. La discussione degli editor viene conservata con
l'esecuzione per essere letta.

**Problemi comuni.**

- Girato breve o semplice: il team aggiunge costo senza un vantaggio visibile. Usa il montaggio con un solo editor.
- Il gruppo fallisce: decide invece un singolo editor, quindi ottieni comunque un montaggio.
- I problemi di clip, trascrizione e montaggio vuoto sono gli stessi di
  [Montaggio dei momenti migliori dal girato](#recipe-highlight-edit-from-footage).
