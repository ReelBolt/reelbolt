---
title: Notifiche
sidebar_position: 5
description: Le notifiche via email che ReelBolt può inviarti, gli interruttori che attivano ciascuna, chi riceve ogni tipo e come funziona la consegna.
i18n_source_sha: a62670e04bcdd81ceb24c277615b0f030b83d781
---

# Notifiche {#notifications}

ReelBolt può inviarti un'**email quando succede qualcosa in uno spazio di lavoro**: un lavoro video finisce o fallisce, viene aggiunto un file, viene modificata una grafica di miniatura o qualcuno lascia una nota su un link di revisione che hai condiviso. Ogni tipo ha il suo interruttore, così scegli tu quali ricevere. Le modifichi in **Impostazioni → Notifiche** nel menu a sinistra.

## Dove modificarle {#where-to-change-them}

Vai su **Impostazioni → Notifiche** nel menu a sinistra. La pagina ha un interruttore per ogni tipo di email e, in fondo, un elenco **Attività recente** che mostra le notifiche consegnate più di recente.

- **Lavoro completato** — un'email quando un lavoro video termina con successo.
- **Lavoro fallito** — un'email quando un lavoro video fallisce, con cosa è andato storto.
- **File aggiunto** — un'email quando viene aggiunto un file a un progetto.
- **Grafica della miniatura modificata** — un'email quando una miniatura o una grafica di un progetto viene modificata.
- **Attività di revisione** — un'email quando qualcuno lascia una nota o una decisione su un link di revisione che hai condiviso.

Tutte e cinque sono **attive di default**. Disattivare un interruttore interrompe quel tipo di email per te, e la modifica ha effetto subito — non c'è nessun pulsante di salvataggio. Se un interruttore non risponde, la pagina mostra il motivo (per esempio, se non è riuscita a caricare le tue impostazioni), e riattivarlo riporta semplicemente l'interruttore com'era.

## Chi riceve ogni tipo {#who-receives-each-kind}

- **Lavoro completato** e **Lavoro fallito** vanno alla **persona che ha avviato il lavoro**. Se un lavoro è stato avviato dal sistema e non da una persona, va invece al **Proprietario** dello spazio di lavoro, così un errore non va mai perso.
- **Attività di revisione** va alla **persona che ha creato il link di revisione**, e solo se è ancora membro dello spazio di lavoro.
- **File aggiunto** e **Grafica della miniatura modificata** vanno a **tutti i membri dello spazio di lavoro tranne la persona che ha fatto la modifica** — chi ha svolto il lavoro non riceve un'email per la propria azione. Se la modifica è stata fatta dal sistema (non da una persona), raggiunge **tutti** i membri dello spazio di lavoro.
- Una modifica che fai in uno spazio di lavoro in cui sei **l'unico membro** non ti manda nessuna email: chi agisce viene sempre escluso, e se sei l'unico non resta nessuno da avvisare.

## Come funziona la consegna {#how-delivery-works}

Le notifiche vengono inviate con la stessa configurazione email che l'installazione usa già per le email dell'account (reimpostazione della password e inviti). Se quella configurazione email non è impostata, la notifica viene comunque **registrata** e scritta nel log del server invece di essere inviata, così non va mai persa — semplicemente non esce dall'installazione.

Ogni notifica consegnata (o tentata) viene registrata, e le più recenti compaiono nell'elenco **Attività recente** della pagina, con un indicatore che dice se ciascuna è stata consegnata o non è stato possibile inviarla.

**Una notifica non intralcia mai il lavoro.** Un lavoro che finisce, un file che si carica e una grafica che si modifica si completano normalmente anche se l'email non può essere inviata — l'email è un contorno, non parte dell'operazione.
