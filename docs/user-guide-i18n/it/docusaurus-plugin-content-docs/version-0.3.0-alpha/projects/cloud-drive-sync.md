---
title: Sincronizzare i file da Google Drive, OneDrive o Dropbox
sidebar_position: 2
description: Collegare un progetto a una cartella su Google Drive, OneDrive/SharePoint o Dropbox - collegare il tuo account, scegliere la cartella e quali file sincronizzare, Sincronizza ora (che viene eseguita in background), cosa succede ai tuoi file e cosa non succede mai, come arrivano Documenti, Fogli, Presentazioni e Disegni di Google, come scollegare e cosa significa ogni messaggio.
i18n_source_sha: ce9d0ee52b00714e0a33e2ad52ae334bd946d966
---

# Sincronizzare i file da Google Drive, OneDrive o Dropbox {#syncing-files-from-google-drive-onedrive-or-dropbox}

Un progetto può essere collegato a una cartella su Google Drive, OneDrive (compreso SharePoint) o Dropbox. I file che scegli vengono copiati nel progetto e tenuti aggiornati: ReelBolt controlla la cartella a intervalli regolari, e puoi sincronizzare quando vuoi. Questa pagina spiega come collegare il tuo account, scegliere la cartella e i file, sincronizzare, cosa fa una sincronizzazione ai tuoi file, come scollegare e cosa significano i messaggi nella scheda **Archiviazione cloud**.

## Cosa fa il collegamento di una cartella cloud {#what-linking-a-cloud-folder-does}

Collegare una cartella su Google Drive, OneDrive o Dropbox porta i suoi file nel tuo progetto e li mantiene uguali alla cartella. I nuovi file nella cartella arrivano nel progetto, i file modificati vengono aggiornati, e i file eliminati dalla cartella (o che smetti di scegliere) vengono rimossi dal progetto. ReelBolt **legge soltanto** la tua cartella: non aggiunge, modifica, sposta né elimina mai nulla su Google Drive, OneDrive o Dropbox. Non tocca nemmeno mai i file che tu o un agente avete aggiunto al progetto, né i file arrivati da GitHub o da un altro servizio, anche se ne esiste uno con lo stesso nome. Un progetto può essere collegato a una cartella su ciascuno dei tre servizi, insieme a un repository GitHub.

## Collegare il tuo account Google Drive, OneDrive o Dropbox {#connecting-your-google-drive-onedrive-or-dropbox-account}

Apri il progetto e vai alla scheda **Archiviazione cloud**. Ogni servizio che il tuo amministratore ha attivato ha una propria scheda. Scegli **Collega** sulla scheda (per esempio **Collega Google Drive**): il servizio ti chiede di accedere, se non l'hai già fatto, e di consentire a ReelBolt di **vedere** i tuoi file; poi torni subito alla scheda. Non c'è nessuna password o chiave da copiare da nessuna parte. Completa l'accesso nello stesso browser in cui usi ReelBolt: se nel frattempo esci da ReelBolt, il collegamento viene rifiutato e puoi semplicemente ricominciare. Puoi collegare più di un account dello stesso servizio, e ogni collegamento appartiene a te, nello spazio di lavoro corrente. Se non compare alcuna scheda **Archiviazione cloud**, il tuo amministratore non ha ancora attivato la sincronizzazione con quel servizio.

## Scegliere la cartella e i file da sincronizzare {#choosing-the-folder-and-the-files-to-sync}

Dopo il collegamento, la scheda ti chiede di scegliere:

1. **L'account**, se ne hai collegato più di uno.
2. **Dove si trova la cartella**: il tuo spazio personale (Il mio Drive, il tuo OneDrive, il tuo Dropbox) oppure, quando li hai, un drive condiviso, la raccolta documenti di un sito SharePoint o lo spazio del team di Dropbox.
3. **La cartella**: apri le cartelle un livello alla volta e scegli quella da collegare. Scegliere il livello più alto collega tutto ciò che c'è in quello spazio.

Puoi poi restringere la cartella a una selezione di file e sottocartelle: spuntare una sottocartella porta dentro tutto il suo contenuto, compresi i file aggiunti in seguito. Non scegliere nulla in particolare significa tutti i file della cartella. **Collega** collega subito la cartella e avvia la copia dei file in background; la scheda mostra **Sincronizzazione…** finché la copia non è terminata. Una cartella grande può richiedere alcuni minuti, e nel frattempo puoi lasciare la pagina.

## Cosa succede ai tuoi file durante una sincronizzazione cloud {#what-happens-to-your-files-during-a-cloud-sync}

Ogni sincronizzazione fa corrispondere i file del progetto provenienti da quella cartella alla cartella stessa:

- I **file nuovi** nella cartella vengono aggiunti al progetto.
- I **file modificati** vengono aggiornati sul posto, così tutto ciò che li usa continua a funzionare.
- I **file invariati** vengono lasciati stare e non vengono scaricati di nuovo.
- I **file rimossi** dalla cartella, o che smetti di scegliere, vengono rimossi dal progetto, ma solo dopo che una sincronizzazione ha percorso l'intera cartella. Una sincronizzazione che si interrompe a metà non rimuove nulla.
- I **file vuoti** vengono copiati come qualsiasi altro file.
- Il **disordine di sistema** (`.DS_Store`, `Thumbs.db`, `desktop.ini` e i file temporanei `~$` che Office crea mentre un documento è aperto) non viene mai copiato.
- Un file più grande del limite per file, o che diventa non disponibile mentre viene copiato, viene saltato e gli altri vengono comunque copiati.
- Se un file nella cartella ha lo stesso nome e la stessa posizione di un file che hai caricato, che un agente ha creato, o che GitHub o un altro servizio ha portato, il file esistente viene mantenuto e la copia della cartella viene saltata.

Cosa **non** succede mai: ReelBolt non modifica, non sposta e non elimina mai nulla su Google Drive, OneDrive o Dropbox, e i file sincronizzati non vengono riassunti né analizzati automaticamente.

## Come arrivano Documenti, Fogli, Presentazioni e Disegni di Google {#how-google-docs-sheets-slides-and-drawings-arrive}

I documenti nativi di Google vengono convertiti quando sono copiati, e il nuovo tipo di file viene aggiunto al nome: un **Documento** Google arriva come file di testo Markdown (`.md`), un **Foglio** come foglio di calcolo CSV del suo primo foglio (`.csv`), una presentazione **Presentazioni** come PDF (`.pdf`) e un **Disegno** come immagine PNG (`.png`). Quindi un Documento chiamato "Script" compare nel progetto come `Script.md`. Moduli, Sites, My Maps e collegamenti di Google non possono essere copiati e vengono esclusi. Allo stesso modo vengono esclusi i blocchi appunti OneNote e i collegamenti "condivisi con te" su OneDrive, e i documenti Dropbox Paper.

## Sincronizzare ora e tenere aggiornata la cartella {#syncing-now-and-keeping-the-folder-up-to-date}

ReelBolt controlla da solo ogni cartella collegata a intervalli regolari (ogni mezz'ora, a meno che il tuo amministratore abbia scelto diversamente), quindi le modifiche arrivano senza che tu faccia nulla. **Sincronizza ora** copia la cartella così com'è in questo momento. La sincronizzazione viene eseguita in background: la scheda mostra **Sincronizzazione…**, controlla ogni pochi secondi e poi mostra quando è stata eseguita l'ultima sincronizzazione e quanti file ha il progetto provenienti dalla cartella, oppure, se la sincronizzazione non è terminata, il perché. Puoi lasciare la pagina mentre è in corso. Se una sincronizzazione è già in corso, **Sincronizza ora** aspetta quella. Una cartella può essere sincronizzata su richiesta una volta ogni 20 secondi; chiedere prima ti dice di aspettare un attimo.

Una sincronizzazione conserva i progressi man mano che procede: se viene interrotta a metà, i file copiati fino a quel momento restano nel progetto e la sincronizzazione successiva riparte da lì.

## Cambiare quali file vengono sincronizzati {#changing-which-files-are-synced}

**Cambia file** apre la cartella con la tua scelta attuale spuntata. Salvare una nuova scelta avvia una sincronizzazione in background, quindi i file che hai deselezionato escono dal progetto e quelli appena selezionati arrivano. Salvare la stessa scelta di prima non cambia nulla. Per collegare una cartella diversa, scollega prima quella attuale, poi collega la nuova.

## Scollegare una cartella e disconnettere un account {#unlinking-a-folder-and-disconnecting-an-account}

**Scollega** interrompe la sincronizzazione di quella cartella. I file che ha portato **restano nel progetto**: non viene eliminato nulla. Puoi poi collegare di nuovo la stessa cartella o un'altra.

I tuoi account collegati sono elencati in **Impostazioni → Account collegati**, dove **Scollega** ne rimuove uno: ReelBolt elimina il suo accesso e, dove il servizio lo permette, dice al servizio di annullarlo. Le cartelle collegate con quell'account restano collegate e mantengono i loro file, ma smettono di sincronizzarsi finché non le colleghi di nuovo con un account collegato. Puoi anche rimuovere l'accesso di ReelBolt dalle impostazioni del tuo account Google, Microsoft o Dropbox in qualsiasi momento.

## Quando una sincronizzazione cloud non termina {#when-a-cloud-sync-does-not-finish}

Se una sincronizzazione fallisce, la scheda mostra il motivo in parole semplici e il progetto mantiene i suoi file: non viene eliminato nulla. I messaggi più comuni e cosa fare:

- **"… non accetta più l'accesso di ReelBolt …"**: il servizio ha smesso di accettare l'accesso di ReelBolt al tuo account (è stato rimosso, è scaduto o la tua password è cambiata). Scegli **Ricollega**, accedi allo stesso account e sincronizza di nuovo; la cartella resta collegata.
- **"… non può più aprirla …"**: il proprietario della cartella ha smesso di condividerla con il tuo account. Chiedigli di condividerla di nuovo, oppure collega un'altra cartella.
- **"Impossibile trovare la cartella collegata …"**: la cartella è stata eliminata, spostata nel cestino o non è più condivisa con te. Scollegala e collega un'altra cartella.
- **"… sta limitando quante richieste ReelBolt può fare in questo momento"** o **"… ha avuto un problema nel rispondere"**: di solito è temporaneo; riprova tra qualche minuto. ReelBolt riprova anche da solo più tardi.
- **"La cartella collegata contiene più di … file"**, **"Sono stati scelti troppi file"** o **"I file scelti sono troppo grandi per essere sincronizzati insieme"**: la cartella o la tua scelta supera ciò che un progetto può sincronizzare; il messaggio indica i numeri. Scegli **Scegli meno file** e deseleziona ciò che non ti serve, oppure collega una cartella più piccola.
- **"… è stato disconnesso. Collega di nuovo la cartella."**: l'account usato per questa cartella è stato disconnesso. Collega un account e collega di nuovo la cartella.
- **"… non è configurato su questo server"**: il tuo amministratore ha disattivato la sincronizzazione con quel servizio. Contattalo.
- **"La sincronizzazione si è interrotta a causa di un problema lato ReelBolt"**: riprova tra qualche minuto e contatta l'assistenza se continua a succedere.

## Quando il collegamento di un account non funziona {#when-connecting-an-account-does-not-work}

Se torni alla scheda con un messaggio che dice che il collegamento non è stato completato, di solito è una di queste cause:

- **Hai annullato** nella pagina del servizio: scegli di nuovo **Collega** quando sei pronto.
- **L'accesso ha richiesto troppo tempo** (più di dieci minuti) o è già stato usato: ricomincia.
- **Non avevi effettuato l'accesso a ReelBolt** in quel browser, oppure avevi effettuato l'accesso come un'altra persona: accedi a ReelBolt e ricomincia dallo stesso browser.
- **La tua organizzazione richiede l'approvazione di un amministratore** (Microsoft dice "Need admin approval"): chiedi al tuo amministratore IT di approvare l'app, poi collega di nuovo.
- **Google dice che l'app non è verificata o ti blocca**: durante i test possono collegarsi solo le persone in elenco; chiedi al tuo amministratore ReelBolt.
