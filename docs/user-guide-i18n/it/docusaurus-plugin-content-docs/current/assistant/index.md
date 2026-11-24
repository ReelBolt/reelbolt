---
title: L'assistente
sidebar_position: 1
description: Come usare l'assistente chat integrato di ReelBolt per capire, costruire, modificare ed eseguire i tuoi progetti e flussi di lavoro, e come le schede di approvazione e l'interruttore Chiedi prima di modificare ti lasciano il controllo.
i18n_source_sha: 9adaf9391bc5ee3fc7a3c9a35438ec4f1ae67ba6
---

# L'assistente {#the-assistant}

ReelBolt ha un assistente chat integrato che conosce la piattaforma. Puoi fargli domande in linguaggio semplice ("perché è fallita la mia ultima esecuzione?") e puoi chiedergli di fare cose al posto tuo ("aggiungi un passaggio di revisione alla fine", "crea un progetto chiamato Spring Teaser"). Per impostazione predefinita chiede prima di cambiare qualsiasi cosa: una scheda nella chat dice cosa sta per fare, e non succede nulla finché non premi **Consenti**.

## Dove trovare l'assistente ReelBolt {#where-to-find-the-reelbolt-assistant}

Il pulsante dell'assistente, un'icona a scintilla con il suggerimento **Assistente**, si trova nella barra in alto di ogni pagina della dashboard. Cliccalo per aprire il pannello della chat; cliccalo di nuovo per chiuderlo. Su un computer il pannello si apre sul lato destro dello schermo; su un telefono sale dal basso.

In alto nel pannello c'è un interruttore, **Chiedi prima di modificare**. È attivo per impostazione predefinita, ed è ciò che fa sì che l'assistente chieda la tua approvazione prima di cambiare qualsiasi cosa; vedi [Come l'assistente ReelBolt chiede la tua approvazione](#how-the-reelbolt-assistant-asks-for-your-approval) e [Lasciare che l'assistente agisca senza chiedere](#letting-the-assistant-act-without-asking-auto-mode). Scrivi in **Scrivi all'assistente…** e premi **Invio** per inviare (**Maiusc** + **Invio** va a capo). Mentre una risposta viene scritta puoi cliccare **Interrompi la generazione**. Sotto ogni risposta puoi copiarla o chiederne una nuova versione con **Rigenera la risposta**.

Molte pagine hanno anche pulsanti di scorciatoia che aprono l'assistente con una domanda già pronta, per esempio **Perché è fallita la mia ultima esecuzione?** o **Suggerisci un flusso di lavoro per questi file** nella scheda **Panoramica** di un progetto, **Cosa dovrei caricare?** in una scheda **File** vuota e **Chiedi all'assistente** nella pagina di calibrazione. La pagina del flusso di lavoro ha il proprio pulsante **Assistente**, che apre lo stesso assistente concentrato su quel flusso di lavoro.

## Cosa sa l'assistente ReelBolt della pagina in cui ti trovi {#what-the-reelbolt-assistant-knows-about-the-page-you-are-on}

L'assistente sa chi sei e quale pagina hai aperto. Se stai guardando un progetto, un flusso di lavoro o un'esecuzione, considera quello come significato di "questo progetto" o "questa esecuzione", quindi puoi dire "spiegami questo flusso di lavoro" senza nominarlo. Nell'editor delle miniature sa quale immagine hai aperto, quindi "ingrandisci il titolo" significa il titolo di quell'immagine.

Non serve aprire prima un progetto. Da qualsiasi pagina, compresa la dashboard, puoi nominare un progetto del tuo spazio di lavoro ("elenca i file in Spring Teaser", "esegui Promo video su Launch Teaser") e l'assistente lavora su quello. Se una richiesta ha bisogno di un progetto e non hai detto quale, te lo chiede.

L'assistente vede solo i progetti del tuo spazio di lavoro che potresti aprire tu stesso. Se non potresti aprire qualcosa tu, non può aprirlo nemmeno l'assistente.

**Parla delle cose per nome.** Nelle risposte e sulle schede l'assistente nomina le cose come fa ReelBolt, come il progetto “Launch Teaser”, il file “notes.txt” o il flusso di lavoro “Highlight edit”, e non mostra mai ID interni. Quando chiedi qualcosa, nominalo anche tu.

## Cosa può consultare l'assistente ReelBolt {#what-the-reelbolt-assistant-can-look-up}

L'assistente può leggere, senza cambiare nulla:

- **I tuoi progetti**: l'elenco dei progetti del tuo spazio di lavoro e i dettagli di uno qualsiasi.
- **I tuoi file**: l'elenco dei file di un progetto (nomi, cartelle, tipi, dimensioni, riepiloghi e stato della ricerca) e il contenuto di testo di un file. Di un PDF legge il testo contenuto (fino alle prime 50 pagine; se un PDF più lungo viene tagliato, l'assistente dice quante pagine ha letto); un PDF scansionato senza testo selezionabile non si può leggere, e l'assistente te lo dice e chiede una versione in testo.
- **I tuoi flussi di lavoro**: i flussi di lavoro di un progetto e la configurazione completa di ogni passaggio di un flusso di lavoro.
- **Il catalogo dei modelli**: i modelli di flusso di lavoro integrati (ricette), a cosa serve ciascuno, di cosa ha bisogno e se il tuo progetto ce l'ha, e i loro passaggi pronti all'uso, su cui può basare un nuovo flusso di lavoro.
- **Cosa ha il tuo progetto**: quante clip, file audio, immagini e documenti contiene, se ha un design del brand approvato, quali voci puoi usare, quali servizi AI sono collegati e cosa consente il tuo piano. L'assistente può leggere tutto questo da solo, quindi non ha bisogno di chiederti ciò che può vedere.
- **Cosa ha usato davvero un'esecuzione**: dopo un'esecuzione ti dice i crediti effettivamente usati, non la stima. Mentre un'esecuzione è ancora in corso dice "finora", e se alcuni crediti sono ancora in fase di regolazione dice che la cifra non è definitiva.
- **Cosa userebbe un'esecuzione**: quando lo chiedi, può dirti più o meno quanti crediti richiede di solito l'esecuzione di un flusso di lavoro o di una ricetta e se usa una voce o inquadrature generate dall'AI.
- **Voci**: le voci narranti di un progetto, e se ciascuna si può usare in un'esecuzione in questo momento. Può anche cercare nella libreria di voci del tuo servizio vocale (Fish Audio) per nome e lingua, così puoi scegliere una voce dalla sua descrizione. Non riproduce campioni di voce; li ascolti nella scheda **Voci** del progetto.
- **Avatar**: gli avatar di un progetto, se ciascuno si può usare e, se no, perché (vedi [Avatar](../projects/avatars.md)). Può solo leggerli: creare, modificare, revocare o eliminare un avatar lo fai tu nella scheda **Avatar** del progetto, perché è lì che si dà il consenso della persona. Se gli avatar non sono attivi nel tuo spazio di lavoro, te lo dice.
- **Momenti in un video**: cosa è stato trovato in un video analizzato (cose che compaiono, si muovono o colpiscono, testo che entra ed esce, movimenti di camera, transizioni, battiti, inizio del parlato) e cosa succede tra due di essi. Legge solo ciò che un'analisi ha già salvato, quindi chiedilo dopo un'esecuzione con l'analisi degli eventi attiva; non dà mai i tempi.
- **Stili di montaggio**: gli stili integrati, i tuoi stili salvati e i formati video in cui si può tagliare un'esecuzione (vedi [Stili di montaggio e formato video](../workflows/editing-styles.md)).
- **Cronologia delle esecuzioni**: le esecuzioni passate di un flusso di lavoro, il loro stato, lo stato e gli errori di ogni passaggio e ciò che hanno prodotto. È ciò che usa per spiegare un'esecuzione fallita.
- **L'immagine che hai aperto nell'editor delle miniature**: il suo canvas e ogni livello (nomi, posizioni, dimensioni, testo e come è stato creato ciascuno) e quali strumenti AI per le immagini ha configurato il tuo spazio di lavoro, con circa quanto costa ciascuno.
- **Agenti e tipi di passaggio**: gli agenti che puoi usare (integrati più i tuoi agenti personalizzati) e le impostazioni che accetta ogni tipo di passaggio. Può anche controllare la presenza di errori in un insieme di passaggi pianificato prima di suggerirlo.
- **Questa documentazione**: l'assistente cerca nella documentazione di ReelBolt per rispondere alle domande su come fare, e recupera anche automaticamente le sezioni più rilevanti per ogni domanda che fai. La ricerca nella documentazione richiede un provider di embedding; vedi [Ricerca semantica](../admin/semantic-search.md).

## Cosa può cambiare per te l'assistente ReelBolt {#what-the-reelbolt-assistant-can-change-for-you}

Con la tua approvazione (o senza chiedere, se hai disattivato **Chiedi prima di modificare**), l'assistente può:

- **Flussi di lavoro**: creare un nuovo flusso di lavoro o sostituire i passaggi di uno esistente, ed eliminare un flusso di lavoro. Può creare un flusso di lavoro da una ricetta già pronta in un solo passaggio, con la lingua, la durata e l'aspetto che hai chiesto e gli extra che hai nominato (voce narrante, sottotitoli, musica, effetti sonori, color grading, titoli). La scheda nomina la ricetta, i passaggi aggiunti e cosa userà un'esecuzione; creare un flusso di lavoro non avvia mai un'esecuzione. Se un extra non può essere aggiunto a quella ricetta, o al progetto mancano i file di cui ha bisogno, l'assistente ti dice perché invece di costruire qualcos'altro.
- **Progetti**: creare un progetto, rinominarlo o cambiarne la descrizione, ed eliminarlo.
- **File**: creare un file di testo o sostituirne il contenuto, ed eliminare un file. Può anche cambiare solo una parte di un file di testo, come un paragrafo o poche righe, e la scheda mostra esattamente quali righe vengono rimosse e aggiunte prima che tu consenta. La modifica sostituisce il contenuto precedente del file e non può essere annullata.
- **Stili di montaggio**: salvare un nuovo stile o modificarne uno tuo.
- **Voci**: aggiungere a un progetto una voce della libreria di voci, così le tue esecuzioni possono usarla come voce narrante. La scheda nomina la voce e il progetto; aggiungere una voce della libreria è gratuito. L'assistente non può creare una voce dalle registrazioni di una persona (una voce clonata): serve il consenso di quella persona, che dai tu nella scheda **Voci** del progetto.
- **Esecuzioni**: fermare un'esecuzione in coda o in corso quando lo chiedi (la scheda dice che l'esecuzione si ferma, il lavoro in corso va perso e i crediti già usati non vengono rimborsati), e avviare l'esecuzione di un flusso di lavoro esistente, facoltativamente con uno stile di montaggio, un formato video e una risoluzione (vedi i limiti sotto).
- **L'immagine aperta nell'editor delle miniature**: aggiungere testo, spostare, ridimensionare, ruotare, nascondere, bloccare, riordinare, rinominare o eliminare livelli, cambiare le parole e il font di un livello di testo, cambiare dimensione e sfondo del canvas, usare uno degli strumenti AI per le immagini (prima ti dice il costo stimato) e far disegnare al designer AI una grafica o modificarne una che ha disegnato. L'editor mostra ogni modifica appena viene fatta e indica il livello cambiato; vedi [Chiedi all'assistente di modificare la tua immagine](../editor/thumbnails.md#ask-the-assistant-to-edit-your-picture).

Gli amministratori possono inoltre chiedergli di elencare, aggiungere, modificare e rimuovere le connessioni ai servizi AI (provider di inferenza), elencare le competenze e cambiare quali competenze ha un agente personalizzato, elencare ogni agente dell'installazione, leggere il rapporto di calibrazione dei modelli decisionali ed eseguire il test di connessione su una connessione a un servizio AI spiegando perché fallisce (lo stesso test del pulsante **Verifica connessione** della pagina). Queste capacità da amministratore vengono controllate ogni singola volta; se non sei un amministratore, l'assistente lo dice e non fa nulla.

## Come l'assistente ReelBolt chiede la tua approvazione {#how-the-reelbolt-assistant-asks-for-your-approval}

Finché **Chiedi prima di modificare** è attivo (il valore predefinito), ogni modifica che l'assistente vuole fare aspetta te. Nella chat compare una **scheda** e non è ancora stato cambiato nulla; la riga sotto il tuo messaggio dice "In attesa del tuo OK". La scheda mostra:

- **Cosa farà l'assistente**, in parole semplici, per esempio "Rinomina il progetto “Launch Teaser”" o "Elimina il file “notes.txt”". Nomina sempre l'elemento esatto.
- **Le modifiche**, elencate, quando c'è più da dire del titolo: cosa verrebbe aggiunto, rimosso o cambiato.
- **Il costo**, su una riga a parte, quando l'azione usa crediti, per esempio "Di solito 8–12 crediti (ne servono 13 disponibili per iniziare; può costare di più)." Le prime cifre sono quanto costa di solito; la cifra "servono" è quanto deve avere disponibile il tuo spazio di lavoro prima di poter iniziare. Se il costo non si è potuto calcolare in anticipo, la scheda lo dice, e l'azione usa comunque crediti. Avviare un'esecuzione e usare uno strumento AI a pagamento per le immagini sono i casi più comuni. Per quanto costa l'assistente in generale, compresa la quota mensile che rende la chat gratuita fino a un certo punto, vedi [Quanto costa l'assistente](../account/what-the-assistant-costs.md).
- **Due pulsanti: Consenti e Non consentire.** Per un'azione che usa crediti, **Consenti** diventa **Consenti e usa i crediti**.

Cosa fa ogni risposta:

- **Consenti** fa esattamente la modifica sulla scheda. La scheda dice poi cosa è stato fatto, o perché non è stato possibile.
- **Non consentire** non cambia nulla. L'assistente lo dice e chiede cosa preferisci; non riprova da solo.
- **Nessuna risposta.** Se non rispondi in tempo, la scheda scade e non viene cambiato nulla. Chiedi di nuovo quando vuoi. Se passi a un'altra app, blocchi il telefono o ricarichi la pagina mentre una scheda è in attesa, la scheda resta aperta: quando torni, l'assistente si riconnette e la mostra di nuovo così puoi ancora rispondere.

Consentire una scheda non dà mai all'assistente più di quanto hai tu. Il tuo ruolo nello spazio di lavoro, il tuo piano, il tuo saldo di crediti e i limiti abituali vengono comunque controllati, e una modifica che non corrisponde più alla realtà (per esempio, il progetto è stato rinominato nel frattempo) viene rifiutata invece che applicata.

Sotto i pulsanti, un piccolo link **Consenti sempre** porta l'assistente in modalità automatica e consente questa azione; vedi la sezione successiva.

Per le **modifiche ai flussi di lavoro**, una modifica non ancora applicata compare anche nella pagina del flusso di lavoro come pannello **Modifiche proposte** che elenca ogni passaggio che verrebbe aggiunto, rimosso o cambiato. Clicca **Applica le modifiche** per accettarla (il flusso di lavoro viene salvato subito; non serve cliccare **Salva modifiche**) oppure **Scarta** per eliminarla. Una modifica già applicata non compare più lì. Se il flusso di lavoro è stato modificato dopo che la modifica era stata proposta, vedi **Proposta non più valida** e non viene cambiato nulla; chiedi di nuovo all'assistente una proposta aggiornata.

Una scheda per **eliminare** o **sostituire** qualcosa nomina l'elemento esatto ("Elimina il file “notes.txt”"), così ciò che consenti non è mai vago.

## Lasciare che l'assistente agisca senza chiedere (modalità automatica) {#letting-the-assistant-act-without-asking-auto-mode}

L'interruttore **Chiedi prima di modificare** nell'intestazione dell'assistente è attivo per impostazione predefinita. Disattivalo e l'assistente lavora in **modalità automatica**: fa le modifiche subito, senza scheda, e l'intestazione mostra **Modalità automatica** accanto all'interruttore. Poiché è un passo importante, disattivare l'interruttore apre una conferma, **Lasciare che l'assistente agisca senza chiedere?**. Spiega cosa significa la modalità automatica: l'assistente fa le modifiche subito, comprese l'eliminazione di cose e l'avvio di esecuzioni a pagamento, senza chiederti prima; vedi comunque nella chat ciò che ha fatto; e puoi tornare indietro in qualsiasi momento. Scegli **Attiva la modalità automatica** per procedere, oppure **Continua a chiedere** per lasciare le cose come sono.

- **Un'eccezione: i costi elevati.** Eliminare qualcosa non richiede una scheda in modalità automatica. Un'esecuzione che costerebbe più del tuo limite di approvazione automatica (25 crediti, a meno che il tuo amministratore l'abbia cambiato; il limite è mostrato sotto l'interruttore) ottiene comunque una scheda che dice perché è comparsa (il limite vale per singola azione: più piccole azioni in una risposta vengono eseguite ciascuna senza scheda) e aspetta il tuo OK. Se il costo non si può calcolare, la scheda lo dice.
- **Vedi comunque tutto ciò che fa.** Ogni azione compare nella chat, dicendo cosa è stato fatto o perché è fallita.
- **Puoi tornare indietro in qualsiasi momento** riattivando **Chiedi prima di modificare**; non serve alcuna conferma. Vale dall'azione immediatamente successiva, anche a metà di una risposta.
- **Il link Consenti sempre su una scheda** apre la stessa conferma. Se scegli **Attiva la modalità automatica**, l'assistente esegue quell'azione e tutte le successive senza chiedere.
- **È un'impostazione del tuo account.** Vale solo per te, non per lo spazio di lavoro né per una singola conversazione, e resta come l'hai lasciata la prossima volta che apri l'assistente.
- **I tuoi limiti non cambiano.** In modalità automatica l'assistente non può comunque fare più di quanto puoi fare tu, e valgono ancora il tuo saldo di crediti e il limite orario di esecuzioni.
- **Claude Code è separato.** Se hai [collegato Claude Code a ReelBolt](./claude-code.md), ti chiede a modo suo e questo interruttore non lo influenza.

## Eseguire flussi di lavoro tramite l'assistente ReelBolt {#running-workflows-through-the-reelbolt-assistant}

Puoi chiedere all'assistente di eseguire un flusso di lavoro ("esegui Promo video from your code su questo progetto"), e di eseguirlo con uno stile, un formato e una risoluzione ("come video cinematografico 16:9", "nel mio stile Launch reel, verticale, in 4K"); prima di iniziare ti dice quale stile, formato e risoluzione userà. La risoluzione può essere Auto, 720p, 1080p o 4K, fino al massimo consentito dal tuo piano; se ne chiedi una superiore a quella che il tuo piano consente, l'assistente ti dice il limite del piano invece di avviare l'esecuzione (vedi [Risoluzione di esportazione in ogni piano ReelBolt](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan)). Un'esecuzione usa crediti, quindi la sua scheda mostra il costo stimato e il suo pulsante dice **Consenti e usa i crediti**. Valgono due regole:

- **Clip video a pagamento.** Se il flusso di lavoro genera clip video con un servizio a pagamento, l'assistente deve prima chiederti se riutilizzare le clip generate in precedenza (gratis) o rigenerarle (costa denaro), e ti dice il massimo che potrebbe costare. Non avvierà l'esecuzione finché non scegli.
- **Limite di esecuzioni.** L'assistente può avviare al massimo 10 esecuzioni all'ora che possono usare crediti (esecuzioni con un modello AI o un servizio a pagamento, e ogni modifica a un video finale) e al massimo 60 esecuzioni gratuite all'ora (solo passaggi di montaggio fissi), per ogni utente in ogni progetto. L'ora è mobile: conta le esecuzioni partite negli ultimi 60 minuti. Un'esecuzione gratuita non consuma mai le 10. Se raggiungi il limite, l'assistente ti dice quante ne hai usate, il limite e quanto aspettare; aspetta quel tempo, oppure avvia tu l'esecuzione con **Avvia** nella pagina del flusso di lavoro, che non è soggetta a questo limite.

## Aspettare la fine di un'esecuzione {#waiting-for-a-run-to-finish}

Un'esecuzione può richiedere del tempo. Invece di farti aspettare, l'assistente ti dice che riprenderà la conversazione quando l'esecuzione finisce. Puoi chiudere la scheda, passare a un'altra pagina o uscire da ReelBolt.

- **Quando torni.** L'assistente ha scritto il suo seguito nella stessa conversazione, come un normale messaggio. Dice se l'esecuzione è riuscita, è fallita o è stata annullata.
- **Notifiche.** Il pulsante dell'assistente nella barra in alto mostra un badge che conta le schede in attesa di una tua risposta più le risposte che non hai ancora letto. Mentre sei in ReelBolt compare un piccolo messaggio: "L'assistente sta scrivendo una risposta...", "L'assistente riprende ... ora che la tua esecuzione è finita" e "L'assistente ha risposto". Aprire la conversazione la segna come letta.
- **Notifiche del browser.** Puoi anche chiedere al tuo browser di avvisarti quando arriva una risposta mentre ReelBolt è in secondo piano. È disattivato per impostazione predefinita. Il browser chiede il permesso solo quando lo attivi, e la notifica mostra solo il titolo della conversazione.
- **Quando serve una scheda mentre sei via.** Con **Chiedi prima di modificare** attivo, l'assistente si ferma alla scheda e ti aspetta, esattamente come se tu stessi guardando. Non viene cambiato nulla finché non rispondi, e la scheda scade se non rispondi in tempo.
- **In modalità automatica** l'assistente prosegue da solo, entro il limite abituale per una richiesta (vedi [Quanto costa l'assistente](../account/what-the-assistant-costs.md)).
- **Limiti.** Per evitare seguiti fuori controllo, l'assistente aspetta solo poche esecuzioni per conversazione alla volta e riprende un numero limitato di volte al giorno. Se un'attesa non è possibile, te lo dice e puoi chiedere di nuovo più tardi.

## Conversazioni nell'assistente ReelBolt {#conversation-threads-in-the-reelbolt-assistant}

Le conversazioni sono salvate sul server, quindi puoi chiudere il pannello, passare a un'altra pagina o tornare il giorno dopo e continuare. Ogni conversazione è un **thread**.

- Un nuovo thread prende il nome dal tuo primo messaggio, poi riceve automaticamente un titolo breve dopo la prima risposta.
- L'elenco **Cronologia** raggruppa i thread in Oggi, Ieri e Precedenti, e offre **Cerca nelle conversazioni** quando ne hai diversi.
- Puoi rinominare o eliminare un thread dall'elenco della cronologia.
- I thread sono privati. Nessun altro, compresi gli amministratori, può aprirli tramite la dashboard.
- **Se esci prima che arrivi la risposta**, l'assistente continua a lavorare per alcuni minuti e salva la risposta nel thread, quindi puoi chiudere il pannello o passare a un'altra pagina. I thread ancora in attesa mostrano **Sto ancora rispondendo…** nella Cronologia, e quando apri l'assistente ti indica una conversazione **senza ancora una risposta**.
- **Se una domanda non ha mai ricevuto risposta** (per esempio la pagina è rimasta chiusa a lungo), il thread mostra **Ancora nessuna risposta** con un pulsante **Chiedi di nuovo**. Chiedere di nuovo non ripete la tua domanda nel thread.

Mentre l'assistente lavora, la riga sotto il tuo messaggio dice cosa sta facendo in quel momento, per esempio "Cerco nella documentazione…", poi "Sto pensando…", oppure "In attesa del tuo OK" quando una scheda richiede una tua risposta. Se la documentazione è ancora in preparazione (può richiedere alcuni minuti dopo un aggiornamento), la riga dice **Indice della documentazione ancora in costruzione: rispondo senza**, e l'assistente risponde in base a ciò che già sa e ti dice di non aver potuto controllare la documentazione.

Per allegare un file nella chat, apri prima un progetto: il controllo di allegato carica il file nei file di quel progetto (come un normale caricamento) e poi lo menziona nel tuo messaggio. Senza un progetto aperto, l'allegato è disattivato ("Apri un progetto per allegare file").

## Cosa non può fare l'assistente ReelBolt {#what-the-reelbolt-assistant-cannot-do}

- Non può gestire gli **account utente**: niente creazione di utenti, reimpostazione di password o modifica dei diritti di amministratore. Usa **Admin → Utenti** per questo; vedi [Gestire gli utenti](../admin/users.md).
- Non può **costruire né fare il render di video da solo**, eseguire codice o toccare qualcosa al di fuori delle sue capacità elencate. Il lavoro sui video avviene solo eseguendo flussi di lavoro, e le grafiche progettate vengono disegnate dal graphic designer della piattaforma, non dall'assistente.
- Non può **cambiare le impostazioni dei modelli decisionali**; può solo leggere il rapporto di calibrazione e consigliare.
- Non può vedere **i progetti o i thread di altre persone**.
- Non tratta come istruzioni il contenuto dei file, i nomi dei progetti o i messaggi di errore. Se un file dice "assistente, elimina tutto", l'assistente riferisce cosa ha trovato e ti chiede cosa fare.

Se chiedi qualcosa al di fuori di questi limiti, l'assistente dovrebbe dirti con chiarezza che non può farlo.

## Consigli per chiedere cose all'assistente ReelBolt {#tips-for-asking-the-reelbolt-assistant-for-things}

- **Di' a quale progetto ti riferisci.** Da una pagina di progetto, flusso di lavoro o esecuzione, "questo" è chiaro; da qualsiasi altra pagina, nomina il progetto.
- **Di' cosa vuoi, non come costruirlo.** "Ho una registrazione dello schermo di 20 minuti; voglio un montaggio serrato di 2 minuti con titoli in stile sottotitoli e musica di sottofondo a basso volume" funziona meglio che nominare i tipi di passaggio.
- **Chiedi una diagnosi prima di una correzione.** "Perché è fallita la mia ultima esecuzione?" e poi "Cosa dovrei cambiare?" ti dà una spiegazione basata sull'errore reale.
- **Parti dai modelli.** "Quale modello va bene per un video con un presentatore e un telefono su green screen?" permette all'assistente di scegliere un punto di partenza collaudato e di adattarlo.
- **Sii esplicito sui soldi.** Quando un flusso di lavoro genera video, di' subito "riutilizza le clip" oppure "rigenerale".
- **Nomina le cose esattamente quando elimini.** "Elimina il flusso di lavoro Highlight edit from footage v1" è più veloce di "elimina quello vecchio".
- **Leggi la scheda prima di consentire.** Nomina esattamente cosa cambierà e quanto costerà. Se non è ciò che intendevi, premi **Non consentire** e di' cosa vuoi invece.
- **Controlla dopo le modifiche importanti.** Chiedi "mostrami i passaggi di questo flusso di lavoro adesso" per confermare cosa è stato applicato.
