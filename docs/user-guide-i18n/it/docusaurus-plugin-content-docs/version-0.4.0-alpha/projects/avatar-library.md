---
title: La libreria degli avatar dello spazio di lavoro
sidebar_position: 6.5
description: Tieni persone e oggetti una sola volta per tutto lo spazio di lavoro e aggiungili a qualsiasi progetto. Chi può salvarli, modificarli ed eliminarli, che cosa copia l'importazione e che cosa succede quando il consenso viene revocato.
i18n_source_sha: 6717dcd557e2fafe552be97b6d9ef35d44852b6e
---
# La libreria degli avatar dello spazio di lavoro {#the-workspace-avatar-library}

La **Libreria degli avatar** contiene le persone e gli oggetti che il tuo spazio di lavoro tiene a disposizione di tutti i progetti. Un Proprietario o un Amministratore dello spazio di lavoro li salva lì da un progetto; poi qualsiasi membro può aggiungerli ai propri progetti. Un elemento che aggiungi a un progetto resta collegato alla libreria: nome, file e consenso si gestiscono in un solo posto, e una modifica arriva a tutti i progetti che lo usano.

La libreria fa parte degli [Avatar](avatars.md) ed è disponibile solo quando avatar o oggetti sono attivi nel tuo spazio di lavoro. Aprila da **Impostazioni > Libreria degli avatar**.

## Salvare una persona o un oggetto nella libreria {#saving-a-person-or-an-object-to-the-library}

Devi essere Proprietario o Amministratore dello spazio di lavoro.

1. Apri la scheda **Avatar** di un progetto e trova una persona o un oggetto **Pronto**.
2. Apri il suo menu e scegli **Salva nella libreria dello spazio di lavoro**.
3. Leggi la dichiarazione e confermala. Per una persona è un nuovo consenso, per tutto lo spazio di lavoro: confermi che la persona ha acconsentito all'uso del suo viso, del suo corpo e della sua voce per creare video e immagini con l'AI in qualsiasi progetto di questo spazio di lavoro. Per un oggetto confermi che i file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile, e che puoi usarli in qualsiasi progetto di questo spazio di lavoro. Per una persona scegli anche per quanto tempo conservarla.
4. Scegli **Salva nella libreria**.

L'avatar del progetto viene quindi collegato all'elemento della libreria e mostra il badge **Libreria dello spazio di lavoro**. La libreria può contenere fino a 100 persone e 100 oggetti, ognuno con un proprio nome. Un avatar che è stato a sua volta importato dalla libreria non si può salvare di nuovo.

## Aggiungere un elemento della libreria a un progetto {#adding-a-library-item-to-a-project}

Nella scheda **Avatar** del progetto scegli **Dalla libreria dello spazio di lavoro**. L'elenco mostra tutte le persone e gli oggetti della libreria, se si possono usare e se sono già in questo progetto. Scegli **Importa**.

- L'importazione copia i file dell'elemento tra i file del progetto, quindi contano nel tuo spazio di archiviazione.
- L'avatar importato è collegato: nome, file e consenso si gestiscono nella libreria. Nel progetto puoi scegliere con quale voce del progetto parla una persona (**Voce**) e **Rimuovi da questo progetto**.
- Valgono il tuo piano e i limiti del progetto, come per qualsiasi avatar o oggetto: un progetto contiene fino a 20 persone e 20 oggetti.
- Un elemento della libreria può essere in al massimo 50 progetti alla volta.

**Rimuovi da questo progetto** elimina le copie dei suoi file fatte per questo progetto. L'elemento resta nella libreria e in tutti gli altri progetti che lo usano, e puoi importarlo di nuovo in seguito. I video e le immagini già creati con esso restano nel progetto.

## Varianti nella libreria {#variants-in-the-library}

Una [variante](avatars.md#variants) che salvi nella libreria conserva la sua etichetta e ciò che la distingue, e compare sotto il suo originale, per esempio come **Auto · Rossa**.

- Salva prima l'originale, poi le sue varianti: una variante il cui originale è già nella libreria va sotto di esso. Se salvi prima la variante, compare da sola finché non salvi l'originale, poi si sposta sotto di esso.
- Un originale nella libreria può avere fino a 10 varianti, e ogni etichetta si usa una sola volta per originale. Se un altro progetto ha già salvato una variante "Rossa" dello stesso originale, cambia l'etichetta di questa variante nel suo progetto prima di salvarla.
- Quando importi una variante in un progetto che ha già il suo originale, lì va sotto l'originale, come una variante creata nel progetto, e non conta nei 20 del progetto. Se lì l'originale non ha più posto per un'altra variante, o un'altra sua variante usa già quell'etichetta, l'importazione viene rifiutata e non viene copiato nulla. Importata da sola, una variante è un normale avatar del progetto; se in seguito importi il suo originale, va sotto di esso.
- L'etichetta di una variante e ciò che la distingue arrivano con lei dalla libreria: un progetto che la importa non può modificarli.
- Eliminare un originale, o revocare il consenso di una persona, non tocca le sue varianti: ognuna ha i propri file e il proprio consenso, e quando l'originale non c'è più restano da sole nella libreria.

## Gestire la libreria {#managing-the-library}

In **Impostazioni > Libreria degli avatar** ogni membro vede gli elementi, il loro stato e in quanti progetti è usato ciascuno. Un Proprietario o un Amministratore può anche:

- **Rinomina o descrivi**: tutti i progetti che usano l'elemento mostrano il nuovo nome e la nuova descrizione.
- **Conferma di nuovo**: per una persona rinnova il consenso per tutto lo spazio di lavoro e fa ripartire da oggi la data di conservazione; per un oggetto conferma di nuovo la sua dichiarazione. I file restano gli stessi; per usare altri file, salva un nuovo elemento da un progetto.
- **Revoca il consenso** (una persona): la persona non si può più usare in nessun progetto dello spazio di lavoro, da subito. L'elemento e le sue copie nei progetti vengono eliminati automaticamente dopo 30 giorni, o prima se lo elimini. Non si può annullare.
- **Elimina**: rimuove l'elemento dalla libreria e da tutti i progetti che lo usano, con i file copiati lì, e poi ti dice che cosa è stato rimosso.

I video e le immagini già creati con un elemento restano nei loro progetti. Eliminali tu se la persona te lo chiede.

Quando la data di conservazione di una persona si avvicina, i Proprietari e gli Amministratori dello spazio di lavoro ricevono un'email una settimana prima. Se nessuno la conferma di nuovo, l'elemento viene eliminato dalla libreria e da tutti i progetti.

## Quando un elemento della libreria non si può usare {#when-a-library-item-cannot-be-used}

Un elemento della libreria, e ogni avatar di progetto collegato a esso, non si può usare quando:

- il suo consenso è stato revocato;
- la sua data di conservazione è passata (un Proprietario o un Amministratore può confermarlo di nuovo);
- in un progetto, uno dei suoi file copiati è stato eliminato o modificato: rimuovilo da quel progetto e importalo di nuovo.
