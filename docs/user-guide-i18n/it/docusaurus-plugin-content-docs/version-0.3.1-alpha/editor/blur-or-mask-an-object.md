---
title: Sfoca o maschera un oggetto
sidebar_position: 8
description: Come sfocare, pixelare, evidenziare o contornare un oggetto indicato per nome in una clip, come una targa o un volto, dall'editor o chiedendolo all'assistente, quanto costa, quale permesso serve e cosa fare quando non riesce a trovare l'oggetto.
i18n_source_sha: 28d5bf7bd4d0ac69556943d84d91fabe7559f43c
---

# Sfoca o maschera un oggetto {#blur-or-mask-an-object}

Scrivi il nome di un oggetto in una clip, per esempio "targa" o "volto". ReelBolt lo trova nel video e applica l'effetto solo su di esso: lo sfoca, lo pixela, lo evidenzia, oppure al contrario applica l'effetto a tutto tranne che all'oggetto.

## Prima di iniziare {#before-you-start}

- **Il tuo piano.** La funzione non è disponibile nel piano di prova. È disponibile da Creator in su.
- **Una clip su una traccia immagine.** La clip deve provenire da un file del tuo progetto o da un risultato finito.
- **Un permesso una tantum per il progetto.** Per trovare l'oggetto, il video (solo questo video) viene inviato a un servizio AI esterno. Nient'altro viene condiviso. La prima volta che usi la funzione in un progetto, l'editor ti chiede di accettare. Puoi ritirare il permesso in qualsiasi momento.
- **Durata.** Il video può durare fino a 10 minuti.

## Aggiungere un effetto dall'editor {#add-an-effect-from-the-editor}

1. Seleziona una clip su una traccia immagine.
2. Apri la sezione **Oggetti** delle proprietà della clip. Su un telefono, apri il foglio **Altro** della clip e scegli **Sfoca o maschera un oggetto**.
3. In **Cosa deve cercare?**, scrivi l'oggetto. Puoi anche toccare una scelta rapida: targa, volto o logo.
4. Scegli un **Effetto**: Sfocatura, Pixelatura, Evidenzia, Riflettore, Colore in evidenza o Contorno.
5. Se vuoi, attiva **Applica a tutto tranne l'oggetto**, che lascia l'oggetto nitido e applica l'effetto al resto dell'immagine.
6. Imposta l'**Intensità** (e, se presente, la **Sfumatura**, che ammorbidisce il bordo dell'effetto).
7. Controlla la riga del prezzo, poi seleziona **Trova e applica**.
8. Se l'editor chiede **Inviare questo video a un servizio AI esterno?**, leggi il testo, spunta **Accetto di inviare questo video per trovare gli oggetti** e seleziona **Accetta e continua**.

Mentre la ricerca è in corso, la sezione mostra **Cerco ...**. Puoi selezionare **Smetti di seguire**: la ricerca continua. Quando finisce, l'effetto viene aggiunto alla clip in un solo passaggio, quindi un solo annulla lo rimuove. Ogni effetto compare poi nell'elenco **Effetti sugli oggetti di questa clip**, dove puoi disattivarlo, cambiarne l'intensità o rimuoverlo.

## Quanto costa {#what-it-costs}

La sezione mostra il prezzo prima che tu avvii qualsiasi cosa, come **Circa N crediti**. Se lo stesso oggetto è già stato trovato nello stesso video, mostra **Già trovato in questo video: gratis**, e rieseguirlo non costa nulla. Controlla la pagina del tuo piano per vedere cosa include; questa pagina non elenca prezzi.

Se non hai abbastanza crediti, la sezione dice **Non hai abbastanza crediti per trovare questo oggetto.** Se il tuo piano non include la funzione dice **Il tuo piano non include la ricerca di oggetti.**

## L'anteprima lo mostra {#the-preview-shows-it}

Il visualizzatore mostra l'effetto sull'oggetto, così puoi controllarlo prima di esportare. L'etichetta sopra il visualizzatore ti dice quanto l'anteprima è vicina al risultato.

- **Contorno.** Quando il contorno dell'oggetto è pronto, il visualizzatore segue il contorno esatto, come fa il video finito.
- **Riquadro.** Mentre il contorno è ancora in preparazione, il visualizzatore mostra l'effetto dentro un riquadro che segue l'oggetto, e l'etichetta dice **Anteprima del contorno ancora in preparazione, per ora mostra un riquadro.** Il riquadro diventa il contorno da solo quando è pronto.
- **Immagine attenuata.** Se l'oggetto non è ancora stato caricato, il visualizzatore mostra la clip senza l'effetto, attenuata, e l'etichetta dice perché.

Sulla timeline, una clip con un effetto su un oggetto mostra un badge **Effetti sull'oggetto**. Mentre la ricerca è in corso, la clip mostra una striscia con l'avanzamento. Vedi [Pagine e pannelli](./pages-and-panels.md#badges-on-the-timeline).

Fai il render del video (vedi [Esporta il tuo film](export-your-film.md)) per il risultato finale.

## Limiti {#limits}

- Fino a 4 effetti oggetto su una clip. Al limite la sezione dice **Una clip può avere fino a 4 effetti sugli oggetti. Rimuovine prima uno.**
- Video fino a 10 minuti. Uno più lungo viene rifiutato con **Questo video è troppo lungo per cercare un oggetto.**
- Se l'oggetto non si trova, nella tua clip non cambia nulla.

I crediti vengono restituiti per una ricerca che fallisce per un problema da parte nostra. Non vengono restituiti quando la annulli o quando la richiesta stessa era sbagliata. Una ricerca arrivata fino in fondo che non ha trovato nulla ha usato i suoi crediti.

## Chiedilo all'assistente {#ask-the-assistant}

Nell'assistente puoi scrivere "Sfoca la targa nella clip dell'intervista". L'assistente trova la timeline per nome e il video dal nome del file (per esempio "intervista.mp4"), usa tutte le clip di quel video sulla timeline e mostra una scheda con l'oggetto, l'effetto, le clip a cui si applica e i crediti, oppure che è gratis. Approva la scheda per avviare la ricerca.

- L'assistente non dà mai il permesso sul video al posto tuo. Se il progetto non ha ancora accettato, ti dice di aprire la clip nell'editor, scegliere **Oggetti** e accettare una volta.
- Non aspetta il risultato. Dopo alcuni minuti l'effetto compare sulla timeline nell'editor. Se l'oggetto non si trova, non cambia nulla e l'editor te lo dice.
- Se lo stesso oggetto è stato trovato prima, l'effetto viene aggiunto subito e gratis.

## Se qualcosa va storto {#if-something-goes-wrong}

| Cosa vedi | Cosa significa |
| --- | --- |
| We could not find that object in the video. Try describing it differently, or pick another clip. | La ricerca è finita senza risultati. Usa altre parole o un'altra clip. |
| Non abbiamo trovato (oggetto) in questo video. Prova con altre parole. | Lo stesso, mostrato dall'editor con il nome che hai scritto. |
| Non è stato possibile avviare la ricerca. Riprova tra un attimo. | La ricerca non è partita. Riprova. |
| Non è stato possibile leggere il prezzo. Riprova tra un attimo. | Il prezzo non si è caricato. Riprova. |
| Allow this project's video frames to be sent to our object-detection service. Open the clip in the editor, choose Objects and agree once. | Manca il permesso una tantum. Accetta nell'editor, poi riprova. |
| This project has not allowed its video frames to be analysed. Agree to it in the editor and try again. | Hai ritirato il permesso. Accetta di nuovo. Ritirarlo ferma anche le ricerche ancora in attesa. |
| Your plan does not include object detection. Upgrade to use it. | La funzione non è nel tuo piano. |
| There were not enough credits to analyse this video. | Aggiungi crediti e riprova. |
| This video is longer than object detection can handle. Use a shorter clip. | Il video supera i 10 minuti (contati a 30 fotogrammi al secondo: un video più veloce conta come più lungo). Se non riusciamo a leggerne la durata, ti mostriamo il prezzo massimo possibile e ti addebitiamo al massimo quello. |
| This video has too many frames for object detection. Use a shorter clip or a lower frame rate. | Il video ha troppi fotogrammi per la ricerca. |
| We could not open that video. It may have been removed. | Il file dietro la clip non c'è più. |
| The object-detection service had a problem. Nothing was lost; try again in a few minutes. | Un problema da parte nostra o del servizio esterno. Riprova più tardi. |
| The object-detection service returned something we could not use. Try again in a few minutes. | Come sopra. |
| Object detection is not available on this installation, so the video could not be analysed. | La funzione è disattivata dove usi ReelBolt. |
| Object detection is not set up for this workspace. | La funzione non è configurata per il tuo spazio di lavoro. |
| The analysis was cancelled. | La ricerca è stata fermata prima della fine. |
| La ricerca non è andata a buon fine. Riprova. | Qualsiasi altro errore. Riprova. |
| Non è stato possibile ritirare il permesso. Riprova tra un attimo. | La richiesta di ritiro è fallita. Riprova. |

Le frasi in inglese nella colonna di sinistra sono i messaggi esatti del server, che oggi compaiono in inglese.
