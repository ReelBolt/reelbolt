---
title: Quanto costa l'assistente
sidebar_position: 9
description: Cosa fanno gratis l'assistente di ReelBolt e Claude Code, cosa consuma crediti, come funziona il costo in sola lettura su una scheda di approvazione, come viene condivisa l'allocazione mensile dell'assistente e cosa succede quando si esaurisce.
i18n_source_sha: a1574235059c5b8d8bf425bc6cb9246f624ec38d
---

# Quanto costa l'assistente {#what-the-assistant-costs}

Questa pagina spiega cosa fanno gratis l'assistente di ReelBolt e Claude Code e cosa consuma crediti, come si legge il costo su una scheda di approvazione, come funziona l'allocazione mensile dell'assistente e cosa succede quando si esaurisce. Vale per l'assistente in chat presente in ogni pagina e per Claude Code quando è collegato al tuo account ReelBolt. Per le regole generali sui crediti, vedi [Piani e crediti](./plans-and-credits.md).

## Cosa è gratis {#what-is-free}

- **Cercare informazioni.** Fare domande sui tuoi progetti, file, flussi di lavoro, cronologia delle esecuzioni o sui livelli di un'immagine è gratis.
- **Chat e uso degli strumenti entro la tua allocazione.** I messaggi all'assistente e le chiamate agli strumenti di Claude Code sono gratis finché non esaurisci l'allocazione mensile dell'assistente del tuo piano. L'importo del tuo piano è mostrato in **Impostazioni → Piano e fatturazione**.
- **Un suggerimento per la timeline fatto con il tuo provider AI** non costa crediti.
- **Il lavoro svolto con un provider che il tuo spazio di lavoro ha collegato da sé** non costa crediti, qualunque sia la funzione.

## Cosa consuma crediti {#what-uses-credits}

- **Avviare un'esecuzione.** Un'esecuzione spende crediti man mano che i suoi passaggi usano servizi a pagamento. Il costo è mostrato sulla scheda di approvazione prima che l'esecuzione parta (vedi [Leggere la stima](./reading-the-estimate.md)).
- **Strumenti a pagamento per le immagini.** Cancellare, riempire, espandere, riilluminare, aumentare la risoluzione e generare un'immagine spendono crediti ogni volta che ne usi uno. L'assistente ti dice prima il costo stimato.
- **Grafiche progettate.** Ogni progettazione consuma crediti, e il costo è mostrato prima di iniziare.
- **Clip video generate.** Sono la voce più costosa. Prima di avviare un'esecuzione che genererebbe clip, l'assistente chiede se riutilizzare le clip di esecuzioni precedenti (gratis) o generarne di nuove (a pagamento), e ti dice il massimo che potrebbero costare.

## Quando l'assistente riprende dopo un'esecuzione {#when-the-assistant-picks-up-after-a-run}

Se chiedi all'assistente di attendere un'esecuzione, il messaggio di seguito che scrive quando l'esecuzione finisce è un normale messaggio dell'assistente. Segue le stesse regole di qualsiasi altro messaggio: usa prima la tua allocazione mensile, poi il saldo dei crediti.

Ogni richiesta, compreso un seguito, ha un limite di 100 crediti. L'importo messo da parte mentre lavora resta fino a 5 crediti. In modalità automatica un seguito può avviare un lavoro che consuma crediti senza chiedere, sempre entro quel limite e il tuo saldo.

## Leggere il costo su una scheda di approvazione {#read-the-cost-on-an-approval-card}

Di default l'assistente chiede prima di cambiare qualcosa. Ogni scheda mostra cosa succederà e, quando l'azione consuma crediti, una riga di costo come **Di solito 8–12 crediti (ne servono 13 disponibili per partire; può costare di più).**

- Le prime cifre sono quanto costa di solito l'azione.
- La cifra **ne servono** è quanto deve avere disponibile il tuo spazio di lavoro prima che l'azione possa partire.
- Se non è stato possibile calcolare il costo in anticipo, la scheda lo dice, e l'azione consuma comunque crediti.

Per un'azione che consuma crediti, il pulsante dice **Consenti e usa i crediti**. Seleziona **Non consentire** per rifiutare la scheda e dire all'assistente cosa vuoi invece.

In modalità automatica, le esecuzioni che consumano crediti partono senza scheda. Il saldo dei crediti e il limite orario di esecuzioni continuano ad applicarsi.

## Cosa copre l'allocazione mensile {#what-the-monthly-allowance-covers}

Ogni piano include un'allocazione mensile per l'assistente. L'importo per il tuo piano è mostrato in **Impostazioni → Piano e fatturazione**.

- I messaggi in chat e le chiamate agli strumenti di Claude Code condividono un'unica allocazione.
- L'allocazione riparte a ogni periodo di fatturazione. Nel piano Trial riparte a ogni mese di calendario.
- In un piano con prezzo per postazione, l'allocazione è conteggiata per postazione e condivisa in tutto lo spazio di lavoro.
- Per vedere quanto hai usato, apri **Impostazioni → Utilizzo**. La sezione **Quota dell'assistente** mostra l'importo usato, e **Per origine** mostra in cosa sono stati spesi i tuoi crediti.

## Cosa succede quando l'allocazione si esaurisce {#what-happens-when-the-allowance-runs-out}

Quando l'allocazione è esaurita, l'assistente e Claude Code continuano a lavorare con il saldo dei crediti. Ogni messaggio e ogni chiamata a uno strumento di Claude Code mette da parte fino a 5 crediti mentre è in corso. Usa prima l'allocazione, poi il saldo, e viene addebitato solo ciò che ha effettivamente usato.

L'assistente si ferma solo quando l'allocazione è esaurita e nel saldo non restano crediti. In quel caso lo dice in chat.

Uno strumento a pagamento per le immagini o un suggerimento per la timeline controlla il saldo prima di partire. Se i crediti non bastano, ti dice quanti ne servono.

## Claude Code e l'allocazione {#claude-code-and-the-allowance}

Claude Code attinge alla stessa allocazione dell'assistente in chat e segue le stesse regole su cosa è gratis e cosa consuma crediti. Vedi [Quanto costa](../assistant/claude-code.md#what-it-costs) nella guida a Claude Code.

## Pagine correlate {#related-pages}

- [Piani e crediti](./plans-and-credits.md): la panoramica di piani, crediti e pagina Utilizzo.
- [Leggere la stima](./reading-the-estimate.md): la riga del costo prima di un'esecuzione.
- [L'assistente](../assistant/index.md): cosa sa fare l'assistente e come funzionano le approvazioni.
