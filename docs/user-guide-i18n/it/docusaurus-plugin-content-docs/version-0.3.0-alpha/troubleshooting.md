---
title: Risoluzione dei problemi
sidebar_position: 14
description: I problemi e i messaggi di errore più comuni di ReelBolt, cosa li causa e come risolverli.
i18n_source_sha: 4f68768373fb22b7613b34ca886820e87734a94a
---

# Risoluzione dei problemi {#troubleshooting}

Ogni sezione qui sotto tratta un problema: cosa vedi, perché succede e come risolverlo. I codici di errore in maiuscolo (come `BUDGET_EXCEEDED`) sono mostrati nei dettagli del passaggio fallito nella pagina dell'esecuzione; fai clic sul passaggio rosso nel diagramma **Passaggi** per leggerli (apri **Dettagli** nella pagina dell'esecuzione per vedere i dati grezzi di ogni passaggio). Puoi anche chiedere all'[assistente](./assistant/index.md) "Perché è fallita la mia ultima esecuzione?" nella pagina dell'esecuzione.

## "La ricerca nei contenuti non è ancora disponibile" quando cerchi nei file del progetto {#content-search-is-not-available-yet-when-searching-project-files}

**Sintomo.** Nella scheda **File** di un progetto, **Cerca nei file "…"** mostra **La ricerca nei contenuti non è ancora disponibile**. Tramite l'API, la ricerca risponde con `indexNotReady: true` e nessun risultato.

**Causa.** O per l'installazione non è configurato nessun provider di Embedding, oppure i file del progetto non sono ancora stati indicizzati (resi ricercabili). I file caricati prima che esistesse un provider sono segnati come `Failed` nello stato **Indice di ricerca**.

**Soluzione.** Chiedi a un amministratore di aggiungere un provider di Embedding (vedi [Ricerca semantica](./admin/semantic-search.md) e [Provider di inferenza](./admin/inference-providers.md)). Poi, nella scheda **File**, apri **⋮ → Reindicizza per la ricerca** e **Metti in coda** (25 file per volta; ripeti per averne altri). Nel frattempo il filtro per nome continua a funzionare.

## Risultati di ricerca mancanti dopo il cambio dei modelli di embedding {#search-results-missing-after-switching-embedding-models}

**Sintomo.** La ricerca per significato funzionava, un amministratore ha cambiato il provider di Embedding predefinito o il suo modello, e ora le ricerche restituiscono poco o nulla anche se i file risultano `Indexed`.

**Causa.** Ogni modello di embedding ha un proprio indice separato, e quello nuovo parte vuoto. I file indicizzati con il modello vecchio dicono ancora `Indexed`, quindi l'azione collettiva **Reindicizza per la ricerca** li salta.

**Soluzione.** Usa **Reindicizza** su ogni file che ti serve, oppure fai reindicizzare l'intero progetto con `includeIndexed: true` a qualcuno che ha accesso all'API. I dettagli sono in [Ricerca semantica](./admin/semantic-search.md).

## TRANSCRIPTION_UNAVAILABLE o TRANSCRIPTION_FAILED in un passaggio di analisi video {#transcription_unavailable-or-transcription_failed-in-a-video-analysis-step}

**Sintomo.** Un passaggio `VideoAnalyze` fallisce con `TRANSCRIPTION_UNAVAILABLE` ("Transcription is Required but no transcription-capable inference provider is configured.") o `TRANSCRIPTION_FAILED`.

**Causa.** L'impostazione di trascrizione del passaggio (`transcription`) è `Required`, il che dice a ReelBolt di fermarsi invece di montare senza una trascrizione. O nessun provider di riconoscimento vocale (`Transcription`) è impostato come predefinito, oppure quello esistente ha restituito un errore.

**Soluzione.** In alternativa:

- chiedi a un amministratore di aggiungere un provider `Transcription` e renderlo predefinito (il servizio locale `whisper` incluso funziona; vedi [Provider di inferenza](./admin/inference-providers.md)), e fai clic su **Testa connessione** per confermare che funzioni; oppure
- cambia l'impostazione `transcription` del passaggio in `Optional` (il valore predefinito), che prosegue senza trascrizione quando non ce n'è una disponibile, oppure in `Off`.

`VISION_UNAVAILABLE` è la stessa situazione per le descrizioni di ciò che è sullo schermo: l'impostazione `vision` del passaggio è `Required` ma nessun provider `Vision` è quello predefinito.

## Anthropic, Gemini o DeepSeek non si possono usare per la trascrizione {#anthropic-gemini-or-deepseek-cannot-be-used-for-transcription}

**Sintomo.** Quando salvi o testi un provider, ReelBolt rifiuta con un messaggio come "Anthropic providers cannot serve the Transcription capability — Anthropic exposes no speech-to-text API", e l'elenco **Capacità** non offre **Trascrizione (ASR)** per questi tipi.

**Causa.** Anthropic (Claude), Google Gemini e DeepSeek non offrono un servizio di riconoscimento vocale che ReelBolt possa usare. Lo stesso vale per altri abbinamenti: la generazione video è solo MiniMax, e gli embedding richiedono Azure OpenAI o un servizio compatibile con OpenAI.

**Soluzione.** Tieni la tua connessione Anthropic, Gemini o DeepSeek per `Chat` (e `Vision`), e aggiungi un provider `Transcription` separato di tipo **Azure OpenAI**, **Compatibile con OpenAI** (come il servizio `whisper` incluso) o **Fish Audio**. Vedi la tabella in [Provider di inferenza](./admin/inference-providers.md).

## BUDGET_EXCEEDED in un passaggio di generazione video {#budget_exceeded-in-a-video-generation-step}

**Sintomo.** Un passaggio `VideoGenerate` fallisce con `BUDGET_EXCEEDED`, e non è stata creata nessuna clip.

**Causa.** Prima di acquistare una qualsiasi clip, ReelBolt controlla tre limiti di spesa, e almeno uno sarebbe stato superato:

- il tetto di **Spesa massima** del passaggio (`maxSpendCredits`), confrontato con il costo stimato di ogni clip che il passaggio creerebbe;
- il budget giornaliero del progetto (l'equivalente in crediti di 20 dollari USA per giorno UTC, per impostazione predefinita);
- il budget giornaliero dell'intera installazione (l'equivalente in crediti di 50 dollari USA per giorno UTC, per impostazione predefinita).

Quando succede non viene acquistato nulla, quindi non ti è stato addebitato nulla.

L'errore ora dice quale limite ha fermato il passaggio. Per esempio: "Servono circa 120 crediti di generazione video ma il budget video giornaliero del progetto è di 833,33 crediti e oggi ne sono già stati usati 790; si azzera alle 00:00 UTC (tra 5 h 12 min). Genera meno clip o aspetta." Nomina il limite ("il limite di spesa di questo passaggio", "il budget video giornaliero del progetto", "il budget video giornaliero del tuo piano" o "il budget video giornaliero della piattaforma"), quanto è già stato usato oggi e quando il giorno si azzera. Se è stato raggiunto il limite giornaliero della piattaforma, ti dice solo questo e quando si azzera. Quando chiedi all'assistente dell'esecuzione, può anche indicarti quanto budget video resta oggi per il progetto.

**Soluzione.**

- Se l'errore dice che il costo stimato del piano supera il limite del passaggio, alza `maxSpendCredits` sul passaggio, oppure chiedi meno clip o clip più brevi (meno riprese, una durata più breve o un `maxClips` più basso).
- Se il messaggio dice che è stato raggiunto un budget video giornaliero, aspetta che si azzeri alle 00:00 UTC (il messaggio dice quanto manca), oppure chiedi a chi gestisce l'installazione di alzare i budget giornalieri.
- Quando riesegui, scegli **riusa le clip precedenti** invece di **Rigenera (costa denaro)**, così le clip già create vengono riusate gratis.

## Un'esecuzione viene rifiutata perché lo spazio di lavoro ha finito i crediti {#a-run-is-refused-because-the-workspace-is-out-of-credits}

**Sintomo.** Un'esecuzione viene rifiutata prima di partire — o viene rifiutato un passaggio che richiede più crediti di quelli disponibili — e il messaggio nomina un limite del piano o dei crediti. L'esecuzione non è partita e non è stato addebitato nulla.

**Causa.** Il piano dello spazio di lavoro non ha abbastanza crediti disponibili per il lavoro, oppure il piano non include il tipo di passaggio che l'esecuzione vuole (una stanza di montaggio o un video generato su un piano che non li ha). ReelBolt rifiuta prima di eseguire il passaggio per non spendere troppo, quindi un'esecuzione che non ci si può permettere non parte mai e non viene addebitato nulla.

Quando il messaggio dice "Questa esecuzione richiede fino a 788 crediti per partire (di solito costa circa 286); lo spazio di lavoro ne ha 500 disponibili, quindi ne servono altri 288. I crediti non usati vengono restituiti al termine dell'esecuzione.", la prima cifra è il massimo che l'esecuzione può mettere da parte prima di partire, la cifra tra parentesi è quanto costa di solito, e l'ultima è quanti crediti in più ti servono per avviarla. Spesso un'esecuzione costa meno di quanto serve per partire, e i crediti che non usa vengono restituiti al termine. Vale per un'esecuzione avviata dalla dashboard, dall'assistente e da Claude Code, e per una modifica a un video finito.

**Soluzione.**

- Su un piano a pagamento, ricarica crediti da **Impostazioni → Piano e fatturazione → Acquista crediti**, oppure passa a un piano più grande i cui crediti mensili siano adatti al tuo uso (vedi [Piani e crediti](./account/plans-and-credits.md)).
- Se il rifiuto nomina una funzione che il piano non include (stanze o video generato), passa a un piano che la ha, oppure cambia il flusso di lavoro con un tipo di passaggio che il piano consente.
- Su un'installazione self-hosted con la fatturazione disattivata non c'è un saldo che possa finire — le pagine di fatturazione sono semplicemente spente, quindi questo errore lì non comparirebbe.

## Un passaggio fallisce con NO_RUNNER_ONLINE {#a-step-fails-with-no_runner_online}

**Sintomo.** Un'esecuzione si ferma a un passaggio multimediale con l'errore `NO_RUNNER_ONLINE`, e il passaggio non viene eseguito.

**Causa.** La preferenza di calcolo dello spazio di lavoro è **Solo le mie macchine** (la scelta "Solo le mie macchine" in **Spazio di lavoro → Impostazioni → Dove gira il tuo lavoro**), e nessuna macchina collegata era online e in grado di prendere il passaggio. Quella scelta usa solo le tue macchine collegate: l'esecuzione si ferma lì invece di far girare o addebitare in silenzio il lavoro su ReelBolt. Collega una macchina e lasciala online, oppure passa ad **Automatico** o a **Non usare le mie macchine**. Vedi [Runner desktop](./account/desktop-runner.md).

**Soluzione.**

- Avvia (o lascia acceso) la macchina collegata su cui vuoi eseguire il lavoro, assicurati che sia collegata a questo spazio di lavoro in **Runner**, ed esegui di nuovo il passaggio.
- Se non devi forzarlo sulla tua macchina, cambia la preferenza in **Preferisci le mie macchine** o **Automatico**, così un passaggio può girare sulla piattaforma quando la tua macchina è offline.
- Se il passaggio è un render Remotion, la macchina deve aver segnalato un runtime di container (Docker o Podman); una macchina con solo ffmpeg non può prendere i render.

## EGRESS_REFUSED in un passaggio di generazione video {#egress_refused-in-a-video-generation-step}

**Sintomo.** Un passaggio `VideoGenerate` che lavora da un piano di inquadrature fallisce con `EGRESS_REFUSED` prima che venga generato qualcosa.

**Causa.** Il piano di inquadrature chiedeva che una clip generata iniziasse o finisse su un fotogramma preso dai tuoi filmati (un `firstFrame` o `lastFrame`). Farlo significherebbe inviare una parte dei tuoi filmati al servizio di generazione video, e l'impostazione `allowSourceMediaEgress` del passaggio è `false` (il valore predefinito), che lo vieta. L'intero passaggio viene rifiutato così non viene acquistato nulla.

**Soluzione.** Imposta `allowSourceMediaEgress` su `true` nel passaggio, se ti sta bene che dei fotogrammi dei tuoi filmati lascino la tua installazione, oppure cambia il piano in modo che le inquadrature non richiedano un fotogramma iniziale o finale. Con il permesso concesso, ReelBolt estrae davvero quel singolo fotogramma dall'inquadratura indicata dal piano e lo invia con la richiesta della clip — il registro della clip nel risultato del passaggio dice se è stato inviato, e il modello **Footage edit with AI-generated b-roll** concede già questo permesso per te.

Se invece il passaggio fallisce con `KEYFRAME_NOT_SUPPORTED`, il motivo è diverso: il servizio di generazione video a cui il passaggio è stato risolto non può accettare un'immagine, quindi ReelBolt rifiuta prima di acquistare qualcosa, invece di pagare una clip che scarterebbe in silenzio il fotogramma. Punta il passaggio a un provider che accetti un'immagine in ingresso.

## INSERTS_REQUIRE_REENCODE, GENERATED_CLIPS_REQUIRE_REENCODE e errori di compilazione simili {#inserts_require_reencode-generated_clips_require_reencode-and-similar-compile-errors}

**Sintomo.** Un passaggio `VideoCompile` fallisce subito con `INSERTS_REQUIRE_REENCODE` o `GENERATED_CLIPS_REQUIRE_REENCODE`. I codici correlati con la stessa causa sono `GRAPHICS_REQUIRE_REENCODE`, `MUSIC_REQUIRES_REENCODE`, `SFX_REQUIRES_REENCODE`, `COLOR_GRADE_REQUIRES_REENCODE`, `TRANSITIONS_REQUIRE_REENCODE` e `MULTI_SOURCE_REQUIRES_REENCODE`.

**Causa.** Il `mode` del passaggio di compilazione è impostato su `StreamCopy`, una modalità veloce che copia un taglio continuo unico senza rifare il render dell'immagine (un taglio con più parti viene ricodificato). Inserti di schermata tracciati, clip di b-roll generate, grafiche, musica, effetti sonori, correzione colore, transizioni e unione di più video sorgente cambiano tutti l'immagine o l'audio, quindi richiedono di rifare il render del video.

**Soluzione.** Imposta il `mode` del passaggio di compilazione su `Reencode` (il valore predefinito), oppure disattiva la funzione indicata nell'errore (per esempio `enableInserts` o `enableGeneratedClips`). Vedi [Tipi di passaggio](./workflows/step-types.md) e la [ricetta dell'inserto di schermata tracciato](./recipes/tracked-screen-insert.md).

## Un'esecuzione sembra bloccata in Running o Queued {#a-workflow-run-seems-stuck-in-running-or-queued}

**Sintomo.** La pagina dell'esecuzione mostra **IN ESECUZIONE** per molto tempo senza che nessun passaggio finisca, oppure resta **IN CODA**.

**Causa.** Ci sono varie possibilità:

- **È solo lenta.** I passaggi agente che costruiscono e generano animazioni, e l'analisi di filmati lunghi, possono richiedere molto tempo, soprattutto su servizi AI più lenti. Ogni passaggio agente ha un limite di tempo (50 minuti per impostazione predefinita) oltre il quale fallisce con "Agent '…' timed out after … seconds".
- **Sta aspettando uno slot.** Solo un numero limitato di esecuzioni lavora contemporaneamente (4 per impostazione predefinita); le altre aspettano come `Queued` finché una non finisce.
- **Il sistema è stato riavviato a metà esecuzione.** Dopo un aggiornamento o un riavvio, un'esecuzione che stava lavorando riparte automaticamente entro un minuto o due. Se il servizio dei flussi di lavoro è stato fermato bruscamente, l'esecuzione può mostrare per breve tempo `Running` anche se nulla ci sta lavorando, finché ciò non avviene.

**Soluzione.** Controlla il diagramma **Passaggi** e la riga "cosa sta succedendo ora" (apri **Dettagli** per i contatori di token in tempo reale): se i passaggi avanzano o i token aumentano, lasciala andare. Se per molto tempo non cambia nulla, fai clic su **Interrompi**, che segna l'esecuzione come `Cancelled`, poi fai clic su **Riprendi** per proseguire dal passaggio che non è finito (o su **Riavvia** per ricominciare da capo). Se i passaggi agente continuano ad andare in timeout su un servizio AI lento, chiedi a chi gestisce l'installazione di alzare il limite di tempo di quell'agente (l'impostazione `Agents:<AgentName>:RunTimeoutSeconds`).

## Una macchina collegata non prende lavoro {#a-paired-machine-is-not-taking-work}

**Sintomo.** Una macchina che hai collegato non riceve lavoro multimediale. Con la preferenza **Solo le mie macchine** un passaggio che avrebbe girato lì fallisce con `NO_RUNNER_ONLINE`; con le altre preferenze il passaggio gira invece su ReelBolt.

**Causa.** Il governor della macchina l'ha fermata per un motivo che sa valutare, oppure la macchina è semplicemente offline. Esegui `reelbolt-runner status` sulla macchina: la riga indica il motivo — `user-active` (sei alla tastiera, in modalità di pianificazione `idle`), `on-battery`, `outside-window` (una fascia oraria) o `paused` (una pausa esplicita). Una macchina fermata dalla pianificazione ha chiuso del tutto la connessione al gateway mentre è inattiva, quindi non tiene nemmeno una connessione aperta.

**Soluzione.** Premi **Riprendi** nella schermata **Home** dell'app desktop (o esegui `reelbolt-runner resume`) per togliere una pausa. Se è la pianificazione a trattenerla, Home lo dice e indica quando cambierà ("il lavoro riprende lun 09:00"); cambiala in **Impostazioni → Quando lavorare** nell'app. Se non risponde nulla, ferma e riavvia il processo del runner: una pausa non viene mai salvata in modo persistente, quindi dopo un riavvio torna sempre a lavorare. Le impostazioni complete e i valori predefiniti sono in [Impostazioni dell'app desktop](./account/desktop-runner.md#desktop-app-settings) nella pagina [Runner desktop](./account/desktop-runner.md).

## Un'esecuzione è stata annullata e non l'hai fermata tu {#a-workflow-run-was-cancelled-and-you-did-not-stop-it}

**Sintomo.** Un'esecuzione mostra **ANNULLATA** con il messaggio "Interrotta: il motore dei flussi di lavoro si è arrestato mentre questa esecuzione era in corso." (esecuzioni più vecchie), oppure **FALLITA** con "Interrotta: istanza del motore persa".

**Causa.** Il servizio dei flussi di lavoro è stato riavviato o arrestato (per esempio durante una manutenzione o un aggiornamento) mentre la tua esecuzione lavorava. Oggi ReelBolt rimette un'esecuzione così in coda, una sola volta e in automatico, e prosegue dal passaggio che non era finito. Un'esecuzione già ripresa una volta, o che era nel mezzo dell'acquisto di una clip video generata, non viene ripresa automaticamente, così non si paga nulla due volte; finisce come descritto sopra. Un'esecuzione fermata da te dice invece "Stopped by user" seguito dal tuo id utente.

**Soluzione.** Fai clic su **Riprendi** nella pagina dell'esecuzione per proseguire dal passaggio che non era finito, senza pagare di nuovo i passaggi che lo erano. Se Riprendi non è offerto, fai clic su **Riprova**; i passaggi già finiti possono allora essere riusati dalla cache.

## Riprova viene rifiutato con "Questa esecuzione è già in corso o in coda." {#retry-is-refused-with-this-run-is-already-running-or-queued}

**Sintomo.** Riprovare un'esecuzione fallisce con quel messaggio.

**Causa.** Un'esecuzione si può riprovare solo dopo che è terminata, fallita o annullata.

**Soluzione.** Aspetta che finisca, oppure fai clic prima su **Interrompi** e poi su **Riprova**.

## Riprova viene rifiutato con "Questa esecuzione si sta ancora fermando" {#retry-is-refused-with-this-run-is-still-stopping}

**Sintomo.** Premendo **Riavvia** su un'esecuzione che hai fermato compare "Questa esecuzione si sta ancora fermando. Riprova tra un momento."

**Causa.** Interrompi annulla il passaggio in corso; non lo lascia finire. ReelBolt rifiuta **Riavvia** finché l'esecuzione fermata non si è fermata del tutto, così la nuova esecuzione non si sovrappone mai a quella annullata.

**Soluzione.** Aspetta che l'esecuzione finisca di fermarsi, di solito meno di un minuto, poi premi **Riavvia**. La nuova esecuzione riparte dall'inizio. I passaggi che devono essere rieseguiti vengono addebitati di nuovo. I passaggi il cui risultato precedente si può riusare dalla cache non vengono addebitati di nuovo.

## Riprendi viene rifiutato con "Si può riprendere solo un'esecuzione fallita o fermata" {#resume-is-refused-with-only-a-failed-or-stopped-run-can-be-resumed}

**Sintomo.** Premendo **Riprendi** compare "Si può riprendere solo un'esecuzione fallita o fermata."

**Causa.** Si può riprendere solo un'esecuzione terminata come `Failed` o `Cancelled`. Un'esecuzione `Queued`, `Running` o `Passed` no.

**Soluzione.** Aspetta che l'esecuzione finisca, oppure premi **Interrompi** e aspetta che si sia fermata. Un'esecuzione riuscita non ha nulla da riprendere; avviane invece una nuova.

## Riprendi non è disponibile per un'esecuzione più vecchia {#resume-is-not-available-for-an-older-run}

**Sintomo.** **Riprendi** manca o viene rifiutato su un'esecuzione, con un messaggio che dice che la ripresa non è disponibile.

**Causa.** L'esecuzione è partita prima che ReelBolt conservasse una copia del flusso di lavoro per ogni esecuzione, quindi non c'è nulla da cui proseguire.

**Soluzione.** Premi **Riavvia**, che fa ripartire l'esecuzione dal passaggio 1. I passaggi i cui risultati precedenti si possono riusare dalla cache non vengono addebitati di nuovo.

## Riprendi viene rifiutato con "Questa esecuzione si sta ancora fermando" {#resume-is-refused-with-this-run-is-still-stopping}

**Sintomo.** Premendo **Riprendi** su un'esecuzione compare che l'esecuzione si sta ancora fermando.

**Causa.** L'ultimo passaggio dell'esecuzione non ha ancora finito di fermarsi. ReelBolt lo aspetta così l'esecuzione ripresa non si sovrappone mai a quella vecchia.

**Soluzione.** Aspetta un momento, di solito meno di un minuto, poi premi di nuovo **Riprendi**.

## Esegui di nuovo o Riprendi dice che l'esecuzione è cambiata mentre lo chiedevi {#run-again-or-resume-says-the-run-changed-while-you-asked}

**Sintomo.** Premendo **Riavvia** o **Riprendi** compare un messaggio come "Questa esecuzione è cambiata mentre lo chiedevi. Ricaricala e riprova", "Questa esecuzione era già stata rimessa in coda", "Questa esecuzione non esiste più" o "Il flusso di lavoro di questa esecuzione è stato eliminato mentre lo chiedevi".

**Causa.** L'esecuzione è cambiata tra il momento in cui hai aperto la pagina e il momento in cui hai premuto il pulsante. Qualcun altro nel progetto potrebbe aver premuto prima **Riavvia** o **Riprendi**, oppure aver eliminato il flusso di lavoro o l'esecuzione. Non è stato avviato nulla e non è stato addebitato nulla.

**Soluzione.** Ricarica la pagina e guarda lo stato attuale dell'esecuzione. Se è già in corso, lasciala finire. Se è terminata, premi ancora una volta **Riavvia** o **Riprendi**. Se il messaggio dice che il flusso di lavoro o l'esecuzione non ci sono più, non c'è più nulla da rieseguire.

## Riprendi viene rifiutato per via dei crediti o del piano {#resume-is-refused-because-of-credits-or-plan}

**Sintomo.** **Riprendi** o **Riavvia** viene rifiutato con un messaggio di crediti esauriti, un messaggio che dice che il tuo piano non include qualcosa che il flusso di lavoro usa, o un messaggio che dice che il tuo piano consente solo un certo numero di esecuzioni alla volta.

**Causa.** Entrambi controllano il tuo piano e poi il tuo saldo prima di partire. Se il limite del piano sulle esecuzioni contemporanee è raggiunto, il messaggio dice quante esecuzioni consente il tuo piano, e quel controllo viene prima di quello del saldo. Riprendi controlla il saldo solo per i passaggi che devono ancora essere eseguiti. Quel controllo è una stima: può differire un po' da ciò che costano i passaggi, e ogni passaggio viene controllato di nuovo quando parte.

**Soluzione.** Se il tuo piano consente solo un certo numero di esecuzioni alla volta, aspetta che una finisca o cambia piano. Se hai finito i crediti, aggiungine. Poi riprova. Vedi [Un'esecuzione viene rifiutata perché lo spazio di lavoro ha finito i crediti](#a-run-is-refused-because-the-workspace-is-out-of-credits).

## Un passaggio di clip video si ferma con "reservation orphaned" {#a-video-clip-step-stops-with-reservation-orphaned}

**Sintomo.** Un passaggio di generazione video fallisce per una clip con `RESERVATION_ORPHANED`.

**Causa.** Un tentativo precedente potrebbe aver ordinato la clip, ma ReelBolt non ha mai saputo se l'ordine fosse andato a buon fine. Quando riprendi o riprovi, ReelBolt ordina di nuovo una sola volta una clip così. Se la stessa clip risulta di nuovo orfana dopo, si ferma per evitare di pagarla ripetutamente.

**Soluzione.** Chiedi a chi gestisce l'installazione di controllare nell'account del servizio video se c'è un ordine vicino al momento della clip, poi riprendi.

## L'assistente dice che è stato raggiunto il limite di esecuzioni {#the-assistant-says-the-run-limit-was-reached}

**Sintomo.** Quando chiedi all'assistente (o a Claude Code) di eseguire un flusso di lavoro, segnala "limite di esecuzioni raggiunto — 10 esecuzioni a pagamento su 10 nell'ultima ora in questo progetto; riprova tra 13 minuti".

**Causa.** L'assistente limita quante esecuzioni avvia per ogni utente in ogni progetto nell'arco di un'ora mobile: fino a 10 esecuzioni che possono usare crediti (un'esecuzione con un modello AI o un servizio a pagamento, e ogni modifica a un video finito) e fino a 60 esecuzioni gratuite (solo passaggi di montaggio fissi). Il messaggio dice quale tipo hai raggiunto, quante ne hai usate, il limite e quanto manca perché l'esecuzione più vecchia dell'ora sia abbastanza vecchia da liberare un posto. Un'esecuzione gratuita non consuma mai le 10. Serve a proteggere da spese fuori controllo. Chi gestisce l'installazione può cambiare i numeri con le impostazioni `Assistant:MaxPaidExecutionsPerHour` e `Assistant:MaxFreeExecutionsPerHour`.

**Soluzione.** Aspetta il tempo indicato nel messaggio, poi chiedi di nuovo, oppure avvia tu l'esecuzione con **Avvia** nella pagina del flusso di lavoro, che non è soggetta a questo limite. Se il messaggio dice "Questo flusso di lavoro è cambiato dopo l'avvio dell'esecuzione; avviala di nuovo.", il flusso di lavoro è stato modificato tra il controllo e l'avvio. Non è stato addebitato nulla; chiedi di nuovo l'esecuzione.

## La proposta di flusso di lavoro dell'assistente dice "Proposta non aggiornata" {#the-assistants-workflow-proposal-says-proposal-out-of-date}

**Sintomo.** Fare clic su **Applica le modifiche** su una proposta di flusso di lavoro mostra **Proposta non più valida** e non cambia nulla.

**Causa.** Il flusso di lavoro è stato modificato (da te, o in un'altra scheda) dopo la proposta dell'assistente. ReelBolt rifiuta di applicare una proposta a qualcosa che non corrisponde più a ciò che ti era stato mostrato.

**Soluzione.** Chiedi all'assistente di riproporre la modifica, poi applica la nuova proposta.

## Nessun provider di generazione video configurato (PROVIDER_NOT_FOUND) {#no-video-generation-provider-configured-provider_not_found}

**Sintomo.** Un passaggio `VideoGenerate` fallisce con `PROVIDER_NOT_FOUND` ("No video generation provider configured."). I codici correlati provenienti dal servizio stesso sono `PROVIDER_AUTH_FAILED` (la chiave è stata rifiutata) e `PROVIDER_INSUFFICIENT_BALANCE` (l'account presso il servizio video ha esaurito il credito).

**Causa.** Nessun provider `VideoGeneration` è impostato come predefinito e il passaggio non ne indica uno, oppure il servizio ha rifiutato la richiesta.

**Soluzione.** Chiedi a un amministratore di aggiungere un provider MiniMax con la capacità **Generazione video**, impostarlo come predefinito e testarlo. Per un errore di autenticazione, controlla la chiave API; per saldo insufficiente, ricarica l'account presso il servizio video.

## Un endpoint di un provider viene rifiutato come indirizzo privato {#a-provider-endpoint-is-rejected-as-a-private-address}

**Sintomo.** Il salvataggio di un provider fallisce con "Endpoint must be a public https/http URL; internal/private/loopback addresses are not allowed."

**Causa.** I tipi solo cloud (Azure OpenAI, Anthropic, Gemini, DeepSeek, MiniMax, TypeSafe) devono puntare a un indirizzo pubblico su internet. Solo i tipi che si possono ospitare in autonomia (compatibile con OpenAI, Fish Audio e il server decisionale self-hosted) possono puntare a un server nella tua rete o dentro l'installazione.

**Soluzione.** Per un servizio che ospiti tu, scegli **Compatibile con OpenAI** (o **Fish Audio** per la voce narrante) come tipo. Per un fornitore cloud, usa il suo indirizzo pubblico.

## Un provider Anthropic, Gemini o DeepSeek fallisce senza chiave API {#an-anthropic-gemini-or-deepseek-provider-fails-with-no-api-key}

**Sintomo.** Un provider di uno di questi tipi fallisce il test o fallisce durante le esecuzioni, e il suo campo chiave API era stato lasciato vuoto.

**Causa.** Per questi tipi ReelBolt non usa mai in silenzio una chiave dall'ambiente del server; una chiave vuota è un errore.

**Soluzione.** Inserisci la chiave API, oppure inserisci il valore letterale `env:` per usare la chiave dalle variabili d'ambiente del server (`ANTHROPIC_API_KEY`, `GEMINI_API_KEY` o `DEEPSEEK_API_KEY`), che chi gestisce l'installazione deve impostare. Vedi [Provider di inferenza](./admin/inference-providers.md).

## Un provider non si può eliminare {#a-provider-cannot-be-deleted}

**Sintomo.** L'eliminazione di un provider viene rifiutata.

**Causa.** È il provider predefinito per la sua capacità, e ReelBolt non lascia una capacità senza il suo predefinito per caso.

**Soluzione.** Rendi prima predefinito un altro provider per quella capacità, poi elimina quello vecchio. Gli agenti che erano stati indirizzati in modo specifico a un provider eliminato tornano automaticamente a usare quello predefinito.

## Un utente non ha mai ricevuto la password reimpostata {#a-user-never-received-their-reset-password}

**Sintomo.** Un amministratore ha reimpostato la password di un utente, ma l'utente non ha ricevuto la nuova password temporanea.

**Causa.** Una password reimpostata viene sempre e solo inviata via email; non viene mostrata a schermo. Se l'installazione non ha l'email (SMTP) configurata, la nuova password non può essere consegnata.

**Soluzione.** Chiedi a chi gestisce l'installazione di configurare l'email (le impostazioni `SMTP_*`), poi reimposta di nuovo la password. Per i nuovi account è diverso: la loro password temporanea viene mostrata a schermo nella finestra **Utente creato**. Vedi [Gestione degli utenti](./admin/users.md).
