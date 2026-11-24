---
title: Campi dell'app
sidebar_position: 1.5
description: I campi che un'app chiede prima di partire (un prompt, un formato, un interruttore, un modello), come compilarli, come preparare i campi di un'app e come cambiare un campo di un'esecuzione finita.
i18n_source_sha: 4a6e8f5072d93f348e62497973ec490e6cf3279a
---

# Campi dell'app {#app-fields}

Alcune app chiedono qualche informazione tutta loro prima di partire: cosa deve mostrare il video, un formato, una
durata, un interruttore **Modalità leggera (più economica)** o quale modello IA crea la clip. Sono i **campi**
dell'app. Compaiono nella pagina dell'app, sopra **Avvia**, accanto alle scelte di sempre come la lingua e le note.

I campi sono sempre disponibili. Nella finestra Avvia dell'editor i campi di un'app prendono il posto del campo libero
**Il tuo brief**; un'app senza campi propri mantiene le scelte di sempre e il brief.

## Compilare i campi {#filling-in-the-fields}

Ogni campo ha un nome, a volte una riga di aiuto, e spesso un esempio in grigio dentro la casella vuota. L'esempio è
solo un suggerimento: non viene mai inviato.

- **Testo.** Scrivi con parole tue. Un testo lungo mostra quanti caratteri restano quando ti avvicini al limite.
- **Numero** e **Durata.** Scegli uno dei valori proposti, oppure scrivi un numero entro i limiti.
- **Scelta.** Scegli un'opzione. Alcune opzioni hanno sotto una breve riga che dice cosa fanno.
- **Interruttore.** Acceso o spento. Le parole sotto l'interruttore dicono cosa fa ciascuna posizione. Un solo
  interruttore può cambiare più cose insieme: **Sottotitoli** in un montaggio, per esempio, fa trascrivere le tue clip e
  mette le parole sullo schermo.
- **Formato.** Verticale, quadrato, orizzontale o ritratto, secondo ciò che l'app propone.
- **Modello.** Vedi [Scegliere un modello](#choosing-a-model) più sotto.

Un campo obbligatorio va compilato prima che l'app possa partire. Se premi **Avvia** senza compilarlo, la pagina ti
porta al campo e dice cosa manca. Alcuni campi compaiono solo dopo un'altra scelta: per esempio **Cosa dice** appare
solo quando **Modalità leggera (più economica)** è accesa.

Un campo vuoto non obbligatorio mantiene l'impostazione dell'app.

## Altre opzioni {#more-options}

Un'app mostra i campi che le persone cambiano più spesso. Gli altri stanno in **Altre opzioni**, chiuse finché non le
apri; il pulsante dice quanti sono. La pagina ricorda se le hai lasciate aperte per quell'app.

## Scegliere un modello {#choosing-a-model}

Un campo modello permette di scegliere il modello IA usato da un passaggio, per esempio quello che crea una clip
generata o quello che scrive il testo.

- Il modello consigliato è il primo dell'elenco ed è quello usato se non scegli nulla.
- Ogni modello mostra il suo prezzo a parole rispetto agli altri, come **Il più economico** o **Circa 2× il prezzo**.
  Qui ReelBolt non mostra mai il prezzo di un modello in crediti; il totale dell'esecuzione è mostrato sopra **Avvia**.
- Un modello che non si può usare per questa app è in grigio e dice perché, per esempio "Non disponibile nel tuo
  spazio di lavoro" o "Non può creare questo tipo di clip".
- Anche un modello che il tuo piano non include è in grigio e dice quale parte del piano serve, per esempio "Il tuo
  piano non include la generazione di video" o "Il tuo piano non include gli avatar". Se invii comunque un modello
  così, per esempio dall'assistente, l'esecuzione non parte e il campo dice la stessa cosa.
- Su un telefono, i modelli si aprono in un pannello da un solo pulsante.

## Il prezzo segue le tue scelte {#the-price-follows-your-choices}

Il prezzo sopra **Avvia** è calcolato da ciò che hai compilato. Cambiare un modello, una durata, un formato, un numero
o un interruttore lo aggiorna dopo un attimo; scrivere un testo non lo cambia.

Anche i crediti messi da parte all'avvio seguono il modello scelto. Un passaggio già eseguito nel tuo spazio di lavoro
mette da parte quanto costa di solito, adattato al prezzo del modello scelto: un modello più economico mette da parte
meno, uno più caro di più. Un modello di voce che scegli viene prezzato come quel modello, non come quello di sempre.

Un interruttore come **Modalità leggera (più economica)** può portare l'app su un percorso diverso e più economico. Il
prezzo e i controlli prima dell'esecuzione riguardano allora solo il percorso che hai scelto.

## Vedere cosa ha usato un'esecuzione {#seeing-what-a-run-used}

Ogni esecuzione nella cronologia dell'app mostra i suoi primi campi in una riga breve, per esempio *Modalità leggera
(più economica) · "Benvenuti da Caffè Lume!"*. Apri un'esecuzione per vedere tutti i campi che ha usato, con i nomi
che l'app aveva in quel momento e un modello con il suo nome.

## Cambiare un campo di un'esecuzione finita {#changing-one-field-of-a-finished-run}

Quando un'esecuzione è finita, **Cambia una cosa e riavvia** ti permette di cambiare uno o più campi, e le impostazioni
dell'esecuzione che la pagina mostra (lingua, durata, voce narrante, i tuoi file, le note), e rifare solo le parti del
video che li usano. Tutto il resto viene ripreso senza costi. Vedi
[Cambiare i campi di un'app](request-changes.md#changing-an-apps-fields).

Un campo che sceglie un altro percorso (come **Modalità leggera (più economica)**) non si può cambiare così: avvia di
nuovo l'app.

## Preparare i campi di un'app {#setting-up-an-apps-own-fields}

Se puoi modificare il flusso di lavoro, puoi scegliere quali impostazioni le persone compilano nella pagina dell'app:

1. Apri il flusso di lavoro e passa alla vista **Avanzata**.
2. Premi **Input dell'app** per vedere la pagina dell'app e un'anteprima dal vivo.
3. Aggiungi un campo con **Aggiungi un campo**, oppure apri un passaggio e usa **Campi dell'app** per trasformare una
   delle sue impostazioni (un prompt video, una durata, un modello e così via) in un campo.
4. Controlla **Cosa cambia quando salvi**, sotto l'elenco: dice a parole quali campi vengono aggiunti, cambiati o
   rimossi e quali impostazioni iniziano o smettono di usare un campo.
5. Premi **Salva modifiche**.

Trascina un campo dalla sua maniglia per spostarlo, oppure usa le frecce. Un testo breve può diventare un testo lungo (e
viceversa), e un numero una durata (e viceversa), da **Tipo di campo** nel suo **Modifica**; per qualsiasi altro tipo,
aggiungi un nuovo campo e usalo dove era usato quello vecchio.

Altre cose che i **Campi dell'app** di un passaggio possono fare:

- **Un interruttore o una scelta per un'impostazione di altro tipo.** Scegli un interruttore o una scelta per
  l'impostazione e poi, per ogni risposta, cosa usa il passaggio: per esempio un interruttore **Dissolvenza finale** che
  scrive 800 ms quando è acceso e 0 quando è spento. Lascia una risposta su **Tieni il valore del passaggio** per non
  cambiare nulla con quella. Un solo interruttore può compilare più impostazioni, anche in passaggi diversi (un
  interruttore **Sottotitoli** può chiedere la trascrizione all'analisi e accendere i sottotitoli nel montaggio).
- **Un elemento di un elenco.** Un'impostazione dentro un elenco, come l'effetto usato sul primo oggetto da nascondere,
  ha una riga per elemento, così una scelta può decidere **Sfocatura** o **Pixelatura** per quell'elemento.
- **Parole pronunciate esattamente.** In un passaggio di voce narrante con le frasi scritte nel passaggio, l'impostazione
  **Words to speak** crea un campo di testo le cui parole vengono dette così come sono scritte. Ogni paragrafo (separato da una
  riga vuota) è una frase parlata, nell'ordine.
- **L'immagine di un avatar che parla.** In una clip con avatar che parla, l'impostazione **Picture model** fa scegliere
  il modello che crea l'immagine fissa animata dalla clip.

La guida completa, con i modelli, **Mostra solo quando**, il messaggio per i passaggi IA e un percorso più economico
con un interruttore, è in [Preparare la pagina di un'app](templates.md#setting-up-an-apps-page).

Puoi anche chiederlo all'assistente, per esempio "fai scegliere il formato e scrivere cosa succede nel video" o
"aggiungi una modalità più economica a questa app". Ti mostra i campi che aggiungerà prima di salvare qualsiasi cosa.

## Ricette con campi {#recipes-with-fields}

Diverse ricette hanno campi propri, come **Invito all'azione**, **Cosa nascondere** o **Stile dei sottotitoli**.
L'elenco è in [Campi della ricetta](../recipes/run-settings.md#recipe-fields).

Una ricetta la cui pagina ha un campo modello mostra **Modelli disponibili** sulla sua scheda nella galleria. Aprilo
per vedere i modelli che ogni campo offre prima di creare l'app: prima quello consigliato, il prezzo a parole e il
motivo per cui un modello non si può scegliere. Anche l'assistente può dirti le stesse cose su una ricetta.
