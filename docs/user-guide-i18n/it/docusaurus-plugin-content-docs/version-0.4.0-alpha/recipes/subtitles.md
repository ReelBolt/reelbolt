---
title: Aggiungi sottotitoli a un video
sidebar_position: 9
description: Ricetta per imprimere i sottotitoli in un video finito a partire da ciò che si dice, con traduzione facoltativa, e per scaricare i file di sottotitoli SRT e VTT.
i18n_source_sha: db2dec3228523beada65e7fb101405cf64457bf9
---

# Aggiungi sottotitoli a un video {#add-subtitles-to-a-video}

:::note
Questa app ora si apre come parte di [Lingue](languages.md): scegli lì il campo corrispondente. Questa pagina resta come riferimento su ciò che fa ogni passaggio.
:::

**Obiettivo:** prendere un video finito con del parlato e ottenere un nuovo MP4 con i sottotitoli impressi, più gli stessi sottotitoli
come file `.srt` e `.vtt` che puoi caricare su una piattaforma video. Nient'altro del video cambia: niente tagli, niente musica,
niente nuova voce.

## Prerequisiti {#prerequisites}

- **Il video caricato nel progetto** (scheda File).
- **Un provider di riconoscimento vocale.** Un amministratore ne aggiunge uno in Amministrazione, Provider di inferenza, con la capacità `Transcription`
  (funziona il servizio locale `whisper` incluso in ReelBolt). Senza un provider l'esecuzione si ferma al primo passaggio e lo dice,
  invece di restituirti il video senza sottotitoli.
- **Per un'altra lingua, un provider di chat AI** per la traduzione.

## Sottotitoli nella lingua parlata {#subtitles-in-the-language-that-is-spoken}

Aggiungi l'app **Sottotitoli per il tuo video** (`video-subtitles`) da **Nuovo flusso di lavoro**. Ha due passaggi e non chiama nessun modello
AI:

| # | Etichetta | Cosa fa |
|---|---|---|
| 1 | Ascolta il video | Trascrive il parlato. Seleziona il tuo video come origine. |
| 2 | Imprimi i sottotitoli | Disegna ogni frase sullo schermo mentre viene pronunciata e scrive i file dei sottotitoli. |

Indica nel brief quale lingua si parla, se il rilevamento sbaglia (il controllo **Lingua parlata**).

## Sottotitoli in un'altra lingua {#subtitles-in-another-language}

Aggiungi invece **Sottotitoli in un'altra lingua** (`video-subtitles-translated`) e scrivi nel brief la lingua che vuoi
(il controllo **Lingua dei sottotitoli**). Aggiunge un passaggio che traduce ciò che viene detto. I tempi sono sempre quelli dell'originale:
la traduzione sostituisce solo le parole, una frase per una frase, quindi un sottotitolo non compare mai prima o dopo
la frase che traduce. Una frase che la traduzione ha saltato resta nella lingua originale invece di
sparire.

## Cosa ottieni {#what-you-get}

- Il video con i sottotitoli impressi (il risultato dell'esecuzione).
- `subtitles.srt` e `subtitles.vtt`, con le stesse frasi e gli stessi tempi dei sottotitoli impressi. Le righe sono larghe al massimo 42
  caratteri, divise ai confini delle parole e mantenute abbastanza a lungo da poter essere lette. L'esecuzione conserva entrambi i file con il risultato
  del suo ultimo passaggio. La pagina dell'esecuzione non ha ancora un pulsante per scaricarli; la sezione qui sotto spiega come avere oggi i file dei sottotitoli.

## Scaricare oggi i file SRT e VTT {#download-the-srt-and-vtt-files-today}

Finché la pagina dell'esecuzione non ha un suo pulsante, ci sono due modi. Entrambi funzionano dal tuo account, senza nulla da configurare.

**Dall'editor.** L'editor crea i file dei sottotitoli dallo stesso parlato, e puoi correggere una parola prima di scaricarli.

1. Apri **Editor** nel menu a sinistra, premi **Nuova timeline** e metti sulla timeline il tuo video originale (quello senza
   sottotitoli).
2. Apri la scheda **Analizza** e scegli **Trascrivi il parlato** sul video. Mostra prima quanto costerà e parte
   solo quando confermi.
3. Apri **Sottotitoli** e premi **Genera sottotitoli**. Correggi i sottotitoli che vuoi nell'elenco.
4. Apri **Consegna**. Sotto **Sottotitoli**, **Scarica SRT** e **Scarica WebVTT** ti danno subito i file (contengono
   ciò che è salvato, quindi salva prima). Oppure scegli **Solo file separati**, o **Entrambi** per incorporarli anche nel video, e premi
   **Render**: la scheda del film finito elenca i due file, ciascuno con il suo pulsante per scaricarlo, e i file vengono aggiunti
   ai file del progetto.

L'editor ascolta di nuovo il video, quindi le sue righe possono andare a capo in punti un po' diversi dai sottotitoli impressi dall'esecuzione.
Non traduce. Per saperne di più su questi pannelli: [L'editor di timeline](../editor/index.md#subtitles).

**I file dell'esecuzione.** Usa questo modo per i sottotitoli tradotti, o quando vuoi esattamente le righe impresse nel video.
Mentre hai effettuato l'accesso, il tuo browser può scaricare i due file scritti dall'esecuzione:

1. Nella pagina dell'esecuzione, copia l'indirizzo del pulsante **Scarica** del video. Contiene
   `/api/v1/projects/<project>/outputs/<result>/download`.
2. In quell'indirizzo, cambia `outputs` in `step-results` e sostituisci tutto ciò che segue `<result>` con `/subtitles/srt`
   (oppure `/subtitles/vtt`). Ottieni
   `/api/v1/projects/<project>/step-results/<result>/subtitles/srt` sullo stesso sito.
3. Apri il nuovo indirizzo nello stesso browser. Il file viene scaricato. Se la risposta dice che non sono stati scritti sottotitoli,
   l'ultimo passaggio dell'esecuzione non ha creato sottotitoli dal parlato.

## Se qualcosa non torna {#if-something-looks-wrong}

- **L'esecuzione si ferma con "found no speech to caption".** La trascrizione è vuota: controlla che sia selezionato il video giusto,
  che contenga del parlato e che il provider di trascrizione funzioni.
- **Manca una frase.** Le frasi che la trascrizione non ha riconosciuto non possono essere sottotitolate; correggi l'impostazione
  della lingua parlata e avvia di nuovo.
- **I sottotitoli si sovrappongono al testo dell'immagine.** Si spostano automaticamente nella parte alta dell'inquadratura dove la parte bassa è occupata.
- **L'aspetto.** Apri il secondo passaggio per cambiare stile, carattere, dimensione, colori e posizione, come per qualsiasi sottotitolo.
