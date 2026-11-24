---
title: Scegli una ricetta
sidebar_position: 0
description: Abbina quello che vuoi creare alla ricetta ReelBolt che lo realizza e scopri quali richieste ReelBolt non può ancora soddisfare.
i18n_source_sha: 6b8451c159c2e14555d7ea95b400bec5687cad93
---

# Scegli una ricetta {#choose-a-recipe}

Una ricetta è un'app già pronta: la scegli da **Parti da una ricetta** in un progetto, compili qualche
impostazione e premi **Avvia**. Ogni pagina di ricetta qui sotto spiega cosa impostare prima di avviarla, quanto
costa a parole, cosa ottieni e cosa fare quando qualcosa va storto. Il prezzo esatto di un'esecuzione
compare nella pagina dell'esecuzione prima che tu la avvii.

## Crea un video promo dalla tua app {#make-a-promo-video-from-your-app}

| Cosa vuoi | Usa | Serve |
|---|---|---|
| Un breve promo con le schermate reali della tua app | [Video promo dal tuo codice](promo-from-code.md) | Il codice sorgente della tua app nel progetto |
| Lo stesso promo, per un'app grande, con meno lettura a ogni passaggio | [Video promo dal tuo codice (più leggero)](promo-from-code.md#recipe-promo-video-from-your-code-lighter) | Il tuo codice sorgente |
| Un promo che segue i colori, i caratteri e le note di prodotto del tuo brand | [Video promo nel tuo brand](promo-in-your-brand.md) | Un design approvato e un profilo di prodotto |
| Un promo con morbidi sfondi animati dietro le scene | [Video promo con sfondi AI](promo-with-ai-backgrounds.md) | Il tuo codice sorgente e un provider di generazione video |

## Monta il girato che hai filmato {#edit-footage-you-filmed}

| Cosa vuoi | Usa | Serve |
|---|---|---|
| Tempi morti, false partenze e ciak ripetuti tagliati | [Montaggio dei momenti migliori dal girato](derush-edit.md#recipe-highlight-edit-from-footage) | I tuoi clip |
| Un montaggio più ponderato su girato lungo o narrativo | [Montaggio dei momenti migliori, deciso da un team di montaggio](derush-edit.md#recipe-highlight-edit-decided-by-an-editing-team) | I tuoi clip |
| Lo stesso montaggio con musica di sottofondo | [Montaggio del girato con musica di sottofondo](music-sfx-and-grade.md#recipe-footage-edit-with-background-music) | I tuoi clip e una traccia musicale che carichi |
| Lo stesso montaggio con voce narrante e sottotitoli | [Montaggio del girato con voce narrante e sottotitoli](voiceover.md#recipe-footage-edit-with-narration-and-captions) | I tuoi clip e un provider vocale |
| Lo stesso montaggio con inquadrature extra generate dall'AI (apertura a freddo, stacchi, schermata finale) | [Montaggio del girato con b-roll generato dall'AI](generated-b-roll.md#recipe-footage-edit-with-ai-generated-b-roll) | I tuoi clip e un provider di generazione video |

## Aggiungi sottotitoli a un video finito {#add-subtitles-to-a-finished-video}

| Cosa vuoi | Usa | Serve |
|---|---|---|
| Sottotitoli impressi in un video finito, nella lingua parlata, più i file SRT e VTT | [Aggiungi sottotitoli a un video](subtitles.md#subtitles-in-the-language-that-is-spoken) | Il tuo video e un provider di riconoscimento vocale |
| Gli stessi sottotitoli tradotti in un'altra lingua, con i tempi originali | [Aggiungi sottotitoli a un video](subtitles.md#subtitles-in-another-language) | Il tuo video, un provider di riconoscimento vocale e un provider di chat AI |

Nella pagina dell'esecuzione non c'è ancora un pulsante per scaricare i file SRT e VTT; la pagina indicata sopra spiega dove trovarli.

## Metti una registrazione dello schermo su uno schermo in un clip {#put-a-screen-recording-on-a-screen-in-a-clip}

| Cosa vuoi | Usa | Serve |
|---|---|---|
| Una registrazione dello schermo, un video o un'immagine collocati su un green screen nel tuo clip | [Inserimento su green screen](tracked-screen-insert.md#recipe-green-screen-insert) | Un clip con un green screen e il contenuto da inserire |

## Crea un promo, uno spiegone o un clip da un'idea {#make-a-promo-an-explainer-or-a-clip-from-an-idea}

| Vuoi | Usa | Serve |
|---|---|---|
| Un promo del tuo prodotto, con un interruttore per la voce narrante | [Product promo, your way](product-promo-your-way.md) | Il tuo codice sorgente, e un fornitore di voce se la voce narrante è attiva |
| Una persona dei tuoi avatar che spiega un argomento o dice le tue parole (circa 15 secondi al massimo) | [Avatar explainer from a topic](avatar-explainer.md) | Un avatar pronto con una voce, e l'invio dei media attivato da te |
| Un breve clip AI di una scena che descrivi | [AI video clip from your idea](ai-clip-from-idea.md) | Un fornitore di generazione video |

## Crea audio o un'immagine di copertina {#make-audio-or-a-cover-picture}

| Vuoi | Usa | Serve |
|---|---|---|
| Una traccia musicale o un breve effetto sonoro da una descrizione | [Music or sound effect](music-or-sound-effect.md) | Un fornitore di musica o di effetti sonori |
| Il tuo testo letto ad alta voce come file audio | [Read my text aloud](read-text-aloud.md) | Un fornitore di voce |
| Un'immagine di copertina dal tuo video, con un titolo | [Thumbnail and cover text](thumbnail-and-cover-text.md) | Un video nel progetto |

## Cosa ReelBolt non può ancora fare {#what-reelbolt-cannot-do-yet}

- **Avatar parlanti lunghi.** Dove gli [avatar](../projects/avatars.md) sono attivi, **Talking avatar (budget)** fa dire a
  un avatar del progetto un breve copione (da 5 a 15 secondi) e **Spokesperson clip** mostra un avatar senza parole precise;
  scegli la persona nel campo **Avatar** della pagina dell'app. Dove i campi della ricetta sono attivi, le due sono una sola
  ricetta, **Clip con avatar**, con l'interruttore **Modalità leggera (più economica)** per il breve copione (vedi
  [Impostazioni di esecuzione](run-settings.md#recipe-fields)). [Avatar explainer from a topic](avatar-explainer.md) crea un clip parlato da un argomento, ma anch'esso dura circa 15 secondi al massimo. Non esiste una ricetta per un video più lungo con un presentatore.
  Per un video con voce narrante, usa [Montaggio del girato con voce narrante e sottotitoli](voiceover.md#recipe-footage-edit-with-narration-and-captions)
  su girato reale, oppure un promo con una [voce narrante](voiceover.md).
- **Caroselli di immagini.** Non esiste una ricetta che crei una serie di slide statiche per un carosello social.
- **Cambiare il formato di un video finito.** Non esiste una ricetta che prenda un video finito e lo rimonti
  in un altro formato, per esempio da un film orizzontale a uno verticale. Scegli il formato (**Formato**
  in [Impostazioni di esecuzione](run-settings.md)) prima che una ricetta crei un nuovo video; dopo non puoi più cambiarlo.

Se il tuo obiettivo non è nelle tabelle, descrivilo all'assistente. Ti dice qual è la ricetta più
vicina, oppure che nessuna va bene, invece di avviare un'esecuzione che non può darti quello che hai chiesto.

## Prima di premere Avvia {#before-you-press-run}

Ogni ricetta mostra una breve riga di verifica sopra **Avvia**. Un clip, un archivio di codice, una traccia
musicale, un profilo o un provider mancante viene indicato lì, con un link che risolve il problema. Vedi
[Impostazioni di esecuzione](run-settings.md) per sapere cosa cambia ogni impostazione.
