---
title: Mostra un'immagine o un video sopra il film
sidebar_position: 14
description: Come mostrare uno screenshot, una recensione, un logo o una clip sopra il tuo video montato, a schermo intero o in un angolo, e come leggere cosa è stato mostrato.
i18n_source_sha: f2ecf102d2fb9d507d0be4f94040bdb775035959
---

# Mostra un'immagine o un video sopra il film {#show-a-picture-or-video-over-the-film}

Usalo quando il film finito deve mostrare un'immagine o un video che non fa parte delle riprese: lo screenshot di una
testimonianza, una recensione o un messaggio, una tabella dei prezzi, un logo, una clip del prodotto o una seconda
angolazione. Metti le immagini in uno slot; l'agente di montaggio sceglie quale mostrare, in quale momento
calmo, e se occupa tutto lo schermo o un angolo.

## Scegli un'immagine a schermo intero o in un angolo {#choose-a-full-screen-picture-or-a-corner}

| Cosa vuoi mostrare | Scegli | Perché |
|---|---|---|
| Uno screenshot, una recensione, un messaggio, una tabella dei prezzi o un documento che chi guarda deve leggere | **A schermo intero** | L'immagine riempie l'inquadratura abbastanza a lungo da essere letta. L'immagine del film resta coperta finché l'altra è visibile. |
| Un logo, una clip del prodotto, una seconda angolazione o un riferimento tipo "guarda qui" | **In un angolo** | Chi parla e il film restano visibili; l'inserto sta su un bordo, lontano dal centro. |

L'agente fa questa scelta per ogni immagine. Un'immagine a schermo intero viene adattata per intero, quindi uno
screenshot alto mantiene la sua forma con bande ai lati invece di essere ritagliato. Un'immagine in un angolo
mantiene la propria forma e sta in una di nove posizioni (angoli, bordi o centro) in una dimensione piccola,
media o grande.

## Metti le tue immagini nello slot {#put-your-pictures-in-the-slot}

Avvia l'app **Highlight edit from footage**. Il suo modulo iniziale ha **Le tue clip** per le
riprese e **Un'immagine o un video da mostrare** per le immagini e i video che ci vanno sopra.

- Aggiungi fino a quattro immagini o video. Ognuno deve essere un'immagine o un video già presente in questo
  progetto.
- **Lascia lo slot vuoto** e l'agente può scegliere qualsiasi immagine o video utilizzabile nel progetto.
- **Aggiungi file** e vengono offerti solo quei file, nell'ordine in cui li hai aggiunti. L'agente sceglie
  tra questi; non può uscire dall'insieme.
- In questo slot non scegli il secondo esatto. L'agente sceglie tra i momenti calmi che il montaggio ha
  tenuto. Per guidarlo, indica il momento in **Il tuo brief**: "mostra la recensione quando dice che l'app le ha fatto
  risparmiare un'ora".

Puoi anche chiedere all'assistente di fissare un'immagine con precisione: "mostra `testimonial.png` a schermo intero da
0:12 a 0:18". L'assistente ti mostra cosa sta per salvare prima di scrivere qualsiasi cosa, e non
avvia l'esecuzione.

## Scegli cosa succede al suono del film {#choose-what-happens-to-the-films-sound}

Sotto un'immagine a schermo intero, il suono del film normalmente continua. Scegli **Sotto un'immagine a schermo
intero** nel modulo iniziale:

- **Lascia scegliere all'agente** (predefinito): l'agente tiene o silenzia il suono per ogni immagine.
- **Tieni il suono**: chi parla continua a parlare sotto l'immagine.
- **Silenzia il suono**: il suono del film sfuma per l'immagine e torna dopo. Un suono che l'immagine stessa
  porta viene comunque riprodotto.

Un'immagine in un angolo non cambia mai il suono del film. Un video mostrato nel film può portare il proprio
suono, che l'agente mixa a un livello che sceglie (basso, bilanciato o in evidenza), mai amplificato
oltre il livello della clip stessa.

## Cosa mostra la scheda dell'esecuzione {#what-the-run-card-shows}

Quando l'esecuzione finisce, la scheda ha una sezione **Immagini sopra il film**. **Cosa è stato mostrato**
elenca una riga per ogni immagine che compare:

- il nome del file;
- **A schermo intero** oppure **Angolo (in basso a destra)**;
- l'intervallo di tempo, per esempio `0:12–0:18`;
- l'opacità quando è inferiore al 100%;
- un bordo chiaro o scuro, quando c'è;
- **suono mantenuto** o **suono silenziato** per un'immagine a schermo intero, oppure **suono a Basso / Bilanciato /
  In evidenza** per un video con un suono proprio.

Qui compaiono solo le immagini che sono state davvero mostrate. Vedi nomi e parole semplici, non id di file né
coordinate.

## Cosa significa "Non mostrato" {#what-not-shown-means}

Sotto l'elenco, **Non mostrato** indica le immagini che erano state offerte ma non sono comparse, con un
motivo in parole semplici:

- **il momento è stato tagliato**: la parte del film in cui sarebbe andata l'immagine non è nel
  montaggio;
- **quell'immagine non si è potuta leggere**: il file è danneggiato, non è un'immagine o un video, oppure non si può
  aprire;
- **non scelta per questo montaggio**: l'agente ha scelto un'altra immagine;
- **c'era posto solo per poche immagini**: l'immagine superava il numero che il film può contenere;
- **era impostata come invisibile**: la sua opacità è zero;
- **è stata scelta due volte**: la stessa immagine e lo stesso momento sono stati pianificati due volte e ne è stato usato uno solo;
- **quell'immagine è troppo grande**: il file supera il limite di dimensione o di durata per un'immagine sopra il film.

**Non mostrato** è un'informazione, non un errore: l'esecuzione è riuscita e ha prodotto un film. Se manca
un'immagine che ti aspettavi, nominala in **Il tuo brief** o chiedi all'assistente di fissarla.

## Cosa non viene fatto {#what-is-not-done}

- **Nessuna lettura del testo.** Uno screenshot viene mostrato come immagine. ReelBolt non legge, estrae, cerca
  né controlla come testo le parole che contiene.
- **L'immagine non segue uno schermo in movimento.** Se lo schermo di un telefono o di un portatile deve riempirsi con
  la tua immagine e muoversi con essa, usa invece [Metti un'animazione su un green screen](tracked-screen-insert.md).
- **Nessun posizionamento libero trascinando.** Scegli schermo intero o un angolo; l'agente mette
  l'angolo su un bordo e in una dimensione che sceglie. L'assistente e l'editor avanzato possono fissare forma,
  bordo, dimensione e momento, ma non una posizione arbitraria in pixel.
- **Nessuna transizione separata per un'immagine.** Intorno ad essa si applica la normale dissolvenza del film.
- **Nessun ducking per immagine.** Un video con un suono proprio sta a un livello fisso sotto il film.

## Problemi comuni {#pitfalls}

- **Non viene mostrato nulla.** Lo slot è vuoto e l'agente non ne ha scelta nessuna, oppure il progetto non ha
  immagini utilizzabili. Nomina l'immagine in **Il tuo brief**, oppure mettila nello slot e chiedilo esplicitamente.
- **L'immagine compare nel momento sbagliato.** L'agente può usare solo i momenti calmi che il montaggio ha
  tenuto. Indica il momento in **Il tuo brief**, oppure chiedi all'assistente di fissare un intervallo di tempo preciso.
- **Un'immagine a schermo intero copre chi parla troppo a lungo.** Chiedi un angolo, oppure scegli un momento più
  breve. Un'immagine a schermo intero serve per qualcosa che chi guarda deve leggere.
- **L'immagine sembra ritagliata o ha delle bande.** Un'immagine a schermo intero viene adattata per intero e aggiunge bande
  quando la sua forma è diversa da quella del film; un'immagine in un angolo mantiene la propria forma. Controlla il file
  originale se il risultato ti sorprende.
