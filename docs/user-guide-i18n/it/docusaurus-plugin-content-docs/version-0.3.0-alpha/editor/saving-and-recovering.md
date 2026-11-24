---
title: Salvare, recuperare e condividere una timeline
sidebar_position: 4
description: Come l'editor salva il tuo lavoro, cosa significa il messaggio di salvataggio in basso, cosa succede se la connessione cade, se vieni disconnesso o se qualcun altro modifica la stessa timeline, e cosa significa "Puoi guardare ma non modificare".
i18n_source_sha: 09ba6c66b227d6e7cbd29f63b18abd56f5a5b119
---

# Salvare, recuperare e condividere una timeline {#saving-recovering-and-sharing-a-timeline}

L'editor salva per te, un istante dopo ogni modifica. Non c'è nessun pulsante Salva. Il messaggio in basso a sinistra
nell'editor ti dice sempre a che punto è il tuo lavoro.

## Cosa dice il messaggio {#what-the-message-says}

| Cosa vedi | Cosa significa | Cosa fare |
|---|---|---|
| **Salvato** | Tutto quello che hai fatto è stato memorizzato. | Niente. |
| **Modifiche non salvate** / **Salvataggio...** | Una modifica è in arrivo. | Niente; ci vuole un attimo. |
| **Impossibile raggiungere il server. Riprovo...** | Il salvataggio non è andato a buon fine. L'editor continua a riprovare. | Per ora niente. |
| **Sei offline. 2 modifiche verranno salvate quando torni online** | La connessione è assente. Le tue modifiche restano su questo dispositivo. | Continua a lavorare, oppure riconnettiti. Si salvano da sole. |
| **Accedi di nuovo per continuare a salvare** | La sessione è terminata. Non hai perso nulla. | Accedi nella finestra che si apre; la modifica viene salvata subito dopo. Resti dove sei. |
| **Non salvato. Il server ha avuto un problema.** | Il server non è riuscito a memorizzare la modifica. | Premi **Riprova**. |
| **Non salvato. Questa timeline è troppo grande per essere salvata.** | La timeline supera quello che un singolo salvataggio può contenere. | Rimuovi alcune clip o alcuni media, poi premi **Riprova**. |
| **Non salvato. Non hai più il permesso di modificare questa timeline.** | Il tuo accesso è cambiato. | Chiedi al proprietario del progetto. |
| **Non salvato. Questa timeline è stata eliminata.** | Qualcuno l'ha eliminata. | Le tue modifiche restano sullo schermo; copia quello che ti serve. |
| **Non salvato. Il server ha rifiutato questa modifica.** seguito da un motivo | La modifica viola una regola della timeline. Il motivo è scritto a parole semplici. | Annulla la modifica o correggi ciò che indica il motivo, poi premi **Riprova**. |
| **Non salvato. Questa timeline è stata modificata altrove.** | Un'altra scheda, un altro dispositivo o un'altra persona ha salvato per primo. | Scegli cosa tenere (vedi sotto). |
| **Aperta in un'altra scheda** | La timeline è già aperta in un'altra scheda di questo browser. | Usa quella scheda, oppure premi **Modifica qui**. |
| **Sola lettura** | Puoi guardare ma non modificare questa timeline. | Chiedi al proprietario del progetto l'accesso in modifica. |

Su smartphone, **Riprova** e gli altri pulsanti sono abbastanza grandi da essere toccati col pollice.

Se il tuo browser non può conservare nulla sul dispositivo (una finestra privata molto restrittiva), il messaggio aggiunge **Le modifiche sono tenute solo in memoria**: chiudere la scheda prima che la modifica sia salvata la farebbe perdere.

## Se la scheda si chiude prima che l'ultima modifica sia salvata {#if-the-tab-closes-before-your-last-change-was-saved}

L'editor tiene sul tuo dispositivo una copia delle modifiche non salvate. La prossima volta che apri la timeline, ti dice
**Abbiamo recuperato le modifiche dell'ultima visita** con due pulsanti:

- **Recupera** le rimette a posto e le salva. Se nel frattempo qualcun altro ha cambiato la timeline, l'editor unisce le tue
  modifiche alle sue quando non toccano le stesse clip.
- **Scarta** le dimentica.

Non viene scritto nulla finché non scegli.

## Se qualcun altro modifica la stessa timeline {#if-someone-else-edits-the-same-timeline}

Quando la tua modifica e una modifica salvata altrove non toccano le stesse clip, l'editor le unisce senza rumore e mostra
**Le tue modifiche sono state unite a quelle di un'altra scheda o dispositivo.**

Quando entrambi avete cambiato la stessa clip, l'editor si ferma e te lo chiede, e **in ogni caso non si perde nulla**:

- **Tieni la mia copia** salva le tue modifiche come nuova timeline chiamata "*nome* (mie modifiche)" e ti permette di aprirla.
- **Usa la più recente** carica ciò che è salvato adesso. Prima salva le tue modifiche in un file nei tuoi download, così
  potrai guardarle più tardi.

## Due schede, una timeline {#two-tabs-one-timeline}

Se apri una timeline già aperta in un'altra scheda dello stesso browser, la nuova scheda è in sola lettura e dice
**Aperta in un'altra scheda**. Premi **Modifica qui** per prenderne il controllo: l'altra scheda salva quello che ha, diventa
di sola lettura, e questa scheda prosegue da lì.

## Puoi guardare ma non modificare {#you-can-look-but-not-change}

Se il tuo ruolo ti permette di aprire una timeline ma non di modificarla, l'editor la apre in sola lettura con **Puoi guardare ma non
modificare questa timeline.** Niente di ciò che premi può cambiare o salvare qualcosa: i pulsanti di modifica sono disattivati e i tasti
non fanno nulla.
