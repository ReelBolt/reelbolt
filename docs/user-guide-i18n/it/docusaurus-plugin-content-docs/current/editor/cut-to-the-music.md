---
title: Taglia sulla musica
sidebar_position: 9
description: Come trovare il ritmo di una clip musicale, controllarlo e correggerlo, e spostare, dividere o adattare le clip al ritmo nell'editor.
i18n_source_sha: af04f1dbf5267fd2d779d75bb1fbda80b5b3f76b
---

# Taglia sulla musica {#cut-to-the-music}

**Taglia sulla musica** fa cadere i tagli sul ritmo. Scegli la musica, lascia che l'editor trovi il ritmo, poi scegli cosa fare: spostare i tagli sul ritmo, dividere le clip sul ritmo, aggiungere marker sul ritmo o disporre le clip sul ritmo.

Si trova nella barra degli strumenti della timeline, l'icona della musica accanto a **Rimuovi silenzi**. Su smartphone, apri **Sottotitoli** nella barra in fondo e tocca **Taglia sulla musica**.

## Scegliere la musica {#pick-the-music}

Scegli una clip con audio in **Musica**. L'editor ne sceglie una per te: una clip che hai selezionato, oppure la prima clip con audio della timeline.

Una clip creata da un passaggio non ha un file proprio, quindi l'editor non può trovarne il ritmo. Usa una clip dei tuoi file.

## Trovare il ritmo {#find-the-beat}

Premi **Trova il ritmo**. È gratis e legge solo il file musicale. L'editor mostra **In attesa di iniziare…**, poi **Sto cercando il ritmo…**.

Quando è pronto, una riga mostra il tempo ("120 BPM, medio") e quante linee di taglio ha la musica. Possono comparire due avvisi:

- **Il ritmo non è chiaro.** Il ritmo si sente a fatica in alcuni tratti. Puoi andare avanti, ma controlla le linee di taglio prima di applicare.
- **Il ritmo cambia durante il brano.** Il tempo si sposta durante il brano. Controlla che i tagli cadano dove vuoi.

Se il ritmo non viene trovato, l'editor lo dice e offre **Riprova**.

## Controllare e correggere il ritmo {#check-and-fix-the-beat}

- **Tempo**: **Metà**, **Come trovato** o **Doppio**. Se il brano sembra andare a metà o al doppio della velocità indicata, l'editor offre **Usa metà tempo** o **Usa il doppio tempo**.
- **Sposta (passi di 10 ms)**: **Prima** e **Dopo** spostano tutti i battiti un po', fino a mezzo secondo per lato. Usalo quando i tagli cadono appena prima o appena dopo il battito.
- **La battuta inizia sul battito**: dice all'editor quale battito è il primo di una battuta. Scegli **Battito 1**, **Battito 2**, **Battito 3** o **Battito 4**. Cambialo quando i tagli sono sui battiti giusti ma ogni battuta non inizia dove ti aspetti.
- **Taglia su**: **Ogni battito**, **Ogni due battiti**, **Ogni battuta**, **Ogni due battute** o **Ogni quattro battute**. È la distanza tra le linee di taglio.

La riga sotto i comandi dice cosa cambierà prima che venga applicato qualcosa.

## Scegliere cosa fare {#choose-what-to-do}

### Allinea i tagli {#snap-cuts}

**Allinea i tagli** sposta sul ritmo i tagli che gli sono vicini. Scegli **Tracce da allineare** e **Allinea entro (fotogrammi)**; la riga sotto dice a quanti secondi corrisponde. Un taglio senza spazio per muoversi, o uno fuori dalla musica, resta com'è e viene contato tra quelli saltati.

### Dividi ogni {#split-every}

**Dividi ogni** taglia le clip che scegli su ogni linea di taglio. Scegli **Clip da dividere** e **Parte più corta (fotogrammi)**; non viene creata nessuna parte più corta di così.

### Marker sul ritmo {#beat-markers}

**Marker sul ritmo** aggiunge un marker su ogni linea di taglio della musica. **Numero massimo di marker** fissa il limite, 500 per impostazione predefinita. I marker delle battute sono arancioni e quelli dei battiti grigi, così li distingui sulla timeline.

### Adatta le clip alla musica {#fit-clips-to-the-music}

**Adatta le clip alla musica** dispone le clip di una traccia una dopo l'altra sul ritmo, come un montaggio. Scegli:

- **Traccia da adattare**.
- **Battute per clip**: **Automatico** adatta ogni clip all'ultima linea di taglio dentro la sua durata, oppure alla successiva se nessuna ci sta, oppure scegli 1, 2 o 4 battute.
- **Inizio**: **Dall'inizio della musica** o **Dal primo battito della battuta**.
- **Metti la clip più vivace sul cambio di ritmo**: quando l'editor ha misurato il movimento delle tue clip e ha trovato un cambio di ritmo nella musica, mette la clip più vivace sul cambio e riempie il tratto prima. Se la clip più vivace è la prima, prima del cambio vanno le altre clip (quante ne stanno finendo entro il cambio), l'ultima di esse viene allungata fino a finire esattamente sul cambio se il suo file è abbastanza lungo, poi la clip più vivace parte sul cambio e le altre seguono. Se le clip prima del cambio non lo raggiungono, resta uno spazio vuoto e viene contato tra le clip saltate. Altrimenti le clip restano nell'ordine attuale.
- **Ferma alla fine della musica**: le clip che inizierebbero dopo la fine della musica vengono tolte dalla timeline. Lascia spento per tenerle.

Una clip viene allungata solo quanto consente il suo file. Una clip che non riesce a raggiungere il punto scelto viene allungata quanto consente il suo file e viene contata tra quelle saltate. Una clip il cui audio o video collegato è su una traccia bloccata resta com'è, insieme al suo collegato, e le altre clip vengono disposte intorno. Le clip che non possono essere disposte dentro la musica vengono tolte dalla timeline.

## Ascoltare, applicare, annullare {#hear-it-apply-it-undo-it}

La riga sotto i comandi dice cosa cambierà. Premi **Riproduci il risultato** per vedere il taglio nel visualizzatore; non viene ancora salvato nulla, e **Applica** o **Annulla** compare sopra l'immagine. Premi **Applica** per tenere la modifica. Tutta la modifica è un solo passaggio, quindi un solo **Annulla** riporta tutto indietro.

## Vedere i battiti sulla timeline {#see-the-beats-on-the-timeline}

Quando il ritmo è pronto, nella barra degli strumenti della timeline compare **Mostra i battiti**. Disegna una linea su ogni battuta e un piccolo segno su ogni battito. Finché le linee sono visibili, un taglio che trascini si aggancia a loro, come si aggancia agli altri tagli; l'aggancio deve essere attivo. Premi **Nascondi i battiti** per toglierle. Le linee sono solo una guida e non vengono mai salvate nella tua timeline.
