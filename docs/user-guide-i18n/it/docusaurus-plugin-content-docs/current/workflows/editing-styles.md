---
title: Stili di montaggio e formato del video
sidebar_position: 5
description: Scegli come ReelBolt taglia il tuo video (veloce e incisivo, calmo e cinematografico), in che formato esce (verticale, quadrato, ritratto o orizzontale) e a quale risoluzione, ogni volta che esegui un flusso di lavoro.
i18n_source_sha: 926ece143a001009be1ab5a76dd24413b94aa43b
---

# Stili di montaggio e formato del video {#editing-styles-and-video-shape}

Lo stesso flusso di lavoro e lo stesso girato possono diventare video molto diversi. Uno **stile di montaggio** dice
all'AI come tagliare (quanto veloce, in che ordine, come si uniscono le inquadrature, che sensazione deve dare) e il
**formato del video** decide se esce verticale per Reels e TikTok, quadrato per un feed od orizzontale per
YouTube. Scegli entrambi quando premi **Avvia**, e valgono solo per quell'esecuzione.

## Scegliere stile e formato quando avvii un flusso di lavoro {#choosing-a-style-and-shape-when-you-run-a-workflow}

Premi **Avvia** su un flusso di lavoro. Sotto il tuo brief, la finestra di conferma ha tre scelte in più:

- **Stile di montaggio**: scegli uno degli stili integrati, uno dei tuoi stili salvati oppure **Nessuno stile
  particolare**. Un breve riepilogo sotto il menu dice cosa fa lo stile.
- **Formato del video**: un solo clic: **Come impostato** (mantiene il formato con cui è stato creato il flusso di lavoro), **9:16**
  (Reels, TikTok, Shorts), **1:1** (post quadrati nel feed), **4:5** (post in ritratto nel feed che riempiono più
  schermo) o **16:9** (YouTube, siti web).
- **Risoluzione**: **Auto**, **720p**, **1080p** o **4K**; vedi
  [Scegliere la risoluzione di un'esecuzione](#choosing-the-resolution-of-a-run).

Alcuni stili integrati suggeriscono un formato: "Reel social veloce" sceglie il 9:16 per te. Se fai clic tu su un formato,
vince la tua scelta. Lo storico delle esecuzioni mostra stile, formato e risoluzione di ogni esecuzione, così puoi confrontare due
versioni dello stesso video affiancate.

## Gli stili integrati {#the-built-in-styles}

ReelBolt include sei stili che puoi usare subito:

- **Reel social veloce** (`fast-social-reel`): parte dal momento migliore, tagli rapidi e netti, una grande parola di sottotitolo alla volta,
  colori accesi. Suggerisce 9:16.
- **Cinematografico** (`cinematic`): lento e atmosferico, inquadrature lunghe, passaggi morbidi, un look colore da film e pochissimo
  testo. Suggerisce 16:9.
- **Spiegazione aziendale** (`corporate-explainer`): espone il problema, poi mostra la risposta; sottotitoli chiari e titoli ordinati.
  Suggerisce 16:9.
- **Documentario** (`documentary`): racconta la storia in ordine e lascia che le persone finiscano le frasi, con colori
  naturali.
- **Video musicale a ritmo di musica** (`beat-synced-music-video`): tagli a tempo con la musica, un montaggio di momenti, colori vivaci, nessun titolo.
  Suggerisce 9:16.
- **Montaggio dinamico** (`dynamic-edit`): tagli che si muovono con la musica, zoom rapidi, inquadrature che si
  alternano a tempo e stacchi veloci. Suggerisce 9:16. Vedi [Montaggio dinamico e ritmo degli effetti](#dynamic-edit-and-pace).

## Montaggio dinamico e ritmo degli effetti {#dynamic-edit-and-pace}

Lo stile **Montaggio dinamico** rende il montaggio vivo. L'AI sceglie quali momenti ricevono un movimento e di che tipo;
ReelBolt calcola i tempi esatti, così ogni movimento cade a tempo con la musica. Non taglia a ogni battito, a meno che
non venga chiesta un'alternanza rapida. I movimenti disponibili:

- **Tagli a tempo** dentro una lunga inquadratura.
- **Zoom in** rapido a tempo, e zoom indietro.
- **Due scene che si alternano** a tempo per qualche battito, poi ne vince una. Una delle due può essere un clip generato, per esempio un'inquadratura statica contro un b-roll notturno.
- **Strobo**: la stessa alternanza sul drop, per una battuta.
- **Fotogramma flash**: un solo fotogramma bianco o nero a tempo.
- **Rampa di velocità**: il battito prima del drop rallenta, poi torna normale (solo su girato con audio originale disattivato).
- **Whip**: uno stacco veloce a frustata su una battuta.

Il **ritmo degli effetti** decide quanto è fitto il montaggio. Scegli **Calmo**, **Medio**, **Veloce** o **Spento** accanto allo stile quando avvii;
il ritmo degli effetti che scegli sostituisce quello dello stile. È su **Spento** a meno che lo stile ne abbia uno (lo stile **Montaggio dinamico** è
**Veloce**), e **Spento** lascia il video tagliato come lo taglia il flusso di lavoro.

| | Calmo | Medio | Veloce |
|---|---|---|---|
| Movimenti ogni 10 secondi | 1 | 3 | 6 |
| Movimenti consentiti | Zoom in, whip | + tagli a tempo, zoom indietro, fotogramma flash | + scene alternate, strobo, rampa di velocità |
| Intensità | Leggera | Normale | Forte |
| Distanza tra i movimenti | 4 battiti | 2 battiti | 1 battito |

Tra i movimenti scelti dall'editor, il montaggio aggiunge zoom rapidi negli spazi liberi (ogni 2 battiti su Veloce, ogni 4 su Medio, nessuno su Calmo), mai vicino a un altro movimento e mai oltre il limite del ritmo degli effetti. Il limite vale ogni 10 secondi dell'intero film: un film di 30 secondi su Veloce può avere fino a 18 movimenti.

Per un risultato migliore carica girato con più angolazioni. Senza un cambio di angolazione, un taglio si nota poco.

## Creare i tuoi stili {#making-your-own-styles}

Apri **Stili** nel menu a sinistra per vedere gli stili integrati e i tuoi.

- **Nuovo stile** parte da zero; **Crea una copia** su uno stile integrato parte da quello, così puoi
  cambiare una sola cosa.
- Per ogni scelta (**Ritmo**, **Ordine della storia**, **Tagli**, **Tono**, **Sottotitoli**, **Colore**, **Energia
  della musica**, **Testo sullo schermo**, **Dinamica**) scegli un'opzione o lasciala su **Nessuna preferenza**.
- **Note per l'AI** è una o due frasi facoltative su aspetto e atmosfera ("calda e un po' nostalgica",
  "tieni il prodotto sullo schermo il più possibile"). Le note sono trattate come una preferenza,
  mai come istruzioni che cambiano il funzionamento del flusso di lavoro.
- Quando crei uno stile dall'interno di un progetto puoi spuntare **Usa solo in questo progetto**.

Modificare o eliminare uno stile non cambia mai le esecuzioni già fatte: ogni esecuzione conserva una propria copia dello
stile che ha usato.

## Cosa cambia uno stile e cosa no {#what-a-style-changes-and-what-it-does-not}

Uno stile orienta ogni agente AI dell'esecuzione (il montatore che sceglie le inquadrature, chi decide
musica, colore, effetti sonori, titoli e narrazione) verso le tue scelte. Regola anche direttamente alcuni
ritocchi finali:

- I tagli **Morbidi** attivano transizioni delicate tra le inquadrature, a meno che il flusso di lavoro non ne abbia già impostate di proprie.
- Una scelta in **Sottotitoli** cambia l'aspetto dei sottotitoli quando il flusso di lavoro li aggiunge già.
  Non aggiunge mai sottotitoli a un flusso di lavoro che non ne ha.

Uno stile non può aggiungere passaggi a un flusso di lavoro. Se il tuo flusso di lavoro non ha un passaggio musicale, scegliere "alta energia
della musica" non aggiunge musica; se non ha sottotitoli, la scelta sui sottotitoli non fa nulla. Per aggiungerli, usa
un template o chiedi all'assistente di modificare il flusso di lavoro.

## Come funziona il formato del video {#how-the-video-shape-works}

Scegliere un formato cambia la dimensione del video finito per ogni passaggio di montaggio dell'esecuzione. Quando il
formato è più alto o più quadrato del tuo girato, ReelBolt ritaglia ogni clip attorno al soggetto (la persona
o lo schermo del telefono) invece di tagliare semplicemente i lati.

L'editor AI conosce il formato di destinazione e quello di ciascuna delle tue clip, e preferisce le clip che
già corrispondono: per un reel 9:16 sceglie il girato che hai ripreso in verticale, e usa il girato largo solo
per i momenti che nient'altro copre. Per ottenere il miglior video verticale, carica del girato ripreso in ritratto.

## Scegliere la risoluzione di un'esecuzione {#choosing-the-resolution-of-a-run}

Quando premi **Avvia** su un flusso di lavoro, la finestra di conferma offre una scelta **Risoluzione** accanto al
formato del video. Vale solo per quell'esecuzione:

- **Auto**: la dimensione abituale, ed è l'impostazione predefinita. Un video verticale, quadrato, in ritratto od orizzontale esce
  a 1080p, e il formato originale alla dimensione del tuo girato, mai oltre 1080p né oltre il limite del tuo piano,
  a seconda di quale è più basso (quindi 720p nella prova gratuita). In Enterprise, il formato originale mantiene la dimensione del tuo girato.
  È il modo in cui funzionavano le esecuzioni prima che esistesse questa scelta.
- **720p**, **1080p** o **4K**: il video finito esce a quella risoluzione.

La risoluzione si misura sul **lato corto** del video, quindi funziona allo stesso modo per ogni formato: il 4K
è 3840×2160 per un video 16:9 e 2160×3840 per uno 9:16, e il 1080p è 1920×1080 o 1080×1920.

Il tuo piano stabilisce la risoluzione massima che puoi scegliere: 720p nella prova gratuita, 1080p con Creator, 4K con Pro e
Team, e 4K e oltre con Enterprise. Le scelte oltre il tuo piano appaiono disattivate, con un suggerimento per
passare a un piano superiore. Una risoluzione più alta consuma più crediti di render (il 4K circa quattro volte il 1080p) e la
stima prima dell'esecuzione la include. Vedi
[Risoluzione di esportazione in ogni piano ReelBolt](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan).

## Chiedere all'assistente {#asking-the-assistant}

Puoi chiedere all'[assistente](../assistant/index.md) di "elencare i miei stili", "salvare uno stile chiamato Reel di lancio:
veloce, hook all'inizio, sottotitoli incisivi", o "eseguire il flusso di lavoro del reel in stile cinematografico come video
4:5 in 4K". Ti mostra cosa sta per salvare o eseguire e aspetta il tuo sì.
