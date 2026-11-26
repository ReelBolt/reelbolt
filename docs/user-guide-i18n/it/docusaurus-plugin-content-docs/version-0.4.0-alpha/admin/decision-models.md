---
title: Modelli decisionali
sidebar_position: 4
description: Una guida in linguaggio semplice ai modelli decisionali facoltativi di ReelBolt - le modalità Off, Shadow e Gate, quanto costano e che rischi comportano, e come leggere la pagina Calibrazione delle decisioni.
i18n_source_sha: 13a94dfd00917602dccd180140b41cac86130c79
---

# Modelli decisionali {#decision-models}

Un **modello decisionale** è un piccolo servizio AI, veloce, che risponde a semplici domande a scelta multipla, come "quale look colore si adatta a queste riprese: caldo, freddo o cinematografico?". Alcune decisioni dei flussi di lavoro sono abbastanza semplici da poter essere affidate a un modello di questo tipo al posto di un agente completo, con più rapidità e meno costi. I modelli decisionali di ReelBolt ti permettono di **mettere prima alla prova questa idea in sicurezza** e solo dopo, se i numeri la sostengono, di lasciare che il modello veloce si occupi di alcune di quelle decisioni.

Questa funzione è facoltativa, avanzata e **disattivata per impostazione predefinita ovunque**. La maggior parte delle installazioni non ne ha mai bisogno.

## Le tre modalità dei modelli decisionali: Off, Shadow e Gate {#the-three-decision-model-modes-off-shadow-and-gate}

| Modalità | Cosa succede | Cambia i tuoi video? |
|---|---|---|
| `Off` | Niente. Non viene posta alcuna domanda a un modello decisionale. È l'impostazione predefinita. | No |
| `Shadow` | L'agente prende la decisione come al solito. Il modello decisionale riceve le stesse domande in parallelo, e ReelBolt registra se era d'accordo e con quanta sicurezza. | No, è solo un'osservazione |
| `Gate` | Il modello decisionale viene interrogato per primo. Se è abbastanza sicuro su **ogni** domanda di quel passaggio, si usa la sua risposta e l'agente viene saltato. Se è incerto anche solo su una domanda, l'intero passaggio va all'agente come di consueto. | Sì, quando il modello è sicuro |

Il percorso abituale è: lavora in `Shadow` per un po', guarda i risultati nella pagina di calibrazione e passa a `Gate` solo per le decisioni in cui il modello si è dimostrato affidabile.

Un modello decisionale non può mai far fallire un passaggio. Se non è configurato, è lento (per impostazione predefinita ha qualche secondo) o risponde cose senza senso, ReelBolt torna semplicemente all'agente.

## Dove ReelBolt può usare un modello decisionale {#where-reelbolt-can-use-a-decision-model}

I modelli decisionali si usano solo in pochi punti specifici:

- l'agente **Colorist** (scelta del look di color grading),
- l'agente **MusicSupervisor** (scelta di intensità, ducking e adattamento della musica),
- la **stanza del colore**, per decidere se la stanza debba riunirsi o meno,
- un passaggio **Conditional** impostato per decidere il proprio ramo con un modello decisionale,
- l'interruzione anticipata facoltativa di un passaggio **ReviewLoop**.

Tutto il resto in ReelBolt non è interessato.

## Attivare i modelli decisionali in ReelBolt {#turning-decision-models-on-in-reelbolt}

Servono due cose:

1. **Un provider `Decision`.** Un amministratore aggiunge un provider di inferenza con la capacità **Decision (Jev / logprob)** e lo imposta come predefinito. I tipi adatti sono TypeSafe, un server decisionale self-hosted (`OpenJev`), Azure OpenAI, i servizi compatibili con OpenAI e DeepSeek. Anthropic e Google Gemini non si possono usare. Vedi [Provider di inferenza](./inference-providers.md).
2. **Una modalità.** Per gli agenti Colorist e MusicSupervisor, la modalità la imposta chi gestisce l'installazione, nelle sue impostazioni (`COLORIST_DECISION_GATE_MODE` e `MUSIC_SUPERVISOR_DECISION_GATE_MODE`, ciascuna `Off`, `Shadow` o `Gate`). Gli altri tre usi si attivano nelle impostazioni del singolo passaggio del flusso di lavoro. Né la dashboard né l'assistente possono cambiare queste modalità.

**Nota sulla privacy.** Quando un modello decisionale è un servizio cloud, le domande e un po' di contesto sul passaggio vengono inviati a quel servizio, in aggiunta al servizio AI principale che gli agenti usano già. In modalità `Shadow` quel contesto può includere il testo della trascrizione e il testo a schermo delle tue riprese. Un server decisionale self-hosted tiene tutto questo nella tua rete. Tienine conto prima di attivarlo.

## Leggere la pagina Calibrazione delle decisioni in ReelBolt {#reading-the-decision-calibration-page-in-reelbolt}

Gli amministratori possono aprire **Admin → Calibrazione** (`/app/admin/calibration`), la pagina **Calibrazione delle decisioni**. È di sola lettura: mostra cosa hanno fatto finora i modelli decisionali e cosa accadrebbe con impostazioni diverse. Non modifica nulla. Apri **Come funziona questa pagina** in alto per una spiegazione integrata.

I concetti principali:

- **Osservazioni**: quante decisioni sono state registrate.
- **Concordanza**: quanto spesso il modello decisionale ha dato la stessa risposta dell'agente.
- **Confidenza**: quanto il modello ha dichiarato di essere sicuro. Un modello affidabile concorda più spesso quando dichiara una confidenza maggiore; il grafico di affidabilità mostra se questo si verifica.
- **Accetta da** e **Margine minimo** (i due cursori): cosa significa "abbastanza sicuro". Una decisione viene accettata solo se la risposta principale del modello è sicura almeno quanto Accetta da e supera la seconda di almeno il margine minimo. I valori predefiniti sono 0,85 e 0,25. Sposta i cursori per vedere quante decisioni il modello avrebbe gestito da solo con altre impostazioni.
- **Gestirebbe da solo** / **Passerebbe all'agente**: la quota di passaggi che il modello si sarebbe assunto, o avrebbe rimandato all'agente, con le impostazioni dei cursori.
- **Token risparmiati**: una **stima** dell'uso di AI che si sarebbe risparmiato con le impostazioni dei cursori. È una proiezione, mai una misurazione.

Ogni agente ha una scheda con un badge di consiglio: **Dati insufficienti** (meno di 30 osservazioni), **Resta in Shadow** oppure **Puoi provare Gate a X**. Sono suggerimenti basati su regole semplici, non garanzie, e nessuna delle soglie predefinite è stata tarata sulle tue riprese. Usa **Chiedi all'assistente** per una raccomandazione in linguaggio semplice; l'assistente può leggere questo rapporto ma non può cambiare alcuna impostazione.

Se la pagina dice **Nessuna osservazione di decisione registrata**, nessun flusso di lavoro è ancora stato eseguito con `Shadow` o `Gate` attivi.
