---
title: Corsia degli eventi
sidebar_position: 10
description: Come vedere sulla timeline cosa succede nel tuo video, agganciarti a quei momenti e agire su uno con un solo clic.
i18n_source_sha: de2e36fed29f73599b97eceaad0ac012fd28269e
---

# Corsia degli eventi {#events-lane}

La **corsia degli eventi** mostra i momenti che contano nel tuo video: qualcosa compare, qualcosa inizia o smette di muoversi, un colpo, un lampo di luce, un testo che entra, la camera che inizia a muoversi, un battito, qualcuno che inizia a parlare. Ogni momento è un piccolo segno sulla timeline. Tocca o fai clic su un segno e il cursore va sul fotogramma esatto, e un solo menu ti permette di agire su di esso.

È un modo di guardare il tuo video. Niente di ciò che vedi qui viene salvato nel tuo film, e mostrare o nascondere la corsia non cambia nulla di ciò che esporti.

## Mostrare la corsia {#show-the-lane}

Apri **Eventi** nella barra degli strumenti della timeline e scegli **Mostra la corsia degli eventi**, oppure premi **E**. La corsia compare sotto il righello e la corsia dei marcatori, e la scelta viene ricordata per questa timeline su questo dispositivo.

La prima volta, un video non ha ancora eventi. Scegli **Trova gli eventi in questo video** nello stesso menu. Il menu ti dice il prezzo prima che parta qualsiasi cosa: trovare gli eventi è gratis. Richiede un attimo, e i segni compaiono a lavoro finito.

Quando il modo di trovare gli eventi è migliorato dall'ultima volta che un video è stato esaminato, o l'ultimo esame non è riuscito a completare ogni parte, la corsia mostra ancora i segni che ha e il menu propone di nuovo **Trova gli eventi in questo video**. Sceglilo per sostituirli con segni più recenti. Resta gratis. Mentre il video viene esaminato di nuovo i suoi segni lasciano la corsia, e tornano quando il nuovo risultato è pronto. Se l'esame non riesce, la corsia non mostra segni per quel video finché non scegli di nuovo **Trova gli eventi in questo video** e l'esame riesce. Finché un nuovo risultato non è pronto, agli aiutanti AI non vengono indicati i momenti di quel video.

## Leggere i segni {#read-the-marks}

- **Colore e forma** indicano il tipo di momento. La forma significa la stessa cosa in qualsiasi colore, quindi non devi distinguere i colori: un rombo per qualcosa che compare (vuoto quando scompare), un triangolo per il movimento o per la camera che parte, un quadrato per uno stop, per il testo o per il parlato (vuoto quando finisce), un punto per un battito o un lampo, una croce per una transizione e una barra per un colpo, un cambio o una parola dei sottotitoli.
- **L'altezza** indica quanto è forte il momento. Un segno alto è un momento netto.
- Ingrandisci e ogni segno mostra il suo nome. Passa il mouse su un segno, o raggiungilo con la tastiera, per vederne il tipo e il tempo. Quando l'editor è riuscito a capire in che cosa avviene il momento, lo aggiunge tra parentesi: una grafica, un testo o una ripresa filmata.

Sono mostrati solo i segni delle clip video della tua timeline, nel punto in cui quella clip li riproduce. Se tagli o sposti una clip, i suoi segni la seguono.

## Agganciarsi agli eventi {#snap-to-events}

Con la corsia visibile, il cursore e i bordi delle clip si agganciano ai segni come ai bordi delle altre clip. La calamita a sinistra della corsia mostra se è attivo, e **Aggancia agli eventi** nel menu **Eventi** (oppure **Maiusc+N**) lo disattiva. È attivo di default, e l'interruttore di aggancio della timeline ha comunque l'ultima parola.

## Agire su un evento {#act-on-an-event}

Fai clic con il tasto destro su un segno, toccalo su un tablet, oppure sceglilo con la tastiera (**Sinistra** e **Destra**, poi **Invio**). **Maiusc+E** apre il menu per l'evento più vicino al cursore. Il menu elenca cosa puoi fare proprio lì:

- **Aggiungi un suono qui** porta il cursore sull'evento e apre la pagina Media, così scegli un suono e lo metti esattamente sul momento.
- **Aggiungi un titolo qui** aggiunge un titolo che parte sull'evento.
- **Zoom qui** ingrandisce sull'evento per circa un secondo e poi torna indietro, centrato su dove è successo quando l'editor lo sa.
- **Panoramica qui** fa scorrere l'immagine per un secondo, verso l'evento quando l'editor sa dov'è.
- **Scossa** aggiunge una breve scossa della camera che si smorza. Lo stesso evento dà sempre la stessa scossa.
- **Lampo** aggiunge qualche fotogramma bianco sull'evento.
- **Rampa di velocità** rallenta a metà velocità il mezzo secondo dopo l'evento.
- **Taglia qui** divide la clip, e il suo audio collegato, sul fotogramma dell'evento.

Ogni scelta è un solo passo nella cronologia, quindi un solo **Annulla** la riporta indietro. Le scelte sono esatte perché usano il fotogramma dell'evento, non una stima. Un'azione che non ha senso per un segno, per esempio **Zoom qui** su una clip bloccata, non compare nel menu invece di apparire grigia.

## Gli aiutanti AI conoscono questi momenti {#helpers}

Quando un video ha i suoi eventi, agli aiutanti della scheda **Assistente** vengono indicati i suoi momenti più forti in poche parole semplici. **Effetti sonori** può mettere un suono esattamente su uno di essi, **Grafiche** può far partire un titolo su uno di essi e **Revisione** può metterci una nota. Vedi [Chiedere suggerimenti all'AI](index.md#asking-the-ai-for-suggestions-assist).

## Su smartphone {#on-a-phone}

Lo smartphone non ha la corsia né nuovi gesti. Apri **Altro**, poi **Eventi al cursore**. Il foglio nomina l'evento più vicino al cursore, **Evento precedente** ed **Evento successivo** passano da un evento all'altro, e ogni azione è un grande pulsante. Ha anche **Trova gli eventi in questo video** quando un video non ne ha ancora.

## Tastiera {#keyboard}

| Tasto | Cosa fa |
| --- | --- |
| **E** | Mostra o nasconde la corsia |
| **Maiusc+N** | Attiva o disattiva l'aggancio agli eventi |
| **Maiusc+E** | Agisci sull'evento più vicino al cursore |
| **Sinistra** / **Destra** | Passa da un evento all'altro quando la corsia ha il focus |
| **Invio** | Apre le azioni per l'evento |

Puoi cambiarli nelle scorciatoie da tastiera.
