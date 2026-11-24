---
title: Pagine e pannelli
sidebar_position: 6
description: A cosa servono le sette pagine dell'editor, come passare dall'una all'altra, come l'indirizzo ricorda la pagina e come ripristinare le dimensioni dei pannelli.
i18n_source_sha: f68e0da998e67b4f45f69dad6f7174d7f7503fe4
---

# Pagine e pannelli {#pages-and-panels}

L'editor è un'unica finestra con sette pagine. Ogni pagina è un luogo dedicato a una parte del lavoro,
con i propri strumenti e le proprie dimensioni dei pannelli. Passare da una pagina all'altra non cambia
il tuo montaggio: la timeline e il visualizzatore restano com'erano.

## Le sette pagine {#the-seven-pages}

Le pagine seguono l'ordine in cui di solito lavori: **Media**, **Taglio**, **Montaggio**,
**Composizione**, **Colore**, **Audio** e **Consegna**. I loro nomi sono sempre scritti: su un computer
stanno in una riga sotto la barra superiore, su un tablet in una colonna a sinistra.

### Media {#media}

**Media** è dove vivono i tuoi girati. I raccoglitori e la ricerca sono a sinistra, il pool delle clip
è al centro, e il visualizzatore, i dettagli del probe, le etichette e le note della clip selezionata
sono a destra. Da qui metti una clip sulla timeline con **Inserisci**, **Sovrascrivi**, **Aggiungi in
fondo**, **Posiziona sopra** o **Metti sopra**. La timeline resta nascosta finché non scegli **Mostra timeline**, che
apre una striscia corta per avere il contesto.

### Taglio {#cut}

**Taglio** serve per assemblare in fretta. Il **nastro delle origini** a sinistra elenca i media del
progetto, il visualizzatore è al centro e l'ispettore a destra. Sotto, una striscia mostra l'intera
timeline a colpo d'occhio sopra la timeline di dettaglio. **Inserisci** mette un'origine al taglio più
vicino e sposta ciò che segue; **Revisione** riproduce una clip a una velocità proporzionata alla sua
durata; **Inquadrature noiose** elenca le inquadrature lunghe e ferme trovate dall'analisi.

### Montaggio {#edit}

**Montaggio** è la pagina completa di modifica, quella su cui arrivi per prima. Il pannello sinistro
contiene **Media**, **Marcatori**, **Tagli**, **Analizza**, **Trascrizione**, **Sottotitoli**, **Take**,
**Struttura**, **Commenti** e **Cronologia**; il pannello destro contiene **Ispettore**,
**Assistente**, **Impostazioni**, **Versioni** e **Confronta**. Il visualizzatore è al centro e la
timeline attraversa il fondo. Usala per rifilare, spostare, aggiungere titoli, transizioni,
animazioni, versioni e commenti. La barra degli strumenti della timeline, in fondo, contiene gli
strumenti di tutti i giorni, tra cui **Rimuovi i silenzi** e [Taglia sulla musica](cut-to-the-music.md).

### Composizione {#compose}

**Composizione** serve per ciò che succede all'immagine di una clip. Aprila con una clip selezionata. A sinistra
c'è l'elenco di ciò che c'è sulla clip (clip video: Maschera, Reinquadratura, Keyer, Corner pin, Effetti,
Movimento, Vista a nodi; clip di regolazione: Correzione, Effetti, Vista a nodi; inserto: Matte, Angoli) e a destra le proprietà della voce scelta. Il visualizzatore mostra le maniglie di una
maschera, di una riquadratura o di un corner pin sull'immagine mentre lo
strumento è attivo (le maniglie del pin compaiono solo con lo strumento pin attivo). Le barre degli strumenti stanno in una sola riga sopra l'immagine, non sull'immagine. I tasti freccia nell'elenco selezionano una voce e ne mostrano lo strumento mentre il focus resta sull'elenco; clicca o premi `Invio` per portare il focus sull'immagine. Gli strumenti maschera, reinquadratura e pin terminano quando cambi pagina. Maschera, keyer, corner pin e riquadratura compaiono sull'immagine come contorni. Il visualizzatore ne mostra l'effetto sull'immagine, e l'etichetta sopra il visualizzatore ti dice se si avvicina soltanto al video finito. Puoi
anche raggiungere **Aggiungi maschera** (rettangolo o ellisse) e **Aggiungi riquadratura** dal menu della
clip e dalla sezione **Maschera e riquadratura** dell'Ispettore, senza passare da qui.

### Colore {#colour}

**Colore** è la pagina della correzione. La pila di nodi e il bersaglio sono a sinistra, l'immagine con
gli scope sotto è al centro, e gli strumenti di correzione sono a destra come schede: **Primarie**,
**Curve**, **LUT**, **Secondarie**, **Gruppi** e **Galleria**. Tutto è visibile quando la pagina si
apre; non c'è nessun interruttore in più da attivare. Curve, LUT e secondarie si vedono nel visualizzatore come nel video finito; l'etichetta sopra il visualizzatore dice quando un colore è ancora in preparazione.

In **Primarie**, lift, gamma e gain sono ruote colore rotonde, con accanto la ruota dell'offset. **Correggi
esposizione** si trova nell'area Aspetto e schiarisce o scurisce la clip al posto tuo. Vedi [Correggere
colore ed esposizione](./fix-colour-and-exposure.md).

### Audio {#audio}

**Audio** è la pagina del mixer. Il mixer è a sinistra, l'immagine al centro e i bus e il registratore
della voce narrante a destra. Ogni traccia ha un fader del volume, il pan, muto e solo, e un indicatore
di livello; il master mostra il mix. Qui ci sono anche **Volume sonoro**, **Abbassamento** e i comandi
per esperti di bus, compressione e pulizia.

### Consegna {#deliver}

**Consegna** è dove crei il film finito. Le impostazioni del render occupano la colonna sinistra e il
risultato occupa il resto della pagina, senza timeline sotto. Scegli un formato e l'intervallo, dai un
nome al file se vuoi e premi **Render**. La coda mostra l'avanzamento di ogni render, il download
quando è finito e il motivo se è fallito.

## Badge sulla timeline {#badges-on-the-timeline}

Una clip a cui hai fatto delle regolazioni mostra sulla timeline dei piccoli badge, in una riga lungo il lato destro della clip, sotto il suo nome. Ogni badge indica un tipo di modifica:

| Badge | Cosa significa |
| --- | --- |
| **Movimento** | La clip è spostata, scalata, ruotata o animata. |
| **Segue un soggetto** | La clip segue un soggetto che hai scelto. |
| **Ritaglio** | La clip è ritagliata. |
| **Velocità** | La clip è rallentata, accelerata, invertita o ferma su un fotogramma. |
| **Colore** | La clip ha una correzione, una LUT, un gruppo colore o un profilo camera. |
| **Maschera** | La clip ha una maschera a forma o una maschera di un oggetto. |
| **Effetti sull'oggetto** | La clip ha un effetto su un oggetto, per esempio una sfocatura. |
| **Green screen** | La clip ha una chiave colore. |
| **Effetti** | La clip ha effetti come sfocatura o grana. |
| **Stabilizza** | La clip è stabilizzata. |
| **Riquadro automatico** | La clip è reinquadrata attorno a un soggetto. |
| **Angoli fissati** | La clip è fissata a quattro angoli. |

Le dissolvenze non hanno un badge. Una dissolvenza si vede invece come una sottile linea nell'angolo della clip.

Un badge vuoto significa che la modifica c'è ma è disattivata. Un puntino su un badge significa che per quella modifica il visualizzatore si avvicina soltanto al video finito: ambra quando è vicino, un secondo colore per una modifica che il visualizzatore non può mostrare. Passa il puntatore su un badge per vedere il suo nome e, quando serve, il motivo. Se la clip è troppo stretta per tutti i badge, vedi un solo badge con un numero; apri il menu della clip per vedere l'elenco.

- **Aprilo.** Clicca o tocca un badge per selezionare la clip e andare nel punto dove vive quella modifica.
- **Disattivalo o rimuovilo.** Clicca con il tasto destro un badge (tieni premuto su uno schermo touch) per aprire il suo menu: **Apri**, **Disattiva** (o **Attiva**) e **Rimuovi**. **Rimuovi** è un solo passaggio, quindi **Annulla** lo riporta indietro.
- **Dalla tastiera.** Apri il menu della clip e scegli **Regolazioni**. Elenca gli stessi badge con le stesse azioni.

## Una clip in elaborazione {#a-clip-that-is-being-processed}

Mentre un lavoro in background lavora su una clip, la clip lo mostra. Una striscia corre lungo il fondo della clip: si riempie man mano che il lavoro avanza, oppure scorre quando il lavoro non sa dire a che punto è. La clip è un po' attenuata e la sua etichetta dice **In elaborazione**. Se hai chiesto all'editor di ridurre il movimento, la striscia è un motivo fermo.

- **Pronto da controllare** compare sulla clip quando il lavoro è finito e serve il tuo OK. Cliccalo per aprire il risultato.
- **Non completato** compare con un segno rosso quando il lavoro è fallito. Passa il puntatore per leggere il perché.

La clip è protetta mentre il lavoro è in corso. Se provi a cambiare qualcosa da cui dipende il lavoro, come i girati della clip, la sua velocità o, per alcuni lavori, il ritaglio o la posizione, l'editor chiede prima conferma. Puoi proseguire, fermare il lavoro o tornare indietro. Vedi [Vedi cosa è in corso in background](./background-activity.md).

## Passare da una pagina all'altra {#moving-between-pages}

Clicca o tocca il nome di una pagina. Con una tastiera puoi anche premere `Shift+1` per Media, `Shift+2`
per Taglio, `Shift+3` per Montaggio, `Shift+4` per Composizione, `Shift+5` per Colore, `Shift+6` per
Audio e `Shift+7` per Consegna. `Ctrl/Cmd+K` apre la tavolozza dei comandi, dove puoi scrivere il nome
di una pagina ed eseguirla. I pulsanti indietro e avanti del browser si muovono tra le pagine che hai
visitato, come per qualsiasi altro link.

## L'indirizzo ricorda la pagina {#the-address-remembers-the-page}

La pagina su cui ti trovi fa parte dell'indirizzo, per esempio `?page=colour`. Ricarica la pagina e
resti dove eri, e un indirizzo che mandi a qualcuno apre la pagina che indica. Un indirizzo senza
pagina apre l'ultima pagina che hai usato su questo dispositivo; se non hai mai aperto l'editor prima,
apre **Montaggio**.

## Dimensioni dei pannelli e ripristino {#panel-sizes-and-resets}

Trascina la linea sottile tra due pannelli per allargarne uno o restringerlo, oppure porta il focus
sulla linea e usa i tasti freccia. Ogni pagina ricorda le proprie dimensioni su questo dispositivo, e
si adattano quando la finestra cambia dimensione. **Ripristina questa pagina** riporta la pagina su cui
sei alle dimensioni predefinite. **Ripristina tutte le pagine** le riporta tutte indietro e chiede
prima conferma. Un **Layout** salvato conserva un insieme di dimensioni dei pannelli e schede aperte
che puoi applicare di nuovo.

## Su smartphone {#on-a-phone}

Lo smartphone mantiene il proprio layout: una barra superiore, l'immagine, la striscia della timeline e
una barra in fondo con **Media**, **Modifica**, **Audio**, **Aspetto** e **Sottotitoli**. Non ha una
riga delle pagine. **Taglio**, **Composizione** e **Colore** richiedono uno schermo più grande, e lo
smartphone lo dice dove starebbero quegli strumenti. **Correggi esposizione** c'è comunque, nel foglio **Aspetto**. Per il resto è lo stesso editor, quindi una
modifica fatta su smartphone viene salvata con la timeline.
