---
title: Usa un modello sul tuo computer
sidebar_position: 11
description: Come far usare al tuo spazio di lavoro un modello AI che gira sul tuo computer, come attivare la condivisione nell'app desktop, come aggiungerlo nelle impostazioni dello spazio di lavoro e cosa controllare quando non risponde.
i18n_source_sha: 5d9b89fcdb267a76fec19ca3363d4de37b4591ce
---

# Usa un modello sul tuo computer {#use-a-model-on-your-own-computer}

Se esegui un modello AI sul tuo computer, il tuo spazio di lavoro può usarlo per i passaggi che scrivono o leggono testo e guardano immagini. Il modello continua a girare sul tuo computer. ReelBolt gli passa le richieste solo tramite l'app desktop di ReelBolt.

## Cosa fa {#what-it-does}

Il modello lo esegui tu, con un programma come Ollama, LM Studio, llama.cpp, vLLM o qualsiasi altro server che funzioni allo stesso modo. Quando un passaggio del tuo spazio di lavoro lo usa, ReelBolt invia la richiesta all'app desktop sul tuo computer, e l'app la passa al tuo modello. La risposta torna indietro nello stesso modo.

- Non consuma crediti. Paghi il tuo computer, non ReelBolt.
- Funziona per la chat e per i passaggi che guardano immagini, se il tuo modello supporta le immagini.
- Se il modello è un modello di embedding, può essere usato anche per indicizzare i file del tuo progetto per la ricerca. Dopo il passaggio a quel modello, reindicizza i file del progetto così la ricerca usa il nuovo modello.
- È l'app desktop ad avviare la connessione. Non devi aprire nessuna porta sulla tua rete.

## Prima di iniziare {#before-you-start}

- L'app desktop di ReelBolt è installata e collegata al tuo spazio di lavoro. Vedi [Runner desktop](./desktop-runner.md).
- Il programma del tuo modello è in esecuzione sullo stesso computer.
- Sei proprietario o amministratore dello spazio di lavoro.

## Attivare la condivisione nell'app desktop {#switch-sharing-on-in-the-desktop-app}

1. Apri l'app desktop di ReelBolt e vai alla schermata **Modello locale**.
2. In **Dove si trova il tuo modello**, scegli il server trovato dall'app oppure digita il suo indirizzo. L'indirizzo abituale per Ollama è `http://127.0.0.1:11434`.
3. Seleziona la casella per confermare di aver letto l'avviso sui prompt. L'interruttore resta disattivato finché non lo fai. Poi attiva **Condividi il mio modello locale con il mio spazio di lavoro**.
4. Controlla che i nomi dei tuoi modelli compaiano sotto **Modelli trovati**.

La condivisione è disattivata finché non la attivi. Di default l'app parla solo con un modello sullo stesso computer. Se il tuo modello gira su un altro computer della tua rete, seleziona **Il mio modello gira su un altro computer della mia rete** e inserisci il suo indirizzo.

## Aggiungerlo nelle impostazioni dello spazio di lavoro {#add-it-in-workspace-settings}

1. In ReelBolt, apri **Spazio di lavoro → Provider**.
2. Aggiungi un provider e scegli **Modello sul mio computer (app desktop)**.
3. Scegli il computer dall'elenco e inserisci il nome del modello.
4. Salva, poi usa il pulsante di test del provider per verificare che risponda.

Non devi inserire nessuna chiave. Poi scegli il modello nei passaggi o nei predefiniti in cui vuoi usarlo.

## Cosa sapere prima di condividere {#what-to-know-before-you-share}

- Finché la condivisione è attiva, i prompt dei membri del tuo spazio di lavoro, per i passaggi che usano questo modello, arrivano al tuo computer. Chiunque possa usare il tuo computer può leggerli.
- Il computer deve essere acceso, l'app desktop deve essere in esecuzione e connessa, e il programma del tuo modello deve essere in esecuzione.
- Se l'app è in pausa, la condivisione è disattivata o il computer è offline, il passaggio fallisce con un messaggio che dice che il tuo modello locale è offline. ReelBolt non passa a un altro modello.
- ReelBolt non controlla la velocità del modello né la qualità delle sue risposte. Dipende dal tuo modello e dal tuo computer.

## Risoluzione dei problemi {#troubleshooting}

- **"Il tuo modello locale è offline".** Apri l'app desktop e controlla che sia connessa e che la condivisione sia attiva, poi avvia di nuovo il passaggio.
- **"Impossibile raggiungere il server del tuo modello locale".** Avvia il programma del tuo modello e controlla l'indirizzo nella schermata **Modello locale**.
- **"Occupato".** Il tuo modello sta già gestendo il numero massimo di richieste che l'app consente contemporaneamente. Riprova tra un attimo.
- **"Nessuna risposta entro il tempo limite".** Il modello potrebbe essere ancora in caricamento, oppure la richiesta potrebbe essere troppo grande. Riprova, oppure usa un input più piccolo. Un modello lento può aver bisogno di più tempo per la prima risposta, quindi aspetta che il passaggio finisca prima di riprovare.
- **Il computer non compare nell'elenco.** Controlla che l'app desktop sia collegata a questo spazio di lavoro e che la condivisione sia attiva, poi ricarica la pagina.
- **L'elenco dei modelli non è aggiornato.** Dopo aver aggiunto o rimosso un modello, riconnetti l'app desktop.
