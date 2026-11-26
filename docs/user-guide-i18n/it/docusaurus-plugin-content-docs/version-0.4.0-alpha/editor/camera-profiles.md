---
title: Filmati piatti dalla tua fotocamera
sidebar_position: 9
description: Come l'editor trasforma i filmati log, piatti e grigi, in colori normali, come viene applicato in automatico, come disattivarlo e come usare la LUT di visualizzazione.
i18n_source_sha: 38b6dd2ad9eacd73ca47d59a92b6414fdb152148
---

# Filmati piatti dalla tua fotocamera {#flat-footage-from-your-camera}

Molte fotocamere possono registrare in "log". Il filmato sembra piatto e grigio di proposito, perché conserva più dettaglio nelle parti chiare e scure dell'immagine. Alcuni esempi: Apple Log su un iPhone 15 Pro, S-Log3 su una Sony, V-Log su una Panasonic e le modalità log di Canon, DJI, ARRI, Fujifilm, Nikon e RED.

L'editor può convertire questi filmati in colori normali fin dall'inizio, così tagli, correzione colore ed esportazione partono da un'immagine con contrasto e colore normali.

## Come viene applicato {#how-it-is-applied}

- **In automatico, quando è chiaro.** Quando l'editor è sicuro di una clip, la converte appena la aggiungi. La clip mostra il badge "Convertito da {profile} automaticamente". Fai clic su **Disattiva** per annullare.
- **Come suggerimento, quando è un'ipotesi.** Per la maggior parte delle fotocamere l'editor si limita a suggerire. Vedi "Sembra {profilo}. Convertire in colori standard?" Fai clic su **Converti** per accettare o su **Non questo** per ignorare.
- **A mano.** Seleziona una clip e scegli un **Profilo camera** nelle sue impostazioni. Scegli **Nessuno** per disattivarlo. **Applica a tutte le clip di questo file** imposta lo stesso profilo su ogni clip tagliata dallo stesso file.

I filmati già HDR (HLG o PQ) non vengono mai convertiti come filmati log. Se una clip ha entrambi, vale la gestione HDR e il profilo camera viene ignorato. I filmati standard normali restano com'è.

La conversione fa parte del tuo film: è nell'anteprima e nell'esportazione.

È nell'immagine dell'esportazione del film finito; le esportazioni che contengono solo il montaggio (EDL, SRT, VTT, FCPXML) non la includono, mentre un'esportazione OTIO la conserva.

## La LUT di visualizzazione {#the-viewing-lut}

Una LUT di visualizzazione cambia solo ciò che vedi nel visualizzatore. Serve quando vuoi valutare un filmato piatto con un look, senza cambiare il film.

- Apri il menu della LUT di visualizzazione nel visualizzatore e scegli **No**, uno dei file LUT del progetto oppure **Profilo camera come vista**.
- Finché è attiva, l'immagine mostra il badge "LUT di visualizzazione attiva. Solo anteprima, non nell'esportazione."
- Non fa mai parte di un'esportazione, di un fotogramma, di un link di revisione o di una consegna. Viene ricordata solo su questo dispositivo, per questa timeline.
- Non è disponibile su smartphone.
- Gli oscilloscopi e i fotogrammi di riferimento leggono sempre l'immagine pulita, senza la LUT di visualizzazione.

## File LUT utilizzabili {#lut-files-you-can-use}

Un file LUT è un file `.cube` del tuo progetto. Gli stessi file funzionano come LUT di visualizzazione e nella scheda **LUT** degli strumenti colore. ReelBolt accetta tre tipi:

- Un file `.cube` **3D**, con da 2 a 65 punti per lato.
- Un file `.cube` **1D**, con da 2 a 65.536 voci. Se ha un intervallo di ingresso, ReelBolt lo rispetta.
- Una **tabella 1D e 3D in un solo file**, come la scrive DaVinci Resolve: prima una tabella 1D, poi una 3D, ciascuna con il proprio intervallo di ingresso.

Un file può pesare fino a 16 MiB. ReelBolt controlla il file quando lo scegli e di nuovo quando parte un'anteprima o un'esportazione. Se il file non è valido o usa una funzione che ReelBolt non supporta, viene rifiutato e il messaggio ne indica il motivo.

C'è un'unica approssimazione. Se un file contiene sia una tabella 1D sia una 3D e imposti l'intensità sotto il 100%, ReelBolt le unisce in una sola tabella 3D da 65 punti, quindi il risultato può differire molto leggermente dal file completo. Al 100% le due tabelle vengono usate così come sono.

## Limiti {#limits}

- **Non ancora verificato su ogni fotocamera.** I profili sono costruiti dalle curve pubblicate dai produttori, ma non sono ancora stati verificati su filmati reali di ogni fotocamera. I colori possono essere leggermente diversi. La nota compare accanto al profilo.
- **Il profilo DJI D-Log M è approssimativo.** DJI non pubblica la sua curva, quindi il profilo usa D-Log al suo posto.
- **L'anteprima è molto vicina, non identica.** L'anteprima può differire dall'esportazione per minime differenze di colore.
- **Le miniature della timeline restano piatte.** La striscia di miniature sulla timeline viene creata dall'immagine originale, quindi può sembrare grigia anche quando il visualizzatore è corretto.
- **Il riconoscimento è prudente.** Se la tua fotocamera non viene riconosciuta, scegli il profilo a mano.

Se una clip risulta ancora troppo scura o troppo chiara dopo la conversione, vedi [Correggere colore ed esposizione](./fix-colour-and-exposure.md).
