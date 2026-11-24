---
title: I tuoi dati, le esportazioni e la chiusura dell'account
sidebar_position: 13
description: Come scaricare una copia di tutto ciò che ReelBolt conserva su di te, come chiudere il tuo account, cosa significa il periodo di tolleranza di 30 giorni, cosa conserva ReelBolt e perché, e come iscriverti o disiscriverti dalle email di marketing.
i18n_source_sha: 462e7431fe8159950291df6eb9abe49f52aacf5f
---

# I tuoi dati, le esportazioni e la chiusura dell'account {#your-data-exports-and-closing-your-account}

Puoi portare via i tuoi dati da ReelBolt in qualsiasi momento, e puoi chiudere il tuo account senza chiedere a nessuno. Questa pagina spiega entrambe le cose, e cosa succede esattamente alle tue informazioni quando lo fai.

## Scarica una copia dei tuoi dati {#download-a-copy-of-your-data}

Puoi chiedere a ReelBolt una copia di tutto ciò che conserva sul **tuo account e sul tuo spazio di lavoro personale**, in un unico file zip.

1. Apri **I tuoi dati** sotto **Impostazioni** nel menu a sinistra. (Le stesse card sono anche in fondo alla pagina **Account**.)
2. In **Scarica i tuoi dati**, seleziona **Richiedi una copia**.
3. ReelBolt prepara il file in background — qualche minuto, di più se hai molti filmati — e ti manda via email un link per il download quando è pronto. Puoi lasciare la pagina mentre lavora.
4. La copia compare anche sulla stessa card: seleziona **Scarica i tuoi dati (zip)**. Ogni volta che la selezioni ottieni un download nuovo, quindi non importa se il link ricevuto via email è scaduto.

Una copia viene conservata per **sette giorni** dopo la creazione, poi viene eliminata. Puoi chiedere **una copia al giorno**: se ne chiedi un'altra prima, la card ti dice quanto aspettare. Se la copia che hai chiesto non è ancora comparsa, è ancora in preparazione e l'email sta arrivando.

Se la card dice che il download di una copia non è disponibile, sul server che usi la funzione è disattivata: chiedi a chi lo gestisce.

### Cosa c'è nell'esportazione {#what-is-inside-the-export}

Lo zip contiene una cartella di semplici file JSON — uno per ogni tipo di record — più i tuoi file originali:

- **Il tuo account**: il tuo indirizzo email, il nome visualizzato, quando l'account è stato creato e verificato. La tua password non c'è mai, in nessuna forma.
- **I tuoi progetti, file, flussi di lavoro ed esecuzioni**: nomi, descrizioni e tutto ciò che gli agenti hanno prodotto lungo il percorso — script, piani, trascrizioni dei tuoi filmati, sottotitoli, feedback di revisione.
- **I tuoi upload**: i file originali che hai messo nei tuoi progetti, fino a un limite di dimensione. Tutto ciò che supera il limite è elencato nel manifest (`manifest.json`) invece di essere copiato, così anche una libreria di filmati molto grande ti dà un download gestibile.
- **Le tue chat con l'assistente**: ogni conversazione che hai avuto con l'assistente.
- **I tuoi elementi salvati**: agenti personalizzati, stili di montaggio, voci (compreso il registro del consenso per una voce clonata, senza la copia del fornitore) e documenti dell'editor con la loro cronologia.
- **Il tuo utilizzo e la fatturazione**: cosa hai usato, quanto è costato, il tuo piano e il suo stato e la cronologia dei tuoi crediti.
- **La tua scelta sulle email di marketing**: se sei iscritto, la formulazione esatta a cui hai acconsentito, quando e da dove, e ogni modifica successiva.

Alcune cose **non** sono incluse di proposito, e il manifest lo dice:

- **La tua password**, in qualsiasi forma, e i registri di sicurezza dietro i link di verifica e di reimpostazione che ReelBolt ti ha mandato via email. Servono a proteggere il tuo account; non sono un tuo contenuto.
- **Le scorciatoie salvate**: la cache di riuso che permette a un'esecuzione ripetuta di saltare il lavoro già fatto. I risultati veri e propri sono nell'esportazione.
- **Il contenuto di un altro spazio di lavoro.** Un'esportazione copre te e il tuo **spazio di lavoro personale**. Il lavoro dentro uno spazio del team appartiene al team e alle altre persone che ne fanno parte, quindi non è incluso: chiedi a un Proprietario del team se ti servono i dati del team.
- **Le chiavi delle tue connessioni AI.** Se hai configurato un tuo servizio AI dentro uno spazio di lavoro, l'esportazione elenca la connessione — nome, indirizzo e modello — ma mai la chiave in sé.

## Chiudi il tuo account {#close-your-account}

Puoi chiudere da solo il tuo account ReelBolt, dalla dashboard. Se vuoi una copia dei tuoi dati, scaricala prima (vedi [Scarica una copia dei tuoi dati](#download-a-copy-of-your-data)).

1. Apri **I tuoi dati** sotto **Impostazioni** nel menu a sinistra. (Le stesse card sono anche in fondo alla pagina **Account**.)
2. In **Elimina account**, seleziona **Elimina account…**.
3. Inserisci la tua **password**. Chiederla di nuovo è voluto: chi trova il tuo computer con l'accesso aperto non deve poter chiudere il tuo account.
   - Ti sei registrato con **Google o GitHub** e non hai mai impostato una password? Seleziona invece **Accedo con Google o GitHub**. Dopo la conferma, ReelBolt ti manda a Google o GitHub per accedere; non viene eliminato nulla finché non torni da quell'accesso.
4. Scrivi **DELETE** nella casella per confermare, poi seleziona **Elimina il mio account** (oppure **Continua**, per Google o GitHub).

Vieni disconnesso subito da ogni dispositivo e portato alla pagina di accesso, che mostra la data in cui il tuo account verrà eliminato. Un'email ti dice la stessa data.

ReelBolt rifiuta se sei l'**unico Proprietario di uno spazio di lavoro del team che ha ancora altri membri**, e indica il team: passa a quel team con il selettore di spazio di lavoro in alto nel menu a sinistra, poi in **Spazio di lavoro → Membri** rendi Proprietario qualcun altro o rimuovi gli altri membri, e riprova. Quando succede, non viene eliminato nulla.

Se paghi un piano, annullalo prima presso il fornitore di pagamento — vedi [Cosa ReelBolt non può rimuovere per te](#what-reelbolt-cannot-remove-for-you).

### Ripensarci {#change-your-mind}

ReelBolt non elimina nulla nel momento in cui lo chiedi: il tuo account viene **programmato** per l'eliminazione **30 giorni** dopo. Accedi di nuovo in qualsiasi momento durante quei 30 giorni — con la tua password, o con Google o GitHub — e l'eliminazione viene annullata. Non si perde nulla, e ReelBolt te lo dice quando arrivi. Vieni disconnesso da ogni dispositivo nel momento in cui lo chiedi, quindi accedere di nuovo è sempre un passo voluto, mai un caso.

### Cosa succede alla fine dei 30 giorni {#what-happens-at-the-end-of-the-30-days}

ReelBolt rimuove:

- i tuoi progetti, file e ogni oggetto che hanno archiviato — compreso ciò che fosse rimasto dopo un errore precedente,
- i tuoi flussi di lavoro, esecuzioni, risultati dei passaggi, feedback di revisione e registri di generazione video,
- i tuoi documenti dell'editor e la loro cronologia,
- l'indice di ricerca costruito dal testo dei tuoi file,
- le tue chat con l'assistente,
- i tuoi agenti personalizzati, stili di montaggio e voci; il modello di una voce clonata viene eliminato anche presso il servizio AI,
- la tua appartenenza a qualsiasi spazio di lavoro e gli inviti indirizzati alla tua email.

ReelBolt conserva, per motivi che è tenuto a rispettare:

- **I registri di fatturazione** — fatture e pagamenti vengono conservati per il periodo richiesto dalla legge fiscale (di solito da sette a dieci anni). Il tuo indirizzo email viene tolto da essi; restano associati allo spazio di lavoro per cui sono stati pagati.
- **I registri di utilizzo** — quanto è costata un'esecuzione, conservati per la contabilità. Restano, ma il collegamento con te viene rimosso.
- **Un'impronta irreversibile del tuo indirizzo email**, così la prova gratuita non può essere richiesta due volte eliminando un account e registrandosi di nuovo. Non si può riconvertire nel tuo indirizzo.
- **Se ti eri iscritto alle email di marketing, o ti eri disiscritto**, una seconda impronta irreversibile del tuo indirizzo email, così ReelBolt non ti manda mai più email di marketing. Il registro del tuo consenso viene invece eliminato.

Due cose sugli spazi di lavoro:

- Il tuo **spazio di lavoro personale** se ne va con te. Il suo contenuto viene eliminato e lo spazio viene chiuso e reso anonimo: ogni nome derivato dal tuo viene rimosso. Le righe contabili collegate (utilizzo, crediti, fatture) restano, senza il tuo indirizzo email.
- Uno **spazio di lavoro del team** che condividi con altre persone resta, e con lui il lavoro — quelle persone ne hanno ancora bisogno. Prima che tu elimini il tuo account, ReelBolt rifiuta se sei l'unico Proprietario di un team che ha ancora altri membri, perché il team resterebbe senza nessuno in grado di gestirlo. Passa la gestione del team — o rimuovi gli altri membri — e riprova.

### Cosa ReelBolt non può rimuovere per te {#what-reelbolt-cannot-remove-for-you}

Alcuni dei tuoi dati sono in mano ad altre aziende che ReelBolt usa per lavorare — il fornitore di pagamento che gestisce la fatturazione, i servizi AI che generano testo, voce e video e il servizio email che invia questi messaggi. Ognuna conserva i propri registri secondo i propri termini.

Due di questi richiedono la tua attenzione quando chiudi un account:

- **Un abbonamento a pagamento.** ReelBolt annulla il piano che gestisce direttamente e smette di concedere l'accesso, ma un abbonamento presso il fornitore di pagamento si annulla **lì**: apri **Piano e fatturazione** sotto **Impostazioni** nel menu a sinistra e seleziona **Gestisci fatturazione e fatture**, che apre il loro portale. Chiudere un account ReelBolt non pone fine in silenzio a un accordo di pagamento presso il fornitore — annullalo prima, oppure controlla il portale dopo.
- **Una voce clonata.** ReelBolt chiede al servizio AI di eliminare il modello clonato prima di rimuovere le registrazioni. Se quel servizio non è raggiungibile in quel momento, l'eliminazione viene registrata per un seguito invece di essere saltata in silenzio.

## Email di marketing da ReelBolt {#marketing-emails-from-reelbolt}

ReelBolt può mandarti via email novità sul prodotto, consigli e offerte, ma solo se lo chiedi tu. La scelta è solo tua: appartiene al tuo indirizzo email, non a uno spazio di lavoro, e nessun altro può farla al posto tuo. Le email sul tuo account e sulla fatturazione vengono inviate in ogni caso.

**Iscrizione.** Ovunque ReelBolt offra le email di marketing — quando crei un account, prima di un pagamento, nella pagina che annuncia un aggiornamento dell'Informativa sulla privacy e nel modulo di contatto del sito di ReelBolt — c'è una casella separata che parte non selezionata. Puoi creare un account, acquistare un piano o accettare i termini senza selezionarla.

- Se ti iscrivi dal tuo account, l'iscrizione vale una volta confermato il tuo indirizzo email.
- Se ti iscrivi dal modulo di contatto del sito, ReelBolt ti manda prima un link via email. Aprilo e seleziona **Conferma l'iscrizione**; non viene inviato nulla finché non lo fai. Il link funziona per sette giorni. Se non l'hai chiesto, ignora l'email.

**Attivarle o disattivarle.** Apri **Account** sotto **Impostazioni** nel menu a sinistra e usa l'interruttore **Email di marketing**. La card mostra quando hai acconsentito o quando ti sei disiscritto.

**Disiscriversi da un'email.** Ogni email di marketing ha un link per disiscriversi. Apre una pagina che chiede **Vuoi disiscriverti dalle email di marketing di ReelBolt?**; seleziona **Annulla l'iscrizione**. Non serve accedere e ha effetto subito.

**Cosa registra ReelBolt.** Quando acconsenti, ReelBolt conserva la formulazione esatta a cui hai acconsentito, la data, dove hai acconsentito e l'indirizzo di rete che hai usato, per poter dimostrare che l'hai chiesto tu. Viene registrata anche ogni modifica successiva. Questo registro fa parte del download dei tuoi dati (vedi [Scarica una copia dei tuoi dati](#download-a-copy-of-your-data)) e viene eliminato quando chiudi il tuo account.

## Quando ReelBolt aggiorna l'Informativa sulla privacy {#when-reelbolt-updates-its-privacy-policy}

Quando l'Informativa sulla privacy cambia, ReelBolt te lo comunica nella dashboard, al più tardi al tuo prossimo accesso. Una pagina intitolata **Abbiamo aggiornato la nostra Informativa sulla privacy** riassume cosa è cambiato e rimanda all'informativa; seleziona **Continua** per proseguire. Non ti viene chiesto di accettare nulla, e la casella delle email di marketing in quella pagina è facoltativa.

Quella pagina non ti impedisce mai di uscire, scaricare i tuoi dati o chiudere il tuo account: rimanda a entrambi, e **I tuoi dati** sotto **Impostazioni** resta aperto. Il resto di **Impostazioni** si riapre dopo che hai continuato.

Quando cambiano i Termini di servizio, la pagina ti chiede invece di selezionare una casella per accettare i nuovi termini prima di continuare.

## Conservazione: cosa elimina ReelBolt da solo {#retention-what-reelbolt-deletes-on-its-own}

Il piano su cui si trova il tuo spazio di lavoro decide per quanto tempo si conservano i file generati — di solito 30 giorni nella prova gratuita e nel piano Creator, 90 in Pro e Team. ReelBolt ripulisce automaticamente i render e i file di analisi più vecchi senza avvisarti, perché è questo che significa il piano. **I tuoi upload non vengono mai rimossi da quella pulizia**: solo i file generati da ReelBolt.

Un'altra cosa da sapere: **le esecuzioni e i risultati dei loro passaggi non scadono da soli**. Restano finché non elimini il progetto o l'account. Se vuoi che spariscano, elimina il progetto.

## Correlati {#related}

- [Spazi di lavoro e team](./workspaces.md): spazi personali e del team, ruoli e uscita da un team.
- [Concetti](./concepts.md): cosa sono progetti, file, esecuzioni e risultati.
