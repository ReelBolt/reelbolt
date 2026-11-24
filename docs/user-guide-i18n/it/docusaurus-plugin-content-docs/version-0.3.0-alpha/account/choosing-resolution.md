---
title: Scegliere la risoluzione
sidebar_position: 10
description: Come scegliere la risoluzione di output per un'esecuzione o per un'esportazione dall'editor, come viene misurata, cosa succede se scegli una risoluzione non inclusa nel tuo piano e perché una risoluzione più alta consuma più crediti.
i18n_source_sha: 5f28b5198c7079370afb2f20651d52f662cc93b7
---

# Scegliere la risoluzione {#choosing-a-resolution}

Questa pagina spiega come scegliere la risoluzione con cui viene creato un video, sia per un'esecuzione di un flusso di lavoro sia per un'esportazione dall'editor. Spiega come ReelBolt misura la risoluzione, cosa succede se scegli un valore non incluso nel tuo piano e perché una risoluzione più alta consuma più crediti. Il tuo piano stabilisce la risoluzione massima che puoi scegliere. La tabella dei piani in [Piani e crediti](./plans-and-credits.md#the-plans-on-sale-in-reelbolt) indica il limite di ciascun piano, e **Impostazioni → Piano e fatturazione** mostra quello attuale.

## Come viene misurata la risoluzione {#how-resolution-is-measured}

La risoluzione si misura sul **lato corto** del video, così una sola impostazione va bene sia per un video orizzontale sia per uno verticale. Il 4K è 3840×2160 in orizzontale e 2160×3840 in verticale. Un video quadrato si misura allo stesso modo: un quadrato di 2160×2160 conta come 4K.

## Scegliere una risoluzione per un'esecuzione {#choose-a-resolution-for-a-run}

1. Apri il flusso di lavoro e avvia l'esecuzione dalla pagina del flusso di lavoro.
2. Nelle impostazioni dell'esecuzione, accanto allo stile di montaggio e al rapporto d'aspetto, apri **Risoluzione**.
3. Scegli un valore. **Auto** mantiene l'output fino a 1080p, o al limite del tuo piano se è più basso, così un'esecuzione genera in 4K solo quando lo chiedi tu. In Enterprise, Auto mantiene le dimensioni della tua sorgente.
4. Controlla la stima, che include già la risoluzione scelta (vedi [Leggere la stima](./reading-the-estimate.md)), poi avvia l'esecuzione.

Puoi anche chiedere una risoluzione all'assistente, per esempio "in 4K". Controlla prima il tuo piano e ti indica il limite se chiedi più di quanto il piano consente.

Le opzioni oltre il limite del tuo piano appaiono disattivate. Se un'esecuzione viene comunque inviata con una di queste, per esempio tramite una chiamata API, ReelBolt la rifiuta prima che parta e indica quale risoluzione consente il tuo piano.

## Scegliere una risoluzione per un'esportazione dall'editor {#choose-a-resolution-for-an-export-in-the-editor}

:::note Disponibilità dell'editor video

L'editor video, compreso il suo spazio **Consegna**, è disponibile come **anteprima anticipata**. Per un'esecuzione
non avviata dall'editor, scegli la risoluzione nelle impostazioni dell'esecuzione, descritte sopra.

:::

Nell'editor, apri lo spazio **Consegna** e scegli un formato. Se il formato è più grande di quanto consente il tuo piano, ReelBolt lo riduce al limite del piano mantenendo la stessa forma, e il modulo mostra le dimensioni che avrà l'esportazione. Il 4K appare disattivato quando il tuo piano non lo include. Vedi [Generare un video finito (Consegna)](../editor/index.md#rendering-a-finished-video-deliver).

## Quali risoluzioni consente il tuo piano {#which-resolutions-your-plan-allows}

Ogni piano stabilisce la risoluzione massima che un'esecuzione o un'esportazione può produrre. Se il tuo spazio di lavoro passa a un piano inferiore mentre un'esecuzione è in attesa, l'esecuzione termina alla risoluzione massima del nuovo piano. Per vedere il limite del tuo piano, apri **Impostazioni → Piano e fatturazione**.

## Quanto costa una risoluzione più alta {#what-a-higher-resolution-costs}

Il rendering e la codifica si addebitano in base alla risoluzione dell'output, misurata sul lato corto. Un render in 4K consuma circa quattro volte i crediti di render di uno in 1080p, e uno in 720p ne consuma meno della metà. Un video quadrato o verticale costa come un video widescreen con lo stesso lato corto.

Il lavoro dei modelli in un'esecuzione, come scrittura, pianificazione e trascrizione, costa uguale a qualsiasi risoluzione, quindi cambia solo la parte di rendering del costo. La stima nella pagina dell'esecuzione include già la risoluzione che hai scelto.

## Pagine correlate {#related-pages}

- [Piani e crediti](./plans-and-credits.md): la tabella dei piani e come funzionano i crediti.
- [Leggere la stima](./reading-the-estimate.md): cosa significa la riga del costo prima di un'esecuzione.
- [Gestire il tuo piano](./managing-your-plan.md): upgrade, passaggio a un piano inferiore e cancellazione.
