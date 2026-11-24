---
title: Usare ReelBolt da Claude Code
sidebar_position: 2
description: Collegare lo strumento da riga di comando Claude Code a ReelBolt perché possa lavorare con i tuoi progetti, flussi di lavoro e immagini - accesso, cosa può fare, l'impostazione facoltativa degli strumenti da amministratore, quanto costa e come scollegarlo.
i18n_source_sha: ad6db9749455a76fc16a465be1fc243e55f2f567
---

# Usare ReelBolt da Claude Code {#using-reelbolt-from-claude-code}

Claude Code è un assistente AI da riga di comando. Puoi collegarlo a ReelBolt così che, invece di
passare alla dashboard, puoi chiedergli dal terminale di guardare i tuoi progetti, ispezionare un
flusso di lavoro, controllare come è andata un'esecuzione o modificare un flusso di lavoro al posto
tuo. Raggiunge lo stesso meccanismo assistente e gli stessi permessi dell'assistente ReelBolt che
vive nella dashboard: è un altro modo di raggiungerli, non una seconda copia.

Questa pagina è per chi usa già Claude Code e ha un account ReelBolt. Non serve sapere com'è
costruito ReelBolt per seguirla.

## Cosa può fare Claude Code una volta collegato {#what-claude-code-can-do-once-it-is-connected}

Una volta collegato, Claude Code riceve un insieme di strumenti ReelBolt. In generale può:

- **Consultare le cose.** Elencare i tuoi progetti, leggere i file di un progetto e l'elenco degli
  agenti, elencare i flussi di lavoro di un progetto e leggere i passaggi di un flusso di lavoro.
- **Controllare le esecuzioni.** Elencare le esecuzioni recenti e aprirne una per vedere lo stato di
  ogni passaggio, quanto ha impiegato, quanti crediti ha usato e il motivo completo per cui un
  passaggio è fallito. Claude Code può anche chiedere in qualsiasi momento lo **stato** di
  un'esecuzione: risponde subito con a che punto è arrivata e, mentre l'esecuzione è in corso,
  quanto aspettare prima di chiedere di nuovo. Se un'esecuzione che hai chiesto in 4K ha potuto
  essere prodotta solo a risoluzione inferiore, il risultato dell'esecuzione lo dice con una frase
  semplice.
- **Controllare i tuoi crediti.** Chiedere quanti crediti hai, quanti sono trattenuti dalle
  esecuzioni in corso e quando si rinnova il tuo piano.
- **Preparare e modificare flussi di lavoro.** Verificare che un insieme di passaggi sia valido e poi
  salvare la modifica in una sola chiamata dopo il tuo consenso.
- **Compilare i campi di un'app.** Dove il tuo spazio di lavoro ha campi per le app, un'app può
  chiedere alcune cose nella sua pagina prima di ogni esecuzione, come "Cosa succede nel video",
  una durata, un formato, una modalità Lite più economica o il modello AI da usare. Claude Code legge
  quei campi (con i modelli tra cui puoi scegliere, il consigliato per primo e il prezzo di ciascuno
  in parole) e li compila quando avvia un'esecuzione, invece di cambiare i passaggi del flusso di
  lavoro. Può anche preparare i campi di un flusso di lavoro che hai creato tu, così il tuo team deve
  solo compilarli; la modifica la approvi prima.
- **Aggiungere file a un progetto.** Caricare dal tuo computer un file audio, video o immagine (fino
  allo spazio di archiviazione consentito dal tuo piano; i file grandi vengono inviati a parti), o
  importare un file da un link web pubblico (`https://`, fino a 512 MB). Claude Code ti mostra cosa
  sta per aggiungere e tu lo approvi.
- **Ottenere un link a un risultato.** Per qualsiasi video o file finale di un progetto, Claude Code
  può darti dimensione, durata e formato, un link di breve durata per scaricarlo senza accedere di
  nuovo (il link funziona per circa 15 minuti) e alcune griglie di fotogrammi del video, così può
  descrivere come appare. Se i fotogrammi non sono ancora pronti, ti dice di chiedere di nuovo tra
  un minuto.
- **Conservare un risultato come file di progetto.** Copiare un video finale nei file del progetto
  perché le esecuzioni successive possano usarlo, per esempio come musica o come clip da inserire.
  L'originale resta dov'è e la copia conta nel tuo spazio di archiviazione.
- **Mettere una registrazione dello schermo su un green screen.** Di' "metti questa registrazione
  dello schermo sul green screen in quella clip". Claude Code controlla la clip e la registrazione e,
  dopo il tuo consenso, salva il flusso di lavoro che userebbe (o aggiorna quello con quel nome). Non usa alcun modello AI (viene addebitato solo il tempo di
  elaborazione) e non avvia l'esecuzione: chiedi poi a Claude Code di eseguire il flusso di lavoro.
- **Modificare un video finale.** Per un video animato realizzato da ReelBolt, chiedi una modifica
  ("ingrandisci il titolo nell'ultima scena") e Claude Code ti mostra le scene che può cambiare e il
  costo stimato prima del tuo consenso. Una modifica avvia una nuova esecuzione e conta come
  esecuzione a pagamento per il limite orario qui sotto.
- **Rifare una parte di un'esecuzione finita.** Chiedi una battuta diversa della voce narrante, una
  nuova take di una clip, musica nuova o una nuova miniatura, e Claude Code ti mostra il prezzo e cosa
  viene rifatto prima del tuo consenso. Per cambiare più parti della stessa esecuzione insieme — per
  esempio la voce narrante e le grafiche — chiedile tutte insieme: approvi una sola scheda che elenca
  ogni modifica con un solo preventivo totale, e viene creata una sola nuova esecuzione.
- **Scegliere una voce narrante.** Elencare le voci di un progetto, cercare nella libreria di voci del
  tuo servizio vocale (Fish Audio) per nome e lingua, e aggiungere una voce della libreria al progetto
  dopo il tuo consenso. Aggiungere una voce della libreria è gratuito. Un'esecuzione può poi usarla
  come voce narrante ("eseguilo con la voce Aria"). Claude Code non può creare una voce dalle
  registrazioni di una persona; serve il consenso di quella persona, che dai nella scheda **Voci**
  del progetto.
- **Avviare un'esecuzione.** Far partire un flusso di lavoro, con lo stesso limite orario
  dell'assistente della dashboard (vedi [Limiti di esecuzione](#run-limits)).
  Puoi chiedere uno stile di montaggio, un formato video e una risoluzione per l'esecuzione
  ("eseguilo come video verticale in 4K"); lo strumento `ExecuteWorkflow` li riceve come `styleId` o
  `stylePresetKey`, `outputFormat` e `outputResolution` (`Auto`, `720p`, `1080p` o `2160p` per il 4K).
- **Lavorare con l'immagine aperta nell'editor di immagini.** Leggere di cosa è fatta un'immagine,
  guardare cosa mostra davvero e fare modifiche come aggiungere testo, spostare o eliminare
  livelli, cambiare il canvas o usare uno strumento a pagamento per le immagini.

Il tuo client Claude Code ti chiede conferma prima di chiamare uno strumento che scrive o spende
denaro: non viene scritto nulla finché non acconsenti. Avviare un'esecuzione, eliminare qualcosa e qualsiasi
cosa che costi denaro tornano sempre prima da te.

## Seguire un'esecuzione {#watching-a-run}

Un'esecuzione può durare più di un singolo turno di conversazione. Ci sono tre modi per seguirla.

- **Chiedere lo stato.** Claude Code chiede lo stato dell'esecuzione e ottiene subito una risposta.
  Funziona in ogni client.
- **Farti avvisare dal tuo client.** Ogni esecuzione è disponibile come risorsa,
  `reelbolt://runs/<run id>`. Un client che supporta le iscrizioni alle risorse può iscriversi e
  viene avvisato quando l'esecuzione cambia, con il numero di passaggi completati come avanzamento.
  Non tutti i client lo supportano; se il tuo non lo fa, usa lo stato o il comando di controllo. Ogni
  persona può avere fino a 10 iscrizioni (5 per sessione), e un'iscrizione termina dopo due ore.
- **Usare il controllo esecuzioni del plugin.** Il plugin ReelBolt include un piccolo comando che
  stampa una breve riga ogni volta che l'esecuzione cambia e si ferma quando finisce. Claude Code lo
  avvia come **Monitor**, così ogni riga ti arriva come notifica mentre tu continui a lavorare. Serve
  [Node.js](https://nodejs.org) 22 o successivo sul tuo computer.

  La prima volta accedi una volta sola. Claude Code ti indica il comando da eseguire (nel tuo
  terminale, o in Claude Code digitando `!` davanti). Si apre il browser e ti chiede di approvare
  l'accesso in sola lettura allo stato delle esecuzioni del tuo spazio di lavoro. Non c'è nulla da
  copiare o incollare. L'accesso può solo leggere lo stato delle esecuzioni, è memorizzato in un file
  che solo tu puoi leggere e puoi rimuoverlo in qualsiasi momento con il comando `logout` del
  plugin. Se Node.js manca o preferisci non accedere, Claude Code chiede lo stato.

  Se gestisci ReelBolt da solo e usi il server stdio separato, il suo comando `reelbolt-mcp-watch`
  svolge lo stesso compito con il tuo token. Chiedi la configurazione al tuo amministratore.

Le notifiche nell'assistente della dashboard di ReelBolt funzionano in modo diverso: vedi
[Aspettare la fine di un'esecuzione](./index.md#waiting-for-a-run-to-finish).

## Limiti di esecuzione {#run-limits}

Per proteggere i tuoi crediti, c'è un limite al numero di esecuzioni che Claude Code e l'assistente
della dashboard possono avviare per te. Viene conteggiato per ogni persona in ogni progetto su
un'ora mobile:

- **Esecuzioni che possono usare crediti** (tutto ciò che usa modelli AI o servizi a pagamento, e
  ogni modifica a un video finale): fino a **10 all'ora**.
- **Esecuzioni gratuite** (solo passaggi di montaggio fissi, senza modelli AI né servizi a
  pagamento): fino a **60 all'ora**. Le esecuzioni gratuite non consumano mai le tue 10.

Quando raggiungi un limite, Claude Code ti dice quante ne hai usate, qual è il limite e quanto
aspettare prima di riprovare. Avviare un'esecuzione tu stesso con **Avvia** nella dashboard non viene
conteggiato.

Se salvi la modifica di un flusso di lavoro che fa usare servizi a pagamento a un'esecuzione gratuita
subito dopo che l'hai richiesta, l'esecuzione si ferma prima di fare qualsiasi cosa e non viene
addebitata. Claude Code dice "This workflow changed after the run started; start it again." Chiedigli
di avviare di nuovo l'esecuzione.

## Collegarsi {#connecting}

Ti colleghi aggiungendo a Claude Code l'indirizzo MCP di ReelBolt. L'indirizzo è il tuo indirizzo web
di ReelBolt seguito da `/mcp`, per esempio `https://reelbolt.example.com/mcp`, oppure
`http://localhost/mcp` in un'installazione locale.

ReelBolt include un **plugin per Claude Code** che lo fa al posto tuo: registra l'endpoint di
ReelBolt e ti chiede quell'unico indirizzo e nient'altro, quindi non c'è alcun token da creare,
incollare o conservare. Aggiungi il suo marketplace pubblico e installalo:

```bash
claude plugin marketplace add ReelBolt/reelbolt-claude-plugin
claude plugin install reelbolt@reelbolt --config server_url=https://reelbolt.ai/mcp
```

Se usi un'installazione self-hosted, usa il tuo indirizzo al posto di `https://reelbolt.ai/mcp` e
approva nel browser come descritto sotto. Puoi anche aggiungere il server da solo; i passaggi sono
gli stessi. Il README del plugin contiene la guida completa all'installazione, compreso come
cambiare l'indirizzo in seguito e come caricarlo da una copia del codice sorgente di ReelBolt.

Impostato l'indirizzo, Claude Code poi:

1. Si registra automaticamente presso ReelBolt.
2. Apre il browser sulla pagina di accesso di ReelBolt se non hai già effettuato l'accesso.
3. Ti mostra una schermata di consenso che indica cosa viene richiesto e da chi.
4. Una volta che approvi, dà a Claude Code una credenziale che conserva e usa da quel momento.

Approvi una volta sola. Se in seguito la connessione viene revocata, ti ricolleghi allo stesso modo.

La schermata di consenso chiede l'**uso degli strumenti dell'assistente di ReelBolt**. Se per il tuo
account sono stati attivati gli strumenti da amministratore (vedi sotto), lo dice anche, perché è un
permesso più ampio. Negare la richiesta ti riporta semplicemente a Claude Code senza nulla di
collegato.

## Gli strumenti da amministratore via MCP sono disattivati per impostazione predefinita {#admin-tools-over-mcp-are-off-by-default}

L'assistente di ReelBolt ha alcuni strumenti che solo gli amministratori possono usare, quelli che
gestiscono le impostazioni dei provider AI dell'installazione. Questi strumenti **non** sono
disponibili tramite Claude Code a meno che non siano stati attivati per il tuo account:

- Quando sono disattivati (il valore predefinito), ReelBolt non dice nemmeno a Claude Code che
  esistono. Non possono essere richiamati per errore o con una richiesta formulata ad arte.
- Quando sono attivi, compaiono, e controllano comunque i tuoi permessi a ogni singola chiamata.
  L'impostazione da sola non concede nulla: se non sei un amministratore, la chiamata viene rifiutata.

Attivarli è un'impostazione del tuo account ReelBolt, e non è qualcosa che Claude Code può attivare
da solo. Al momento non esiste un interruttore nel portale, quindi chiedi a chi amministra la tua
installazione ReelBolt di attivarli per te; poi ricollega Claude Code.

## Quanto costa {#what-it-costs}

ReelBolt registra **ogni** chiamata di Claude Code: cosa è stato richiesto, se è stato permesso
eseguirla e quanto ha impiegato. Quella registrazione esiste perché l'utilizzo sia visibile e perché
si possa notare un'attività insolita. Una registrazione non è un addebito.

**I crediti vengono consumati solo dove il lavoro stesso costa denaro a ReelBolt**, esattamente come
quando usi la dashboard:

- Guardarsi intorno (progetti, file, flussi di lavoro, cronologia delle esecuzioni, saldo crediti,
  link e fotogrammi di un risultato, livelli di un'immagine) è gratuito.
- Aggiungere un file o conservare un risultato come file non consuma crediti, ma il file conta nel
  tuo spazio di archiviazione.
- **Avviare un'esecuzione** consuma crediti man mano che i passaggi dell'esecuzione usano servizi a
  pagamento.
- **Modificare un video finale** consuma crediti, e Claude Code mostra prima la stima.
- Gli **strumenti a pagamento per le immagini** (cancellare, riempire, espandere, cambiare
  illuminazione, aumentare la risoluzione, generare) e le **grafiche progettate** consumano crediti
  quando li applichi. A Claude Code viene comunicato il costo stimato quando ne fa uno, così può
  dirtelo prima del tuo consenso.
- Le **clip video generate dall'AI** sono la voce più costosa. Claude Code rifiuta di avviare
  un'esecuzione che le genererebbe finché non hai scelto se riutilizzare le clip dell'ultima volta
  (gratis) o generarne di nuove (a pagamento).

Vedi [Piani e crediti](../account/plans-and-credits.md) per come funzionano i crediti in generale.

## Scollegare {#disconnecting}

Rimuovere il server ReelBolt da Claude Code gli impedisce di usare il tuo account.

Una credenziale ReelBolt già rilasciata non può essere ritirata all'istante: è un token firmato che
resta valido fino alla scadenza, entro l'ora. Ciò che si può fermare è il suo **rinnovo**. Revocare
la connessione termina la concessione che continua a emettere nuove credenziali, quindi la
connessione decade a breve invece di essere interrotta in questo preciso istante. L'endpoint OAuth di
ReelBolt esiste per questo; al momento non esiste un controllo nel portale, quindi chiedi a chi
amministra la tua installazione ReelBolt.

Dopo, ricollegarsi significa approvare una nuova connessione da zero.

## Gestire il server in proprio (avanzato) {#running-the-server-yourself-advanced}

Invece di collegarti a un ReelBolt ospitato, puoi eseguire in locale il server MCP di ReelBolt e usare
il tuo token di accesso. **Il plugin non lo configura**: registra l'endpoint ospitato descritto sopra.
Eseguire il server in locale è una configurazione separata, manuale, pensata per le installazioni
self-hosted e per gli utenti avanzati; è documentata nel README del repository del plugin. In breve,
il server locale ha bisogno di due cose da te: l'indirizzo di ReelBolt da chiamare e un token ReelBolt
per il tuo account. Non ha mai bisogno, e non dovrebbe mai ricevere, una chiave di firma della
piattaforma.

## Se non funziona {#if-it-does-not-work}

- **Claude Code dice che deve accedere.** Esegui di nuovo il collegamento; il passaggio nel browser
  deve essere completato da una persona. Nulla si collega in silenzio.
- **Claude Code non vede gli strumenti di ReelBolt.** Controlla che l'indirizzo del server termini con
  `/mcp` e che la sessione abbia effettuato l'accesso. Poi ricollega il server.
- **Le chiamate vengono rifiutate con un messaggio sui permessi.** Lo strumento esiste ma il tuo
  account potrebbe non poterlo usare, oppure gli strumenti da amministratore non sono attivi per te.
  Rifiuti di questo tipo sono risposte, non arresti anomali: dicono cosa fare dopo.
- **Un'esecuzione viene rifiutata per troppe esecuzioni.** L'assistente della dashboard e Claude Code
  condividono un limite orario sul numero di esecuzioni che una persona può avviare in un progetto
  (vedi [Limiti di esecuzione](#run-limits)). Il messaggio dice quante ne hai usate e quanto
  aspettare. Aspetta quel tempo, oppure avvia tu l'esecuzione con **Avvia** nella dashboard.
- **Claude Code dice che la sessione non è stata trovata.** Dopo il riavvio di ReelBolt, una
  connessione che era aperta prima del riavvio non è più riconosciuta. Claude Code si ricollega da
  solo; se non lo fa, ricollega il server.
- **Un link a un risultato ha smesso di funzionare.** I link durano circa 15 minuti. Chiedi a Claude
  Code un nuovo link. Un link smette di funzionare anche se non sei più membro dello spazio di
  lavoro.
- **L'importazione di un file è stata rifiutata.** Funzionano solo i link pubblici `https://`, il file
  deve pesare al massimo 512 MB e deve stare nel tuo spazio di archiviazione. I link a indirizzi
  privati o interni vengono rifiutati.
- **Un'esecuzione viene rifiutata a causa della risoluzione.** Il tuo piano stabilisce la risoluzione
  massima che un'esecuzione può usare (720p in prova, 1080p con Creator, 4K con Pro e Team). Chiedi
  quella risoluzione o una inferiore, oppure lasciala su Auto; vedi
  [Risoluzione di esportazione in ogni piano ReelBolt](../account/plans-and-credits.md#export-resolution-on-each-reelbolt-plan).
