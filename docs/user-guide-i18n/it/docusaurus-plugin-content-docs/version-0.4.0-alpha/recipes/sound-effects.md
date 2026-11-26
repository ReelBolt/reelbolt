---
title: Aggiungi effetti sonori a un video finito
sidebar_position: 10
description: Ricetta per aggiungere brevi effetti sonori a un video finito nei punti in cui succede qualcosa sullo schermo, senza tagliare nulla, usando la libreria di suoni integrata.
i18n_source_sha: 218ed81d44794f9e75c1d25d739122f959e63771
---

# Aggiungi effetti sonori a un video finito {#add-sound-effects-to-a-finished-video}

**Obiettivo:** prendere un video già montato e ottenere un nuovo MP4 con brevi effetti sonori nei momenti in cui
succede qualcosa sullo schermo: un pop dove appare qualcosa, un whoosh su un movimento o un cambio di scena, uno scatto
su un flash fotografico. Non viene tagliato nulla e non viene aggiunta musica. La voce resta in primo piano; gli effetti
stanno sotto.

## Prerequisiti {#prerequisites}

- **Il video caricato nel progetto** (scheda File).
- **Un provider di chat AI**, per il passaggio che sceglie i suoni.
- **Un provider AI di visione è facoltativo e migliora il risultato.** Con un provider di visione, il passaggio 1 guarda
  anche l'immagine in ogni momento e annota che cosa mostra ("compare un cartellino del prezzo", "un uomo passa
  camminando"): così i suoni si adattano a ciò che succede e le normali riprese non ne ricevono. Senza, l'app
  funziona lo stesso.
- **I tuoi file audio sono facoltativi.** ReelBolt offre la sua libreria di suoni integrata (pop, clic, whoosh, impatti,
  uno scatto di fotocamera e altro, gratuiti da usare). Se il progetto contiene anche file audio tuoi, vengono offerti
  anche quelli.

Non serve un provider di riconoscimento vocale.

## La ricetta {#the-recipe}

Aggiungi l'app **Sound effects for your video** (`video-sound-effects`) da **Nuovo flusso di lavoro**. Ha tre
passaggi:

| # | Etichetta | Che cosa fa |
|---|---|---|
| 1 | Find the moments in the video | Elenca ciò che succede nell'immagine: cambi di scena, elementi che compaiono, si muovono o lampeggiano, testo che entra. Seleziona il tuo video come sorgente. Per trovare i momenti non si usa nessun modello AI. Se il tuo spazio di lavoro ha un modello di visione, il passaggio guarda poi alcuni momenti per dire che cosa mostrano. |
| 2 | Choose a sound for each moment | Il sound designer sceglie un suono adatto per i momenti che lo meritano. |
| 3 | Mix the sound effects in | Mette ogni suono sul suo momento e ti restituisce il nuovo video. |

Nel brief puoi dire che carattere devono avere i suoni, per esempio "morbidi e discreti" oppure "solo sui momenti
importanti".

In **Altre opzioni**, nella pagina dell'app:

| Opzione | Che cosa fa |
|---|---|
| Suoni anche sulle riprese | Disattivata per impostazione predefinita: i suoni vanno sui momenti grafici (grafiche, testo, transizioni), non sui semplici stacchi tra riprese né sulle persone che si muovono. Attivala per avere suoni anche lì. Un flash fotografico e un impatto ricevono sempre un suono. |
| Modello che guarda l'immagine | Quale modello di visione guarda i momenti. Lascia quello consigliato, a meno che tu non abbia un motivo. |
| Numero massimo di sguardi all'immagine | Il numero massimo di momenti che il modello guarda nel passaggio 1 (da 0 a 40). Lascia vuoto e decide la durata del video: circa uno sguardo ogni sei secondi, tra 12 e 30. Imposta 0 per disattivare gli sguardi: nessuna immagine viene inviata al modello di visione. Quando gli sguardi sono attivi, il sound designer nel passaggio 2 può chiedere fino a 6 sguardi in più su singoli momenti. Ogni sguardo è una piccola chiamata al modello di visione; la stima che vedi prima dell'esecuzione li comprende. |

## Che cosa ottieni {#what-you-get}

- Lo stesso video, con la stessa durata e la stessa immagine, con gli effetti sonori mixati.
- Il risultato del passaggio 3 elenca ogni suono e il momento su cui è stato messo, e dice perché un suono previsto è
  stato lasciato fuori, quando succede.

## Che cosa non fa {#what-it-will-not-do}

- **Non mette un suono su ogni parola dei sottotitoli animati.** Le parole dei sottotitoli che compaiono una dopo
  l'altra vengono riconosciute e lasciate stare.
- **Non taglia, non accorcia e non riordina il tuo video.**
- **Non crea suoni nuovi.** Sceglie dalla libreria integrata e dai tuoi file.

## Se qualcosa non va {#if-something-looks-wrong}

- **Troppi o troppo pochi suoni.** Dillo nel brief ("meno suoni", "un suono a ogni cambio di scena") ed esegui di nuovo.
- **Un suono è sul tipo di momento sbagliato.** I momenti vengono trovati solo dall'immagine, quindi un elemento che
  compare sopra riprese movimentate può essere letto come un semplice cambiamento. I suoni sono mixati nel nuovo video,
  quindi un singolo suono non si può spostare o rimuovere dopo. Esegui di nuovo l'app e scrivi nel brief che cosa
  cambiare ("nessun suono sul logo all'inizio", "niente sulle riprese filmate"), oppure attiva o disattiva
  **Suoni anche sulle riprese** in **Altre opzioni**. Una nuova esecuzione crea un nuovo video dal tuo
  originale; non si perde nulla.
- **Vuoi posizionare ogni suono a mano.** Apri il tuo video originale nell'editor, mostra la
  [corsia degli eventi](../editor/events-lane.md) e usa **Aggiungi un suono qui** su un momento. Così posizioni file
  audio tuoi; la libreria integrata è usata solo da questa app.
- **Nessun suono.** Il risultato del passaggio 2 dice perché: per esempio il video non ha momenti chiari (un semplice
  video parlato), oppure non è configurato un provider di chat AI.
