---
title: Sincronizzare i file da GitHub
sidebar_position: 1
description: Collegare un progetto a un repository GitHub - collegare il tuo account GitHub, scegliere il repository, il branch e quali file e cartelle sincronizzare, cosa mostra la vista collegata, Sincronizza ora (che viene eseguita in background), cambiare i file, scollegare e cosa significa ogni messaggio.
i18n_source_sha: 993b265b8341465e05bee49c9f0139a26ed126f3
---

# Sincronizzare i file da GitHub {#syncing-files-from-github}

Un progetto può essere collegato a uno dei tuoi repository GitHub. I file che scegli vengono copiati nel progetto e, ogni volta che qualcuno fa un push sul branch che hai selezionato, vengono aggiornati automaticamente. Questa pagina spiega come collegare il tuo account GitHub, scegliere il repository, il branch e i file, leggere la vista collegata, sincronizzare su richiesta, cambiare quali file vengono sincronizzati, scollegare e cosa significano i messaggi nella scheda **Repository**.

## Cosa fa il collegamento di un repository GitHub {#what-linking-a-github-repository-does}

Collegare un repository porta i suoi file nel tuo progetto e li mantiene aggiornati. Scegli un repository, un branch e i file o le cartelle che vuoi; ReelBolt li copia nei **File** del progetto e da quel momento ogni push su quel branch li aggiorna: i file nuovi arrivano, i file modificati vengono aggiornati e i file eliminati nel repository (o che smetti di scegliere) vengono rimossi. ReelBolt **legge soltanto** il tuo repository: non cambia mai nulla su GitHub. Non tocca nemmeno mai i file che tu o un agente avete aggiunto al progetto, anche se uno ha lo stesso nome di un file nel repository. Un progetto può essere collegato a un repository, e un repository a un progetto.

## Collegare il tuo account GitHub {#connecting-your-github-account}

Apri il progetto, vai alla scheda **Repository** e scegli **Collega l'account GitHub**. GitHub ti chiede di accedere (se non l'hai già fatto) e di approvare ReelBolt; poi torni subito alla scheda. Non c'è nessuna password o token da copiare da nessuna parte. Colleghi una volta per spazio di lavoro, e puoi disconnettere dalla stessa scheda o da **Impostazioni → GitHub**, oppure rimuovere in qualsiasi momento l'accesso di ReelBolt dalle impostazioni di GitHub. Collegare, esplorare i file di un repository, cambiare quali file vengono sincronizzati e **Sincronizza ora** richiedono tutti il tuo account collegato: ReelBolt mostra e sincronizza solo i repository che il tuo account GitHub può aprire. Se la scheda dice che il collegamento di repository GitHub "non è ancora configurato su questo server", il tuo amministratore deve prima attivare la funzione.

## Scegliere il repository {#choosing-the-repository}

Dopo il collegamento, la scheda elenca i repository che ReelBolt può leggere, raggruppati per account o organizzazione GitHub di appartenenza; scrivi in **Cerca un repository** per restringere l'elenco. Un repository già collegato a un altro progetto nello stesso spazio di lavoro viene mostrato ma non si può scegliere. **Se il repository che vuoi non c'è**, a ReelBolt non è stato dato accesso su GitHub: scegli **Dai a ReelBolt l'accesso ad altri repository su GitHub**, seleziona i repository nella pagina GitHub che si apre (tutti, oppure solo alcuni), poi torna e scegli **Ricarica l'elenco**. Dove un account condivide solo alcuni repository, un link **Cambia quali su GitHub** ti porta alla stessa scelta per quell'account.

## Scegliere il branch e i file da sincronizzare {#choosing-the-branch-and-the-files-to-sync}

Una volta scelto un repository, seleziona il **Branch** da seguire (il branch predefinito è elencato per primo e contrassegnato "predefinito"). Sotto, i file del branch sono mostrati come un albero con una casella di controllo su ogni file e cartella:

- Spuntare una **cartella** spunta tutto ciò che contiene, e una cartella spuntata porta dentro anche i file aggiunti in seguito. Una cartella mostra un trattino quando solo alcuni dei suoi file sono spuntati; cliccandola si spunta l'intera cartella.
- **Cerca file e cartelle** restringe l'albero a ciò che corrisponde. Con una ricerca attiva, **Seleziona N corrispondenze** e **Deseleziona i risultati** agiscono solo su ciò che la ricerca mostra; senza ricerca, **Seleziona tutto** e **Deseleziona** agiscono su tutto.
- Sotto l'albero, un conteggio mostra quanti file hai scelto, quanto pesano in totale e il limite che un progetto può sincronizzare. Se la scelta supera un limite, il motivo viene mostrato con i numeri e il pulsante **Collega** resta disattivato finché non deselezioni abbastanza: l'output di build, i test, la documentazione o le riprese grezze sono di solito le prime cose da lasciare fuori.
- Le cartelle chiamate `node_modules` e `.git` non vengono mai sincronizzate, e un singolo file più grande del limite per file viene saltato (il conteggio ti dice quanti). I file vuoti, come i file `.gitkeep` usati spesso per mantenere una cartella vuota, vengono copiati come qualsiasi altro file.

Un nuovo collegamento parte con tutto spuntato quando l'intero repository rientra nei limiti, e con niente spuntato quando non ci rientra, così scegli tu. **Collega e sincronizza** collega subito il repository e avvia la copia dei file scelti in background; la scheda mostra la vista collegata con **Sincronizzazione…** finché la copia non è terminata, poi ti dice quanti file sono arrivati. Una scelta ampia può richiedere alcuni minuti, e nel frattempo puoi lasciare la pagina. Se quella prima copia non riesce a terminare (per esempio perché GitHub è brevemente non disponibile), il repository resta comunque collegato e la vista collegata mostra il perché.

## Leggere la vista collegata {#reading-the-linked-view}

Una volta collegato, la scheda **Repository** mostra il nome del repository (un link ad esso su GitHub) e uno stato: **Sincronizzazione…** mentre i file vengono copiati, poi **Sincronizzato**, **Ultima sincronizzazione non riuscita** o **Non ancora sincronizzato**. Sotto vedi:

- **Branch**: il branch seguito, con il link ai suoi file su GitHub.
- **File inclusi**: i file e le cartelle che hai scelto, oppure "Tutti i file del repository".
- **Ultima sincronizzazione**: quanto tempo fa è terminata l'ultima sincronizzazione riuscita e il commit che ha copiato, con il link a quel commit su GitHub.
- **File in questo progetto**: quanti file il collegamento mantiene nel progetto.
- **Collegato**: quando il repository è stato collegato.

L'intestazione del progetto mostra anche una piccola etichetta **Collegato a GitHub** su ogni scheda; diventa rossa come **Sincronizzazione GitHub non riuscita** quando l'ultima sincronizzazione non è terminata, e cliccandola si apre la scheda **Repository**.

## Sincronizzare ora e cambiare i file {#syncing-now-and-changing-the-files}

**Sincronizza ora** copia il branch così com'è in questo momento, senza aspettare un push. La copia viene eseguita in background: il pulsante e lo stato diventano **Sincronizzazione…**, la scheda controlla ogni pochi secondi e, quando la sincronizzazione è terminata, un messaggio ti dice quanti file ha ora il progetto provenienti dal repository, oppure, se non è terminata, il perché. Puoi lasciare la pagina mentre è in corso; la sincronizzazione prosegue. Se una sincronizzazione è già in corso, **Sincronizza ora** aspetta semplicemente quella. Per evitare di ripetere lo stesso lavoro, un progetto può essere sincronizzato una volta ogni 20 secondi; chiedere prima ti dice quanto aspettare.

**Cambia file** apre lo stesso albero con la tua scelta attuale spuntata e ti permette di cambiare anche il branch; **Salva e sincronizza** salva la nuova scelta e avvia una sincronizzazione in background, così i file che hai deselezionato escono dal progetto e quelli appena selezionati arrivano. Salvare una scelta uguale a quella di prima non cambia nulla.

Una sincronizzazione conserva i progressi man mano che procede. Se viene interrotta a metà (il server si riavvia o GitHub smette di rispondere), i file copiati fino a quel momento restano nel progetto e la sincronizzazione successiva riparte da lì invece di ricominciare. I file vengono rimossi dal progetto solo dopo che una sincronizzazione ha percorso l'intera scelta.

## Quando una sincronizzazione non termina {#when-a-sync-does-not-finish}

Se una sincronizzazione fallisce, la scheda mostra **L'ultima sincronizzazione non è terminata** con il motivo in parole semplici, e il progetto mantiene i file dell'ultima sincronizzazione riuscita, più eventuali file che la sincronizzazione fallita aveva già copiato: non viene eliminato nulla. Un singolo file che GitHub non consegna non ferma una sincronizzazione: viene saltato e gli altri vengono copiati. I motivi più comuni:

- **Sono stati scelti troppi file** (o **troppo grandi per essere sincronizzati insieme**): i file scelti superano ciò che un progetto può sincronizzare; il messaggio dà entrambi i numeri. Scegli **Scegli meno file** e deseleziona le cartelle che non ti servono.
- **Impossibile trovare il branch**: è stato rinominato o eliminato su GitHub; scegli un altro branch in **Cambia file**.
- **GitHub ha rifiutato l'accesso a ReelBolt**: l'accesso di ReelBolt al repository è stato rimosso su GitHub; dagli di nuovo l'accesso, poi sincronizza.
- **GitHub ha avuto un problema nel rispondere**: di solito è temporaneo; riprova la sincronizzazione tra qualche minuto.
- **ReelBolt non è riuscito a raggiungere GitHub**: la connessione è fallita o ha richiesto troppo tempo; riprova tra qualche minuto.
- **Un problema dentro ReelBolt, non su GitHub**: qualcosa è andato storto lato ReelBolt. Riprova tra qualche minuto e contatta l'assistenza se continua a succedere.
- **Collega prima il tuo account GitHub**: la tua connessione GitHub in questo spazio di lavoro manca o ha smesso di funzionare; il messaggio offre **Collega l'account GitHub**.

Anche un push successivo sul branch ripara il progetto da solo, una volta risolta la causa.

## Scollegare un repository {#unlinking-a-repository}

**Scollega repository** (in fondo alla scheda) chiede conferma e poi interrompe il collegamento: i push non aggiornano più il progetto. I file che il repository ha portato **restano nel progetto**: non viene eliminato nulla. Puoi poi collegare di nuovo lo stesso repository o un altro. Disconnettere il tuo account GitHub non scollega nulla; significa solo che non puoi cambiare il collegamento né sincronizzare su richiesta finché non ti colleghi di nuovo.
