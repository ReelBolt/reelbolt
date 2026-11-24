---
title: Concetti
sidebar_position: 3
description: Le idee alla base di ReelBolt spiegate con parole semplici - progetti, file, flussi di lavoro, passaggi, agenti, esecuzioni, risultati, cache, brief e costi.
i18n_source_sha: c5bf1927ff4e67760c2c02794beb6e425902200b
---

# Concetti {#concepts}

Questa pagina spiega le parole che usa ReelBolt e come si incastrano i vari pezzi. Ogni sezione si capisce da sola, quindi puoi saltare direttamente a quella che ti serve.

## Spazi di lavoro in ReelBolt {#workspaces-in-reelbolt}

Uno **spazio di lavoro** è il contenitore che possiede tutto ciò che fai in ReelBolt: i tuoi progetti, i loro file, i flussi di lavoro, i render, gli agenti personalizzati e le connessioni AI. È anche l'unità con cui si condivide e si paga: chi sta nello stesso spazio di lavoro lavora sugli stessi progetti, e chi sta fuori non li vede affatto.

Ogni account ha un proprio **spazio di lavoro personale**, sempre mostrato come **Personale**, che contiene solo il suo proprietario. Quando devi lavorare con altre persone crei uno **spazio di lavoro del team** e le inviti; puoi farne parte di quanti vuoi e passare dall'uno all'altro dal menu in alto a sinistra. In uno spazio di lavoro ogni membro ha uno di quattro ruoli — **Proprietario**, **Amministratore**, **Membro** o **Fatturazione** — e il ruolo decide cosa può fare lì.

Ne derivano due cose, facili da fraintendere:

- **Un progetto che vedi tu è un progetto che vede il tuo spazio di lavoro**, non solo tu. L'eccezione è lo spazio di lavoro personale, dove ci sei solo tu.
- **L'amministrazione della piattaforma non è l'appartenenza a uno spazio di lavoro.** Il gruppo di menu chiamato **Piattaforma** appartiene a chi gestisce l'intera installazione e, di proposito, non dà alcuna vista sui progetti di nessuno spazio di lavoro.

Consulta [Spazi di lavoro e team](./workspaces.md) per i ruoli nel dettaglio, gli inviti, il cambio di spazio e come uscire.

## Progetti in ReelBolt {#projects-in-reelbolt}

Un **progetto** è un contenitore per un singolo lavoro video: un video di lancio di un prodotto, il montaggio di un tutorial, un teaser di una funzione. Contiene tre cose:

- i **file** che carichi e quelli prodotti durante il lavoro,
- i **flussi di lavoro** che descrivono come trasformare quei file in un video,
- i **render**, cioè i video finiti.

Un progetto appartiene allo **spazio di lavoro** in cui è stato creato. Tutti i membri di quello spazio il cui ruolo permette di lavorare sui progetti — Proprietario, Amministratore e Membro — possono vederlo ed eseguirne i flussi di lavoro; chi è fuori dallo spazio non lo vede affatto, e nemmeno un amministratore della piattaforma, che gestisce account e servizi AI e non i progetti degli altri. Eliminare un progetto elimina definitivamente tutto ciò che contiene. Vedi [Spazi di lavoro e team](./workspaces.md).

## File del progetto e le loro tre categorie {#project-files-and-their-three-categories}

Ogni file di un progetto ReelBolt ha una **categoria** che dice da dove viene. Vedrai questi nomi quando parli con l'assistente o leggi i dettagli di un file:

| Categoria | Mostrata nella scheda File come | Cosa contiene |
|---|---|---|
| `userFiles` | **Sorgenti** ("Caricato da te") | Tutto ciò che hai caricato: codice, filmati, musica, immagini, documenti. |
| `agentFiles` | **Generati** ("Creato da un agente") | File scritti dagli agenti durante le esecuzioni, come risultati di analisi, clip generate e audio di voce narrante sintetizzata. |
| `outputFiles` | **Render** ("Render") | Video finiti prodotti dalle esecuzioni. |

I file possono stare in **cartelle**. Le cartelle sono solo un modo per organizzare l'elenco: compaiono quando ci carichi o ci sposti un file, e una cartella vuota esiste solo finché non ci metti qualcosa. Puoi spostare i file tra cartelle, e anche tra categorie, con **Sposta in...**.

I file di testo ricevono un **riepilogo** automatico (poche righe su ciò che contiene) e vengono aggiunti all'**indice di ricerca** del progetto, così si possono cercare per significato. Le immagini (PNG, JPEG, GIF e WebP) invece vengono descritte dal modello di visione dell'installazione: la descrizione diventa il riepilogo dell'immagine ed è ciò su cui si basa la ricerca. Se non è configurato nessun modello di visione, le immagini restano senza riepilogo. Ogni altro tipo di file (video, audio, PDF, archivi, font e altri file binari) viene saltato in entrambi i casi. Il riepilogo aiuta gli agenti e l'assistente a capire il tuo materiale senza leggere ogni file per intero.

## Flussi di lavoro in ReelBolt {#workflows-in-reelbolt}

Un **flusso di lavoro** è una ricetta: un elenco ordinato di passaggi che trasforma i file del tuo progetto in un video. I flussi di lavoro appartengono a un solo progetto.

Puoi ottenere un flusso di lavoro in tre modi:

- **Automaticamente**: ogni nuovo progetto riceve **Promo video from your code v1** (chiamato **Quick Win Promo v1** nei progetti creati prima di ottobre 2026), un flusso di lavoro già pronto per una "promo dal tuo codice".
- **Da un modello**: nella scheda **Flussi di lavoro** del progetto, fai clic su **Nuovo flusso di lavoro**, scegli una delle card sotto **Parti da un modello** e fai clic su **Usa questo modello**. Vedi [Modelli di flussi di lavoro](./workflows/templates.md).
- **Costruendolo o descrivendolo**: aggiungi i passaggi tu stesso nel builder visuale, oppure chiedi all'[assistente](./assistant/index.md) di abbozzarne uno e consentigli di crearlo.

Puoi rinominare un flusso di lavoro, cambiarne i passaggi, attivare o disattivare **Richiedi un brief prima di ogni esecuzione** e fare clic su **Salva modifiche**. Per eseguire un flusso di lavoro si usa **Avvia**.

## Passaggi e tipi di passaggio in un flusso di lavoro ReelBolt {#steps-and-step-types-in-a-reelbolt-workflow}

Un **passaggio** è un singolo compito di un flusso di lavoro. I passaggi vengono eseguiti in ordine e ognuno può usare ciò che hanno prodotto quelli precedenti. Ogni passaggio ha un **tipo di passaggio**, che decide che genere di compito è:

- **Agent**: un agente AI fa il lavoro (scrive uno script, decide quali inquadrature tenere, costruisce un'animazione).
- **ReviewLoop**: un agente valuta il risultato e, se è sotto un punteggio minimo, rimanda il flusso di lavoro a un passaggio precedente per riprovare, fino a un numero massimo di volte.
- **Conditional**, **ForEach** e **Parallel**: controllano il flusso, scegliendo un ramo, ripetendo su un elenco o eseguendo più agenti insieme.
- **Extract**: riduce un risultato precedente molto grande alle sole parti che servono ai passaggi successivi, così le esecuzioni costano meno e sono più veloci.
- **VideoAnalyze**: esamina i filmati (pause, cambi di scena, parlato, cosa c'è sullo schermo) senza prendere alcuna decisione AI per conto suo.
- **VideoCompile**: taglia e assembla il video finale in base alle decisioni dei passaggi precedenti, aggiungendo, se richiesti, grafiche, musica, effetti sonori, colore e inserti.
- **EditRoom**, **GraphicsRoom** e **ColorGradeRoom**: più agenti discutono insieme una decisione, come una piccola squadra di montaggio, e un agente regista la chiude.
- **VideoGenerate**: crea nuove clip video con un servizio di generazione video a pagamento.
- **Voiceover**: trasforma il testo di uno script in audio di narrazione.

Ogni tipo di passaggio è descritto nel dettaglio in [Tipi di passaggio](./workflows/step-types.md).

## Agenti: integrati e personalizzati {#agents-built-in-and-custom}

Un **agente** è un lavoratore AI con un ruolo preciso e un insieme di istruzioni. Ogni passaggio Agent di un flusso di lavoro indica l'agente che deve fare il lavoro.

- Gli **agenti integrati** arrivano con ReelBolt: analizzatori di codice, un traduttore di scene che trasforma la tua interfaccia in scene animate, uno stratega dell'animazione, uno sceneggiatore, un regista, un autore che costruisce e genera il video, revisori e specialisti di montaggio video come l'editor della storia, il pianificatore di motion graphics, il supervisore musicale, il sound designer, il colorist e il direttore delle inquadrature. Le loro istruzioni non si possono modificare, perché il resto del sistema si basa su ciò che producono.
- Gli **agenti personalizzati** sono quelli che crei tu nella pagina **Agenti** con **Nuovo agente**: gli dai un **Nome**, una **Descrizione**, un **Prompt di sistema** (le sue istruzioni in linguaggio semplice) e un **Colore**. Gli agenti personalizzati si possono modificare in qualsiasi momento.

Ogni agente usa un servizio AI disponibile per lo spazio di lavoro: le connessioni con cui è stata configurata l'installazione di ReelBolt e, in uno spazio di lavoro del team il cui piano lo consente, le connessioni dello spazio di lavoro stesso, che lì hanno la precedenza. Un amministratore della piattaforma può indirizzare tutti gli agenti, ovunque, a un altro servizio AI; il Proprietario o un Amministratore di uno spazio di lavoro può fare lo stesso solo per quello spazio — per un tipo di agente integrato e per gli agenti personalizzati dello spazio (per esempio un modello più potente per il regista). Vedi [Provider di inferenza](./admin/inference-providers.md). L'elenco completo è in [Agenti](./workflows/agents.md).

## Esecuzioni e risultati dei passaggi {#executions-runs-and-step-results}

Un'**esecuzione** è un singolo avvio di un flusso di lavoro dall'inizio alla fine. Ogni esecuzione ha uno stato:

| Stato | Significato |
|---|---|
| `Queued` | In attesa di uno slot libero; solo un numero limitato di esecuzioni lavora contemporaneamente. |
| `Running` | Sta eseguendo i suoi passaggi. |
| `Passed` | Terminata con successo. |
| `Failed` | Un passaggio non è riuscito a completarsi; l'errore è mostrato nella pagina dell'esecuzione. |
| `Cancelled` | Fermata da te premendo **Interrompi**. Un'esecuzione interrotta da un aggiornamento o da un riavvio non viene annullata: riparte automaticamente, una sola volta. |

Ogni passaggio eseguito lascia un **risultato del passaggio**: cosa è entrato, cosa è uscito, il suo stato (`Pending`, `Running`, `Completed`, `Failed` o `Skipped`), l'eventuale errore, quanti token ha usato e quale tentativo era (un ciclo di revisione può eseguire lo stesso passaggio più volte). Puoi leggere tutto questo facendo clic su un passaggio nella pagina dell'esecuzione. Un passaggio che fallisce di solito viene ritentato in automatico prima che l'intera esecuzione venga segnata come `Failed`.

## Risultati e render {#outputs-and-renders}

Un **risultato** è tutto ciò che produce un'esecuzione. I risultati più importanti sono i **render**: file video riproducibili. I render compaiono nella scheda **Render** del progetto, sotto **Render** nella scheda **File** e nella scheda **Panoramica** sotto **Ultimi render**. Puoi riprodurli, **Scaricarli** e **Condividerli**.

Alcuni passaggi producono anche risultati non video che puoi ispezionare nella pagina dell'esecuzione, come un'analisi completa del filmato, l'elenco dei tagli o un manifesto delle clip generate. Le esecuzioni di montaggio video producono anche una timeline che puoi sfogliare nella sezione **Editor** (l'editor video è disponibile come anteprima anticipata).

## La cache dei passaggi: quando una nuova esecuzione riusa risultati precedenti {#the-step-cache-when-a-rerun-reuses-earlier-results}

Quando esegui di nuovo un flusso di lavoro, ReelBolt può riusare il risultato precedente di un passaggio invece di rifare il lavoro, se **non è cambiato nulla da cui quel passaggio dipende**. Ne fanno parte le impostazioni del passaggio, l'agente e le sue istruzioni, il servizio AI usato, tutto ciò che i passaggi precedenti hanno prodotto per lui, il tuo brief e i file del progetto. Se cambia una qualsiasi di queste cose, il passaggio viene eseguito da zero. I risultati riusati vengono conservati per un massimo di 7 giorni, per impostazione predefinita.

Un passaggio riusato è indicato con **riusato dalla cache** nella pagina dell'esecuzione, insieme a quanti token ha fatto risparmiare. Così le riesecuzioni sono più veloci ed economiche, e nella generazione video a pagamento una clip identica non viene pagata due volte.

Alcuni passaggi per impostazione predefinita non vengono mai riusati, perché modificano cose fuori dal proprio risultato o perché un giudizio nuovo è proprio lo scopo: i passaggi il cui agente scrive file di progetto o costruisce e genera video, i cicli di revisione, la stanza grafica e i passaggi creativi dei modelli integrati. I passaggi di analisi e di taglio vengono riusati per impostazione predefinita.

Ogni passaggio ha un'impostazione **Modalità cache** (la chiave JSON è `cacheMode`):

| Modalità cache | Significato |
|---|---|
| `Default` | Usa le regole integrate descritte sopra. |
| `Always` | Consente sempre il riuso per questo passaggio, anche dove le regole integrate non lo farebbero. Usala con cautela. |
| `Never` | Esegue sempre questo passaggio da zero. Usala quando vuoi ogni volta un'interpretazione creativa nuova. |

## Il tuo brief per un'esecuzione (Richiedi un brief prima di ogni esecuzione) {#your-brief-for-a-run-require-a-brief-before-each-run}

Ogni esecuzione può portare con sé un breve brief: quando fai clic su **Avvia**, la finestra di conferma ha un campo **Il tuo brief** per testo libero (per le app senza campi propri), come "resta sotto i 45 secondi e chiudi sulla pagina dei prezzi". Un'app che ha campi propri (vedi [Campi dell'app](workflows/app-inputs.md)) chiede quelli al posto di un brief libero. Lasciarlo vuoto va bene, a meno che il flusso di lavoro non abbia attivato **Richiedi un brief prima di ogni esecuzione** (nella pagina del flusso di lavoro; l'impostazione si chiama `requiresUserInput`): in quel caso l'esecuzione non può partire senza.

Il brief viene passato a ogni agente dell'esecuzione come contesto aggiuntivo e viene ricordato insieme all'esecuzione, quindi **Riprova** usa lo stesso brief. Poiché il brief fa parte di ciò che la cache dei passaggi confronta, modificarlo fa eseguire da zero i passaggi interessati.

## Costi e limiti di spesa in ReelBolt {#costs-and-spending-limits-in-reelbolt}

La maggior parte del lavoro di ReelBolt usa servizi AI collegati al tuo spazio di lavoro (suoi o dell'installazione). Quei servizi di solito fanno pagare a consumo; ReelBolt mostra quanti token ha usato ogni passaggio nella pagina dell'esecuzione, ma il conto vero lo emette il fornitore del servizio, non ReelBolt.

**La generazione video a pagamento è l'unico punto in cui ReelBolt protegge attivamente la tua spesa.** Un passaggio `VideoGenerate` deve avere un tetto di **Spesa massima** (`maxSpendCredits`, in **crediti** — la stessa unità del resto della tua fatturazione). Prima di comprare qualsiasi cosa, ReelBolt controlla quattro limiti e rifiuta l'intero passaggio, senza acquistare nulla, se anche uno solo verrebbe superato:

- il tetto di **Spesa massima** del passaggio, confrontato con il costo stimato di ogni clip che creerebbe,
- un **budget giornaliero per progetto** (l'equivalente in crediti di 20 dollari USA al giorno, per impostazione predefinita),
- il budget video giornaliero del **piano del tuo spazio di lavoro** (un piano senza generazione video non ne ha affatto, quindi il passaggio viene rifiutato),
- un **budget giornaliero su tutta l'installazione** (l'equivalente in crediti di 50 dollari USA al giorno, per impostazione predefinita).

I giorni sono contati in UTC. I budget giornalieri del progetto e dell'installazione sono impostati da chi gestisce l'installazione; il budget video giornaliero del tuo spazio di lavoro dipende dal suo piano.

Quando avvii o ritenti un flusso di lavoro che genera video, ReelBolt chiede se **riusare le clip precedenti** (gratis: viene riusata una clip identica già creata) o **Rigenera (costa denaro)**. Il limite massimo mostrato è la somma dei tetti di Spesa massima dei passaggi video; il costo reale di solito è inferiore. Vedi [Risoluzione dei problemi](./troubleshooting.md) per sapere cosa significa un errore `BUDGET_EXCEEDED`.
