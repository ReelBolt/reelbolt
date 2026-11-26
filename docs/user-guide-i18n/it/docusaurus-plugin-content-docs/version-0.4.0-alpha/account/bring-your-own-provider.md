---
title: Usa il tuo provider
sidebar_position: 3
description: Come uno spazio di lavoro di team di ReelBolt collega i propri servizi AI, cosa offre la pagina dello spazio di lavoro rispetto a quella della piattaforma e il limite di piano che la controlla.
i18n_source_sha: 1346200827ee62635d52f7ac3fcd3d13ef70f189
---

# Usa il tuo provider {#bring-your-own-provider}

Di default, ogni spazio di lavoro di ReelBolt usa le connessioni AI **gestite dalla piattaforma** con cui è stata configurata l'installazione. Se hai account personali presso servizi AI — una chiave OpenAI, un modello che ospiti sulla tua GPU, un servizio vocale — uno spazio di lavoro di team può collegare i **propri** servizi AI e usarli solo per il lavoro svolto in quello spazio di lavoro. Si chiama **usa il tuo provider** (BYO, bring your own provider). Questa pagina descrive queste connessioni dal punto di vista dello spazio di lavoro; il punto di vista dell'amministratore della piattaforma è in [Provider di inferenza](../admin/inference-providers.md).

## Cos'è la connessione propria di uno spazio di lavoro in ReelBolt {#what-a-workspaces-own-connection-is-in-reelbolt}

Una **connessione dello spazio di lavoro** appartiene a un solo spazio di lavoro di team. È visibile e utilizzabile solo al suo interno, è pagata dall'account di quello spazio di lavoro presso il servizio AI e non influisce sul lavoro di nessun altro. Quando uno spazio di lavoro imposta una delle sue connessioni come predefinita per una funzione, quella connessione ha la precedenza per quello spazio di lavoro — il predefinito della piattaforma continua a servire tutti gli altri.

Poiché il lavoro passa dal tuo account, **le connessioni dello spazio di lavoro costano 0 crediti ReelBolt**: paghi direttamente il tuo provider e ReelBolt registra l'utilizzo (così la pagina Utilizzo resta completa) senza addebitare il tuo saldo. Lo stesso vale per il lavoro svolto sulla macchina collegata allo spazio di lavoro — vedi [Runner desktop](./desktop-runner.md).

## Chi può usare la pagina dei provider dello spazio di lavoro in ReelBolt {#who-can-use-the-workspace-provider-page-in-reelbolt}

La pagina **Provider** dello spazio di lavoro si trova in **Spazio di lavoro → Provider** nel menu a sinistra.

- **Proprietario e Amministratore** possono aggiungere, modificare, testare ed eliminare le connessioni dello spazio di lavoro.
- **Membro e Fatturazione** non possono aprire affatto la pagina. Un Membro può comunque **scegliere** tra le connessioni offerte dalle impostazioni di un passaggio di un flusso di lavoro, perché è una scelta in sola lettura.
- Una connessione creata da uno spazio di lavoro viene usata solo al suo interno. Le connessioni gestite dalla piattaforma restano disponibili per tutti, e una connessione dello spazio di lavoro non le modifica mai.

## Aggiungere una connessione in uno spazio di lavoro ReelBolt {#adding-a-connection-in-a-reelbolt-workspace}

In **Spazio di lavoro → Provider**, clicca **Nuovo provider** e compila lo stesso modulo della pagina della piattaforma (nome, tipo, funzione, endpoint, modello, nome visualizzato, chiave API, timeout, imposta come predefinito, attivo), poi **Testa connessione** e **Crea**. Così aggiungi il tuo account con il suo primo modello.

Aggiungi ogni account una sola volta. Per usare altri modelli dello stesso account, apri il provider e clicca **Aggiungi modello**; per dare a un modello un altro compito (per esempio `Vision` su un modello di chat), clicca **Aggiungi funzione** su quel modello. La chiave e l'indirizzo appartengono al provider, quindi non dovrai mai digitare di nuovo la chiave, e se la cambi la modifica vale per tutti i modelli sotto di esso. I provider dello spazio di lavoro non hanno prezzi: il loro lavoro costa 0 crediti.

Il modulo offre meno opzioni della pagina della piattaforma, e ReelBolt fa rispettare la differenza a prescindere da ciò che mostra il modulo:

- **Nessuna scorciatoia `env:`.** Una connessione dello spazio di lavoro deve avere la propria chiave; non può puntare a una chiave dell'ambiente dell'installazione.
- **Nessuna modalità di abbonamento personale.** L'interruttore **Dev/OAuth (Personal use only)** che il modulo della piattaforma offre per Anthropic qui non è disponibile — un abbonamento Claude personale è concesso in licenza solo per l'uso del suo titolare, e uno spazio di lavoro che serve altre persone deve usare una vera chiave API.
- **Solo indirizzi pubblici.** Una connessione dello spazio di lavoro deve usare un indirizzo pubblico. Un servizio self-hosted nella rete privata dell'installazione può essere solo una connessione gestita dalla piattaforma.

La matrice tipo-e-funzione, i predefiniti per funzione e il comportamento di **Testa connessione** sono identici a quelli della pagina della piattaforma — vedi [Provider di inferenza](../admin/inference-providers.md) per la tabella completa e i dettagli del test.

## Il limite di piano che controlla l'uso del proprio provider in ReelBolt {#the-plan-limit-that-controls-bring-your-own-in-reelbolt}

Aggiungere, modificare o testare una connessione dello spazio di lavoro richiede che il piano dello spazio di lavoro includa l'uso del proprio provider.

- **Se il piano lo include** (Creator, Pro, Team ed Enterprise sì; la Prova gratuita no), la pagina funziona normalmente.
- **Se il piano non lo include**, aggiungere, modificare o testare una connessione viene rifiutato e il modulo mostra un limite di piano con il percorso per fare l'upgrade — non fallisce mai in modo oscuro, nulla si rompe e nulla viene addebitato. **Vedere l'elenco ed eliminare una connessione funzionano sempre**, così uno spazio di lavoro che ha perso la funzione può comunque fare pulizia. Una connessione esclusa dal piano semplicemente non viene usata finché il piano non la consente di nuovo.

Poiché un piano può negare la funzione, uno spazio di lavoro può anche puntare a un modello più potente per un solo agente senza toccare gli altri: un Proprietario o Amministratore può scegliere, per un agente integrato, qualsiasi connessione di chat attiva che lo spazio di lavoro può usare — gestita dalla piattaforma o propria — e vale solo per quello spazio di lavoro. Vedi [Provider di inferenza](../admin/inference-providers.md) per come funziona questa scelta per singolo agente.

## Scegliere una connessione per un passaggio in ReelBolt {#choosing-a-connection-for-one-step-in-reelbolt}

Nelle impostazioni di un passaggio di un flusso di lavoro, la connessione usata dal passaggio si sceglie tra quelle che lo spazio di lavoro può usare, nello stesso ordine con cui ReelBolt le risolve ovunque: una connessione scelta specificamente per quel passaggio, poi la connessione predefinita per quella funzione (un predefinito dello spazio di lavoro ha la precedenza al suo interno), poi il ripiego della piattaforma. Un Membro può fare questa scelta anche se non può aprire la pagina Provider, perché scegliere una connessione per un passaggio è in sola lettura.

## Pagine correlate in ReelBolt {#related-pages-in-reelbolt}

- [Provider di inferenza](../admin/inference-providers.md) — il riferimento completo dell'amministratore della piattaforma su tipi, funzioni, chiavi e test.
- [Piani e crediti](./plans-and-credits.md) — quali piani includono l'uso del proprio provider e perché il lavoro dello spazio di lavoro costa 0 crediti.
- [Runner desktop](./desktop-runner.md) — collegare una macchina tua per il lavoro di rendering.
- [Usa un modello sul tuo computer](./local-model.md) — fai usare al tuo spazio di lavoro un modello che gira sul tuo computer tramite l'app desktop.
- [Spazi di lavoro e team](../workspaces.md) — i ruoli che possono aprire questa pagina.
