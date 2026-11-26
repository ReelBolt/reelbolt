---
title: Runner desktop
sidebar_position: 4
description: Il runner desktop — collegare un computer tuo per generare i video di ReelBolt, le piattaforme supportate, approvare e revocare un dispositivo, la preferenza di calcolo dello spazio di lavoro, installazione e configurazione, impostazioni, cosa gira dove e quali dati lasciano la macchina, e risoluzione dei problemi.
i18n_source_sha: 6040721f47d11dd8be937710bbe78ac50f9c8979
---

# Runner desktop {#desktop-runner}

Un **runner desktop** è un computer tuo — il portatile, una workstation, una macchina di scorta — che genera i tuoi video ReelBolt al posto del cloud. Lo collegi a uno spazio di lavoro approvando un breve codice che mostra, e da quel momento il lavoro che può girare su di esso gira lì. Generare sulla tua macchina non consuma **nessun credito ReelBolt**: il lavoro lo fa la macchina, quindi per quella parte non paghi nulla a ReelBolt. Questa pagina riguarda il collegamento, l'approvazione e la revoca di un dispositivo, le piattaforme supportate, l'impostazione dello spazio di lavoro che decide quando il lavoro sui media va a una macchina collegata, l'installazione e l'avvio del runner, le sue impostazioni e quali dati un runner invia, e dove.

## Installare e avviare un runner desktop ReelBolt {#installing-and-running-a-reelbolt-desktop-runner}

Quando viene pubblicata una release, scarichi l'installer da [reelbolt.ai/runner](https://reelbolt.ai/runner) e segui le pagine di [Runner ReelBolt](../runner/index.md) ([Windows](../runner/install-windows.md), [Linux](../runner/install-linux.md), [collegamento](../runner/pairing.md)). Finché non c'è una release, o se preferisci, puoi compilarlo dai sorgenti di ReelBolt sulla macchina che lo eseguirà (servono la toolchain Go e una copia dei sorgenti), poi collegarlo e avviarlo:

```sh
# Build the headless daemon (or the tray shell, which embeds it).
cd runner && go build -o reelbolt-runner ./cmd/reelbolt-runner
./reelbolt-runner pair --name "My workstation"
./reelbolt-runner run
```

`pair` avvia il flusso di collegamento descritto più avanti e salva il risultato; `run` poi serve i lavori di quello spazio di lavoro finché non lo fermi. Un runner che compili tu non installa mai aggiornamenti da solo; per aggiornarlo, compilalo di nuovo da sorgenti più recenti. `doctor` è il comando da eseguire per primo: controlla ffmpeg, il container runtime, l'immagine runtime fissata e la postura di egress del container, e stampa l'esatto documento delle capacità su cui il gateway instrada il lavoro. `status` mostra la configurazione locale e lo stato del dispositivo, e indica il motivo per cui la macchina non lavora se il governor l'ha messa in pausa.

**Dove vive il runner sul disco.** La configurazione è un file di testo che puoi modificare, `runner.json`, nella cartella di configurazione utente del tuo sistema operativo (per esempio `~/.config/ReelBolt/runner.json` su Linux, `%AppData%\ReelBolt\runner.json` su Windows), con permessi 0600. La chiave privata del dispositivo si trova nel portachiavi del sistema operativo (oppure in un file 0600 accanto alla configurazione, con un avviso, dove il portachiavi non è raggiungibile). Lo spazio temporaneo dei lavori va nella cartella cache utente del sistema operativo e viene eliminato quando il lavoro termina. Nient'altro di tuo viene letto.

**La scelta del container runtime.** Una macchina che genera render Remotion ha bisogno di un container runtime: **Docker (24 o successivo) o Podman (5 o successivo)**. Quale usare su un portatile:

- **Podman** — nessun daemon, nessuna GUI. L'isolamento lo fa il motore della macchina stessa; sono supportate sia la modalità rootless sia quella con VM. Per la maggior parte delle macchine personali è la scelta più pulita.
- **Docker Desktop** — una GUI completa e una VM sotto il cofano. Su Windows è l'unica scelta; su Linux è questione di gusti. Attenzione alla licenza: Docker Desktop è gratuito per uso personale e per organizzazioni fino a 250 dipendenti; un'organizzazione più grande ha bisogno di un abbonamento a Docker Desktop. Podman non ha questa licenza.
- **WSL2** (Windows): esegui il runner e Podman dentro la distribuzione Linux; la rete senza egress del container è il confine di isolamento, e la rete di WSL2 ne è fuori.
- **Nessun runtime** — il runner funziona comunque: offre solo il lavoro ffmpeg (compilazione video ed estrazione per l'analisi), come processi dell'host, e non può accettare render Remotion.

La rete sandbox del runtime è una rete `--internal` (senza egress) che il runner crea e verifica all'avvio: una sonda dal vivo, dall'interno di un container verso l'internet pubblico, deve fallire. Se non può essere creata, la macchina ripiega sui tipi di lavoro ffmpeg e ne spiega il motivo, invece di non avviarsi.

**Eseguirlo in modo non presidiato.** Il runner si svuota alla ricezione di SIGTERM (attende `drainSeconds`, di default 5, poi si chiude), quindi un gestore di servizi è il normale supervisore: un'unità systemd **utente** su Linux (così il portachiavi è raggiungibile) o un servizio di Windows. Eseguilo sotto uno di questi se vuoi che resti attivo senza presidio.

## Usare l'app desktop di ReelBolt {#using-the-reelbolt-desktop-app}

L'app desktop ha quattro schermate, nel menu sulla sinistra.

- **Home** dice, in una riga, cosa sta facendo il tuo computer in questo momento: pronto, al lavoro su un job (con quale progetto e a che punto), in pausa, o cosa c'è da sistemare. **Pausa** gli impedisce di accettare nuovi job finché non premi **Riprendi**; un job già in corso finisce. Finché il computer non è configurato del tutto, Home mostra anche i passaggi di configurazione: **Collegati al tuo spazio di lavoro** (approvi un codice nel browser), **Configura il rendering video** (Docker o Podman, poi un download una tantum del toolkit video, con l'avanzamento) e una domanda una tantum sui report di arresto anomalo.
- **Attività** elenca i job che questo computer ha eseguito da quando l'app è stata avviata, dal più recente, con come è finito ciascuno su questo computer: **Completato**, **Fallito** (con il motivo), **Troppo lungo**, **Annullato** o **Interrotto**. "Completato" significa che il computer ha finito la sua parte; se l'intero video è venuto bene lo mostra la pagina dell'esecuzione in ReelBolt, che si apre con la freccia accanto a un job.
- **Cartelle** mantiene copiate nei progetti alcune cartelle del tuo computer. Vedi la sezione successiva.
- **Impostazioni** contiene tutto ciò che puoi cambiare, qui sotto.

## Impostazioni dell'app desktop {#desktop-app-settings}

Ogni gruppo si salva da solo, e ogni modifica viene controllata prima del salvataggio, così un errore non può lasciare l'app configurata a metà.

- **Questo computer**: il suo nome, lo spazio di lavoro per cui lavora e chi lo ha collegato. **Rimuovi** lo toglie dallo spazio di lavoro: smette di accettare job, scompare dall'elenco dei computer dello spazio di lavoro e le sue cartelle smettono di sincronizzarsi. I file già presenti in ReelBolt restano. Attiva "Elimina anche l'identità salvata di questo computer" quando dai il computer a qualcun altro.
- **Quando lavorare**: **In qualsiasi momento**, **Quando non ci sono** (dopo che il computer è rimasto inattivo per un numero di minuti a tua scelta), **Quando è collegato alla corrente** oppure **In orari stabiliti** (giorni e orari; un orario di fine precedente a quello di inizio vale per la notte). Puoi anche aggiungere orari stabiliti sopra alle altre scelte, e **Pausa a batteria**. La pausa da Home vince sempre sulla pianificazione.
- **Quanto usare**: quanti job girano contemporaneamente, la memoria e i core del processore che ogni job può usare, la sua **priorità** (Più bassa mantiene reattive le tue app mentre un job gira) e lo spazio su disco che ogni job può riempire. I valori superiori a quelli del computer vengono ridotti a quelli che ha.
- **Archiviazione**: i filmati scaricati da un job restano sul computer, così il job successivo che usa lo stesso file parte senza scaricarlo di nuovo. **Conserva fino a** imposta il limite (quando viene raggiunto vanno via prima i file più vecchi) e **Svuota** lo azzera. **Pulisci** rimuove i file lasciati da job terminati.
- **Avvio e aggiornamenti**: apri l'app in silenzio nell'area di notifica quando accedi, installa gli aggiornamenti automaticamente (solo quando nessun job è in corso) e **Ricevi le versioni anticipate** per provare prima le nuove funzioni.
- **Privacy**: i report di arresto anomalo sono disattivati finché non li attivi. **Vedi cosa viene inviato** mostra esattamente cosa contiene un report e cosa non contiene mai (file, cartelle, nomi, indirizzi email, progetti). **File di diagnostica** salva un file che puoi allegare quando contatti il supporto; non viene mai caricato al posto tuo.

### Per gli amministratori: il file delle impostazioni {#for-administrators-the-settings-file}

L'app scrive le sue impostazioni in `runner.json` nella tua cartella di configurazione utente (per esempio `~/.config/ReelBolt/runner.json` su Linux, `%AppData%\ReelBolt\runner.json` su Windows). Modificarlo a mano è possibile ma non serve mai; il comando headless `reelbolt-runner status` lo stampa.

| Impostazione | Predefinito | Cosa fa |
| --- | --- | --- |
| `slots` | 1 | Job contemporanei, fino a 16. |
| `limits` | 4 GB di memoria, 2 core, priorità `below-normal`, 8 GB di disco per job | I limiti per job mostrati sotto **Quanto usare**. |
| `schedule` | in qualsiasi momento, anche a batteria | `mode` (`always`, `idle`, `ac`, `windows`), `idleMinutes`, `windows` (`{days, start, end}`), `onBattery` (`allow` o `pause`). |
| `mediaCache.maxBytes` | 10 GB | Il limite **Conserva fino a** per i filmati scaricati; 0 disattiva la cache. |
| `updateChannel` | `stable` | `stable` o `beta` (**Ricevi le versioni anticipate**). |
| `autoUpdate` | attivo | Installa gli aggiornamenti automaticamente. |

**Mettere in pausa dalla riga di comando.** `reelbolt-runner pause` e `resume` fanno lo stesso dei pulsanti dell'app. Una pausa non viene mai conservata dopo un riavvio: riavviando si torna sempre al lavoro.

**Non puoi mettere in pausa dalla dashboard.** Nulla nella dashboard invia una pausa a una macchina collegata. Per fermare una macchina, rimuovila (vedi "Revocare una macchina collegata") oppure mettila in pausa sulla macchina stessa.

## Copiare una cartella del tuo computer in un progetto {#copying-a-folder-from-your-computer-into-a-project}

L'app desktop può anche mantenere una cartella del tuo computer copiata in un progetto ReelBolt — a senso unico, dal tuo
computer a ReelBolt. Apri la schermata **Cartelle** dell'app, scegli **Aggiungi una cartella**, seleziona una cartella e il
progetto in cui copiarla, e da quel momento ReelBolt mantiene il progetto uguale alla cartella. È così che
porti **iCloud Drive** in ReelBolt: Apple non permette a un sito web di leggerlo, ma iCloud per Windows lo mostra
come una normale cartella, e ReelBolt copia da essa come da qualsiasi altra. OneDrive, Dropbox, una condivisione NAS e una cartella semplice
funzionano allo stesso modo. La guida completa è [Copiare una cartella del tuo computer in un
progetto](../projects/local-folder-sync.md).

Tre cose da sapere:

- **Solo Windows e Linux.** Non esiste una versione Mac dell'app desktop, quindi questa funzione
  non è disponibile su Mac.
- **Si sincronizza finché l'app è in esecuzione**, non solo mentre è in corso un render. Tieni l'app aperta e le modifiche arrivano
  al progetto in pochi secondi; ciò che è cambiato mentre era chiusa viene copiato al successivo avvio.
- **Ciò che lascia la tua macchina sono solo i file della cartella che hai scelto.** Nulla al di fuori di quella cartella
  viene letto, nulla viene mai riscritto sul tuo computer o sul tuo spazio cloud, e i file che hai caricato tu
  (o scritto da un agente) non vengono mai toccati.

**Per fermarla**, apri il menu **⋯** della cartella e scegli **Interrompi la sincronizzazione**: i file già copiati restano nel
progetto e non viene eliminato nulla. **Metti in pausa la sincronizzazione** nello stesso menu smette di copiare per un po' senza scollegare
la cartella. Chiudere l'app interrompe
del tutto la sincronizzazione delle cartelle finché non la riapri.

## Cosa gira dove e quali dati lasciano la tua macchina {#what-runs-where-and-what-data-leaves-your-machine}

La suddivisione è la stessa in ogni sezione di questa pagina, quindi la indichiamo una volta sola e ci contiamo:

**Gira sulla tua macchina** — il lavoro sui media: render Remotion, compilazione dei video (ffmpeg) ed estrazione per l'analisi video. Una macchina senza container runtime esegue solo la metà ffmpeg, come processi dell'host.

**Resta sulla piattaforma, mai sul runner** — le chiamate ai modelli e tutto il ragionamento AI, la trascrizione (ASR), la generazione della descrizione di ciò che appare a schermo (visione), il tracciamento degli oggetti, il pre-passaggio di derush, il controllo degli input e la fatturazione. Servono le credenziali dei provider, e nessun messaggio che il runner riceve ne contiene una.

**Cosa lascia la macchina, in totale:**

- Il WebSocket in uscita verso il gateway di ReelBolt (il runner non è mai in ascolto su una porta), che porta il lavoro — la specifica del job e le operazioni — e riporta indietro i risultati.
- I file di cui un job ha bisogno: le sorgenti del progetto e i WAV della voce narrante vengono **forniti a te da URL prefirmati dell'object store** (una GET), e il risultato generato viene **pubblicato su un URL prefirmato** (una PUT) nello spazio di chiavi proprio del job. I dati binari non viaggiano mai sul socket.
- Il nome della macchina, il sistema operativo, l'architettura e la versione del runner, dichiarati al collegamento e nel documento delle capacità. Non c'è geolocalizzazione né ricerca dell'indirizzo IP. La schermata di approvazione mostra l'indirizzo grezzo da cui è arrivata la richiesta di collegamento e nient'altro su di esso.
- I report di arresto anomalo, **solo se li attivi** in **Impostazioni → Privacy**. Un report indica il tipo di errore e dove si è verificato nel codice dell'app, e mai un file, una cartella, un nome, un indirizzo o un progetto; **Vedi cosa viene inviato** mostra il report esatto prima che tu decida.

**Cosa viene controllato prima dell'uso.** L'output di un runner è **non attendibile** per costruzione — il binario è controllato dal suo proprietario. Ogni artefatto prodotto da un runner viene verificato dalla piattaforma (dimensione, durata, dimensione dei fotogrammi, codec, frame rate, checksum) prima di essere promosso a file del progetto; un output rifiutato fa fallire il passaggio. Un passaggio eseguito sulla tua macchina è contrassegnato nei dettagli del passaggio dell'esecuzione, e la pagina dell'esecuzione mostra un chip quando almeno uno dei suoi passaggi lo è stato. La piattaforma ricontrolla anche lo stato di revoca del dispositivo, così una macchina revocata smette di ricevere lavoro anche prima della connessione successiva.

**Cosa la piattaforma non vede mai:** i tuoi altri file, il tuo portachiavi, qualsiasi cosa fuori dalla cartella temporanea del job (eliminata alla fine del job), e la rete del container è una rete `--internal` senza egress, quindi un job non può contattare l'esterno.

## Risoluzione dei problemi di una macchina collegata {#troubleshooting-a-paired-machine}

Il catalogo completo dei messaggi di errore è nella pagina [Risoluzione dei problemi](../troubleshooting.md); le voci del runner sono lì. Le quattro situazioni che non richiedono una pagina:

- **La macchina non accetta lavoro, e `status` dice perché.** Leggi la riga: `user-active` (sei alla tastiera — in modalità `idle`), `on-battery`, `outside-window` o `paused`. `resume` cancella una pausa esplicita senza modificare un file; un blocco dovuto alla pianificazione indica quando cambierà, e la stessa riga di `status` dice che `resume` non ha effetto su di esso. Se non risponde proprio nulla, termina il processo: una pausa non viene mai conservata, quindi un riavvio torna sempre al lavoro.
- **`NO_RUNNER_ONLINE` nell'errore di un passaggio.** Il tuo spazio di lavoro è impostato su **Solo le mie macchine** e nessuna macchina collegata era online e in grado di accettare il passaggio. Quella scelta non ripiega mai sul cloud; avvia (o lascia accesa) la macchina, assicurati che sia collegata a questo spazio di lavoro in **Runner**, e riesegui il passaggio. Vedi la pagina [Risoluzione dei problemi](../troubleshooting.md) per la voce completa.
- **La macchina non mostra nulla per i render nel documento delle capacità.** `doctor` lo indica: nessun container runtime (Docker o Podman), il runtime è più vecchio del minimo (Docker 24, Podman 5) oppure non è stato possibile creare la rete `--internal` senza egress. Una macchina solo ffmpeg non può accettare render Remotion; sistema il runtime ed esegui di nuovo `doctor`.
- **Hai revocato la macchina e la rivuoi.** Revocare la rimuove dall'elenco; per usarla di nuovo, collegala da capo dalla schermata **Home** dell'app (oppure con `reelbolt-runner pair`). La chiave privata del dispositivo non cambia; è stato eliminato solo il record che lo spazio di lavoro ne aveva.
- **L'app dice "Configura il rendering video".** Installa Docker Desktop o Podman Desktop, aprilo una volta, poi premi **Controlla di nuovo**; l'app ti propone allora di scaricare il suo toolkit video e mostra l'avanzamento del download.

## Piattaforme supportate per un runner desktop ReelBolt {#supported-platforms-for-a-reelbolt-desktop-runner}

Un runner è disponibile per **Windows (x64)** e **Linux (x64 o arm64)**. **macOS non è supportato.** Un dispositivo su qualsiasi altra piattaforma viene mostrato con i valori grezzi che ha dichiarato, senza essere ricondotto a un'etichetta che sembri supportata.

Una macchina ottiene il runner dall'installer su [reelbolt.ai/runner](https://reelbolt.ai/runner) quando viene pubblicata una release (vedi [Runner ReelBolt](../runner/index.md)), oppure compilandolo dai sorgenti. Una macchina che lo ha mostra un codice di collegamento, e un Proprietario o Amministratore dello spazio di lavoro lo approva. Come compilare e avviare il binario, la scelta del container runtime (la licenza Docker Desktop da 250 dipendenti, Podman, WSL2) e l'esecuzione non presidiata sono in [Installare e avviare un runner desktop ReelBolt](#installing-and-running-a-reelbolt-desktop-runner) più sopra in questa pagina.

## Cosa può fare un runner e cosa no {#what-a-runner-can-do-and-what-it-cannot}

Una macchina collegata esegue il **lavoro sui media** di un'esecuzione: render Remotion, compilazione dei video ed estrazione per l'analisi video. Tutto il resto — le chiamate ai modelli, il ragionamento AI, la trascrizione, i sottotitoli, la fatturazione — resta sulla piattaforma. Una macchina senza container runtime (Docker o Podman) può fare solo il lavoro ffmpeg (compilare ed estrarre); un render Remotion richiede una macchina che abbia dichiarato un container runtime. Poiché ciò che una macchina dichiara di saper fare viene preso per buono e poi verificato, la piattaforma controlla l'output prima di usarlo.

## Collegare una macchina in ReelBolt {#pairing-a-machine-in-reelbolt}

Il collegamento parte dal dispositivo, non dalla dashboard.

1. Sulla macchina, avvia il runner ReelBolt. Mostra un breve codice di collegamento (otto caratteri, come `7HQD-4M2R`) e apre un link alla schermata di approvazione di ReelBolt con quel codice.
2. Nello spazio di lavoro, apri **Runner → Collega un dispositivo** (oppure digita il codice nella schermata **Collega un runner** se hai seguito un link senza codice).
3. Nella schermata di approvazione, controlla che il codice corrisponda carattere per carattere a quello sullo schermo, scegli in quale spazio di lavoro collegare il dispositivo e clicca **Approva questo dispositivo** (oppure **Rifiuta** per respingerlo).

Puoi approvare un dispositivo in uno spazio di lavoro in cui sei **Proprietario o Amministratore**, oppure nel tuo spazio di lavoro **Personale**. Un dispositivo viene collegato allo spazio di lavoro che scegli, e solo le persone di quello spazio di lavoro lo vedranno.

**Approva solo un codice mostrato su un dispositivo tuo.** Approvare collega un computer allo spazio di lavoro e gli permette di ricevere i tuoi media e di generare i tuoi video. Se qualcuno ti ha mandato un codice invece di una macchina che lo mostra, rifiutalo — sta cercando di collegare la sua macchina al tuo spazio di lavoro. La schermata mostra anche il nome della macchina, la piattaforma, la versione, quando è stata richiesta, la scadenza e l'indirizzo di rete da cui è arrivata la richiesta. Non attribuisce una posizione a quell'indirizzo: ReelBolt non ne cerca una.

Quando approvi, **ogni Proprietario di quello spazio di lavoro riceve un'email**, così un errore è visibile a posteriori. Un codice di collegamento dura poco; se è scaduto, chiedi al dispositivo di mostrarne uno nuovo.

## Cosa la schermata di approvazione volutamente non mostra {#what-the-approval-screen-deliberately-does-not-show}

La schermata di approvazione mostra il nome del dispositivo, la piattaforma, la versione del runner, l'ora della richiesta, la scadenza e l'indirizzo di rete grezzo della richiesta. **Non** attribuisce una città o una posizione a quell'indirizzo — ReelBolt non ha geolocalizzazione in questo prodotto, quindi nulla qui è mai etichettato con un luogo. Se un codice è sconosciuto, scaduto, già usato o rifiutato, oppure appartiene a uno spazio di lavoro di cui non sei membro, la risposta è sempre la stessa: **non esiste una richiesta di collegamento corrispondente.** ReelBolt non li distingue, quindi non ti dice mai che un codice che hai digitato è valido ma si trova nello spazio di lavoro di qualcun altro.

## Revocare una macchina collegata in ReelBolt {#revoking-a-paired-machine-in-reelbolt}

In **Runner**, ogni macchina collegata ha un'azione **Revoca**. Revocare la disconnette subito e non riceve più lavoro; poi scompare dall'elenco — lo spazio di lavoro qui non conserva traccia di un dispositivo revocato. Per usare di nuovo la macchina, collegala da capo dalla macchina stessa. Una macchina revocata non mostra un badge "Revocato"; semplicemente non c'è più nell'elenco, perché l'elenco è "ciò che può eseguire lavoro adesso".

## La preferenza di calcolo in ReelBolt {#the-compute-preference-in-reelbolt}


In **Spazio di lavoro → Impostazioni**, sotto **Dove gira il tuo lavoro**, un Proprietario o Amministratore sceglie dove gira il lavoro sui media. Usiamo le tue macchine collegate per il lavoro sui media quando sono online; tutto il resto gira su ReelBolt.

Le quattro scelte, nell'ordine in cui compaiono:

- **Automatico (predefinito)** — usa una macchina collegata quando ce n'è una online; altrimenti lo esegue ReelBolt.
- **Non usare le mie macchine** — il lavoro sui media gira sempre su ReelBolt, e collegare una macchina non cambia nulla.
- **Preferisci le mie macchine** — usa una macchina collegata ogni volta che ce n'è una online in grado di accettare il passaggio; altrimenti lo esegue ReelBolt. Una macchina offline o occupata non viene attesa.
- **Solo le mie macchine** — il lavoro sui media gira solo sulle tue macchine collegate. Se nessuna macchina collegata è online e in grado di accettare il passaggio, il passaggio **fallisce con `NO_RUNNER_ONLINE`** e l'esecuzione si ferma lì: niente viene eseguito o addebitato su ReelBolt al suo posto. I render Remotion richiedono una macchina che abbia segnalato un container runtime, quindi una macchina con solo ffmpeg non può accettarli; con questa scelta un render fallisce invece di girare altrove. È l'unica scelta che trasforma una macchina offline in un'esecuzione fallita invece che in coda.

## Cosa un runner non mette in pausa dalla dashboard {#what-a-runner-does-not-pause-from-the-dashboard}

Una macchina collegata può essere messa in pausa e ripresa dalla **schermata Home dell'app desktop o dalla sua riga di comando**, ma nulla nella dashboard le invia una pausa — non c'è nessun pulsante di pausa nella pagina Runner. Se vuoi che una macchina smetta di accettare lavoro, revocala (vedi sopra) oppure mettila in pausa sulla macchina stessa. `reelbolt-runner status` indica il motivo per cui non lavora, e `resume` cancella una sospensione esplicita senza modificare un file; i nomi completi delle impostazioni e i valori predefiniti sono in [Impostazioni dell'app desktop](#desktop-app-settings) più sopra in questa pagina.

## Pagine correlate in ReelBolt {#related-pages-in-reelbolt}

- [Spazi di lavoro e team](../workspaces.md) — chi può approvare un dispositivo e lo spazio di lavoro a cui è collegato.
- [Copiare una cartella del tuo computer in un progetto](../projects/local-folder-sync.md) — la sincronizzazione delle cartelle dell'app desktop: aggiungere una cartella, iCloud, pausa e cosa non succede mai.
- [Piani e crediti](./plans-and-credits.md) — perché il lavoro sulla tua macchina costa 0 crediti e quanta archiviazione include ogni piano.
- [Risoluzione dei problemi](../troubleshooting.md) — cosa significa `NO_RUNNER_ONLINE` quando un passaggio fallisce, e le altre voci del runner.
