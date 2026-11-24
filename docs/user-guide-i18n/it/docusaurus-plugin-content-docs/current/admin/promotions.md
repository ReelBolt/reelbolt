---
title: Promozioni e crediti omaggio
sidebar_position: 5
description: Come gli amministratori della piattaforma ReelBolt creano codici promozionali, sconti automatici e bonus sui pacchetti, li mettono in pausa o li terminano, vedono chi li ha usati e regalano crediti gratuiti a uno spazio di lavoro.
i18n_source_sha: c64c471953376c4db49a0544a74011f1ba872012
---

# Promozioni e crediti omaggio {#promotions-and-credit-grants}

Questa pagina riguarda la schermata **Promozioni** (**Piattaforma → Promozioni** nel menu a sinistra, all'indirizzo `/app/admin/promotions`), che appartiene all'amministratore della piattaforma, cioè la persona che gestisce l'intera installazione di ReelBolt. Da qui gestisci offerte valide per tutti gli spazi di lavoro e regali crediti gratuiti a uno solo.

Le promozioni richiedono la fatturazione. In un'installazione senza fatturazione la pagina mostra **La fatturazione non è attiva in questa installazione** e non si può usare nulla.

## I quattro tipi di promozione ReelBolt {#the-four-kinds-of-reelbolt-promotion}

Seleziona **Nuova promozione** e scegli un tipo in alto nel modulo.

| Tipo | Cosa ottiene il cliente | Serve un codice? |
|---|---|---|
| **Codice crediti** | Crediti gratuiti aggiunti allo spazio di lavoro quando qualcuno riscatta il codice in **Piano e fatturazione**. I crediti scadono dopo il numero di giorni che imposti (90 per impostazione predefinita). | Sì |
| **Sconto sul piano** | Un prezzo più basso sull'abbonamento a un piano, per il primo pagamento, per un certo numero di mesi o per ogni pagamento. | Facoltativo |
| **Sconto sul pacchetto** | Un prezzo più basso su un pacchetto di crediti, per quel solo acquisto. | Facoltativo |
| **Bonus sul pacchetto** | Crediti extra in più rispetto a un pacchetto di crediti acquistato, come percentuale del pacchetto. I crediti bonus scadono dodici mesi dopo l'acquisto. | Facoltativo |

**Con codice o automatica.** Una promozione con codice si applica solo quando il cliente digita il codice. Uno sconto o un bonus senza codice è **automatico**: si applica da solo a ogni pagamento che rientra nei criteri, e la tabella lo contrassegna come **Automatica**. I clienti vedono un'offerta automatica come un badge con il suo titolo pubblico sui piani e sui pacchetti che copre.

## Creare una promozione ReelBolt {#creating-a-reelbolt-promotion}

1. Seleziona **Nuova promozione** e scegli il tipo.
2. Compila i campi comuni:
   - **Nome**: solo per gli amministratori.
   - **Titolo pubblico**: ciò che vedono i clienti, per esempio "Saldi di primavera: 20% di sconto". Mantienilo fedele all'offerta.
   - **Codice**: lettere e cifre, da 4 a 32 caratteri; maiuscole e minuscole sono indifferenti. Obbligatorio per un codice crediti. Lascialo vuoto per rendere automatico uno sconto o un bonus.
   - **Inizio** e **Fine**: vuoto significa che parte subito e non finisce mai.
   - **Riscatti totali** (vuoto significa illimitati) e **Per spazio di lavoro** (quante volte può usarla un singolo spazio di lavoro, 1 per impostazione predefinita). Il limite **Per spazio di lavoro** vale anche per ogni persona in tutti i suoi spazi di lavoro, quindi creare un altro spazio di lavoro non dà un altro utilizzo.
3. Compila i campi del tipo scelto:
   - **Codice crediti:** **Crediti** (da 1 a 100.000) e **I crediti scadono dopo (giorni)** (da 1 a 730).
   - **Sconto sul piano:** **Percentuale** (**Percentuale di sconto**, da 0,01 a 100) oppure **Importo fisso (USD)** (**Importo dello sconto (centesimi di dollaro)**: 500 sono cinque dollari). Poi la durata, cioè **Primo pagamento**, **Più mesi** (con **Mesi**) o **Per sempre**, e i **Piani** che copre.
   - **Sconto sul pacchetto:** gli stessi campi per l'importo e i **Pacchetti di crediti** che copre. Copre sempre un solo pagamento.
   - **Bonus sul pacchetto:** **Bonus (percentuale dei crediti del pacchetto)**, da 1 a 100, e i **Pacchetti di crediti** che copre.
   - Lascia vuoto **Piani** o **Pacchetti di crediti** per coprire tutto ciò che è in vendita.
4. Seleziona **Crea promozione**.

**Perché una promozione può essere rifiutata.**

- **Un'altra promozione usa già quel codice.** I codici sono unici: scegline un altro.
- **Una promozione automatica copre già questi piani o pacchetti.** Due offerte automatiche dello stesso tipo non possono sovrapporsi nel tempo sullo stesso piano o pacchetto, perché a un pagamento se ne può applicare una sola. Metti in pausa l'altra o restringi i piani o i pacchetti.
- **Un piano o un pacchetto scelto non può essere scontato**, perché non è in vendita presso il provider di pagamento.
- **Il provider di pagamento ha rifiutato la richiesta.** Gli sconti vengono creati prima presso il provider di pagamento; se rifiuta, non viene salvato nulla. Riprova, oppure controlla la configurazione della fatturazione.

## Modificare, mettere in pausa e terminare una promozione ReelBolt {#changing-pausing-and-ending-a-reelbolt-promotion}

Ogni riga della tabella ha un menu di azioni.

- **Modifica** cambia il **Nome**, il **Titolo pubblico** e la data di fine (**Fine**, oppure **Nessuna data di fine**). Dopo la creazione non si può cambiare nient'altro: né il codice, né l'importo, né la durata, né i piani e i pacchetti coperti. Per cambiarli, archivia la promozione e creane una nuova.
- **Pausa** ferma subito la promozione; **Attiva** la riattiva. La riattivazione viene rifiutata se ora si sovrapporrebbe a un'altra promozione automatica.
- **Archivia** la disattiva e la contrassegna come ritirata. Una promozione archiviata può comunque essere riattivata con **Attiva**.

Una modifica allo stato o alla data di fine di uno sconto arriva prima al provider di pagamento e viene salvata in ReelBolt solo quando il provider la accetta.

Un bonus promesso al momento del pagamento viene comunque concesso quando il pagamento si completa, anche se nel frattempo hai messo in pausa o terminato la promozione: il cliente l'aveva vista quando ha pagato.

## Cosa succede a uno sconto sul piano quando il cliente cambia piano {#what-happens-to-a-plan-discount-when-a-customer-changes-plan}

Uno sconto sul piano che dura più di un pagamento (**Più mesi** o **Per sempre**) segue l'abbonamento quando il cliente cambia piano:

- **Verso un piano coperto dalla promozione**, lo sconto resta, per i pagamenti che gli rimangono.
- **Verso un piano non coperto dalla promozione**, lo sconto termina subito, nella stessa modifica. Il cliente viene avvisato prima di confermare, per esempio con **La tua promozione del 20% termina con questa modifica.**
- **Non torna.** Se il cliente è passato a un piano più piccolo che ha fatto terminare lo sconto e poi seleziona **Mantieni {current plan}**, resta sul suo piano al prezzo normale.

Una promozione con **Piani** lasciato vuoto copre tutti i piani in vendita al momento della creazione, quindi il suo sconto resta se si passa a uno qualsiasi di quei piani. Uno sconto che copriva solo il primo pagamento è già esaurito e non è interessato.

## Vedere chi ha usato una promozione ReelBolt {#seeing-who-used-a-reelbolt-promotion}

Seleziona **Riscatti** nel menu di azioni di una riga. Il pannello elenca gli ultimi 500 utilizzi, ciascuno con il momento in cui è avvenuto, lo spazio di lavoro, cosa è successo (**Codice riscattato**, **Sconto applicato** o **Bonus concesso**), i crediti dati, l'importo dello sconto e la transazione del provider di pagamento.

Il pannello elenca anche i pagamenti in cui l'offerta non è stata concessa. Mostrano un badge rosso **Non concessa** con un breve motivo, per esempio che la persona o lo spazio di lavoro aveva già usato l'offerta o che il pagamento è stato sostituito da uno più recente. Gli utilizzi concessi appena oltre un limite, perché il pagamento era stato avviato prima che il limite si esaurisse, mostrano un badge arancione **Oltre il limite**. Uno sconto sul piano terminato perché il cliente è passato a un piano non coperto dalla promozione mostra **Terminato al cambio di piano** con la data. Per controllare cosa ha ricevuto uno spazio di lavoro, apri uno dei suoi membri in **Piattaforma → Utenti**: la scheda **Organizzazioni e crediti** mostra il saldo di ogni spazio di lavoro.

La colonna **Riscatti** nella tabella conta solo gli utilizzi concessi: codici riscattati, sconti applicati e bonus concessi. Uno sconto o un bonus viene conteggiato quando il pagamento si completa. I nuovi pagamenti si fermano ai limiti **Riscatti totali** e **Per spazio di lavoro**.

**Quando un cliente paga dopo che un limite è stato raggiunto.** Un pagamento tiene riservata la sua offerta per un'ora, quindi un cliente può ancora pagare dopo che il limite è stato raggiunto da qualcun altro. Il cliente ottiene l'offerta, e conta come un utilizzo oltre il limite, solo se sono vere tutte queste condizioni:

- Il suo pagamento è stato aperto quando la promozione era ancora entro tutti i limiti.
- Nessun pagamento più recente per la stessa offerta, della stessa persona o dello stesso spazio di lavoro, lo ha sostituito, e ha pagato entro l'ora.
- Né la persona né lo spazio di lavoro hanno già ottenuto un utilizzo oltre il limite di questa promozione.
- La promozione non ha già superato i suoi limiti troppe volte: al massimo 5 utilizzi extra e, per **Riscatti totali** inferiori a 500, un utilizzo extra ogni 100 completi (almeno uno).

In caso contrario non vengono aggiunti crediti bonus. Uno sconto che il provider di pagamento ha già tolto dal prezzo resta al cliente, ma non conta come utilizzo. Quando un cliente apre un nuovo pagamento per la stessa offerta, o ne lascia uno non pagato per un'ora, ReelBolt annulla il vecchio presso il provider di pagamento così che non si possa più pagare. Se un cliente segnala un bonus mancante, controlla il saldo dello spazio di lavoro e usa **Assegna crediti** per compensare.

Solo i clienti con l'indirizzo email verificato possono usare una promozione. A un cliente che non l'ha verificato viene chiesto di farlo, e un'offerta senza codice non viene applicata al suo pagamento.

## Regalare crediti gratuiti a uno spazio di lavoro ReelBolt {#granting-free-credits-to-a-reelbolt-workspace}

Puoi dare crediti promozionali a uno spazio di lavoro a mano, per esempio per rimediare a un problema.

1. Trova lo spazio di lavoro. Nella pagina **Promozioni**, apri la scheda **Assegna crediti** e cerca per nome dello spazio di lavoro o email del proprietario. Oppure apri una persona in **Piattaforma → Utenti**: la scheda **Organizzazioni e crediti** elenca gli spazi di lavoro a cui appartiene, con i relativi saldi.
2. Seleziona **Assegna crediti** sulla riga dello spazio di lavoro.
3. Inserisci i **Crediti** (da 1 a 1.000.000), la data **Scade il** (tra domani e tre anni da oggi) e un **Motivo**. Il motivo è obbligatorio e viene conservato con l'assegnazione.
4. Seleziona **Assegna crediti**.

I crediti vengono aggiunti subito al saldo dello spazio di lavoro, come crediti promozionali con quella scadenza. Ogni assegnazione registra chi l'ha fatta e perché; seleziona la riga di uno spazio di lavoro per vedere le assegnazioni recenti. Se invii per errore due volte lo stesso modulo, l'assegnazione avviene una volta sola.

## Cosa viene comunicato ai clienti sui crediti promozionali {#what-customers-are-told-about-promo-credits}

Quando uno spazio di lavoro riscatta un codice crediti, ReelBolt mostra i crediti aggiunti, la data di scadenza e il fatto che i crediti promozionali non hanno valore in denaro e non possono essere trasferiti. La pagina **Piano e fatturazione** dello spazio di lavoro elenca ogni lotto di crediti con la sua scadenza sotto **Crediti per scadenza**. Per ciò che vedono i clienti, leggi [Piani e crediti](../account/plans-and-credits.md).
