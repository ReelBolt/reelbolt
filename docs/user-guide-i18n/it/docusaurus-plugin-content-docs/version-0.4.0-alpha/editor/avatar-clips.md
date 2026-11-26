---
title: Clip con i tuoi avatar e oggetti
sidebar_position: 14
description: Come creare dal pool multimediale dell'editor una nuova clip video che mostra fino a tre persone e oggetti del progetto, cosa fa ogni scelta della finestra, quanto costa e cosa succede mentre la clip viene creata.
i18n_source_sha: 829dc50cf4a9fe2aeab0ec40f3430681ed9ce6ed
---

# Clip con i tuoi avatar e oggetti {#clips-with-your-avatars-and-objects}

L'editor può creare una nuova clip video che mostra persone e oggetti della scheda [Avatar](../projects/avatars.md) del progetto, per esempio "Anna prende la bottiglia e sorride alla camera". La clip viene creata da un servizio video AI e aggiunta ai media del progetto, quindi puoi metterla nella timeline come qualsiasi altra clip. Non fa parte di un flusso di lavoro.

La trovi nella scheda **Avatar** del pool multimediale, accanto a **Media** ed **Effetti sonori**. Su un telefono, apri il pannello per aggiungere media e scegli **Avatar**. La scheda c'è solo quando avatar o oggetti sono attivi nel tuo spazio di lavoro.

## Cosa mostra la scheda {#what-the-tab-shows}

La scheda elenca le **Persone** e gli **Oggetti** del progetto, ognuno con l'indicazione se è pronto. Uno che al momento non si può usare è mostrato con il motivo; sistemalo nella scheda **Avatar** del progetto (il link **Aggiungili o gestiscili nella scheda Avatar** ti porta lì). Se il progetto non ne ha ancora, la scheda lo dice.

## Creare una clip {#make-a-clip}

Scegli **Genera clip** per aprire **Genera una clip**:

- **Chi o cosa compare nella clip.** Fino a tre persone e oggetti del progetto, al massimo una persona.
- **Cosa succede nella clip.** Descrivi la scena e cosa fa ciascuno, in fino a 2.000 caratteri.
- **Cosa dice (file audio).** Solo quando hai scelto esattamente una persona: scegli una registrazione tra i file del progetto e la persona la dice. La clip dura allora quanto la registrazione. Lascia **Niente, nessun parlato** per una clip senza parlato.
- **Modello.** **Il modello predefinito** del tuo spazio di lavoro, oppure un altro modello video configurato nel tuo spazio di lavoro.
- **Durata (secondi)**, **Dimensione** e **Forma.** La forma parte da quella più vicina all'immagine della tua timeline.
- **Quante versioni.** Una o due clip della stessa richiesta, tra cui scegliere.

La finestra mostra il prezzo prima di iniziare, per esempio "Circa 12 crediti". Quando compare **Include un'immagine fissa creata prima**, il modello fa partire la clip da un'immagine delle tue persone e dei tuoi oggetti nella scena, che viene creata prima e fa parte del prezzo. Premi **Genera clip** per iniziare.

## Inviare i loro file al servizio AI {#sending-their-files-to-the-ai-service}

Per mostrarli, i loro file vengono inviati ai servizi AI usati dal progetto: le loro immagini e, a seconda del servizio, anche i loro brevi video e le registrazioni della voce di una persona. La prima volta che un progetto ne ha bisogno, l'editor te lo chiede: spunta che sei d'accordo e scegli **Accetta e continua**. Te lo chiede una sola volta per progetto, e puoi revocarlo nella scheda **Avatar** del progetto. Vedi [Invio dei file ai servizi AI](../projects/avatars.md#sending-media-to-ai-services).

## Mentre la clip viene creata {#while-the-clip-is-made}

La clip viene creata in secondo piano, quindi puoi continuare a montare. Compare in **Clip in creazione** con il suo stato: **In attesa**, **Creazione in corso**, **Fatto**, **Non riuscita** o **Interrotta**. **Interrompi** ferma una clip ancora in creazione; viene addebitato solo ciò che il servizio AI ha già creato. **Togli** toglie dall'elenco una voce finita.

Una clip finita è un nuovo file video del progetto e compare nella scheda **Media**. Una clip che mostra una persona è segnata come generata con l'AI nel file video e mostra anche una piccola etichetta **AI-generated** nell'immagine, a meno che l'avatar di quella persona chieda un altro modo di dichiararlo. Una clip che mostra solo oggetti è segnata nel file, senza etichetta.

Ogni persona e ogni oggetto viene ricontrollato subito prima di creare la clip. Se uno nel frattempo non è più pronto (per esempio il suo consenso viene revocato o uno dei suoi file cambia), la clip non viene creata e non ti viene addebitata.

## Quando una clip non si può creare {#when-a-clip-cannot-be-made}

La finestra dice perché, e non viene addebitato nulla:

- **Il modello non può mostrare immagini.** Alcuni modelli video creano una clip solo dalle parole. La finestra indica un modello della stessa famiglia che può farlo, con un pulsante per usarlo quando il tuo spazio di lavoro ha quel modello.
- **Il tuo piano non lo include.** Clip come queste richiedono un piano che includa la creazione di video con l'AI, più gli avatar di persone o gli oggetti per ciò che hai scelto. **Vedi i piani** mostra le tue opzioni.
- **Crediti insufficienti.** **Aggiungi crediti** ti porta alla fatturazione.
- **Le clip che mostrano una persona non sono ancora disponibili nel tuo spazio di lavoro.** Uno spazio di lavoro può avere le clip di oggetti prima di quelle di persone; una clip con soli oggetti si può comunque creare.
- **Una delle tue scelte al momento non si può usare.** Controllala nella scheda **Avatar** del progetto.
- **Troppe clip in poco tempo.** Aspetta un po' e riprova.

L'assistente può creare per te lo stesso tipo di clip, usando i nomi delle tue persone e dei tuoi oggetti; vedi [Cosa può fare l'assistente](../projects/avatars.md#what-the-assistant-can-do).
