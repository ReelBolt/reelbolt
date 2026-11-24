---
title: Provider di inferenza
sidebar_position: 2
description: Come gli amministratori della piattaforma ReelBolt collegano i servizi AI dell'installazione, e come uno spazio di lavoro del team collega i propri, per agenti, trascrizione, sottotitoli, generazione video, voce narrante e ricerca.
i18n_source_sha: 3b6b6ed2b40dc4bacf0babe874a266233c730aa2
---

# Provider di inferenza {#inference-providers}

Un **provider di inferenza** è una connessione a un servizio AI: il posto in cui ReelBolt manda il lavoro quando un agente deve ragionare, quando le riprese hanno bisogno di una trascrizione, quando va generata una clip, e così via. Nulla di ciò che usa l'AI funziona finché non è configurato almeno un provider, e gli utenti comuni non devono mai toccare queste pagine per *usare* ReelBolt: servono solo per cambiare dove viene inviato il lavoro.

Le connessioni sono **su due livelli**, e questa pagina li copre entrambi:

| Livello | Dove si gestisce | Chi può modificarlo |
|---|---|---|
| **Gestito dalla piattaforma**: le connessioni proprie dell'installazione, condivise da ogni spazio di lavoro | **Piattaforma → Inferenza** (`/app/admin/inference-providers`) | Solo gli amministratori della piattaforma |
| **Spazio di lavoro (porta il tuo)**: le connessioni proprie di uno spazio di lavoro del team | **Spazio di lavoro → Provider** (`/app/org/inference-providers`) | I Proprietari e gli Amministratori di quello spazio di lavoro |

Il resto di questa pagina riguarda la pagina **Piattaforma**. La pagina dello spazio di lavoro offre una versione ridotta dello stesso modulo, descritta più avanti in "Le connessioni proprie di uno spazio di lavoro".

## Come è organizzato un provider {#how-a-provider-is-organized}

Un provider si configura in un massimo di quattro livelli:

| Livello | Che cos'è | Esempio |
|---|---|---|
| **Provider** | Un account presso un servizio: tipo, indirizzo, chiave API e timeout. Aggiungi ogni account una sola volta. | "Anthropic" con la chiave della tua azienda |
| **Modello** | Un modello che usi da quell'account, con il nome che le persone vedono quando lo scelgono. Un provider può contenere molti modelli. | `claude-sonnet-5`, mostrato come "Claude Sonnet" |
| **Capacità** | Un compito che un modello svolge per ReelBolt, come `Chat` o `Vision`. Un modello può svolgere più compiti. Ogni capacità ha il proprio valore predefinito, il proprio interruttore di attivazione e il proprio test. | "Claude Sonnet" svolge `Chat` e `Vision` |
| **Prezzo** | Quanto costa un'unità di quel compito (solo nella pagina della piattaforma). | un costo per milione di token in ingresso |

La chiave e l'indirizzo appartengono al provider, quindi cambiare l'uno o l'altro vale per ogni modello sotto di esso. La pagina te lo dice prima che tu salvi.

## Aggiungere un provider {#adding-a-provider}

Clicca **Nuovo provider**. Il modulo crea il provider e il suo primo modello in un solo passaggio:

- **Nome**: la tua etichetta per l'account, per esempio "Azure di produzione". I nomi devono essere unici.
- **Tipo**: quale servizio o fornitore è (vedi la sezione successiva). Non si può cambiare in seguito.
- **Capacità**: il primo compito che svolge questo modello. Puoi aggiungerne altri dopo.
- **Endpoint** / **URL di base**: l'indirizzo web del servizio. Viene compilato automaticamente per i fornitori con un indirizzo fisso.
- **Modello** (o **Nome del deployment** per Azure OpenAI): quale modello usare.
- **Nome visualizzato**: ciò che le persone vedono quando scelgono questo modello. Se lo lasci vuoto, è il nome del modello.
- **Chiave API**: il segreto che permette a ReelBolt di usare il servizio. Vedi "Chiavi API" più avanti.
- **Timeout (secondi)**: quanto aspettare una risposta prima di rinunciare. Se lo lasci vuoto, il valore predefinito è 300 secondi.
- **Imposta come provider … predefinito**: lo rende il modello usato per la sua capacità, a meno che non ne venga scelto uno più specifico.
- **Attivo**: disattivalo per smettere di usare il provider senza eliminarlo.

Usa **Verifica connessione** prima di salvare per controllare che i dati funzionino, poi **Crea**.

## Aggiungere modelli e capacità {#adding-models-and-capabilities}

Clicca un provider nell'elenco per aprire la sua pagina. La pagina mostra l'account in alto e i suoi modelli sotto, ciascuno con le proprie capacità.

- **Aggiungi modello** aggiunge un altro modello sotto lo stesso account, con la sua prima capacità. Non devi digitare di nuovo la chiave.
- **Aggiungi capacità** su un modello aggiunge un altro compito per quel modello, per esempio `Vision` su un modello che già fa `Chat`.
- Su ogni capacità, **Predefinito** la rende quella predefinita per quel compito, **Attivo** la attiva o disattiva, **Verifica** controlla che funzioni (vedi "Verificare un provider di inferenza ReelBolt" più avanti) e l'icona del cestino la rimuove.
- **Modifica** in alto cambia l'account: nome, indirizzo, chiave, timeout e interruttore di attivazione.

## Prezzi e ricarico {#prices-and-markup}

I prezzi esistono solo nella pagina **Piattaforma**. Il lavoro svolto tramite il provider proprio di uno spazio di lavoro non viene mai addebitato, quindi le sue capacità non hanno prezzi.

Ogni capacità nella pagina della piattaforma elenca i propri prezzi: l'unità, una variante facoltativa, quanto addebita il fornitore (**Costo**), un **Valore in crediti personalizzato** facoltativo, i crediti effettivamente addebitati (**Crediti effettivi**), il **Margine** e quando si applica. Per cambiare un prezzo, clicca **Aggiungi prezzo**:

- **Unità**: ciò che viene conteggiato, per esempio token in ingresso, secondi di video generato o immagini create.
- **Variante**: facoltativa. Separa i prezzi di una stessa unità, come la risoluzione di un video (`1080p`) o un livello di qualità delle immagini.
- **Costo**: quanto addebita il fornitore per un'unità. Il modulo è precompilato con l'elenco dei prezzi pubblicati dei fornitori di ReelBolt quando conosce il modello.
- **Valore in crediti personalizzato**: facoltativo. Lascialo vuoto per addebitare costo × ricarico ÷ valore del credito.
- **Valido dal**: quando inizia il prezzo. Vuoto significa subito.

Un nuovo prezzo parte all'ora scelta e sostituisce il prezzo attuale per la stessa unità e variante, quindi l'utilizzo passato mantiene il prezzo con cui è stato addebitato. **Ritira** termina un prezzo; da quel momento quell'unità è senza prezzo, a meno che un altro prezzo la copra. Quando aggiungi una capacità nella pagina della piattaforma, ReelBolt compila i suoi prezzi a partire dai prezzi pubblicati dei fornitori che conosce.

Il **ricarico** si imposta una volta per tutti i prezzi, in **Piattaforma → Fatturazione** sotto **Prezzi**. Un prezzo senza valore in crediti personalizzato addebita costo × ricarico ÷ valore del credito. Il **Valore del credito** (quanto vale un credito) è mostrato accanto e non può essere modificato lì.

**Prezzi mancanti.** La stessa pagina elenca ogni capacità attiva della piattaforma che non ha un prezzo per qualcosa che usa ("N capacità senza prezzo"). Il lavoro svolto da una capacità di questo tipo viene registrato ma addebitato a 0 crediti, quindi aggiungi un prezzo per ciascuna di quelle elencate.

## Capacità: a cosa serve ogni tipo di connessione a un provider ReelBolt {#capabilities-what-each-kind-of-reelbolt-provider-connection-is-for}

| Capacità (etichetta nel modulo) | A cosa la usa ReelBolt |
|---|---|
| `Chat` (**Chat**) | Il ragionamento dietro ogni agente: script, decisioni di montaggio, piani, revisioni, l'assistente. Necessaria per quasi tutto. |
| `Transcription` (**Trascrizione (ASR)**) | Trasformare il parlato delle tue riprese in una trascrizione, così gli agenti di montaggio sanno cosa viene detto. |
| `Vision` (**Visione (descrizione delle inquadrature)**) | Descrivere cosa c'è sullo schermo in ogni inquadratura, come dettaglio extra facoltativo per gli agenti di montaggio. |
| `Decision` (**Decisione (Jev / logprob)**) | Giudizi rapidi sì/no e a scelta multipla usati dai modelli decisionali facoltativi. Vedi [Modelli decisionali](./decision-models.md). Mai usata per la chat. |
| `VideoGeneration` (**Generazione video**) | Creare nuove clip video (b-roll, inquadrature di apertura, schermate finali). Costa denaro vero per ogni clip ed è limitata dal budget. |
| `SpeechSynthesis` (**Sintesi vocale (TTS / voce narrante)**) | Trasformare il testo dello script in voce narrante. |
| `WebSearch` (**Ricerca web**) | Permettere ad alcuni agenti di cercare fatti aggiornati sul web, con addebito per ogni ricerca. DeepSeek, Google Gemini e OpenAI (solo con l'endpoint `https://api.openai.com/v1`). Vedi [Ricerca web per gli agenti](../workflows/web-search.md). |
| `Embedding` (**Embedding (ricerca semantica nei file)**) | Cercare nei file di progetto per significato, e la ricerca nella documentazione dell'assistente. Vedi [Ricerca semantica](./semantic-search.md). |
| `ImageGeneration`, `ImageEdit`, `ImageErase`, `ImageFill`, `ImageExpand`, `ImageBackgroundReplace`, `ImageRelight`, `ImageUpscale`, `Segmentation`, `Depth` | Gli strumenti AI dell'[editor delle miniature](../editor/thumbnails.md), una capacità per strumento. Vedi [Strumenti dell'editor di immagini](#picture-editor-tools-one-capability-per-tool). |
| `VideoSegmentation` (**Segmentazione video (inseguimento di un oggetto)**) | Inseguire un oggetto nelle riprese con un modello ospitato, così da applicare un effetto solo a quella parte dell'immagine. Solo fal.ai; il modello è `fal-ai/sam-3-1/video-rle`. Vedi [Sfocare o evidenziare un oggetto](../recipes/prompted-region-effects.md). |

## Quali tipi di provider offrono quali capacità in ReelBolt {#which-provider-kinds-serve-which-capabilities-in-reelbolt}

Non tutti i servizi possono svolgere ogni compito. Il modulo offre solo le capacità supportate da un tipo, e ReelBolt rifiuta le combinazioni non supportate quando salvi.

| Tipo (etichetta nel modulo) | Chat | Transcription | Vision | Decision | VideoGeneration | SpeechSynthesis | Embedding |
|---|---|---|---|---|---|---|---|
| `AzureOpenAI` (**Azure OpenAI**) | sì | sì | sì | sì | no | no | sì |
| `OpenAICompatible` (**Compatibile con OpenAI**) | sì | sì | sì | sì | no | sì | sì |
| `Anthropic` (**Anthropic (Claude)**) | sì | no | sì | no | no | no | no |
| `Gemini` (**Google Gemini**) | sì | no | sì | no | no | no | no |
| `DeepSeek` (**DeepSeek**) | sì | no | sì | sì | no | no | no |
| `TypeSafe` (**TypeSafe AI (Jev)**) | no | no | no | sì | no | no | no |
| `OpenJev` (server decisionale self-hosted) | no | no | no | sì | no | no | no |
| `MiniMax` (**MiniMax**) | no | no | no | no | sì | no | no |
| `Higgsfield` (**Higgsfield**) | no | no | no | no | sì | no | no |
| `ComfyUI` (**ComfyUI (self-hosted)**) | no | no | no | no | sì | no | no |
| `FishAudio` (**Fish Audio**) | no | sì | no | no | no | sì | no |

Note:

- **Compatibile con OpenAI** copre OpenAI stessa e qualsiasi servizio che parla la stessa lingua, compresi i modelli che ospiti tu (per esempio sul tuo server GPU) e i due servizi di supporto locali descritti più avanti. Per usare un modello OpenAI, imposta l'**Endpoint** su `https://api.openai.com/v1`; ReelBolt parla allora automaticamente con l'interfaccia più recente di OpenAI, così gli agenti possono usare strumenti insieme a un livello di ragionamento, e omette l'impostazione della temperatura mentre il ragionamento è attivo (OpenAI la rifiuta).
- **Ricerca web OpenAI.** Una connessione di Ricerca web di tipo compatibile con OpenAI funziona solo quando l'endpoint è quello di OpenAI (`https://api.openai.com/v1`); ReelBolt rifiuta qualsiasi altro indirizzo per questa capacità. Usa lo strumento di ricerca integrato di OpenAI e addebita ogni ricerca più l'utilizzo del modello.
- **Anthropic, Google Gemini e DeepSeek non possono trascrivere il parlato.** Usa Azure OpenAI, un servizio compatibile con OpenAI (come il servizio locale di trascrizione incluso) oppure **Fish Audio** per `Transcription`.
- **Fish Audio svolge due compiti e solo due:** `SpeechSynthesis` e `Transcription`. La sua trascrizione è il servizio di trascrizione ospitato di Fish Audio; una connessione Fish Audio usata per `Transcription` richiede che la chiave API sia compilata (una chiave vuota viene rifiutata, perché non esiste un servizio di trascrizione self-hosted senza chiave a cui ripiegare). Tieni presente che la trascrizione di Fish Audio riporta cosa è stato detto e quando è iniziata e finita ogni frase, **ma non dove cade ogni parola**: quindi la sincronizzazione dei sottotitoli da una trascrizione Fish Audio è a livello di frase, non di parola. Usa una connessione compatibile con Whisper se ti servono i tempi parola per parola.
- **Le etichette dei parlanti (chi sta parlando) richiedono un modello che le fornisca.** Per una connessione Fish Audio con `Transcription`, imposta **Model** su `transcribe-1-pro` (stesso prezzo di `transcribe-1`, che non distingue i parlanti): ReelBolt segna allora ogni frase con un parlante anonimo come `S1` o `S2`, e l'agente di montaggio può tenere o tagliare per parlante. Per una connessione compatibile OpenAI verso OpenAI, imposta **Model** su `gpt-4o-transcribe-diarize` (OpenAI ha annunciato il ritiro di questo modello il 2027-02-26, e non fornisce i tempi parola per parola); `gpt-transcribe` non etichetta i parlanti. Le etichette non sono mai nomi e valgono solo all'interno di una registrazione: se una registrazione lunga va trascritta in più parti, ReelBolt la lascia senza etichette invece di indovinare quale parlante sia quale. Con qualsiasi altro modello la trascrizione resta invariata e senza parlanti.
- **La generazione video è solo MiniMax, Higgsfield o ComfyUI**, e questi tre non fanno altro. MiniMax e Higgsfield sono servizi cloud a pagamento. **ComfyUI è un server che gestisci tu** (facoltativamente il servizio incluso `comfyui`, che richiede una GPU ed è disattivato per impostazione predefinita): non costa denaro, quindi un flusso di lavoro lo limita in **minuti di GPU** invece che in dollari; il suo indirizzo può essere privato (per esempio `http://comfyui:8188`); e il suo **Modello** deve essere uno dei due nomi di flusso di lavoro integrati, `wan2.2-ti2v-5b-t2v` o `wan2.2-ti2v-5b-i2v` (Wan 2.2, un modello con licenza Apache-2.0): ReelBolt non gli invia mai altri flussi di lavoro. La sua **Chiave API** è facoltativa e serve solo se un reverse proxy davanti al server ne richiede una. La **voce narrante** usa Fish Audio (il suo servizio ospitato, o un server vocale Fish che gestisci tu) oppure un server vocale compatibile con OpenAI come quello locale incluso (`http://whisper:8000/v1`, costo zero). Fish Audio è anche un servizio di **trascrizione** valido (`Transcription`), non solo un servizio vocale. Solo Fish Audio può **clonare una voce** da registrazioni; una voce clonata è una copia della voce di una persona reale, e ReelBolt registra chi ha attestato il consenso di quella persona, conserva la voce solo per il periodo di conservazione che imposti e ti permette di revocare il consenso o eliminare la voce e le sue registrazioni in qualsiasi momento dalla scheda **Voci** del progetto.
- **`OpenJev`** è per un server decisionale che ospiti tu. Non è nell'elenco **Tipo** del modulo; un amministratore della piattaforma può crearlo chiedendolo all'[assistente](../assistant/index.md).
- Le connessioni Vision devono usare un modello che accetta immagini.

## Strumenti dell'editor di immagini: una capacità per strumento {#picture-editor-tools-one-capability-per-tool}

Ogni strumento AI dell'[editor delle miniature](../editor/thumbnails.md) usa la propria capacità, così puoi dare a ogni strumento un servizio e un modello adatti a quel compito:

| Strumento dell'editor | Capacità (etichetta nel modulo) | Chi può offrirla | Modello fal.ai predefinito |
|---|---|---|---|
| **Crea un'immagine** | `ImageGeneration` (**Generazione di immagini (da testo a immagine)**) | fal.ai, compatibile con OpenAI, Black Forest Labs, MiniMax | `ideogram/v4.5` |
| **Modifica con le parole** | `ImageEdit` (**Modifica di immagini (modifica da una descrizione)**) | fal.ai, compatibile con OpenAI, Black Forest Labs, MiniMax | `google/nano-banana-2.1/edit` |
| **Cancella** | `ImageErase` (**Cancellazione di immagini (rimuovi oggetti)**) | fal.ai, compatibile con OpenAI, Black Forest Labs | `fal-ai/object-removal/mask` |
| **Riempi** | `ImageFill` (**Riempimento di immagini (dipingi un'area)**) | fal.ai, compatibile con OpenAI, Black Forest Labs | `fal-ai/flux-pro/v1/fill` |
| **Espandi** | `ImageExpand` (**Espansione di immagini (outpaint)**) | solo fal.ai | `fal-ai/bria/expand` (accetta una descrizione; l'alternativa, senza descrizione, è `fal-ai/flux-2-pro/outpaint`) |
| **Nuovo sfondo** | `ImageBackgroundReplace` (**Sostituzione dello sfondo**) | solo fal.ai | `fal-ai/bria/background/replace` |
| **Cambia illuminazione** | `ImageRelight` (**Cambio di illuminazione**) | solo fal.ai | `fal-ai/image-apps-v2/relighting` |
| **Aumenta la risoluzione** | `ImageUpscale` (**Upscale di immagini**) | solo fal.ai | `fal-ai/seedvr/upscale/image` |
| **Ritaglia un soggetto**, **Rimuovi lo sfondo** | `Segmentation` (**Segmentazione (ritaglio del soggetto)**) | solo fal.ai | `fal-ai/sam-3-1/image` (la rimozione dello sfondo usa sempre `fal-ai/birefnet/v2`) |

- **Il modello è l'endpoint del compito.** In una connessione fal.ai, l'**ID dell'endpoint** deve essere uno degli endpoint elencati per quello strumento; il modulo li suggerisce, con quello predefinito per primo. Quando salvi, ReelBolt rifiuta una connessione il cui endpoint svolge un compito diverso (per esempio una connessione **Upscale di immagini** impostata su un endpoint di cambio illuminazione), perché altrimenti verrebbe ignorata in silenzio.
- **Configura tutti gli strumenti per le immagini con un clic.** Apri un provider fal.ai che hai già e premi **Configura tutti gli strumenti per le immagini**. ReelBolt aggiunge, sotto quello stesso provider, un modello per ogni strumento per le immagini che non ne ha ancora uno, ciascuno con l'endpoint predefinito di quello strumento. Usano tutti la chiave del provider, quindi non devi digitarla di nuovo. Una nuova connessione diventa quella predefinita per il suo strumento solo dove lo strumento non aveva un valore predefinito, quindi nulla di ciò che hai già scelto viene sostituito. Verifica poi ogni nuova connessione dall'elenco.
- **Uno strumento senza una connessione propria** usa la connessione `ImageEdit` predefinita, come facevano tutti gli strumenti prima di essere separati, **ma solo quando il servizio di quella connessione può svolgere il compito**. Una connessione fal.ai `ImageEdit` può quindi ancora eseguire ogni strumento, ciascuno sul proprio endpoint predefinito; una connessione MiniMax `ImageEdit` non può rimuovere oggetti né aumentare la risoluzione, quindi quegli strumenti dicono di non essere configurati invece di fallire lato servizio.
- Ogni strumento per le immagini è una chiamata a pagamento, addebitata per immagine creata al prezzo dell'endpoint che è stato effettivamente eseguito.

## Connessioni gestite dalla piattaforma e connessioni proprie di uno spazio di lavoro {#platform-managed-connections-and-a-workspaces-own-connections}

Una connessione **gestita dalla piattaforma** appartiene all'installazione di ReelBolt. È disponibile in ogni spazio di lavoro, ed è quella che ogni spazio di lavoro usa a meno che non ne abbia fornita una propria. Solo gli amministratori della piattaforma possono vederla o modificarla. È la pagina che stai leggendo.

Una **connessione dello spazio di lavoro** (detta anche "porta il tuo", o BYO) appartiene a un solo spazio di lavoro del team. È visibile solo all'interno di quello spazio di lavoro, è pagata da quello spazio di lavoro e non influisce sul lavoro di nessun altro. Se uno spazio di lavoro imposta una delle sue connessioni come predefinita per una capacità, quella connessione ha la precedenza per quello spazio di lavoro: il valore predefinito della piattaforma continua ad applicarsi ovunque altrove.

Le due sono completamente separate. Un membro di uno spazio di lavoro non può vedere la pagina della piattaforma, e un amministratore della piattaforma non può vedere né modificare le connessioni di uno spazio di lavoro.

## Le connessioni proprie di uno spazio di lavoro {#a-workspaces-own-connections}

I **Proprietari e gli Amministratori** di uno spazio di lavoro del team gestiscono le sue connessioni in **Spazio di lavoro → Provider**, la pagina intitolata **Provider** nel gruppo **Spazio di lavoro** del menu a sinistra. Il modulo funziona esattamente come quello descritto sopra, con tre differenze volute, tutte imposte da ReelBolt qualunque cosa offra il modulo:

- **Niente scorciatoia `env:`.** Una connessione dello spazio di lavoro deve avere la propria chiave; non può mai puntare a una chiave dell'ambiente dell'installazione.
- **Niente modalità abbonamento personale.** L'interruttore **Dev/OAuth (solo uso personale)**, che il modulo della piattaforma offre per Anthropic, qui non è disponibile.
- **Solo indirizzi pubblici.** Una connessione dello spazio di lavoro deve usare un indirizzo pubblico. I servizi self-hosted nella rete privata dell'installazione possono essere solo connessioni gestite dalla piattaforma.

Vale la pena conoscere altre due regole:

- Aggiungere, modificare o verificare una connessione dello spazio di lavoro richiede che il piano dello spazio di lavoro includa la possibilità di portare i propri provider. In caso contrario, il modulo lo dice e propone il percorso per passare a un piano superiore invece di fallire in modo oscuro. **Vedere l'elenco ed eliminare una connessione funzionano sempre**, così uno spazio di lavoro che ha perso la funzione può comunque fare pulizia da solo. Le connessioni escluse dal piano semplicemente non vengono usate finché il piano non le consente di nuovo.
- **Un Membro non può aprire la pagina**, ma può comunque scegliere tra le connessioni offerte dalle impostazioni di un passaggio, esattamente come nella pagina della piattaforma.

La pagina dello spazio di lavoro è descritta dal punto di vista dello spazio di lavoro in [Spazi di lavoro e team](../workspaces.md) e [Porta il tuo provider](../account/bring-your-own-provider.md).

## Provider predefiniti: uno per capacità {#default-providers-one-per-capability}

Ogni capacità ha il proprio valore predefinito indipendente. Puoi avere contemporaneamente un provider `Chat` predefinito, un provider `Transcription` predefinito e un provider `Embedding` predefinito; rendere predefinita una connessione sostituisce solo il precedente valore predefinito **per la stessa capacità**. Anche uno spazio di lavoro può avere un proprio valore predefinito per una capacità e, dove lo ha, **il valore predefinito dello spazio di lavoro ha la precedenza all'interno di quello spazio di lavoro**, mentre il valore predefinito della piattaforma continua a servire tutti gli altri.

Quando ReelBolt ha bisogno di una capacità, usa nell'ordine:

1. una connessione scelta specificamente per quel compito (per esempio la scelta specifica di un agente, o un provider scelto nelle impostazioni di un passaggio),
2. altrimenti la connessione predefinita per quella capacità,
3. altrimenti, solo per `Chat`, i dati di Azure OpenAI nel file di impostazioni dell'installazione, se ne sono stati impostati. Per `Embedding` lo stesso file di impostazioni viene usato come ultima risorsa.

Per ogni altra capacità non c'è alternativa: se non esiste un valore predefinito, le funzioni che ne hanno bisogno o saltano quella parte (per esempio, una modifica senza trascrizione) oppure si fermano con un errore quando le impostazioni del passaggio dicono che è obbligatoria.

Non puoi eliminare una capacità che è attualmente quella predefinita per il suo compito, né un modello o un provider che ne contiene una; rendi prima predefinito un altro modello. Le modifiche ai provider hanno effetto entro circa un minuto, senza riavviare nulla.

**Anche la voce narrante guarda i risultati dei test.** Solo per `SpeechSynthesis`, se la connessione predefinita **ha fallito l'ultimo test** e un'altra connessione vocale **lo ha superato**, ReelBolt narra con quella funzionante, e l'esecuzione mostra un avviso ("Il tuo servizio vocale predefinito ha fallito l'ultima verifica della connessione…"). Una connessione scelta nelle impostazioni di un passaggio viene sempre usata come scelta, e un valore predefinito mai verificato è considerato affidabile. Se l'esecuzione ha usato una connessione il cui ultimo test è fallito, avvisa che la voce narrante potrebbe mancare. Rifai il test del valore predefinito dopo averlo sistemato e verrà usato di nuovo entro circa un minuto. Qualsiasi utente che ha effettuato l'accesso può vedere quale servizio vocale userebbe un'esecuzione e se funziona correttamente (non vengono mostrati né indirizzi né chiavi).

## Verificare un provider di inferenza ReelBolt {#testing-a-reelbolt-inference-provider}

**Verifica** su una capacità (o **Verifica connessione** nel modulo) invia una piccolissima richiesta attraverso di essa e riporta **Connessione OK** oppure **Connessione non riuscita** con il motivo. Il risultato di una capacità viene salvato e mostrato sulla sua riga nella pagina del provider (passa il mouse per vedere l'ora o l'errore). Cosa fa il test dipende dalla capacità:

- `Chat`: chiede una risposta di una sola parola.
- `Transcription`: invia una frazione di secondo di audio silenzioso.
- `Vision`: invia una piccolissima immagine e chiede "ok".
- `Decision`: pone una domanda fissa ("2 è maggiore di 1?") e controlla che il servizio sia chiaramente sicuro che la risposta sia sì.
- `Embedding`: trasforma la parola "ping" in numeri e ne riporta la lunghezza (per esempio `vector length 1024`).
- `SpeechSynthesis`: sintetizza la parola "ok" e controlla che sia tornato dell'audio vero.
- `VideoGeneration`: controlla che la chiave e il servizio rispondano **senza** creare un video, quindi il test non costa nulla.
- Le capacità per le immagini (`ImageGeneration`, `ImageEdit`, `ImageErase`, `ImageFill`, `ImageExpand`, `ImageBackgroundReplace`, `ImageRelight`, `ImageUpscale`) e `Segmentation`/`Depth`: inviano al servizio una richiesta vuota che rifiuta senza creare un'immagine, quindi il test non costa nulla. Su fal.ai il test controlla anche che l'endpoint della connessione sia uno di quelli per quello strumento.

Quando modifichi una connessione salvata puoi verificarla senza digitare di nuovo la chiave; viene riutilizzata quella memorizzata.

Quando un test fallisce, il motivo include cosa è andato davvero storto, per esempio una connessione rifiutata con l'indirizzo che ha provato, oppure il messaggio di errore del servizio con il suo numero di richiesta, così puoi distinguere un indirizzo sbagliato da un nome di modello sbagliato da un'interruzione lato servizio. Per MiniMax, un errore che torna come errore del server (500) mentre la chiave è accettata significa che il problema è lato MiniMax; il messaggio lo dice e include l'ID della richiesta MiniMax per il loro supporto.

## Chiedere all'assistente di verificare un provider di inferenza ReelBolt {#asking-the-assistant-to-test-a-reelbolt-inference-provider}

Gli amministratori della piattaforma possono anche chiedere all'assistente di eseguire il test, per esempio "verifica la connessione MiniMax e dimmi perché non funziona". L'assistente esegue esattamente lo stesso test del pulsante **Verifica connessione**, salva il risultato nello stesso posto, legge l'errore e lo spiega in parole semplici. Invia una sola piccolissima richiesta reale, proprio come il pulsante, e non modifica alcuna impostazione: se un'impostazione va cambiata, propone la modifica e aspetta la tua approvazione.

## Scegliere un provider diverso per un agente {#choosing-a-different-provider-for-one-agent}

Per impostazione predefinita ogni agente usa il provider `Chat` predefinito dello spazio di lavoro in cui è in esecuzione. Un amministratore della piattaforma può indirizzare un singolo agente a una connessione di chat diversa ovunque, e il Proprietario o l'Amministratore di uno spazio di lavoro può fare lo stesso **solo per quello spazio di lavoro**, per esempio un modello più potente per il regista o uno più economico per l'analisi del codice.

Nella pagina della piattaforma:

1. Apri **Agenti** e clicca l'agente (anche gli agenti integrati possono essere sostituiti).
2. In **Provider di inferenza** ("Sostituisce il provider predefinito globale per questo agente."), scegli una connessione, oppure **Provider predefinito** per tornare al valore predefinito.

Il Proprietario o l'Amministratore di uno spazio di lavoro vede lo stesso controllo nella pagina dell'agente mentre quello spazio di lavoro è attivo, ma solo per un agente **integrato**: un agente integrato è una riga condivisa, quindi la loro scelta viene memorizzata sullo spazio di lavoro invece che sull'agente. Possono scegliere qualsiasi connessione `Chat` attiva che lo spazio di lavoro può usare, gestita dalla piattaforma o propria dello spazio di lavoro, e vale solo per quello spazio di lavoro, con precedenza sull'impostazione valida per tutta l'installazione. Tutti gli altri, e ogni agente personalizzato, vedono la scelta attuale in sola lettura.

Sono elencate solo le connessioni `Chat` attive. Se in seguito elimini la connessione scelta, l'agente torna in silenzio al valore predefinito. I Membri possono vedere quale provider usa un agente ma non possono cambiarlo.

## Chiavi API e la scorciatoia env: {#api-keys-and-the-env-shortcut}

Le chiavi API sono memorizzate cifrate e non vengono più mostrate dopo che le salvi: la pagina mostra solo gli ultimi quattro caratteri (per esempio `•••• 1a2b`), e l'assistente non le rilegge mai.

- Quando modifichi, **lascia vuota la Chiave API per mantenere quella attuale**. Per rimuovere una chiave memorizzata, attiva **Rimuovi la chiave memorizzata**.
- Per **Anthropic**, **Google Gemini** e **DeepSeek** puoi digitare il valore letterale `env:` al posto di una chiave. ReelBolt allora non memorizza alcuna chiave e usa quella delle variabili d'ambiente del server: `ANTHROPIC_API_KEY` o `ANTHROPIC_AUTH_TOKEN` per Anthropic, `GEMINI_API_KEY` o `GOOGLE_API_KEY` per Gemini, `DEEPSEEK_API_KEY` per DeepSeek. Le imposta chi gestisce l'installazione.
- Per questi tre tipi, una **chiave vuota è un errore**, non un ripiego silenzioso: una connessione senza chiave e senza `env:` fallisce quando viene usata.
- Le chiavi di MiniMax e Fish Audio vengono usate esattamente come le inserisci; `env:` lì non ha alcun significato speciale.

**Abbonamenti personali Anthropic.** Il modulo Anthropic ha un interruttore **Dev/OAuth (solo uso personale)** per usare la credenziale di un abbonamento personale a Claude invece di una chiave API della Console. Un abbonamento personale è concesso in licenza solo per l'uso del suo titolare; un'installazione che serve altre persone deve usare una vera chiave API.

**Licenza di Fish Audio.** Il servizio ospitato di Fish Audio, compreso il livello gratuito, non ha una restrizione documentata all'uso non commerciale. Eseguire i modelli aperti di Fish sul tuo server è diverso: l'uso commerciale richiede una licenza a pagamento separata da Fish Audio.

## Configurare la trascrizione locale (il servizio whisper) {#setting-up-local-speech-to-text-the-whisper-service}

L'installazione standard di ReelBolt include un servizio locale facoltativo di trascrizione chiamato `whisper`, che trascrive le riprese sul tuo server invece di inviare l'audio a un fornitore cloud. Non è collegato automaticamente. Per usarlo:

1. Assicurati che il servizio `whisper` sia in esecuzione (chi gestisce l'installazione può avviarlo).
2. Clicca **Nuovo provider** e imposta **Tipo** su **Compatibile con OpenAI**, **Capacità** su **Trascrizione (ASR)**.
3. Imposta **URL di base** su `http://whisper:8000/v1` (l'indirizzo del servizio all'interno dell'installazione).
4. Imposta **Modello** su uno dei suggerimenti, per esempio `Systran/faster-whisper-small` (veloce) o `Systran/faster-whisper-large-v3` (più accurato, più lento). Il modello viene scaricato la prima volta che viene usato, quindi la prima trascrizione è lenta.
5. Lascia vuota la **Chiave API**, attiva **Imposta come provider di trascrizione predefinito**, clicca **Verifica connessione**, poi **Crea**.

Funziona sul processore, non su una scheda grafica, quindi trascrivere riprese lunghe richiede un po' di tempo.

## Configurare la ricerca locale (il servizio embeddings) {#setting-up-local-search-the-embeddings-service}

L'installazione standard include anche un servizio locale facoltativo chiamato `embeddings`, che alimenta la ricerca per significato senza un fornitore cloud. Per collegarlo:

1. Assicurati che il servizio `embeddings` sia in esecuzione. Il primo avvio scarica il suo modello e può richiedere alcuni minuti.
2. Clicca **Nuovo provider**: **Tipo** **Compatibile con OpenAI**, **Capacità** **Embedding (ricerca semantica nei file)**.
3. **URL di base** `http://embeddings:80/v1`, **Modello** `Qwen/Qwen3-Embedding-0.6B`, **Chiave API** vuota.
4. Attiva **Imposta come provider di embedding predefinito**, clicca **Verifica connessione** (dovrebbe riportare `vector length 1024`), poi **Crea**.
5. Reindicizza i file di progetto esistenti perché diventino ricercabili; vedi [Ricerca semantica](./semantic-search.md).

## Gestire i provider di inferenza tramite l'assistente {#managing-inference-providers-through-the-assistant}

Come gli amministratori della piattaforma possono chiedere all'[assistente](../assistant/index.md) di elencare le connessioni, aggiungerne una, modificarne una o rimuoverne una. Ogni modifica viene mostrata prima su una scheda e viene fatta solo dopo che premi **Consenti** (a meno che tu non abbia disattivato **Chiedi prima di modificare** dell'assistente). L'assistente non mostra mai le chiavi API memorizzate, e valgono le stesse regole di questa pagina (per esempio, non può eliminare una connessione predefinita).

Gli strumenti dell'assistente per i provider agiscono solo sulle connessioni **gestite dalla piattaforma**. Non può elencare, leggere, verificare né modificare le connessioni proprie di uno spazio di lavoro, quindi al Proprietario o all'Amministratore di uno spazio di lavoro che gli chiede di modificarne una verrà detto che la connessione non esiste: usa **Spazio di lavoro → Provider**.
