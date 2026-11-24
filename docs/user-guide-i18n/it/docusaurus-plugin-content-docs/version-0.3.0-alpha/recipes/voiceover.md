---
title: Aggiungi una voce narrante
sidebar_position: 8
description: Ricetta per aggiungere una voce narrante parlata, generata dal testo, a un video montato o a un promo creato dal codice.
i18n_source_sha: ae73012bdb97b976637666adac2f5163b448b19e
---

# Aggiungi una voce narrante {#add-a-voiceover}

**Obiettivo:** aggiungere una voce narrante parlata a un video. ReelBolt trasforma righe scritte in parlato con un
servizio vocale (Fish Audio) e le mescola nel video. Ci sono due modi, a seconda del tipo
di video:

- **Girato montato:** scrivi tu le righe e quando inizia ciascuna; il passaggio `VideoCompile` le mescola
  nel video.
- **Promo dal codice:** l'`ScriptwriterAgent` scrive già la voce narrante per ogni scena; un
  passaggio `Voiceover` la pronuncia e l'`AuthorAgent` mette ogni riga nella sua scena.

ReelBolt pronuncia esattamente il testo che riceve: il tuo, quello dello sceneggiatore o (con i template
per il girato qui sotto) quello di un autore della narrazione. Nessun template promo include una voce narrante; aggiungi tu un
passaggio `Voiceover` a un flusso di lavoro promo. Tre template per il girato ne includono una; vedi
[Far scrivere la voce narrante per il girato a un'AI](#letting-an-ai-write-the-narration-for-footage).

## Prerequisiti per una voce narrante {#prerequisites-for-a-voiceover}

- **Un provider vocale**: un amministratore ne aggiunge uno in Amministrazione, Provider di inferenza, con la capacità
  `SpeechSynthesis` (tipo `FishAudio` con una chiave API di Fish Audio, oppure tipo `OpenAICompatible` per
  il server vocale locale gratuito incluso) e lo imposta come predefinito (oppure il passaggio lo indica con
  `providerId`).
- **Un id di voce** di Fish Audio (il suo "reference id" per la voce che vuoi), usato come
  `defaultVoiceId`.
- Nota sulla licenza: il servizio ospitato di Fish Audio non ha una restrizione non commerciale documentata; eseguire
  i modelli aperti di Fish Audio in autonomia è coperto da una licenza separata che richiede un accordo a pagamento
  per l'uso commerciale.

## Voce narrante per il girato montato {#voiceover-for-edited-footage}

Parti da un qualsiasi flusso di lavoro per il girato, per esempio il template **Montaggio dei momenti migliori dal girato**
(`video-derush-edit`). Aggiungi un passaggio `Voiceover` prima del passaggio di compilazione e attiva la voce narrante nel
passaggio di compilazione. Esempio di struttura:

| # | Etichetta | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analizza il video sorgente | `VideoAnalyze` | `VideoTransform` |
| 2 | Decidi quali tratti tenere | `Agent` | `VideoStoryEditor` |
| 3 | Sintetizza la voce narrante | `Voiceover` | `VideoTransform` (non è un'AI) |
| 4 | Compila il video montato | `VideoCompile` | `VideoTransform` |
| 5 | Verifica la qualità del montaggio | `ReviewLoop` ritorno al passaggio 2 | `VideoReviewAgent` |

**Passaggio 3** (`voiceoverConfigJson`):

```json
{
  "source": {
    "kind": "Inline",
    "lines": [
      {"text": "This is how we built our first prototype.", "startSec": 2.0},
      {"text": "Three months later, it shipped.", "startSec": 41.5, "voiceId": "OTHER-VOICE-ID"}
    ]
  },
  "defaultVoiceId": "YOUR-FISH-AUDIO-VOICE-ID"
}
```

- `startSec` è misurato sull'orologio del video **originale, non tagliato**. ReelBolt sposta ogni riga nel
  momento giusto del video montato. Una riga il cui inizio è stato tagliato dal montaggio viene scartata.
- `voiceId` per riga è facoltativo; altrimenti si usa `defaultVoiceId`.
- Il testo tra parentesi come `[laughs]` o `(pause)`, i link e i caratteri di controllo vengono rimossi prima di
  pronunciare; una riga che resta vuota viene saltata e mai fatturata.

**Passaggio 4**: poiché sono stati inseriti dei passaggi, punta all'editor tramite il suo numero:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableVoiceover": true, "voiceoverStepOrder": 3}
```

Nota che `voiceoverStepOrder` è un semplice numero di passaggio, non un riferimento `{"from": ...}`. La voce narrante
richiede `"mode": "Reencode"` (il valore predefinito). La voce narrante non viene abbassata; se è attiva anche la musica di sottofondo,
la musica non sale mentre la voce narrante è in riproduzione.

## Voce narrante per un promo creato dal codice {#voiceover-for-a-promo-made-from-code}

Nei template promo l'`ScriptwriterAgent` scrive la voce narrante per ogni scena. Per farla pronunciare:

1. Aggiungi un passaggio `Voiceover` **dopo** il passaggio `ScriptwriterAgent` e **prima** del passaggio
   `AuthorAgent`.
2. Usa lo sceneggiatore come origine:

```json
{"source": {"kind": "ScriptScenes", "scriptwriterStepOrder": 8},
 "defaultVoiceId": "YOUR-FISH-AUDIO-VOICE-ID"}
```

3. Avvia il flusso di lavoro. L'`AuthorAgent` raccoglie da sé le righe pronunciate, mette ciascuna nella sua
   scena e genera il promo con la voce narrante. Le scene senza testo di narrazione non hanno audio.

In **Video promo dal tuo codice** (`quick-win-promo`) lo sceneggiatore è il passaggio 8, quindi il nuovo passaggio
va inserito come passaggio 9 e i passaggi successivi scalano di uno: il regista diventa 10, l'Author 11 e il
passaggio di revisione 12. **Aggiorna `loopTargetStepOrder` del passaggio di revisione a 11** perché continui a rimandare il lavoro
all'Author. In **Video promo dal tuo codice (più leggero)** lo sceneggiatore è il passaggio 9. Imposta sempre
`scriptwriterStepOrder` sul numero effettivo dello sceneggiatore.

I due template promo con voce narrante, **Reel verticale veloce** (`cinematic-feature-spotlight`) e
**Trailer di lancio con voce narrante** (`story-arc-launch-trailer`), hanno già questo passaggio, subito dopo lo
sceneggiatore e prima del regista, quindi devi solo impostare la voce.

## Cosa controllare dopo aver aggiunto una voce narrante {#what-to-check-after-adding-a-voiceover}

- **Il risultato del passaggio `Voiceover`** elenca ogni riga (`vo0`, `vo1`, ...) con il suo stato (`ok`,
  `failed` oppure `skipped`) e la sua durata parlata. `meta.allLinesFailed: true` significa che non è stato pronunciato
  nulla (di solito un problema del provider o dell'id della voce).
- **Girato montato:** il risultato di `VideoCompile` ha una sezione voce narrante che riporta quante righe
  sono state applicate, quali sono state scartate e perché (per esempio una riga il cui clip è stato tagliato). Ogni riga
  viene riprodotta sul clip per cui è stata scritta, anche in un montaggio fatto con molti clip. Se alcune righe sono state
  scartate, l'esecuzione mostra un avviso; se ne è stata scartata più della metà, il passaggio di revisione rimanda il montaggio
  indietro per un altro tentativo invece di approvarlo.
- **Promo:** l'`AuthorAgent` riporta quali scene hanno ricevuto la voce narrante nella nota `metadata.audio` del suo
  output. Un promo senza passaggio `Voiceover` dice lì "No audio: this workflow has no
  narration step, so the video is silent", invece di consegnare il silenzio senza una parola.

## Insidie della voce narrante {#pitfalls-with-voiceover}

- **Nessun provider vocale:** il passaggio non può pronunciare nulla. Chiedi a un amministratore di aggiungere un provider Fish Audio o
  compatibile con OpenAI con la capacità `SpeechSynthesis`; gli altri tipi di provider non possono essere usati
  per il parlato.
- **Numeri di passaggio sbagliati dopo aver inserito il passaggio:** controlla `voiceoverStepOrder`,
  `scriptwriterStepOrder`, il `decision` del passaggio di compilazione e il `loopTargetStepOrder` di ogni ciclo di revisione. In **Video promo dal tuo codice (più leggero)**, i passaggi agente successivi leggono anche un elenco scelto a mano
  di passaggi precedenti tramite il numero (contesto `SelectedPriorSteps`); rinumera anche quegli elenchi.
- **`ScriptScenes` collocato dopo l'Author:** l'Author usa solo la voce narrante che esiste già
  quando gira; metti il passaggio `Voiceover` prima.
- **Righe sovrapposte:** è ammesso; vengono riprodotte una sull'altra. Distanzia i tuoi valori `startSec` in base alla lunghezza di ogni
  riga.
- **Una voce clonata che non funziona:** un `projectVoiceId` il cui consenso è stato revocato o la cui
  conservazione è terminata fa fallire il passaggio di proposito (`VOICE_CONSENT_MISSING`). Clona di nuovo la voce con un
  nuovo consenso, oppure usa una voce della libreria.
- **Quale voce parla quando non ne hai scelta una:** se il progetto ha esattamente una voce clonata utilizzabile,
  un passaggio di narrazione senza una voce propria la usa. Con due o più, il passaggio si ferma e ti chiede di sceglierne una
  (`VOICE_CHOICE_REQUIRED`) invece di narrare con la voce predefinita.
- **Nessuna sincronizzazione labiale:** i pickup e i doppiaggi cambiano solo il suono. La bocca sullo schermo è quella della ripresa
  originale, e il risultato lo dice (`lipsync.applied: false`).
- **Non supportato:** rimontare il girato per adattarlo alla voce narrante (la revisione chiede invece parole
  più brevi) e la sincronizzazione labiale.

## Far scrivere la voce narrante per il girato a un'AI {#letting-an-ai-write-the-narration-for-footage}

Il template **Montaggio del girato con voce narrante e sottotitoli** (`video-derush-edit-voiceover`) aggiunge un
agente `NarrationWriter` che scrive le righe per il montaggio, ciascuna legata a un'inquadratura, a una pausa o a una frase parlata
piuttosto che a un tempo, e un passaggio `Voiceover` con `"kind": "NarrationPlan"` che le pronuncia,
con i sottotitoli karaoke attivi. L'editor taglia per primo e l'autore lo segue. Quando una riga è
più lunga del momento a cui è legata, il risultato riporta l'eccedenza e il passaggio di revisione chiede all'autore
parole più brevi; l'immagine non viene mai rimontata sulla voce. Vedi
[Template](../workflows/templates.md#footage-edit-with-narration-and-captions-video-derush-edit-voiceover).

Per **scrivere prima la storia** e scegliere e ordinare i clip in base a essa, usa il template
**Storia di prodotto con voce narrante** (`narrated-story`): lo stesso `NarrationWriter` gira prima dell'editor, a partire dal tuo brief e da ciò che l'analisi ha visto in ogni clip, e l'editor tiene i clip a cui ogni
riga è legata, nell'ordine del copione. Vedi
[Template](../workflows/templates.md#narrated-product-story-narrated-story).

## Sottotitoli dalla voce narrante {#captions-from-the-narration}

Nel passaggio **Compila**, apri **Voce narrante e sottotitoli** e attiva **Incorpora i sottotitoli** (in JSON,
`"enableCaptions": true`). Le parole impresse nell'immagine sono quelle **effettivamente
pronunciate** (dopo che ReelBolt ha rimosso tutto ciò che non legge ad alta voce), sincronizzate sul parlato misurato.

**Scegli uno stile:**

- **Sottotitolo** (predefinito) mostra una o due righe alla volta su un riquadro trasparente. Funziona ovunque.
- **Karaoke** illumina ogni parola con il colore della parola pronunciata nel momento in cui viene detta.
- **Grassetto** mostra parole grandi e in grassetto, due o tre alla volta, bianche con un contorno spesso e senza riquadro:
  l'aspetto usato dalla maggior parte dei Reel di Instagram e dei TikTok. In un video verticale si trova a circa due terzi
  dell'altezza, sopra i pulsanti e la didascalia che le app disegnano sul fondo dello schermo. Funziona
  anche senza un provider di trascrizione. Lo stile **Reel social veloce** lo sceglie automaticamente.
- **Punch** mostra una sola parola grande, in grassetto e con contorno, alla volta, al centro dell'inquadratura: l'aspetto usato dalla maggior parte
  dei video brevi.

Karaoke e Punch richiedono un **provider di trascrizione** perché ReelBolt possa sentire dove cade ogni parola;
senza, vengono saltati con il motivo `no_word_alignment`, e Sottotitolo funziona comunque.

**Scegli un carattere.** L'editor mostra un campione di ogni carattere così puoi vederlo prima di avviare. Lascialo
su **Automatico** per ottenere il carattere pensato per il tuo stile (Inter per Sottotitolo, Montserrat per Karaoke,
Anton per Punch), oppure scegli Inter, Montserrat, Poppins, Bebas Neue, Anton o DejaVu Sans. Ogni carattere
è integrato e con licenza per l'uso commerciale.

**Dimensione e posizione.** Lascia attiva **Dimensione del testo automatica**: i sottotitoli vengono allora dimensionati per lo stile e
per i telefoni, più grandi nei video verticali (9:16). Disattivala per impostare tu una dimensione, come percentuale
dell'altezza del video. **Posizione** è il fondo dell'inquadratura (o il centro per Punch) a meno che tu non
scelga In basso, Al centro o In alto. I sottotitoli restano sempre lontani dai bordi, e nei video verticali
restano fuori dal quinto inferiore e dalla parte alta dell'inquadratura, dove TikTok, Reels e Shorts mettono i propri
pulsanti e testi.

**Colori.** Scegli il colore del testo, il riquadro dietro (trasparente scuro o chiaro, nero pieno, oppure nessun
riquadro con testo contornato) e, per Karaoke, il colore della parola pronunciata. Punch non usa mai un riquadro.

I sottotitoli vengono disegnati per ultimi, quindi non sfumano mai con l'immagine. Il risultato del passaggio di compilazione ha una
sezione `captions` che dice quali carattere, posizione e dimensione sono stati usati; se mostra
`degradeReason: "libass_unavailable"`, al motore video mancava il suo renderer dei sottotitoli e sono stati disegnati
sottotitoli più semplici. Anche i sottotitoli salvati prima che esistesse il dimensionamento automatico (dimensione `4`) ricevono ora
la dimensione automatica.

## La voce narrante alla fine del video {#narration-at-the-end-of-the-video}

L'ultima riga della voce narrante termina sempre. Se fosse ancora in riproduzione quando l'immagine finisce,
ReelBolt mantiene l'ultimo fotogramma del video un po' più a lungo (fino a un quarto di secondo dopo la fine
della riga) invece di tagliare la voce. Una dissolvenza in uscita alla fine del video
(`programAudioFadeOutMs`) sfuma la musica, il suono originale e gli effetti sonori, mai la voce
narrante, quindi una frase pronunciata durante la dissolvenza si sente per intero.

Il risultato del passaggio di compilazione ti dice quando è successo: `voiceover.extendedForNarrationSec` indica per
quanti secondi è stato mantenuto l'ultimo fotogramma, e la durata del video (`outputDurationSec`) li include.

## Clonare una voce (con consenso) {#cloning-a-voice-with-consent}

La scheda **Voci** del progetto elenca le voci che un passaggio `Voiceover` può usare tramite `projectVoiceId`:

- Le **voci della libreria** sono voci che il provider vocale offre già; registrane una con il suo id.
- Le **voci clonate** sono create da registrazioni di una persona reale già presenti nel progetto.
  Clonare richiede un provider Fish Audio. Prima che ReelBolt invii qualsiasi cosa, devi spuntare la dichiarazione di consenso
  parola per parola e indicare la persona; la dichiarazione, chi l'ha spuntata e quando vengono conservati
  con la voce. Scegli per quanto tempo la voce viene conservata (fino a due anni). **Revoca il consenso** impedisce
  a ogni passaggio di usare subito la voce; **Elimina** rimuove la voce dal provider, le
  registrazioni dal progetto e la voce stessa, e riporta cosa ha rimosso. Le voci oltre la data di
  conservazione vengono eliminate automaticamente.

Un passaggio che nomina una voce clonata senza un consenso valido fallisce; non ripiega mai su un'altra voce.

## Insegnare al narratore come pronunciare una parola {#teaching-the-narrator-how-to-say-a-word}

Se il narratore dice male un nome o una parola, apri la scheda **Pronuncia** del progetto. Scrivi
la parola, poi la stessa parola con un accento sulla sillaba da enfatizzare, per esempio `Novara` e
`Novàra`. Scrivi la seconda grafia come la parola deve essere letta: aggiungi un accento, o cambia le lettere (per esempio `Xiaomi` e `Sciaomi`). Usa solo lettere e accenti, niente cifre né link. ReelBolt controlla ogni grafia che salvi e rifiuta quella che viola le regole sui contenuti. Spunta **Rispetta le maiuscole esatte** solo quando le stesse lettere in un'altra forma di maiuscole/minuscole devono essere
lasciate com'è. L'elenco si applica solo alla voce narrante parlata, mai ai sottotitoli o al testo a schermo,
e funziona in ogni video con voce narrante del progetto. Puoi conservare fino a 200 parole e puoi
modificarne o eliminarne qualsiasi. L'assistente può anche aggiungere una parola per te quando glielo chiedi.

**Se il tuo provider Fish è un server vocale Fish che gestisci tu** (il suo indirizzo non è
`api.fish.audio`), la clonazione funziona in modo un po' diverso, e ReelBolt se ne accorge da solo:

- Usa **esattamente una** registrazione, e deve essere un file **.wav**. Funzionano meglio 10–30 secondi chiari della persona
  che parla.
- Il server deve sapere cosa si dice nella registrazione, quindi ReelBolt lo trascrive prima usando il tuo
  provider **Trascrizione** predefinito. Senza, la clonazione si ferma con un messaggio che ti chiede di configurarne
  uno. Se quel provider è un servizio ospitato e non locale, anche la registrazione viene inviata lì.
- Consenso, periodo di conservazione, **Revoca il consenso** ed **Elimina** funzionano esattamente come sopra. Elimina rimuove
  anche la voce dal tuo server.

## Pickup con la voce del presentatore {#pickups-in-the-presenters-own-voice}

Il template **Correggi le battute sbagliate in un video con una persona che parla** (`video-derush-edit-pickups`) richiede
girato con una persona che parla con un parlato chiaro. Lascia che un
agente `PickupPlanner` individui le frasi su cui il presentatore ha inciampato e scriva la frase corretta
per ciascuna. Imposta `projectVoiceId` nel suo passaggio `Voiceover` su una voce clonata con consenso del
presentatore, e il passaggio di compilazione silenzia la frase originale e riproduce al suo posto il pickup.
L'immagine non cambia.

## Doppiaggio in un'altra lingua {#dubbing-into-another-language}

Il template **Video doppiato in un'altra lingua** (`video-derush-edit-dub`) richiede girato con
parlato. Traduce il piano della narrazione
con un agente `NarrationTranslator` (indica la lingua nel riquadro della richiesta quando avvii), pronuncia
la traduzione, sostituisce il parlato originale sotto ogni riga (`"voiceoverMode":
"ReplaceDialogue"`) e imprime i sottotitoli di ciò che è stato pronunciato. Per abbassare soltanto il parlato originale
sotto la voce narrante, usa invece `"voiceoverMode": "DuckDialogue"`.

## Quando il girato parla già {#when-the-footage-already-speaks}

Per impostazione predefinita l'audio del girato continua a suonare sotto la voce narrante. Se il video da cui parti
contiene già la stessa narrazione (per esempio un video che l'assistente ha montato in precedenza), la sentiresti
due volte. Quando quel film è fatto soprattutto di parlato, l'esecuzione spegne da sola l'audio del film e
aggiunge una nota al risultato ("il video ha già la narrazione nel suo audio ..."). La musica, gli effetti sonori
e la nuova narrazione suonano comunque.

Se nel tuo girato ci sono persone che parlano dove va la narrazione, l'esecuzione non cambia l'audio e
aggiunge soltanto una nota. Per abbassare quel parlato sotto la narrazione usa `"voiceoverMode":
"DuckDialogue"`; per rimuoverlo usa `"ReplaceDialogue"`. Questi limiti sono valori di partenza che non sono
ancora stati tarati su video reali.

## Un servizio vocale locale e gratuito {#a-free-local-speech-service}

Un amministratore può registrare il server vocale locale incluso come provider **compatibile con OpenAI** con la
capacità `SpeechSynthesis` (endpoint `http://whisper:8000/v1`, modello `speaches-ai/Kokoro-82M-v1.0-ONNX`, id di voce come
`af_heart`). Non costa nulla e non invia audio a nessun fornitore, ma non può clonare le voci.

## Ricetta: Montaggio del girato con voce narrante e sottotitoli {#recipe-footage-edit-with-narration-and-captions}

Template: **Montaggio del girato con voce narrante e sottotitoli**. Taglia prima i tuoi clip, poi scrive una
riga di voce narrante per le inquadrature tenute, la pronuncia e aggiunge i sottotitoli.

**Da impostare prima di avviare.** I tuoi clip, **Lingua** (la lingua della voce narrante e dei sottotitoli),
durata, formato, stile e **Voce narrante** con la voce. Usa le note per l'argomento e il tono. Per
una storia scritta prima del taglio, usa invece una ricetta di storia con voce narrante. Il significato delle impostazioni è in
[Impostazioni di esecuzione](run-settings.md).

**Quanto costa.** Usa crediti e una voce: l'editor, l'autore della narrazione, il parlato e
il revisore. La pagina dell'esecuzione mostra il prezzo.

**Cosa ottieni.** Un video montato con una voce narrante parlata e sottotitoli impressi.

**Problemi comuni.**

- L'esecuzione si ferma al passaggio vocale: non è collegato nessun provider vocale, o non è scelta nessuna voce. Chiedi
  al tuo amministratore, e scegli una voce per cui hai il consenso.
- La voce narrante è nella lingua sbagliata: imposta **Lingua** invece di chiederla nelle note.
- La voce narrante va oltre l'immagine: riduci la durata o accorcia ciò che le chiedi di coprire.
