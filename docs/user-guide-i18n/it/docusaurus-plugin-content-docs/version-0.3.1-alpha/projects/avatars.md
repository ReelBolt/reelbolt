---
title: Avatar
sidebar_position: 6
description: Un avatar è la riproduzione, con consenso, di una persona reale, fatta con sue foto, brevi video e registrazioni della voce. Cosa serve, il consenso che dai, quanto viene conservato e come revocarlo o eliminarlo.
i18n_source_sha: f9e83cbdaa56fa835cd3bc386b59e69fab422c19
---
# Avatar {#avatars}

Un **avatar** è la riproduzione di una persona reale, fatta con alcune sue foto, brevi video e registrazioni della sua voce, conservata in un progetto. Funziona come una voce clonata: la persona deve essere d'accordo, tu registri quel consenso in ReelBolt e puoi revocarlo o eliminare l'avatar in qualsiasi momento.

Gli avatar sono una funzione nuova, in fase di attivazione, e potrebbero non essere ancora attivi nel tuo spazio di lavoro. La creazione di clip video che mostrano la persona viene prima provata e potrebbe arrivare nel tuo spazio di lavoro dopo la scheda Avatar; gli avatar che prepari ora funzionano con essa.

## Cosa ti serve {#what-you-need}

- **Il consenso della persona.** O sei tu la persona, oppure hai il suo permesso scritto. La persona deve avere almeno 18 anni. Non sono ammessi avatar di minori, di qualcuno che non ha dato il consenso o che imitano un personaggio pubblico.
- **Almeno una foto chiara della persona.** Viso frontale, buona luce, una sola persona nella foto, niente occhiali da sole. JPEG, PNG o WebP, fino a 30 MB, almeno 256 pixel per lato.
- **Se vuoi, brevi video e registrazioni.** Un video della persona che parla verso la fotocamera (MP4 o MOV, da 2 a 15 secondi, fino a 50 MB) e brevi registrazioni della sua voce (WAV o MP3, da 2 a 15 secondi, fino a 15 MB).

Un avatar contiene al massimo 8 foto, 3 video e 3 registrazioni, e un progetto al massimo 20 avatar. I file sono normali file del progetto: caricali prima, poi sceglili quando crei l'avatar. I file ancora in fase di controllo dopo il caricamento non si possono usare finché il controllo non è finito.

## Creare un avatar {#making-an-avatar}

Apri il progetto e vai alla scheda **Avatar**, accanto a **Voci**. Scegli i file, poi indica chi è la persona: il nome, e se sei tu oppure qualcuno che ti ha dato il permesso scritto (puoi allegare il permesso firmato come file del progetto). Conferma che la persona ha almeno 18 anni, leggi per intero la dichiarazione di consenso e spuntala, poi scegli per quanto tempo conservare l'avatar (fino a due anni, un anno se non scegli). Puoi anche collegare all'avatar una delle voci del progetto.

La dichiarazione di consenso è mostrata nella tua lingua. Spuntandola confermi che la persona ha acconsentito all'uso del suo viso, del suo corpo e della sua voce per creare video con l'AI in questo progetto, e all'invio di questi file, per quello scopo, ai servizi di AI che il progetto usa.

## Creare una clip con un avatar {#making-a-clip-with-an-avatar}

Quando è disponibile nel tuo spazio di lavoro, un passaggio **Genera video** può mostrare uno degli avatar del progetto. I modelli di workflow **Spokesperson clip** e **Talking avatar (budget)** sono punti di partenza pronti. Scegli l'avatar in uno di due posti:

- **Nella pagina dell'app, quando avvii.** Un'app creata da uno dei due modelli ha un campo **Avatar** tra le sue scelte. Elenca gli avatar del progetto con il loro stato (per esempio **Pronto**); uno che non si può usare è disattivato, con il motivo. Scegline uno, poi premi **Avvia**. Se lo lasci su **Nessuno**, la clip non mostra nessuno della scheda **Avatar**.
- **Nel passaggio stesso.** Apri **Modifica i passaggi (avanzato)**, apri il passaggio **Genera video** e trova la sua sezione **Avatar**. In **Avatar da mostrare** scegli l'avatar, oppure **Nessuno** per usare quello scelto nella pagina dell'app all'avvio. In **Riferimenti da inviare** puoi togliere la spunta ai tipi di file che non vuoi inviare (almeno un'immagine deve restare spuntata). **Parlato che la persona dice** prende le parole da un passaggio **Voce narrante** precedente o da un file audio del progetto; lo usa solo un modello che fa parlare la persona (come in **Talking avatar (budget)**), qualsiasi altro modello rifiuta il passaggio.

Un avatar funziona solo in un passaggio in cui **Cosa generare** è **Prompt scritto qui**, e mai insieme a un primo o ultimo fotogramma: in questi casi **Avatar da mostrare** è disattivato e spiega perché. Questi campi compaiono solo quando gli avatar sono attivi nel tuo spazio di lavoro.

Descrivi nel prompt che cosa fa la persona. Una **Spokesperson clip** non dice per forza parole precise, anche con registrazioni della voce nell'avatar: per far dire alla persona un copione usa **Talking avatar (budget)**.

Servono due permessi, e nessuno dei due sostituisce l'altro:

- il consenso della persona, che hai registrato quando hai creato l'avatar;
- l'interruttore del passaggio **Consenti l'invio dei tuoi file al servizio video**, subito sotto la sezione **Avatar** del passaggio **Genera video**, che attivi tu. La pagina dell'app ti avvisa quando è disattivato. L'assistente non può mai attivarlo al posto tuo.

Che cosa succede quando il passaggio viene eseguito:

- Le foto, i brevi video e le registrazioni della voce dell'avatar vengono inviati al provider video con ogni clip, così il provider mantiene la persona riconoscibile. Una clip non può usare insieme un avatar e un primo o ultimo fotogramma.
- Su alcuni modelli l'invio di foto e video costa in più, oltre alla clip: la stima dei costi lo include.
- L'avatar viene ricontrollato subito prima di creare ogni clip. Se il consenso viene revocato, la data di conservazione scade o i suoi file cambiano mentre il workflow è in corso, la clip successiva non viene creata e non ti viene addebitata; il passaggio si ferma e ne spiega il motivo. Una clip già in lavorazione viene completata e addebitata.
- Ogni clip di una persona è segnata come generata dall'IA nel file video stesso e mostra anche una piccola etichetta **AI-generated** nell'immagine, a meno che per quell'avatar tu abbia scelto di dichiararlo in un altro modo.
- Una clip creata con un avatar viene riutilizzata (invece di essere creata di nuovo) solo finché l'avatar non cambia. Dopo che revochi il consenso o cambi i file dell'avatar, le clip precedenti non vengono mai riutilizzate.

## Una clip parlata a basso costo {#a-talking-clip-on-a-budget}

Il modello di workflow **Talking avatar (budget)** crea una clip più economica della persona che dice alcune frasi, con la sua voce. Scrive le parole partendo dal tuo brief, le pronuncia con la voce collegata all'avatar, crea un'immagine fissa della persona dalle foto dell'avatar e poi la anima in modo che la persona dica quelle parole.

- L'avatar ha bisogno di una **voce collegata** (oppure scegli una voce per l'esecuzione). Collegala quando crei o modifichi l'avatar.
- La clip dura quanto ciò che la persona dice: almeno 5 e al massimo 15 secondi. Chiedi da 10 a 15 secondi nel tuo brief.
- Fino a tre foto dell'avatar vengono inviate al servizio di immagini per creare l'immagine fissa, e la narrazione e l'immagine fissa vengono inviate al servizio video. Attiva tu **Consenti l'invio dei tuoi file al servizio video** nell'ultimo passaggio (**Modifica i passaggi (avanzato)**, il passaggio **Genera video**); l'assistente non lo fa mai.
- L'immagine fissa viene conservata con l'avatar, riutilizzata per le clip successive finché l'avatar non cambia ed eliminata insieme all'avatar. Non compare mai tra i file del progetto.
- Il costo è un'immagine fissa più i secondi di video; la stima lo mostra prima dell'avvio.

## Una sola ricetta: Clip con avatar {#one-recipe-avatar-clip}

Dove i campi della ricetta sono attivi nel tuo spazio di lavoro, **Spokesperson clip** e **Talking avatar (budget)** sono
una sola ricetta, **Clip con avatar**. Scegli l'avatar e il **Formato**, poi decidi con l'interruttore **Modalità leggera
(più economica)**:

- **Spento:** scrivi **Cosa fa** e scegli una **Durata** di 6 o 10 secondi. È la clip del portavoce.
- **Acceso:** scrivi **Cosa dice**. Le parole vengono pronunciate esattamente come le hai scritte, con la voce collegata
  all'avatar, e animate su un'immagine fissa, come nella clip a basso costo qui sopra. Ogni paragrafo (separato da una riga
  vuota) è una frase parlata. Viene eseguito e stimato solo il percorso scelto.

I modelli sono in **Altre opzioni**; quello consigliato è già scelto. In modalità leggera puoi scegliere anche il
**Modello per l'immagine**, il modello che crea l'immagine fissa della persona. Consenti l'invio dei file nel passaggio della clip
del percorso che usi. I flussi di lavoro che hai già creato dai due vecchi modelli continuano a funzionare come prima.

## Quando un avatar non si può usare {#when-an-avatar-cannot-be-used}

La scheda **Avatar** mostra se ogni avatar è pronto. Un avatar non si può usare quando:

- il suo consenso è stato revocato;
- la data fino a cui conservarlo è passata;
- una delle sue foto, dei suoi video o delle sue registrazioni è stata eliminata, sostituita o modificata dopo che l'hai aggiunta (scegli di nuovo i file per risolvere);
- uno dei suoi file è stato messo in attesa da un controllo di sicurezza;
- non ha una foto utilizzabile della persona.

Una voce collegata che non si può più usare non blocca l'avatar; riguarda solo la voce.

## Revocare il consenso ed eliminare {#withdrawing-consent-and-deleting}

**Revoca il consenso** rende subito l'avatar inutilizzabile, ovunque nel progetto. Un avatar revocato viene eliminato automaticamente dopo 30 giorni. **Elimina** rimuove subito l'avatar, i suoi file e le immagini fisse create da esso, e ti dice cosa è stato rimosso. Quando la data fino a cui conservarlo passa, l'avatar viene eliminato automaticamente; per tenerlo più a lungo, conferma di nuovo il consenso prima di quella data.

I video che hai già creato con un avatar restano tuoi file; eliminali tu se la persona te lo chiede.

In **File** del progetto, un file usato da un avatar è segnato con **Avatar:** seguito dal nome dell'avatar. Un file così non si può eliminare da solo: prima toglilo dall'avatar (**Cambia i file**) oppure elimina l'avatar. Ricevi un'email una settimana prima che un avatar che hai creato arrivi alla data di conservazione; puoi disattivarla in **Impostazioni > Notifiche**.

## Cosa può fare l'assistente {#what-the-assistant-can-do}

L'assistente può elencare gli avatar di un progetto, dirti quali si possono usare e, se uno non si può usare, perché. Non può creare, modificare, revocare o eliminare un avatar: il consenso lo dai sempre e solo tu, nella scheda **Avatar**. Se gli avatar non sono attivi nel tuo spazio di lavoro, l'assistente te lo dice.

Quando l'assistente avvia o modifica un workflow che mostra un avatar, la scheda di conferma nomina l'avatar e dice che le foto, i video e le registrazioni della voce della persona vengono inviati al provider video. Non attiva mai il permesso di un passaggio di inviare contenuti.
