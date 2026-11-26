---
title: Per iniziare
sidebar_position: 2
description: Accedi, crea il tuo primo progetto, carica i file, avvia la tua prima app e trova il video finito.
i18n_source_sha: aa25450303b1dd397e69b133a589848e61ba02c3
---

# Per iniziare {#getting-started}

:::note
Questa è la versione dettagliata; quella breve è in [Le basi](./basics/index.md).
:::

:::tip Sei nuovo?

Le guide brevi e illustrate in [Le basi](./basics/index.md) ti portano dalla registrazione al tuo primo video in dieci minuti.

:::

Questa pagina ti guida nella tua prima sessione in ReelBolt: accedere, creare un progetto, aggiungere file, avviare un'app, seguire l'esecuzione e trovare il video finito.

## Fai il tuo primo film con una clip di esempio {#make-your-first-film-with-a-sample-clip}

Se non hai ancora eseguito nulla, la dashboard mostra una scheda **Fai il tuo primo film** (la pagina Apps vuota offre lo stesso pulsante). Con un clic si crea un progetto chiamato "My first film", si aggiunge una breve clip di esempio e si apre l'app di montaggio dei momenti migliori con quella clip già scelta. Non parte nulla da solo: premi **Avvia** quando sei pronto. Puoi chiudere la scheda con la sua **X** e sparisce quando hai un'esecuzione.

## Il primo accesso a ReelBolt {#signing-in-to-reelbolt-for-the-first-time}

Come entri dipende dal tipo di ReelBolt che usi.

**Un ReelBolt self-hosted non ha registrazione pubblica.** Un amministratore crea il tuo account e ti dà una **password temporanea** (ti arriva via email se l'installazione ha la posta configurata; altrimenti te la consegna direttamente l'amministratore).

1. Apri la pagina di accesso di ReelBolt, inserisci la tua **Email** e la **Password** temporanea, poi clicca **Accedi**.
2. Poiché la password è temporanea, ReelBolt ti porta subito alla schermata di cambio password. Non puoi usare il resto dell'app finché non completi questo passaggio.
3. Compila **Password attuale** (quella temporanea), **Nuova password** (almeno 10 caratteri) e **Conferma la nuova password**, poi clicca **Cambia password**.

**Un ReelBolt cloud ti permette di registrarti da solo** (quando la registrazione autonoma è attiva per l'installazione; in una self-hosted non lo è).

1. Apri la pagina di registrazione. Dove l'installazione lo offre, **Continua con Google** o **Continua con GitHub** crea il tuo account senza niente da scrivere e senza email da confermare. Altrimenti inserisci il tuo **Nome**, la tua **Email** e una **Password** (almeno 10 caratteri; la scrivi una volta sola), accetta i termini e l'informativa sulla privacy, poi clicca **Crea account**.
2. Se ti sei registrato con l'email, ReelBolt invia un link di conferma a quell'indirizzo. Aprendo il link confermi l'indirizzo e accedi. Finché non lo fai, resta in alto una barra gialla **Conferma il tuo indirizzo email** e le esecuzioni non partono. **Invia di nuovo l'email** su quella barra rimanda il messaggio. (Se ti registri da un link di invito a un team, l'indirizzo viene confermato in automatico, perché l'invito lo dimostra.)
3. La prima volta che arrivi, ReelBolt ti fa due brevi domande e prepara il tuo primo progetto in base alle risposte; vedi [Registrati e dai uno sguardo in giro](./basics/sign-up-and-first-look.md#answer-two-questions). Parti dallo **spazio di lavoro personale** assegnato al tuo account.

Da allora in poi, a ogni accesso arrivi nella dashboard. L'accesso dura 24 ore; dopo ti viene chiesto di accedere di nuovo. Se dimentichi la password, chiedi a un amministratore di reimpostarla oppure usa **Password dimenticata** nella pagina di accesso; vedi [Gestire gli utenti](./admin/users.md).

## Orientarsi nella dashboard di ReelBolt {#finding-your-way-around-the-reelbolt-dashboard}

Il menu a sinistra contiene, in alto, le cose che usi ogni giorno:

- **Dashboard**: una panoramica delle attività recenti, delle esecuzioni in corso e di tutto ciò che richiede attenzione.
- **Progetti**: tutti i tuoi progetti.
- **Editor**: una vista timeline dei montaggi prodotti dalle app video (l'editor video e l'editor di immagini sono disponibili come anteprima anticipata).

Sotto **Altro** trovi:

- **Stili**: gli stili di montaggio con cui si può avviare un'app.
- **Runner**: i computer collegati a questo spazio di lavoro che possono creare i suoi video.
- **Agenti (avanzato)**: i lavoratori AI dietro le app (di rado ti serve questa pagina).

Tutto il resto è raggruppato in sezioni che puoi aprire e chiudere. Una sezione si apre da sola quando la pagina in cui ti trovi ne fa parte, così il menu non nasconde mai dove sei:

- **Impostazioni**: **Account** — la tua pagina personale, con i tuoi dati, la password, l'aspetto della dashboard e gli account che hai collegato — poi **Utilizzo** e **Piano e fatturazione**, che riguardano lo spazio di lavoro in cui ti trovi in quel momento.
- **Spazio di lavoro** (Proprietari e Amministratori dello spazio di lavoro): le **Impostazioni** dello spazio di lavoro, i suoi **Membri** e i suoi **Provider**.
- **Piattaforma** (solo amministratori della piattaforma): **Panoramica**, **Servizio flussi di lavoro**, **Inferenza**, **Fatturazione**, **Calibrazione**, **Competenze** e **Utenti**.

Amministrare l'intera installazione è compito della sezione **Piattaforma**. Non è il posto dove aggiungere qualcuno a uno spazio di lavoro: per quello c'è **Spazio di lavoro → Membri**, e le due cose sono volutamente separate.

Il pulsante **assistente** (un'icona a scintilla) si trova nella barra in alto, in ogni pagina. Apre un pannello di chat sulla destra (o dal basso su un telefono). Vedi [L'assistente](./assistant/index.md).

## Creare un progetto in ReelBolt {#creating-a-project-in-reelbolt}

Un progetto contiene tutto ciò che serve per un video: i suoi file, le sue app e i suoi video.

1. Vai su **Progetti** e clicca **Nuovo progetto**.
2. Dagli un **Nome** (per esempio "Teaser di primavera") e, se vuoi, una **Descrizione**.
3. Salva. Il nuovo progetto si apre nella scheda **Panoramica**.

La pagina di un progetto ha diverse schede, tra cui **Panoramica**, **File**, **App** (si chiama **Flussi di lavoro** dove le app sono disattivate), **Video** e **Voci** (le voci narranti che questo progetto può usare; vedi la [ricetta sulla voce narrante](./recipes/voiceover.md)), più le schede delle impostazioni come **Brand e prodotto** e **Repository**. Un link che termina con `?tab=voices` (oppure `files`, `workflows`, `renders`) apre direttamente quella scheda. Per rinominare un progetto o eliminarlo, usa il menu **⋮** (Azioni del progetto) accanto a **Carica file** e scegli **Modifica nome e descrizione** o **Elimina progetto**. Eliminare un progetto rimuove definitivamente tutti i suoi file, le impostazioni delle sue app e i video che hanno creato.

I progetti sono privati: li vede solo la persona che li ha creati.

## Caricare i file in un progetto ReelBolt {#uploading-files-to-a-reelbolt-project}

Clicca **Carica file** in cima alla pagina del progetto, oppure apri la scheda **File** e usa **Carica**, che offre **Scegli i file** o **Scegli una cartella**. Puoi anche trascinare file o intere cartelle in un punto qualsiasi della pagina del progetto. Quando carichi una cartella, la sua struttura viene mantenuta.

Carica ciò che serve all'app: codice sorgente per un promo creato dal tuo prodotto, video grezzi per un montaggio, file di musica ed effetti sonori, immagini o documenti di riferimento. I file video molto grandi sono supportati (sono consentiti upload di molti gigabyte).

**Dove compaiono i file.** La scheda **File** può mostrare **Sorgenti** (ciò che hai caricato), **Generati** (i file creati durante le esecuzioni), **Video** (i video finiti) oppure **Tutti**. Puoi anche filtrare per tipo (video, audio, immagine, testo), ordinare, passare dalla vista a elenco a quella a griglia e organizzare i file in cartelle con **Nuova cartella**, **Sposta in...**, rinomina ed elimina. Per rinominare un singolo file, apri il suo menu **⋮** (o la sua anteprima) e scegli **Rinomina**; i flussi di lavoro che usano il file continuano a funzionare. Un nome non può essere vuoto, contenere `/` o `\`, superare i 255 caratteri o coincidere con un altro file nella stessa cartella: ReelBolt ti dice quale di questi casi si verifica.

**Riepiloghi.** Dopo aver caricato un file di testo (codice, documenti), ReelBolt ne scrive automaticamente un breve riepilogo. Clicca un file per aprire la sua anteprima e guarda la scheda **Riepilogo**; finché non è pronto vedrai "Riepilogo in preparazione". Il riepilogo di un'immagine è una breve descrizione scritta dal modello di visione, quando ne è configurato uno. Un PDF con testo selezionabile viene riassunto a partire da quel testo; un PDF scansionato no. I file video, audio e gli altri file non testuali non vengono riassunti. I caricamenti non aspettano mai i riepiloghi: una cartella grande si carica a piena velocità e i suoi riepiloghi vengono scritti dopo, pochi alla volta, con una breve pausa ogni volta che stai chattando con l'assistente, così risponde prima a te.

**Indicizzazione per la ricerca.** I file di testo vengono aggiunti anche all'indice di ricerca del progetto, così tu (e gli agenti) potete cercare al loro interno per significato, non solo per nome del file. Funziona solo se il tuo amministratore ha configurato un provider di Embedding; vedi [Ricerca semantica](./admin/semantic-search.md). I file video e audio non vengono mai indicizzati. La scheda **Dettagli** di un file mostra lo stato dell'indicizzazione, con un pulsante **Reindicizza**.

## L'app iniziale di ogni progetto ReelBolt {#the-starter-workflow-every-new-reelbolt-project-gets}

Un nuovo progetto parte con un'app già pronta, scelto in base a cosa hai detto che è il materiale del video quando l'hai creato. Un progetto che parte da clip riceve **Highlight edit from footage v1**, che toglie dal tuo girato i tempi morti, le false partenze e i ciak ripetuti; vedi [Montaggio dei momenti migliori dal girato](./recipes/derush-edit.md#recipe-highlight-edit-from-footage). Un progetto che parte da un'app o da un sito che hai creato riceve **Promo video from your code v1** (nei progetti più vecchi si chiama **Quick Win Promo v1**), e lo stesso vale per un progetto creato senza quella scelta, per esempio tramite l'API. Il resto di questa sezione parla di quest'ultimo. È la pipeline "promo dal tuo codice": analizza il codice che hai caricato (struttura, dipendenze, componenti, percorsi e stili), lo trasforma in scene animate, pianifica l'animazione, scrive narrazione e sottotitoli, dirige le inquadrature, costruisce e genera il video e infine esegue una revisione di qualità che può rimandare il video indietro per un altro tentativo se il punteggio è sotto 8 su 10. Quando lo avvii, ti chiede il brief: a chi è rivolto il video, cosa mostrare, quanto deve durare e il formato (un "reel verticale da 15 s" esce in 1080x1920). Non ha un passaggio di narrazione, quindi il video è muto a meno che tu non ne aggiunga uno.

È pensato per progetti i cui file sono codice sorgente. Se il tuo progetto riguarda filmati reali, scegli invece un'altra app: vai alla scheda **App** (oppure **Flussi di lavoro**), clicca **Nuovo flusso di lavoro**, scegli una delle schede sotto **Parti da un template** (ognuna mostra il nome del template e cosa produce) e clicca **Usa questo template**. I template disponibili sono descritti in [Template dei flussi di lavoro](./workflows/templates.md). Puoi anche chiedere all'assistente di suggerirti o abbozzare un flusso di lavoro per i tuoi file.

## Avviare un'app in ReelBolt {#running-a-workflow-in-reelbolt}

1. Apri il progetto, vai alla scheda **App** (si chiama **Flussi di lavoro** dove le app sono disattivate) e clicca l'app.
2. Clicca **Avvia** in cima alla pagina dell'app. Non parte ancora nulla: prima si apre una finestra di conferma.
3. La finestra elenca cosa farà l'esecuzione, in parole semplici, e più o meno quanto ci vorrà (in base alle tue esecuzioni precedenti della stessa app, oppure un avviso che le esecuzioni lunghe possono richiedere fino a un'ora).
4. Scrivi **Il tuo brief**: che video vuoi, per esempio "un teaser di 30 secondi incentrato sulla nuova dashboard, verticale, con un tono vivace". Ogni parte dell'esecuzione lo vede. Il brief viene sempre proposto; è obbligatorio solo quando nel flusso di lavoro è attivo **Richiedi un brief prima di ogni esecuzione**.
5. Clicca **Avvia esecuzione**. Se il flusso di lavoro genera clip video a pagamento, una seconda finestra intitolata **Questo flusso di lavoro genera clip video a pagamento** ti chiede se riutilizzare le clip generate in precedenza (gratis) o rigenerarle (costa denaro), e mostra un limite massimo di quanto potrebbe costare rigenerarle.
6. ReelBolt avvia l'esecuzione, la aggiunge subito alla **Cronologia esecuzioni** del flusso di lavoro e ti porta alla sua pagina in diretta.

Puoi avviare di nuovo la stessa app in qualsiasi momento. Il lavoro i cui input non sono cambiati può essere riutilizzato dall'esecuzione precedente invece di essere rifatto; vedi [Concetti](./concepts.md).

## Seguire un'esecuzione in ReelBolt {#watching-a-reelbolt-workflow-run}

La pagina dell'esecuzione si aggiorna in diretta mentre l'esecuzione procede. Il titolo indica l'esecuzione come la vedrai ovunque, per esempio **Esecuzione 3 · 4 ott, 14:02** (la terza esecuzione di quel flusso di lavoro, avviata il 4 ottobre alle 14:02).

- **Prima gli avvisi.** Se qualcosa nell'esecuzione non è andato come richiesto — righe di narrazione che non è stato possibile posizionare, nessuna musica perché il progetto non ha file audio, un video molto più corto della durata richiesta, una voce narrante che non funziona — un riquadro giallo in alto lo dice in parole semplici. Un'esecuzione può finire e avere comunque un avviso, quindi leggilo prima di condividere il video.
- Il badge di stato mostra **In attesa di avvio**, **In corso**, **Completata**, **Fallita** o **Fermata**, con una barra di avanzamento sotto.
- Mentre lavora, una riga dice cosa sta succedendo in questo momento in parole semplici, per esempio "Sto scrivendo la narrazione…" o "Sto guardando i tuoi filmati…", con il nome del passaggio.
- Quattro riquadri mostrano **Passaggi fatti**, quando l'esecuzione è **Avviata**, da quanto tempo va avanti (**Trascorso**, oppure **Durata** quando termina) e **Tempo rimasto**. Il tempo rimanente è una stima basata sul tempo impiegato dai passaggi già completati; i passaggi che generano video di solito richiedono più degli altri, quindi consideralo un'indicazione di massima.
- Il diagramma **Passaggi** mostra ogni passaggio con il suo compito in linguaggio semplice. Clicca un passaggio per vedere i suoi **Dettagli del passaggio**: cosa ha prodotto, quanto tempo ha impiegato, eventuali problemi e se è stato riutilizzato da un'esecuzione precedente.
- **Attività** elenca cosa è successo, dal più recente, con ogni agente indicato dal suo compito.
- **Dettagli** (sotto i riquadri) mostra le informazioni tecniche: lo stato della connessione in diretta, il conteggio dei token (una misura di quanto testo AI è stato letto e scritto), i cicli di revisione, l'id interno dell'esecuzione, i dati grezzi di ogni passaggio e il payload grezzo di ogni evento. Non ti serve nulla di tutto questo per seguire un'esecuzione.
- **Ferma** (visibile mentre l'esecuzione è in corso) annulla l'esecuzione. **Riavvia** (visibile quando l'esecuzione è terminata, fallita o fermata) la fa ripartire.

Le esecuzioni possono richiedere del tempo: da pochi minuti per i lavori piccoli a molto di più per codebase grandi o filmati lunghi. Puoi lasciare la pagina; l'esecuzione prosegue e la dashboard la mostra tra le esecuzioni in corso.

## Trovare il video finito in ReelBolt {#finding-the-finished-video-in-reelbolt}

Quando un'esecuzione genera un video, questo compare nella scheda **Video** del progetto (e in **Ultimi video** nella scheda **Panoramica**). Ogni video prende il nome dall'app e dall'esecuzione che l'hanno prodotto (per esempio "Storia del prodotto da 60 s — Esecuzione 2") e mostra quando è stato creato, la durata, il formato e le dimensioni (per esempio "Verticale 9:16 · 1080×1920").

- **Finale** indica il video finito di un'esecuzione completata. **Bozza** indica un tentativo precedente — un passaggio di revisione che ha rimandato indietro il lavoro produce una bozza per ogni ciclo — oppure un video di un'esecuzione che non è terminata. **In corso** indica un video di un'esecuzione ancora in corso. La scheda si apre sul video finale più recente.
- Eventuali avvisi dell'esecuzione di quel video sono mostrati sotto il player.
- Clicca **Scarica** per salvare il video; il file prende il nome dal flusso di lavoro e dall'esecuzione.
- Clicca **Condividi** per aprire il menu di condivisione del tuo dispositivo. Dove il browser non ha un menu di condivisione, il link viene copiato e un messaggio "Link copiato" lo conferma; se anche la copia è bloccata, il messaggio mostra il link così puoi copiarlo da solo. **Il link si apre solo per le persone che hanno effettuato l'accesso a questo progetto** (oggi è solo il proprietario del progetto), quindi è per uso tuo, non per inviarlo a un cliente. I link pubblici o con scadenza non sono disponibili.
- Clicca **Avvia** per passare all'esecuzione che l'ha prodotto.

I video compaiono anche come file sotto **Video** nella scheda **File**. Le app di montaggio video ti permettono inoltre di aprire il montaggio su una timeline nella sezione **Editor**: elenca i video montati del progetto per app ed esecuzione e, se non ce ne sono ancora, il pulsante **Avvia un'app** ti porta alle app del progetto. L'editor video è disponibile come anteprima anticipata, e ogni pagina dell'editor lo dice.

## Prossimi passi dopo la tua prima esecuzione in ReelBolt {#next-steps-after-your-first-reelbolt-run}

- Impara il vocabolario in [Concetti](./concepts.md).
- Non sai cosa creare? Parti da [Cosa vuoi fare?](./index.md#what-do-you-want-to-make) oppure da [Scegli un'app](./recipes/choose-a-recipe.md).
- Segui una guida, per esempio [un promo dal tuo codice](./recipes/promo-from-code.md) oppure [derush e montaggio di filmati](./recipes/derush-edit.md).
- Chiedi all'[assistente](./assistant/index.md) "Perché è fallita la mia ultima esecuzione?" se qualcosa va storto, oppure leggi [Risoluzione dei problemi](./troubleshooting.md).
