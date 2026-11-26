---
title: Piani e crediti
sidebar_position: 2
description: Cos'è un piano ReelBolt, come i crediti vengono assegnati e spesi, quali piani esistono e cosa include ciascuno, quanto spazio di archiviazione ha uno spazio di lavoro e cosa conta ai fini del limite, e dove trovare le pagine su cambio piano, codici promo, stime, risoluzione e costi dell'assistente.
i18n_source_sha: f4f4317f24e0df3f55f234087eac48a1f66f0822
---

# Piani e crediti {#plans-and-credits}

:::note
Questa è la versione dettagliata; quella breve è in [Piano, crediti e fatturazione](../basics/plan-credits-and-billing.md).
:::

Un **piano** ReelBolt è ciò a cui si abbona uno spazio di lavoro, e i **crediti** sono ciò che costa il lavoro al suo interno. Ogni unità di lavoro AI che ReelBolt acquista per tuo conto, come una chiamata a un modello, una trascrizione, una clip generata o un render, viene registrata in un registro e, in un piano a pagamento, addebitata in crediti. Questa pagina è la panoramica sul lato economico: quali sono i piani, come i crediti entrano ed escono e dove vedi tutto. Ogni argomento ha una sua pagina, collegata alla fine della relativa sezione.

## Cos'è un piano in ReelBolt {#what-a-plan-is-in-reelbolt}

Un piano stabilisce cosa può fare lo spazio di lavoro in un mese: quanti crediti riceve, quanto spazio di archiviazione include, la risoluzione massima di esportazione, se le esportazioni hanno una filigrana, quante esecuzioni possono andare in parallelo, se include le stanze di montaggio e il video generato, e quanto video generato può acquistare al giorno. Il piano del tuo spazio di lavoro compare in **Impostazioni → Piano e fatturazione** come **Piano attuale**, con il suo stato, la data di rinnovo e il numero di postazioni.

Solo il **Proprietario** dello spazio di lavoro e i membri con il ruolo **Fatturazione** possono acquistare, cambiare o annullare un piano. Tutti gli altri vedono la pagina in sola lettura. Vedi [Gestire il tuo piano](./managing-your-plan.md) per i passaggi.

## Come funzionano i crediti in ReelBolt {#how-credits-work-in-reelbolt}

Un **credito** è un'unità di spesa. ReelBolt misura ogni unità fatturabile che acquista da un provider e ne calcola il prezzo quando viene eseguita, quindi una successiva variazione di prezzo non riscrive mai quanto ti è stato fatturato. La pagina **Utilizzo** (**Impostazioni → Utilizzo**) mostra il tuo saldo, quanto è trattenuto per il lavoro in corso e quanto hai usato, suddiviso per chi ha svolto il lavoro.

- **Il lavoro con i provider di ReelBolt viene addebitato.** Quando un passaggio gira su un provider configurato dall'installazione di ReelBolt, i suoi crediti escono dal tuo saldo.
- **Il lavoro tuo è gratis per te.** Il lavoro con un provider che il tuo spazio di lavoro ha collegato da sé (vedi [Usa il tuo provider](./bring-your-own-provider.md)) e il lavoro su un computer che il tuo spazio di lavoro ha collegato da sé (vedi [Runner desktop](./desktop-runner.md)) viene comunque registrato, così la pagina Utilizzo resta completa, ma costa **0 crediti**. Paghi il tuo provider e la tua macchina, non ReelBolt.
- **Un passaggio può finire un po' sopra la stima.** L'utilizzo reale di un passaggio non si può prevedere con esattezza, quindi può finire leggermente sopra a quanto era stato trattenuto. L'eccedenza viene addebitata e il saldo può andare in negativo, il che blocca altro lavoro finché lo spazio di lavoro non ricarica.

**Come viene trattato un errore.** Se un passaggio fallisce per colpa della piattaforma, i crediti che stava trattenendo vengono rimborsati. Se fallisce per un tuo input, perché l'hai fermato tu o perché ha fallito un provider che il tuo spazio di lavoro ha collegato da sé, i crediti effettivamente usati restano addebitati.

## Cosa consuma crediti {#what-uses-credits}

| Cosa fai | Come viene addebitato |
|---|---|
| **Esecuzioni dei flussi di lavoro** | Ogni passaggio viene addebitato per il lavoro che svolge, come descritto sopra. |
| **L'assistente** e **[Claude Code](../assistant/claude-code.md)** | Gratis fino all'allocazione mensile dell'assistente del tuo piano, poi addebitati per ciò che usano. I due condividono un'unica allocazione. |
| **Strumenti AI dell'editor di immagini** (per esempio modifica a parole, cancella, aumenta la risoluzione o ritaglia un soggetto) | Addebitati ogni volta che ne usi uno. |
| **Suggerimenti sulla timeline** | Addebitati ogni volta che ne chiedi uno. |
| **Grafiche progettate** | Addebitate ogni volta che ne progetti una. |
| **Riassunti dei file**, **indicizzazione per la ricerca** e **archiviazione** | Gratis. L'archiviazione è invece limitata dal piano (vedi sotto). |

Il lavoro con un provider che il tuo spazio di lavoro ha collegato da sé costa 0 crediti, qualunque sia la voce. L'importo dell'allocazione mensile dell'assistente per il tuo piano è mostrato in **Impostazioni → Piano e fatturazione**. Per il costo dettagliato di assistente e Claude Code, vedi [Quanto costa l'assistente](./what-the-assistant-costs.md).

## I piani in vendita in ReelBolt {#the-plans-on-sale-in-reelbolt}

Questi sono i piani del catalogo. I prezzi sono IVA e imposte sulle vendite escluse, che vengono aggiunte al checkout dal provider di pagamento. Le cifre di crediti e limiti qui sotto sono l'insieme attualmente pubblicato.

| Piano | Prezzo | Crediti | Archiviazione | Cosa aggiunge |
|---|---|---|---|---|
| **Trial** | Gratis, 100 crediti una tantum | 100 (una tantum) | 1 GB | Esportazioni in 720p con filigrana, nessuna carta richiesta. Non si può ricaricare, nessun provider proprio, nessuna stanza, nessun video generato. |
| **Creator** | $29 / mese | 1.000 / mese | 15 GB | Esportazioni in 1080p, senza filigrana, provider proprio, crediti di ricarica. |
| **Pro** | $79 / mese | 3.500 / mese | 50 GB | Esportazioni in 4K, le stanze di montaggio, clip b-roll generate, render prioritario, crediti di ricarica. |
| **Team** | $49 / mese per postazione (minimo 3 postazioni) | 1.500 per postazione / mese, in comune | 100 GB condivisi | Tutto ciò che include Pro (comprese le esportazioni in 4K), con uno spazio di lavoro condiviso e ruoli. I crediti sono in comune tra le postazioni. |
| **Enterprise** | Contattaci | — | illimitata | 4K e oltre, single sign-on, capacità dedicata, una licenza commerciale senza obblighi AGPL, condizioni e supporto personalizzati. |

I pacchetti di ricarica sono disponibili nei piani a pagamento (Creator, Pro e Team) nei tagli da 500, 1.000 e 3.000 crediti. Scadono dodici mesi dopo l'acquisto. Il piano Trial gratuito non può ricaricare, quindi scegli prima un piano a pagamento.

## Risoluzione di esportazione in ogni piano ReelBolt {#export-resolution-on-each-reelbolt-plan}

Ogni piano stabilisce la risoluzione massima che un'esecuzione o un'esportazione dall'editor può produrre, e la tabella dei piani qui sopra indica il limite di ciascuno. La risoluzione si misura sul lato corto del video, così una sola impostazione va bene sia per video orizzontali sia per verticali. Scegli la risoluzione di un'esecuzione nelle sue impostazioni, oppure di un'esportazione nello spazio **Consegna** dell'editor (l'editor video è un'anteprima anticipata). Una risoluzione più alta costa più crediti, perché il rendering si addebita in base alle dimensioni dell'output.

Per come scegliere una risoluzione, cosa succede se ne scegli una non inclusa nel tuo piano e quanto costa ciascuna, vedi [Scegliere la risoluzione](./choosing-resolution.md).

## Archiviazione in uno spazio di lavoro ReelBolt {#storage-in-a-reelbolt-workspace}

Ogni piano include una quantità di archiviazione per l'intero spazio di lavoro, ed Enterprise è illimitato. L'archiviazione si misura su tutto ciò che c'è nei progetti dello spazio di lavoro, non solo sui tuoi upload. Comprende i file che carichi, i file sincronizzati da GitHub, da un cloud drive o da una cartella del tuo computer, i file che un agente scrive durante un'esecuzione e i video, le immagini e gli altri risultati prodotti da un'esecuzione.

**Cosa succede quando l'archiviazione è piena.** Non vengono più aggiunti nuovi file. Un upload viene rifiutato e ti dice che lo spazio di lavoro ha esaurito l'archiviazione. Una sincronizzazione in background copia ciò che ci sta, lascia il resto e dice quanti file non ci stavano. Un flusso di lavoro già in esecuzione non viene interrotto e i suoi risultati vengono comunque scritti, perché l'esecuzione è già stata pagata. **Non viene mai eliminato nulla in automatico**, nemmeno quando passi a un piano con meno archiviazione di quella che usi. Lo spazio di lavoro semplicemente non può aggiungere altro finché non elimini file o fai l'upgrade.

**Dove vederla.** **Impostazioni → Piano e fatturazione** mostra una barra dell'archiviazione con la quantità usata e il limite del piano, e la pagina **Utilizzo** mostra lo stesso. Le pagine dei progetti mostrano un avviso quando lo spazio di lavoro è al 90 % o più. Se un'installazione funziona senza fatturazione, non c'è limite di archiviazione e nemmeno la barra.

## Cambiare il tuo piano {#changing-your-plan}

Acquista, fai l'upgrade, passa a un piano inferiore, cambia le postazioni, annulla e revoca un annullamento in **Impostazioni → Piano e fatturazione**. Il passaggio a un piano più grande ha effetto quando il provider di pagamento lo conferma, e il passaggio a un piano inferiore parte con il periodo di fatturazione successivo. Può essere in attesa una sola modifica alla volta. Leggi [Gestire il tuo piano](./managing-your-plan.md) per ogni passaggio e per cosa fare mentre una modifica è ancora in fase di conferma.

## Codici promo e crediti gratuiti {#promo-codes-and-free-credits}

Inserisci un codice promo in **Impostazioni → Piano e fatturazione** per abbassare il prezzo di un piano o di un pacchetto di crediti prima di pagare. Alcuni codici danno invece crediti gratuiti, che riscatti nella stessa pagina. I crediti promo scadono e vengono spesi prima di quelli che hai acquistato. Leggi [Codici promo e crediti](./promo-codes-and-credits.md) per i passaggi, i rifiuti e le scadenze dei crediti.

## Quanto costerà un'esecuzione prima di avviarla {#what-a-run-will-cost-before-you-start-it}

Prima di avviare un flusso di lavoro, ReelBolt mostra quanto dovrebbe costare l'esecuzione, per esempio **Questa esecuzione dovrebbe costare ≈ 40 crediti (di solito 30–52)**. Dice anche su cosa si basa la cifra e cosa deve esserci nel tuo saldo prima che l'esecuzione possa partire. Leggi [Leggere la stima](./reading-the-estimate.md) per il significato di ogni parte di quella riga.

## Fatture, metodo di pagamento e provider di pagamento {#invoices-payment-method-and-the-payment-provider}

Il provider di pagamento addebita la tua carta, aggiunge le imposte ed emette le fatture. Seleziona **Gestisci presso …** nella scheda del piano attuale (il pulsante riporta il nome del tuo provider di pagamento) per aprire la pagina del tuo account presso il provider, dove puoi scaricare le fatture, cambiare il metodo di pagamento e annullare. Le scorciatoie **Aggiorna metodo di pagamento** e **Annulla presso …** (anch'essa con il nome del provider) aprono direttamente la pagina corrispondente quando il provider la offre.

## Ricaricare i crediti {#topping-up-credits}

Sotto **Acquista crediti** in **Impostazioni → Piano e fatturazione**, un Proprietario o un membro Fatturazione può acquistare un pacchetto di ricarica. I crediti di ricarica vengono aggiunti allo stesso saldo dei crediti mensili del piano. Servono per un mese intenso, non come sostituto di un piano, e scadono dodici mesi dopo l'acquisto. Il provider di pagamento mostra l'addebito esatto, nella tua valuta, prima che tu paghi.

## Cosa fare quando il saldo si abbassa {#what-to-do-when-the-balance-runs-low}

- **Il lavoro si ferma invece di spendere troppo.** Un passaggio che richiede più crediti di quelli disponibili viene rifiutato prima di partire, quindi non ti viene mai addebitato più del tuo saldo senza che tu ricarichi.
- **Ricarica** da **Impostazioni → Piano e fatturazione → Acquista crediti**, oppure **passa a un piano più grande** i cui crediti mensili si adattano al tuo utilizzo.
- **In un'installazione self-hosted senza fatturazione**, non esiste un saldo da esaurire. Le pagine di utilizzo e fatturazione sono disattivate.

## Dove vengono spesi i tuoi crediti {#where-your-credits-are-spent}

La pagina **Utilizzo** (**Impostazioni → Utilizzo**) è il registro aggiornato in tempo reale. Mostra **Crediti disponibili** (con l'importo nell'account e quello trattenuto per il lavoro in corso), **Crediti usati** e la suddivisione per chi ha svolto il lavoro: i provider di ReelBolt, un provider che il tuo spazio di lavoro ha collegato da sé e il runner dello spazio di lavoro. Ogni voce riporta il provider e il modello che hanno svolto il lavoro e i crediti addebitati.

Se un'installazione funziona senza fatturazione, questa pagina lo dice e non mostra nulla. Non c'è nessun piano da acquistare, nessun saldo da ricaricare e nessuna fattura da aprire, e nulla di ciò che esegui viene misurato o addebitato.

## Pagine correlate in ReelBolt {#related-pages-in-reelbolt}

- [Gestire il tuo piano](./managing-your-plan.md): acquistare, fare l'upgrade, cambiare piano e postazioni, annullare.
- [Codici promo e crediti](./promo-codes-and-credits.md): codici promo, codici per crediti gratuiti e scadenza dei crediti promo.
- [Leggere la stima](./reading-the-estimate.md): la riga del costo prima di un'esecuzione e cosa è gratis.
- [Scegliere la risoluzione](./choosing-resolution.md): l'impostazione della risoluzione e quanto costa ciascuna.
- [Quanto costa l'assistente](./what-the-assistant-costs.md): l'allocazione dell'assistente e di Claude Code e i costi delle approvazioni.
- [Spazi di lavoro e team](../workspaces.md): chi può acquistare il piano e come è organizzato lo spazio di lavoro.
- [Usa il tuo provider](./bring-your-own-provider.md): il percorso gratuito per te per il lavoro dei modelli.
- [Runner desktop](./desktop-runner.md): il percorso gratuito per te per il lavoro di rendering.
- [Copiare una cartella del tuo computer in un progetto](../projects/local-folder-sync.md): come la sincronizzazione delle cartelle aggiunge file a uno spazio di lavoro, che contano ai fini dell'archiviazione.
- [Risoluzione dei problemi](../troubleshooting.md): cosa fare quando un'esecuzione è rifiutata per mancanza di crediti o quando lo spazio di lavoro ha esaurito l'archiviazione.
