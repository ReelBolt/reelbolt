---
title: Attività per tag
sidebar_position: 5
description: Vedi quanti progetti, esecuzioni e crediti ha usato ogni tag in 7, 30 o 90 giorni e come leggere i totali.
i18n_source_sha: 7105a0c3f5c96e72455ea58b9e7aa31737ecb8fa
---
# Attività per tag {#activity-by-tag}

Se etichetti i progetti con i nomi dei clienti (vedi [Organizzare i progetti](./organizing-projects.md)), **Attività per tag** mostra cosa ha usato ogni cliente. Aprila dal link nella pagina dei progetti, oppure vai a `/app/tags`.

## Leggi la tabella {#read-the-table}

Scegli un periodo: **7 giorni**, **30 giorni** o **90 giorni**. Ogni tag ha una riga con:

| Colonna | Significato |
|---|---|
| **Progetti** | I progetti che hanno il tag |
| **Esecuzioni** | Le esecuzioni di flussi di lavoro avviate nel periodo in quei progetti |
| **Riuscite** / **Fallite** | Come sono finite quelle esecuzioni |
| **Crediti** | I crediti usati da quei progetti nel periodo |

Un'esecuzione rientra nel periodo in base a quando è partita. Si contano le esecuzioni di ogni tipo di flusso di lavoro.

Sotto i tag trovi la riga **Totale dello spazio di lavoro** e la riga **Senza tag** per i progetti che non ne hanno.

## Perché i tag possono sommare più del totale {#why-the-tags-can-add-up-to-more-than-the-total}

Un progetto con più tag viene contato sotto ciascuno di essi. Se un progetto ha i tag `acme` e `spring`, le sue esecuzioni compaiono in entrambe le righe, quindi le righe dei tag possono sommare più del **Totale dello spazio di lavoro**. Per il dato reale usa il totale dello spazio di lavoro.

## Un tag in dettaglio {#one-tag-in-detail}

Clicca un tag per vedere i suoi progetti con esecuzioni e crediti, e le sue 50 esecuzioni più recenti. Clicca **Apri esecuzione** per andare a un'esecuzione.

## Quando mancano i crediti {#when-credits-are-missing}

Se la tua installazione ha la fatturazione disattivata, la colonna **Crediti** è nascosta e vedi comunque progetti ed esecuzioni.

## Chi la vede {#who-can-see-it}

Chiunque nello spazio di lavoro possa vedere l'utilizzo può aprire questa pagina. Mostra crediti, mai importi in denaro.
