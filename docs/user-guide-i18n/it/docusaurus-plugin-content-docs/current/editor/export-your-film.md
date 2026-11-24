---
title: Esporta il tuo film
sidebar_position: 3
description: Come scegliere un preset, leggere quanto costerà un render, generare il film dalla tua timeline, seguire la coda, scaricare il film, condividerlo per la revisione, esportare il montaggio per altre app ed esportare una sola clip.
i18n_source_sha: 739abec2391e122fb6160f204ad255f745571d26
---

# Esporta il tuo film {#export-your-film}

Quando la modifica è pronta, apri **Consegna** (il menu **Altro > Esporta** dell'editor la apre). L'etichetta **Anteprima anticipata** dell'editor resta anche in questa pagina, come ovunque nell'editor.

Su computer o tablet la pagina ha due lati. A sinistra scegli cosa esportare: un **preset**, le sue opzioni e un breve riepilogo di ciò che verrà creato e di quanto costa, con il pulsante **Render** subito sotto. A destra c'è tutto ciò che ne esce: l'ultimo film, la coda di render, i link di revisione, l'esportazione per altre app e il pacchetto di consegna.

## Prima di tutto il tuo film {#your-film-comes-first}

La sezione **Ultimo film** mostra il tuo ultimo film finito: un player, il nome del file, la durata, la dimensione del file e quanto tempo fa è stato creato. Sotto il player:

- **Scarica** salva il film sul tuo computer con il suo nome.
- **Apri nei file del progetto** ti porta alla pagina File del progetto con il film selezionato.
- **Copia il link** copia l'indirizzo del film nel tuo progetto. Le persone che hanno accesso al progetto possono aprirlo. Non è un link pubblico.
- **Elimina** chiede conferma, poi rimuove il film dai file del progetto. La timeline non cambia. Se puoi solo guardare la timeline, Elimina non viene proposto.

Sotto i pulsanti, una riga ti dice se il film contiene le tue ultime modifiche: "Ultimo render: la tua ultima modifica", oppure "Ultimo render: prima delle tue ultime modifiche (3 modifiche da allora)".

## Scegli un preset e premi Render {#choose-a-format-and-press-render}

**Preset** è l'elenco in cima al lato sinistro. Ogni riga è un tipo di film e dice cosa produce: la dimensione, la forma e a cosa serve. Un preset è sempre selezionato e l'elenco non viene mai nascosto.

- **Full HD orizzontale**, **4K orizzontale**, **Verticale per telefoni**, **Quadrato** e **Verticale 4:5** sono forme fisse. Se la forma non corrisponde alla tua timeline, ti viene detto che l'immagine sarà ritagliata o avrà delle bande.
- **Master** esporta alla dimensione della tua timeline con la qualità più alta, da conservare o da finire altrove.
- **Solo audio** crea un file audio senza immagine.

L'elenco ti dice cosa fa il tuo piano con ogni preset prima che tu faccia clic. Un preset che il tuo piano non può esportare è indicato come **Non incluso nel tuo piano** e non si può scegliere; una riga sotto l'elenco spiega il motivo e porta ai piani. Un preset più grande di quanto il tuo piano consente viene esportato comunque: la sua riga dice a quale dimensione uscirà.

**Opzioni** contiene ciò che rifinisce il preset scelto:

- **Qualità** è Bozza (la più veloce e leggera), Buona o Ottima (la più nitida, più lenta, file più grande).
- **Solo l'intervallo in/out segnato** esporta soltanto la parte compresa tra i tuoi punti di entrata e di uscita. Finché non sono impostati entrambi, l'interruttore ti dice quali tasti li impostano.
- **Nome del file** è facoltativo. Lascialo vuoto per dare al film il nome della timeline.
- **Sottotitoli**, quando la tua timeline ne ha, sceglie se incorporarli nell'immagine, avere solo file separati o entrambe le cose.
- **Dimensione e qualità** e **Avanzate** raccolgono la dimensione del file, la normalizzazione del volume e le scelte su codec e velocità, per chi ne ha bisogno.

Il pulsante **Render** dice cosa creerà, per esempio "Esporta il film verticale". Salva prima le tue ultime modifiche, così il film contiene sempre ciò che vedi sullo schermo. Quando Render è disattivato, una riga sopra il pulsante dice perché e cosa fare: la timeline è vuota, il tuo piano non include questa dimensione, il nome del file contiene un carattere che non si può usare, ti servono altri crediti, oppure non è stato possibile salvare le ultime modifiche ("Salva prima:" con il motivo e un modo per riprovare a salvare).

## Salva i tuoi preset {#save-your-own-presets}

Sotto l'elenco, **I miei preset** conserva le impostazioni che usi spesso.

1. Imposta la pagina come vuoi: il preset, la qualità, la dimensione del file e il volume.
2. Premi **Salva queste impostazioni come preset** e dagli un nome.
3. La prossima volta premi **Applica** su quel preset. Un preset le cui impostazioni sono quelle della pagina in questo momento è indicato come **In uso**.

**Rinomina** ed **Elimina** sono su ogni preset; Elimina chiede conferma. Un preset contiene formato, qualità, dimensione, codec e volume. L'intervallo, il nome del file e la scelta dei sottotitoli appartengono a ogni singolo render, quindi non vengono salvati.

I tuoi preset restano in questo browser, solo su questo dispositivo. Non ti seguono su un altro computer e, se cancelli i dati del sito dal browser, vengono rimossi. Se il browser non permette alla pagina di conservare nulla (per esempio in navigazione privata), la sezione lo dice e il resto della pagina funziona come sempre.

## Imposta la dimensione del file {#set-the-file-size}

In **Dimensione e qualità**, **Come scegliere la dimensione del file** offre tre scelte:

- **Aspetto costante** mantiene l'immagine sempre allo stesso livello. La dimensione del file dipende dal tuo video.
- **Prima l'aspetto, con un limite** mantiene l'aspetto e il file vicino o sotto il bitrate video che imposti. È un obiettivo, quindi il file può superarlo di poco.
- **Punta a una dimensione** punta al bitrate video che imposti, così il file risulta vicino alla dimensione mostrata sotto l'impostazione.

Imposta **Bitrate video (Mbps)** da 0,2 a 100 Mbps e **Bitrate audio (kbps)** da 64 a 320. La dimensione mostrata è sempre approssimativa ("Circa"), perché il file reale dipende dal tuo video. Un film solo audio non ha un bitrate video.

**Suggerito per** compila le impostazioni per una destinazione. Sono punti di partenza suggeriti, non la promessa che un sito accetti il file. Gli 8 Mbps di YouTube sono la sua raccomandazione pubblicata per il 1080p; Instagram Reels e TikTok non pubblicano alcun bitrate, quindi quei valori sono nostri suggerimenti.

| Destinazione | Formato | Impostazione della dimensione | Video | Audio |
|---|---|---|---|---|
| YouTube | Full HD orizzontale | Punta a una dimensione | 8 Mbps | 320 kbps |
| Instagram Reels | Verticale per smartphone | Prima l'aspetto, con un limite | 5 Mbps | 128 kbps |
| TikTok | Verticale per smartphone | Prima l'aspetto, con un limite | 6 Mbps | 128 kbps |
| Piccolo per email o chat | Il tuo formato attuale | Punta a una dimensione | 2,5 Mbps | 128 kbps |

Il bitrate non cambia il prezzo: un render costa uguale a 2 Mbps e a 100 Mbps.

## Quanto costa {#what-it-costs}

**Prima del render** è un unico riepilogo che resta visibile sopra il pulsante Render. Dice:

- **Formato**: il preset e la dimensione dell'immagine, con la dimensione a cui il tuo piano la esporterà quando è più piccola.
- **Intervallo**: tutta la timeline o l'intervallo segnato, con inizio e fine in timecode.
- **Sottotitoli**: dove vanno, quando la tua timeline ne ha.
- **Costo**: circa quanti crediti usa il render e circa quanto tempo richiede, l'intervallo in cui rientra di solito e quanti crediti hai.

Le cifre vengono dalla stessa stima che usa il render, per le impostazioni esatte della pagina. Quando cambi un'impostazione, il riepilogo dice che sta calcolando il costo e poi mostra le nuove cifre; non mostra mai il vecchio costo accanto a impostazioni nuove.

- Se non hai abbastanza crediti, il riepilogo indica quanti te ne servono ancora e propone **Aggiungi crediti**, e Render resta disattivato.
- Se in questo momento non è possibile calcolare il costo, lo dice e propone **Riprova**. I tuoi crediti vengono comunque controllati prima che un render parta.
- Se i crediti non fanno parte del tuo piano, non c'è una riga del costo: il riepilogo dice **Pronto per il render**.

## Esporta una sola clip {#export-only-one-clip}

Seleziona una clip, apri Consegna e premi **Esporta solo "nome della clip"**. Con un solo clic l'intervallo viene impostato su quella clip e il render parte. Mentre lavora, il pannello dice "Render di "nome della clip" in corso". I tuoi punti di entrata e di uscita restano impostati sulla clip anche dopo.

## Esporta per altre app {#export-for-other-apps}

La sezione **Esporta per altre app** salva il tuo montaggio come file leggibile da un altro programma. Non usa crediti.

1. Scegli il **Tipo di file**.
2. Leggi cosa include quel file e cosa lascia fuori. I due elenchi vengono dallo stesso punto che scrive il file, quindi descrivono esattamente il download. Una riga sotto dice se il file rimanda ai tuoi file multimediali.
3. Premi **Scarica** per quel tipo.

Gli altri tipi di file sono elencati sotto il download, per chi sa già quale vuole. I due tipi di file dei sottotitoli sono proposti solo quando la tua timeline ha sottotitoli. I file contengono ciò che è già salvato.

## Per un altro editor {#for-another-editor}

La sezione **Pacchetto di consegna** prepara un unico pacchetto per un montatore professionista: il montaggio, i tuoi file video con i loro nomi originali, un elenco di ciò che contiene e una breve nota su come aprirlo.

1. Scegli l'editor: Premiere Pro, DaVinci Resolve, Final Cut Pro, un altro editor oppure **Solo tagli (EDL)**. Premiere Pro e DaVinci Resolve possono usare due tipi di file; la scheda sceglie il primo.
2. Scegli **Includi i file video** oppure **Solo l'elenco dei file**. La scheda mostra cosa contiene il pacchetto, per esempio "12 file, 1,4 GB". Quando i file insieme sono troppo grandi per un solo pacchetto, la prima opzione è disattivata, la scheda ne spiega il motivo e **Solo l'elenco dei file** viene scelto per te. In quel caso copia tu gli originali.
3. Per **Solo tagli (EDL)** con più di una traccia video, scegli quale traccia video scrivere.
4. Premi **Scarica il pacchetto**. Il file viene salvato dal tuo browser. Se qualcosa lo ferma (per esempio ci sono già due pacchetti in preparazione), la scheda lo dice a parole.

I pacchetti di consegna portano l'etichetta **Anteprima anticipata**. Non ne abbiamo ancora aperto nessuno in Premiere Pro, DaVinci Resolve o Final Cut Pro, quindi consideralo una prima versione.

**Come aprirlo** dà tre righe per ogni editor. Non abbiamo aperto questi pacchetti in tutti gli editor, quindi le righe dicono "Dovrebbe aprirsi in ..." e ti chiedono di dirci se non succede.

Su smartphone la scheda dice soltanto che questa è un'operazione da desktop; i link di esportazione qui sopra funzionano comunque.

## Importare una timeline da un altro editor {#importing-a-timeline-from-another-editor}

Nella pagina delle timeline, **Importa** legge un file OpenTimelineIO. Al termine, un pannello dice cosa è successo: i file trovati (e come: stesso file dell'esportazione, trovato per nome, trovato per nome e durata) e le clip il cui media è **offline**, ciascuna con il motivo:

- "Nel progetto non c'è nessun file con questo nome"
- "Due file hanno questo nome; sceglierne uno"
- "La durata del file è diversa da quella della clip"

Ogni clip offline diventa uno spazio vuoto della sua stessa durata, con un segno rosso. **Ricollega** mette lì un file del progetto; se due file hanno lo stesso nome, **Scegli un file** li elenca con durata e dimensione. Il ricollegamento è un passaggio che puoi annullare. L'interruttore **L'importazione fallisce se manca qualcosa** (disattivato di default) rifiuta invece l'intera importazione. Ricollegare un'intera cartella in una volta non è ancora disponibile.

## L'elenco dei render {#the-render-list}

La sezione **Coda di render**, sotto l'ultimo film, elenca tutti i render di questa timeline, dal più recente: In attesa di uno slot libero, In corso (con l'avanzamento e un pulsante **Ferma**), Completato, Non riuscito o Annullato. Ogni riga indica il formato e, quando è finito, la durata del film, la dimensione del file e quando è stato creato.

- Una riga finita ha **Scarica**. Se il render ha creato file dei sottotitoli, sono elencati anch'essi sulla riga.
- Un render non completato ne spiega il motivo con parole semplici e offre **Riprova** (con le impostazioni mostrate ora) e **Rimuovi**.
- **Esporta di nuovo** rimette sulla pagina le impostazioni con cui un render era stato avviato: il preset, la qualità e la dimensione del file, la scelta dei sottotitoli, l'intervallo e il nome del file. Non parte ancora nulla. Leggi il riepilogo, poi premi Render. Esporta di nuovo è proposto solo per i render avviati da questo browser, perché solo questo browser ricorda le loro impostazioni complete.
- Ciò che un render ha escluso o segnalato si trova sotto la sua riga, con parole semplici.

Prima del tuo primo render, la coda lo dice e propone **Scegli un preset**, che ti porta all'elenco dei preset.

## Condividilo per la revisione {#share-it-for-review}

La sezione **Condividi per revisione**, accanto al film, crea un link che mostra un film finito a qualcuno senza account. Premi **Crea un link di revisione**, imposta le opzioni e copia il link quando compare: viene mostrato una sola volta. I link che hai creato sono elencati sotto, con visualizzazioni e note, e ognuno può essere disattivato. Vedi [Condividi un film per la revisione](share-for-review.md).

## Su smartphone {#on-a-phone}

Tocca **Esporta** nella barra in alto. Il pannello si apre a tutta altezza: scegli un formato (Verticale per smartphone è il primo), controlla la riga del prezzo e premi **Render**, che resta in fondo al pannello così non devi mai scorrere per trovarlo. L'intervallo, il nome del file, il modo in cui vengono consegnati i sottotitoli e le scelte sul volume sono sotto **Altre opzioni**.

Mentre un film viene generato puoi lasciare la pagina. Quando torni, l'elenco è aggiornato. Se un film è finito mentre il pannello era chiuso, **Esporta** mostra un segno verde **Pronto** e ricevi un messaggio sullo schermo. Il film finito si riproduce lì stesso. **Scarica** lo salva con il suo nome e **Condividi il film** passa il file a un'altra app quando il tuo telefono lo permette (altrimenti **Condividi il link** condivide il link del progetto, e se il tuo telefono non può condividere affatto il pulsante non viene mostrato). Non inviamo un'email quando un film è pronto.

## Scaricare da altre pagine {#downloading-from-other-pages}

Nella scheda Render del tuo progetto e nel risultato di un passaggio di un flusso di lavoro, **Scarica** salva il video come file invece di aprirlo nel browser.
