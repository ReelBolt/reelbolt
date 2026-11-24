---
title: Correggere colore ed esposizione
sidebar_position: 10
description: Come usare le ruote colore rotonde e il pulsante Correggi esposizione nell'editor per cambiare il colore di ombre, mezzitoni e luci e per schiarire o scurire una clip.
i18n_source_sha: 8371a1a0776b6e53ee1dcd43ccb17f2e9760467b
---

# Correggere colore ed esposizione {#fix-colour-and-exposure}

La pagina **Colore** ha due strumenti per cambiare l'aspetto di una clip: le ruote colore rotonde per il colore e il pulsante **Correggi esposizione** per una clip troppo scura o troppo chiara. Le ruote richiedono un tablet o uno schermo più grande. **Correggi esposizione** funziona anche su smartphone.

## Ruote colore {#colour-wheels}

Apri la pagina **Colore** e la scheda **Primarie**. Vedi quattro ruote:

- **Lift (ombre)** cambia le parti scure dell'immagine.
- **Gamma (mezzitoni)** cambia i toni di mezzo, dove si trova la maggior parte dell'immagine.
- **Gain (luci)** cambia le parti chiare.
- **Offset** sposta l'intera immagine.

Su ogni ruota, trascina il puntino lontano dal centro per aggiungere colore. La direzione è il colore e la distanza è quanto. Vicino al centro il puntino si muove a passi più piccoli, così le piccole modifiche sono facili. Il cursore sotto ogni ruota rende quella parte dell'immagine più chiara o più scura. Il visualizzatore e gli scope seguono il trascinamento in tempo reale.

- **Azzerare il colore:** fai doppio clic sulla ruota, oppure fai clic su **Azzera** per quella ruota.
- **Ritoccare:** fai clic su una ruota e usa i tasti freccia. Tieni premuto Maiusc per passi più grandi.
- **Agganciare un colore principale:** tieni premuto Maiusc mentre trascini per agganciare rosso, giallo, verde, ciano, blu o magenta.
- **Annullare:** un trascinamento è un solo passo di annullamento.

I numeri esatti sono ancora disponibili in **Valori esatti** quando vuoi digitarli.

## Correggi esposizione {#fix-exposure}

Seleziona una clip nella pagina **Colore** e fai clic su **Correggi esposizione**. Su smartphone apri il foglio **Aspetto** e toccalo lì.

L'editor guarda la clip con la correzione disattivata. Controlla il fotogramma sotto la testina e fino a quattro altri fotogrammi lungo la clip. Guarda solo l'immagine della clip, quindi le bande nere intorno non contano. Servono pochi secondi e puoi fare clic su **Annulla** durante la misurazione.

Poi aggiunge una regolazione chiamata **Auto fix** in cima all'elenco della clip. Cambia solo la luminosità, non il colore. Fai clic su **Correggi di nuovo l'esposizione** e la stessa regolazione viene sostituita, non aggiunta di nuovo.

Al termine ti dice cosa ha fatto, per esempio "Schiarito di 0.8 stop". Può anche dirti:

- **Questa clip va già bene.** Non è stato cambiato nulla.
- **Questa clip è di un solo colore uniforme; non c'è nulla da correggere.**
- **Impossibile leggere l'immagine.** Tieni la clip a schermo e riprova.
- **Questa clip ha già il numero massimo di regolazioni. Rimuovine una prima.** L'editor non rimuove mai una tua regolazione al posto tuo.

Se la clip è in un gruppo colore che ha una correzione, la modifica va nel gruppo e cambia tutte le clip al suo interno. Una nota sotto il pulsante indica **Corregge l'intero gruppo**.

Se modifichi la clip durante la misurazione, puoi scegliere **Applica comunque** o **Misura di nuovo**.

## Filmati piatti dalla fotocamera {#flat-footage-from-a-camera}

Se il tuo filmato è piatto e grigio perché è un filmato log, imposta prima il profilo della fotocamera, poi usa **Correggi esposizione**. La correzione misura la clip dopo la conversione. Vedi [Filmati piatti dalla tua fotocamera](./camera-profiles.md).

## Cosa mostra il visualizzatore {#what-the-viewer-shows}

Il visualizzatore disegna lift, gamma, gain e offset come fa l'esportazione. Curve, LUT e secondarie (gli intervalli di colore e luminosità che limitano una correzione a una parte dell'immagine) si vedono dal vivo mentre le cambi e corrispondono all'esportazione. Finché un file LUT si sta ancora caricando, il visualizzatore mostra la correzione senza di esso e l'etichetta sopra il visualizzatore lo dice. Lo mostra anche il badge **Colore** sulla clip nella timeline.
