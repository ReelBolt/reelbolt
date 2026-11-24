---
title: Scegliere i modelli per i tuoi agenti
sidebar_position: 7
description: Come Proprietari e Amministratori scelgono quale modello AI usa ogni agente integrato in uno spazio di lavoro, cosa significano il modello consigliato e il costo relativo, e come annullare una scelta.
i18n_source_sha: d52bfb390e7b7c465a8cd0a441fa4b95ef6a793f
---

# Scegliere i modelli per i tuoi agenti {#choosing-models-for-your-agents}

Ogni [agente](./agents.md) integrato usa un modello AI. ReelBolt sceglie un **modello consigliato** per ogni agente, e un Proprietario o un Amministratore di uno spazio di lavoro può sceglierne un altro per quello spazio. La scelta vale per ogni flusso di lavoro dello spazio che usa quell'agente.

## Chi può cambiarlo {#who-can-change-it}

- **Proprietari e Amministratori** scelgono un modello per ogni agente dello spazio di lavoro.
- **Membri** non possono cambiare il modello. Chiedi a un Proprietario o a un Amministratore dello spazio di lavoro; anche l'assistente può dirti quale modello usa un agente.

## Scegliere un modello per un agente {#pick-a-model-for-an-agent}

1. Apri la pagina **Agenti** e seleziona l'agente.
2. Apri il selettore del modello. Il modello contrassegnato come **Consigliato** è quello che ReelBolt suggerisce per questo agente, con un breve motivo. Per esempio, per un agente il consiglio può essere DeepSeek V4.1 Flash o GPT-6 Luna.
3. Scegli un modello dall'elenco.

La modifica vale dal passaggio successivo dell'agente. Un passaggio già in corso termina con il modello con cui è partito.

## Leggere il costo relativo {#read-the-relative-cost}

Ogni opzione mostra come si confronta con il modello consigliato, per esempio "circa 4 volte più caro" o "circa 3 volte più economico". Sono confronti per una quantità di lavoro tipica, non prezzi. Per vedere quanto costa un'esecuzione, usa la stima prima di avviarla (vedi sotto).

## I tuoi modelli collegati {#your-own-connected-models}

Se il tuo spazio di lavoro ha collegato i propri modelli (vedi [Usa il tuo provider](../account/bring-your-own-provider.md)), compaiono nello stesso elenco. Non mostrano un costo relativo, perché li paghi direttamente al tuo provider.

## Ripristinare il consigliato {#reset-to-recommended}

Scegli **Ripristina il consigliato** nel selettore per rimuovere la scelta dello spazio di lavoro. L'agente torna al modello consigliato e segue ogni futura modifica del consiglio.

## Le stime seguono la tua scelta {#estimates-follow-your-choice}

La stima del costo che vedi prima di un'esecuzione usa il modello scelto per ogni agente del flusso di lavoro. Se cambi il modello di un agente, la stima cambia di conseguenza.

## Chiedi all'assistente {#ask-the-assistant}

L'[assistente](../assistant/index.md) può dirti quale modello usa ogni agente. Per ora il modello si cambia dalla pagina dell'agente.
