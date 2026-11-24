---
title: Stanze
sidebar_position: 7
description: Cosa fanno le quattro stanze nei flussi di lavoro ReelBolt (montaggio, grafica, color grading e giornalieri), quando usarne una al posto di un singolo passaggio, quali piani le includono, quanto costano rispetto a un singolo passaggio e quali template le usano.
i18n_source_sha: d939d7f068640fd0e7482df47898fa17258fae78
---

# Stanze {#rooms}

Questa pagina descrive le quattro **stanze** che ReelBolt può usare in un flusso di lavoro: la stanza di montaggio, la stanza grafica, la stanza del colore e la stanza dailies. Una stanza sostituisce un passaggio AI con un piccolo gruppo di specialisti AI che discutono il lavoro e arrivano insieme a una sola decisione. Qui trovi cosa fa ogni stanza, quando usarla, quali piani la includono, perché costa più di un singolo passaggio e quali template integrati la usano già.

## Cos'è una stanza {#what-a-room-is}

Un singolo passaggio chiede a un solo agente AI di prendere una decisione, per esempio quali parti di un video tenere. Una stanza chiede invece a un gruppo. I membri del gruppo discutono a turno il materiale, poi un regista scrive una decisione finale nella stessa forma che avrebbe dato un singolo agente. Il resto del flusso di lavoro tratta quella decisione come prima.

Se un gruppo non riesce a portare a termine il lavoro, per impostazione predefinita la stanza ripiega su un solo agente per quel passaggio, così l'esecuzione si completa comunque.

Le stanze non sono su tutti i piani. La tabella dei piani in [Piani e crediti](../account/plans-and-credits.md#the-plans-on-sale-in-reelbolt) mostra quali piani le includono.

## Stanza di montaggio {#edit-room}

La stanza di montaggio è un gruppo di montatori AI. Discutono il girato analizzato: un montatore si concentra sul ritmo, uno sulla storia e uno sul mestiere, e un regista decide cosa tenere.

**Usala quando** vuoi un montaggio ponderato di una registrazione lunga, come un parlato in camera o una registrazione dello schermo, e la prima scelta di un singolo montatore non basta.

**Template:** [Montaggio dei momenti migliori, deciso da un team di montaggio](./templates.md#highlight-edit-decided-by-an-editing-team-video-derush-edit-room).

## Stanza grafica {#graphics-room}

La stanza grafica è un gruppo di artisti AI. Uno pianifica l'impaginazione, uno i tempi, uno i testi e uno l'inserimento dei contenuti dello schermo. Un regista decide quali titoli, sottopancia e callout vanno dove, e li disegna.

**Usala quando** il video ha bisogno di più titoli o callout che devono stare bene insieme, e vuoi che la pianificazione consideri l'insieme invece di una grafica alla volta.

**Template:** [Montaggio del girato con titoli, progettato da un team di grafica](./templates.md#footage-edit-with-titles-designed-by-a-graphics-team-video-derush-edit-graphics-room).

## Stanza del colore {#colour-grade-room}

La stanza del colore è un gruppo di coloristi AI. Discutono il colore misurato di ogni inquadratura, e un regista sceglie un solo look per tutto il video. Il look è uno tra **Nessuno**, **Caldo**, **Freddo**, **Filmico**, **Vivace**, **Tenue** o **Mono**, con un'intensità **Leggera**, **Normale** o **Forte**.

**Usala quando** il girato ha luci o colori disomogenei e vuoi un look coerente su tutto il montaggio.

**Template:** [Montaggio del girato con color grading](./templates.md#footage-edit-with-a-colour-grade-video-derush-edit-grade-room).

## Stanza dailies {#dailies-room}

La stanza dailies è un gruppo di giudici AI. Lavora su video generato che ha più riprese di ogni clip pianificata. I giudici confrontano le riprese di ogni inquadratura, e un regista sceglie la ripresa migliore per ciascuna. Un'inquadratura che la stanza lascia indecisa mantiene la prima ripresa.

**Usala quando** hai b-roll generato e vuoi la ripresa più forte di ogni clip invece della prima, al prezzo di generare più di una ripresa.

**Template:** [Montaggio del girato con b-roll AI, ripresa migliore scelta da un gruppo](./templates.md#footage-edit-with-ai-b-roll-best-take-chosen-by-a-panel-video-derush-edit-dailies).

## Le stanze costano più di un singolo passaggio {#rooms-cost-more-than-a-single-step}

Una stanza fa molte chiamate AI, perché ogni membro del gruppo interviene più volte prima che il regista decida. Un'esecuzione che usa una stanza consuma quindi più crediti della stessa esecuzione con un singolo passaggio al suo posto. La stima prima dell'esecuzione include il costo della stanza, così lo vedi prima di iniziare (vedi [Leggere la stima](../account/reading-the-estimate.md)).

Usa una stanza dove la cura in più vale i crediti in più. Per una bozza veloce, di solito basta un singolo passaggio.

## Quali piani includono le stanze {#which-plans-include-rooms}

Non tutti i piani includono le stanze. Controlla la tabella dei piani in [Piani e crediti](../account/plans-and-credits.md#the-plans-on-sale-in-reelbolt), oppure apri **Impostazioni → Piano e fatturazione** per vedere il tuo piano. Se il tuo piano non include una stanza, il template che la usa non può essere eseguito nel tuo spazio di lavoro finché non passi a un piano che la include.

## Pagine correlate {#related-pages}

- [Template dei flussi di lavoro](./templates.md): i template integrati, compresi quelli che usano le stanze.
- [Tipi di passaggio](./step-types.md): i singoli passaggi che una stanza sostituisce.
- [Leggere la stima](../account/reading-the-estimate.md): quanto costa un'esecuzione prima di avviarla.
