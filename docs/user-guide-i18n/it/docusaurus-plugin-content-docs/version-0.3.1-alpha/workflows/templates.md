---
title: Template dei flussi di lavoro
sidebar_position: 1
description: Ogni template di flusso di lavoro già pronto di ReelBolt, cosa produce ciascuno, cosa devi caricare prima e come aggiungerne uno a un progetto.
i18n_source_sha: 89a8f00e1656d5f8a5e28052914e5eb23b9863bd
---

# Template dei flussi di lavoro {#workflow-templates}

Un **flusso di lavoro** è la ricetta che ReelBolt segue per trasformare i tuoi file in un video. Un **template** è un
flusso di lavoro già pronto che puoi aggiungere a un progetto con un clic e poi eseguire così com'è o modificare.

ReelBolt include diciotto template in due famiglie:

- **Promo dal codice**: legge il codice sorgente di una web app che carichi e produce un video promozionale
  animato su di essa. Cinque template.
- **Montaggio di girato reale**: prende le clip video che carichi e le taglia, le narra, le decora e le
  rifinisce. Tredici template, tutti costruiti sullo stesso nucleo in tre passaggi: *analizzare il girato*,
  *decidere cosa tenere*, *produrre il file montato*.

Ogni template in questa pagina riporta il nome visualizzato, la sua chiave (il nome breve che usano l'assistente e
l'API), se viene aggiunto automaticamente, i suoi passaggi in ordine, cosa produce e cosa
devi caricare o configurare prima di eseguirlo.

**Ogni template ti chiede il brief quando lo esegui.** Facendo clic su **Avvia** si apre una finestra in cui
descrivi il video che vuoi: l'argomento, a chi è rivolto, quanto deve durare e il formato (per
esempio "reel verticale di 15 s sul timeline editor"). I passaggi AI lo leggono; una richiesta di reel, short, storia
o TikTok fa sì che i template promo producano un video verticale 9:16. Puoi lasciare la finestra
vuota per lasciar decidere all'AI.

**Ogni template rivede il proprio lavoro e può correggerlo.** Il passaggio di revisione di ogni template guarda
il film (fotogrammi, timeline e suono) e, quando un problema dipende da un'impostazione o da un passaggio
precedente, può cambiare quell'impostazione o far rifare quel passaggio solo per questa esecuzione, per esempio
ripetere una riga della voce narrante o rifare un'inquadratura generata. I passaggi rifatti vengono addebitati come
qualsiasi altro passaggio e la pagina dell'esecuzione elenca ogni modifica. Vedi
[il passaggio di ciclo di revisione](step-types.md#review-loop-step-reviewloop).

## Partire dalla scheda App {#start-from-the-apps-tab}

La scheda **App** del progetto (sostituisce la vecchia scheda Flussi di lavoro) elenca **Le tue app**, dall'esecuzione più recente, e
**Parti da una ricetta**. Una ricetta è un template. Fai clic su **Usa questa ricetta** e ReelBolt la prepara e la apre,
senza passare da un modulo vuoto. Ogni scheda dice a parole quanto costa l'app: **Nessun credito**, **Usa crediti**,
**Usa crediti e una voce** oppure **Usa crediti e inquadrature AI** (quella spesa ha un tetto per ogni esecuzione). L'ultima
esecuzione di un'app compare come **Completata** o **Non riuscita**. Se a una ricetta serve qualcosa che il tuo progetto non ha ancora, la scheda
lo dice con un link che risolve il problema, come **Aggiungi clip**. Usa i pulsanti delle categorie e la casella di ricerca per restringere
l'elenco, e **Flusso di lavoro personalizzato (avanzato)** in fondo alle ricette per costruire i tuoi passaggi.

## Eseguire un'app {#running-an-app}

Aprendo un'app vedi le poche scelte a sinistra (lingua, durata, formato, narrazione, stile, le tue clip e una nota
libera) e il risultato a destra. Sotto le scelte una piccola striscia ti dice, con un'icona e una parola, se
c'è qualcosa da sistemare prima di eseguire: **Da correggere**, **Controlla**, **Nota** o **Pronto**. Il pulsante **Avvia** mostra il
prezzo ("Avvia · circa 40 crediti") con l'intervallo abituale sotto, oppure **Nessun credito usato**. Se manca qualcosa,
come le clip, il pulsante resta grigio, dice perché e offre la soluzione con un clic. Mentre è in corso vedi passaggi
semplici ("Scrittura del copione", "Registrazione della narrazione"); quando finisce puoi riprodurre e **Scaricare** il video,
**Cambiare una cosa e rieseguire**, oppure **Chiedere all'assistente di cambiare qualcosa**. **Modifica i passaggi (avanzato)** nel menu
in alto apre il costruttore di passaggi.

## Preparare la pagina di un'app {#setting-up-an-apps-page}

Quando nel tuo spazio di lavoro sono attivi i campi personalizzati, il pulsante **Input dell'app** del costruttore di
passaggi apre la pagina che le persone vedono quando avviano l'app: un breve elenco dei suoi campi, con un'anteprima
della pagina accanto (sotto, su un telefono). Prova i campi nell'anteprima: non viene inviato nulla.

- Ogni campo mostra il suo nome, il suo tipo (**Testo breve**, **Testo lungo**, **Scelta**, **Interruttore**,
  **Numero**, **Durata**, **Formato** o **Modello**) e dove lo usa il flusso di lavoro, ad esempio "Passaggio 3 ·
  Video prompt". Un campo che nessuno usa lo dice, e l'app non si può salvare finché non lo usi o non lo rimuovi.
- **Modifica** apre un campo: il nome nella pagina, una riga di aiuto, un esempio (mostrato in grigio nella casella
  vuota, mai inviato), **Obbligatorio**, **In "Altre opzioni"**, il valore iniziale e ciò che serve al suo tipo: le
  opzioni di una scelta, il numero minimo e massimo, le durate o i formati da proporre, le parole di un interruttore.
  **Mostra solo quando** nasconde un campo finché un altro interruttore o scelta non ha un certo valore.
- **Aggiungi un campo** aggiunge un nuovo campo, oppure un'impostazione dell'esecuzione che i passaggi già leggono
  (lingua, durata e così via). Trascina un campo dalla sua maniglia, oppure usa le frecce, per cambiare l'ordine dei
  campi. Il cestino ne rimuove uno, e ogni passaggio che lo usava torna a un valore fisso; un campo letto da una
  condizione non si può rimuovere finché non cambi la condizione.
- **Tipo di campo**, nel **Modifica** di un campo, trasforma un testo breve in un testo lungo (e viceversa) o un numero
  in una durata (e viceversa): i passaggi che lo usano continuano a funzionare. Ogni altro cambio di tipo è un nuovo
  campo: aggiungilo, usalo dove si usa quello vecchio, poi rimuovi quello vecchio.
- **Messaggio per i passaggi IA** è un testo facoltativo aggiunto alla richiesta di ogni esecuzione, con **Inserisci
  un campo** per mettere un campo dove si trova il cursore, così gli autori IA vedono ciò che le persone hanno compilato.
- La pagina mostra al massimo 8 campi alla volta e ne contiene 16 in tutto; gli altri stanno in **Altre opzioni**.
  Tieni solo ciò che cambia da un'esecuzione all'altra.

**Compilare un'impostazione di un passaggio dalla pagina.** Apri un passaggio: **Campi dell'app** elenca le
impostazioni di quel passaggio che la pagina può compilare (per una clip generata: il prompt del video, la durata
della clip, il formato, le take e il modello). **Usa un campo per questa impostazione** sceglie un campo che hai già,
oppure ne crea uno nuovo dall'impostazione, con il suo nome, i suoi limiti e il valore attuale come valore iniziale;
per un testo, le parole di prima diventano l'esempio del campo. Un'impostazione compilata mostra **Dall'app:** e il
nome del campo, con **Smetti di usare il campo**. Un testo può mescolare parole fisse e campi: quando contiene un
campo, modificalo lì e usa **Inserisci un campo**. Un'impostazione già decisa da un'impostazione dell'esecuzione della
pagina (il formato, quando la pagina chiede il formato) lo dice. In un passaggio con un agente IA, **Lascia scegliere
il modello di questo agente** aggiunge un campo modello.

**Modelli.** Un campo modello permette di scegliere il modello usato da un passaggio. Di base si può scegliere
qualsiasi modello di quel tipo presente nel tuo spazio di lavoro. Nel suo **Modifica**, spegni **Le persone possono
scegliere qualsiasi modello** per spuntare i **Modelli consentiti**, e scegli quello **Consigliato**: viene mostrato
per primo e usato quando nessuno sceglie. Salva la pagina una volta prima di restringere l'elenco.

**Un percorso più economico.** Un campo **Interruttore** può scegliere tra due percorsi del flusso di lavoro. In un
passaggio di condizione, **Leggi un campo dell'app** mette l'interruttore nella condizione, e **Termina l'esecuzione
qui** chiude un percorso invece di proseguire verso un passaggio. Il prezzo nella pagina dell'app segue il percorso
scelto dall'interruttore.

**Cosa cambia quando salvi** sta sotto l'elenco: prima di salvare, dice a parole quali campi vengono aggiunti, cambiati
o rimossi, il nuovo titolo o messaggio per i passaggi IA e quali impostazioni iniziano o smettono di usare un campo.

**Salva modifiche** salva insieme i passaggi e la pagina. La pagina viene controllata al salvataggio e ogni problema
compare sotto il campo che riguarda.

## Come aggiungere un template a un progetto {#how-to-add-a-template-to-a-project}

I template si aggiungono a un progetto dal costruttore di flussi di lavoro:

1. Apri il progetto e scegli **Nuovo flusso di lavoro** (nel pannello Panoramica o nella scheda Flussi di lavoro).
2. Sotto **Parti da un template**, fai clic su una scheda. Ogni scheda mostra il nome del template e una
   descrizione di una riga di cosa ottieni e cosa devi caricare prima.
3. Fai clic su **Usa questo template**. ReelBolt crea subito il flusso di lavoro completo, con il nome del template e la
   sua versione (per esempio "Montaggio dei momenti migliori dal girato v1").

L'elenco nel costruttore mostra solo i template da attivare. L'unico template creato automaticamente,
**Video promo dal tuo codice** (`quick-win-promo`), è già aggiunto a ogni nuovo progetto, quindi non
compare in quell'elenco.

Puoi anche chiedere un template all'**assistente**. L'assistente può cercare qualsiasi template tramite la sua
chiave e ottenerne l'elenco completo di passaggi, pronto da proporre (con il suo strumento `GetWorkflowTemplate`), poi
offrirtelo come nuovo flusso di lavoro o come modifiche a uno esistente. Non viene salvato nulla finché non
lo consenti, a meno che tu non abbia disattivato **Chiedi prima di modificare** dell'assistente. Vedi
[l'assistente](../assistant/index.md).

Dopo l'aggiunta, un template è un normale flusso di lavoro: puoi rinominarlo, cambiare le impostazioni di qualsiasi passaggio,
aggiungere o rimuovere passaggi e salvare. Modificarlo non cambia mai il template stesso.

## Scegliere un template: guida rapida {#choosing-a-template-quick-guide}

Scegli un template in base a ciò da cui parti e a ciò che vuoi ottenere:

| Hai | Vuoi | Template (chiave) |
|---|---|---|
| Codice sorgente di una web app | Un video promo pulito (senza audio, a meno che tu non aggiunga la narrazione) | Video promo dal tuo codice (`quick-win-promo`) |
| Codice sorgente di una web app, e ti importa del costo | Lo stesso promo con meno token AI spesi | Video promo dal tuo codice (più leggero) (`lean-context-promo`) |
| Codice sorgente di una web app e un provider vocale | Un breve reel narrato, verticale quando lo chiedi | Reel verticale rapido (`cinematic-feature-spotlight`) |
| Codice sorgente di una web app e un provider vocale | Un trailer di lancio narrato, con una struttura da storia | Trailer di lancio narrato (`story-arc-launch-trailer`) |
| Codice sorgente di una web app e un account di generazione video | Il promo con sfondi astratti generati dall'AI | Video promo dal tuo codice con sfondi AI (`quick-win-promo-plates`) |
| Clip video (registrazioni dello schermo, riprese del prodotto, b-roll) e un provider vocale | Una storia narrata scritta dal tuo brief, con sottotitoli | Storia di prodotto narrata (`narrated-story`) |
| Un video girato (parlato in camera, vlog, intervista) | Un montaggio asciutto, senza tempi morti | Montaggio dei momenti migliori dal girato (`video-derush-edit`) |
| Un video girato | Lo stesso, deciso da un gruppo di montatori AI | Montaggio dei momenti migliori, deciso da un team di montaggio (`video-derush-edit-room`) |
| Un video girato | Il montaggio più titoli, sottopancia e callout | Montaggio del girato con titoli e callout (`video-derush-edit-graphics`) |
| Un video girato | Il montaggio più grafiche, pianificate da un gruppo di artisti AI | Montaggio del girato con titoli, progettato da un team di grafica (`video-derush-edit-graphics-room`) |
| Un video girato e file musicali che hai caricato | Il montaggio con un tappeto musicale di sottofondo | Montaggio del girato con musica di sottofondo (`video-derush-edit-music`) |
| Un video girato e brevi file audio che hai caricato | Il montaggio con whoosh, clic e stinger | Montaggio del girato con effetti sonori (`video-derush-edit-sfx`) |
| Un video girato | Il montaggio con un color grading, scelto da un gruppo | Montaggio del girato con color grading (`video-derush-edit-grade-room`) |
| Un video girato e un account a pagamento di generazione video | Il montaggio con b-roll generato dall'AI | Montaggio del girato con b-roll generato dall'AI (`video-derush-edit-broll`) |
| Un video girato e un account a pagamento di generazione video | Lo stesso, con due riprese per clip e un gruppo che sceglie la migliore | Montaggio del girato con b-roll AI, ripresa migliore scelta da un gruppo (`video-derush-edit-dailies`) |
| Un video girato e un provider vocale | Il montaggio con narrazione e sottotitoli scritti dopo il taglio | Montaggio del girato con narrazione e sottotitoli (`video-derush-edit-voiceover`) |
| Girato con un parlato in camera, un provider vocale e una voce clonata con consenso | Il montaggio con le frasi sbagliate registrate di nuovo con la voce del presentatore | Correggi le frasi sbagliate in un video con parlato in camera (`video-derush-edit-pickups`) |
| Girato con parlato e un provider vocale | Il montaggio doppiato in un'altra lingua, con sottotitoli | Video doppiato in un'altra lingua (`video-derush-edit-dub`) |
| Una clip girata con uno schermo di telefono o laptop verde, e una registrazione dello schermo (o qualsiasi video o immagine) da mostrarci sopra | La registrazione sullo schermo, che segue lo schermo mentre si muove | Inserto su green screen (`green-screen-insert`) |
| Un video finito con parlato | Lo stesso video con le parole pronunciate incise come sottotitoli, più file SRT e VTT | Sottotitoli per il tuo video (`video-subtitles`); per un'altra lingua, Sottotitoli in un'altra lingua (`video-subtitles-translated`). Vedi [la ricetta dei sottotitoli](../recipes/subtitles.md) |

**Non c'è** nessuna libreria integrata di musica o effetti sonori: i template per musica ed effetti sonori
scelgono solo tra i file audio che carichi prima nel progetto. Se non ne hai caricati, il risultato non ha
musica né effetti, e l'esecuzione lo segnala.

**Inserto su green screen** (`green-screen-insert`) è descritto per intero [più sotto](#green-screen-insert-green-screen-insert). Non viene aggiunto automaticamente: chiedilo all'assistente ("metti questa registrazione dello schermo sul green screen in quella clip") oppure segui la
[ricetta dell'inserto di schermo tracciato](../recipes/tracked-screen-insert.md).

Quattro template aggiungono la narrazione al girato (`narrated-story`, `video-derush-edit-voiceover`,
`video-derush-edit-pickups` e `video-derush-edit-dub`), e due template promo sono narrati
(`cinematic-feature-spotlight` e `story-arc-launch-trailer`). Gli altri due template promo sono
muti finché non inserisci un passaggio `Voiceover`; vedi la [ricetta della voce narrante](../recipes/voiceover.md).

## Video promo dal tuo codice (`quick-win-promo`) {#promo-video-from-your-code-quick-win-promo}

**Video promo dal tuo codice** è il template di partenza. È l'unico template aggiunto
automaticamente a ogni nuovo progetto. Quando lo esegui, ti chiede il brief (pubblico, messaggio,
durata, formato); lascialo vuoto per lasciar decidere all'AI. Il video è **senza audio**: l'unico suono che un
promo può avere è la narrazione, e questo template non ha un passaggio di narrazione. Le note del video finito
lo dicono; inserisci un passaggio `Voiceover` dopo il passaggio 8 per narrarlo (vedi la
[ricetta della voce narrante](../recipes/voiceover.md)).

**Cosa produce:** un video promozionale animato su una web app, costruito con Remotion (lo
strumento di animazione basato sul codice che ReelBolt usa per disegnare e generare i video promo). Il video finito
compare come risultato del passaggio 10.

**Cosa serve:** il codice sorgente della web app caricato come file del progetto e un provider AI di chat
funzionante.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze code structure | `Agent` | `CodeStructureAnalyzer` |
| 2 | Analyze UI dependencies | `Agent` | `DependencyAnalyzer` |
| 3 | Inventory components | `Agent` | `ComponentInventoryAnalyzer` |
| 4 | Map routes and API | `Agent` | `RouteAndApiAnalyzer` |
| 5 | Extract style tokens | `Agent` | `StyleAndThemeExtractor` |
| 6 | Translate to Remotion scenes | `Agent` | `RemotionComponentTranslator` |
| 7 | Create animation strategy | `Agent` | `AnimationStrategyAgent` |
| 8 | Draft narration and captions | `Agent` | `ScriptwriterAgent` |
| 9 | Build shot direction | `Agent` | `DirectorAgent` |
| 10 | Assemble and render master composition | `Agent` | `AuthorAgent` |
| 11 | Review quality and compliance | `ReviewLoop` | `ReviewAgent` |

Il passaggio 11 dà un punteggio al risultato su 10. Sotto 8 rimanda il lavoro al passaggio 10 per un altro tentativo, al
massimo 2 giri di revisione in totale.

**Sceglilo quando** vuoi un primo promo affidabile senza alcuna configurazione. Vedi la
[ricetta del promo dal codice](../recipes/promo-from-code.md).

## Video promo dal tuo codice, più leggero (`lean-context-promo`) {#promo-video-from-your-code-lighter-lean-context-promo}

**Video promo dal tuo codice (più leggero)** è una versione da attivare del promo di partenza che spende
meno token AI. Come quello di partenza, ti chiede il brief ed è muto a meno che tu non aggiunga la narrazione. Aggiunge
un passaggio non AI che riduce l'elenco dei componenti dell'app all'essenziale (al massimo 40
componenti, ciascuno ridotto a nome, file e scopo) prima che i passaggi successivi lo leggano, e dà a ogni
passaggio successivo solo i risultati precedenti che gli servono davvero.

**Cosa produce:** lo stesso tipo di promo animato di quello di partenza, generato dal passaggio 11.

**Cosa serve:** il codice sorgente della web app caricato come file del progetto.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze code structure | `Agent` | `CodeStructureAnalyzer` |
| 2 | Analyze UI dependencies | `Agent` | `DependencyAnalyzer` |
| 3 | Inventory components | `Agent` | `ComponentInventoryAnalyzer` |
| 4 | Map routes and API | `Agent` | `RouteAndApiAnalyzer` |
| 5 | Extract style tokens | `Agent` | `StyleAndThemeExtractor` |
| 6 | Reduce component inventory to a lean view | `Extract` | `ExtractTransform` (non è un'AI) |
| 7 | Translate to Remotion scenes (lean context) | `Agent` | `RemotionComponentTranslator` |
| 8 | Create animation strategy (lean context) | `Agent` | `AnimationStrategyAgent` |
| 9 | Draft narration and captions | `Agent` | `ScriptwriterAgent` |
| 10 | Build shot direction (lean context) | `Agent` | `DirectorAgent` |
| 11 | Assemble and render master composition (lean context) | `Agent` | `AuthorAgent` |
| 12 | Review quality and compliance | `ReviewLoop` | `ReviewAgent` |

Il passaggio 12 rimanda il lavoro al passaggio 11 quando il punteggio è sotto 8, al massimo 2 giri.

**Sceglilo quando** la tua app è grande e il promo di partenza è lento o costoso.

## Reel verticale rapido (`cinematic-feature-spotlight`) {#quick-vertical-reel-cinematic-feature-spotlight}

**Reel verticale rapido** è un breve spotlight su una funzione, incisivo e **narrato**, pensato per i social, con
transizioni di rivelazione e un finale con una forte call to action. Ti **chiede il brief** prima di ogni esecuzione
(per esempio "reel verticale di 15 s sulla nuova dashboard, chiudi con una call to action per registrarsi"), e
ogni passaggio AI lo vede. Se chiedi un reel, short, storia, TikTok o video verticale ottieni un video 1080x1920
(9:16); "quadrato" dà 1080x1080; altrimenti è orizzontale 1920x1080. Una durata indicata è la
durata del video.

**Cosa produce:** un promo animato narrato, generato dal passaggio 9. La narrazione è la voce narrante
del copione stesso, registrata dal passaggio 7 prima che il regista sincronizzi le inquadrature su di essa.

**Cosa serve:** il codice sorgente della web app caricato come file del progetto, un **provider vocale** configurato
da un amministratore (senza, l'esecuzione fallisce al passaggio 7) e un breve brief scritto quando la avvii.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze cinematic candidate surfaces | `Agent` | `CodeStructureAnalyzer` |
| 2 | Find hero components | `Agent` | `ComponentInventoryAnalyzer` |
| 3 | Extract brand visuals | `Agent` | `StyleAndThemeExtractor` |
| 4 | Translate hero scenes | `Agent` | `RemotionComponentTranslator` |
| 5 | Plan cinematic transitions | `Agent` | `AnimationStrategyAgent` |
| 6 | Write emotional feature hooks | `Agent` | `ScriptwriterAgent` |
| 7 | Record the narration (`"kind": "ScriptScenes"`) | `Voiceover` | `VideoTransform` (non è un'AI; chiama il provider vocale) |
| 8 | Define reveal sequence | `Agent` | `DirectorAgent` |
| 9 | Assemble cinematic master cut | `Agent` | `AuthorAgent` |
| 10 | Cinematic quality review | `ReviewLoop` | `ReviewAgent` |

Il passaggio 10 rimanda il lavoro al passaggio 9 quando il punteggio è sotto 9, al massimo 3 giri.

## Trailer di lancio narrato (`story-arc-launch-trailer`) {#narrated-launch-trailer-story-arc-launch-trailer}

**Trailer di lancio narrato** è un template promo costruito come un trailer narrativo: introduzione, tensione
crescente, una rivelazione risolutiva e una call to action finale di lancio, pronunciata sopra le immagini. Ti **chiede
il brief** prima di ogni esecuzione; formato e durata seguono il brief esattamente come nel reel
verticale rapido qui sopra.

**Cosa produce:** un trailer animato narrato, generato dal passaggio 9.

**Cosa serve:** il codice sorgente della web app caricato come file del progetto, un **provider vocale** configurato
da un amministratore e un breve brief scritto quando la avvii.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Map narrative assets | `Agent` | `CodeStructureAnalyzer` |
| 2 | Extract user journey flow | `Agent` | `RouteAndApiAnalyzer` |
| 3 | Extract mood and tone tokens | `Agent` | `StyleAndThemeExtractor` |
| 4 | Translate story scenes | `Agent` | `RemotionComponentTranslator` |
| 5 | Write narrative arc script | `Agent` | `ScriptwriterAgent` |
| 6 | Record the narration (`"kind": "ScriptScenes"`) | `Voiceover` | `VideoTransform` (non è un'AI; chiama il provider vocale) |
| 7 | Create trailer shotlist | `Agent` | `DirectorAgent` |
| 8 | Plan suspense pacing | `Agent` | `AnimationStrategyAgent` |
| 9 | Assemble and render trailer | `Agent` | `AuthorAgent` |
| 10 | Trailer impact review | `ReviewLoop` | `ReviewAgent` |

Il passaggio 10 rimanda il lavoro al passaggio 9 quando il punteggio è sotto 9, al massimo 3 giri.

## Cosa fa ogni template promo con i tuoi testi {#what-every-promo-template-does-with-your-copy}

I quattro template promo seguono tre regole comuni, perché il video si legga bene sullo schermo per cui è fatto:

- **Testo semplice sullo schermo.** Le parole sono disegnate esattamente come scritte, quindi l'AI non scrive mai markdown
  come `*word*`. Quello che finisce comunque nel copione viene rimosso automaticamente, e un render il cui
  testo a schermo dichiarato lo contiene ancora viene rifiutato prima di iniziare.
- **Leggibile a dimensione telefono.** Il testo del corpo è alto almeno circa 48 pixel su un fotogramma largo 1080, e
  un video verticale ingrandisce la parte della schermata della tua app che conta invece di rimpicciolire
  tutto il layout desktop.
- **Niente di inventato.** Nomi, citazioni, valutazioni, prezzi e numeri arrivano solo dal tuo codice, dai tuoi
  file o dal tuo brief. Una schermata che saluta un utente dice "Bentornato", mai un nome inventato.

## Montaggio dei momenti migliori dal girato (`video-derush-edit`) {#highlight-edit-from-footage-video-derush-edit}

**Montaggio dei momenti migliori dal girato** è il template base per montare il girato. "Derush" significa scorrere il girato
grezzo e tenere solo le parti buone. Trova pause, cambi di inquadratura e (facoltativamente) ciò che
viene detto, lascia che un montatore AI scelga quali parti tenere, e poi produce il file video montato.

**Cosa produce:** un MP4 montato (`edited.mp4`), prodotto dal passaggio 3. Il video montato viene aggiunto anche
ai file del progetto, così puoi montarlo di nuovo più tardi.

**Cosa serve:**

- Un file video caricato nel progetto, **selezionato come sorgente nel passaggio 1** (il template lascia
  il file vuoto di proposito; il passaggio mostra "nessun file selezionato" finché non ne scegli uno).
- Perché il montatore capisca il parlato, un **provider di trascrizione** configurato da un amministratore (il servizio
  locale `whisper` incluso funziona). Senza, l'analisi viene eseguita comunque, ma il montatore
  vede solo pause e cambi di inquadratura.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Compile edited video | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 4 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio di compilazione usa transizioni automatiche delicate tra i tagli (`"transitionPolicy": "Auto"`), una
breve dissolvenza in entrata e in uscita all'inizio e alla fine, ed elimina ogni parte tenuta più corta di 0,8 secondi.
I nuovi flussi di lavoro creati dai template di montaggio del girato, tranne quello dei giornalieri, offrono **punti di taglio dentro
le inquadrature lunghe** nel passaggio 1, dove l'immagine si ferma o cambia. Un'inquadratura lunga e singola (altrimenti una clip silenziosa senza
tagli è una sola inquadratura) può così essere divisa in parti, e il montatore può tenere solo la parte buona.
Il montatore può usare questi punti di taglio per rispettare una durata che hai chiesto. Il modulo del passaggio mostra l'impostazione **Punti di taglio dentro
le inquadrature lunghe** con tre scelte: **No**, **Uniformi** (a intervalli uguali) e **Dove l'immagine si ferma o cambia**. I flussi di lavoro che hai già creato mantengono le loro impostazioni.
Il passaggio 4 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri.

**Sceglilo quando** hai un video con parlato in camera, un'intervista o un vlog con tempi morti, false partenze o
ripetizioni. Vedi la [ricetta del montaggio derush](../recipes/derush-edit.md).

## Montaggio dei momenti migliori, deciso da un team di montaggio (`video-derush-edit-room`) {#highlight-edit-decided-by-an-editing-team-video-derush-edit-room}

**Montaggio dei momenti migliori, deciso da un team di montaggio** funziona come il montaggio dei momenti migliori, ma la decisione su cosa tenere è
presa da un piccolo **gruppo di montatori AI** invece che da uno solo. Tre "posti" da montatore (ritmo, storia e
mestiere) discutono il girato in una breve conversazione di gruppo, e poi un regista scrive una
decisione finale. Se il gruppo fallisce per qualsiasi motivo, decide invece un singolo montatore AI.

**Cosa produce:** un MP4 montato, prodotto dal passaggio 3.

**Cosa serve:** un file video selezionato come sorgente nel passaggio 1; è consigliato un provider di
trascrizione.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Edit room deliberation | `EditRoom` | gruppo: tre posti da montatore più `VideoEditDirector` |
| 3 | Compile edited video | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 4 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 4 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri. Il gruppo costa più
chiamate AI di un singolo montatore.

**Sceglilo quando** il montaggio richiede giudizio (una storia da raccontare, più riprese tra cui scegliere) e
puoi permetterti una decisione più lenta e più approfondita.

## Montaggio del girato con titoli e callout (`video-derush-edit-graphics`) {#footage-edit-with-titles-and-callouts-video-derush-edit-graphics}

**Montaggio del girato con titoli e callout** aggiunge grafiche a schermo (titoli, sottopancia, callout) al
montaggio base. Il passaggio di analisi trova anche punti buoni e liberi sullo schermo per le grafiche, e un'AI di
motion graphics sceglie dove metterle e le progetta. Per impostazione predefinita progetta ogni
grafica come una vera animazione che genera da sé con Remotion; il semplice testo su un riquadro è
la soluzione di ripiego.

**Cosa produce:** un MP4 montato con grafiche, prodotto dal passaggio 4.

**Cosa serve:** un file video selezionato come sorgente nel passaggio 1; è consigliato un provider di trascrizione
perché le grafiche corrispondano a ciò che viene detto.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video (`"emitOverlayPlacements": true`) | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan motion graphics overlays | `Agent` | `MotionGraphicsPlanner` |
| 4 | Compile edited video with graphics (`"enableGraphics": true`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 5 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri. Il passaggio 3 può richiedere
molto tempo perché scrive e genera piccole animazioni.

Questo template è anche il punto di partenza più semplice per un **inserto di schermo tracciato**: attiva il
rilevamento degli inserti nel passaggio 1 e gli inserti nel passaggio 4. Vedi la
[ricetta dell'inserto di schermo tracciato](../recipes/tracked-screen-insert.md) e la
[ricetta delle motion graphics](../recipes/motion-graphics-overlays.md).

## Montaggio del girato con titoli, progettato da un team di grafica (`video-derush-edit-graphics-room`) {#footage-edit-with-titles-designed-by-a-graphics-team-video-derush-edit-graphics-room}

**Montaggio del girato con titoli, progettato da un team di grafica** sostituisce la singola AI di motion graphics con un **gruppo di
artisti AI**: un artista dell'impaginazione, uno dei tempi, uno dei testi e uno degli inserti discutono le
opzioni, poi un direttore capo scrive un piano grafico finale e genera le grafiche. Se il
gruppo fallisce, pianifica invece una singola AI di motion graphics.

**Cosa produce:** un MP4 montato con grafiche, prodotto dal passaggio 4.

**Cosa serve:** un file video selezionato come sorgente nel passaggio 1.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video (`"emitOverlayPlacements": true`) | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Graphics room deliberation (reads step 1) | `GraphicsRoom` | gruppo: quattro posti da artista più `MotionGraphicsDirector` |
| 4 | Compile edited video with graphics (`"enableGraphics": true`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 5 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri.

## Montaggio del girato con musica di sottofondo (`video-derush-edit-music`) {#footage-edit-with-background-music-video-derush-edit-music}

**Montaggio del girato con musica di sottofondo** aggiunge un tappeto musicale di sottofondo al montaggio base. Non c'è
nessuna libreria musicale integrata: **carica prima una traccia musicale nei file del progetto**. Ogni file audio
che hai caricato nel progetto viene proposto come scelta musicale, un'AI supervisore musicale sceglie al massimo una traccia
e decide quanto forte deve essere e quanto deve abbassarsi sotto il parlato, e il passaggio di compilazione la mixa.

**Cosa produce:** un MP4 montato con musica, prodotto dal passaggio 4. Se non hai caricato file audio, il
video non ha musica, e il motivo indicato dal passaggio musicale lo dice con parole semplici.

**Cosa serve:** un file video selezionato come sorgente nel passaggio 1 e **uno o più file musicali**
(file audio) caricati nel progetto.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video (`"offerMusicTracks": true`) | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Pick background music | `Agent` | `MusicSupervisor` |
| 4 | Compile edited video with music (`"enableMusic": true`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 5 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri. Vedi la
[ricetta di musica, effetti sonori e color grading](../recipes/music-sfx-and-grade.md).

## Montaggio del girato con effetti sonori (`video-derush-edit-sfx`) {#footage-edit-with-sound-effects-video-derush-edit-sfx}

**Montaggio del girato con effetti sonori** aggiunge brevi effetti sonori (whoosh, clic, stinger) in momenti scelti
del montaggio. Non c'è nessuna libreria di effetti sonori integrata: **carica prima i tuoi effetti nei file del
progetto**. Ogni file audio del progetto viene proposto come possibile effetto, e un'AI sound designer decide quale effetto suona quando, e quanto forte. Anche scegliere nessun effetto è
un risultato valido.

**Cosa produce:** un MP4 montato con effetti sonori, prodotto dal passaggio 4.

**Cosa serve:** un file video selezionato come sorgente nel passaggio 1 e **brevi file di effetti sonori**
caricati nel progetto (dagli nomi chiari, per esempio `whoosh.wav`; l'AI sceglie in base al nome del file).

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video (`"offerSfxClips": true`) | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan sound-effect cues | `Agent` | `SoundDesigner` |
| 4 | Compile edited video with sound effects (`"enableSfx": true`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 5 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri.

## Montaggio del girato con color grading (`video-derush-edit-grade-room`) {#footage-edit-with-a-colour-grade-video-derush-edit-grade-room}

**Montaggio del girato con color grading** aggiunge un color grading (un "look" generale, come caldo, freddo,
filmico o tenue) al montaggio. Un gruppo di coloristi AI discute il colore misurato di ogni inquadratura,
poi un colorista supervisore sceglie un solo look per tutto il video, solo a parole (per esempio "Caldo,
Leggero"). ReelBolt trasforma quelle parole nella vera correzione del colore. "Nessuno" (nessun grading) è un
risultato valido.

**Cosa produce:** un MP4 montato e con color grading, prodotto dal passaggio 4.

**Cosa serve:** un file video selezionato come sorgente nel passaggio 1.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Color grade room deliberation (reads step 1) | `ColorGradeRoom` | gruppo: tre posti da colorista più `ColorGradeDirector` |
| 4 | Compile edited video with grade (`"enableColorGrade": true`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 5 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 5 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri.

## Montaggio del girato con b-roll generato dall'AI (`video-derush-edit-broll`) {#footage-edit-with-ai-generated-b-roll-video-derush-edit-broll}

**Montaggio del girato con b-roll generato dall'AI** aggiunge al montaggio **clip video generate dall'AI**: stacchi sul parlato,
un cold open all'inizio o una schermata finale. Un'AI direttore delle inquadrature pianifica le clip, un passaggio di generazione
paga un servizio di generazione video per crearle (con un tetto di spesa rigido), e il passaggio di compilazione
le inserisce.

**Cosa produce:** un MP4 montato con clip generate, prodotto dal passaggio 5.

**Cosa serve:**

- Un file video selezionato come sorgente nel passaggio 1.
- Un **provider di generazione video** (MiniMax) configurato da un amministratore. Questo **costa denaro vero**.
- Limiti di spesa: il passaggio di generazione di questo template consente al massimo 4 clip e al massimo 5 dollari
  USA per esecuzione (`"maxClips": 4`, `"maxSpendCredits": 5.0`), e gli amministratori impostano in più dei limiti giornalieri.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan generated b-roll shots | `Agent` | `ShotDirector` |
| 4 | Generate planned b-roll clips | `VideoGenerate` | `VideoTransform` (non è un'AI; chiama il provider a pagamento) |
| 5 | Compile edited video with generated clips (`"enableGeneratedClips": true`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 6 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 6 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri. Quando esegui o riprovi
questo flusso di lavoro, ReelBolt chiede se **riutilizzare le clip generate in precedenza** (gratis) o
**rigenerarle** (costa denaro). Vedi la [ricetta del b-roll generato](../recipes/generated-b-roll.md).

## Montaggio del girato con narrazione e sottotitoli (`video-derush-edit-voiceover`) {#footage-edit-with-narration-and-captions-video-derush-edit-voiceover}

**Montaggio del girato con narrazione e sottotitoli** aggiunge una **narrazione parlata** scritta da un'AI dopo il
taglio, con i sottotitoli incisi. Ti chiede il brief (argomento, tono, durata) quando lo esegui. Un
agente autore della narrazione scrive zero o più righe, ognuna legata a un'inquadratura, a una pausa o a una frase pronunciata del montaggio (mai a un
tempo), un passaggio `Voiceover` le trasforma in voce e calcola dove inizia ogni riga, e il passaggio di
compilazione le mixa sopra l'audio originale.

**Cosa produce:** un MP4 montato con narrazione e sottotitoli in stile karaoke, prodotto dal passaggio 5.

**Cosa serve:**

- Un file video selezionato come sorgente nel passaggio 1.
- Un **provider vocale** (Fish Audio, o un server vocale locale compatibile con OpenAI) configurato da un
  amministratore, e un id voce inserito come `defaultVoiceId` nel passaggio 4 (oppure una voce della scheda
  **Voci** del progetto come `projectVoiceId`).
- Un **provider di trascrizione** per i sottotitoli: i sottotitoli karaoke seguono ogni parola pronunciata, e
  senza i tempi delle parole una riga non ottiene alcun sottotitolo (il passaggio di compilazione segnala `no_word_alignment`).
  Passa il passaggio 5 a `"captionStyle": "Subtitle"` se non hai un provider di trascrizione.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Write narration lines | `Agent` | `NarrationWriter` |
| 4 | Synthesize narration (`"kind": "NarrationPlan"`) | `Voiceover` | `VideoTransform` (non è un'AI; chiama il provider vocale) |
| 5 | Compile edited video with narration (`"enableVoiceover": true`, `"enableCaptions": true`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 6 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 6 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri. Una riga che non
sta nel suo momento viene segnalata, e la revisione chiede all'autore della narrazione parole più brevi; nulla
rimonta l'immagine sulla voce. I sottotitoli sono attivi (`"captionStyle": "Karaoke"`); imposta
`"enableCaptions": false` nel passaggio 5 per disattivarli. Poiché il montatore taglia prima e l'autore
segue, la narrazione descrive le clip tenute; per una storia scritta prima, usa **Storia di prodotto
narrata** qui sotto. Vedi la [ricetta della voce narrante](../recipes/voiceover.md).

## Correggi le frasi sbagliate in un video con parlato in camera (`video-derush-edit-pickups`) {#fix-flubbed-lines-in-a-talking-head-video-video-derush-edit-pickups}

**Questo template richiede un girato con parlato in camera e un audio chiaro.** **Correggi le frasi sbagliate in un video con
parlato in camera** registra di nuovo le frasi che il presentatore ha **sbagliato**, con la
voce clonata del presentatore stesso. Un agente pianificatore dei pickup indica le frasi pronunciate da sostituire e la
frase corretta per ciascuna, un passaggio `Voiceover` le pronuncia, e il passaggio di compilazione silenzia la
ripresa originale esattamente per quella frase e vi riproduce il pickup. L'immagine non viene toccata, quindi la
bocca è ancora quella della ripresa originale: **la sincronizzazione labiale non è disponibile**, e il risultato lo dice.

**Cosa produce:** un MP4 montato con pickup, prodotto dal passaggio 5.

**Cosa serve:**

- Un file video selezionato come sorgente nel passaggio 1 e un **provider di trascrizione**, perché
  i pickup sostituiscono frasi pronunciate (`t0`, `t1`, ...) e senza trascrizione non ce ne sono.
- Una **voce clonata con consenso registrato** nella scheda **Voci** del progetto (richiede un provider
  Fish Audio). Inseriscila come `projectVoiceId` nel passaggio 4; una voce il cui consenso è mancante, revocato o
  scaduto **fa fallire il passaggio** invece di usare un'altra voce.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan pickup lines | `Agent` | `PickupPlanner` |
| 4 | Record pickups (`"kind": "PickupPlan"`) | `Voiceover` | `VideoTransform` (non è un'AI; chiama il provider vocale) |
| 5 | Compile edited video with pickups (`"enablePickups": true`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 6 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 6 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri.

## Video doppiato in un'altra lingua (`video-derush-edit-dub`) {#dubbed-video-in-another-language-video-derush-edit-dub}

**Questo template richiede un girato con parlato.** **Video doppiato in un'altra lingua** crea una versione del montaggio in **un'altra lingua**. L'autore della
narrazione scrive le righe, un agente traduttore riscrive le stesse righe nella lingua che indichi quando
esegui il flusso di lavoro, un passaggio `Voiceover` pronuncia la traduzione, e il passaggio di compilazione **sostituisce**
il parlato originale sotto ogni riga e incide **sottotitoli** delle parole effettivamente pronunciate. L'immagine
non viene toccata: **la sincronizzazione labiale non è disponibile**, e il risultato lo dice.

**Cosa produce:** un MP4 doppiato e sottotitolato, prodotto dal passaggio 6.

**Cosa serve:**

- Un file video selezionato come sorgente nel passaggio 1 e un **provider vocale**.
- **Testo quando lo esegui:** la lingua di destinazione (per esempio "spagnolo"), nella casella della richiesta.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Write narration lines | `Agent` | `NarrationWriter` |
| 4 | Translate narration | `Agent` | `NarrationTranslator` |
| 5 | Synthesize dub (`"kind": "NarrationPlan"`, pointing at step 4) | `Voiceover` | `VideoTransform` (non è un'AI; chiama il provider vocale) |
| 6 | Compile dubbed video (`"voiceoverMode": "ReplaceDialogue"`, `"enableCaptions": true`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 7 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 7 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri.

## Storia di prodotto narrata (`narrated-story`) {#narrated-product-story-narrated-story}

**Storia di prodotto narrata** scrive prima la **storia** e poi taglia le tue clip su di essa. Un autore
della narrazione legge il tuo brief e ciò che l'analisi ha visto in ogni clip, e scrive un breve copione con
un inizio, uno svolgimento e una fine, legando ogni riga alla clip su cui deve essere riprodotta. Il montatore
tiene poi quelle clip **nell'ordine del copione**, la narrazione viene registrata e il passaggio di compilazione la
mixa con i sottotitoli karaoke. Ti chiede il brief quando lo esegui: di cosa parla il video, a chi
è rivolto, il tono e quanto deve durare.

**Cosa produce:** un MP4 narrato e con sottotitoli, prodotto dal passaggio 5.

**Cosa serve:**

- Le tue clip video (registrazioni dello schermo, riprese del prodotto, b-roll), caricate e **selezionate come
  sorgenti nel passaggio 1**. Le clip senza audio proprio vanno bene.
- Un **provider vocale** configurato da un amministratore e un **provider di trascrizione** per i sottotitoli
  karaoke (vedi il template di narrazione qui sopra).
- Facoltativo: un **provider di visione**, così l'analisi può descrivere cosa mostra ogni clip e
  l'autore può scegliere la clip giusta per ogni riga. Senza, l'esecuzione funziona comunque.
- Il formato di uscita: il passaggio 5 mantiene per impostazione predefinita il formato delle tue clip. Scegli verticale, quadrato od
  orizzontale lì, prima di eseguirlo.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze your clips (`"vision": "Optional"`) | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Write the story | `Agent` | `NarrationWriter` |
| 3 | Choose and order clips for the story | `Agent` | `VideoStoryEditor` |
| 4 | Record the narration (`"kind": "NarrationPlan"`, pointing at step 2) | `Voiceover` | `VideoTransform` (non è un'AI; chiama il provider vocale) |
| 5 | Compile the narrated video (`"enableVoiceover": true`, `"enableCaptions": true`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 6 | Review the finished video | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 6 rimanda il lavoro al **passaggio 2** quando il punteggio è sotto 8, al massimo 3 giri, così un risultato debole
ottiene una storia riscritta oltre a un nuovo montaggio. Le clip vengono riprodotte nell'ordine del copione tra clip
diverse; due momenti della **stessa** clip vengono sempre riprodotti nell'ordine proprio di quella clip.

## Montaggio del girato con b-roll AI, ripresa migliore scelta da un gruppo (`video-derush-edit-dailies`) {#footage-edit-with-ai-b-roll-best-take-chosen-by-a-panel-video-derush-edit-dailies}

**Montaggio del girato con b-roll AI, ripresa migliore scelta da un gruppo** è il template b-roll con un secondo parere: il passaggio di
generazione crea **due riprese di ogni clip pianificata**, un secondo passaggio di analisi misura ogni ripresa
esattamente come misura il tuo girato, e un gruppo di giudici AI (una **stanza dailies**) sceglie la
ripresa migliore di ogni inquadratura. Vengono inserite solo le riprese scelte.

**Cosa produce:** un MP4 montato con le clip generate scelte, prodotto dal passaggio 7.

**Cosa serve:** lo stesso del template b-roll. Costa circa il doppio per esecuzione, dato che
ogni clip viene creata due volte (`"takes": 2`, `"maxClips": 4`, `"maxSpendCredits": 8.0`). Il passaggio di generazione
ha inoltre attivi **l'abbinamento del look** e la **continuità della camera**, quindi a ogni clip viene chiesto di abbinarsi al
colore e alla resa di camera dell'inquadratura reale accanto a cui si trova.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Analyze source video | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Decide which spans to keep | `Agent` | `VideoStoryEditor` |
| 3 | Plan generated b-roll shots | `Agent` | `ShotDirector` |
| 4 | Generate two takes per planned shot | `VideoGenerate` | `VideoTransform` (chiama il provider a pagamento) |
| 5 | Measure every generated take (`"source": {"kind": "GeneratedClips", "stepOrder": 4}`) | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 6 | Dailies room: pick one take per shot | `DailiesRoom` | posti `TakeSelector` + `DailiesDirector` |
| 7 | Compile edited video with the chosen takes (`"takeSelection": {"from": "Step", "stepOrder": 6}`) | `VideoCompile` | `VideoTransform` (non è un'AI) |
| 8 | Review edit quality | `ReviewLoop` | `VideoReviewAgent` |

Il passaggio 8 rimanda il lavoro al passaggio 2 quando il punteggio è sotto 8, al massimo 3 giri. Vedi la
[ricetta del b-roll generato](../recipes/generated-b-roll.md).

## Video promo dal tuo codice con sfondi AI (`quick-win-promo-plates`) {#promo-video-from-your-code-with-ai-backgrounds-quick-win-promo-plates}

**Video promo dal tuo codice con sfondi AI** è **Video promo dal tuo codice** con un passaggio in più prima
dell'Autore: un passaggio `VideoGenerate` che crea uno o due brevi **sfondi astratti**, a partire da una
descrizione fissa e integrata (sfumature morbide e fluttuanti, senza testo, senza persone). L'Autore li
porta nel proprio spazio di lavoro e può usarli come sfondi a pieno schermo dietro le sue scene animate.

**Cosa produce:** un MP4 promo animato, prodotto dal passaggio 11.

**Cosa serve:** lo stesso di Video promo dal tuo codice, più un provider di generazione video. **Costa
denaro** (al massimo 3 dollari USA per esecuzione, `"maxSpendCredits": 3.0`), quindi non viene aggiunto automaticamente.
I passaggi da 1 a 9 sono quelli di Video promo dal tuo codice; il passaggio 10 è quello di generazione; il passaggio 11 è l'Autore; il passaggio 12
rivede e torna al passaggio 11.

## Inserto su green screen (`green-screen-insert`) {#green-screen-insert-green-screen-insert}

**Cosa fa:** mostra una registrazione dello schermo, un video o un'immagine sul green screen di un telefono, monitor o
laptop nella tua clip. Il contenuto segue lo schermo mentre si muove e ruota, e resta dietro una mano o un
gatto che gli passa davanti. La clip viene mantenuta intera: non si taglia nulla.

**Quanto costa:** solo tempo di elaborazione. Non c'è nessun passaggio AI, quindi non viene usato alcun modello, e l'esecuzione conta nel
limite orario più ampio per le esecuzioni che non chiamano alcun modello. Non viene aggiunto automaticamente.

**Cosa serve:** la clip con il green screen e il contenuto da mostrarci sopra, entrambi caricati nel
progetto. Chiedi all'assistente di configurarlo, oppure apri il flusso di lavoro e scegli tu i file.

| # | Etichetta del passaggio | Tipo di passaggio | Agente |
|---|---|---|---|
| 1 | Find the green screen | `VideoAnalyze` | `VideoTransform` (non è un'AI) |
| 2 | Put the content on the screen | `VideoCompile` | `VideoTransform` (non è un'AI) |

**Cosa devi impostare:** il file del passaggio 1 è la clip con il green screen. Il contenuto da inserire del passaggio 2 è la
registrazione dello schermo, il video o l'immagine. Finché non vengono scelti, l'esecuzione fallisce subito; l'assistente compila
entrambi per te, e la configurazione di ogni passaggio indica il campo che sta aspettando (`placeholders`:
`source.projectFileId` nel passaggio 1, `insertContentRef` nel passaggio 2), così una copia fatta dal selettore di
template dice cosa manca ancora. Il risultato è un nuovo video sotto **Risultati**, come qualsiasi film compilato; non viene aggiunto ai file del progetto.

## Cosa hanno in comune tutti i template di girato {#what-every-footage-template-has-in-common}

Tutti i tredici template di girato (`video-derush-edit`, le sue undici varianti e `narrated-story`) condividono
questi aspetti:

- **Il passaggio 1 deve puntare al tuo video.** È impostato su un file del progetto senza alcun file scelto, quindi un'esecuzione
  fallisce subito con `SOURCE_UNRESOLVED` finché non apri il passaggio 1 e selezioni il video caricato.
- **I passaggi successivi puntano a quelli precedenti tramite il numero.** Per esempio il passaggio di compilazione nel template
  musicale legge la decisione di montaggio dal passaggio 2 e la scelta musicale dal passaggio 3. Se inserisci
  o rimuovi passaggi, controlla questi numeri (vedi [Tipi di passaggio](step-types.md)).
- **L'AI non scrive mai tempi o posizioni.** Il passaggio di analisi dà a ogni inquadratura, pausa, frase
  pronunciata, punto grafico e area di green screen un breve id (come `s3`, `g1`, `t7`, `p2`, `r0`),
  e gli agenti AI scelgono solo tra quegli id. È ReelBolt stesso a calcolare i fotogrammi esatti. Per questo
  l'AI non può collocare qualcosa in un momento che non esiste.
- **Il passaggio di revisione controlla il risultato** e rimanda il lavoro al passaggio 2 (la decisione di montaggio,
  o la storia in `narrated-story`) quando il punteggio è troppo basso, fino a 3 giri; dopo di che l'esecuzione
  termina con il miglior risultato ottenuto.
- **Chiedono il brief prima di essere eseguiti.** Scrivi le istruzioni per il montatore AI (per esempio
  "tienilo sotto i 60 secondi"), oppure lascia la casella vuota. Per rendere il brief facoltativo, apri il
  flusso di lavoro, disattiva **Richiedi un brief prima di ogni esecuzione** e salva.
