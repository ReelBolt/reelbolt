---
title: Doppia un video in un'altra lingua
sidebar_position: 6
description: Fai parlare un video che hai già in un'altra lingua, con le voci originali sostituite da una traduzione parlata e sottotitoli di ciò che viene detto.
i18n_source_sha: 6ee39ad68734213c0377bab1a69e5b5cc43d2ea6
---

# Doppia un video in un'altra lingua {#dub-a-video-into-another-language}

**Obiettivo:** un nuovo video in cui il parlato è in un'altra lingua. L'immagine resta la stessa, le voci originali sono sostituite da una traduzione parlata e i sottotitoli mostrano ciò che viene detto.

## Prima di avviarla {#before-you-run-it}

- **Il tuo video**, con del parlato, caricato nel progetto.
- Un servizio di trascrizione, un servizio di chat AI e un servizio di voce. Se ne manca uno, la riga di disponibilità sopra **Avvia** lo indica e rimanda alla soluzione. Li configura il tuo amministratore (vedi [Provider di inferenza](../admin/inference-providers.md)).

## Doppialo {#dub-it}

1. Apri il tuo progetto e vai su **App**.
2. Apri [Lingue](languages.md).
3. Scegli **Il tuo video**.
4. Scegli la **Lingua di destinazione**.
5. Attiva **Doppiaggio**. In **Altre opzioni** puoi scegliere la voce.
6. Premi **Avvia**. Il prezzo compare nella pagina dell'esecuzione prima che tu inizi.

Le labbra non sono risincronizzate: la bocca si muove ancora sulle parole originali.

## Altri modi per doppiare {#other-ways-to-dub}

Se le tue riprese vanno ancora tagliate, l'app [Modifica il mio video](edit-my-video.md) può tagliare e aggiungere una voce narrante in un'unica esecuzione. Per tenere il parlato originale ma abbassarlo sotto una nuova voce, vedi [Voce narrante](voiceover.md#when-the-footage-already-speaks).

## Dove andare dopo {#where-to-go-next}

- [Lingue](languages.md) elenca ogni campo e quanto costa.
- [Aggiungi sottotitoli a un video](subtitles.md) se vuoi solo i sottotitoli, non una nuova voce.
- Non sai quale app fa per te? Chiedi all'[assistente](../basics/assistant-basics.md) o vedi [Scegli un'app](choose-a-recipe.md).

## Come funziona {#how-it-works}

Questa pagina riguarda l'impostazione di doppiaggio dell'app Lingue. Il vecchio template **Video doppiato in un'altra lingua** (`video-derush-edit-dub`) è descritto in [Voce narrante](voiceover.md#dubbing-into-another-language). Usa un agente `NarrationTranslator` e l'impostazione `"voiceoverMode": "ReplaceDialogue"`.
