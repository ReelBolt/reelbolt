---
title: Self-hosting di ReelBolt
sidebar_position: 12
description: Esegui il tuo ReelBolt con Docker Compose - requisiti, segreti da impostare, primo accesso, aggiunta di un provider di modelli, rafforzamento della sicurezza e cosa è facoltativo.
i18n_source_sha: 95684fc96318e33062eb1e24dcc0b44cb2d22a0c
---

# Self-hosting di ReelBolt {#self-hosting-reelbolt}

Questa pagina ti porta da un clone appena fatto a un ReelBolt funzionante su una sola macchina: una dashboard in cui puoi accedere e una prima esecuzione di un flusso di lavoro che non costa nulla. Usa Docker Compose e nient'altro.

Un ReelBolt self-hosted esegue lo stesso codice del servizio ospitato su reelbolt.ai, gestito da te. Alcune cose esistono solo nel servizio ospitato: piani a pagamento, crediti e fatturazione, e operazioni gestite (vedi [Piani e crediti](account/plans-and-credits.md)). I provider di modelli li porti tu e li paghi direttamente. ReelBolt in sé non ti addebita nulla, e niente in questa guida chiama un provider a pagamento.

## Cosa ti serve {#what-you-need}

- Una macchina Linux, macOS o Windows con Docker Engine e il plugin Docker Compose (`docker compose version` deve funzionare). Linux è la piattaforma testata.
- **Memoria:** prevedi circa 8 GB di RAM libera. Lo stack a riposo occupa circa 5,5 GB, in gran parte per il modello di embedding locale (circa 3,4 GB); i render video richiedono altro margine.
- **Disco:** circa 15 GB per le immagini e la cache di build alla prima compilazione, più lo spazio per i tuoi media.
- **Tempo:** il primo `docker compose up --build` compila ogni servizio e richiede da 10 a 20 minuti con una connessione veloce. Gli avvii successivi richiedono pochi minuti.
- Git e `openssl` (per generare i segreti).

## Installa {#install}

```bash
git clone https://github.com/ReelBolt/reelbolt.git reelbolt
cd reelbolt
cp .env.example .env
cp garage/garage.toml.example garage/garage.toml
```

## Imposta i segreti obbligatori {#set-the-required-secrets}

Diversi servizi si rifiutano di partire, o Compose si rifiuta di funzionare del tutto, finché questi non hanno valori reali. Nessuno ha un valore predefinito sicuro, quindi genera tu ciascuno e non fare mai commit di `.env` o `garage/garage.toml` (entrambi sono ignorati da git).

| Dove | Nome | Cos'è |
| --- | --- | --- |
| `.env` | `JWT_SIGNING_KEY` | Firma le sessioni di accesso. Almeno 32 caratteri: `openssl rand -hex 32`. |
| `.env` | `SANDBOX_API_TOKEN` | Segreto condiviso tra il motore dei flussi di lavoro e la sandbox che esegue il render del codice video scritto dai modelli. `openssl rand -hex 32`. |
| `.env` | `IMPORT_WORKER_TOKEN` | Segreto condiviso per il worker isolato che apre i file zip e PDF caricati. `openssl rand -hex 32`. |
| `.env` | `MINIO_SECRET_KEY` | Chiave segreta dell'archivio oggetti incluso (Garage). Almeno 16 caratteri: `openssl rand -hex 20`. |
| `garage/garage.toml` | `rpc_secret` | 64 caratteri esadecimali: `openssl rand -hex 32`. |
| `garage/garage.toml` | `admin_token`, `metrics_token` | Qualsiasi stringa casuale lunga: `openssl rand -hex 24`. |

Cambia anche `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD` e `MINIO_ACCESS_KEY` rispetto ai valori di esempio, per qualunque macchina che non sia un computer locale usa e getta. `RABBITMQ_PASSWORD` deve essere impostata (Compose si rifiuta di avviarsi senza).

Puoi inserire i valori generati con un unico blocco (`sed` GNU; su macOS usa `sed -i ''`):

```bash
sed -i "s|^JWT_SIGNING_KEY=.*|JWT_SIGNING_KEY=$(openssl rand -hex 32)|" .env
sed -i "s|^SANDBOX_API_TOKEN=.*|SANDBOX_API_TOKEN=$(openssl rand -hex 32)|" .env
sed -i "s|^IMPORT_WORKER_TOKEN=.*|IMPORT_WORKER_TOKEN=$(openssl rand -hex 32)|" .env
sed -i "s|^MINIO_SECRET_KEY=.*|MINIO_SECRET_KEY=$(openssl rand -hex 20)|" .env
sed -i "s|CHANGE_ME_32_BYTE_HEX|$(openssl rand -hex 32)|" garage/garage.toml
sed -i "s|admin_token = \"CHANGE_ME\"|admin_token = \"$(openssl rand -hex 24)\"|" garage/garage.toml
sed -i "s|metrics_token = \"CHANGE_ME\"|metrics_token = \"$(openssl rand -hex 24)\"|" garage/garage.toml
```

Se la porta 80 o 443 è già usata sulla macchina, imposta `APP_PORT` e `HTTPS_PORT` in `.env` su porte libere. Il resto di questa pagina presuppone il valore predefinito `APP_PORT=80`.

### L'account amministratore {#the-admin-account}

`ADMIN_EMAIL` (predefinito `admin@reelbolt.local`) è il primo amministratore. Lascia `ADMIN_PASSWORD` vuoto e ReelBolt genera una password casuale al primo avvio. Imposta invece `ADMIN_PASSWORD` se vuoi sceglierla tu.

## Avvia lo stack {#start-the-stack}

```bash
docker compose up --build -d
docker compose ps
```

Aspetta che i servizi risultino `healthy`. `sandbox-runtime` risulta `Exited (0)`: costruisce solo l'immagine usata dalla sandbox, ed è normale.

## Accedi per la prima volta {#sign-in-for-the-first-time}

Apri <http://localhost> (oppure `http://localhost:<APP_PORT>`). La dashboard è su `/app`.

Se hai lasciato `ADMIN_PASSWORD` vuoto, la password generata non viene mai scritta nel log: viene salvata una sola volta in un file con permessi 0600 sul volume `go-api-secrets` (`/var/lib/reelbolt/bootstrap-admin-password` nel container `go-api`, oppure il percorso in `ADMIN_PASSWORD_FILE`), e il log di `go-api` indica quel file:

```bash
docker compose exec go-api cat /var/lib/reelbolt/bootstrap-admin-password
```

Accedi come `ADMIN_EMAIL` con quella password. ReelBolt ti fa scegliere una nuova password prima di poter fare qualsiasi altra cosa. Il file sopravvive ai riavvii dei container; eliminalo dopo aver effettuato l'accesso. Se perdi la password prima di cambiarla, esegui `docker compose down -v` e riparti da un'installazione vuota.

Questa installazione non ha un provider email, quindi le email di benvenuto, di verifica e di reimpostazione della password vengono scritte nel log di `go-api` invece di essere inviate. Aggiungine uno in `.env` (le variabili `SMTP_*`) quando inviti altre persone. Vedi [Gestione degli utenti](admin/users.md).

## Aggiungi un provider di modelli {#add-a-model-provider}

Una nuova installazione non ha nessun provider di modelli, quindi tutto ciò che richiede un modello AI resta non disponibile. Come amministratore, apri **Piattaforma → Inferenza** e aggiungi un provider con la tua chiave API. I passaggi sono in [Provider di inferenza](admin/inference-providers.md). I provider ti fatturano direttamente; ReelBolt non vede né ricarica quei costi.

Finché non ne aggiungi uno, puoi comunque provare l'editor e i flussi di lavoro che non usano modelli, come l'inserto con green screen e l'estrazione di fotogrammi. Un'esecuzione di soli questi passaggi viene contata come esecuzione gratuita.

## Esegui qualcosa di gratuito {#run-something-free}

1. Crea un progetto e carica un breve video.
2. Crea un flusso di lavoro con un solo passaggio **Cattura un fotogramma** rivolto al video, ed eseguilo.
3. L'esecuzione termina in pochi secondi e registra il fotogramma come immagine nel progetto. Nessun provider viene chiamato.

## Rafforza la sicurezza dell'installazione {#harden-the-install}

I valori predefiniti sono pensati per una prova in locale. Prima che qualcuno al di fuori della tua macchina possa raggiungerla:

- **Porta ogni componente fuori dalla modalità di sviluppo.** Il `.env` fornito è un `.env` da sviluppatore. Prima che l'installazione sia raggiungibile da internet o da una rete condivisa, imposta questi valori in `.env` e ricompila (`docker compose up --build -d`):
  - `APP_ENV=production` (il servizio di accesso). In `development` scrive nel proprio log i link completi di accesso, verifica e reimpostazione della password ogni volta che non è impostato un provider email, e chiunque possa leggere quel log può usarli. In `production` non lo fa mai, quindi configura prima le variabili `SMTP_*` o le persone invitate non riceveranno nulla.
  - `ASPNETCORE_ENVIRONMENT=Production` (i servizi di inferenza e dei flussi di lavoro). `Development` espone l'esploratore API Swagger e il documento OpenAPI, che elencano ogni route.
  - `WEB_BUILD_TARGET=production` e `WEB_NODE_ENV=production` (la dashboard), e `SITE_BUILD_TARGET=production` e `SITE_NODE_ENV=production` (il sito pubblico). I valori predefiniti costruiscono server di sviluppo: `next dev` con hot-reload, con pagine più lente e pesanti ed errori dettagliati. Per la dashboard imposta anche `WEB_NODE_OPTIONS=` su vuoto, perché il valore predefinito avvia un debugger Node che esegue codice per chiunque raggiunga la sua porta.
- **Cambia le credenziali predefinite.** Un `.env` nuovo usa gli accessi noti a tutti per database, coda di messaggi e archivio oggetti (`postgres`/`postgres`, `guest`/`guest`, `minioadmin`). Sono accettabili solo perché quei servizi ascoltano su `127.0.0.1` di una macchina che usi solo tu. Su qualsiasi host a cui altre persone possono accedere, o se mai cambi quei binding, imposta valori robusti per `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, `MINIO_ACCESS_KEY` e `MINIO_SECRET_KEY` (`openssl rand -hex 20`) prima del primo avvio, e rendi `JWT_SIGNING_KEY` un valore casuale nuovo. Cambiarli dopo il primo avvio non riscrive le copie memorizzate: reimposta con `docker compose down -v` su un'installazione vuota, oppure cambiali dentro ogni servizio.
- **Usa un vero TLS.** Punta un dominio alla macchina, imposta `DOMAIN` e `ACME_EMAIL` in `.env`, poi esegui `docker compose --profile tls up -d caddy` e `docker compose up -d --force-recreate nginx`. Quando `https://<il tuo dominio>` mostra un certificato attendibile, imposta `FORCE_HTTPS=true` e `COOKIE_SECURE=true` e riavvia nginx. Lascia `COOKIE_SECURE` disattivato su `http://` semplice, altrimenti l'accesso smette di funzionare. Gli stessi passaggi, con i dettagli sul rinnovo, sono in `docs/tls.md` nel repository.
- **Tieni disattivata la registrazione pubblica.** `SIGNUP_ENABLED=false` è il valore predefinito. L'amministratore crea gli account.
- **Fai attenzione alle porte pubblicate da Docker.** Docker pubblica le porte aggirando il firewall dell'host (le regole di `ufw` e `firewalld` non si applicano). Database, coda di messaggi, archivio oggetti e altri servizi interni sono legati solo a `127.0.0.1`; lascia le cose così ed esponi solo `APP_PORT` e `HTTPS_PORT`. Non cambiare un binding `127.0.0.1:` in `0.0.0.0` senza un motivo che sapresti difendere.
- **Tieni `SANDBOX_NETWORK_EGRESS=false`.** La sandbox esegue codice scritto da un modello AI. Con questa impostazione non ha già accesso alla rete; non attivarlo.
- **Dai alla sandbox una macchina tutta sua se è condivisa.** Il servizio sandbox controlla Docker sul suo host tramite il socket di Docker. Su una macchina usata da altre persone o che contiene altri dati, eseguilo su una VM dedicata e tieni privato il suo `SANDBOX_API_TOKEN`.
- **Fai un backup** dei volumi Docker (`postgres`, `garage`) e del tuo file `.env` insieme. Senza i segreti in `.env`, i dati non si possono leggere.

## Cosa è facoltativo {#what-is-optional}

I servizi inclusi partono per impostazione predefinita così che tutte le funzioni lavorino, ma puoi farne a meno:

- **Whisper** (trascrizione vocale locale, `whisper`) - serve solo per trascrivere senza un provider cloud.
- **Embedding** (`embeddings`, il servizio che usa più memoria) - serve solo per la ricerca semantica dei file. Vedi [Ricerca semantica](admin/semantic-search.md).
- **ComfyUI, il modello decisionale e Fabric** - funzioni GPU e di calcolo remoto dietro i profili Compose `gpu` e `fabric`. Non partono se non richiedi quei profili.
- **Grafana** - dashboard dietro il profilo `observability`.

## Licenza di Remotion {#remotion-licence}

ReelBolt genera i video con [Remotion](https://www.remotion.dev). Remotion non è software open source: è source-available con una propria licenza, che la licenza di ReelBolt non copre.

- La Free License di Remotion copre singoli individui, organizzazioni a scopo di lucro fino a tre persone (tre incluse), organizzazioni non profit e chiunque lo stia valutando. Un'organizzazione di più di tre persone (quattro o più) ha bisogno di una Company License. L'opzione "Remotion for Automators" di Remotion vale solo per chi ha una Company License.
- Quando ospiti ReelBolt in autonomia esegui Remotion tu stesso, quindi **tu** sei il licenziatario di Remotion per quell'installazione. Verifica quale livello si applica alla tua organizzazione su <https://www.remotion.dev/docs/license> prima di usare ReelBolt a scopo commerciale. Il servizio ospitato funziona con una propria licenza Remotion; non si estende alla tua installazione.
- ReelBolt non permette a nessuno di caricare un proprio progetto Remotion per farne il render. I termini di Remotion vietano di offrire un servizio che lo faccia.
- Il codice che gli agenti di ReelBolt scrivono è codice Remotion. Se lo scarichi o lo condividi, è soggetto anche alla licenza di Remotion, e chiunque lo riceva deve verificare quale livello si applica a sé. Il codice Remotion che esporti o scarichi deve riportare un avviso "built with Remotion" che rimanda a <https://www.remotion.dev/docs/license>. L'avviso riguarda il codice, non i video che genera.

*Remotion ha confermato al responsabile del progetto che questo uso è coperto dalla sua licenza gratuita. È una dichiarazione del responsabile, non un documento pubblicato da Remotion.*

## Se una build non riesce a scaricare i pacchetti {#if-a-build-fails-to-download-packages}

Errori come `DNS: transient error` da `apk`, o `NU1301` da `dotnet restore`, durante `docker compose build` significano che i container di build di Docker non riescono a risolvere i nomi, cosa che accade quando l'unico server DNS dell'host è un indirizzo IPv6. Crea un ambiente di build con server DNS espliciti e usalo:

```bash
printf '[dns]\n  nameservers=["1.1.1.1","8.8.8.8"]\n' > /tmp/buildkitd.toml
docker buildx create --name reelbolt-builder --driver docker-container \
  --driver-opt network=host --buildkitd-config /tmp/buildkitd.toml
BUILDX_BUILDER=reelbolt-builder docker compose build
docker compose up -d
```

Per tutto il resto, vedi [Risoluzione dei problemi](troubleshooting.md).
