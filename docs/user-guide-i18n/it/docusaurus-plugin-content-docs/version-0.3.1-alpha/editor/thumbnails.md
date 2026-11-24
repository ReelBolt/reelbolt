---
title: Crea una miniatura
sidebar_position: 2
description: Come aprire un'immagine nell'editor di miniature, muoverti nel canvas, ritagliare i soggetti, aggiungere testo, usare gli strumenti AI, far disegnare una grafica a un designer AI, lasciare che l'assistente faccia le modifiche ed esportare per YouTube, Photoshop, GIMP o Krita.
i18n_source_sha: 81eb26b2bd1c37f044d41ff17809db0606f29e40
---

# Crea una miniatura {#make-a-thumbnail}

L'editor di miniature è un editor di immagini a livelli che funziona nel tuo browser. Apri una delle immagini del tuo progetto, la costruisci con i livelli (l'originale, persone od oggetti ritagliati, testo, altre immagini, risultati AI) ed esporti l'immagine finita come PNG, JPEG o altro formato, oppure come file Photoshop che conserva ogni livello. L'immagine originale non viene mai modificata: ogni modifica è un livello sopra di essa.

L'editor di immagini fa parte dell'**anteprima anticipata** dell'editor, quindi la sua pagina mostra l'etichetta **Anteprima anticipata**. Clicca o tocca l'etichetta per leggere cosa significa: l'editor è ancora in fase di completamento; il tuo lavoro viene salvato, ma alcuni strumenti potrebbero cambiare.

## Un editor di immagini e un editor video non sono la stessa cosa {#a-picture-editor-and-a-video-editor-are-not-the-same-thing}

ReelBolt ha due editor, e lavorano su materiali diversi.

- L'**editor di immagini** (questa pagina, a volte chiamato editor di miniature) lavora su **una sola immagine**: una
  pila di livelli con un canvas, l'immagine originale, persone od oggetti ritagliati, testo, risultati AI. Ci
  arrivi aprendo un'immagine, e il suo indirizzo contiene l'id di quell'immagine.
- L'**editor video** lavora su una **timeline**: clip, suoni e titoli assemblati in un film, di solito
  a partire da ciò che ha prodotto un'esecuzione. Ci arrivi da un render o dall'editor di un progetto, mai da un'immagine. L'
  editor video è disponibile come anteprima anticipata.

La **pagina Editor** non è nessuno dei due: è l'elenco di ciò che puoi aprire, con le schede **Video**
e **Immagini**. Le sue righe aprono l'uno o l'altro editor, e non si sta
modificando nulla finché guardi soltanto l'elenco.

L'assistente può modificare un'immagine per te (livelli, testo, canvas, ritocco AI) ma non modifica una
timeline: chiedigli invece di spiegare una modifica o di cambiare il flusso di lavoro che l'ha prodotta.

## Apri un'immagine nell'editor {#open-a-picture-in-the-editor}

Puoi raggiungere l'editor di miniature da due posti.

- **Dalla pagina Editor.** Apri **Editor** e scegli la scheda **Immagini**. Ogni immagine che hai iniziato nel progetto è elencata lì con il numero di livelli; cliccane una per continuare. Per iniziarne una nuova, scegli una forma sotto **Nuova immagine** (Quadrata, Post 4:5, Storia 9:16, Orizzontale 16:9 o Personalizzata), dagli un nome e premi **Nuova immagine** per un canvas vuota. Per partire da una foto, scegli una delle tue immagini e premi **Apri nell'editor di immagini**, oppure premi **Carica una foto** per aggiungerne una dal telefono o dal computer. Oltre le sei immagini compare una casella di ricerca.
- **Dai file del tuo progetto.** Apri il progetto, vai alla scheda **File**, apri il menu accanto a un'immagine e scegli **Modifica come immagine**.

Ogni immagine mostra la sua ultima esportazione come miniatura (o la foto da cui è partita). Accanto a ogni immagine c'è un menu **⋮** con **Rinomina**, **Inizia una nuova immagine da questa foto** ed **Elimina immagine**. L'eliminazione chiede conferma e rimuove l'immagine e i suoi livelli; la foto da cui è nata e le esportazioni che hai salvato restano nei file del progetto. Il nome dell'immagine è il titolo della sua pagina nell'editor.

La prima volta che apri un'immagine, ReelBolt ne crea una nuova a livelli. Dopo di allora, riaprire la stessa immagine ti riporta a quella invece di crearne una seconda. Si possono aprire così solo le immagini: un video, o un file Photoshop o OpenRaster che hai esportato in precedenza, non offre questa possibilità.

Una nuova immagine si apre con le dimensioni e la forma della tua foto, quindi la foto c'è tutta, non ritagliata. Una foto con il lato lungo oltre 4096 pixel viene ridotta a 4096 mantenendo la forma.

Se in seguito elimini la foto da **File** nel tuo progetto, la finestra di eliminazione ti dice quante immagini la usano. Ognuna conserva una propria copia, quindi si aprono e si esportano ancora.

Conservare quelle copie occupa spazio. Se nel tuo piano non ne resta abbastanza, la foto non viene eliminata e File ti spiega perché. Elimina alcune immagini o libera spazio, poi riprova.

## Orientarsi nell'editor di miniature {#find-your-way-around-the-thumbnail-editor}

Il canvas è al centro. A sinistra ci sono cinque strumenti: **Seleziona e sposta**, **Muoviti nel canvas**, **Testo**, **Ritaglia un soggetto** e **Maschera a pennello**. A destra ci sono quattro schede: **Livelli**, **Testo**, **AI** e **Canvas**. Sopra il canvas, **Annulla** e **Ripristina** ti portano indietro e avanti tra le modifiche, un'etichetta ti dice se le modifiche sono salvate, ed **Esporta** salva o scarica l'immagine finita.

Su smartphone gli strumenti stanno in una riga sopra il canvas e le schede si spostano sotto.

Le modifiche vengono salvate per te un istante dopo che smetti di modificare. Non premi nessun pulsante di salvataggio. Se un salvataggio non riesce, compare un messaggio con un pulsante **Riprova**, e l'editor ti avvisa prima che tu lasci la pagina con modifiche non salvate. Anche andare su un'altra pagina o chiudere la scheda subito dopo una modifica la salva comunque.

Se qualcun altro cambia la stessa immagine nello stesso momento, l'editor te lo dice, ricarica l'ultima versione e scarta le modifiche che non è stato possibile applicare. Non sovrascrive mai in silenzio il lavoro altrui.

## Su smartphone {#on-a-phone}

Su smartphone l'immagine resta in alto e i pannelli si aprono dal fondo dello schermo. Tocca Livelli, Testo, AI o Canvas sotto l'immagine per aprirne uno; trascina la barra in cima (o toccala) per renderlo più alto o più basso, e tocca la croce o usa il gesto indietro per chiuderlo. L'immagine resta visibile sopra il pannello, così vedi ogni modifica mentre la fai.

Ogni livello nell'elenco ha un occhio, un lucchetto e un nome, più un menu con Porta avanti, Manda indietro, Porta in primo piano, Manda in secondo piano ed Elimina livello. Sotto Posizione e dimensione, i quattro pulsanti freccia spostano il livello selezionato di 1 pixel, o di 10 quando scegli 10 px. Pizzicare con due dita ingrandisce l'immagine e non dipinge, cancella o aggiunge mai testo.

## Cambia la forma, aggiungi una tua immagine, esporta in piccolo {#change-the-shape-add-your-own-image-export-small}

- **Forma.** Nella scheda **Canvas**, scegli un'altra forma (per esempio 4:5 per un post di Instagram). L'editor ti chiede: **Adatta il contenuto** ridimensiona e centra tutto ciò che c'è nell'immagine nella nuova forma, e un solo **Annulla** rimette tutto com'era; **Mantieni le posizioni** cambia soltanto il riquadro. **Dimensione originale** torna alla dimensione della tua foto.
- **Carica un'immagine.** Nella scheda **Livelli**, premi **Carica un'immagine** per aggiungere un'immagine dal tuo dispositivo. Viene salvata nei file del progetto e inserita nell'immagine come livello. Se il caricamento non riesce, l'editor lo dice in una frase.
- **Esporta.** **JPEG**, **PNG** e **WebP** sono elencati per primi; **AVIF**, **Photoshop** e **OpenRaster** sono sotto **Altri formati** (in quei file a livelli, il testo diventa pixel appiattiti). Prima di scaricare, la finestra mostra più o meno quanto sarà grande il file e si aggiorna mentre muovi il cursore della qualità. Premi **Sotto i 2 MB** e sceglie la qualità più alta che ci sta.
- **Quanto costano gli strumenti AI.** Ogni strumento AI mostra **circa N crediti** prima di eseguirlo e **Usati N crediti** dopo. Se non hai abbastanza crediti, lo strumento è disattivato e compare un link **Aggiungi crediti**. Se non è configurato nessun servizio AI, ti viene detto di chiedere all'amministratore del tuo spazio di lavoro oppure, se sei tu l'amministratore, compare un link **Configura gli strumenti per le immagini**.

## Muoviti nel canvas e ingrandisci {#move-around-the-canvas-and-zoom}

Il canvas dell'editor di miniature non ha bordi. La tua immagine sta in un riquadro al centro, e tutto ciò che trascini oltre il riquadro resta visibile accanto, un po' più scuro. Solo ciò che è dentro il riquadro finisce in un'esportazione PNG, JPEG, WebP o AVIF.

- **Muoverti:** tieni premuto **Spazio** e trascina, trascina con il tasto centrale del mouse, scegli lo strumento **Muoviti nel canvas**, oppure trascina con due dita su uno schermo touch.
- **Zoom:** gira la rotellina del mouse, pizzica sul trackpad o sullo schermo touch, oppure usa i pulsanti **−** e **+** nell'angolo del canvas.
- **Vedere tutta l'immagine:** premi il pulsante di adattamento nell'angolo del canvas. La percentuale tra i pulsanti dello zoom mostra l'immagine alla dimensione reale.

Quando il canvas ha il focus della tastiera, **+** e **−** fanno lo zoom, **0** adatta tutta l'immagine e **1** la mostra alla dimensione reale.

## Sposta, ridimensiona e ruota i livelli {#move-resize-and-rotate-layers}

Scegli **Seleziona e sposta** e clicca un livello sul canvas. Compare un riquadro con le maniglie.

- Trascina il livello per spostarlo. Tieni premuto **Maiusc** per mantenere lo spostamento dritto in orizzontale o in verticale.
- Trascina una maniglia d'angolo per ridimensionarlo. Un livello mantiene sempre le sue proporzioni.
- Trascina la maniglia rotonda sopra il livello per ruotarlo. Tieni premuto **Maiusc** per ruotare a scatti di 15 gradi.
- Usa i tasti freccia per spostare il livello selezionato di un pixel, oppure tieni premuto **Maiusc** per dieci.

Un livello bloccato si può selezionare ma non spostare. Sbloccalo prima dal pannello dei livelli.

## Gestisci i livelli {#manage-layers}

Apri la scheda **Livelli**. Il livello più in primo piano è in cima.

- Trascina un livello in su o in giù per cambiarne l'ordine, oppure usa le piccole frecce accanto.
- L'occhio nasconde o mostra un livello. Un livello nascosto viene conservato e non compare in un'esportazione appiattita.
- Il lucchetto blocca un livello così non può essere spostato per sbaglio.
- Seleziona un livello per cambiarne il nome, l'opacità, il modo in cui si fonde con i livelli sotto (**Fusione**), il ruolo, e per rifletterlo in orizzontale o in verticale.
- Il cestino elimina un livello definitivamente. L'eliminazione non si può annullare, quindi l'editor chiede conferma e poi cancella la cronologia degli annullamenti.

Per aggiungere un'altra immagine del progetto come nuovo livello, per esempio un logo, sceglila sotto **Aggiungi un'immagine da questo progetto** e premi **Aggiungi all'immagine**. Un'immagine più grande del canvas viene adattata al suo interno e centrata, così la vedi tutta; trascina una maniglia d'angolo per ingrandirla. Su uno schermo touch le immagini del progetto sono un elenco: tocca un'immagine, poi premi **Aggiungi all'immagine**.

Su uno schermo touch i pulsanti di ogni riga di livello e i pulsanti dello zoom sono più grandi, così il dito colpisce quello giusto.

## Aggiungi e formatta il testo {#add-and-style-text}

Apri la scheda **Testo** e premi **Aggiungi testo**, oppure scegli lo strumento **Testo** e clicca sul canvas dove deve andare il testo. Seleziona un livello di testo per cambiare:

- le parole, comprese le interruzioni di riga e il **TUTTO MAIUSCOLO**;
- il carattere e lo spessore, da un breve elenco che comprende i caratteri di ReelBolt Chakra Petch e JetBrains Mono (un carattere viene caricato la prima volta che serve);
- la dimensione, il colore, l'allineamento, l'interlinea e la spaziatura tra le lettere;
- un contorno facoltativo e un'ombra facoltativa;
- una larghezza facoltativa a cui le righe vanno a capo.

Il testo viene disegnato dal tuo browser, quindi resta nitido a qualsiasi dimensione. L'editor ti dice a parole semplici se un valore è fuori intervallo, per esempio una dimensione del carattere oltre 600, e non invia un valore non valido per essere salvato.

## Scegli uno strumento AI {#choose-an-ai-tool}

Apri la scheda **AI** e premi **Scegli uno strumento AI**. Si apre un elenco con tutti gli strumenti e una frase su ciò che fanno; su smartphone riempie lo schermo. Scegline uno, e la scheda mostra solo i comandi di quello strumento. **Cambia strumento** riapre l'elenco.

- **Ritaglia un soggetto** trasforma una persona o una cosa in un livello a sé.
- **Rimuovi sfondo** ritaglia il soggetto principale con i bordi morbidi.
- **Nuovo sfondo** mantiene il soggetto e sostituisce tutto ciò che c'è dietro.
- **Cancella** e **Riempi** lavorano su un'area che dipingi con il pennello.
- **Modifica con le parole** fa una modifica che descrivi, per esempio "rendi la maglietta rossa".
- **Espandi** allarga l'immagine oltre i suoi bordi fino a una nuova forma, come una copertina reel 9:16 o un post quadrato; vedi [Espandere un'immagine a una forma](#expand-a-picture-to-a-shape).
- **Aumenta la risoluzione** rende l'immagine più nitida e due volte più grande, il doppio in larghezza e il doppio in altezza. Funziona su immagini fino a 5040 × 2160 pixel; un'immagine più grande viene rifiutata con un messaggio e non viene addebitato nulla.
- **Cambia la luce** modifica l'illuminazione.
- **Crea un'immagine** genera una nuova immagine da una descrizione e la aggiunge dietro agli altri livelli.
- **Progetta una grafica** fa disegnare a un designer AI un titolo, un badge, un cartellino del prezzo, una freccia o una forma, con sfondo trasparente. Vedi [Progettare una grafica](#design-a-graphic).

Seleziona prima il livello immagine su cui lavorare; tutto tranne **Crea un'immagine** lavora sul livello selezionato. Ogni strumento AI è una chiamata a pagamento e usa crediti; vedi [Piani e crediti](../account/plans-and-credits.md). Dopo un'esecuzione, lo strumento mostra quale servizio AI ha risposto.

Se nel tuo spazio di lavoro non è configurato nessun servizio AI per uno strumento, lo strumento lo dice e resta disattivato, e l'elenco lo segna come non configurato. Ogni strumento può usare il proprio servizio AI, quindi uno può essere configurato mentre un altro no. **Ritaglia un soggetto** usa lo stesso servizio di **Rimuovi sfondo**, quindi ti avvisa subito se quest'ultimo non è configurato. Un amministratore può aggiungerli in **Amministrazione, Provider di inferenza**; vedi [Strumenti dell'editor di immagini](../admin/inference-providers.md#picture-editor-tools-one-capability-per-tool).

**I ritagli restano ritagliati.** Quando usi **Cancella**, **Riempi**, **Modifica con le parole**, **Cambia la luce** o **Aumenta la risoluzione** su un livello con sfondo trasparente (un ritaglio, o un PNG con parti trasparenti), il risultato mantiene le stesse aree trasparenti, anche se la maggior parte dei servizi AI risponde con un'immagine piena. **Riempi** conserva anche ciò che hai dipinto in un'area trasparente, così puoi aggiungere un cappello sopra una testa ritagliata. **Nuovo sfondo** ed **Espandi** riempiono lo sfondo di proposito, quindi non lo fanno. Se l'AI restituisce un'immagine di forma diversa, la trasparenza non si può allineare; l'editor te lo dice e il risultato viene aggiunto così com'è arrivato.

Se un servizio AI rifiuta o fallisce, il messaggio dice quale strumento non ha funzionato, che nulla è stato aggiunto alla tua immagine e cosa ha detto il servizio. Quando il servizio è fallito o è scaduto il tempo, l'editor ricarica anche l'immagine, nel caso un risultato sia arrivato in ritardo.

Quello che scrivi in uno strumento resta lì mentre guardi un'altra scheda.

## Espandere un'immagine a una forma {#expand-a-picture-to-a-shape}

**Espandi** ingrandisce un'immagine e lascia che l'AI continui la scena nel nuovo spazio. È il modo in cui una foto orizzontale riempie una copertina reel verticale 9:16, o un ritratto riempie una miniatura 16:9.

1. Seleziona il livello immagine e scegli **Espandi**.
2. Lascia **Adatta a una forma** e scegli la forma. La prima scelta è la forma del tuo progetto grafico quando l'immagine non ce l'ha ancora, quindi per una copertina 9:16 è già 9:16. Il pannello dice dove cresce l'immagine, per esempio "Aggiunge immagine sopra e sotto".
3. Se il servizio AI accetta una descrizione, puoi dire cosa deve comparire, per esempio "più della stessa strada". Alcuni servizi non ne accettano una; il pannello allora non mostra il campo descrizione e lo dice.
4. Premi **Espandi l'immagine**. Il risultato è un nuovo livello posizionato in modo che la parte originale stia esattamente dove era la tua immagine.

Per far crescere solo alcuni lati, scegli **Pixel per lato** e scrivi quanti pixel di immagine aggiungere su ogni lato. Ogni servizio aggiunge solo una certa quantità su un lato; il pannello mostra quel limite e mantiene i numeri al suo interno. Per una modifica più grande, usa **Adatta a una forma**.

**Foto grandi.** Una foto presa direttamente dal telefono può essere più grande di quanto alcuni servizi AI accettino. L'editor invia sempre l'immagine completa quando il servizio la accetta. Quando non la accetta, l'editor invia l'immagine alla dimensione con cui compare nel tuo progetto e te lo dice con una breve nota dopo l'esecuzione, per esempio "Questa immagine era più grande di quanto accetti il servizio AI, quindi è stata usata alla dimensione con cui compare nel tuo progetto (1247×831)." Il risultato finisce comunque nel punto giusto e alla dimensione giusta.

## Guarda uno strumento AI lavorare sul canvas {#watch-an-ai-tool-work-on-the-canvas}

Mentre uno strumento AI lavora, la parte dell'immagine che sta cambiando diventa sfocata e scintilla sul canvas, proprio dov'è: l'area che hai dipinto per **Cancella** e **Riempi**, il livello selezionato per la maggior parte degli strumenti e l'intero riquadro per **Crea un'immagine**. Quando arriva il risultato, la parte modificata esce dalla sfocatura e torna a fuoco in meno di un secondo.

Se il tuo dispositivo è impostato per ridurre il movimento, l'area viene mostrata sfocata senza scintillio e il risultato compare subito. Una modifica AI può richiedere un minuto; se richiede più di circa un minuto e mezzo, si interrompe con un messaggio e puoi riprovare. Ogni risultato viene aggiunto come **nuovo livello** sopra quello da cui sei partito, così l'originale resta intatto e puoi nascondere o eliminare il risultato.

## Ritaglia un soggetto {#cut-out-a-subject}

Seleziona il livello immagine da cui ritagliare, poi scegli lo strumento **Ritaglia un soggetto** a sinistra, oppure scegli **Ritaglia un soggetto** sotto **Scegli uno strumento AI**. Funziona qualsiasi livello con dei pixel: l'originale, un'immagine che hai aggiunto, un risultato AI come uno creato con **Crea un'immagine**, o un ritaglio precedente. Senza nessun livello selezionato si usa l'immagine originale. Il pannello dice da quale livello stai ritagliando; i segni che hai fatto vengono cancellati quando selezioni un livello diverso, perché appartengono a quell'immagine.

Puoi indicare il soggetto che intendi in uno qualsiasi di questi modi, anche combinandoli:

- **Clicca** su di esso.
- **Escludi qualcosa**: scegli **Escludi questo** sopra l'elenco dei segni, poi cliccalo o toccalo (con la tastiera, Maiusc+clic fa lo stesso). Scegli **Ritaglia questo** per tornare a segnare il soggetto.
- **Trascina un riquadro** attorno ad esso.
- Oppure **scrivi il suo nome**, per esempio "la persona a sinistra".

Attiva **Dammi fino a 4 opzioni** se vuoi più ritagli candidati, ciascuno aggiunto come livello a sé. Premi **Ritaglia**. Ogni ritaglio diventa un nuovo livello con sfondo trasparente, posto direttamente sopra il livello da cui è stato ritagliato, nella stessa posizione e con le stesse dimensioni, così puoi spostarlo separatamente.

Ritagliare un soggetto invia l'immagine a un provider AI e usa crediti. Ogni pressione è una chiamata a pagamento.

## Progettare una grafica {#design-a-graphic}

**Progetta una grafica** serve per le parti di una miniatura che sono progettate e non fotografate: un titolo di grande impatto, un badge "NEW", un cartellino del prezzo, una freccia che indica qualcosa, un grande numero stilizzato, un blocco di forme dietro un testo. Un designer AI la disegna per te e la aggiunge come nuovo livello con sfondo trasparente, così sta sopra la tua immagine e puoi spostarla e ridimensionarla come qualsiasi altro livello.

1. Apri la scheda **AI**, premi **Scegli uno strumento AI** e scegli **Progetta una grafica**.
2. Descrivi cosa vuoi, con le parole esatte che deve mostrare, per esempio: *un badge adesivo giallo con la scritta "NEW" in lettere nere spesse, leggermente inclinato*. Indica i colori del tuo brand o uno stile (minimale, deciso) se ne hai uno.
3. Scegli dove va (**Centro**, **In alto**, **In basso a destra** e così via). Lascia la dimensione su **Adatta all'immagine** per far posizionare la grafica dal designer sull'intera immagine, oppure scegli **Personalizzata** per darle un riquadro di dimensioni precise.
4. Controlla la riga del costo, poi premi **Progettala**.

La progettazione richiede qualche minuto, perché il designer disegna la grafica, la controlla e solo dopo te la consegna. Nel frattempo puoi continuare a modificare: il punto in cui arriverà la grafica scintilla sul canvas, il pannello mostra cosa sta facendo il designer e il nuovo livello compare lì quando è pronto. Le parole compaiono esattamente come le hai scritte.

**Caratteri del brand.** Una grafica progettata usa un carattere tra quelli installati in ReelBolt. Se il tuo brand usa un carattere non installato, il designer lo sostituisce con quello installato più simile. I caratteri del tuo brand sono elencati nel suo profilo; vedi [Profilo del brand e del prodotto](../design-system.md).

**Modificare una grafica in seguito.** Seleziona una grafica creata dal designer e scegli **Modifica questa grafica** (nel pannello, o accanto al livello nella scheda **Livelli**). Di' cosa deve cambiare, per esempio *rendila più decisa* o *usa il blu del nostro brand*. Il designer parte dal proprio progetto invece che da zero, e la nuova versione va dove avevi messo la vecchia; la vecchia è nascosta, non eliminata, quindi puoi riportarla indietro.

Ogni progettazione è a pagamento e usa crediti, come una breve esecuzione di un flusso di lavoro; il pannello mostra la stima prima che tu cominci, e se una progettazione fallisce i tuoi crediti vengono restituiti. Se la progettazione di grafiche non è disponibile nel tuo spazio di lavoro, il pannello lo dice prima che tu scriva qualsiasi cosa: o l'installazione non può ancora progettare, oppure per il designer non è configurato nessun provider AI di chat (un amministratore ne aggiunge uno in Provider AI).

## Chiedi all'assistente di modificare la tua immagine {#ask-the-assistant-to-edit-your-picture}

L'assistente può lavorare sull'immagine che hai aperto. Aprilo con il pulsante assistente nell'intestazione e chiedi con parole tue, per esempio:

- "Ingrandisci il titolo e spostalo in alto a sinistra."
- "Nascondi il logo e cambia lo sfondo in blu scuro."
- "Rimuovi l'auto a destra." (l'assistente segna l'area da cancellare)
- "Progetta un badge SALE rosso per l'angolo in alto a destra."

L'assistente prima guarda i livelli della tua immagine, poi mostra una scheda con una modifica concreta: quali livelli, dove, con quali parole. **Non cambia nulla finché non premi Consenti** (a meno che tu non abbia disattivato **Chiedi prima di modificare**; vedi [l'assistente](../assistant/index.md)). Per uno strumento AI a pagamento o una grafica progettata la scheda mostra il costo stimato e il suo pulsante dice **Consenti e usa i crediti**. Quando consenti, la modifica viene fatta e l'editor la mostra subito: il livello modificato è selezionato e torna a fuoco, e un messaggio dice cosa ha fatto l'assistente. Un livello aggiunto dall'assistente può essere rimosso con **Annulla**. L'assistente può modificare solo le immagini dei progetti che puoi modificare, e scrive le parole che compaiono nella tua immagine esattamente come le hai date.

## Dipingi una maschera per cancellare o riempire {#paint-a-mask-to-erase-or-fill}

Seleziona un livello immagine, poi scegli lo strumento **Maschera a pennello** a sinistra, oppure scegli **Cancella** o **Riempi** sotto **Scegli uno strumento AI**. Dipingi sulla parte che vuoi cambiare; l'area dipinta appare in rosso. Scegli **Sfuma via** per togliere area dalla maschera e usa il cursore per la dimensione del pennello. **Azzera** rimuove l'intera maschera.

**Cancella** rimuove l'area dipinta e riempie il vuoto in modo coerente. **Riempi** mette invece ciò che descrivi. La maschera viene usata solo per il successivo Cancella o Riempi, e viene azzerata quando ha successo.

## Nascondi parti di un livello con una maschera {#hide-parts-of-a-layer-with-a-mask}

Una maschera nasconde parti di un livello senza toccarne i pixel. Puoi aggiungerne una a qualsiasi livello immagine (l'originale, un ritaglio, un'immagine AI o un'immagine che hai caricato). I livelli di testo non hanno pixel, quindi non hanno maschera.

1. Nell'elenco **Livelli**, apri il pulsante maschera sul livello (su smartphone, il menu con tre puntini) e scegli **Aggiungi maschera**.
2. Si apre il pannello della maschera. Scegli **Nascondi** per dipingere via delle parti o **Mostra** per riportarle. Tieni premuto Alt per un tratto per passare dall'una all'altra. Imposta la dimensione del pennello e la morbidezza del bordo.
3. Dipingi sull'immagine. **Annulla tratto** e **Ripristina tratto** scorrono i tuoi tratti (Ctrl+Z fa lo stesso mentre dipingi).
4. Premi **Fatto** per salvare la maschera. Se il salvataggio non riesce, la pittura resta aperta e il pannello ne spiega il motivo; premi **Riprova**. **Annulla** scarta la pittura, dopo averti chiesto conferma.

Un livello con una maschera mostra una sua piccola immagine nella riga. Premila per continuare a dipingere. Il menu del livello ha anche **Inverti maschera** (scambia nascosto e visibile), **Elimina maschera** (il livello torna intero) e **Mostra solo la maschera** (guarda la maschera in bianco e nero). Le esportazioni, le copie dell'immagine e i file a livelli usano tutti la maschera; i pixel originali non vengono mai modificati.

## Imposta la dimensione e lo sfondo del canvas {#set-the-canvas-size-and-background}

Apri la scheda **Canvas**.

- **Forma** imposta le proporzioni: 16:9 per YouTube, 9:16 per Shorts e Reels, 1:1 quadrato, 4:5 per Instagram, 4:3, 3:2, 2:3 e 21:9.
- **Dimensione** imposta quanti pixel ha l'immagine, indicati dal lato corto: **720p**, **1080p**, **1440p** o **4K**. In 16:9, 1080p è 1920 × 1080 e 4K è 3840 × 2160.
- **Dimensione originale** imposta il canvas alla dimensione della tua foto (lato lungo al massimo 4096 pixel) e rimette l'intera foto nel riquadro. È un solo passaggio, quindi **Annulla** lo riporta indietro.
- **Dimensione esatta** accetta qualsiasi larghezza e altezza da 16 a 8192 pixel. Premi **Usa** per applicarla; il pulsante di scambio trasforma una dimensione orizzontale in una verticale.

I tuoi livelli mantengono le loro posizioni quando la dimensione cambia, quindi un canvas più grande lascia spazio vuoto invece di stirare l'immagine, e uno più piccolo lascia parte di un livello fuori dal riquadro. Un'immagine molto grande potrebbe non aprirsi su alcuni smartphone; la scheda ti avvisa quando superi quella dimensione.

Disattiva **Trasparente** per scegliere un colore dietro tutti i livelli. Lascialo attivo per esportare un PNG con sfondo trasparente.

## Esporta la tua miniatura {#export-your-thumbnail}

Premi **Esporta** sopra il canvas e scegli un tipo di file:

- **PNG**: la qualità migliore, mantiene le aree trasparenti. Ottimo per YouTube e per modificare in seguito.
- **JPEG**: un file piccolo che ogni sito accetta. Le aree trasparenti diventano il colore di sfondo, oppure bianco.
- **WebP**: più piccolo del PNG a parità di aspetto, e mantiene le aree trasparenti.
- **AVIF**: i file più piccoli. Viene offerto solo quando il tuo browser è in grado di crearlo.
- **Photoshop (PSD)**: si apre in Photoshop e Photopea con ogni livello conservato, con il suo nome, in ordine, con opacità, fusione e visibilità.
- **OpenRaster (ORA)**: si apre in GIMP e Krita con ogni livello conservato.

Per JPEG, WebP e AVIF puoi anche impostare la **Qualità**. Dai un nome al file, poi premi **Salva nel progetto** per conservarlo nei file del progetto, nella cartella **thumbnails**, oppure **Scarica** per salvarlo sul tuo computer. Il messaggio nella finestra ti dice il nome e la dimensione con cui è stato salvato. Se un livello si sta ancora caricando, i pulsanti aspettano e ti dicono quale. Se l'immagine di un livello non c'è più (il suo file è stato eliminato), il canvas mostra un riquadro barrato dove si trovava, la sua riga in **Livelli** dice **Immagine mancante**, e l'esportazione lo lascia fuori invece di aspettare; la finestra Esporta indica il nome del livello che lascia fuori. Se l'immagine di un livello non si è potuta caricare per un altro motivo (connessione caduta, errore del server), l'editor riprova alcune volte; se ancora non riesce a caricarla, Esporta rifiuta e indica il livello, così il file non viene mai creato senza di esso. Controlla la connessione, poi premi **Riprova**.

PNG, JPEG, WebP e AVIF contengono solo ciò che è dentro il riquadro. I file a livelli conservano anche le parti di un livello che escono dal riquadro, fino a metà della larghezza o dell'altezza del riquadro su ogni lato, così potrai riportarle dentro più tardi. Il testo diventa un livello immagine in un file a livelli, con il nome del testo; lì non è un testo modificabile. Un file salvato nel progetto può arrivare a 200 MB; usa **Scarica** per qualsiasi cosa più grande.

Esportare non è una chiamata AI e non usa crediti.


## Posizionare, allineare, ritagliare e unire i livelli {#placing-lining-up-cropping-and-merging-layers}

Nella scheda **Livelli**, sotto l'elenco:

- **Posizione e dimensione**: scrivi Sinistra, Alto, Larghezza e Altezza in pixel e premi Invio. Larghezza e altezza mantengono le proporzioni dell'immagine.
- **Allinea**: attiva **Scegli più livelli**, spunta i livelli, poi scegli un pulsante di allineamento (sinistra, centro, destra, alto, metà, basso) **rispetto all'immagine** oppure **tra di loro**. Con tre o più livelli, **Distribuisci** rende uguali gli spazi.
- **Ritaglia**: apre l'immagine con un riquadro. Trascina gli angoli e i bordi, oppure scrivi i numeri, poi premi **Ritaglia**. La parte fuori dal riquadro è nascosta, non buttata via: riapri Ritaglia per allargarla, oppure premi **Rimuovi ritaglio**. Gli strumenti di ritaglio soggetto, pennello e AI lavorano sull'intera immagine, quindi rimuovi il ritaglio prima di usarli.
- **Unisci in un solo livello**: spunta due o più livelli e premilo.
- Il tasto **Canc** rimuove il livello selezionato; **Annulla** lo riporta indietro. Annullare qualcosa che hai appena aggiunto lo fa sparire anche dall'elenco dei livelli.

Ritaglia modifica lo stesso livello. Unisci crea un nuovo livello e mette da parte gli originali. **Annulla** annulla l'uno o l'altro, in un solo passaggio.

## Dopo l'esecuzione di uno strumento AI {#after-an-ai-tool-runs}

Una barra ti dice cosa è cambiato e quanti crediti è costato. Usa **Confronta con il prima**, **Annulla** o **Riprova**. Quando un ritaglio trova più opzioni, viene mostrata la prima: usa le frecce per guardare le altre, poi **Tieni questa** o **Tieni tutte come livelli**. Se uno strumento impiega molto tempo (fino a 90 secondi) e la connessione cade, l'editor controlla di nuovo e mostra il risultato se è finito; ti viene addebitato una sola volta.

Nell'elenco **Immagini**, il menu di ogni immagine ha **Duplica**.
