---
title: Correggere colore ed esposizione
sidebar_position: 10
description: Come usare le ruote colore rotonde e il pulsante Correggi esposizione nell'editor per cambiare il colore di ombre, mezzitoni e luci e per schiarire o scurire una clip.
i18n_source_sha: d2ac85b0efb851280e9b1d371048fe31e5d797f6
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

## Correzioni: nome, ordine e solo {#corrections-name-order-and-solo}

Il colore di una clip è fatto di **correzioni**, elencate a sinistra nella pagina **Colore**. Vengono applicate dall'alto verso il basso, quindi l'ordine cambia il risultato. Una clip può averne fino a otto.

- **Rinominare:** fai doppio clic su un nome (oppure selezionalo e fai clic su **Rinomina**), scrivi e premi Invio. Esc mantiene il vecchio nome.
- **Riordinare:** trascina una correzione in una nuova posizione, oppure selezionala e fai clic su **Sposta su** o **Sposta giù**. Con la tastiera, vai sulla maniglia all'inizio della riga, premi Spazio per prendere la correzione, usa i tasti freccia per spostarla e premi Spazio per rilasciarla. Esc annulla.
- **Attivare o escludere:** usa l'interruttore all'inizio della riga.
- **Duplica** crea una copia subito dopo la correzione selezionata. **Azzera** la riporta a nessuna modifica e ne mantiene il nome.
- **Solo (S):** mostra l'immagine con quella sola correzione, così vedi cosa fa da sola. Mentre è attivo, sopra gli scope e sopra gli strumenti di correzione colore compare una riga con **Solo:** e il nome della correzione. Per uscire fai clic su **Esci dal solo**, fai di nuovo clic su **S** oppure premi Esc. Il solo finisce anche quando selezioni un'altra clip o lasci la pagina **Colore**.

Il solo è soltanto un modo di guardare. Non viene mai salvato e non cambia mai l'esportazione.

Ognuna delle altre modifiche è un solo passo di annullamento.

## La catena di correzione {#the-grade-chain}

Sopra l'elenco, **Catena di correzione** disegna le stesse correzioni come una fila di caselle numerate, da **In** a sinistra a **Out** a destra. Se la clip è in un gruppo colore, le correzioni del gruppo vengono prima, perché sono applicate per prime.

- Fai clic su una casella per selezionare quella correzione. È la stessa selezione dell'elenco.
- Una casella con il bordo tratteggiato è una correzione esclusa.
- La correzione in solo porta una **S**.
- Piccoli simboli nella casella mostrano cosa contiene la correzione: primari, curve, una LUT, un qualificatore o una finestra.
- Con la tastiera, usa i tasti freccia per muoverti lungo la catena e Invio per passare agli strumenti di correzione colore.

Fai clic sulla freccia accanto a **Catena di correzione** per nasconderla. L'editor lo ricorda su questo dispositivo.

## Scope {#scopes}

Gli scope stanno sotto il visualizzatore nella pagina **Colore**. Usa i tre pulsanti di disposizione per mostrare **uno scope**, **due scope affiancati** o **tutti e quattro** (forma d'onda, parade RGB, vettorscopio e istogramma). Con uno scope scegli quale; con due scegli quello di sinistra e quello di destra.

Trascina il bordo superiore degli scope per renderli più alti o più bassi (su tablet usa i pulsanti più e meno), oppure fai clic sulla freccia accanto a **Scope** per ripiegarli. La disposizione e l'altezza vengono ricordate su questo dispositivo.

## Prima e dopo {#before-and-after}

Tre controlli stanno sopra gli strumenti di correzione colore, in ogni scheda:

- **Escludi correzione** mostra l'immagine senza alcuna modifica di colore. Fai di nuovo clic per riattivare la correzione.
- **Dividi prima / dopo** mostra da un lato l'immagine senza correzione e dall'altro quella corretta. Trascina **Wipe** per spostare la linea. **Chiudi divisione** la toglie.
- **Fermo immagine di riferimento** ti permette di confrontare con un frame che hai conservato. Fai clic su **Cattura fermo immagine** per conservare il frame corrente, poi sceglilo nell'elenco e trascina **Wipe**.

Nessuno di questi controlli cambia il tuo film o la tua esportazione.

## Cosa mostra il visualizzatore {#what-the-viewer-shows}

Il visualizzatore disegna lift, gamma, gain e offset come fa l'esportazione. Curve, LUT e secondarie (gli intervalli di colore e luminosità che limitano una correzione a una parte dell'immagine) si vedono dal vivo mentre le cambi e corrispondono all'esportazione. Finché un file LUT si sta ancora caricando, il visualizzatore mostra la correzione senza di esso e l'etichetta sopra il visualizzatore lo dice. Lo mostra anche il badge **Colore** sulla clip nella timeline.
