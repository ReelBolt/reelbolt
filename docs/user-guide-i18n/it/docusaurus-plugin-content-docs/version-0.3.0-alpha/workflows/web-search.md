---
title: Ricerca web per gli agenti
sidebar_position: 10
description: Lascia che alcuni agenti integrati cerchino informazioni aggiornate sul web. Cosa fa, come la attiva un amministratore, quanto costa e come trattare i risultati.
i18n_source_sha: e0c58ea4dac0432629f613a65f7e9bc9408ad669
---

# Ricerca web per gli agenti {#web-search-for-agents}

Alcuni agenti integrati possono cercare sul web mentre lavorano, per un fatto recente, una statistica, o un nome
o una data da verificare. L'agente scrive una breve query di ricerca, riceve alcuni risultati (titolo, link e
un breve estratto) e li usa come riferimento.

## Attivarla {#turning-it-on}

La ricerca web è disattivata finché un amministratore non aggiunge un provider per questa funzione.

1. Apri **Admin**, poi **Provider di inferenza**, e aggiungi un provider con la capacità **Ricerca web**.
2. Scegli DeepSeek, Google Gemini o OpenAI. Per OpenAI, l'endpoint deve essere `https://api.openai.com/v1`.
3. Impostalo come predefinito per la ricerca web.

I passaggi sono in [Provider di inferenza](../admin/inference-providers.md). Se non è impostato nessun provider, gli agenti
funzionano comunque: la ricerca segnala semplicemente di non essere disponibile e l'agente va avanti senza.

## Quali agenti la usano {#which-agents-use-it}

Lo Sceneggiatore, il Regista, il Regista delle inquadrature e l'Autore della narrazione. Gli agenti personalizzati non hanno
la ricerca web. Vedi [Agenti](./agents.md).

## Quanto costa {#what-it-costs}

Ogni query di ricerca viene fatturata a parte, al prezzo del provider più un ricarico, negli stessi crediti del
resto dell'esecuzione. Per OpenAI e Google viene fatturato anche l'uso del modello per la ricerca, quando il modello ha un prezzo. Un agente
può fare un numero limitato di ricerche per passaggio e per esecuzione, quindi il costo ha un tetto. Un passaggio che ha cercato
non viene mai salvato nella cache, perché il web cambia; un passaggio che avrebbe potuto cercare ma non l'ha fatto può essere messo in cache. Se usi la connessione al tuo provider,
ReelBolt non addebita le ricerche.

## Google Gemini non è ancora disponibile per l'uso condiviso {#google-gemini-is-not-available-for-shared-use-yet}

I termini di Google limitano il modo in cui i risultati di ricerca possono essere salvati e mostrati. Finché ReelBolt non potrà mostrare i
suggerimenti di ricerca di Google richiesti, i risultati di una connessione Google Gemini vengono rifiutati e l'agente va avanti
senza. Per ora usa DeepSeek o OpenAI per la ricerca web.

## Come trattare i risultati {#how-to-treat-the-results}

I risultati di ricerca arrivano da pagine web di terze parti e possono essere sbagliati o non aggiornati. ReelBolt li contrassegna
come non attendibili, quindi un agente non segue le istruzioni trovate in una pagina. I link vengono mostrati così puoi verificare una
fonte da solo; gli agenti non li aprono. La query di ricerca esce da ReelBolt e viene inviata al provider che hai
scelto, e può contenere parole del tuo progetto. Vedi [I tuoi dati](../your-data.md).
