---
title: Gestire gli utenti
sidebar_position: 1
description: Come gli amministratori di ReelBolt creano account, assegnano password temporanee, reimpostano le password, concedono i diritti di amministratore ed eliminano gli utenti.
i18n_source_sha: b3c4ba2c86c1821c05584e66983bd2947eb282ad
---

# Gestire gli utenti {#managing-users}

Questa pagina riguarda la schermata **Utenti** (**Piattaforma → Utenti** nel menu a sinistra, all'indirizzo `/app/admin/users`), che appartiene all'amministratore della piattaforma, cioè la persona che gestisce l'intera installazione di ReelBolt. Si occupa degli *account*: crearli, reimpostarne le password ed eliminarli.

Non è il posto in cui aggiungi qualcuno a uno spazio di lavoro. Aggiungere un collega ai tuoi progetti è un'azione dello spazio di lavoro, in **Spazio di lavoro → Membri**, ed è descritta in [Spazi di lavoro e team](../workspaces.md). Le due cose sono volutamente separate, come i diritti che le governano: i diritti di amministratore della piattaforma non danno mai accesso allo spazio di lavoro di un'altra persona, e essere Proprietario o Amministratore di uno spazio di lavoro non ti rende mai amministratore della piattaforma.

## Il primo account amministratore di ReelBolt {#the-first-reelbolt-administrator-account}

Quando ReelBolt si avvia per la prima volta senza alcun account, crea automaticamente un account di amministratore della piattaforma. Email e password vengono dalle impostazioni dell'installazione (`ADMIN_EMAIL` e `ADMIN_PASSWORD`; l'email predefinita è `admin@reelbolt.local`). Se non è stata impostata una password, ReelBolt ne genera una casuale e la stampa una sola volta nel log del server del servizio account, così chi ha installato ReelBolt può trovarla lì.

Questo primo account deve cambiare la password al primo accesso, come ogni nuovo account.

## Creare un account utente ReelBolt {#creating-a-reelbolt-user-account}

Questa schermata è il modo in cui compaiono gli account in una installazione **self-hosted**. In un ReelBolt self-hosted è l'unico modo, perché la registrazione autonoma è disattivata e la pagina di registrazione non è raggiungibile. (Un ReelBolt cloud può attivare la registrazione autonoma: in quel caso le persone si registrano da sole dalla pagina di registrazione, vedi [Per iniziare](../getting-started.md); ogni account creato così parte con il proprio spazio di lavoro personale e senza inviti a spazi di lavoro.)

1. Vai in **Piattaforma → Utenti** e clicca **Nuovo utente**.
2. Compila **Email** e **Nome visualizzato** (entrambi obbligatori).
3. Attiva **Amministratore** solo se questa persona deve gestire l'installazione: gestire gli utenti, i servizi AI propri dell'installazione e le altre pagine **Piattaforma**. Non serve per entrare in uno spazio di lavoro.
4. Clicca **Crea**.

A un nuovo account viene assegnato subito uno **spazio di lavoro personale** vuoto, così la persona può iniziare a lavorare da sola senza che nessuno la inviti. Invitarla in uno spazio di lavoro condiviso è un passaggio separato: vedi [Spazi di lavoro e team](../workspaces.md).

Compare quindi una finestra **Utente creato** con una **password temporanea** di 16 caratteri e un pulsante **Copia password**. Se l'installazione ha la posta (SMTP) configurata, la stessa password viene inviata anche via email al nuovo utente in un messaggio "Welcome to ReelBolt". Se la posta non è configurata, questa finestra è l'unico posto in cui compare la password: copiala e consegnala prima di chiuderla.

L'utente accede con la password temporanea e viene subito obbligato a sceglierne una nuova (di almeno 8 caratteri). Finché non lo fa, nell'elenco utenti compare accanto al suo nome il badge **Deve cambiare la password**.

## Reimpostare la password di un utente ReelBolt {#resetting-a-reelbolt-users-password}

1. Apri l'utente (clicca sul suo nome nell'elenco) e clicca **Modifica**.
2. Attiva **Reimposta password (genera una nuova password temporanea)**.
3. Clicca **Aggiorna**.

ReelBolt assegna all'utente una nuova password temporanea e gli chiede di cambiarla al prossimo accesso. **La nuova password viene inviata solo via email**; a differenza di quanto accade alla creazione dell'account, non viene mostrata a schermo. Quindi una reimpostazione funziona per te solo se l'installazione ha la posta configurata. In caso contrario la reimpostazione ha comunque effetto, ma nessuno può vedere la nuova password e l'utente resta bloccato finché la posta non viene configurata. In quel caso chiedi a chi gestisce l'installazione di configurare la posta (le impostazioni `SMTP_*`) prima di reimpostare le password.

## Concedere o togliere i diritti di amministratore in ReelBolt {#granting-or-removing-administrator-rights-in-reelbolt}

Apri l'utente, clicca **Modifica**, attiva o disattiva **Amministratore** e clicca **Aggiorna**. L'elenco utenti mostra per ognuno un badge **Admin** o **User**.

Gli amministratori della piattaforma possono:

- gestire gli utenti in questa schermata,
- aggiungere e modificare le connessioni ai servizi AI propri dell'installazione (vedi [Provider di inferenza](./inference-providers.md)),
- scegliere quale servizio AI usa un determinato agente in tutta l'installazione,
- vedere le pagine **Piattaforma** nel menu a sinistra (**Panoramica**, **Servizio flussi di lavoro**, **Inferenza**, **Fatturazione**, **Calibrazione**, **Competenze**, **Utenti**),
- usare le capacità a livello di piattaforma dell'[assistente](../assistant/index.md).

**I diritti di amministratore non sono diritti sullo spazio di lavoro.** Un amministratore della piattaforma gestisce gli account e i servizi AI dell'installazione; dalla dashboard non vede i progetti, i file, i flussi di lavoro o i render di alcuno spazio di lavoro, e non può aprire, unirsi o modificare uno spazio di lavoro di cui non è membro. Se esegui un flusso di lavoro in uno spazio di lavoro, lì valgono le regole di quello spazio, esattamente come per tutti gli altri membri.

Una modifica dei diritti si applica entro circa un minuto. ReelBolt rilegge i diritti di piattaforma e il ruolo nello spazio di lavoro di ogni persona dai propri archivi a ogni richiesta, invece di fidarsi di quanto è stato scritto nell'accesso, quindi una promozione o una retrocessione non aspetta un nuovo accesso.

## Eliminare un utente ReelBolt {#deleting-a-reelbolt-user}

Apri l'utente e clicca **Elimina**, oppure usa l'azione di eliminazione nell'elenco utenti, poi conferma nella finestra **Elimina utente**. L'eliminazione è permanente e porta con sé molte cose. **Ogni progetto di proprietà di quella persona viene eliminato**, ovunque si trovi, compresi i progetti all'interno di uno spazio di lavoro condiviso del team, insieme ai file, ai flussi di lavoro e ai render di quei progetti. Viene eliminato anche il suo **spazio di lavoro personale**. Gli agenti personalizzati che ha creato nel suo spazio di lavoro personale vanno con esso; gli agenti personalizzati che ha creato in uno spazio di lavoro condiviso vengono conservati, ma non appartengono più a nessuno. Lo spazio di lavoro condiviso in sé e il lavoro degli altri suoi membri non vengono eliminati. Poiché l'eliminazione arriva fino agli spazi di lavoro del team, se i progetti della persona lì devono sopravvivere rimuovila prima da quegli spazi di lavoro: eliminare l'account non è reversibile.

Se vuoi solo impedire a qualcuno di accedere temporaneamente, valuta di reimpostare la sua password invece di eliminarlo.

## Gestire gli utenti ReelBolt tramite l'assistente {#managing-reelbolt-users-through-the-assistant}

L'[assistente](../assistant/index.md) non può creare, modificare o eliminare utenti, né reimpostare password. Usa la schermata **Utenti** per tutta la gestione degli account.
