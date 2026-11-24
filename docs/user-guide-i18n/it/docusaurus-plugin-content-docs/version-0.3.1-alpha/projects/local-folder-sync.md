---
title: Copiare una cartella del tuo computer in un progetto
sidebar_position: 3
description: Usare l'app desktop di ReelBolt per tenere una cartella del tuo computer copiata in un progetto - scegliere la cartella, cosa viene copiato e cosa non succede mai, i file presenti solo in iCloud, OneDrive o Dropbox, mettere in pausa e riprendere, e cosa significa ogni messaggio.
i18n_source_sha: 06083d9ab05f9b7d6d0338d6418d8e7189f657e4
---

# Copiare una cartella del tuo computer in un progetto {#copying-a-folder-from-your-computer-into-a-project}

Puoi puntare l'app desktop di ReelBolt a una cartella del tuo computer (il Desktop, la cartella di una scheda
della fotocamera, o una cartella in iCloud Drive, OneDrive o Dropbox) e ReelBolt ne tiene una copia di tutto il
contenuto dentro uno dei tuoi progetti. È a senso unico (dal tuo computer a ReelBolt) e in tempo reale: se
modifichi un file mentre l'app è in esecuzione, il progetto si allinea in pochi secondi, e tutto ciò che hai
modificato mentre l'app era chiusa viene copiato quando si riavvia.

È così che iCloud Drive funziona con ReelBolt. Apple non permette a un sito web di leggere direttamente il tuo
iCloud Drive, ma su Windows l'app iCloud mostra iCloud Drive come una normale cartella in Esplora file, quindi
ReelBolt può copiare da lì come da qualsiasi altra cartella. OneDrive, Dropbox, Box, una condivisione NAS e una
semplice cartella sul tuo disco funzionano allo stesso modo.

**Solo Windows e Linux.** Non esiste una versione per Mac dell'app desktop, quindi questa funzione non è
disponibile su Mac. Su Windows, iCloud Drive è coperto tramite iCloud per Windows di Apple.

## Installare l'app desktop di ReelBolt {#installing-the-reelbolt-desktop-app}

La sincronizzazione delle cartelle del computer fa parte dell'**app desktop di ReelBolt** (chiamata anche
runner desktop). È la stessa app che può esportare i tuoi video sulla tua macchina; la sincronizzazione delle
cartelle è una delle sue schermate.

1. Compila l'app dal codice sorgente di ReelBolt per **Windows** o **Linux**; ReelBolt non pubblica un
   programma di installazione. [Runner desktop](../account/desktop-runner.md#installing-and-running-a-reelbolt-desktop-runner)
   riporta i comandi.
2. Accedi collegandola al tuo spazio di lavoro: l'app mostra un breve codice, e tu lo approvi in
   **Runner → Collega un dispositivo** (vedi [Runner desktop](../account/desktop-runner.md) per i passaggi di collegamento).
3. Lascia l'app in esecuzione. La sincronizzazione delle cartelle funziona solo mentre l'app è in esecuzione.

Se la schermata **Cartelle** dell'app dice che non può aprire un selettore di cartelle, l'app è stata avviata da
un terminale senza finestra; in quella schermata è indicato un comando per aggiungere una cartella dal terminale.

## Aggiungere una cartella da copiare {#adding-a-folder-to-copy}

1. Nell'app desktop, apri **Cartelle** (nella finestra dell'app è la sezione intitolata **Cartelle su questo
   computer**).
2. Scegli **Aggiungi una cartella**. Seleziona la cartella che vuoi copiare, per esempio `C:\Users\you\iCloudDrive\Footage`.
3. Scegli il progetto in cui copiarla e il nome della cartella da usare all'interno di quel progetto. ReelBolt
   compila il nome della cartella stessa (così `Footage` diventa la cartella di progetto `Footage`), e puoi cambiarlo.
4. Conferma. La cartella compare nell'elenco con il suo stato, e la copia inizia.

Puoi aggiungere più cartelle, nello stesso progetto o in progetti diversi. Ognuna viene copiata in una propria
cartella dentro il progetto, quindi due cartelle non possono mai sovrascriversi a vicenda. Puoi anche
sincronizzare da più di un computer: le cartelle di ogni computer finiscono nelle proprie cartelle di progetto.

## Cosa viene copiato e cosa non succede mai {#what-gets-copied-and-what-never-happens}

Mentre l'app è in esecuzione, il progetto viene mantenuto uguale alla cartella che hai scelto:

- I **file nuovi** nella cartella vengono aggiunti al progetto.
- I **file modificati** vengono aggiornati sul posto nel progetto, così tutto ciò che li usa continua a
  funzionare. Il file mantiene la sua identità: un flusso di lavoro che vi fa riferimento lo trova ancora.
- I **file che elimini dalla cartella** vengono rimossi anche dal progetto. Prima di rimuovere una parte
  consistente di una cartella, ReelBolt si ferma e chiede (vedi "Mettere in pausa e riprendere").
- I **file invariati** vengono lasciati stare e non vengono caricati di nuovo.
- I **file vuoti** vengono copiati come qualsiasi altro file.

Cosa non succede mai:

- **Non viene mai scritto nulla sul tuo computer.** ReelBolt legge soltanto la cartella. Non modifica, sposta,
  rinomina né elimina mai nulla sul tuo disco o nel tuo archivio cloud.
- **I file che hai caricato tu non vengono mai toccati**, e nemmeno quelli scritti da un agente o arrivati da
  GitHub o da un cloud drive, anche se uno ha lo stesso nome. In quel caso la copia della cartella viene
  saltata e l'app lo segnala.
- **Rimuovere una cartella non elimina nulla.** I file già copiati restano nel progetto.

Alcuni file non vengono mai copiati, e ognuno viene conteggiato nello stato dell'app così puoi vedere cosa è
stato escluso: file e cartelle nascosti (nomi che iniziano con un punto), disordine di sistema come `.DS_Store`,
`Thumbs.db` e `desktop.ini`, file temporanei come `~$…`, `.tmp`, `.part` e `.crdownload`, e collegamenti
(symlink). Un singolo file che non può essere letto viene saltato e gli altri vengono comunque copiati.

## File presenti solo in iCloud, OneDrive o Dropbox {#files-that-are-only-in-icloud-onedrive-or-dropbox}

iCloud, OneDrive e Dropbox possono tenere alcuni file **solo nel cloud** per risparmiare spazio sul tuo PC.
Quei file non hanno contenuto sul tuo disco finché qualcosa non li apre. ReelBolt non li aprirà da solo,
perché scaricherebbe in silenzio gigabyte che potresti non voler scaricare: quindi vengono saltati e l'app li
elenca come "solo nel cloud, non scaricato su questo PC".

Per copiarli, di' al servizio di tenerli su questo PC:

- **iCloud Drive**: clicca con il tasto destro sulla cartella in Esplora file e scegli **Mantieni sempre su
  questo dispositivo** (nelle versioni meno recenti di iCloud per Windows l'opzione si chiama **Mantieni
  scaricato**). Puoi farlo su un singolo file o su tutta la cartella.
- **OneDrive**: clicca con il tasto destro sulla cartella e scegli **Mantieni sempre su questo dispositivo**.
- **Dropbox**: clicca con il tasto destro sulla cartella e scegli **Rendi disponibile offline**.

Dopo che i file sono stati scaricati, ReelBolt li copia la volta successiva che controlla la cartella, subito
se l'app è in esecuzione. La stessa regola vale per qualsiasi cartella cloud che usa file "solo online".

## Mettere in pausa e riprendere una cartella {#pausing-and-resuming-a-folder}

Puoi mettere in pausa una cartella nell'app desktop in modo che per un po' non venga copiato nulla da essa;
**Riprendi** la fa ripartire. La pausa non elimina nulla nel progetto.

ReelBolt mette anche in pausa una cartella **da solo** quando la cartella sembra improvvisamente vuota o quasi
sparita, per esempio un'unità non collegata, o una cartella cloud che non ha finito di caricarsi. È una misura
di sicurezza perché una cartella temporaneamente non disponibile non possa cancellare il tuo progetto. Quando
succede, l'app mostra:

> "La cartella su **questo computer** sembra vuota o quasi sparita: la sincronizzazione è in pausa, così non
> viene eliminato nulla. Se hai davvero rimosso quei file, riprendila dall'app desktop."

Cosa fare:

- Se **non** hai eliminato quei file, lasciala in pausa, sistema la cartella (ricollega l'unità, lascia che la
  cartella cloud finisca di caricarsi) e riprendila. Non è stato eliminato nulla.
- Se li hai eliminati **di proposito**, scegli **Riprendi e rimuovi i file eliminati**, il pulsante che l'app
  mostra proprio per questa pausa. Conferma le rimozioni, e il progetto si allinea.

Una cartella messa in pausa nell'app può essere ripresa anche dalla pagina del progetto in ReelBolt, dove sono
elencati i suoi computer. Una pausa fatta in ReelBolt si riprende in ReelBolt.

## Cosa significa ogni messaggio {#what-each-message-means}

La schermata **Cartelle** mostra uno stato per cartella. I più comuni:

- **Aggiornato**: tutto ciò che c'è nella cartella è nel progetto. Non c'è nulla da fare.
- **Sincronizzazione di N file**: l'app sta copiando file in questo momento.
- **In pausa**: non viene copiato nulla. Riprendila quando vuoi che continui.
- **Richiede attenzione**: qualcosa ha fermato la sincronizzazione; la riga sotto dice cosa. I motivi più comuni:
  - *"Questo computer non è più collegato a ReelBolt: collegalo di nuovo."* L'app è stata scollegata o
    revocata dallo spazio di lavoro. Collega di nuovo il computer.
  - *"La persona che ha collegato questo computer non può più modificare i progetti in questo spazio di lavoro."*
    L'account che ha collegato questo computer ha lasciato lo spazio di lavoro o ha perso il permesso di
    modifica. Collega di nuovo il computer con un account che può farlo.
  - *"Questa cartella non è più collegata a un progetto ReelBolt."* La cartella è stata scollegata in
    ReelBolt. Aggiungila di nuovo se vuoi ancora che venga copiata.
  - *"Lo spazio di archiviazione è pieno (X di Y usati)."* Lo spazio di lavoro non ha più spazio. Elimina file
    o passa a un piano superiore, poi la sincronizzazione continua da sola.
  - *"Un file di un'altra origine ha già questo nome nel progetto, quindi questo viene saltato."* Un file che
    hai caricato, che un agente ha scritto, o che GitHub o un cloud drive ha portato ha già quel nome e quella
    posizione. Rinomina uno dei due se vuoi la copia della cartella.
  - *"Solo in iCloud/OneDrive/Dropbox, non scaricato su questo PC…"* Vedi la sezione sopra; usa **Mantieni
    sempre su questo dispositivo**.
- **Alcuni file non sono stati copiati**: una nota gialla che indica quanti e perché, con alcuni nomi di
  esempio. I motivi sono l'elenco in "Cosa viene copiato e cosa non succede mai".

## Rimuovere una cartella {#removing-a-folder}

Scegli **Rimuovi** su una cartella nell'app desktop e conferma. ReelBolt smette di copiare da essa, e **i file
già copiati restano nel progetto**: non viene eliminato nulla. Se vuoi che venga copiata di nuovo in seguito,
aggiungila di nuovo. (Puoi anche scollegare una cartella dalla pagina del progetto in ReelBolt, con lo stesso
risultato.)

## Spazio di archiviazione: quanto ne include il tuo piano {#storage-how-much-your-plan-includes}

Tutto ciò che c'è nel tuo spazio di lavoro conta contro lo spazio di archiviazione del tuo piano: i file che
carichi, i file sincronizzati da GitHub, da un cloud drive o da una cartella del computer, i file scritti
dagli agenti, e i video e gli altri risultati prodotti da un'esecuzione. I piani includono:

| Piano | Spazio di archiviazione |
|---|---|
| Prova gratuita | 1 GB |
| Creator | 15 GB |
| Pro | 50 GB |
| Team | 100 GB |
| Enterprise | illimitato |

Quando lo spazio è pieno, i nuovi file smettono di essere aggiunti: un caricamento viene rifiutato, e una
sincronizzazione lascia i file che non ci stavano e te lo dice. **Non viene eliminato nulla automaticamente**,
nemmeno se passi a un piano più piccolo: liberi spazio eliminando file, oppure passi a un piano superiore.

Vedi quanto ne stai usando in **Impostazioni → Piano e fatturazione** ("4,2 GB di 15 GB usati") o nella pagina
**Utilizzo**, e vedi [Piani e crediti](../account/plans-and-credits.md) per cosa conta e cosa fare quando è
pieno.

## Pagine correlate in ReelBolt {#related-pages-in-reelbolt}

- [Runner desktop](../account/desktop-runner.md): installare l'app, collegare un computer e quali dati lasciano
  la tua macchina.
- [Piani e crediti](../account/plans-and-credits.md): lo spazio di archiviazione incluso in ogni piano e cosa
  conta ai fini del limite.
- [Sincronizzare i file da Google Drive, OneDrive o Dropbox](./cloud-drive-sync.md): l'altro modo di portare una
  cartella cloud in un progetto, che legge direttamente il cloud invece di passare dall'app desktop.
- [Sincronizzare i file da GitHub](./github-repository.md): tenere un progetto allineato a un repository.
- [Risoluzione dei problemi](../troubleshooting.md): l'elenco completo dei messaggi e cosa fare in ciascun caso.
