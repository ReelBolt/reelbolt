---
title: Spazi di lavoro e team
sidebar_position: 4
description: Cos'è uno spazio di lavoro ReelBolt, la differenza tra lo spazio personale e uno spazio del team, i quattro ruoli dei membri e come invitare persone, cambiare spazio, modificare i ruoli o uscire.
i18n_source_sha: 1e5712fa8c12d243a5376cace558e0b9ea6d5f88
---

# Spazi di lavoro e team {#workspaces-and-teams}

Uno **spazio di lavoro** in ReelBolt è il contenitore condiviso che possiede tutto ciò che fai: i tuoi progetti, i loro file, i tuoi flussi di lavoro, i tuoi render, i tuoi agenti personalizzati, le tue connessioni AI — e la tua fatturazione. Chiunque acceda a ReelBolt lavora in un solo spazio di lavoro alla volta, e il menu degli spazi di lavoro in alto a sinistra ti dice sempre quale.

Questa pagina spiega i due tipi di spazio di lavoro, i quattro ruoli che un membro può avere e le operazioni di tutti i giorni: invitare qualcuno, cambiare spazio, modificare un ruolo e uscire.

## I due tipi di spazio di lavoro {#the-two-kinds-of-workspace}

Ce ne sono due, e la differenza sta in chi ne fa parte.

- **Il tuo spazio di lavoro personale** ti viene dato quando crei l'account. È solo tuo — esattamente un membro, tu, come **Proprietario**. Non si può condividere, non ci si può essere invitati e non si può lasciare, e non si può eliminare. Nel menu degli spazi di lavoro è sempre mostrato come **Personale**, comunque si chiami internamente. Se non devi mai lavorare con altri, è l'unico spazio di lavoro che userai.
- **Uno spazio di lavoro del team** è uno che crei per altre persone. Può avere un numero qualsiasi di membri, ciascuno con uno dei quattro ruoli qui sotto, ed è l'unico tipo di spazio di lavoro che si può condividere, lasciare o eliminare.

In un'installazione cloud ottieni uno spazio di lavoro personale nel momento in cui ti registri; in un'installazione self-hosted è il tuo amministratore a darti un account, e anche quell'account parte con uno spazio di lavoro personale. In ogni caso, lo spazio personale è la casa predefinita.

## Creare uno spazio di lavoro del team {#creating-a-team-workspace}

Chiunque abbia effettuato l'accesso può crearne uno:

1. Apri **Spazio di lavoro → Impostazioni** nel menu a sinistra.
2. Fai clic su **Nuovo spazio di lavoro del team**.
3. Scrivi un **Nome dello spazio di lavoro**, per esempio "Acme Team".
4. Fai clic su **Crea spazio di lavoro**. ReelBolt lo crea, ti rende **Proprietario** e ti ci porta subito dentro.

Puoi far parte di quanti spazi di lavoro del team vuoi — uno per ogni cliente, per dire — e passare dall'uno all'altro in qualsiasi momento.

Se vuoi solo tenere separati i progetti di un cliente da quelli di un altro, non ti serve uno spazio di lavoro per ciascuno. Metti invece su ogni progetto un'etichetta con il nome del cliente (vedi [Organizzare i progetti](./projects/organizing-projects.md)).

## I quattro ruoli dei membri {#the-four-member-roles}

Ogni membro di uno spazio di lavoro del team ha esattamente un ruolo. Il ruolo decide cosa può fare quella persona ed è impostato per ogni spazio di lavoro: essere Proprietario di uno non ti dà nulla in un altro.

| Ruolo | In parole semplici |
|---|---|
| **Proprietario** | Tutto, compreso rinominare ed eliminare lo spazio di lavoro, acquistare il piano e i crediti e gestire la fatturazione. Ogni spazio di lavoro del team mantiene sempre almeno un Proprietario. |
| **Amministratore** | Gestisce lo spazio di lavoro giorno per giorno: membri, inviti, le connessioni AI dello spazio, il collegamento di macchine e runner e l'eliminazione dei progetti. Non può eliminare lo spazio di lavoro, cambiare chi ne è proprietario né acquistare il piano. |
| **Membro** | Fa il lavoro: crea progetti, carica file, costruisce ed esegue flussi di lavoro, usa l'assistente. Non può gestire le persone né le connessioni AI. |
| **Fatturazione** | Vede solo le pagine di fatturazione — il piano, i crediti e gli acquisti. Non può aprire progetti, file, agenti o connessioni AI, e non vede nemmeno l'elenco dei membri. |

Due regole sorprendono le persone, quindi conviene dirle chiaramente:

- **Un Amministratore non può mai creare, rimuovere o declassare un Proprietario.** Può farlo solo un Proprietario. L'ultimo Proprietario di uno spazio di lavoro non può essere rimosso o declassato da nessuno: ReelBolt rifiuta con **"the last owner cannot be removed or demoted"**.
- **L'amministrazione della piattaforma non è un ruolo dello spazio di lavoro, e un ruolo dello spazio di lavoro non è amministrazione della piattaforma.** Chi gestisce l'intera installazione di ReelBolt vede il menu **Piattaforma**, ma questo non gli dà accesso ai progetti del tuo spazio di lavoro — un amministratore della piattaforma che è solo Membro di uno spazio di lavoro non può fare lì più di qualsiasi altro Membro. Al contrario, essere Proprietario del tuo spazio di lavoro non ti rende amministratore della piattaforma. Vedi [Gestione degli utenti](./admin/users.md).

## Acquistare il piano e la fatturazione {#buying-the-plan-and-billing}

Il piano e i crediti di uno spazio di lavoro si gestiscono in **Impostazioni → Piano e fatturazione** nel menu a sinistra, e la stessa pagina mostra il saldo dei crediti e cosa si sta spendendo. Solo il **Proprietario** e i membri con il ruolo **Fatturazione** possono cambiare qualcosa lì; tutti gli altri vedono la pagina in sola lettura. In uno spazio di lavoro del team, il **Proprietario** e i membri **Fatturazione** acquistano il piano, cambiano i posti e aprono il portale di fatturazione; un **Membro** o un **Amministratore** no. Vedi [Piani e crediti](./account/plans-and-credits.md) per sapere quali sono i piani e come funzionano i crediti.

## Invitare persone in uno spazio di lavoro {#inviting-people-to-a-workspace}

Solo un **Proprietario** o un **Amministratore** può invitare qualcuno.

1. Passa nello spazio di lavoro in cui vuoi invitare la persona.
2. Apri **Spazio di lavoro → Membri**.
3. Sotto **Invita qualcuno**, scrivi l'**Email** della persona, scegli un **Ruolo** e fai clic su **Invia invito**.

ReelBolt invia a quell'indirizzo un'email con un link valido per **7 giorni**. Se l'installazione non ha l'email configurata, l'invito viene comunque creato ma l'email non viene inviata, e la pagina lo dice — in quel caso configura l'email (le impostazioni `SMTP_*`), poi revoca l'invito e invialo di nuovo. Invitare qualcuno che è già membro viene rifiutato. Sotto **Inviti in sospeso**, nella stessa pagina, vedi tutti gli inviti non ancora usati e puoi **Revocarli**; un link revocato smette di funzionare subito.

Cosa succede quando si apre il link dipende dal fatto che la persona abbia già un account:

- **Ha già un account ReelBolt.** Il link le chiede di accettare, e accettando entra nello spazio di lavoro e ci passa subito.
- **Non ha ancora un account.** Il link la porta a una pagina di registrazione che crea il suo account a partire dall'indirizzo email invitato. Poiché l'invito dimostra che l'indirizzo è suo, non deve poi confermarlo via email.

Un Amministratore non può assegnare il ruolo di **Proprietario** né invitare qualcuno come Proprietario; solo un Proprietario può fare l'una o l'altra cosa. Un invito è indirizzato a un solo indirizzo email, e solo un account che si registra con quell'esatto indirizzo può usarlo.

## Passare da uno spazio di lavoro all'altro {#switching-between-workspaces}

Usa il menu degli spazi di lavoro in alto a sinistra. Elenca prima il tuo spazio **Personale**, poi tutti gli spazi del team di cui fai parte, e selezionarne uno ti ci porta dentro. ReelBolt ricarica la dashboard per il nuovo spazio di lavoro, e qualsiasi esecuzione in diretta che stavi seguendo si riconnette sotto il nuovo spazio, così non guardi mai i dati di uno spazio mentre il menu ne indica un altro.

Puoi passare solo a uno spazio di lavoro di cui sei membro. Uno spazio di cui non sei membro viene trattato come se non esistesse.

Una sessione del browser porta un solo spazio di lavoro attivo alla volta. Per lavorare in due spazi affiancati, usa un secondo profilo del browser e accedi di nuovo lì. Tutto ciò che accede fuori dal browser — uno script, o [Claude Code collegato a ReelBolt](./assistant/claude-code.md) — riceve una credenziale per uno spazio di lavoro e la mantiene, quindi cambia spazio prima di avviarlo se vuoi che lavori altrove.

## Cambiare il ruolo di un membro e rimuovere un membro {#changing-a-members-role-and-removing-a-member}

In **Spazio di lavoro → Membri**, un Proprietario o un Amministratore può cambiare il **Ruolo** di chiunque dall'elenco e può rimuovere un membro. Un Membro può aprire la stessa pagina e vede chi c'è nello spazio di lavoro, in sola lettura; un utente Fatturazione non vede affatto l'elenco dei membri.

Una modifica si applica **subito** — ReelBolt rilegge il ruolo di un membro dai propri archivi a ogni richiesta, invece di fidarsi del ruolo scritto nel suo accesso, quindi un declassamento o una rimozione ha effetto al clic successivo, non al prossimo accesso. La schermata di un Proprietario si aggiorna da sola quando cambia il proprio ruolo.

Rimuovere qualcuno da uno spazio di lavoro gli toglie l'accesso ai progetti, ai file, ai flussi di lavoro, ai render e alle connessioni AI di quello spazio. **Non** elimina il suo account e non elimina il suo spazio di lavoro personale.

## Uscire da uno spazio di lavoro {#leaving-a-workspace}

Puoi toglierti da qualsiasi spazio di lavoro del team da **Spazio di lavoro → Membri**, rimuovendo te stesso nello stesso modo in cui un Proprietario rimuove un altro (la finestra di conferma dice **Uscire dallo spazio di lavoro?**). Un **Membro** può farlo anche se non può gestire l'appartenenza di nessun altro, quindi non devi mai chiedere a un Proprietario di lasciarti andare. ReelBolt ti sposta poi nel tuo spazio di lavoro personale.

L'uscita viene rifiutata se sei l'**ultimo Proprietario** dello spazio di lavoro — promuovi prima qualcun altro, oppure elimina lo spazio di lavoro. Uno spazio di lavoro personale non si può lasciare affatto.

## Rinominare ed eliminare uno spazio di lavoro {#renaming-and-deleting-a-workspace}

Apri **Spazio di lavoro → Impostazioni**, che agisce sempre sullo spazio di lavoro in cui ti trovi.

- **Rinomina.** Un Proprietario o un Amministratore può cambiare il nome dello spazio di lavoro. Tutti gli altri vedono il nome in sola lettura. Gli spazi di lavoro del team mostrano questo nome ovunque; uno spazio personale è sempre mostrato come **Personale**, quindi il campo per rinominarlo non è disponibile.
- **Elimina.** Solo un **Proprietario** può eliminare uno spazio di lavoro, e solo uno spazio del team. L'eliminazione ha effetto immediato per tutti: lo spazio sparisce dal menu di ogni membro, i suoi progetti non si possono più aprire e l'accesso non torna. Non si può annullare dalla dashboard.

## Connessioni AI dello spazio di lavoro (porta le tue) {#workspace-ai-connections-bring-your-own}

Per impostazione predefinita ogni spazio di lavoro usa le connessioni AI **gestite dalla piattaforma** con cui è stata configurata l'installazione di ReelBolt. I Proprietari e gli Amministratori di uno spazio di lavoro del team possono invece collegare i servizi AI **propri** dello spazio, in **Spazio di lavoro → Provider**:

- Una connessione dello spazio di lavoro si usa solo dentro quello spazio. Il lavoro di nessun altro cambia, e le connessioni dell'installazione restano disponibili per te.
- Se lo spazio di lavoro imposta una delle proprie connessioni come predefinita, quella connessione è preferita a quella predefinita dell'installazione **solo per questo spazio di lavoro**.
- Il modulo offre meno opzioni della pagina della piattaforma, e ReelBolt lo fa rispettare: una connessione dello spazio di lavoro non può usare l'impostazione d'ambiente condivisa per la sua chiave (`env:`), non può usare la credenziale di un abbonamento Claude personale e deve puntare a un indirizzo pubblico.
- Aggiungere, modificare o testare una connessione dello spazio di lavoro richiede che il piano dello spazio includa l'uso di un provider proprio. In caso contrario, il modulo lo dice e spiega come passare a un piano superiore; non si rompe nulla e non viene addebitato nulla. Vedere l'elenco ed eliminare una connessione funzionano sempre — così uno spazio di lavoro che ha perso la funzione può comunque fare ordine.
- Un **Membro** non può aprire la pagina **Provider**, ma può comunque **scegliere** tra le connessioni offerte nelle impostazioni di un passaggio di un flusso di lavoro, perché è una scelta in sola lettura.

Le connessioni gestite dalla piattaforma e quelle dello spazio di lavoro sono descritte insieme in [Provider di inferenza](./admin/inference-providers.md).

## Come risponde l'assistente sugli spazi di lavoro {#how-the-assistant-answers-about-workspaces}

L'[assistente](./assistant/index.md) lavora nello spazio di lavoro in cui ti trovi. Elenca gli agenti personalizzati di quello spazio e — per un Proprietario o un Amministratore — può assegnare loro competenze. Non può creare, rinominare o eliminare spazi di lavoro, invitare persone, cambiare ruoli o rimuovere membri: quelle azioni stanno nelle pagine **Spazio di lavoro** e da nessun'altra parte.

Gli strumenti per i provider dell'assistente agiscono solo sulle connessioni **gestite dalla piattaforma**, quindi un Proprietario o un Amministratore che gli chiede di cambiare le connessioni AI non può mai toccare quelle dello spazio di lavoro.

## Prossimi passi {#next-steps}

- Scopri cos'è un piano e come si spendono e si ricaricano i crediti in [Piani e crediti](./account/plans-and-credits.md).
- Collega i servizi AI propri dello spazio di lavoro in [Porta il tuo provider](./account/bring-your-own-provider.md).
- Collega una macchina tua su cui fare i render in [Runner desktop](./account/desktop-runner.md).
