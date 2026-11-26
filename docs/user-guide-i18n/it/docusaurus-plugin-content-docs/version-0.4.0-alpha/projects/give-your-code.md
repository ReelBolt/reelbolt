---
title: Dai il tuo codice a ReelBolt
sidebar_position: 0
description: Tre modi per dare a un workflow "dal tuo codice" il sorgente della tua app, caricare uno zip, collegare GitHub o sincronizzare una cartella del computer, e cosa fare quando uno zip non si apre.
i18n_source_sha: 22a23c6dd1c25f734e6261941e200c5a93e91a90
---

# Dai il tuo codice a ReelBolt {#give-reelbolt-your-code}

Un workflow come "Video promo dal tuo codice" legge i file sorgente della tua app per costruire il video. Puoi dargli quei file in tre modi.

| Modo | Ideale per |
|---|---|
| Carica uno `.zip` | Un video una tantum da una copia del tuo codice |
| [Collega GitHub](./github-repository.md) | Codice che cambia e vive su GitHub |
| [Sincronizza una cartella del computer](./local-folder-sync.md) | Codice che continui a modificare in locale |

## Carica uno zip {#upload-a-zip}

1. Comprimi la cartella del sorgente **senza `node_modules`**. Lo zip è più piccolo, si carica più in fretta e resta entro i limiti.
2. Apri il progetto e clicca **Carica file**, oppure trascina lo zip nella pagina del progetto.
3. Aspetta qualche istante. ReelBolt apre lo zip in una cartella con il suo nome. Uno zip chiamato `my-app.zip` diventa la cartella `my-app`. Se il nome è già usato, la cartella è `my-app (2)`.
4. Apri la scheda **File** e controlla la cartella.

Quando tutto è stato estratto, lo zip viene rimosso: vedi i file una sola volta e contano una sola volta nel tuo spazio. Se l'estrazione non riesce, lo zip resta e la scheda File mostra il motivo.

Se lo zip contiene una sola cartella di primo livello, ReelBolt usa direttamente il suo contenuto invece di aggiungere una seconda cartella con lo stesso nome.

### Cosa viene escluso {#what-is-left-out}

ReelBolt salta i file che un video non usa mai:

- Dipendenze e controllo versione: `node_modules`, `bower_components`, `.git`, `.svn`, `.hg`, `.venv`, `venv`, `.gradle`.
- Risultati di build e cache: `dist`, `build`, `out`, `.next`, `.nuxt`, `.output`, `.svelte-kit`, `coverage`, `target`, `bin`, `obj`, `.turbo`, `.cache`, `.parcel-cache`; `__pycache__` e `*.pyc` vengono sempre saltati. Uno zip che contiene solo file compilati, senza sorgente, li mantiene.
- Resti di sistema ed editor: `__MACOSX`, `.DS_Store`, `Thumbs.db`, file `._*`, `.idea`.

### Limiti {#limits}

| Limite | Valore |
|---|---|
| Dimensione dello zip | 64 MiB |
| File mantenuti | 2.000 |
| Dimensione totale dopo l'estrazione | 128 MiB |
| Dimensione di un singolo file | 32 MiB |

I file più grandi del limite per file vengono saltati. Uno zip che supera un altro limite non viene estratto. I file estratti devono anche stare nello spazio del tuo piano.

### Se lo zip non si apre {#if-the-zip-does-not-unpack}

Lo zip resta nel progetto e la scheda File mostra il perché. Motivi comuni e rimedi:

- **Troppo grande o troppi file.** Comprimi di nuovo senza `node_modules`, cartelle di build e video o immagini pesanti che non servono.
- **Non è uno zip valido, o è protetto da password.** Crea di nuovo lo zip senza password.
- **Spazio insufficiente.** Libera spazio o passa a un piano superiore, poi riprova.

Per riprovare, apri la scheda **File**, premi **Riprova** nell'avviso che dice che lo zip non si è potuto decomprimere. Puoi anche eliminare lo zip e caricarne uno corretto.

:::tip Prima di un'esecuzione a pagamento
Un progetto il cui unico "codice" è uno zip che non si è aperto non è pronto. ReelBolt te lo dice prima dell'esecuzione, e il workflow si ferma senza addebitarti nulla se non trova sorgente leggibile.
:::

## Collega GitHub o sincronizza una cartella {#connect-github-or-sync-a-folder}

La sincronizzazione con [GitHub](./github-repository.md), [cloud drive](./cloud-drive-sync.md) e [cartella del computer](./local-folder-sync.md) copia i file così come sono. Non apre mai uno zip: uno zip dentro la cartella sincronizzata resta un solo file e il workflow non può leggerlo. Tieni il sorgente come normali file nella cartella sincronizzata, oppure carica lo zip direttamente nel progetto.

## Prossimi passi {#next}

- [Crea un progetto e carica i file](../basics/create-a-project-and-upload.md)
- [Crea un video promo dal codice della tua app](../recipes/promo-from-code.md)
