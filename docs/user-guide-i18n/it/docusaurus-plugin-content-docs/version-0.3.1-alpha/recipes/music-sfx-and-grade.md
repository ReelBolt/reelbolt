---
title: Aggiungi musica, effetti sonori e correzione colore
sidebar_position: 7
description: Ricette per aggiungere a un video montato un letto musicale di sottofondo, brevi effetti sonori e un aspetto cromatico generale, singolarmente o tutti insieme.
i18n_source_sha: d369050a93f0504ac3ea73a68a9140c163397d2c
---

# Aggiungi musica, effetti sonori e correzione colore {#add-music-sound-effects-and-a-colour-grade}

Questa pagina tratta tre rifiniture per un video montato, ciascuna con il proprio template, e come
combinarle in un solo flusso di lavoro:

- **Musica di sottofondo**: una traccia musicale sotto tutto il montaggio, più bassa mentre le persone parlano.
- **Effetti sonori**: brevi suoni (whoosh, clic, stinger) in momenti scelti.
- **Correzione colore**: un solo aspetto generale (caldo, freddo, cinematografico, ...) applicato a tutto il video.

In tutti e tre i casi un'AI sceglie **solo con parole e id** (quale traccia, "Balanced", "Warm", ...), e
ReelBolt trasforma queste scelte in livelli di volume, tempi e impostazioni di colore esatti. Tutti e tre richiedono
`"mode": "Reencode"` nel passaggio `VideoCompile` (il valore predefinito).

## Prerequisiti per musica ed effetti sonori {#prerequisites-for-music-and-sound-effects}

- Il video caricato nel progetto.
- **File audio caricati nel progetto** (per esempio MP3 o WAV). Non esiste una libreria integrata di musica o
  effetti sonori, quindi carica prima le tue tracce e i tuoi effetti. Ogni file audio del
  progetto viene offerto sia come possibile traccia musicale sia come possibile effetto sonoro. Se non ne hai
  caricato nessuno, i passaggi di musica ed effetti sonori non scelgono nulla e il loro motivo lo dice in parole
  semplici ("No music: this project has no audio files to choose from…").
- Dai ai file nomi chiari (`calm-piano-bed.mp3`, `whoosh-short.wav`): l'AI sceglie in base al nome del file.
- Assicurati di avere i diritti per usare la musica e i suoni.

## Ricetta musica di sottofondo {#background-music-recipe}

Aggiungi il template **Montaggio del girato con musica di sottofondo** (`video-derush-edit-music`) e seleziona il tuo video
nel passaggio 1.

| # | Etichetta | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analizza il video sorgente | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decidi quali tratti tenere | `Agent` | `VideoStoryEditor` |
| 3 | Scegli la musica di sottofondo | `Agent` (contesto `FullWorkflow`) | `MusicSupervisor` |
| 4 | Compila il video montato con la musica | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 5 | Verifica la qualità del montaggio | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 1 aggiunge `"offerMusicTracks": true`, che offre ogni file audio come `m0`, `m1`, ... (fino a
`maxMusicTracks`, 20). Passaggio 4:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableMusic": true, "musicPlan": {"from": "Step", "stepOrder": 3},
 "transitionPolicy": "Auto", "programFadeInMs": 500, "programFadeOutMs": 800,
 "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900, "minSegmentMs": 800}
```

Il `MusicSupervisor` sceglie al massimo una traccia, quanto deve essere presente (`Quiet`, `Balanced`, `Feature`),
quanto si abbassa mentre qualcuno parla (`Off`, `Light`, `Normal`, `Heavy`) e se si ripete per
riempire il montaggio (`LoopToFit`) o viene riprodotta una volta (`PlayOnce`). È ammesso non scegliere nessuna traccia. La musica sale
un poco nelle pause più lunghe e ha una dissolvenza in entrata (1,5 s) e in uscita (2,5 s).

**Senza l'AI:** salta il passaggio 3 e indica tu la traccia con
`"musicTrackProjectFileId": "ID-OF-THE-AUDIO-FILE"` nel passaggio 4 (mantieni `"enableMusic": true`, rimuovi
`musicPlan`). Questo file viene usato anche come riserva quando la scelta dell'AI non può essere utilizzata.

Impostazioni utili del passaggio 4: `musicDucking` (`"SpeechEnvelope"` predefinito, oppure `"Off"` per un letto
costante più basso), `musicFitPolicy` (`"LoopToFit"` predefinito oppure `"PlayOnce"`), `musicFadeInMs` (1500),
`musicFadeOutMs` (2500).

**Un reel tagliato a tempo di musica, senza l'audio dei clip:** imposta `"muteSourceAudio": true` nel passaggio 4
(l'interruttore **Silenzia l'audio originale delle clip** nell'editor). I clip generati hanno spesso un proprio
audio, che altrimenti si sentirebbe sotto la traccia. Quando nessuno parla e non c'è voce narrante, la
musica è l'intera colonna sonora e viene riprodotta al suo livello pieno, non al livello più basso che usa
sotto il parlato. Con la voce narrante ma senza audio dei clip, la musica sale tra le
battute della voce narrante e si abbassa sotto ciascuna, della quantità di ducking scelta dal passaggio della musica.

**Controllo:** il risultato del passaggio 4 ha una sezione musica che dice se la musica è stata applicata e, in caso contrario, perché no.
Il passaggio di revisione controlla anche che il parlato resti chiaramente sopra la musica.

## Tagliare a ritmo di musica {#cutting-to-the-beat-of-the-music}

Per un reel tagliato a tempo di musica, ReelBolt può misurare la musica e far cadere i tagli sul suo ritmo:

1. Nel passaggio 1, attiva **Misura il ritmo della musica** (`"analyzeMusicBeats": true`) e scegli la
   traccia sotto **Musica su cui è tagliato il video** (`"musicBeatTrackProjectFileId"`). All'editor viene quindi
   comunicato il tempo della musica (per esempio "veloce, 135 battiti al minuto"), quanto durano un battito e una battuta,
   dove si trova il drop (il momento in cui la musica colpisce più forte, per esempio "battuta 8, a 13,9 s") e
   dove si attenua. Vengono misurate anche le tracce musicali offerte.
2. Nel brief, chiedilo: "taglia a ritmo, metti la grande presentazione del prodotto sul drop".
3. Nel passaggio 4, imposta **Taglia a tempo** su **Su ogni battito** o **A ogni battuta**
   (`"beatSync": "Beat"` oppure `"Bar"`). Ogni taglio si sposta allora sul battito o sulla battuta più vicini
   accorciando un po' la fine di un'inquadratura, mai allungandola, e il taglio più vicino al drop
   cade esattamente su di esso quando l'introduzione è abbastanza lunga. Un'inquadratura troppo breve per contenere un battito intero resta
   com'è, e un'inquadratura non viene mai accorciata tanto da tagliare la voce narrante pronunciata sopra di essa.

Serve la musica di sottofondo attiva nel passaggio 4 (una traccia fissa o la scelta del passaggio della musica). Il risultato del
passaggio 4 ha una sezione `beatSync` che elenca ogni taglio: dov'era, dove si è spostato e se
è caduto su una battuta, un battito o sul drop. Misurare una traccia richiede alcuni secondi per traccia.

## Ricetta effetti sonori {#sound-effects-recipe}

Aggiungi il template **Montaggio del girato con effetti sonori** (`video-derush-edit-sfx`) e seleziona il tuo video nel
passaggio 1.

| # | Etichetta | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analizza il video sorgente | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decidi quali tratti tenere | `Agent` | `VideoStoryEditor` |
| 3 | Pianifica gli effetti sonori | `Agent` (contesto `FullWorkflow`) | `SoundDesigner` |
| 4 | Compila il video montato con gli effetti sonori | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 5 | Verifica la qualità del montaggio | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 1 aggiunge `"offerSfxClips": true`, che offre ogni file audio come `x0`, `x1`, ... (fino a
`maxSfxClips`, 40). Passaggio 4:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableSfx": true, "sfxPlan": {"from": "Step", "stepOrder": 3},
 "transitionPolicy": "Auto", "programFadeInMs": 500, "programFadeOutMs": 800,
 "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900, "minSegmentMs": 800}
```

Il `SoundDesigner` pianifica zero o più cue. Ogni cue è un suono offerto (un id `x`) che viene riprodotto quando
un'inquadratura, una pausa o una frase offerta inizia nel montaggio, con una parola per il tempo (`OnCut`, oppure `Lead` /
`Lag`, 150 ms prima o dopo) e una parola per il volume (`Subtle`, `Normal`, `Strong`). Nessun effetto è
un risultato valido.

Limiti nel passaggio 4: `maxSfxCues` (8) e `maxSfxCueSeconds` (4 secondi, i file più lunghi vengono tagliati).
Non esiste un'impostazione "lo stesso suono a ogni taglio"; la collocazione è sempre scelta dall'AI.

**Controllo:** il risultato del passaggio 4 elenca i cue applicati e quelli scartati con un motivo (per esempio un cue
il cui momento è stato tagliato dal montaggio). Gli effetti hanno bisogno di un po' di audio in cui mescolarsi: un video senza suono
e senza musica non riceve effetti.

## Ricetta correzione colore {#colour-grade-recipe}

Aggiungi il template **Montaggio del girato con correzione colore** (`video-derush-edit-grade-room`) e seleziona il tuo
video nel passaggio 1.

| # | Etichetta | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analizza il video sorgente | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decidi quali tratti tenere | `Agent` | `VideoStoryEditor` |
| 3 | Deliberazione della stanza di correzione colore | `ColorGradeRoom` | gruppo più `ColorGradeDirector` |
| 4 | Compila il video montato con la correzione colore | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 5 | Verifica la qualità del montaggio | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 1 non richiede impostazioni aggiuntive: misura per impostazione predefinita il colore di ogni inquadratura. Il `view` del passaggio 3 punta al
passaggio 1: `{"version": 1, "view": {"from": "Step", "stepOrder": 1}}`. Passaggio 4:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableColorGrade": true, "colorGradePlan": {"from": "Step", "stepOrder": 3},
 "transitionPolicy": "Auto", "programFadeInMs": 500, "programFadeOutMs": 800,
 "programAudioFadeInMs": 300, "programAudioFadeOutMs": 900, "minSegmentMs": 800}
```

Il gruppo (un artista dei toni, uno della palette e uno della continuità) discute il girato, e il
direttore sceglie un solo aspetto per tutto il video:

- `look`: `None`, `Warm`, `Cool`, `Filmic`, `Vibrant`, `Muted`, `Mono`
- `strength`: `Subtle`, `Normal`, `Strong`
- `shadowTone`: `Neutral`, `Lifted`, `Deepened`
- `highlightTone`: `Neutral`, `Softened`, `Brightened`

`None` (nessuna correzione) è un risultato perfettamente valido per un girato che ha già l'aspetto giusto.

**Alternativa più economica:** sostituisci il passaggio 3 con un solo passaggio `Agent` che esegue `Colorist` (contesto
`FullWorkflow`); dà lo stesso tipo di risposta, e il passaggio 4 resta uguale.

**Controllo:** il risultato del passaggio 4 ha una sezione di correzione colore che dice quale aspetto è stato applicato, o perché nessuno
lo è stato.

## Crea la musica o gli effetti sonori invece di caricarli {#make-the-music-or-sound-effects-instead-of-uploading-them}

Se il tuo piano include i contenuti generati, aggiungi un passaggio **Genera musica o suono**
(`AudioGenerate`) prima del passaggio di compilazione. Scegli **Musica** o **Effetto sonoro**, descrivi cosa
vuoi, imposta la durata e un **Limite di spesa (crediti)**, e avvia. Ogni risultato viene aggiunto ai file
del progetto come file audio, quindi i passaggi di musica ed effetti sonori lo offrono come uno che hai
caricato. Per farlo offrire nella stessa esecuzione, metti il passaggio di generazione prima del passaggio
`VideoAnalyze`; altrimenti è disponibile dall'esecuzione successiva. Vedi
[Passaggio Genera musica o suono](../workflows/step-types.md#generate-music-or-sound-step-audiogenerate).

## Riprodurre la musica a pieno volume {#play-the-music-at-full-volume}

Per impostazione predefinita il letto musicale sta basso sotto l'immagine. Per riprodurre una traccia al suo livello, imposta
`musicGainDb` sul passaggio di compilazione a `0` (l'intervallo consentito va da -40 a +6 dB). Il parlato può comunque abbassare
la musica quando il ducking è attivo. Un clip generato che arriva con un proprio suono è controllato da
`generatedClipAudio`: `Keep` lo lascia come sottofondo senza abbassare la musica, `Mute` lo rimuove.
Il risultato del passaggio riporta il livello della musica usato e come è stato trattato il suono di ogni clip generato.

## Combinare musica, effetti e correzione colore in un solo flusso di lavoro {#combining-music-effects-and-grade-in-one-workflow}

Puoi usare tutti e tre in un solo flusso di lavoro. Metti i passaggi AI tra l'editor e il passaggio di compilazione,
e fai puntare il passaggio di compilazione a ciascuno tramite il suo numero:

| # | Tipo di passaggio | Agente |
|---|---|---|
| 1 | `VideoAnalyze` con `"offerMusicTracks": true` e `"offerSfxClips": true` | `VideoTransform` |
| 2 | `Agent` | `VideoStoryEditor` |
| 3 | `Agent` (`FullWorkflow`) | `MusicSupervisor` |
| 4 | `Agent` (`FullWorkflow`) | `SoundDesigner` |
| 5 | `Agent` (`FullWorkflow`) | `Colorist` |
| 6 | `VideoCompile` | `VideoTransform` |
| 7 | `ReviewLoop` ritorno al passaggio 2 | `VideoReviewAgent` |

Passaggio 6:

```json
{"version": 1, "decision": {"from": "Step", "stepOrder": 2}, "analysisStepOrder": 1,
 "enableMusic": true, "musicPlan": {"from": "Step", "stepOrder": 3},
 "enableSfx": true, "sfxPlan": {"from": "Step", "stepOrder": 4},
 "enableColorGrade": true, "colorGradePlan": {"from": "Step", "stepOrder": 5}}
```

Imposta `cacheMode` dei passaggi AI su `"Never"` così a ogni esecuzione si ottengono scelte nuove.

## Insidie di musica, effetti e correzione colore {#pitfalls-with-music-effects-and-grade}

- **"Previous" punta al passaggio sbagliato.** In questi flussi di lavoro il passaggio subito prima del passaggio di compilazione
  è quello della musica, degli effetti o della correzione colore, non l'editor. Usa sempre `{"from": "Step", "stepOrder": N}`.
- **Non viene offerto nulla.** Musica ed effetti richiedono `offerMusicTracks` / `offerSfxClips` nel passaggio 1 e almeno
  un file audio nel progetto.
- **Lo stesso file usato come musica e come effetto.** Entrambi gli elenchi contengono ogni file audio; nomi di file
  chiari aiutano le AI a distinguere i letti musicali dai brevi effetti.
- **`MUSIC_REQUIRES_REENCODE`, `SFX_REQUIRES_REENCODE`, `COLOR_GRADE_REQUIRES_REENCODE`:** questi
  extra richiedono `"mode": "Reencode"`. Musica ed effetti falliscono anche con
  `MUSIC_REQUIRES_AUDIO_REENCODE` / `SFX_REQUIRES_AUDIO_REENCODE` quando `audioCodec` è `"copy"`.
- **Correzione colore con un inserimento su green screen.** Una correzione forte può rendere meno
  precisa la sagoma dello schermo; vedi la [ricetta inserimento su schermo tracciato](tracked-screen-insert.md).

## Ricetta: Montaggio del girato con musica di sottofondo {#recipe-footage-edit-with-background-music}

Template: **Montaggio del girato con musica di sottofondo**. Un montaggio dei momenti migliori con una traccia musicale mescolata sotto
le voci.

**Da impostare prima di avviare.** I tuoi clip, la lingua parlata, la durata, il formato, lo stile e le note su
cosa tenere. Carica prima la traccia musicale nei file del progetto: non esiste una libreria
musicale integrata. Il significato delle impostazioni è in [Impostazioni di esecuzione](run-settings.md).

**Quanto costa.** Usa crediti per l'editor, la scelta della musica e il revisore. La traccia è
tua, quindi nulla viene generato o acquistato. La pagina dell'esecuzione mostra il prezzo.

**Cosa ottieni.** Un video montato con la traccia sotto le voci. Se nessun audio caricato è adatto,
il passaggio della musica non sceglie nulla e ne spiega il motivo in parole semplici, e ottieni comunque il montaggio.

**Problemi comuni.**

- Nessuna musica nel risultato: il progetto non ha nessun file audio. Carica una traccia e avvia di nuovo.
- Musica troppo alta o troppo bassa: scrivilo nelle note e avvia di nuovo.
- I problemi di clip e di montaggio vuoto sono gli stessi di
  [Montaggio dei momenti migliori dal girato](derush-edit.md#recipe-highlight-edit-from-footage).
