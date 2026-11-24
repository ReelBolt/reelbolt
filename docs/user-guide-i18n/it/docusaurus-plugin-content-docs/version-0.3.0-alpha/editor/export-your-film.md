---
title: Esporta il tuo film
sidebar_position: 3
description: Come generare il film dalla tua timeline, guardarlo, scaricarlo, trovarlo nei file del progetto, condividerne il link, eliminarlo ed esportare una sola clip.
i18n_source_sha: c2ce9b438a48ed063c705a769f68d74e6a5426c0
---

# Esporta il tuo film {#export-your-film}

Quando la modifica è pronta, apri **Consegna** (il menu **Altro > Esporta** dell'editor la apre). L'etichetta **Anteprima anticipata** dell'editor resta anche in questa pagina, come ovunque nell'editor.

## Prima di tutto il tuo film {#your-film-comes-first}

In cima a Consegna trovi il tuo **ultimo film finito**: un player, il nome del file, la durata, la dimensione del file e quanto tempo fa è stato creato. Sotto il player:

- **Scarica** salva il film sul tuo computer con il suo nome.
- **Apri nei file del progetto** ti porta alla pagina File del progetto con il film selezionato.
- **Copia il link** copia l'indirizzo del film nel tuo progetto. Le persone che hanno accesso al progetto possono aprirlo. Non è un link pubblico.
- **Elimina** chiede conferma, poi rimuove il film dai file del progetto. La timeline non cambia.

Sotto i pulsanti, una riga ti dice se il film contiene le tue ultime modifiche: "Ultimo render: la tua ultima modifica", oppure "Ultimo render: prima delle tue ultime modifiche (3 modifiche da allora)".

## Scegli un formato e premi Render {#choose-a-format-and-press-render}

- **Formato** è la forma del film: Full HD orizzontale, 4K orizzontale, Verticale per smartphone, Quadrato, Verticale 4:5, Master (grande, per modificarlo altrove) o Solo audio. Se la forma non corrisponde alla tua timeline, ti viene detto che l'immagine sarà ritagliata o avrà delle bande.
- **Qualità** è Bozza (la più veloce e leggera), Buona o Massima (la più nitida, più lenta, file più grande).
- **Solo l'intervallo in/out segnato** esporta soltanto la parte compresa tra i tuoi punti di entrata e di uscita.
- **Avanzate** contiene la normalizzazione del volume e le scelte su codec e velocità, per chi ne ha bisogno.

**Render** salva prima le tue ultime modifiche, così il film contiene sempre ciò che vedi sullo schermo. Se il salvataggio non riesce, Render resta disattivato e mostra "Prima salva:" con il motivo, e da lì puoi riprovare a salvare.

## Quanto costa {#what-it-costs}

Se il tuo piano usa i crediti, la riga accanto a Render dice "Circa 12 crediti, circa 3 min" prima che tu cominci. Se non ne hai abbastanza, indica quanti te ne servono ancora e propone **Aggiungi crediti**. Se i crediti non fanno parte del tuo piano, non viene mostrato nulla.

## Esporta una sola clip {#export-only-one-clip}

Seleziona una clip, apri Consegna e premi **Esporta solo "nome della clip"**. Con un solo clic l'intervallo viene impostato su quella clip e il render parte. Mentre lavora, il pannello dice "Render di "nome della clip" in corso". I tuoi punti di entrata e di uscita restano impostati sulla clip anche dopo.

## Per un altro editor {#for-another-editor}

Sotto i link di esportazione, **Per un altro editor** prepara un unico pacchetto per un montatore professionista: il montaggio, i tuoi file video con i loro nomi originali, un elenco di ciò che contiene e una breve nota su come aprirlo.

1. Scegli l'editor: Premiere Pro, DaVinci Resolve, Final Cut Pro, un altro editor oppure **Solo tagli (EDL)**. Premiere Pro e DaVinci Resolve possono usare due tipi di file; la scheda sceglie il primo.
2. Scegli **Includi i file video** oppure **Solo l'elenco dei file**. La scheda mostra cosa contiene il pacchetto, per esempio "12 file, 1,4 GB". Quando i file insieme sono troppo grandi per un solo pacchetto, la prima opzione è disattivata, la scheda ne spiega il motivo e **Solo l'elenco dei file** viene scelto per te. In quel caso copia tu gli originali.
3. Per **Solo tagli (EDL)** con più di una traccia video, scegli quale traccia video scrivere.
4. Premi **Scarica il pacchetto**. Il file viene salvato dal tuo browser. Se qualcosa lo ferma (per esempio ci sono già due pacchetti in preparazione), la scheda lo dice a parole.

I pacchetti di consegna portano l'etichetta **Anteprima anticipata**. Non ne abbiamo ancora aperto nessuno in Premiere Pro, DaVinci Resolve o Final Cut Pro, quindi consideralo una prima versione.

**Come aprirlo** dà tre righe per ogni editor. Non abbiamo aperto questi pacchetti in tutti gli editor, quindi le righe dicono "Dovrebbe aprirsi in ..." e ti chiedono di dirci se non succede.

Su smartphone la scheda dice soltanto che questa è un'operazione da desktop; i link di esportazione qui sopra funzionano comunque.

## Importare una timeline da un altro editor {#importing-a-timeline-from-another-editor}

Nella pagina delle timeline, **Importa** legge un file OpenTimelineIO. Al termine, un pannello dice cosa è successo: i file trovati (e come: stesso file dell'esportazione, trovato per nome, trovato per nome e durata) e le clip il cui media è **offline**, ciascuna con il motivo:

- "Nel progetto non c'è nessun file con questo nome"
- "Due file hanno questo nome; sceglierne uno"
- "La durata del file è diversa da quella della clip"

Ogni clip offline diventa uno spazio vuoto della sua stessa durata, con un segno rosso. **Ricollega** mette lì un file del progetto; se due file hanno lo stesso nome, **Scegli un file** li elenca con durata e dimensione. Il ricollegamento è un passaggio che puoi annullare. L'interruttore **L'importazione fallisce se manca qualcosa** (disattivato di default) rifiuta invece l'intera importazione. Ricollegare un'intera cartella in una volta non è ancora disponibile.

## L'elenco dei render {#the-render-list}

Ogni render di questa timeline è elencato sotto le impostazioni: In attesa, Render in corso (con l'avanzamento e un pulsante **Interrompi**), Finito, Non completato o Interrotto. Un render non completato ne spiega il motivo con parole semplici e offre **Riprova** (con le impostazioni mostrate ora) e **Rimuovi**.

## Su smartphone {#on-a-phone}

Tocca **Esporta** nella barra in alto. Il pannello si apre a tutta altezza: scegli un formato (Verticale per smartphone è il primo), controlla la riga del prezzo e premi **Render**, che resta in fondo al pannello così non devi mai scorrere per trovarlo. L'intervallo, il nome del file, il modo in cui vengono consegnati i sottotitoli e le scelte sul volume sono sotto **Altre opzioni**.

Mentre un film viene generato puoi lasciare la pagina. Quando torni, l'elenco è aggiornato. Se un film è finito mentre il pannello era chiuso, **Esporta** mostra un segno verde **Pronto** e ricevi un messaggio sullo schermo. Il film finito si riproduce lì stesso. **Scarica** lo salva con il suo nome e **Condividi il film** passa il file a un'altra app quando il tuo telefono lo permette (altrimenti **Condividi il link** condivide il link del progetto, e se il tuo telefono non può condividere affatto il pulsante non viene mostrato). Non inviamo un'email quando un film è pronto.

## Scaricare da altre pagine {#downloading-from-other-pages}

Nella scheda Render del tuo progetto e nel risultato di un passaggio di un flusso di lavoro, **Scarica** salva il video come file invece di aprirlo nel browser.
