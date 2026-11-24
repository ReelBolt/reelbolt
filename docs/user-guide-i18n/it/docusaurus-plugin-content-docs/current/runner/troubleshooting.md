---
title: Risoluzione dei problemi del Runner
sidebar_position: 9
description: Soluzioni per un ReelBolt Runner che non riesce ad collegarsi, risulta offline, non ha Docker o Podman, è dietro un proxy o ha l'orologio sbagliato.
i18n_source_sha: f47a6bf9413fc8d2703041e26a8d497cca3eb4a8
---

# Risoluzione dei problemi del Runner {#runner-troubleshooting}

Esegui prima `reelbolt-runner doctor`. Controlla gli strumenti video e Docker o Podman, e dice cosa non va.

## Non riesce ad collegarsi {#it-cannot-pair}

- Il codice scade dopo pochi minuti. Chiedi al Runner un nuovo codice.
- Assicurati di aver effettuato l'accesso a ReelBolt e di essere Proprietario o Amministratore dello spazio di lavoro che hai scelto.
- Controlla l'orologio del computer (vedi sotto).
- Se la pagina non si apre, copia nel browser l'indirizzo stampato dal Runner.

## Il collegamento dice che ReelBolt non è pronto per i runner {#pairing-says-reelbolt-is-not-ready-for-runners}

Se il Runner dice che ReelBolt ha approvato il computer ma non è pronto a ricevere la connessione dei runner, dal tuo lato non c'è nulla che non vada: ReelBolt non ha ancora finito di configurare la connessione per i runner. Contatta il supporto. L'approvazione ha già creato il dispositivo nel tuo spazio di lavoro, quindi il primo tentativo lascia in elenco un dispositivo che non si connette mai. Collega di nuovo quando il supporto conferma che è risolto (un nuovo tentativo viene registrato come nuovo dispositivo), poi rimuovi la voce inutilizzata in **Runner** su ReelBolt.

## Risulta offline {#it-shows-as-offline}

- Il Runner deve essere in esecuzione. Avvialo di nuovo.
- Controlla la tua connessione a internet. Al Runner servono solo connessioni in uscita.
- Un computer che è stato rimosso dallo spazio di lavoro deve essere collegato di nuovo.

## Docker o Podman mancano {#docker-or-podman-is-missing}

Senza Docker (24 o successivo) o Podman (5 o successivo), il Runner funziona comunque ma accetta solo il lavoro video più semplice (compilazione e analisi). Non può accettare i render video, e uno spazio di lavoro impostato su **Solo le mie macchine** farà fallire quei passaggi con `NO_RUNNER_ONLINE`. Installa uno dei due e riavvia il Runner. Altro in [Runner desktop](../account/desktop-runner.md).

## Dietro un proxy aziendale {#behind-a-company-proxy}

Il Runner legge le variabili d'ambiente standard `HTTPS_PROXY` e `NO_PROXY` per la connessione a ReelBolt. Impostale per l'account che esegue il Runner. La tua rete deve anche consentire `downloads.reelbolt.ai`, l'unico indirizzo da cui il Runner scarica gli aggiornamenti.

## L'orologio è sbagliato {#the-clock-is-wrong}

ReelBolt rifiuta le richieste di un computer il cui orologio è sfasato di più di 120 secondi. Attiva l'ora automatica nelle impostazioni di sistema e riavvia il Runner.

## Ancora bloccato {#still-stuck}

Esporta la diagnostica da **Impostazioni** nel Runner e allegala quando contatti il supporto. Vedi [Privacy](privacy.md).
