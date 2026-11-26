---
title: L'editor di timeline
sidebar_position: 1
description: Come aprire una timeline di ReelBolt nell'editor, riprodurla, tagliare e spostare le clip, aggiungere marcatori, annullare, e capire cosa l'anteprima può e non può mostrarti.
i18n_source_sha: 634337c6af80c2c099367afbdba31dc011575779
---

# L'editor di timeline {#the-timeline-editor}

:::note Anteprima anticipata

L'editor video e l'[editor di immagini](./thumbnails.md) sono entrambi disponibili come **anteprima anticipata**:
ogni pagina dell'editor mostra un'etichetta **Anteprima anticipata**. Clicca o tocca l'etichetta per leggere cosa significa: l'editor
è ancora in fase di completamento, il tuo lavoro viene salvato, ma alcuni strumenti potrebbero cambiare. Anteprima anticipata significa che puoi usarlo per lavoro vero, ma pulsanti e strumenti potrebbero spostarsi o cambiare, e non c'è ancora una data di rilascio. Se manca qualcosa, chiedilo all'[assistente](../basics/assistant-basics.md).

:::

L'editor ti permette di aprire un montaggio video come **timeline**, cioè tracce di clip disposte nel tempo, e
modificarla a mano: rifinire un taglio, spostare una clip, dividerne una, aggiungere un titolo o un marcatore, annullare una modifica o tornare
a una versione precedente. Lavora sul montaggio stesso, non su un file video finito, quindi ogni
modifica è leggera e reversibile finché non esporti.

L'**editor di immagini** fa parte della stessa anteprima anticipata, quindi la sua pagina mostra la stessa etichetta
**Anteprima anticipata**.

L'editor ha due layout sulla stessa timeline: **Semplice**, con l'essenziale per un primo video, e
**Completo**, con ogni pagina e strumento. [L'editor semplice e l'editor completo](simple-and-full.md)
li spiega entrambi e dice come passare dall'uno all'altro. Questa pagina descrive l'editor completo.

Ogni sezione qui sotto si legge da sola. Per capire com'è disposto l'editor, inizia da
[Pagine e pannelli](pages-and-panels.md); l'elenco completo dei tasti è in
[Scorciatoie da tastiera](shortcuts.md).

## Aprire una timeline {#opening-a-timeline}

Vai su **Editor** nel menu a sinistra. La pagina mostra un progetto alla volta (cambialo con il selettore
di progetto in alto a destra) e ha due schede: **Video** e **Immagini**.

**Video** elenca ogni timeline video del progetto, con la modifica più recente per prima, indicando quando è stata modificata l'ultima volta
e da cosa è partita.

- **Nuova timeline** ne crea una. Il nome parte come "Video senza titolo", già selezionato, così puoi scriverci sopra. Per una timeline
  vuota scegli un **Formato** (Orizzontale 16:9 1080p, 4K orizzontale, Verticale 9:16, Quadrato 1:1 o Verticale 4:5) e una **Frequenza fotogrammi**
  (24, 25, 30, 50 o 60 fotogrammi al secondo, oppure **Come la mia prima clip**, che usa la frequenza del primo video del tuo progetto
  e lo dice se non ne è ancora nota nessuna). **Avanzate** contiene la forma in pixel e il **Timecode iniziale**. **Parti da** può scegliere invece un video
  finito di un flusso di lavoro: i suoi tagli, titoli, musica e le motivazioni degli agenti diventano il punto di partenza, e mantiene le dimensioni
  e la frequenza fotogrammi di quel video.
- Sopra l'elenco, **Cerca** lo restringe per nome mentre scrivi, e **Ordina** lo ordina per Modificati di recente, Nome o Creati. Il browser
  ricorda l'ordine.
- Un campo timecode accetta le cifre da destra: scrivi 1215 e legge 00:00:12:15. Invio lo conferma, Esc annulla, e qualsiasi valore che non sia
  un tempo reale torna a quello di prima con un breve motivo sotto. Su smartphone si apre il tastierino numerico.
- Nelle **Impostazioni della timeline** dell'editor, **Formato** e **Frequenza fotogrammi** usano le stesse parole. Quando ci sono clip sulla timeline la frequenza
  è bloccata, con il motivo mostrato: "La frequenza fotogrammi non si può cambiare quando ci sono clip sulla timeline. Inizia una nuova timeline con la
  frequenza che vuoi."
- **Dalle tue esecuzioni** elenca i video finiti che non hai ancora aperto. **Modifica** ne trasforma uno
  in una timeline e la apre. Riaprire lo stesso video (da qui o da **Apri nell'editor**
  in un'esecuzione di un flusso di lavoro) ti riporta alla timeline che hai già creato da esso, mai a una seconda copia.
- Se qualcosa dell'esecuzione non può essere inserito, l'editor ti dice quanti elementi sono stati lasciati fuori.
- Se il video nasconde una parte dell'immagine (un volto o un oggetto che hai oscurato), una timeline creata da esso lascia quel tratto **vuoto** invece di mostrare il girato originale, e la nota lo conta come "tratto oscurato". L'audio di quel tratto resta. Se il video sfoca o pixela un oggetto tracciato, l'editor non può riportarlo: non crea affatto la timeline e te lo dice, perché mostrerebbe l'immagine non oscurata.
- **Importa** legge un file OpenTimelineIO (`.otio`) da ReelBolt o da un altro editor.
- Il menu **…** su una riga può **Rinominare** la timeline, **Duplicarla**, aprire la sua **pagina di revisione**, copiare il link di revisione o **Eliminarla**. Elimina chiede conferma e ti dice cosa succede: la timeline passa in **Eliminati di recente** per 30 giorni, la sua pagina di revisione e
  i commenti smettono di funzionare finché è lì, e tornano quando la recuperi. I video che hai già generato restano nel tuo progetto. Subito dopo
  l'eliminazione, per qualche secondo resta un messaggio con **Annulla**. **Eliminati di recente** (sotto l'elenco) mostra quanto tempo fa è stata eliminata ciascuna e quanti giorni
  restano; **Recupera** la riporta con tutte le sue modifiche. Dopo 30 giorni viene rimossa definitivamente.
- Il menu **⋯** su una timeline la duplica, apre la sua pagina di revisione o copia il link di revisione da
  mandare a chi deve solo guardare e commentare.

Clicca una timeline per aprirla. Il suo indirizzo è `/app/editor/<project>/timeline/<timeline>`; puoi
salvarlo nei segnalibri. La freccia indietro in alto a sinistra nell'editor torna all'elenco.

## Cosa vedi {#what-you-see}

L'editor occupa tutta la finestra (il menu dell'app si richiude; il pulsante menu in alto a sinistra lo
riporta). I pannelli si possono ridimensionare trascinando le linee sottili tra di loro, oppure con i tasti freccia quando
una linea ha il focus della tastiera.

- **Barra superiore**: il nome e la revisione della timeline, le **pagine** (Media, Taglio, Montaggio, Composizione,
  Colore, Audio, Consegna) al centro, e a destra la pagina di revisione, i layout salvati, le scorciatoie da tastiera
  ed **Esporta** (che apre Consegna). Ogni pagina ha il proprio indirizzo, così un link può aprire
  la pagina che intendi (vedi [Pagine e pannelli](pages-and-panels.md)).
- **Visualizzatore** (al centro): riproduce la timeline. Sotto ci sono i comandi di trasporto: punto di entrata / punto di uscita, taglio precedente
  e successivo, avanzamento per fotogramma, riproduci/pausa, una casella timecode, salti tra marcatori e ripetizione. Le icone a destra
  salvano il fotogramma corrente come immagine, mostrano l'immagine senza correzioni colore e aprono le opzioni
  di anteprima (risoluzione di riproduzione, modalità clip singola).
- **Timeline** (in basso): una barra degli strumenti (selezione, lama e mano, divisione alla testina, aggancio,
  **Rimuovi i silenzi**, [Taglia sulla musica](cut-to-the-music.md) e zoom con un pulsante **adatta**), poi il righello, una corsia per i marcatori e le
  tracce. Ogni clip è un blocco colorato con il suo nome e la durata, una miniatura o una pellicola per le
  immagini e una forma d'onda per l'audio; un bordo tratteggiato significa che la clip è rifilata e dietro c'è ancora
  altra parte dell'origine. La clip selezionata mostra le maniglie di rifilatura ai bordi.
- **Intestazioni delle tracce** (a sinistra delle tracce): i pulsanti bersaglio, blocco, occhio (video) oppure muto e solo (audio),
  e un pulsante **Opzioni traccia**. Il suo menu rinomina la traccia (o fai doppio clic sul nome),
  la colora, rende la riga Piccola, Media o Grande, la sposta su o giù (oppure trascina la maniglia accanto
  alla riga), aggiunge una traccia video o audio sopra o sotto, e la rimuove. Rimuovere una traccia che
  contiene clip chiede prima conferma, e **Annulla** la riporta. **Mostra forme d'onda**, **Mostra miniature** e
  **Mostra nomi delle clip** cambiano solo ciò che vedi, non il tuo montaggio.
- **Pannello sinistro**: **Marcatori**, **Tagli** (una tabella di ogni taglio con i tempi di entrata e di uscita),
  **Trascrizione**, **Sottotitoli**, **Take** (take alternativi), **Struttura**, **Commenti** e
  **Cronologia** (il tuo elenco di annullamenti). Le schede che non ci stanno scorrono di lato.
- **Pannello destro**: **Ispettore** (il nome della clip selezionata, i tempi, l'indicatore di attivazione, e la sua velocità,
  trasformazione, ritaglio, dissolvenze e fotogrammi chiave), **Assistente**, **Impostazioni** (la dimensione del fotogramma e la
  frequenza fotogrammi della timeline), **Versioni** (versioni salvate a cui puoi tornare) e **Confronta**.
- **Barra di stato**: lo stato di salvataggio, quanti fotogrammi l'anteprima ha dovuto saltare, il tipo di anteprima che
  stai ottenendo (vedi sotto), la cache di render e l'ora corrente.

**Versioni** elenca i punti a cui puoi tornare, raggruppati per sessione di montaggio e con i più recenti per primi (per esempio "Oggi 14:02-14:40"). Ognuno dice perché è
stato salvato: un punto di controllo automatico, prima di un ripristino, prima di un render, prima di un suggerimento AI, o una versione a cui hai dato un nome. I punti di controllo automatici vengono
conservati per 30 giorni e la riga dice quanti giorni restano; dai un nome a una versione per conservarla per sempre. Premi **Salva ora una versione con nome** per dare un nome al punto in cui sei,
oppure usa **Dai un nome a questa versione**, **Rinomina** e **Rimuovi il nome** su qualsiasi riga. **Recupera** chiede prima conferma e conserva il tuo stato attuale come "Prima del
recupero", così puoi sempre tornare indietro. Accettare un suggerimento AI salva prima una versione "Prima di un suggerimento AI". Su smartphone il pannello è un foglio in
fondo allo schermo.

Ogni pagina mantiene il visualizzatore e la timeline (Media mostra la timeline su richiesta e Consegna
non ne ha) e dà i propri pannelli laterali agli strumenti che servono a quella pagina.
**Media** mette i raccoglitori a sinistra, il pool delle clip al centro e il visualizzatore e i dettagli
della clip selezionata a destra (la timeline resta nascosta finché non scegli **Mostra timeline**):
seleziona una clip e premi **Inserisci alla testina**
(entra nel taglio più vicino che le va bene e sposta avanti il resto), **Sovrascrivi** (entra esattamente alla testina e sostituisce
ciò che c'è sotto; non si sposta nulla), **Aggiungi in fondo**, oppure
**Posiziona sopra** (esattamente alla testina sulla traccia libera più in alto, o su una nuova traccia, senza
toccare le altre clip), oppure **Metti sopra** (una piccola immagine nell'immagine sopra quelle già presenti;
vedi [Sovrapponi un video a un altro](./layer-videos.md)). Dopo ognuno, un messaggio ti dice dove è finita ("Posizionata su Video 1 a
00:00:12:04") con un pulsante **Annulla**. La clip entra
con la sua durata intera, oppure solo con la parte tra i punti **Entrata** e **Uscita** che hai impostato nel
visualizzatore dell'origine; **Crea subclip** aggiunge quella parte marcata in fondo alla timeline. Un video con audio
porta con sé il suo audio su una traccia **Audio camera** sotto l'immagine, e un solo annulla rimuove entrambi.
Se il file non è ancora stato preparato la sua durata è sconosciuta: entra con 5 secondi e l'
editor te lo dice, così puoi rifilarlo quando il file è pronto. Il riquadro segue la preparazione
da solo; non serve premere **Ricontrolla**. Un file audio si riproduce in un semplice lettore audio
e un'immagine viene mostrata come immagine.

Per dare a una clip già sulla timeline un proprio audio, selezionala e premi **Estrai audio** nell'
Ispettore: l'audio va sulla traccia **Audio camera** nello stesso punto, collegato all'immagine.

### Immagine e audio viaggiano insieme (clip collegate) {#picture-and-sound-travel-together-linked-clips}

Una clip e il suo audio sono **collegati**: un piccolo segno a catena compare su entrambi, l'Ispettore mostra una nota **Collegata**,
e cliccandone uno si selezionano entrambi. Spostare, spostare di un fotogramma, rifilare, dividere, eliminare, copiare, incollare,
duplicare, cambiare la velocità e disattivare una clip agiscono tutti sulla coppia, e un solo annulla annulla
l'intera modifica. Elimina chiude il vuoto su entrambe le tracce.

Per tagliare l'audio da solo (un taglio a J o a L, in cui l'audio inizia prima o finisce dopo l'immagine),
tieni premuto **Alt** (Opzione su Mac) mentre trascini una maniglia di rifilatura o una clip, oppure attiva **Modifica l'audio
separatamente** nella barra degli strumenti e seleziona solo l'immagine o solo l'audio. I due restano collegati. Per
separare una coppia, selezionala e scegli **Scollega** nel menu della clip (**Ctrl+Alt+Maiusc+L**); **Collega le clip
selezionate** (**Ctrl+Alt+L**) riunisce una clip immagine e una clip audio della stessa origine.

Nell'Ispettore, una coppia selezionata mostra un interruttore **Immagine / Audio** per scegliere quale dei due stai
modificando. Le timeline create prima delle clip collegate mostrano un banner, **Aggiungi tracce audio alle mie clip**: collega
le coppie che hai già e aggiunge l'audio mancante, ti dice a parole semplici cosa ha fatto, è un unico
passaggio annullabile e non parte mai da solo. **Non ora** lo nasconde per quella timeline.

## Su smartphone o in una finestra stretta {#on-a-phone-or-a-narrow-window}

Su smartphone (uno schermo più stretto di 600 pixel) l'editor mostra una vista per smartphone che occupa tutto lo schermo. La barra
superiore ha **Indietro**, il nome della timeline e se è salvata, **Annulla**, **Ripristina**, **Esporta** e un menu **⋯** (Assistente,
Revisione, Cronologia, **Trascina le clip con il dito (sperimentale)** e **Apri vista completa**). Il visualizzatore sta sotto con i pulsanti riproduci,
modifica precedente e successiva, e marcatori; la striscia della timeline è sotto, e la barra in fondo ha **Media**, **Modifica**,
**Audio**, **Aspetto** e **Sottotitoli**. Ciascuno apre un pannello che scorre verso l'alto: trascina la sua maniglia verso il basso per rimpicciolirlo o chiuderlo,
tocca la maniglia per ingrandirlo, oppure premi il pulsante indietro del telefono. Mentre un pannello è aperto il visualizzatore si riduce così la timeline
resta in vista. In orizzontale, il visualizzatore passa a sinistra, la timeline a destra e i pannelli si aprono dal bordo destro.
Una nota dice "Vista smartphone. Alcuni strumenti richiedono uno schermo più grande."; **Copia il link** ti permette di aprire l'editor altrove e **Apri vista
completa** carica il layout per tablet e ricorda la scelta su quel dispositivo. Un tablet (da 600 a 1100 pixel) mantiene il layout completo con bersagli touch più grandi: gli spazi di lavoro stanno in una colonna stretta a sinistra, e su un tablet stretto gli strumenti e l'ispettore scorrono dai lati con i pulsanti sopra l'immagine. Il tablet funziona, ma non è ancora certificato, e l'editor lo dice nel menu Altro dell'intestazione.
È sempre lo stesso editor, quindi una modifica fatta su smartphone viene salvata come qualsiasi altra. Per guardare un render e lasciare
commenti, la **pagina di revisione** della timeline è ancora più semplice.

Dove trovare le cose su smartphone:

| Vuoi | Tocca |
|---|---|
| Aggiungere foto, video o audio | **Media** nella barra in fondo, oppure **Aggiungi media** nella barra sopra |
| Tagliare, rimuovere, copiare o spostare una clip | Tocca la clip, poi la barra sopra quella in fondo (**Dividi**, **Duplica**, **Rifila**, **Sposta**, **Altro**, **Elimina**) |
| Sistemare l'audio | **Audio** (**Abbassa la musica**, **Pulisci la voce**, volume, voce narrante) |
| Cambiare l'aspetto | **Aspetto** (look, luminosità, contrasto, titoli) |
| Correggere un sottotitolo o tagliare i silenzi | **Sottotitoli** |
| Creare il film e inviarlo | **Esporta** nella barra superiore, poi **Render**, **Scarica** o **Condividi il film** (vedi [Esporta il tuo film](export-your-film.md)) |
| Guardarlo e leggere i commenti | **⋯** poi **Revisione** (la pagina di revisione funziona su smartphone) |
| Lavorare su un'immagine o una miniatura | **Immagini** nella pagina Editor (vedi [Immagini su smartphone](thumbnails.md#on-a-phone)) |
| Usare l'editor completo | **⋯** poi **Apri vista completa** (non è ottimizzato per gli smartphone) |

Su uno schermo touch:

- Tocca una clip per selezionarla. La barra sopra quella in fondo mostra allora **Dividi** (alla testina), **Duplica**, **Rifila**,
  **Sposta**, **Altro** e, separato dal resto, **Elimina** (chiude il vuoto, anche sull'audio collegato). Nulla
  chiede "sei sicuro": **Elimina** e **Solleva** mostrano **Annulla** per sei secondi, e Annulla nella barra superiore riporta comunque indietro qualsiasi cosa.
- Senza nulla di selezionato la stessa barra offre **Aggiungi media**, **Aggiungi testo** (un titolo alla testina), **Marcatore** e **Seleziona**.
- **Seleziona** attiva la modalità di selezione: tocca le clip una per una per formare un gruppo (un'immagine e il suo audio collegato vanno insieme),
  poi **Duplica**, **Sposta**, **Solleva** o **Elimina** tutte insieme. **Svuota** azzera il gruppo; **Fatto** esce dalla modalità.
- **Rifila** apre un foglio con Entrata, Uscita e Durata della clip (scrivi un numero di fotogrammi o un timecode), pulsanti **-10 / -1 / +1 / +10**
  fotogrammi per l'inizio e la fine, e la modalità di rifilatura (Sposta tutto il resto, Sposta il taglio, Cambia la parte mostrata, Sposta la clip). Le modifiche si vedono subito e
  un solo **Annulla** riporta indietro tutto ciò che hai fatto in quel foglio. Se una modifica non si può fare, il foglio dice perché.
- **Clip** (un pulsante sulla striscia della timeline quando è selezionata una clip video o audio, anche in **Altro**) apre il foglio della clip: **Adatta**
  e **Riempi**, un tastierino 3 per 3 per posizionare l'immagine, **In orizzontale**, **In verticale**, **Dimensione** e **Opacità**, **Velocità**
  (0,5x, 1x, 1,5x, 2x, o qualsiasi velocità), **Volume** con **Muto**, e **Dissolvenza in entrata** e **Dissolvenza in uscita**. Ogni cursore ha una casella numerica
  accanto per scrivere un valore; ogni modifica è un **Annulla**. Tutto ciò che su desktop è animato viene mostrato ma non modificato qui
  ("Animato su desktop"), e rotazione libera, ritaglio, inversione e transizioni restano su desktop; una clip che li ha già lo dice.
  **Ruota di 90°** nel pannello della clip gira di un quarto di giro un'immagine coricata, come su desktop.
- **Aspetto** apre **Aspetto e correzioni**: scegli un look per nome (oppure **Originale**), imposta **Intensità**, e regola Luminosità, Contrasto,
  Saturazione, Calore e Tinta. **Mostra l'originale** (oppure tieni premuto **Tieni premuto per confrontare**) mostra l'immagine senza il suo colore. **Applica a
  tutte le clip** dà a ogni clip video lo stesso look e le stesse impostazioni in un solo passaggio, quindi un solo **Annulla** lo riporta indietro. **Titoli** aggiunge un titolo
  alla testina (scrivi le parole, scegli Semplice, Terzo inferiore, Riquadrato, Contorno o Scheda) e modifica le parole del titolo che hai selezionato.
  Ruote colore, curve, chiave, maschere e riquadratura sono su desktop; una clip che li ha già li mantiene.
  Le riprese in HDR fatte con uno smartphone vengono convertite in colori standard prima che venga applicata la tua correzione, così non risultano slavate o bruciate. L'anteprima mostra una clip HDR in modo approssimativo (la clip è contrassegnata come **Clip HDR**); il film finito è esatto.
- **Altro** contiene **Seleziona più clip**, **Adatta, posizione e dissolvenze**, **Velocità**, **Volume**, **Sposta prima** e **Sposta dopo**
  (scambia posto con la clip vicina), **Estrai audio**, **Fermo immagine**, **Collega immagine e audio** e
  **Scollega**, **Copia**, **Taglia** e **Incolla alla testina**, **Marcatore precedente** e **Marcatore successivo**,
  **Azzera entrata e uscita**, **Aggancio** e **Solleva (lascia un vuoto)**.
- Lo smartphone non ha la barra degli strumenti della timeline: **Annulla** e **Ripristina** sono nella barra superiore, e la striscia della timeline ha **Adatta** (tutta la timeline sullo schermo); pizzica per fare lo zoom.
- Trascina il bordo giallo di una clip selezionata per rifilarla, trascina una parte vuota della timeline per scorrerla, pizzica per fare lo zoom e trascina con due dita
  per scorrere senza cambiare la selezione. Tocca due volte una clip per ingrandirla. Ognuno di questi gesti ha anche un pulsante (**Rifila**, **Adatta**,
  i pulsanti clip precedente e successiva). Spostare una clip tenendola premuta e trascinandola è sperimentale e disattivato finché non lo attivi in
  **⋯** (**Trascina le clip con il dito**); fino ad allora, tieni premuta una clip per il suo menu, oppure usa **Sposta** e **Sposta prima / dopo**.
- **Media** (e **Aggiungi media** nella barra) apre il foglio Aggiungi media. **Scegli foto, video o audio** ti permette di scegliere file dal rullino
  o dai file (anche più di uno alla volta), **Scatta una foto o un video** usa la fotocamera, e ogni file mostra a che punto è ("Caricamento 40%", "Aggiunto",
  oppure "Non caricato" con **Riprova**). Ogni riquadro dice se la sua anteprima è **Pronta**, **Anteprima in elaborazione**, oppure "Anteprima non pronta: puoi
  comunque aggiungerlo". Tocca **Azioni** su un riquadro per **Inserisci alla testina**, **Aggiungi in fondo**, **Aggiungi al raccoglitore**, **Rimuovi dal raccoglitore**, **Parole chiave e note** e
  **Anteprima**. Dopo che aggiungi una clip il foglio si riduce così puoi vedere la timeline, e **Annulla** è offerto per sei secondi. Le etichette dei raccoglitori in alto
  filtrano i riquadri, e **Nuovo raccoglitore** ne crea uno da un nome. Trascina un dito di lato sull'immagine di un riquadro per scorrere la clip.
- **Esporta** nella barra superiore apre il foglio di esportazione: **Render** resta in fondo, e un film finito può essere scaricato o condiviso lì stesso.
- La casella timecode apre il tastierino numerico: scrivi solo cifre, quindi `1215` diventa 12 secondi 15 fotogrammi.
- **Audio** apre il pannello audio. **Abbassa la musica sotto la voce** abbassa la musica ovunque qualcuno parli, e **Pulisci la voce** rimuove
  il rumore di fondo e uniforma il volume sulle tracce voce; ciascuno è un tocco e un **Annulla**. Ogni traccia audio ha **Muto**, **Solo** e un
  cursore del volume (**Torna a 0 dB** lo reimposta), e c'è anche il volume della clip selezionata. **Voce narrante** registra dal microfono alla
  testina; se il browser non può registrare dice "La registrazione non è disponibile in questo browser. Carica invece un file vocale." e ti lascia scegliere un file.
- **Sottotitoli** apre il pannello dei sottotitoli: ogni sottotitolo si può modificare (una casella a tutta larghezza, salvata quando tocchi **Fatto**), **Dividi** alla testina,
  **Unisci al successivo** o **Elimina**; **Aggiungi alla testina** e **Genera dal parlato** ne creano di nuovi. **Rimuovi i silenzi** mostra i tratti silenziosi
  e cosa succederà tagliandoli, prima che cambi qualcosa. **Elimina parole** mostra le parole pronunciate: tieni premuta una parola, oppure tocca **Seleziona parole** e
  tocca la prima e l'ultima parola, trascina le due maniglie rotonde (o usa i pulsanti freccia) per cambiare l'intervallo, poi **Elimina** le taglia e chiude il vuoto.
  **Altri strumenti** in fondo contiene l'analisi, la trascrizione, i marcatori e gli aspetti dei sottotitoli.
- Se la timeline è stata modificata altrove, una breve barra sopra quella in fondo lo dice: **Tieni la mia copia** salva le tue modifiche
  come nuova timeline, **Usa la più recente** carica ciò che è salvato. Nascondere l'app o passare a un'altra app non fa perdere nessuna modifica.

Alcuni strumenti richiedono uno schermo più grande e lo dicono su smartphone invece di mostrare un comando che non
funzionerebbe: clip composte, bus e automazione, curve, tracciamento di una finestra di potenza lungo una clip,
spostare raccoglitori dentro altri raccoglitori, trascinare i paragrafi della trascrizione (le frecce li riordinano comunque) e
selezionare disegnando un riquadro (usa invece **Seleziona** nella barra per toccare più clip; il menu **Clip** lo dice).

Alcune scorciatoie da tastiera non hanno un pulsante su smartphone: i tasti di scorrimento J, K e L,
Stop, riproduci da entrata a uscita, gli spostamenti della testina di dieci fotogrammi, lo spostamento
di una clip su un'altra traccia, le pagine Taglio, Composizione e Colore (richiedono uno schermo più
grande; lo smartphone ha Media, Modifica, Audio, Aspetto e Sottotitoli) e la tavolozza dei comandi. Il
menu in eccesso dello smartphone le elenca con il motivo; l'elenco completo è in
[Scorciatoie da tastiera](shortcuts.md#on-a-phone).

## Riprodurre e muoversi {#playing-and-moving-around}

| Azione | Tasti |
|---|---|
| Riproduci / pausa | `Space` |
| Riproduci da entrata a uscita | `Shift+Space` |
| Shuttle indietro | `J` |
| Stop | `K` |
| Shuttle avanti | `L` |
| Indietro di un fotogramma | `←` |
| Avanti di un fotogramma | `→` |
| Indietro di dieci fotogrammi | `Shift+←` |
| Avanti di dieci fotogrammi | `Shift+→` |
| Taglio precedente | `↑` |
| Taglio successivo | `↓` |
| Marker precedente | `Shift+↑` |
| Marker successivo | `Shift+↓` |
| Vai all'inizio | `Home` |
| Vai alla fine | `End` |
| Segna entrata | `I` |
| Segna uscita | `O` |
| Cancella entrata e uscita | `Alt+X` |
| Aggiungi marker | `M` |
| Strumento lama | `B` |
| Strumento selezione | `V` |
| Strumento mano | `H` |
| Modalità trim: ripple (predefinita) | `R` |
| Modalità trim: roll | `T` |
| Modalità trim: slip | `Y` |
| Modalità trim: slide | `U` |
| Attiva/disattiva aggancio | `N` |
| Elimina la selezione (chiude il vuoto) | `Delete` o `Backspace` |
| Solleva la selezione (lascia un vuoto) | `Shift+Delete` o `Shift+Backspace` |
| Taglia alla testina | `Ctrl/Cmd+B` |
| Sposta a sinistra di un fotogramma | `,` |
| Sposta a destra di un fotogramma | `.` |
| Sposta a sinistra di dieci fotogrammi | `Shift+,` |
| Sposta a destra di dieci fotogrammi | `Shift+.` |
| Copia le clip | `Ctrl/Cmd+C` |
| Taglia le clip (copia, poi elimina) | `Ctrl/Cmd+X` |
| Incolla le clip alla testina | `Ctrl/Cmd+V` |
| Duplica le clip | `Ctrl/Cmd+D` |
| Sposta la clip sulla traccia sopra | `Alt+↑` |
| Sposta la clip sulla traccia sotto | `Alt+↓` |
| Collega le clip selezionate (immagine e audio) | `Ctrl/Cmd+Alt+L` |
| Scollega le clip selezionate | `Ctrl/Cmd+Shift+Alt+L` |
| Deseleziona | `Escape` |
| Annulla | `Ctrl/Cmd+Z` |
| Ripristina | `Ctrl/Cmd+Shift+Z` o `Ctrl/Cmd+Y` |
| Seleziona tutto | `Ctrl/Cmd+A` |
| Seleziona in avanti dalla testina | `A` |
| Seleziona indietro dalla testina | `Shift+A` |
| Seleziona tutto sulla traccia | `Alt+A` |
| Strumento maschera | `Shift+M` |
| Strumento reinquadratura | `Shift+F` |
| Strumento corner pin | `Shift+P` |
| Maschera: passa da rettangolo a ellisse | `Shift+S` |
| Maschera: inverti | `Shift+I` |
| Composizione: rimuovi la voce selezionata | `Alt+Backspace` |
| Composizione: voce precedente | `[` |
| Composizione: voce successiva | `]` |
| Aggiungi clip di regolazione | `Ctrl/Cmd+Alt+A` |
| Vai a Media | `Shift+1` |
| Vai a Taglio | `Shift+2` |
| Vai a Montaggio | `Shift+3` |
| Vai a Composizione | `Shift+4` |
| Vai a Colore | `Shift+5` |
| Vai ad Audio | `Shift+6` |
| Vai a Consegna | `Shift+7` |
| Tavolozza dei comandi | `Ctrl/Cmd+K` |

Premi di nuovo `J` o `L` per scorrere più veloce (1x, 2x, 4x, 8x). Premi `?` (oppure scegli l'icona
della tastiera in alto a destra) per un elenco ricercabile di tutte le scorciatoie, e `Ctrl/Cmd+K` per
la tavolozza dei comandi; vedi [Scorciatoie da tastiera](shortcuts.md). **Scorciatoie** (in alto a
destra) ti permette di cambiarle.

I tuoi punti di entrata e di uscita vengono salvati con la timeline: ci sono ancora dopo aver ricaricato la pagina,
e annulla e ripristina riportano anche i punti precedenti. Un punto viene salvato solo quando entrambi sono impostati e quello di uscita
viene dopo quello di entrata. Quando entrambi sono impostati, **Consegna** parte con il render limitato a quell'
intervallo.

Puoi anche cliccare o trascinare sul righello per spostare la testina, e scrivere un tempo nella casella timecode.
Scrivere cifre riempie la casella da destra, quindi `1215` significa 12 secondi e 15 fotogrammi. Scrivere `+15`
sposta avanti di 15 fotogrammi, `-2s` indietro di due secondi, `+1:00` avanti di un secondo.

## Selezionare, tagliare, spostare e rifilare {#selecting-cutting-moving-and-trimming}

- **Clicca** una clip per selezionarla; **Maiusc+clic** su un'altra clip della stessa traccia per selezionare tutto
  ciò che sta in mezzo; **trascina su una parte vuota di una traccia** per disegnare un riquadro attorno a più clip;
  `Ctrl/Cmd+A` seleziona tutto.
- Il menu **Seleziona** sopra la timeline offre **Seleziona tutto**, **Seleziona in avanti dalla
  testina**, **Seleziona all'indietro dalla testina**, **Seleziona tutto su questa traccia** e **Deseleziona**.
  I tasti `A` e `Shift+A` selezionano ogni clip dalla testina in avanti o all'indietro (una clip in cui
  si trova la testina conta in entrambe le direzioni); `Alt+A` seleziona ogni clip sulla traccia della clip selezionata.
- **Lama** (`B` o il pulsante forbici) divide una clip dove clicchi. **Lama alla testina** divide
  ogni clip sotto la testina.
- **Sposta**: trascina una clip lungo la sua traccia o su un'altra traccia dello stesso tipo.
- **Rifila**: trascina la maniglia a una delle estremità della clip selezionata. Il menu **Rifila:** nella barra degli strumenti sceglie
  cosa fa il trascinamento: **Ripple** (predefinito: le clip dopo si spostano con il bordo), **Roll** (sposta il taglio
  tra due clip), **Slip** (cambia quale parte dell'origine si vede, nulla si sposta) o **Slide**
  (sposta la clip; la clip vicina dal lato in cui trascini assorbe la modifica, e l'altro lato mantiene il suo bordo, quindi lì può aprirsi un vuoto).
- **Unisci, Adatta al vuoto, Sostituisci** (nel menu **Clip** e nel menu del clic destro, su uno schermo più grande): **Unisci con la clip successiva** rimette insieme le due
  metà di una clip divisa. **Adatta al vuoto** allunga o comprime una clip per riempire il vuoto dopo di essa e ti dice la nuova velocità
  ("La velocità diventa 83%"). **Sostituisci con...** riproduce un altro file del pool dei media nello stesso punto e con la stessa durata. Quando una di queste non può essere eseguita, il
  motivo è scritto sotto.
- **Elimina**: `Delete` (o `Backspace`) rimuove le clip selezionate e chiude il vuoto, anche sull'audio abbinato alla clip (la clip corrispondente su
  un'altra traccia, stessa origine e stesso punto). `Shift+Delete`
  le solleva invece e lascia un vuoto; **Chiudi il vuoto prima di questa clip** nel menu **Clip** lo chiude in seguito.
- **Annulla / Ripristina**: i pulsanti freccia a sinistra della barra degli strumenti della timeline (oppure `Ctrl+Z` / `Ctrl+Shift+Z`).
- **Aggancio** (`N`) fa scattare un bordo trascinato sui tagli vicini, sui marcatori, sulla testina e sui punti
  di entrata e di uscita, e un'etichetta dice quale ha preso. Disattivalo quando devi posizionare qualcosa tra di
  essi. Le modalità di trim, l'indicazione e l'anteprima del ripple sono spiegate in
  [Modalità di trim](pages-and-panels.md#trim-modes).
- **Strumento selezione / strumento mano** (`V` / `H`): lo strumento mano fa scorrere la timeline invece di modificarla.

Una clip trascinata diventa una modifica solo quando la rilasci, quindi un trascinamento è un passaggio annullabile.

Il menu **Aggiungi** sopra la timeline posiziona un **Titolo** o una **Scheda colore** di tre secondi alla
testina, sulla traccia video più alta che ha spazio lì (oppure su una nuova traccia video sopra le altre).
**Titolo con stile e durata...** apre un'unica finestra: scegli uno stile (Semplice, Terzo inferiore, Riquadrato, Contorno,
Scheda) con un'anteprima dal vivo, scrivi il testo, imposta la durata e scegli la traccia. **Scheda colore con un colore
a scelta...** offre campioni di colore con nome oppure un #RRGGBB personalizzato. Entrambi dicono dove è finito l'elemento.
Il nuovo elemento è selezionato così puoi cambiarlo subito nell'Ispettore (il colore di una scheda colore
ha gli stessi campioni anche lì), e un solo annulla lo rimuove.

**Fermo immagine qui (2 s)** è nel menu della clip (e nel pulsante "..." Clip): divide la clip sotto
la testina, mantiene quel fotogramma per due secondi e sposta più avanti il resto di quella traccia. La pausa è
senza audio, e un solo annulla rimette tutto a posto.

## Marcatori {#markers}

Premi `M` per aggiungere un marcatore alla testina, oppure scrivi un nome nel pannello **Marcatori** e premi
**Aggiungi**. Clicca un marcatore per saltare lì; il pannello ti permette di rinominarlo o rimuoverlo.

Le timeline nate da un'esecuzione di agenti mostrano anche i **marcatori di ragionamento**: per ogni taglio fatto dall'agente,
il motivo che ha dato, al fotogramma a cui si applica. Sono etichettati con l'agente e il servizio AI
che ha preso la decisione. Puoi leggerli ma non modificarli: sono le note degli agenti, non le tue.

## Annulla, ripristina e salvataggio {#undo-redo-and-saving}

- `Ctrl/Cmd+Z` annulla, `Ctrl/Cmd+Shift+Z` (o `Ctrl/Cmd+Y`) ripristina. Il pannello **Cronologia** elenca ogni modifica
  della sessione; clicca una voce qualsiasi per tornare a quel punto.
- Il salvataggio è automatico. La barra di stato mostra **Salvataggio…**, poi **Salvato · rev N**. Non serve mai
  premere un pulsante di salvataggio.
- Se l'editor dice **Non valido**, ha trovato un problema con la tua ultima modifica e non l'ha inviata; annulla
  la modifica.
- Se dice **Sola lettura** con un banner, qualcun altro (o tu in un'altra scheda) ha salvato una versione più recente
  mentre stavi modificando. Le tue ultime modifiche restano sullo schermo così puoi vederle, ma non sono
  salvate. Premi **Ricarica l'ultima** per continuare dalla versione più recente; le tue modifiche non salvate vengono
  scartate, mai unite automaticamente: è voluto, così due persone non possono mai sovrascriversi in silenzio
  il lavoro a vicenda.

## Tornare a una versione precedente {#going-back-to-an-earlier-version}

ReelBolt conserva dei punti di ripristino per te. Ne salva uno quando inizi a modificare dopo una pausa di un paio di
minuti, prima di un recupero e prima di ogni render, più la primissima versione di ogni timeline. Puoi
anche salvare tu lo stato attuale e dargli un nome. **I punti di ripristino che ReelBolt salva per te vengono conservati
per 30 giorni. Una versione a cui hai dato un nome, e la prima versione, vengono conservate finché non le rimuovi.** Una timeline
eliminata può essere recuperata dal cestino per 30 giorni, poi sparisce per sempre.

L'elenco delle versioni mostra quali punti si possono recuperare. **Recupera** riporta quella copia come *nuova* revisione, quindi non si perde mai
nulla: puoi recuperare anche il recupero. Recupera chiede prima conferma, perché sostituisce l'intera
timeline e azzera la cronologia di Annulla di questa sessione.

## Cosa ti mostra l'anteprima, e cosa non può mostrare {#what-the-preview-shows-you-and-what-it-cannot}

Il visualizzatore riproduce la timeline stessa, non un file generato. Per farlo in modo veloce usa copie proxy
(a bassa risoluzione) delle tue clip, quindi:

- Un'etichetta sopra il visualizzatore dice quanto è fedele il fotogramma corrente. **Fedele** significa che ciò che vedi
  è ciò che farà il render (tagli, quale clip è sopra, volume). **Approssimativa** significa che il render
  differirà nei dettagli (titoli e sottotitoli sono disegnati con un carattere sostitutivo, i valori animati vengono
  interpolati dal browser, una clip al rallentatore o accelerata mostra i tempi giusti ma non lo
  stiramento dell'audio del render, una fusione che differisce di un livello dove una clip è parzialmente trasparente). **Render necessario** significa che il visualizzatore non può mostrare quell'effetto; compare nel video finito.
  **Anteprima non pronta** significa che una parte dell'immagine manca o è ancora in preparazione. Una clip con
  più regolazioni elenca una riga per ciascuna, non solo la prima.
- **Tocca o clicca l'etichetta** per vedere il perché, a parole semplici, per esempio "Clip "Intervista": rallentatore
  (0,5x) ...". Ogni riga su una clip è un pulsante: seleziona quella clip e sposta la testina lì,
  così puoi andare direttamente a ciò di cui parla l'etichetta. Le righe sulle clip che non sono sullo schermo
  in questo momento sono elencate sotto "Altrove in questa timeline". L'etichetta non è mai "Fedele" mentre
  l'anteprima è l'Anteprima semplice, gira a qualità ridotta, o manca un'immagine.
- **Preparazione dell'anteprima.** Quando apri una timeline, ReelBolt chiede una copia piccola e veloce (un
  proxy) di ogni clip che usa. Finché la copia di una clip non è pronta il visualizzatore lo dice invece di mostrare
  il nero: "Preparazione anteprima 42%" con una barra di avanzamento, "Origine offline" quando il file non può essere
  raggiunto, oppure "Anteprima non riuscita: ..." con un pulsante **Riprova**. Una riga sopra il visualizzatore ("Preparazione
  anteprima 42%", "Anteprima non riuscita per 2 clip") apre l'elenco delle clip con il loro stato, **Riprova**
  per ognuna fallita e **Prepara tutte**. Quando tutto è pronto la riga sparisce.
- **Qualità dell'anteprima** (il pulsante dei cursori sotto il visualizzatore) è **Auto**, **Piena**, **Metà** o
  **Un quarto**. Con Auto il visualizzatore mostra sempre l'anteprima dal vivo, su computer, tablet e
  smartphone: ogni livello, maschera, correzione colore ed effetto che imposti è sull'immagine. Auto disegna
  l'immagine non più grande di come il visualizzatore la mostra, e ancora più piccola su uno smartphone, su un
  browser che disegna con grafica software, o su un computer con quattro o meno core di processore o circa 4 GB
  di memoria o meno.
- **Riproduzione fluida su un dispositivo più lento.** Se il dispositivo non regge il ritmo durante la
  riproduzione, il visualizzatore rimpicciolisce l'immagine invece di togliere effetti o saltare fotogrammi, e
  sull'immagine compare la piccola nota "Più piccola in riproduzione". Metti in pausa e l'immagine a
  dimensione piena torna subito. Il visualizzatore ricorda di cosa ha avuto bisogno il tuo dispositivo, così la
  riproduzione successiva parte fluida.
- **Anteprima semplice** è un interruttore nello stesso menu, per il raro caso in cui l'anteprima dal vivo non
  riesce a girare bene a nessuna dimensione. Riproduce una clip alla volta dalla sua copia piccola, senza livelli
  né effetti, ed è etichettata **Anteprima semplice** sopra il visualizzatore. Il visualizzatore la propone solo
  quando l'immagine non può più rimpicciolirsi. Le tue modifiche restano esatte in ogni caso: il video finito è
  sempre prodotto dal render. La tua scelta della qualità viene ricordata su questo dispositivo.
- **Schermo intero.** Il pulsante accanto alle opzioni dell'anteprima (o il tasto `P`) mostra l'immagine e i
  suoi controlli di riproduzione su tutto lo schermo. Premilo di nuovo, o premi `Esc`, per tornare indietro.
- La barra di stato conta i **fotogrammi persi**. Se il numero sale, il tuo computer non riesce a decodificare e
  disegnare abbastanza in fretta; il visualizzatore rimpicciolisce l'immagine da solo, e puoi anche abbassare la qualità dell'anteprima. Il conteggio non è mai
  nascosto, così sai sempre quando l'anteprima sta barando.
- La barra di stato dice anche che **tipo di anteprima** hai: **dal vivo** (il compositore completo),
  **semplice** (una clip alla volta dalla sua copia piccola), o **non disponibile** (non esiste ancora una copia per la clip;
  lo spazio di lavoro Media mostra ciò che è stato generato).
- Mentre il visualizzatore si avvia mostra un fotogramma grigio neutro delle dimensioni della tua immagine, così il layout
  non salta e non c'è nessun lampo nero.

Il video finito è sempre prodotto da un **render**, mai dall'anteprima. Lo avvii dallo spazio di lavoro
**Consegna**; vedi [Generare un video finito (Consegna)](#rendering-a-finished-video-deliver).

## Tracce e impostazioni della timeline {#tracks-and-timeline-settings}

I piccoli pulsanti **+V** e **+A** sopra i nomi delle tracce aggiungono una traccia video o audio in
fondo alla pila; l'icona del cestino su una traccia la rimuove, con tutto ciò che contiene, in un unico
passaggio annullabile. I pulsanti blocco, occhio, muto e solo su una traccia ora vengono salvati con la timeline, quindi
sono uguali quando la riapri. La scheda **Impostazioni** a destra modifica la dimensione del fotogramma, la frequenza fotogrammi,
la frequenza di campionamento, il rapporto dei pixel, la regola del formato e il timecode iniziale: cambia ciò che ti serve e premi
**Applica**, una sola modifica annullabile per tutto.

## Spostare le clip di poco {#nudging-clips}

Con una o più clip selezionate, premi `,` per spostarle di un fotogramma prima e `.` di un fotogramma dopo;
tieni premuto Maiusc per dieci fotogrammi. Uno spostamento che sovrapporrebbe un'altra clip o partirebbe prima dell'inizio viene
rifiutato e non si muove nulla.

## Rimuovere i silenzi {#removing-silence}

**Rimuovi i silenzi** (nella barra degli strumenti della timeline) elenca le pause misurate dall'analisi del girato, riportate sulle
clip della traccia scelta, ciascuna con il suo timecode e la sua durata. Deseleziona quelle che vuoi tenere, scegli
quanti fotogrammi lasciare su ogni lato di un taglio e quanto breve deve essere una pausa da ignorare, leggi il riepilogo
("3 tagli, 212 fotogrammi rimossi") e premi **Rimuovi**. Tutti i tagli arrivano in un solo passaggio, quindi un solo Annulla
riporta tutto indietro. La finestra può offrire solo ciò che è stato misurato: una timeline che non è partita
da un'esecuzione analizzata non ha pause da elencare, e la finestra lo dice.

Per tagli che seguono la musica invece delle pause, vedi [Taglia sulla musica](cut-to-the-music.md).

## Generare un video finito (Consegna) {#rendering-a-finished-video-deliver}

Apri lo spazio di lavoro **Consegna** (oppure premi **Esporta** in alto a destra). Scegli un formato: HD 1080p, 4K,
verticale, quadrato, verticale 4:5, la dimensione della timeline in qualità master, oppure solo audio; decidi se
generare l'intera timeline o solo la parte tra i tuoi punti di entrata e di uscita, dai un nome al file
(o lascia vuoto per usare il nome della timeline), se vuoi normalizza il volume e premi
**Render**. Il render funziona come qualsiasi altro lavoro: la coda sotto lo mostra in coda, in corso con
il suo avanzamento, poi finito con un pulsante di download (salva il file con il nome che gli hai dato), oppure fallito con il motivo. **Interrompi** ferma un
render in corso. Le modifiche non salvate non sono in un render finché non vengono salvate, e il pulsante lo dice.

Se un render finito ha dovuto lasciare fuori qualcosa (un inserto dello schermo, per esempio, che i video generati non includono ancora) o ha
qualcosa da dirti, la sua riga lo dice in una riga come "2 elementi sono stati lasciati fuori da questo render · 1
nota". Tocca o clicca **Mostra dettagli** per leggerli uno per uno; un elemento è chiamato come lo è nella tua
timeline.

Il tuo piano fissa la risoluzione massima di un'esportazione, misurata sul lato corto dell'immagine
(vedi [Risoluzione di esportazione in ogni piano ReelBolt](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan)).
HD 1080p, verticale, quadrato, verticale 4:5 e qualità master sono sempre disponibili: se il formato è
più grande di quanto permetta il tuo piano, l'esportazione viene ridotta al limite del piano con la stessa forma e
le stesse impostazioni di qualità, e il modulo dice "Esporti a {cap}p con il tuo piano." (per esempio, un'esportazione
verticale in prova esce a 720×1280). 4K è una dimensione che scegli di proposito, quindi è
in grigio quando il tuo piano non la include, con una nota che indica il limite del tuo piano e un link
ai piani.

Ogni render ricorda quali parti della timeline ha potuto riutilizzare da render precedenti: dopo un
render della versione che stai guardando, una sottile striscia in fondo al righello diventa blu dove una
parte è stata riutilizzata e rossa dove è stata generata di nuovo, e la barra di stato mostra il conteggio. Modifica
qualsiasi cosa e la striscia sparisce fino al render successivo: non mostra mai la cache di una versione che
non è più sullo schermo.

## Spostare, scalare, ruotare e sfumare una clip {#moving-scaling-rotating-and-fading-a-clip}

Seleziona una clip e apri l'**Ispettore**. Tutto è in unità semplici. **Opacità** è una percentuale (un campo
e un cursore). **Dissolvenza in entrata** e **Dissolvenza in uscita** sono in secondi; la clip selezionata mostra anche una maniglia rotonda
in ogni angolo superiore, così puoi trascinare una dissolvenza a occhio (oppure dare il focus alla maniglia e usare i tasti freccia per un fotogramma
alla volta). **Dissolvenza automatica** mette una dissolvenza di mezzo secondo a entrambe le estremità. La sezione **Trasformazione** sposta l'immagine
(Posizione X e Y sono frazioni del fotogramma, quindi 0,25 è un quarto della larghezza verso destra), la scala e
la ruota; **Ritaglio** rifila ogni bordo. Ogni sezione ha un **Reimposta**. Un valore fuori intervallo viene rifiutato
con una frase che dice qual è l'intervallo, e il campo mostra di nuovo il valore memorizzato; nulla viene
cambiato in silenzio.

L'Ispettore mostra i comandi che si applicano a dove si trova la clip. Una clip su una traccia immagine offre
i comandi per l'immagine (velocità, inversione, fermo immagine, Anima, Trasformazione, Ritaglio, Dissolvenze) e nessun comando audio:
l'audio di una clip su una traccia immagine non viene riprodotto né generato, quindi usa **Estrai audio** per metterlo su una
traccia audio. Una clip su una traccia audio offre velocità, inversione, Canali audio, Dissolvenze e **Volume**, e nessuno dei
comandi per l'immagine. **Volume** è una percentuale con un interruttore **Muto** (0% è l'impostazione più bassa; riattivare l'audio
riporta il volume che avevi). La casella dB e il suo rombo dei fotogrammi chiave sono sotto **Avanzate**. Un numero che scrivi
conta come una modifica quando premi Invio, esci dal campo o smetti di scrivere, quindi un solo Annulla lo riporta indietro.

### Raddrizzare una clip coricata {#turn-a-sideways-clip-upright}

Un video girato con lo smartphone in verticale appare dritto nell'editor e nel film esportato, senza
nessun passaggio da parte tua. Anche le timeline create prima di questa correzione lo mostrano bene.

Se una clip resta comunque coricata (per esempio perché lo smartphone l'ha registrata di lato),
selezionala e premi **Ruota di 90°** nell'Inspector. Ogni pressione gira l'immagine di un quarto di giro
in senso orario. L'etichetta accanto al pulsante indica dove si trova (**Dritto**, 90°, 180° o 270°), e
quattro pressioni la riportano com'era. La rotazione avviene prima che la clip venga adattata al
fotogramma, quindi un'immagine verticale riempie una timeline verticale invece di girare dentro un
riquadro orizzontale. Il ritaglio si applica all'immagine girata. Ogni pressione è un **Annulla**.
**Rotazione** in **Trasformazione** è diversa: inclina di un angolo qualsiasi l'immagine già nel fotogramma.

### Sposta l'immagine direttamente nel visualizzatore {#move-the-picture-right-on-the-viewer}

Clicca l'immagine nel visualizzatore per selezionare la clip, l'immagine fissa o il titolo sotto il puntatore, oppure selezionala sulla timeline. Con la testina su di essa,
il visualizzatore mostra le maniglie attorno all'immagine, anche mentre il film è in riproduzione (la prima pressione lo mette in pausa). Trascina l'immagine per spostarla,
trascina un angolo per ridimensionarla in qualsiasi forma (tieni premuto **Maiusc** per mantenere la forma, **Alt** per ridimensionare dal centro), trascina il centro di un
lato per allungarla in una sola direzione e trascina la maniglia rotonda per ruotare (tieni premuto **Maiusc** per ruotare a
scatti di 15 gradi). L'immagine si aggancia al centro, ai terzi, ai margini di sicurezza e ai bordi delle
altre immagini quando ci passi vicino; tieni premuto **Maiusc**, **Ctrl** o **Cmd** per muoverti liberamente. Il pulsante **Trasforma** sopra il visualizzatore (`Shift+T`) attiva e disattiva le maniglie. Vedi [Sposta e ridimensiona sull'immagine](./move-and-resize-on-the-picture.md). Con l'immagine in focus, i tasti
freccia la spostano di un pixel (**Maiusc** per dieci). Il menu **Posizione** sopra il visualizzatore ha **Adatta**,
**Riempi**, una griglia 3 x 3 che appoggia l'immagine a un bordo o a un angolo, e quattro preset di "immagine nell'angolo".
Se un valore ha già dei fotogrammi chiave, la modifica viene memorizzata come fotogramma chiave alla testina.
Un gesto è un Annulla, e i numeri dell'Ispettore seguono il trascinamento.

## Animare una clip {#animating-a-clip}

Seleziona una clip immagine e guarda **Anima**. Tocca **Ingrandisci**, **Riduci**, **Pop**, **Scorri da sinistra,
destra, alto o basso**, **Dissolvenza** o **Ken Burns**. Ogni tocco imposta tutti i fotogrammi chiave di cui il movimento ha bisogno come un'unica
modifica, quindi un solo Annulla riporta indietro l'intero movimento, e la testina salta all'inizio della clip così puoi
premere play. Ogni preset ha una piccola anteprima in ripetizione. Sotto le etichette puoi impostare quanto dura il movimento,
quanto è intenso e, per scorrimenti e dissolvenze, se la clip **entra** o **esce**. Toccare un altro
preset sostituisce il primo; **Rimuovi animazione** azzera il movimento; un'etichetta **Animata** indica che la
clip contiene fotogrammi chiave.

**Regola i fotogrammi chiave** serve per il controllo preciso. Accanto a ogni numero c'è un piccolo rombo. Metti la testina dove
vuoi che inizi la modifica e clicca il rombo: imposta lì un **fotogramma chiave** con il valore corrente.
Sposta la testina, cambia il numero: con i fotogrammi chiave attivi, il campo imposta un fotogramma chiave alla testina invece di
cambiare l'intera clip. Il pulsante che mostra il conteggio apre la **curva**: clicca un punto per selezionarlo, trascinalo
a sinistra o a destra per spostarlo nel tempo, scegli come si raccorda al punto successivo, oppure eliminalo. Tutto è
annullabile, un passaggio per ogni modifica.

## Segui un soggetto {#follow-a-subject}

Per tenere inquadrata una persona o un oggetto in movimento, seleziona una clip con immagine e apri **Segui soggetto** nell'Ispettore. Su telefono si trova nel foglio della clip.

1. Scegli come indicare il soggetto. **Disegna un riquadro** è gratis: trascina un riquadro attorno al soggetto sull'immagine, in un momento in cui si vede bene. (Il riquadro si disegna nell'editor più grande, non su telefono.) **Usa un oggetto trovato** elenca gli oggetti già trovati in questo video. Se l'elenco è vuoto, la sezione **Oggetti** può trovarne uno, e questo può costare crediti.
2. Aspetta **Soggetto trovato**. La riga mostra il tratto del video in cui il soggetto è stato trovato.
3. Imposta l'aspetto. **Inquadratura** (Larga, Media, Stretta) è quanta parte dell'immagine occupa il soggetto. **Fluidità** (Dolce, Normale, Reattiva) è la velocità con cui l'immagine si muove dietro al soggetto. **Quando il soggetto si perde** può essere **Mantieni** (tiene l'ultima inquadratura) o **Allarga** (mostra lentamente l'immagine intera). **Consenti uno zoom più morbido** permette all'immagine di ingrandirsi di più, fino a 4x, e può risultare meno nitida. Nell'editor lo zoom si ferma anche dove l'immagine uscirebbe dall'inquadratura, quindi può fermarsi prima: per un video largo in un'inquadratura verticale è circa 1,6x.
4. Premi **Segui**.

Ora l'immagine fa zoom e si sposta insieme al soggetto. Sotto il cofano sono normali keyframe di posizione e scala, quindi puoi rifinirli come in [Animare una clip](#animating-a-clip). Un solo Annulla toglie l'intero inseguimento, e **Rimuovi inseguimento** lo cancella. Dove il soggetto si è perso, la sezione elenca il tratto. Se lo zoom si è fermato prima per mantenere nitida l'immagine, lo dice.

Segui non funziona su una clip ritagliata, che usa un reframe, stabilizzata, un fermo immagine o con una velocità che cambia nel tempo. L'editor dice quale e cosa disattivare prima. Se cambi la clip dopo aver premuto Segui (la velocità, il ritaglio, la dimensione della timeline), l'editor ti chiede di **Rifare**.

## Velocità, inversione e fermo immagine {#speed-reverse-and-freeze-frame}

**Velocità** ha delle etichette (0,25x, 0,5x, 1x, 1,5x, 2x, 4x), un selettore per qualsiasi altro valore e una riga che dice
**La clip ora dura N s**, perché la durata della clip cambia di conseguenza e tutto ciò che viene dopo si sposta. Se la
nuova durata fosse più breve delle dissolvenze, le dissolvenze vengono accorciate e un messaggio lo dice. **Reimposta** riporta la
velocità a 1x. **Inverti** la riproduce al contrario (fino a un minuto di girato). **Fermo immagine** mantiene il
primo fotogramma della clip per tutta la sua durata, senza audio.

## Transizioni {#transitions}

Ogni taglio tra due clip sulla stessa traccia mostra un **+** rotondo (un bersaglio di 44 pixel su uno schermo touch).
Cliccalo per aprire il selettore delle transizioni: circa otto parole con etichetta (Dissolvenza incrociata, Dissolvenza al nero, Dissolvenza al bianco,
Tendina a sinistra, Tendina a destra, Scorrimento a sinistra, Scorrimento a destra, Apertura a cerchio), ciascuna con un'anteprima in ripetizione, **Altre** per il
resto e una durata (0,25, 0,5, 1 o 1,5 secondi). Una riga accanto al selettore dice quanto l'anteprima è vicina
all'esportazione: quasi ogni parola è **fedele**: il visualizzatore riproduce la stessa tendina, lo stesso scorrimento o la stessa rivelazione dell'esportazione. Quattro parole, Dissolvenza, Distanza, Grigi sfumati e Sfocatura orizzontale, sono **approssimative**: all'occhio sembrano uguali, con una piccola differenza nel dettaglio. **Aggiungi** posiziona la transizione centrata sul taglio. **Applica a tutti i tagli**
aggiunge la parola scelta a ogni taglio che può contenerla in un solo passaggio, e ti dice a quanti tagli mancava
girato di riserva. Una clip selezionata mostra le sue maniglie di rifilatura ai bordi al posto del +; lo stesso selettore è nella
sezione Transizione dell'Ispettore.

La clip dopo il taglio ha bisogno di un po' di girato prima del suo primo fotogramma da mostrare durante la sfumatura. Quando non ne ha,
il selettore lo dice e offre **Fai spazio**: rifila l'inizio della clip successiva (le clip dopo di essa
si spostano, e il suo audio collegato la segue) così la sfumatura ha girato a cui attingere. Se la clip è troppo corta per
darne, il selettore dice questo. Una transizione esistente mostra un piccolo glifo sul taglio: cliccalo per
cambiare la parola (il nome dell'elemento la segue) oppure **Rimuovi transizione** (torna a un taglio netto). Il + e il glifo sono per
schermi più grandi; su smartphone usa la sezione Transizione dell'Ispettore. Per un controllo preciso al fotogramma e i preset di transizione salvati,
apri **Avanzate** nell'Ispettore. Il render usa sempre un'interpolazione lineare, qualunque sia quella che scegli.

## Il mixer (spazio di lavoro Audio) {#the-mixer-audio-workspace}

Apri lo spazio di lavoro **Audio**. Ogni traccia audio ha un solo fader **Volume** dal silenzio (−∞) fino a +12 dB,
con una scala sotto. Trascinalo, oppure scrivi un numero nella casella dB (Invio per applicare; "-inf" per il silenzio).
Fai doppio clic sul fader, oppure premi il piccolo pulsante di reimpostazione accanto alla casella, per tornare a 0 dB. Sotto
ci sono il pan (doppio clic per centrare), muto e solo, un interruttore **Pulisci la voce**, un interruttore **Taglia il rombo grave**
e un indicatore di livello. I comandi per esperti (i numeri esatti dell'equalizzatore, bus, compressore, gate,
limiter, de-esser e registrazione dell'automazione) stanno dietro l'interruttore **Audio avanzato** in alto.

**Pulisci la voce** rimuove il rumore di fondo e uniforma il volume di quella traccia con un solo interruttore. Come
ogni elaborazione audio si sente completamente nel film generato, quindi premi **Ascolta attorno alla
testina**: l'editor genera circa dieci secondi attorno alla testina nello stesso modo del render finale
e li riproduce lì stesso. Il campione è un'anteprima usa e getta, non un file nel tuo progetto.

## Volume rispetto a un obiettivo {#loudness-against-a-target}

Sotto **Volume sonoro** scegli dove verrà ascoltato il film: **Social** (−14 LUFS), **Podcast** (−16) o
**Broadcast** (−23). **Misura il volume** riproduce una volta l'intero mix e ti dice di quanto si discosta
dall'obiettivo ("6,0 dB troppo basso per Social"); un risultato entro 1 dB conta come in linea con l'obiettivo. **Allinea
all'obiettivo** sposta il volume di ogni traccia audio non muta di quella differenza con un solo Annulla e misura di nuovo.
L'indicatore dal vivo sotto è solo un'anteprima. Se il tuo piano addebita crediti per la misurazione, ti viene
detto quanti prima che parta.

## Abbassare la musica sotto il parlato {#ducking-music-under-speech}

Nella sezione **Abbassamento** del mixer scegli la traccia musicale e le tracce voce (vengono scelte
per te quando le tracce hanno nomi come *Musica* e *Voce*), scegli di quanto scende la musica e premi
**Abbassa la musica sotto la voce**. L'etichetta dice **Abbassamento: attivo**, e il volume della traccia musicale ottiene punti
che scendono ovunque qualcuno parli. Finché l'abbassamento è attivo, i punti seguono le clip voce quando le sposti
o le rifili (ogni aggiornamento è un Annulla). **Aggiorna l'abbassamento** lo fa su richiesta, **Rimuovi l'abbassamento**
ripristina un volume piatto e lo disattiva. I punti sono normali fotogrammi chiave che puoi ancora modificare nella
corsia di automazione della traccia.

## Modificare dalla trascrizione (Testo) {#editing-from-the-transcript-text}

Se la timeline è partita da un'esecuzione analizzata con la trascrizione attiva, la scheda **Testo** nel pannello
sinistro mostra ciò che è stato detto, come paragrafi di parole. Clicca una parola per saltare lì; Maiusc+clic su un'altra per
selezionare il tratto tra le due; **Elimina parole** taglia esattamente quei fotogrammi dalla traccia e chiude
il vuoto, in un solo passaggio annullabile, come qualsiasi taglio. **Trova i riempitivi…** elenca ogni "ehm", "uhm" e balbettio che riesce a
sentire; deseleziona quelli che vuoi tenere e rimuovi gli altri in un colpo solo. Trascina un paragrafo su un altro
(o usa le sue frecce) per spostare quella parte della registrazione prima o dopo. Le parole mostrate sbiadite non sono
più sulla traccia; una parola contrassegnata da ≈ ha un tempo stimato, quindi un taglio su di essa cade dentro la sua frase
e non sul bordo della parola stessa.

## Sottotitoli {#subtitles}

La scheda **Sottotitoli** genera i sottotitoli dalla trascrizione: scegli quanti caratteri entrano in una riga e
quante righe può avere un sottotitolo, scegli la traccia su cui metterli e premi **Genera sottotitoli**. Per
impostazione predefinita i sottotitoli vanno sulla traccia dei sottotitoli già presente nella timeline; la prima volta, quando non ce n'è
nessuna, l'editor aggiunge una traccia "Sottotitoli". Generare di nuovo riempie solo i vuoti: un sottotitolo che si sovrapporrebbe
a uno già sulla traccia viene saltato, così non ottieni mai una seconda copia. Ogni sottotitolo è una normale clip: modifica il suo testo nell'elenco, disattivalo,
**dividilo** dove si trova la testina o **uniscilo** al successivo. I sottotitoli su una traccia
attiva vengono **incorporati** nel render; per avere invece un file di sottotitoli separato, disattiva la traccia dei
sottotitoli e scarica **SRT** o **WebVTT** dalla stessa scheda (contengono ciò che è salvato, quindi
salva prima).

## Lo spazio di lavoro Taglio {#the-cut-workspace}

Lo spazio di lavoro **Taglio** serve per assemblare in fretta. La striscia in alto mostra l'intera
timeline in un colpo solo, cliccala per saltare; la timeline di dettaglio la segue. Sotto, il **nastro delle origini** elenca i
media del progetto: **Inserisci** mette un'origine al taglio più vicino alla testina e sposta ciò che
segue. **Revisione** riproduce ogni clip a una velocità proporzionata alla sua durata, così un'inquadratura lunga non richiede
molto tempo per essere controllata. **Inquadrature noiose** elenca le inquadrature che l'analisi ha misurato come lunghe e ferme, **Mostra**
ne seleziona una, **Rimuovi** la taglia e chiude il vuoto. **Guarda** su una riga del nastro apre la clip nel
visualizzatore come sorgente, dove la segni e la posizioni con `F9`, `F10`, `F8` o `Shift+F8`; la sezione
[Da Taglio a Montaggio](pages-and-panels.md#from-cut-to-edit) la descrive.

## Take alternativi {#alternate-takes}

Dove l'analisi ha trovato inquadrature quasi identiche, la scheda **Take** le impila: per ogni clip con
alternative mostra il take che stai usando e gli altri, segnando quello che l'analisi ha valutato migliore
e segnando come **corto** un take più breve della clip. **Usa questo take** sostituisce il girato
mantenendo il posto e la durata della clip. Rifiuta un take più corto della clip, perché la clip
continuerebbe nel girato dopo quel take, e ti dice quanto dura il take e quanto serve alla clip;
accorcia prima la clip o scegli un take più lungo.

## Confrontare due timeline {#comparing-two-timelines}

La scheda **Confronta** nel pannello destro elenca le differenze tra un'altra timeline del progetto
(un duplicato, un montaggio precedente) e quella che stai modificando: clip aggiunte, rimosse, spostate, rifilate o
cambiate, più le modifiche a tracce e impostazioni. Clicca un tempo per saltare lì.

## Esportare, importare, fotogrammi singoli e clip singole {#exporting-importing-stills-and-single-clips}

In **Consegna**, i link **Esporta la timeline** scaricano la timeline salvata come **OpenTimelineIO**,
un **EDL CMX3600** oppure sottotitoli **SRT**/**WebVTT** (una timeline senza sottotitoli lo dice invece di offrire un file vuoto). OpenTimelineIO mantiene tutto, quindi **Importa OTIO**
nella scheda **Video** riporta una timeline intera, oppure importa i tagli di un file creato da un altro editor.
Quando i media di alcune clip non sono in questo progetto la timeline si apre comunque: ogni clip mancante è un segno rosso
**Offline: nome** dove si trovava, un messaggio le elenca, e **Ricollega** ti permette di scegliere un file del progetto
da mettere in quel punto (un passaggio annullabile). Sotto i link di esportazione, **Cosa c'è nel file?** dice cosa porta e cosa lascia
fuori ciascun formato, a quali media punta, e una riga **Consegna** che conta ciò che sarà nel file.
**Fotogramma** sopra il visualizzatore salva il fotogramma corrente come PNG alla qualità dell'anteprima, utile come riferimento,
non come fotogramma finito. Con una clip selezionata, **Esporta solo "…"** in Consegna imposta i punti di entrata e di uscita
su quella clip e attiva **Solo l'intervallo in/out segnato**, così il render successivo copre solo quella.

## Parole chiave, etichette e raccoglitori intelligenti {#keywords-labels-and-smart-bins}

Nello spazio di lavoro **Media**, selezionare una clip mostra **Note e parole chiave**: aggiungi parole chiave, una nota, un'etichetta
colorata, e contrassegnala come preferita o segnalata, poi **Salva**. Nell'albero dei raccoglitori, **Nuovo raccoglitore intelligente…** (vedi sotto) crea un raccoglitore che mostra sempre ogni origine che corrisponde a un filtro: parole chiave, tipo, etichetta,
preferita, segnalata, parte del nome, invece di ciò che ci trascini dentro. Etichetta le clip e i raccoglitori intelligenti
si riempiono da soli.

Per mettere una clip in un raccoglitore normale, trascina il suo riquadro sul raccoglitore, oppure apri il menu **⋯** sul riquadro e
scegli **Aggiungi al raccoglitore** (i raccoglitori intelligenti non vengono mai offerti: si riempiono da soli).

### Cercare, mettere la stella e ordinare i media {#finding-starring-and-sorting-media}

Il pool mostra un'immagine per ogni clip (un video mostra il suo primo fotogramma), la sua durata e dimensione quando note, e una riga discreta, **Non ancora analizzato**,
quando nessuno l'ha misurata. La **stella** e la **bandierina** su un riquadro si salvano nel momento in cui le tocchi; un puntino colorato mostra l'etichetta colore della clip e piccole
etichette indicano i raccoglitori in cui si trova. **Cerca** confronta nomi di file e parole chiave, **Ordina** ordina per data di aggiunta, più recente, nome, durata o dimensione, e **Filtra** restringe
per tipo (video, audio, immagini) oppure per Con stella, Segnalati o Non ancora analizzati. **Azzera i filtri** riporta tutto.

### Raccoglitori senza trascinare {#bins-without-dragging}

Ogni raccoglitore ha un menu **⋯**: **Rinomina**, **Nuovo sotto-raccoglitore** (il prossimo raccoglitore che aggiungi va al suo interno), **Sposta in** un altro raccoglitore o al livello principale (desktop), **Modifica regola**
(raccoglitori intelligenti) ed **Elimina raccoglitore**, che chiede prima conferma. La riga di una clip dentro un raccoglitore ha il proprio menu **⋯ > Sposta in**.

### Raccoglitori intelligenti {#smart-bins}

**Nuovo raccoglitore intelligente…** apre un breve modulo con parole semplici: i tipi di media, con stella o segnalati, un'etichetta colore, parole chiave e parte del nome. Una riga
sotto dice cosa significa la regola e quante origini corrispondono in questo momento ("Video, con stella: 3 origini corrispondono ora"). Una regola senza nulla di scelto viene rifiutata,
perché ripeterebbe soltanto Tutti i media. Scegli **Modifica regola** nel menu di un raccoglitore intelligente per cambiarla in seguito; l'albero mostra il numero di corrispondenze di ogni raccoglitore intelligente.

### Mettere i media sulla timeline {#putting-media-on-the-timeline}

Trascina un riquadro su una traccia della timeline: una linea blu mostra esattamente dove finirà, e una traccia su cui la clip non può andare (un audio su una traccia video) mostra
una linea rossa e rifiuta il rilascio. La clip copre ciò che c'è sotto, nient'altro si sposta, l'intervallo **Entrata/Uscita** marcato nel visualizzatore dell'origine viene usato quando ne hai
impostato uno, e un messaggio dice dove è finita, con **Annulla**. Senza mouse, usa il menu **⋯ > Mettila sulla timeline** del riquadro, oppure i pulsanti accanto al
visualizzatore dell'origine. Lo spazio di lavoro **Montaggio** si apre con il pool dei media a sinistra su un computer o un tablet, così un riquadro e la timeline sono sullo schermo insieme.

### Il visualizzatore dell'origine {#the-source-viewer}

C'è un solo lettore: usa la copia più leggera che è pronta (etichettata **Anteprima semplice**) e altrimenti l'originale, senza nessun interruttore a cui pensare.

**Carica** (in cima allo spazio di lavoro Media, e nel suo stato vuoto) aggiunge video, audio o immagini dal tuo dispositivo; puoi anche trascinare i file
nell'area Media. Ogni file mostra il suo avanzamento, e uno che fallisce dice perché e offre **Riprova**. Quando finisce compare nel
pool. Su smartphone, **Carica** apre il selettore di file. Eliminare un raccoglitore chiede prima conferma, indicando il raccoglitore e quanti sotto-raccoglitori e clip contiene:
le clip restano nel tuo progetto, se ne va solo il raggruppamento.

## Correggere il colore di una clip (pagina Colore) {#grading-a-clip-colour-workspace}

**Colore è la pagina della correzione.** Con una clip selezionata, la pila di nodi e il bersaglio sono a
sinistra, l'immagine con gli scope sotto è al centro, e gli strumenti di correzione sono a destra. Gli
strumenti sono schede: **Primarie**, **Curve**, **LUT**, **Secondarie**, **Gruppi** e **Galleria**.
Tutto è visibile quando la pagina si apre; non c'è nessun interruttore in più da attivare.

- **Galleria** mostra il tuo fotogramma corrente con ogni look applicato: Caldo, Freddo, Cinematografico,
  Vivace, Smorzato, Mono e Teal & orange. Cliccane uno per correggere la clip con quello; **Intensità**
  (da 0 a 150 percento, 100 è il look così com'è) lo rende più sottile o più forte, e **Originale** lo
  toglie. Sotto **Correggi**, cinque semplici cursori (Luminosità, Contrasto, Saturazione, Calore, Tinta)
  rifiniscono l'immagine.
- **Primarie** ti dà lift, gamma, gain e offset per rosso, verde, blu e tutti insieme, più esposizione,
  contrasto (attorno a un perno), saturazione, tonalità, temperatura e tinta. **Aggiungi correzione**
  aggiunge una correzione in fondo alla pila di nodi.
- **Curve** ti permette di cliccare punti su una curva tonale per la luma o per un canale.
- **LUT** applica un file `.cube` del tuo progetto con l'intensità che scegli.
- **Secondarie** contiene il selettore e la finestra di potenza. **Modifica la finestra sull'immagine**
  mette le maniglie della finestra sull'immagine; selezionare un nodo da solo non lo fa.
- **Gruppi** contiene i gruppi di colore descritti sotto.

**Mostra l'originale (prima)** alterna il visualizzatore tra prima e dopo senza cambiare la clip;
**Dividi prima / dopo** disegna l'originale a sinistra del visualizzatore con un cursore per la linea di
divisione. **Applica a tutte le clip** dà a ogni clip lo stesso look e le stesse correzioni in un solo
passaggio (un Annulla riporta tutto indietro); con più clip selezionate puoi applicarlo solo a quelle,
oppure alle clip dello stesso file. **Copia correzione** e **Incolla sulle selezionate** spostano
un'intera correzione, e **Abbina questa clip a…** sceglie un'altra clip e avvicina questa al suo colore
nella misura che scegli (un abbinamento di primo ordine: rifinisci o rimuovi in seguito la correzione di
abbinamento). **Livelli di regolazione** elenca i livelli che correggono tutto ciò che sta sotto per un
intervallo; aggiungine uno sopra la clip selezionata, selezionalo e dagli un look come a qualsiasi clip.

Ogni correzione ha il proprio interruttore di attivazione, e l'interruttore in alto esclude l'intera
correzione. **Aggiungi look** trasforma le parole che usa un agente Colorist, Caldo, Freddo,
Cinematografico, Vivace, Smorzato, Mono, con un'intensità, con ombre sollevate o accentuate e luci
attenuate o schiarite, in una normale correzione che puoi tenere, ritoccare o eliminare. **Correzione
sì/no** sopra il visualizzatore confronta prima e dopo solo nell'anteprima: i render e i fotogrammi
singoli usano sempre la correzione così com'è impostata. Curve, LUT e secondarie si vedono
nell'anteprima come nel video finito. Finché un file LUT si sta ancora caricando, l'etichetta dice che l'anteprima è approssimativa.

## Gruppi di colore {#colour-groups}

Le clip corrette insieme appartengono a un **gruppo di colore**: selezionale e premi **Raggruppa le clip selezionate**,
oppure **Gruppi dai gruppi di look** per creare un gruppo per ogni insieme di inquadrature dall'aspetto simile che l'analisi
ha trovato. La correzione di un gruppo viene applicata per prima, poi quella di ogni clip. Scegli il gruppo
nel menu dei bersagli della pagina Colore (o nella scheda **Gruppi**) per correggerlo; rinominalo,
aggiungi le clip selezionate o rimuovi membri nell'elenco.

## Fotogrammi fissi, tendina, scope e abbinamento delle inquadrature {#stills-wipe-scopes-and-shot-match}

**Cattura fotogramma** conserva un'immagine del fotogramma corrente nella galleria; cliccane uno per renderlo il
riferimento e trascina **Tendina** per confrontarlo con l'immagine dal vivo nel visualizzatore. Gli **scope**
(forma d'onda, parade, vettorscopio, istogramma) sono sempre agganciati sotto l'immagine nella pagina
Colore, con un comando per richiuderli, e leggono l'anteprima composta, correzione compresa. Poiché ci
sono sempre, **Abbina al riferimento** funziona da qualsiasi scheda: aggiunge una correzione che avvicina
luminosità, bilanciamento e contrasto della clip corrente al fotogramma di riferimento, un punto di
partenza da rifinire, non una garanzia.

## Maschere, chiavi, pin ed effetti (pagina Composizione) {#masks-keys-pins-and-effects-compose-workspace}

Non devi per forza partire da Composizione. Seleziona una clip e apri il suo menu, oppure la sezione
**Maschera e riquadratura** dell'Ispettore, e scegli **Aggiungi maschera** (rettangolo o ellisse) o
**Aggiungi riquadratura**: l'editor seleziona la clip, mette in pausa la riproduzione e porta le maniglie
sull'immagine, senza cambiare pagina. Aggiungere una maschera o una riquadratura è un solo passaggio
annullabile.

Apri **Composizione** con una clip selezionata per la vista più completa. A sinistra c'è l'elenco di ciò
che c'è sulla clip, a destra le proprietà della voce scelta. Una clip video elenca Maschera,
Reinquadratura, Keyer, Corner pin, Effetti, Movimento e Vista a nodi; una clip di regolazione elenca Correzione,
Effetti e Vista a nodi; un inserto elenca Matte e Angoli. Premi `[` e `]` per passare alla
voce precedente o successiva. I tasti freccia selezionano una voce e ne mostrano lo strumento mentre il focus resta sull'elenco; clicca o premi `Invio` per portare il focus sull'immagine. Le scorciatoie dell'editor funzionano mentre una voce ha il focus; l'elenco mantiene frecce, `Home`, `End` e `Invio`. Una **maschera di forma** mostra
la clip solo dentro (o fuori) un rettangolo o un'ellisse sfumati; una **chiave cromatica** rende
trasparente un colore, con rimozione dello spill; un **corner pin** posiziona l'intera immagine su
quattro angoli; gli **effetti** (sfocatura, nitidezza, vignettatura, grana, pixelizzazione, mono) si
impilano in ordine con una quantità ciascuno; **Stabilizza** rende più ferme le riprese traballanti nel
render; **Riquadra** ritaglia secondo le proporzioni della timeline attorno a un punto del soggetto;
**Rallentatore** può interpolare i fotogrammi per un rallentatore più fluido. L'Ispettore e
Composizione mostrano gli stessi campi della maschera e della riquadratura, quindi una modifica in uno è
una modifica anche nell'altro.

Per disegnare una maschera sull'immagine, seleziona una clip video, porta la testina su di essa e premi
`Maiusc+M` (oppure scegli **Disegna sull'immagine** in Composizione, il primo pulsante della voce maschera; diventa **Ridisegna** quando esiste una maschera, e **Aggiungi maschera** e **Aggiungi maschera ellittica** restano). Trascina sull'immagine per
disegnare il riquadro; tieni premuto `Maiusc` per un quadrato e `Alt` per disegnare dal centro. Durante un trascinamento, un `Esc` annulla solo il trascinamento; il successivo esce dallo strumento; quello dopo deseleziona. `Invio` aggiunge la maschera predefinita senza trascinare. La barra degli
strumenti sta in una sola riga sopra l'immagine e ha la forma della maschera, **Sfumatura maschera**, **Inverti**, **Ridisegna**, **Rimuovi maschera**,
**Aggiungi maschera** e **Fatto**. `Maiusc+S` passa da rettangolo a ellisse, `Maiusc+I` inverte e
`Alt+Backspace` rimuove la voce selezionata.

Per posizionare un corner pin, premi `Maiusc+P`. Le maniglie del pin compaiono solo mentre lo strumento pin è attivo. Senza un pin lo strumento lo dice e offre **Aggiungi corner pin**, che avvia anche lo strumento. Quattro maniglie indicano dove
arrivano gli angoli; trascinane una, oppure usa i tasti freccia per spostare la maniglia attiva dell'1%
(`Maiusc` per il 10%). Su una clip scalata, spostata, ruotata o che non riempie l'inquadratura, una nota dice che le maniglie segnano gli angoli dell'inquadratura, non i bordi dell'immagine. `Maiusc+F` apre lo strumento di reinquadratura; senza un punto offre **Aggiungi reinquadratura**, e entrare nello strumento non scrive nulla. Gli strumenti maschera, reinquadratura e pin terminano quando cambi pagina. Premere la scorciatoia di uno
strumento in un'altra pagina con una clip multimediale selezionata apre Composizione e avvia lo strumento; **Aggiungi maschera** dall'Ispettore di Modifica o dal menu della clip avvia lo strumento senza cambiare pagina. Una scorciatoia che imposti tu ha la precedenza su una predefinita. Un inserto non ha
maniglie sull'immagine: i suoi angoli restano campi numerici in Composizione.

Con lo strumento maschera o Riquadra attivo, la forma è sull'immagine: trascina
la forma gialla (trascina dentro per spostarla, le maniglie quadrate per ridimensionarla, la maniglia
rotonda per ammorbidire il bordo) o la croce verde per Riquadra, che mantiene un punto fisso. **Inverti
maschera** sta sopra l'immagine, i tasti freccia spostano dell'1% (Maiusc per il 10%), e un solo annulla
annulla un intero trascinamento; i cursori restano per la rifinitura. La finestra di potenza nella scheda
**Secondarie** della pagina Colore si può spostare allo stesso modo (viola) quando è attivo **Modifica la
finestra sull'immagine**, a meno che non segua l'immagine: in quel caso disattiva il tracciamento per
spostarla a mano. Il visualizzatore li mostra sull'immagine, e l'etichetta sopra il visualizzatore dice quando si avvicina soltanto al video finito. Per vedere il risultato reale prima di consegnare, premi **Anteprima 3 s** nell'intestazione Proprietà: mostra
prima il prezzo (o "incluso"), poi genera tre secondi attorno alla testina e li riproduce sopra il
visualizzatore. **Chiudi anteprima** o **Torna alla modifica** ti riporta alla timeline, che non è
cambiata. Per un **inserto**, Composizione modifica la sua matte e gli **Angoli** del suo quadrilatero
tracciato alla testina (**Keyframe degli angoli (n)** li conta). Con nulla di selezionato, **Aggiungi clip di regolazione** (`Ctrl/Cmd+Alt+A`) mette una clip su una
nuova traccia in cima la cui correzione e i cui effetti si applicano a tutto ciò che sta sotto per la sua
durata.

## Sfoca o maschera un oggetto {#blur-or-mask-an-object}

Per sfocare una targa o un volto, o evidenziare un prodotto, in una clip, vedi [Sfoca o maschera un oggetto](blur-or-mask-an-object.md).

## Migliora una clip {#enhance-a-clip}

Per rendere un video più nitido, più fluido al rallentatore, più pulito o più naturale nel movimento, vedi [Migliora una clip](enhance-a-clip.md).

## Modelli di titolo {#title-templates}

Seleziona un titolo: il **Modello** dell'ispettore offre un terzo inferiore, un titolo riquadrato, uno con contorno
e una scheda, oltre al semplice titolo centrato. Il visualizzatore disegna posizione, riquadro e
contorno del modello; il testo stesso è disegnato dal browser, quindi la sua dimensione esatta può differire leggermente dal render.

## Annidare le clip (clip composte) {#nesting-clips-compound-clips}

Seleziona alcune clip e apri la scheda **Struttura**: **Crea clip composta** le trasforma in un'unica clip che
si comporta come qualsiasi altra: spostala, rifilala, tagliala. **Apri** mostra cosa c'è dentro così puoi modificarlo
(tagliare, spostare, rifilare, eliminare, annullare); **Salva nella clip composta** rimette a posto le tue modifiche, e un solo Annulla all'esterno
riporta indietro l'intera sessione. **Scomponi** rimette le clip sulla timeline com'erano.

## Più telecamere dello stesso momento (multicam) {#several-cameras-of-one-moment-multicam}

Seleziona due o più clip registrate nello stesso momento (la prima è il riferimento) e premi
**Sincronizza dalla forma d'onda**: l'editor ascolta l'audio di ogni clip e le allinea. Ogni camera mostra
il suo scostamento in secondi e un'etichetta di affidabilità; un'etichetta di *bassa affidabilità* significa che l'audio non corrispondeva
chiaramente. Rifinisci uno scostamento con **−1 fotogramma** e **+1 fotogramma** oppure scrivendolo. Sotto **Audio della
multicam** scegli quale registrazione fornisce l'audio: la prima camera (programma), qualsiasi clip selezionata
(la clip di un registratore separato è una di queste), o nessun audio. **Crea multicam** le sostituisce con un'unica
clip; cambia l'audio in seguito dal pannello della multicam. Riproducila e clicca il pulsante di una camera per tagliare
su di essa in quel momento; fermati e clicca un pulsante per cambiare la camera della sezione sotto la
testina. Ogni taglio è un Annulla.

## Bus, compressione e pulizia (spazio di lavoro Audio) {#buses-compression-and-clean-up-audio-workspace}

Sotto il mixer, **Bus ed elaborazione** ti permette di raggruppare le tracce: aggiungi un bus (per esempio *Dialoghi*), instrada
le tracce su di esso, e instrada i bus tra loro o verso il master. Ogni traccia e bus può avere un
compressore, un limiter, un noise gate, un de-esser, una riduzione del rumore e un leveler che uniforma il parlato. Questi
si applicano al video generato. Bus, compressore, limiter, noise gate e de-esser si sentono anche nell'anteprima, in modo approssimativo (ognuno mostra "Si sente nell'anteprima, in modo approssimativo" oppure "Si sente solo dopo il render"); la riduzione del rumore e il leveler si sentono solo dopo il render.

**Automazione**: imposta una traccia su **Latch**, riproduci e muovi il suo fader; quando ti fermi, i tuoi movimenti vengono
scritti come curva del volume della traccia (un Annulla). **Read** riproduce la curva sul fader.

**Canali**: nell'ispettore, *Canali audio* sistema una clip registrata su un solo lato, la riduce a
mono o scambia sinistra e destra. Senti subito la modifica nell'anteprima, e il render applica la stessa cosa.

## Registrare una voce narrante {#recording-a-voiceover}

Nello spazio di lavoro Audio scegli il microfono (se ne hai più di uno) e premi **Registra alla
testina**. Parte un conto alla rovescia 3-2-1 mentre l'indicatore di livello mostra ciò che il microfono sente; **Ferma e
posiziona** salva il take nei file del progetto e lo mette su una nuova traccia *Voce narrante* dove si trovava la
testina. **Rifai** registra di nuovo e sostituisce quella traccia con un solo Annulla. Se il browser blocca il
microfono, non ne ha o non può registrare, il pannello lo dice con parole semplici e **Carica una registrazione**
mette invece sulla timeline un file che hai già.

## Chiedere suggerimenti all'AI (Assistente) {#asking-the-ai-for-suggestions-assist}

Su smartphone, **⋯ poi Assistente** apre un foglio sopra la striscia con gli stessi aiutanti. Il prezzo e il limite orario
vengono mostrati prima di chiedere. Ogni modifica suggerita è una riga: toccala per saltare a quel momento e vedere sulla striscia solo il suo
contorno. Una modifica a una clip che hai creato tu ha una casella da spuntare che devi spuntare prima. **Accetta**
e **Rifiuta** restano in fondo; dopo Accetta una barra **Applicato. Annulla** annulla l'intero suggerimento in un solo passaggio.

Apri la scheda **Assistente**, scegli un **aiutante**: *Ri-taglio*, *Grafiche*, *Correzione colore*, *Effetti sonori* o
*Revisione* (il compito di ciascuno è scritto sotto il selettore), se vuoi segna entrata e uscita per limitarlo a
una parte della timeline, scrivi ciò che vuoi nell'unica casella di richiesta e premi **Chiedi suggerimenti**.

Prima di chiedere, il pannello ti dice quanto costerà ("Chiedere costa circa 2,5 crediti"; non viene
mostrato nulla quando la fatturazione è disattivata, e dice *Incluso* quando il tuo spazio di lavoro usa un proprio provider AI),
quanti suggerimenti ti restano in quest'ora ("12 suggerimenti su 20 ancora disponibili in quest'ora") ed esattamente
cosa viene inviato all'AI: conteggi di clip e secondi, il tuo brief, mai video, audio o nomi di file.
Guardare questo non costa nulla. Se hai finito i crediti il pulsante è disattivato e dice quanti
te ne servono; se hai esaurito i suggerimenti dell'ora dice quanto aspettare. Se l'AI dà una risposta
inutilizzabile, il pannello lo dice e indica quanti crediti ha usato quel tentativo.

La risposta è un elenco di modifiche, ciascuna con il suo motivo, e la timeline mostra cosa cambierebbe:
contorni verdi per le aggiunte, tratteggio rosso per le rimozioni, arancione per le modifiche. Premi **Accetta** su una
modifica per applicare solo quella, **Salta** per scartarla, oppure spunta più modifiche e premi **Accetta le modifiche
selezionate**: si applicano in un solo passaggio (un Annulla) e le altre restano elencate. Una modifica a una clip che hai creato **tu**
è contrassegnata, e devi prima spuntare *Consenti di modificare la mia clip*. Se non è configurato nessun provider AI,
il pannello lo dice; un amministratore ne aggiunge uno in Amministrazione → Provider di inferenza.

**Tagliare in base a ciò che si dice.** *Rimontaggio* può tagliare in base al contenuto: chiedigli di "togliere tutte
le parti parlate", "eliminare i silenzi", "togliere gli ehm" o "tenere solo la parte sui prezzi", e taglia
esattamente quei tratti dalla clip e chiude i vuoti, usando la trascrizione e l'analisi dei silenzi del tuo girato.
Fa ciò che hai chiesto e nient'altro: se chiedi di tagliare, non disattiva mai una clip al suo posto, e la risposta
inizia con una riga che dice cosa ha fatto ("Tagliati 7 passaggi parlati (12,4 s)"). Se il girato non è ancora stato
analizzato per ciò che la richiesta richiede, il pannello lo dice prima di addebitare qualsiasi cosa e propone
**Analizza prima**, con il suo prezzo (la trascrizione costa crediti; trovare i silenzi è gratuito). Accettare i tagli
è un solo passaggio, quindi un solo Annulla li ripristina tutti.

**Momenti nell'immagine.** Quando hai trovato gli eventi di un video (vedi [Corsia degli eventi](events-lane.md)),
anche gli aiutanti li conoscono: dove qualcosa compare, atterra, lampeggia o cambia, e dove la camera inizia a muoversi.
*Effetti sonori* può allora mettere un suono esattamente su uno di questi momenti, *Grafiche* può farvi partire un titolo
e *Revisione* può metterci la sua nota. Agli aiutanti vengono indicati solo i momenti più forti, in poche parole semplici;
i momenti di normale ripresa filmata e le parole dei sottotitoli restano fuori. Un suono dura fino a due secondi, quindi
di due suoni che si sovrapporrebbero viene suggerito solo il primo. Trovare gli eventi è gratuito, e chiedere a un
aiutante non lo avvia mai al posto tuo: se un video non ha ancora eventi, l'aiutante lavora semplicemente senza.

## Lavorare offline {#working-offline}

Se la connessione cade, continua a modificare: la barra di stato dice **Offline** e le tue modifiche vengono conservate su
questo dispositivo (anche se chiudi la scheda). Quando la connessione torna vengono salvate. Se nel frattempo qualcun altro ha
cambiato la stessa parte della timeline, l'editor non tira a indovinare: conserva le tue
modifiche, mostra **Scarica le mie modifiche offline** e ti chiede di ricaricare.

## Scorciatoie e layout {#shortcuts-and-layouts}

Premi `?` (oppure scegli l'icona della tastiera in alto a destra) per aprire l'elenco **Scorciatoie da
tastiera**: raggruppa ogni azione sotto Trasporto, Navigazione, Marcatori, Strumenti, Modifica e Pagine,
e **Cerca** lo restringe. L'elenco serve per leggere; **Modifica le scorciatoie** apre l'editor. Lì
clicca **Cambia** e premi i tasti che vuoi. Due azioni sullo stesso tasto vengono segnalate e vanno
risolte prima di salvare. **Trova una scorciatoia** cerca per azione o per tasto, **Esporta JSON** /
**Importa JSON** conservano le tue scorciatoie in un file, e **Ripristina le impostazioni precedenti**
rimette ciò che c'era prima del tuo ultimo salvataggio (conservato su questo dispositivo). Se salvi da
due dispositivi, un salvataggio mantiene le modifiche dell'altro dispositivo. I menu e i suggerimenti
mostrano i tasti della tua configurazione attuale, quindi una modifica compare ovunque nello stesso
momento. `Ctrl/Cmd+K` apre la **tavolozza dei comandi**, dove puoi cercare ogni azione, vederne il tasto
ed eseguirla. Vedi [Scorciatoie da tastiera](shortcuts.md).

Ogni pagina ricorda le dimensioni dei propri pannelli su questo dispositivo, come frazioni della
finestra, così si adattano a uno schermo più piccolo. **Layout** salva layout con un nome (le schede
aperte e le loro dimensioni) per la pagina in cui sei, e il cestino accanto a un layout salvato lo
elimina. **Ripristina questa pagina** riporta la pagina su cui sei alle dimensioni predefinite;
**Ripristina tutte le pagine** le riporta tutte indietro e chiede prima conferma. Tutto ti segue in ogni
progetto.

## Impostazioni di visualizzazione {#display-settings}

Il menu **Impostazioni di visualizzazione** nell'intestazione dell'editor imposta quanto sono spaziosi i comandi: **Comodo** o
**Compatto**. Uno schermo touch ottiene sempre le dimensioni touch più grandi. **Sempre scuro** mantiene l'editor scuro
mentre il resto dell'app è chiaro; scegli **Come l'app** (o chiudi l'editor) per tornare alla tua
impostazione. Su smartphone, **Editor sempre scuro** è nel menu **Altro** dell'intestazione. Entrambe le scelte sono conservate per
ogni persona su questo dispositivo.

## Preset {#presets}

Salva le impostazioni che riutilizzi come preset del progetto: una transizione (nell'ispettore, tra due clip),
una pila di effetti (Composizione) o un obiettivo di volume (Consegna; l'editor offre anche Social −14 LUFS,
Podcast −16 LUFS e Broadcast −23 LUFS). Scegli un preset per applicarlo; eliminare un preset non cambia
nulla di ciò a cui l'hai già applicato. **Gestisci** accanto al selettore mostra cosa fa ogni preset e
ti permette di applicarlo, rinominarlo, duplicarlo o eliminarlo (l'eliminazione chiede prima conferma).

## Correggere un colore o un'area (pagina Colore) {#correcting-one-colour-or-one-area-colour-workspace}

Nella scheda **Secondarie**, **Selettore HSL** limita una correzione a un intervallo di colori (tonalità,
saturazione, luminosità), e **Power window** la limita a un'ellisse o a un rettangolo. **Segui nella
clip** segue ciò che sta dentro la finestra mentre si muove. Questi si vedono nel video generato;
l'anteprima mostra la clip senza.

## Revisione e commenti {#review-and-comments}

La scheda **Revisione** contiene commenti con timecode: scrivi, premi **Commenta** alla testina (o sopra l'intervallo
marcato), rispondi e risolvi. Ogni scheda di commento ha pulsanti per **saltare al suo tempo**,
**modificare** il testo, **segnarlo come risolto** (o riaprirlo) ed **eliminarlo**, nella scheda dell'editor e nella
pagina di revisione. **Link di revisione** apre un'unica pagina che funziona su smartphone e su computer: il
render finito più recente dell'intera timeline, con la sua immagine. Su un computer il video sta accanto ai
commenti; su smartphone si impilano. I marcatori su una barra sotto il video mostrano dove sono i commenti, e un'etichetta
sopra la casella dei commenti mostra il tempo a cui verrà agganciato un nuovo commento (il fotogramma su cui il video è in pausa). Se hai
modificato la timeline dopo che quel render è stato creato, un banner dice **Esistono modifiche più recenti** e rimanda all'
editor; il selettore **Render** ti permette di guardare un render più vecchio. **Copia link di revisione** (e **Condividi** dove lo
smartphone lo offre) dà il link; la pagina dice "Chiunque in questo spazio di lavoro con accesso può aprirlo".
Per permettere a chi non ha un account di guardare e commentare, vedi [Condividi un film per la revisione](share-for-review.md). Su smartphone puoi rispondere a un commento, risolverlo ed eliminarlo.

## Final Cut Pro {#final-cut-pro}

**Consegna → Esporta la timeline → XML per Final Cut Pro** scarica il montaggio (clip, vuoti, transizioni,
marcatori, tracce aggiuntive come clip collegate) per Final Cut Pro. Titoli, correzioni ed effetti non sono
inclusi.

## Cosa non fa l'editor {#what-the-editor-does-not-do}

- Non automatizza il volume di un bus.
- L'anteprima non riproduce compressione o riduzione del rumore; il render le applica entrambe.
- Non ha rampe di velocità. Le modalità di fusione ci sono; vedi [Sovrapponi un video a un altro](./layer-videos.md).

### Modificare sulla timeline {#editing-on-the-timeline}

**Cosa è cambiato:** `Delete` lasciava un vuoto, come solleva. Ora chiude il vuoto, e `Shift+Delete` solleva la clip e lascia il vuoto.

- **Elimina** chiude il vuoto; **Shift+Delete** (o **Solleva** nel menu **Clip**) lascia un vuoto. Clicca uno spazio vuoto tra due clip per selezionare quel vuoto, poi premi
  Delete (o **Chiudi il vuoto selezionato**) per chiuderlo. **Chiudi tutti i vuoti** chiude ogni vuoto sulla traccia della clip selezionata.
- **Clic destro su una clip** (o tocca **Clip** su uno schermo touch) per l'elenco completo: dividi, elimina, solleva, copia, taglia, incolla alla testina, duplica, adatta per riempire, sostituisci
  dal pool dei media, unisci, sposta sulla traccia sopra o sotto, velocità, disattiva, seleziona in avanti o all'indietro, proprietà. Fai doppio clic su una clip per aprirla nell'ispettore.
- **Copia / Incolla** (Ctrl o Cmd con C, X, V; Ctrl/Cmd+D duplica) mantiene la distanza tra più clip e funziona tra tracce e timeline.
- I pulsanti **-10 / -1 / +1 / +10** spostano la clip selezionata o la testina di fotogrammi.
- **Rifila**: scegli ripple, roll, slip o slide nel menu **Rifila** (tasti R, T, Y, U). Mentre trascini un bordo, un'etichetta mostra i fotogrammi. Nell'ispettore, scrivi
  **Entrata**, **Uscita** o **Durata** della clip in fotogrammi o timecode e premi Invio; se non si può fare, una frase dice perché.


### Con le dita {#with-your-fingers}

- **Tocca** una clip per selezionarla. **Trascina** con un dito per far scorrere la timeline di lato. **Pizzica** con due dita per fare lo zoom, e muovi le due dita insieme per scorrere.
- **Tocca due volte** una clip per ingrandirla.
- **Tieni premuta** una clip per mezzo secondo per aprire le sue azioni (sposta, dividi, elimina). Ogni azione è anche nel menu **Clip**.
- Trascina le maniglie gialle sulla clip selezionata per rifilarla. Le maniglie sono abbastanza grandi per un dito.
- Spostare una clip trascinandola con un dito è disattivato per impostazione predefinita. Un'impostazione del dispositivo lo attiva; allora tenere premuto solleva la clip.
- I tocchi nei primi millimetri del bordo dello schermo vengono ignorati, così il gesto indietro del browser continua a funzionare.
- In **Media**, premi una clip e trascina di lato per scorrerla; un'etichetta mostra dove sei. Trascina in su o in giù per scorrere l'elenco come al solito.

## Analizza il tuo girato, poi taglia in base a ciò che contiene {#analyse-your-footage-then-cut-by-what-it-contains}

Apri la scheda **Analizza** e premi **Analizza** su un file per trovare i suoi tratti silenziosi, le inquadrature e il movimento. **Trascrivi il parlato** mostra prima quanto costerà e parte solo quando confermi. Poi:

- **Rimuovi i silenzi** ed **Elimina parole** tagliano l'immagine e il suo audio collegato insieme. **Riproduci il risultato** mostra il taglio nel visualizzatore prima che venga salvato qualsiasi cosa; **Applica** lo mantiene, **Annulla** lo scarta.
- Le parole contrassegnate da una tilde hanno solo un tempo approssimativo; ti viene chiesto conferma prima di tagliarle.
- **Sottotitoli** crea i sottotitoli dalla trascrizione, ti permette di scegliere un aspetto e una posizione, aggiungere un sottotitolo a mano (due righe vanno bene), ritoccare i tempi, oppure **Importa SRT o WebVTT** (prima l'anteprima, poi aggiungi o sostituisci).
- In **Consegna**, scegli se incorporare i sottotitoli nel video, avere file SRT e WebVTT separati, o entrambi.
- **Take** raggruppa le battute che hai detto più di una volta, suggerisce la più pulita e mette da parte le altre su una traccia disattivata.
