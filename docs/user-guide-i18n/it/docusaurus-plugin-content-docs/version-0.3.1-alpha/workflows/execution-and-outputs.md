---
title: Eseguire i flussi di lavoro e trovare i risultati
sidebar_position: 4
description: Cosa succede quando esegui un flusso di lavoro ReelBolt, come seguirlo e fermarlo, come funzionano i nuovi tentativi e i cicli di revisione, e dove finiscono video e altri risultati.
i18n_source_sha: 2bbcc5c77dbc0eb0f0d785b30dbfc50e5a062e58
---

# Eseguire i flussi di lavoro e trovare i risultati {#running-workflows-and-finding-the-results}

Questa pagina spiega cosa succede dopo che premi **Avvia** su un flusso di lavoro: come viene messa in coda l'esecuzione, cosa
significano gli stati dei passaggi, come si comportano i nuovi tentativi e i cicli di revisione, come fermare un'esecuzione e dove finiscono
il video finale e gli altri risultati.

## Avviare un'esecuzione {#starting-a-run}

Avvii un flusso di lavoro dalla sua pagina nel progetto (scheda Flussi di lavoro, apri il flusso di lavoro, fai clic su
**Avvia**). Un'esecuzione non parte mai con un solo clic: prima si apre una finestra di conferma.

- La finestra elenca ogni passaggio a parole semplici ("Guarda i tuoi filmati", "Scrive la voce narrante") e
  quanto dura più o meno l'esecuzione, in base alle tue esecuzioni precedenti dello stesso flusso di lavoro.
- Offre sempre **Il tuo brief**: testo libero passato a ogni agente AI dell'esecuzione come istruzioni
  aggiuntive ("resta sotto il minuto", "mostra la nostra pagina dei prezzi sullo schermo del telefono"). È
  facoltativo, a meno che per il flusso di lavoro non sia attivata l'opzione **Richiedi un brief prima di ogni esecuzione**.
- Offre anche uno **Stile di montaggio** (come l'AI deve montare il video) e un **Formato video** con un solo clic
  (9:16, 1:1, 4:5 o 16:9), valido solo per questa esecuzione; vedi
  [Stili di montaggio e formato video](./editing-styles.md).
- Accanto al formato, **Risoluzione** imposta la nitidezza del video finale: **Auto**, **720p**,
  **1080p** o **4K**. Auto dà la dimensione abituale: fino a 1080p, o il limite del tuo piano se è
  inferiore. Le scelte oltre il tuo piano appaiono disattivate e una risoluzione più alta usa più crediti
  di render; vedi [Risoluzione di esportazione in ogni piano ReelBolt](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan).
- Fai clic su **Avvia esecuzione**. L'esecuzione compare subito nella **Cronologia esecuzioni** del flusso di lavoro, numerata
  **Esecuzione 1**, **Esecuzione 2**… con la prima riga del suo brief, e ReelBolt apre la sua pagina in tempo reale.
- Se il flusso di lavoro contiene un passaggio `VideoGenerate` (clip video AI a pagamento), ReelBolt chiede se
  **riutilizzare le clip generate in precedenza** (gratis, è l'opzione predefinita) o **Rigenerarle** (costa denaro). La
  finestra mostra il costo massimo possibile: la somma dei limiti di spesa dei passaggi video.

Puoi anche chiedere all'[assistente](../assistant/index.md) di eseguire un flusso di lavoro per te.

## La coda e gli stati dell'esecuzione {#the-queue-and-run-statuses}

Una nuova esecuzione è prima **In coda**, poi **In corso**. La piattaforma esegue un numero limitato di flussi di lavoro
contemporaneamente (4 per impostazione predefinita, stabilito dall'amministratore); le esecuzioni in più restano in coda e partono
da sole quando si libera un posto. Un'esecuzione può durare da pochi minuti (un montaggio breve) a oltre un'ora (un promo
i cui agenti scrivono e generano molto codice).

Un'esecuzione termina con uno di questi stati (la pagina dell'esecuzione li mostra come **Terminata**, **Fallita** e
**Interrotta**):

| Stato | Significato |
|---|---|
| `Passed` | Ogni passaggio è terminato. |
| `Failed` | Un passaggio è fallito (dopo eventuali nuovi tentativi automatici), quindi l'esecuzione si è fermata lì. L'errore è mostrato nella pagina dell'esecuzione. |
| `Cancelled` | L'esecuzione è stata interrotta, da te o dal sistema. La pagina dell'esecuzione dice da chi o perché. |

### Richiede attenzione {#needs-attention}

Un'esecuzione terminata può mostrare un badge ambra **Richiede attenzione** al posto di **Passed**. Una nota ambra nella
pagina dell'esecuzione ne spiega il motivo, per esempio: "La revisione si è fermata dopo 3 cicli a 6/10; l'obiettivo era
8/10." Il film è pronto ma non ha raggiunto la soglia di qualità. Puoi comunque vederne l'anteprima e scaricarlo,
oppure chiedere modifiche ed eseguirlo di nuovo.

## Stati dei passaggi {#step-statuses}

Ogni passaggio di un'esecuzione mostra il proprio stato:

| Stato | Significato |
|---|---|
| `Pending` | Non ancora raggiunto. |
| `Running` | In lavorazione. I passaggi lunghi mostrano messaggi di avanzamento in tempo reale (per esempio "Tracking insert regions" o "Encoding"). |
| `Completed` | Terminato. Fai clic per vedere cosa ha prodotto. |
| `Failed` | Non è terminato; i dettagli dell'errore ne spiegano il motivo. |
| `Skipped` | Non eseguito perché il suo agente non è stato trovato — per esempio un passaggio che usa un agente personalizzato (gli agenti personalizzati non vengono eseguiti nei flussi di lavoro). |

Un passaggio riutilizzato da un'esecuzione identica precedente è contrassegnato come proveniente **dalla cache** (vedi "Riutilizzare
i risultati precedenti" più sotto).

## Seguire un'esecuzione in tempo reale {#following-a-run-live}

La pagina dell'esecuzione si aggiorna in tempo reale mentre l'esecuzione è in corso: gli stati dei passaggi cambiano, compaiono messaggi di avanzamento per
i passaggi lunghi (analisi video, codifica, generazione di clip) e i passaggi stanza (`EditRoom`,
`GraphicsRoom`, `ColorGradeRoom`) mostrano il turno di ogni membro del panel mentre avviene. I messaggi di avanzamento sono
solo informativi; un passaggio è concluso soltanto quando il suo stato dice `Completed`.

In cima alla pagina dell'esecuzione c'è il nome dell'esecuzione ("Esecuzione 3 · 4 ott, 14:02"), una frase che dice a parole semplici cosa
sta succedendo ora ("Scrittura della voce narrante…") e una stima del tempo rimanente basata sulla durata dei
passaggi già terminati — è una stima, perché i passaggi di render di solito durano più degli altri. Se un passaggio
ha segnalato un problema che dovresti conoscere (battute di voce narrante che non è stato possibile collocare, nessuna musica
perché il progetto non ha file audio, un video molto più corto della durata che hai chiesto, una
voce narrante che non funziona), un riquadro giallo in cima lo dice, anche se l'esecuzione
è terminata. Conteggi dei token, stato della connessione in tempo reale e dati grezzi di ogni passaggio si trovano sotto **Dettagli**.

## Nuovi tentativi automatici {#automatic-retries}

ReelBolt riprova da sé alcuni errori prima di arrendersi:

- I passaggi **agente AI** (`Agent`, `ReviewLoop`, `ForEach`, `Parallel`) e i passaggi `ColorGradeRoom`,
  `VideoGenerate` e `Voiceover` hanno **fino a 3 tentativi** per impostazione predefinita (l'amministratore può
  cambiarlo). Quando la risposta di un agente ha la forma sbagliata, il nuovo tentativo dice all'agente cosa non
  andava. Le attese tra un tentativo e l'altro aumentano un po' ogni volta.
- **Un agente che esaurisce il tempo non viene riprovato**, perché un nuovo tentativo ripeterebbe solo lo stesso
  lungo lavoro. L'errore dice che è scaduto il tempo; un operatore può aumentare il limite di tempo di quell'agente.
- I passaggi `Extract`, `VideoAnalyze`, `VideoCompile`, `EditRoom` e `GraphicsRoom` **non vengono riprovati
  per intero**: fallirebbero di nuovo allo stesso modo, oppure rifarebbero minuti di elaborazione video
  per niente. (La trascrizione del parlato dentro `VideoAnalyze` e la stesura finale del regista dentro i passaggi
  stanze hanno ciascuna i propri piccoli nuovi tentativi.)
- Se un agente ferma deliberatamente l'esecuzione (il suo strumento "fermare l'esecuzione"), non ci sono nuovi tentativi.

Quando un passaggio fallisce ancora dopo i suoi tentativi, l'intera esecuzione viene contrassegnata come `Failed`.

## Cicli di revisione {#review-loops}

Un passaggio `ReviewLoop` è un controllo di qualità. Un agente revisore dà al lavoro un voto su 10:

- Se è pari o superiore al punteggio di superamento del passaggio (`minScore`), l'esecuzione prosegue.
- Se è inferiore, l'esecuzione **torna indietro** a un passaggio precedente (`loopTargetStepOrder`) e rifà tutto
  da lì, passando ai passaggi rifatti i commenti del revisore.
- Dopo `maxIterations` cicli l'esecuzione **prosegue comunque** con l'ultimo risultato; un punteggio finale
  basso non fa fallire l'esecuzione.
- In un ciclo di revisione che può correggere da solo (attivo in ogni template fornito; vedi
  [tipi di passaggio](step-types.md#review-loop-step-reviewloop)), il revisore può anche cambiare un'impostazione o
  chiedere di rifare un passaggio precedente, per esempio una riga della voce narrante o un'inquadratura generata.
  L'esecuzione torna a quel passaggio dentro la stessa esecuzione, ogni passaggio rifatto viene addebitato come
  qualsiasi altro e la pagina dell'esecuzione elenca ogni modifica con il motivo ("Il revisore ha cambiato ... solo
  per questa esecuzione") e il costo del rifacimento. Se i crediti finiscono durante un rifacimento, la revisione si
  ferma e l'esecuzione termina con la sua versione migliore, segnata come **Richiede attenzione**.

### Richiede attenzione {#needs-attention-1}

Quando la revisione si ferma sotto il punteggio di superamento, l'esecuzione termina comunque come `Passed` e il film è
a tua disposizione, ma la pagina dell'esecuzione mostra **Richiede attenzione** con questa nota: "La revisione si è fermata dopo
*N* cicli a *S*/10; l'obiettivo era *M*/10. Il film è pronto ma non ha raggiunto la soglia di qualità."
Anche l'assistente segnala un'esecuzione così allo stesso modo, invece di definirla un successo. I crediti
non cambiano: l'esecuzione viene addebitata esattamente come qualsiasi altra esecuzione terminata e tutti i suoi risultati vengono conservati. Se
più revisioni si fermano prima della soglia, viene mostrato il punteggio più basso. Riprovare o riprendere l'esecuzione cancella la nota
e la imposta di nuovo solo se anche il nuovo tentativo si ferma prima della soglia.

Nei template per filmati il ciclo torna al passaggio 2 (la decisione di montaggio), quindi l'analisi non viene
ripetuta; nei template promo torna al passaggio `AuthorAgent`, che rifà il render.

## Fermare un'esecuzione {#stopping-a-run}

Premi **Interrompi** nella pagina dell'esecuzione mentre è in corso. L'esecuzione si ferma al successivo punto sicuro e termina come
`Cancelled`, registrata come interrotta da te. Il lavoro già finito (per esempio una clip già generata)
viene conservato.

## Modificare o eliminare un flusso di lavoro non cambia mai un'esecuzione {#editing-or-deleting-a-workflow-never-changes-a-run}

Quando un'esecuzione parte, ReelBolt conserva una propria copia del flusso di lavoro com'era in quel momento e l'esecuzione
usa quella copia dall'inizio alla fine. Modificare il flusso di lavoro mentre un'esecuzione è in corso, o dopo, non
cambia quell'esecuzione, e lo stesso vale per le modifiche agli agenti che usa. Anche Riprendi ed Esegui di nuovo usano la
copia dell'esecuzione, mai le tue modifiche successive. Per usare una modifica, salvala e avvia una nuova esecuzione.

Puoi eliminare un flusso di lavoro se nessuna delle sue esecuzioni è in corso. Le sue esecuzioni precedenti restano nella tua cronologia
con il nome del flusso di lavoro. Se un'esecuzione è ancora in coda o in corso, l'eliminazione del flusso di lavoro viene rifiutata
finché l'esecuzione non termina.

## Riprendere un'esecuzione {#resuming-a-run}

Quando un'esecuzione è terminata come `Failed` o `Cancelled`, **Riprendi** nella pagina dell'esecuzione prosegue da dove si era
fermata invece di ricominciare da capo.

- **Cosa viene conservato.** I passaggi già terminati non vengono eseguiti di nuovo e non vengono addebitati di nuovo.
  Riprendi riparte dal primo passaggio che non era terminato, e da lì in poi tutto viene eseguito.
- **Gruppi di passaggi.** Un ciclo, un gruppo di passaggi in parallelo, una stanza o un ciclo di revisione viene conservato solo
  se è terminato per intero. Altrimenti l'intero gruppo viene eseguito di nuovo.
- **Un risultato mancante.** Se il file salvato di un passaggio terminato non è più disponibile, quel passaggio viene eseguito di nuovo.
- **Crediti.** Riprendi controlla il tuo saldo per i passaggi che devono ancora essere eseguiti e rifiuta se è
  troppo basso. I limiti del tuo piano valgono come per una nuova esecuzione.
- **Clip video a pagamento.** Una clip già generata viene conservata. Se un tentativo precedente può aver
  ordinato una clip che non è mai arrivata, Riprendi la ordina di nuovo e ti viene addebitata una sola volta.
- **La stessa esecuzione.** L'esecuzione mantiene la sua ora di inizio e il suo posto nella tua cronologia.
- **Il tentativo fallito.** I risultati del tentativo che non è terminato, a partire dal primo passaggio che
  viene eseguito di nuovo, non sono più mostrati nella pagina dell'esecuzione. Restano nei tuoi registri e nella cronologia dei crediti.
  Premere **Riprendi** di nuovo più tardi non ti addebita mai due volte lo stesso passaggio.
- **Premuto nello stesso momento di un'altra persona.** Se un'altra persona del progetto ha premuto prima **Riprendi** o
  **Riavvia**, o ha eliminato il flusso di lavoro, vedi un messaggio che dice cosa è cambiato. Ricarica la pagina.
  Nulla viene avviato o addebitato due volte.

Riprendi non è disponibile quando:

- l'esecuzione è ancora in corso, o si sta ancora fermando dopo che hai premuto Interrompi (riprova tra un attimo);
- l'esecuzione è più vecchia di questa funzione. Per queste esecuzioni usa **Riavvia**;
- non hai accesso in modifica al progetto, oppure la tua email non è verificata.

Se un'esecuzione va persa perché il sistema si è riavviato, ReelBolt la riprende una volta da solo. Non lo fa
finché una clip video che ha ordinato è ancora in attesa di arrivare; queste esecuzioni vengono contrassegnate come `Failed` e aspettano
che premi **Riprendi**, così non ti viene mai addebitata due volte la stessa clip.

## Riprovare un'esecuzione {#retrying-a-run}

Quando un'esecuzione è terminata (`Passed`, `Failed` o `Cancelled`), **Riavvia** nella pagina dell'esecuzione avvia la stessa
esecuzione di nuovo **dal passaggio 1**, con lo stesso testo di richiesta, stile, formato e risoluzione, e la stessa copia
del flusso di lavoro usata dall'esecuzione. Le modifiche successive al flusso di lavoro non vengono recepite. I passaggi i cui input non sono cambiati possono essere
riutilizzati dalla cache, quindi un nuovo tentativo di solito è più veloce della prima esecuzione. I passaggi che devono essere eseguiti di nuovo vengono
addebitati di nuovo; i passaggi il cui risultato precedente può essere riutilizzato dalla cache non vengono addebitati di nuovo. Se il flusso di lavoro genera clip
a pagamento, Esegui di nuovo chiede ancora se riutilizzarle o pagarne di nuove.

I risultati del tentativo precedente non sono più mostrati nella pagina dell'esecuzione, ma vengono conservati nei tuoi
registri e nella cronologia dei crediti. Esegui di nuovo controlla il tuo piano e il tuo saldo di crediti come
fa l'avvio di una nuova esecuzione.

Interrompi annulla il passaggio in corso; non lo lascia finire. Dopo che premi Interrompi, Esegui di nuovo viene rifiutato finché l'
esecuzione non si è fermata del tutto, il che di solito richiede meno di un minuto. Fino ad allora dice che l'esecuzione si sta ancora fermando.

Se il flusso di lavoro è stato eliminato e l'esecuzione è troppo vecchia per conservare una propria copia, Esegui di nuovo non è disponibile;
avvia invece una nuova esecuzione da un flusso di lavoro.

Per cambiare qualcosa prima di riprovare, modifica il flusso di lavoro, salva e avvia una nuova esecuzione.

## Dove finiscono i risultati {#where-the-results-end-up}

Passaggi diversi producono tipi diversi di risultati. Fai clic su un passaggio completato nella pagina dell'esecuzione per vedere
il suo pannello dei risultati.

- **Video.** Un passaggio che ha prodotto un video lo mostra in un player **Video di questo passaggio** nel suo
  pannello dei risultati. Tutti i video prodotti nel progetto compaiono anche nella scheda **Render** del progetto (e in
  "Ultimi render" nella Panoramica). Tra questi ci sono il promo finale del passaggio `AuthorAgent`,
  il video montato di un passaggio `VideoCompile`, le clip generate da un passaggio `VideoGenerate` e le
  grafiche o le scene di inserto generate da un passaggio `MotionGraphicsPlanner`.
- **Video montato come file del progetto.** Per impostazione predefinita un passaggio `VideoCompile` aggiunge il proprio risultato anche ai
  **File** del progetto (`registerProjectFile`, attivo di default), così puoi usarlo come sorgente di un
  nuovo montaggio. Ogni esecuzione conserva lì un solo file: quando un ciclo di revisione rimonta il video, o un passaggio viene
  riprovato, il video più recente sostituisce il più vecchio in **File** (anche un flusso di lavoro che usa sempre lo stesso
  nome di file sostituisce il file dell'esecuzione precedente). I video più vecchi non vengono eliminati; li trovi
  ancora nella pagina della loro esecuzione e in **Render**.
- **Registri dettagliati.** I passaggi `VideoAnalyze`, `VideoCompile` e `VideoGenerate` conservano anche un grande
  registro dettagliato (l'analisi completa, l'elenco completo dei tagli o l'elenco delle clip generate), mostrato
  nella scheda **Dati** del passaggio, che compare dopo che apri **Dettagli** nella pagina dell'esecuzione. Sono informazioni, non video.
- **Timeline.** Il risultato di un passaggio `VideoCompile` offre **Apri nell'editor**, una timeline interattiva
  che mostra i tagli, le grafiche e l'audio del montaggio.
- **Riepiloghi.** Il risultato testuale di ogni passaggio è mostrato nel suo pannello. Per un passaggio `VideoCompile` dice
  cosa è stato applicato e cosa è stato saltato e perché (per esempio una grafica o un inserto che non è stato possibile
  usare), quindi controllalo quando nel video manca qualcosa che ti aspettavi.
- **Discussioni delle stanze.** I passaggi stanza conservano la discussione del loro panel, così puoi leggere come si è arrivati
  alla decisione. È solo informativa.
- **Modifiche a un video animato.** Un'esecuzione terminata che ha creato un video animato ha una scheda **Modifica questo
  video** nella sua pagina: descrivi cosa deve cambiare, da solo o insieme a modifiche ai passaggi, e ReelBolt crea una nuova versione senza
  eseguire di nuovo il flusso di lavoro. Vedi [Richiedere modifiche a un video finito](request-changes.md).

## Dove sono finite le take precedenti di un video {#where-earlier-takes-of-a-video-went}

Ogni volta che un flusso di lavoro viene eseguito di nuovo, o un ciclo di revisione chiede un altro montaggio, il video viene rifatto.
Ogni versione è una **take** dello stesso video. Per rendere facile trovare il video più recente, la
scheda **Render** del progetto e gli **Ultimi render** della Panoramica mostrano ogni video una sola volta, con la sua
ultima take: la versione più recente di un'esecuzione terminata. Un'esecuzione ancora in corso non la
sostituisce mai finché non termina.

Le take precedenti non vanno perse:

- Nella scheda **Render**, sotto il video che stai guardando, fai clic su **N take precedenti**. Ogni take precedente
  si può riprodurre e scaricare. "Take in corso" indica una versione che l'esecuzione ha sostituito lungo la
  strada verso quella finale (per esempio un passaggio di un ciclo di revisione).
- In **File**, le take precedenti dei tuoi video sono nascoste; fai clic su **Mostra N take precedenti dei tuoi
  video** per elencare anche quelle.

**Pulizia.** Fai clic sulla puntina accanto a una take per tenerla. **Elimina le take precedenti** elimina in una volta tutte le
take precedenti di quel video (dopo la tua conferma); l'ultima take e quelle fissate restano. Puoi
anche eliminare una singola take precedente con la sua icona del cestino. Eliminare una take rimuove per sempre il file video e la sua
voce in File; l'esecuzione stessa, e ciò che ogni passaggio ha riportato, restano nella cronologia dell'esecuzione.
L'unica eccezione è un file video che una versione della tua composizione usa ancora. ReelBolt conserva quel
file, e la sua voce in File, così la composizione continua a essere riprodotta.

## Riutilizzare i risultati precedenti (la cache dei passaggi) {#reusing-earlier-results-the-step-cache}

ReelBolt può **riutilizzare il risultato precedente di un passaggio** invece di rifarlo, quando il passaggio e tutto ciò
che legge sono esattamente uguali a quelli di un'esecuzione precedente riuscita (stesse impostazioni, stessi risultati precedenti,
stesso testo di richiesta, stessi file del progetto). Questo fa risparmiare tempo e costi AI, e per i passaggi `VideoGenerate`
fa risparmiare denaro.

- Riutilizzati per impostazione predefinita: i passaggi `Extract`, `VideoAnalyze`, `VideoCompile`, `Voiceover`, `EditRoom`,
  `ColorGradeRoom`, `VideoGenerate` e i passaggi `Agent` il cui agente si limita a leggere.
- Mai riutilizzati: gli agenti che scrivono codice, scrivono file o generano (`RemotionComponentTranslator`,
  `AuthorAgent`, `MotionGraphicsPlanner`), `GraphicsRoom`, `ReviewLoop`, `Conditional`, `ForEach`,
  `Parallel`.
- Il `cacheMode` di ogni passaggio prevale su questo: `Never` rifà sempre il passaggio, `Always` consente sempre
  il riutilizzo. I template impostano su `Never` ogni passaggio AI creativo, così una nuova esecuzione ottiene una decisione nuova.
- Un passaggio riutilizzato appare come proveniente dalla cache nella pagina dell'esecuzione.

Poiché un input cambiato cambia tutto ciò che viene dopo, modificare le impostazioni di un passaggio iniziale fa rifare
automaticamente il lavoro ai passaggi successivi.

## Clip video a pagamento: riutilizzare o rigenerare {#paid-video-clips-reuse-or-regenerate}

I passaggi `VideoGenerate` costano denaro, quindi ReelBolt evita di pagare due volte:

- **Il riutilizzo è l'opzione predefinita.** Quando una clip con lo stesso prompt e le stesse impostazioni è già stata generata
  con successo (in questa esecuzione o in una precedente del progetto) ed esiste ancora, viene riutilizzata senza
  costi. Vale anche quando un ciclo di revisione fa tornare indietro l'esecuzione.
- **Rigenera** quando esegui o riprovi il flusso di lavoro per pagare clip nuove. La finestra mostra il costo massimo
  possibile.
- Il `loopBackBehavior` di un passaggio impostato su `"Regenerate"` paga clip nuove a ogni ciclo di revisione
  dopo il primo; il valore predefinito `"Reuse"` no.
- Il `cacheMode` di un passaggio impostato su `"Never"` impedisce solo che l'intero passaggio venga riprodotto; le singole clip
  identiche vengono comunque riutilizzate, a meno che tu non scelga **Rigenera**.

La spesa ha sempre un tetto: il `maxSpendCredits` di ogni passaggio, più i limiti giornalieri per progetto e per
l'intera piattaforma stabiliti dall'amministratore. Se un tetto verrebbe superato, **non** viene acquistato nulla e il
passaggio fallisce con `BUDGET_EXCEEDED`. Vedi la [ricetta sul b-roll generato](../recipes/generated-b-roll.md).
