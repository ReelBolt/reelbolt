---
title: Profilo di brand e prodotto
sidebar_position: 10
description: Cos'è il profilo di brand e prodotto di ReelBolt, come si costruisce a partire dai tuoi file di design, come si approvano le modifiche e quali parti usano crediti.
i18n_source_sha: 295680d6bdef035fa6fa801c54465e9e24023867
---

# Profilo di brand e prodotto {#brand-and-product-profile}

Un profilo di brand e prodotto è la descrizione salvata da ReelBolt del tuo prodotto: il suo aspetto e la sua voce, e il modo in cui è fatto. ReelBolt usa il profilo ogni volta che genera un video per il progetto, così il risultato segue i tuoi colori, i tuoi font e il tuo tono reali, e descrive il tuo prodotto reale. Questa pagina spiega cosa contengono i due profili, come costruirli e importarli, come si approvano le modifiche, come vedere quale versione ha usato un video e cosa è gratis.

## Cos'è un profilo di brand e prodotto {#what-a-brand-and-product-profile-is}

Un **profilo di brand** raccoglie ciò che fa sembrare il tuo prodotto se stesso: i colori, i font, la spaziatura che usi tra gli elementi e la voce con cui scrivi. Un **profilo di prodotto** raccoglie il modo in cui è costruito il tuo prodotto: quale framework usa, quali pagine, schermate e route ha, di quali componenti è fatto e da quali librerie dipende.

ReelBolt conserva i due profili per ogni progetto. Insieme sostituiscono le supposizioni con un riepilogo che il tuo team può leggere e correggere, e ogni video generato può essere confrontato con essi.

## Costruire un profilo di brand e prodotto {#building-a-brand-and-product-profile}

Apri il progetto e vai alla scheda **Profilo**, poi scegli **Crea profilo di brand e prodotto**. ReelBolt legge prima i tuoi file di design e ricava da solo tutto ciò che può, senza alcun costo. Solo le parti che non riesce a ricavare vengono completate da un modello AI, come ripiego. Quando la costruzione termina, scegli **Salva come bozze** per conservare il risultato e poterlo rivedere; nulla è approvato finché non lo approvi tu. Puoi rivedere il risultato, modificarlo e approvarlo prima che un video lo usi.

Prima di confermare, ReelBolt mostra il costo stimato come intervallo, per esempio "tra 12 e 80 crediti". Se la stima supera il limite per una singola costruzione, ReelBolt te lo dice e indica la via gratuita: importare un file di design token, un file `DESIGN.md`, un file CSS con i tuoi colori e font, o uno zip esportato dal tuo strumento di design. Alcuni modelli video sono scritti per un progetto che ha già un profilo approvato; restano disattivati nell'elenco dei modelli finché il progetto non ne ha uno.

## Libreria del brand condivisa {#shared-brand-library}

Una **libreria del brand condivisa** è un insieme di valori di brand tenuti per l'intero spazio di lavoro. Un progetto può ereditarla, così i progetti che lo fanno lavorano da un unico brand. Un progetto che eredita parte dai valori condivisi e può aggiungerne di propri, o modificare quelli ereditati, solo per quel progetto.

Un valore bloccato nella libreria del brand condivisa prevale su qualsiasi cosa imposti un progetto, quindi un progetto non può cambiarlo. I Proprietari e gli Amministratori dello spazio di lavoro possono modificare la libreria del brand condivisa. I Membri possono vederla ma non modificarla, e i membri Fatturazione non possono aprirla.

Aggiornare la libreria del brand condivisa non cambia un video già eseguito. Ogni progetto approva comunque il proprio profilo, e ogni esecuzione registra le versioni che ha usato.

## Importare i tuoi file di design {#importing-your-design-files}

Puoi portare i tuoi file di design esistenti in un progetto invece di digitare tutto a mano. ReelBolt accetta i formati che il tuo team già conserva:

- **Token di design** — il formato JSON DTCG, compresi i file scritti per Style Dictionary, e le esportazioni di Tokens Studio.
- **CSS** — proprietà personalizzate e blocchi di tema scritti per Tailwind v4.
- **`DESIGN.md`** — un file Markdown con un piccolo blocco di front matter (tra due righe `---`) che elenca i tuoi colori o la tipografia. Solo quel blocco diventa colori e font; i codici esadecimali o i nomi dei font scritti nel testo o in una tabella non vengono letti, e i titoli del testo diventano soltanto il tono del brand. Un file senza il blocco ti dà una bozza senza colori né font, e ReelBolt ti avvisa.
- **Cataloghi di componenti** — file di registro shadcn e un indice Storybook.
- **Variabili di Figma** — quando il tuo spazio di lavoro ha attivato l'importazione da Figma, puoi puntare ReelBolt a un file e lui legge le variabili una volta.
- **Un PDF della guida di brand** — ReelBolt legge il testo della guida ed estrae i colori e la tipografia che descrive.

Puoi anche caricare un archivio zip con questi file. Archivi e PDF vengono estratti e letti da un assistente separato e isolato che non tocca mai direttamente i tuoi progetti, e ReelBolt pone limiti rigorosi a quanto possono essere grandi e annidati.

Puoi anche portare uno zip salvato da uno strumento di design — una libreria condivisa o un progetto iniziale, per esempio — e ReelBolt individua i colori, i font e i token che riconosce.

Un'esportazione di Claude Design viene letta per intero: ReelBolt legge le sue modalità chiara e scura, il suo insieme completo di colori e stili di testo (compresi i valori z-index e le trasformazioni del testo), i suoi marchi SVG, il suo logo e le icone dell'app, i suoi font, l'elenco dei componenti e le card di anteprima. Un marchio SVG viene reso sicuro prima di essere salvato — script, pagine incorporate e link ad altri file vengono rimossi, e il disegno resta — e le immagini vengono aggiunte al progetto come file. Le card di anteprima compaiono nel profilo di prodotto come elenco delle card contenute nell'esportazione, con i loro nomi, gruppi ed etichette scritte; ReelBolt non mostra né esegue mai le card in sé. L'elenco dei componenti e le card diventano una bozza di profilo di prodotto che puoi rivedere prima di approvarla. Il readme, le linee guida e le note del kit UI vengono conservati come documenti di riferimento che puoi leggere e cercare, e i font usati dal brand vengono registrati. Se un file di font è troppo grande, o i file di font sono più di quanti ne accetti un'importazione, quel file viene saltato con una nota e il resto dell'importazione procede. Script, screenshot, il bundle compilato e le pagine web dentro l'archivio non vengono comunque mai eseguiti né importati. Quando un foglio di stile nomina un font che l'esportazione non include, ReelBolt registra il nome e annota che la licenza è sconosciuta, invece di scaricarlo.

Per v0, Lovable, Penpot e Sketch, ReelBolt ricava dove lo strumento tiene colori e font deducendo la struttura delle sue esportazioni. Queste strutture sono dedotte e non sono state verificate su un'esportazione reale di quegli strumenti. Inoltre gli strumenti di design cambiano nel tempo il modo in cui esportano, quindi ReelBolt **potrebbe non riconoscere ogni esportazione**. Quando incontra qualcosa che non sa leggere, salta quella parte e importa il resto invece di fallire. Un file di configurazione Tailwind dentro uno zip non viene letto; aggiungilo invece al progetto come file. Dopo l'importazione di uno zip, ReelBolt elenca i file che ha saltato e perché (script e pagine web non vengono mai eseguiti, file troppo grandi da leggere e file con un nome illeggibile o non sicuro), con alcuni nomi di esempio. Mostra anche un breve rapporto di ciò che l'importazione ha portato per ogni parte del design — colori e valori, chiaro e scuro, immagini e loghi, font, componenti, card di anteprima, testi scritti, indicazioni di voce, valori calcolati e documenti di riferimento — e di ciò che ha lasciato fuori, e perché. I nomi di file con accenti e lettere non latine (per esempio `café.css` o `日本語.json`) vanno bene e vengono letti come tutti gli altri; vengono saltati solo i nomi con caratteri invisibili o di controllo della direzione del testo, e due nomi uguali una volta normalizzati gli accenti (una `é` precomposta e una scomposta, o nomi che differiscono solo per maiuscole e minuscole) contano come un solo file, quindi il secondo viene saltato.

I file importati sono trattati solo come dati di riferimento: ReelBolt ne legge il contenuto, non li esegue e non scarica nulla da internet per loro conto.

## Loghi nei tuoi video {#logos-in-your-videos}

Quando importi file di design che includono loghi, ReelBolt guarda ogni immagine per decidere se è il logo del tuo prodotto o quello della tua azienda, e se è chiaro o scuro. Non si affida al solo nome del file. Un video usa il logo del prodotto che risalta sul suo sfondo, e ReelBolt controlla che il logo sia leggibile prima di completare il video.

Il logo dell'azienda compare solo quando lo chiedi nel brief, per esempio "di La Tua Azienda". Viene aggiunto come piccola riga in più e non sostituisce mai il logo del prodotto.

Se viene scelto il logo sbagliato, rinomina il file in modo che il nome includa il nome del prodotto (e `company` per il logo dell'azienda), oppure carica nel progetto il file che vuoi e importa di nuovo. Il riconoscimento funziona per file PNG e SVG fino a 2 MB.

## Esportare il tuo profilo {#exporting-your-profile}

Puoi scaricare un profilo di brand approvato per usarlo fuori da ReelBolt. Scegli **Esporta** e seleziona il formato che serve al tuo team:

- **JSON DTCG**: design token nel formato DTCG, per gli strumenti che leggono design token. Mantengono i valori dei tuoi token; se importi di nuovo quel file, le note, i blocchi, le modalità, la voce e le voci dei font che contiene non vengono riletti.
- **Variabili CSS**: un file CSS di proprietà personalizzate.
- **Tema Tailwind v4**: un blocco di tema per Tailwind v4.
- **DESIGN.md**: un documento Markdown che descrive il brand.
- **JSON**: l'intero profilo approvato così come lo conserva ReelBolt.

Un formato conserva solo ciò che sa esprimere, quindi un'esportazione potrebbe non contenere ogni parte del tuo profilo. Un file CSS non porta ombre né definizioni complete della tipografia, e un documento `DESIGN.md` conserva le parti scritte di un brand piuttosto che ogni dettaglio tecnico. Un profilo di prodotto si esporta solo in JSON. Esportare è gratis.

## Restare aggiornati {#staying-up-to-date}

Una volta costruito o importato un profilo, ReelBolt continua a tenere d'occhio i file da cui proviene. Quando quei file cambiano — perché qualcuno ha caricato una nuova versione, o una cartella o un repository collegati si sono sincronizzati — ReelBolt li rilegge e valuta se qualcosa è davvero cambiato. Controlla anche a intervalli regolari, così una modifica viene notata anche quando nessuna sincronizzazione l'ha segnalata.

Quando trova una modifica, ReelBolt prepara un aggiornamento da rivedere invece di applicarlo. Nulla del tuo profilo cambia finché una persona non lo approva. Se hai già rifiutato la stessa modifica di recente, ReelBolt resta in silenzio invece di chiedertelo di nuovo. I valori che hai bloccato non vengono mai sovrascritti da un aggiornamento: la modifica compare nel confronto, contrassegnata **Bloccato, lasciato com'è**, e il tuo valore bloccato prevale. I blocchi si impostano su una bozza; una versione approvata non si può modificare, quindi per bloccare un valore parti da una bozza.

## Approvare le modifiche {#approving-changes}

Ogni modifica a un profilo viene salvata come **bozza**. Una bozza non è mai usata da un video da sola: una persona la rivede e la approva prima. Prima di approvare una bozza, ReelBolt mostra un breve riepilogo di ciò che cambia rispetto alla versione attuale (quanti valori sono stati aggiunti, modificati o rimossi) e ti chiede di confermare, perché approvare sostituisce la versione che ogni nuovo video usa. Un'importazione che non trova alcun colore o font, o che manca di valori presenti nella versione attuale, lo dice con parole semplici nel suo risultato e ti chiede di confermare anche quando non c'è una versione attuale con cui confrontare, così un'importazione vuota non può sostituire in silenzio un buon design. Un aggiornamento proposto mostra cosa è cambiato e resta in attesa finché non lo approvi o lo rifiuti — una proposta a cui nessuno risponde per abbastanza tempo semplicemente scade. Quando approvi una bozza diventa la versione che ReelBolt usa, e la versione sostituita viene conservata così puoi vedere cosa è cambiato e quando.

## Chiedere all'assistente {#asking-the-assistant}

L'assistente della piattaforma può rispondere a domande su un profilo e preparare una modifica per te: chiedigli quali sono i colori del tuo brand, o chiedigli di aggiornare un valore, e ti mostrerà la modifica proposta. Può leggere il profilo attuale e avanzare una proposta, ma non può approvare da solo una modifica al tuo profilo — serve sempre una persona. Approvare richiede il tuo clic sulla card di approvazione in ReelBolt o nella pagina del profilo, anche quando uno strumento esterno è collegato al tuo account; rifiutare una modifica si può fare tramite l'assistente.

## Vedere quale versione ha usato un video {#seeing-which-version-a-video-used}

Ogni esecuzione registra le versioni del profilo con cui è stata costruita, e la pagina dell'esecuzione le mostra come **"Usati brand v7 / prodotto v3"**. Se in seguito modifichi o ricostruisci un profilo, i video più vecchi mostrano comunque le versioni che hanno usato, così puoi sempre capire quale descrizione del tuo prodotto ha seguito un certo video. Se un video era legato a un design senza colori né font, o una bozza più recente era ancora in attesa di revisione quando è partito, la pagina dell'esecuzione lo segnala in alto, e il video è stato comunque eseguito; approva la bozza ed esegui di nuovo per usarla.

## Cosa è gratis e cosa usa crediti {#what-is-free-and-what-uses-credits}

Leggere e importare i tuoi file di design, tenere d'occhio le modifiche, confrontarle, costruire le parti che ReelBolt ricava da solo e approvare un profilo sono **gratis**. Il ripiego AI che colma le lacune usa **crediti**: prima che l'esecuzione parta, ReelBolt ti mostra l'importo stimato, e lo confermi prima che vengano spesi crediti. Un progetto i cui file di design lo descrivono già bene può non richiedere alcun ripiego, e in quel caso costruire il profilo non costa nulla. Il ripiego colma solo la parte mancante: se i tuoi file di design coprono l'aspetto ma non il prodotto, solo la parte di prodotto usa crediti. Se la stima supera il limite per la costruzione di un profilo, la costruzione non parte e ReelBolt ti mostra invece la stima.
