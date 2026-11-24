---
title: Aggiungi sottotitoli a un video
sidebar_position: 9
description: Ricetta per imprimere i sottotitoli in un video finito a partire da ciò che si dice, con traduzione facoltativa, e per scaricare i file di sottotitoli SRT e VTT.
i18n_source_sha: 5d33a08124e3692e5b23186bc590f021a97bd5a5
---

# Aggiungi sottotitoli a un video {#add-subtitles-to-a-video}

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

Aggiungi il template **Sottotitoli per il tuo video** (`video-subtitles`) da **Nuovo flusso di lavoro**. Ha due passaggi e non chiama nessun modello
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
  caratteri, divise ai confini delle parole e mantenute abbastanza a lungo da poter essere lette. Il risultato del passaggio di compilazione indica dove si trovano i due file
  (`subtitleStorageKeys`), e l'API li serve su `/api/v1/projects/{projectId}/step-results/{stepResultId}/subtitles/srt`
  (oppure `vtt`). La pagina dell'esecuzione non ha ancora un pulsante per scaricarli; finché non c'è, chiedi al tuo amministratore o usa l'API.

## Se qualcosa non torna {#if-something-looks-wrong}

- **L'esecuzione si ferma con "found no speech to caption".** La trascrizione è vuota: controlla che sia selezionato il video giusto,
  che contenga del parlato e che il provider di trascrizione funzioni.
- **Manca una frase.** Le frasi che la trascrizione non ha riconosciuto non possono essere sottotitolate; correggi l'impostazione
  della lingua parlata e avvia di nuovo.
- **I sottotitoli si sovrappongono al testo dell'immagine.** Si spostano automaticamente nella parte alta dell'inquadratura dove la parte bassa è occupata.
- **L'aspetto.** Apri il secondo passaggio per cambiare stile, carattere, dimensione, colori e posizione, come per qualsiasi sottotitolo.
