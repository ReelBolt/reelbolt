'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  deleteImageLayer,
  getImageDocument,
  isRevisionConflict,
  updateImageDocument,
  updateImageLayer,
  type WriteDelivery,
} from '@/lib/api/image-documents';
import { useT, type T } from '@/i18n';
import { applyWrite, diffDocuments, undoneLayers, type DocWrite } from '@/lib/editor/doc-sync';
import {
  canRedo,
  canUndo,
  clearHistory,
  emptyHistory,
  execute,
  layersAdded,
  layersReplaced,
  layersRemoved,
  redo,
  redoLabel,
  retainedLayerIds,
  undo,
  undoLabel,
  type Command,
  type HistoryStack,
} from '@/lib/editor/history';
import { newLayerIds } from '@/lib/editor/reconcile';
import { docFromDto, type EditorDocState } from '@/lib/editor/model';
import { versionedMatteUrl } from '@/lib/editor/layer-mask';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';

/** How long the editor waits after the last edit before it writes to the server. */
export const FLUSH_DELAY_MS = 700;

export type SaveStatus = 'saved' | 'unsaved' | 'saving' | 'error';

interface Model {
  /** What the canvas shows and the user edits. */
  doc: EditorDocState;
  /** What the server last confirmed. The difference between the two is what still has to be sent. */
  server: EditorDocState;
  history: HistoryStack;
}

export interface EditorDocumentApi {
  loading: boolean;
  loadError: string | null;
  doc: EditorDocState | null;
  /** The document's own name (not a layer's). */
  name: string | null;
  revision: number | null;
  saveStatus: SaveStatus;
  saveError: string | null;
  /** A one-line plain message about something that happened to the document (a conflict reload). */
  notice: string | null;
  dismissNotice(): void;
  canUndo: boolean;
  canRedo: boolean;
  undoLabel: string | null;
  redoLabel: string | null;
  undo(): void;
  redo(): void;
  /** Runs a command built from the CURRENT document (so it sees the latest state, not a stale closure). */
  commit(make: (doc: EditorDocState) => Command | null): void;
  /** Sends every pending edit now. Resolves true when nothing is left unsent. */
  flush(): Promise<boolean>;
  /**
   * Runs a server call that creates layers (a cut-out, an AI result, a text layer), then adopts the
   * server's document and records one undo step that hides them again. Pending edits are sent first,
   * so the server's stack and the local one agree about what the new layers sit on.
   *
   * `arrange`, when given, adjusts the new layers locally in the same undo step (e.g. fitting a large
   * picture to the canvas); the adjustment is written to the server by the next flush like any edit.
   */
  addLayers<T extends { id: string }>(
    label: string,
    call: () => Promise<T[]>,
    arrange?: (doc: EditorDocState, created: T[]) => EditorDocState,
  ): Promise<T[]>;
  /**
   * Like {@link addLayers}, but the layers in `replaced` leave the document in the same undo step (merge,
   * crop). Undo brings them back and takes the new ones out.
   */
  replaceLayers<T extends { id: string }>(
    label: string,
    replaced: readonly string[],
    call: () => Promise<T[]>,
    arrange?: (doc: EditorDocState, created: T[]) => EditorDocState,
  ): Promise<T[]>;
  /**
   * After a paid run failed or lost its connection: reads the document again and brings in any layer
   * that is not among `known` (the server finished late) as one undo step. Resolves with the new ids.
   */
  recoverLayers(label: string, known: readonly string[]): Promise<string[]>;
  /**
   * Keeps layers hidden WITHOUT an undo step (a result the server kept hidden because its layer was deleted):
   * adding layers shows them, and undo must not reveal such a result while it still waits for the user's OK.
   */
  holdHidden(ids: readonly string[]): void;
  /** Deletes layers as one undo step (the Delete key): undo brings them back. */
  removeLayers(ids: readonly string[], label: string): void;
  /** Deletes a layer on the server. Irreversible, so it clears the undo history. */
  deleteLayer(layerId: string): Promise<void>;
  /**
   * Brings in a change made on the server by someone other than this editor (the assistant, a
   * finished design job). Pending local edits are sent first; when that fails, nothing more happens
   * (a conflict has already reloaded the server's document with a notice, and a save error is shown
   * as one). Then the server's document is read and adopted: a change that only ADDED layers becomes
   * one undo step that hides them again, exactly like {@link addLayers}; any other change clears the
   * history, since the local steps may no longer apply to what the server now holds.
   */
  syncFromServer(change: ServerChange): Promise<ServerSyncResult>;
  reload(): Promise<void>;
  /**
   * Writes a layer's mask on the server (pending edits are sent first so the revision is current),
   * then records the new revision and the layer's new mask address without touching the undo history.
   * Resolves with that address (null when the mask was removed). A stale revision reloads the
   * document with a notice and rejects; any other failure rejects untouched.
   */
  writeMatte(
    layerId: string,
    call: (baseRevision: number | undefined) => Promise<{ matteUrl: string | null; revision: number | null }>,
    /** Called with the layer's new mask address just before the document shows it (to preload its pixels). */
    beforeAdopt?: (matteUrl: string | null) => void,
  ): Promise<string | null>;
}

/** A change made on the server, as {@link EditorDocumentApi.syncFromServer} takes it. */
export interface ServerChange {
  /** `layersAdded` is the only kind recorded as an undo step; every other kind clears the history. */
  kind: 'layersAdded' | 'layersChanged' | 'layersDeleted' | 'documentChanged';
  /** The layers the change touched (added, changed or deleted). */
  layerIds: readonly string[];
  /** The undo step's name, for an addition. */
  label: string;
}

export interface ServerSyncResult {
  /** False when pending edits could not be sent first, so nothing was brought in by this call. */
  synced: boolean;
  /** The touched layers that exist in the server's document now (a deleted one is not among them). */
  present: string[];
}

/** The document the editor shows: the server's, minus the layers the user has undone (held for redo). */
function shownWithout(doc: EditorDocState, prev: Model | null): EditorDocState {
  if (!prev) return doc;
  const gone = new Set(undoneLayers(prev.server, prev.doc).map((l) => l.id));
  return gone.size === 0 ? doc : { ...doc, layers: doc.layers.filter((l) => !gone.has(l.id)) };
}

const messageOf = (t: Pick<T, 'msg'>, cause: unknown, fallback: string) => humanizeErrorT(t, cause, fallback);

/**
 * The thumbnail editor's document: loads it, applies edits locally and instantly, keeps the undo
 * stack, and writes the difference to the server a moment after the last edit.
 *
 * See `lib/editor/doc-sync.ts` for why undo needs no server code, and `lib/editor/history.ts` for
 * what is undoable. The revision is sent as `baseRevision` on the FIRST write of each batch only:
 * every write bumps the document's revision, so a second write carrying the old one would collide
 * with the first write of the same batch.
 */
export function useEditorDocument(projectId: string, documentId: string): EditorDocumentApi {
  const t = useT('editor-thumbnail-a.document');
  const [model, setModel] = useState<Model | null>(null);
  const [name, setName] = useState<string | null>(null);
  const [revision, setRevision] = useState<number | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const modelRef = useRef<Model | null>(null);
  const revisionRef = useRef<number | null>(null);
  const inFlight = useRef<Promise<boolean> | null>(null);
  const syncFromServerRef = useRef<((change: ServerChange) => Promise<ServerSyncResult>) | null>(null);

  useEffect(() => {
    modelRef.current = model;
  }, [model]);

  const adopt = useCallback((fresh: Awaited<ReturnType<typeof getImageDocument>>) => {
    revisionRef.current = fresh.revision;
    setRevision(fresh.revision);
    setName(fresh.name);
  }, []);

  const open = useCallback(
    (fresh: Awaited<ReturnType<typeof getImageDocument>>) => {
      const doc = docFromDto(fresh);
      adopt(fresh);
      setModel({ doc, server: doc, history: emptyHistory });
      setLoadError(null);
      setSaveError(null);
    },
    [adopt],
  );

  const reload = useCallback(async () => {
    try {
      open(await getImageDocument(projectId, documentId));
    } catch (cause: unknown) {
      setLoadError(messageOf(t, cause, t('openFailed')));
    }
  }, [documentId, open, projectId, t]);

  // The first load. State is set only from the promise callbacks, never synchronously here.
  useEffect(() => {
    let alive = true;
    getImageDocument(projectId, documentId).then(
      (fresh) => {
        if (alive) open(fresh);
      },
      (cause: unknown) => {
        if (alive) setLoadError(messageOf(t, cause, t('openFailed')));
      },
    );
    return () => {
      alive = false;
    };
  }, [documentId, open, projectId, t]);

  const send = useCallback(
    async (write: DocWrite, baseRevision: number | undefined, delivery?: WriteDelivery) => {
      // The delivery is passed only when there is one, so an ordinary write is the same call as ever.
      const how: [WriteDelivery] | [] = delivery ? [delivery] : [];
      switch (write.kind) {
        case 'document':
          await updateImageDocument(projectId, documentId, { ...write.patch, baseRevision }, undefined, ...how);
          return;
        case 'move':
          await updateImageLayer(projectId, documentId, write.layerId, { zOrder: write.position, baseRevision }, ...how);
          return;
        case 'layer':
          await updateImageLayer(projectId, documentId, write.layerId, { ...write.patch, baseRevision }, ...how);
          return;
      }
    },
    [documentId, projectId],
  );

  const runFlush = useCallback(async (delivery?: WriteDelivery): Promise<boolean> => {
    const snapshot = modelRef.current;
    if (!snapshot) return true;
    // Undone layers that can no longer be redone are deleted on the server for good (before the diff,
    // so they are not hidden first).
    const kept = retainedLayerIds(snapshot.history);
    const gone = undoneLayers(snapshot.server, snapshot.doc).filter((l) => !kept.has(l.id));
    const leaving = delivery?.keepalive === true;
    const writes = (leaving ? (ws: DocWrite[]) => ws.filter((w) => !(w.kind === 'layer' && undoneLayers(snapshot.server, snapshot.doc).some((g) => g.id === w.layerId))) : (ws: DocWrite[]) => ws)(diffDocuments(
      gone.length > 0 ? { ...snapshot.server, layers: snapshot.server.layers.filter((l) => !gone.some((g) => g.id === l.id)) } : snapshot.server,
      snapshot.doc,
    ));
    if (writes.length === 0 && gone.length === 0) return true;

    setSaving(true);
    setSaveError(null);
    try {
      for (const layer of gone) {
        await deleteImageLayer(projectId, documentId, layer.id);
        setModel((prev) => (prev ? { ...prev, server: { ...prev.server, layers: prev.server.layers.filter((l) => l.id !== layer.id) } } : prev));
      }
      let base = gone.length > 0 ? undefined : revisionRef.current ?? undefined;
      if (gone.length > 0 && writes.length > 0) {
        base = (await getImageDocument(projectId, documentId)).revision;
        revisionRef.current = base;
      }
      for (const write of writes) {
        await send(write, base, delivery);
        base = undefined;
        setModel((prev) => (prev ? { ...prev, server: applyWrite(prev.server, write) } : prev));
      }
      // The writes bumped the revision; read the new one so the NEXT batch's first write is current.
      const fresh = await getImageDocument(projectId, documentId);
      adopt(fresh);
      return true;
    } catch (cause: unknown) {
      if (isRevisionConflict(cause)) {
        setNotice(t('changedElsewhere'));
        await reload();
      } else {
        setSaveError(messageOf(t, cause, t('saveFailed')));
      }
      return false;
    } finally {
      setSaving(false);
    }
  }, [adopt, documentId, projectId, reload, send, t]);

  const flushWith = useCallback(
    async (delivery?: WriteDelivery): Promise<boolean> => {
      // One flush at a time: a second caller waits for the running one, then runs its own pass,
      // which picks up whatever was edited in the meantime.
      if (inFlight.current) await inFlight.current.catch(() => false);
      const run = runFlush(delivery);
      inFlight.current = run;
      try {
        return await run;
      } finally {
        if (inFlight.current === run) inFlight.current = null;
      }
    },
    [runFlush],
  );
  const flush = useCallback(() => flushWith(), [flushWith]);

  // Leaving must not lose the last edit (it used to, inside the save delay): going to another page
  // of the app unmounts the editor, which sends what is pending at once; a hidden tab (the phone's
  // app switcher) does the same; and a page being closed or reloaded fires every pending write
  // straight away. All of them go out with `keepalive`, so the browser finishes them after the page
  // is gone.
  const leaveRef = useRef({ flushWith, send, ids: { projectId, documentId } });
  useEffect(() => {
    leaveRef.current = { flushWith, send, ids: { projectId, documentId } };
  }, [flushWith, send, projectId, documentId]);
  useEffect(() => {
    const pending = () => {
      const snapshot = modelRef.current;
      return snapshot ? diffDocuments(snapshot.server, snapshot.doc) : [];
    };
    const onHidden = () => {
      if (document.visibilityState === 'hidden' && pending().length > 0) {
        void leaveRef.current.flushWith({ keepalive: true });
      }
    };
    const dropUndone = () => {
      // The redo steps die with the page, so an undone layer the server still holds goes now.
      const snapshot = modelRef.current;
      if (!snapshot) return;
      for (const layer of undoneLayers(snapshot.server, snapshot.doc)) {
        void Promise.resolve(
          deleteImageLayer(leaveRef.current.ids.projectId, leaveRef.current.ids.documentId, layer.id, { keepalive: true }),
        ).catch(() => undefined);
      }
    };
    const onPageHide = () => {
      dropUndone();
      // No time to wait for answers: every write is sent now, the first carrying the revision.
      const base = revisionRef.current ?? undefined;
      const ghosts = new Set(modelRef.current ? undoneLayers(modelRef.current.server, modelRef.current.doc).map((l) => l.id) : []);
      // A layer being deleted is not also patched: two writers on one document race.
      pending().filter((w) => !(w.kind === 'layer' && ghosts.has(w.layerId))).forEach((write, i) => {
        void leaveRef.current.send(write, i === 0 ? base : undefined, { keepalive: true }).catch(() => undefined);
      });
    };
    document.addEventListener('visibilitychange', onHidden);
    window.addEventListener('pagehide', onPageHide);
    return () => {
      document.removeEventListener('visibilitychange', onHidden);
      window.removeEventListener('pagehide', onPageHide);
      dropUndone();
      if (pending().length > 0) void leaveRef.current.flushWith({ keepalive: true });
    };
  }, []);

  const dirty = useMemo(
    () =>
      model
        ? diffDocuments(model.server, model.doc).length > 0 ||
          (() => {
            const kept = retainedLayerIds(model.history);
            return undoneLayers(model.server, model.doc).some((l) => !kept.has(l.id));
          })()
        : false,
    [model],
  );
  const pendingDoc = model?.doc;

  useEffect(() => {
    if (!dirty) return;
    const timer = setTimeout(() => {
      void flush();
    }, FLUSH_DELAY_MS);
    return () => clearTimeout(timer);
    // `pendingDoc` is a new object on every edit, so the countdown restarts with each one; it is
    // not read inside, it is the trigger.
  }, [dirty, pendingDoc, flush]);

  const commit = useCallback((make: (doc: EditorDocState) => Command | null) => {
    const now = Date.now();
    setModel((prev) => {
      if (!prev) return prev;
      const command = make(prev.doc);
      if (!command) return prev;
      const result = execute(prev.history, prev.doc, command, now);
      return { ...prev, doc: result.state, history: result.history };
    });
  }, []);

  const undoStep = useCallback(() => {
    setModel((prev) => {
      if (!prev || !canUndo(prev.history)) return prev;
      const result = undo(prev.history, prev.doc);
      return { ...prev, doc: result.state, history: result.history };
    });
  }, []);

  const redoStep = useCallback(() => {
    setModel((prev) => {
      if (!prev || !canRedo(prev.history)) return prev;
      const result = redo(prev.history, prev.doc);
      return { ...prev, doc: result.state, history: result.history };
    });
  }, []);

  const addOrReplace = useCallback(
    async <T extends { id: string }>(
      label: string,
      replaced: readonly string[],
      call: () => Promise<T[]>,
      arrange?: (doc: EditorDocState, created: T[]) => EditorDocState,
    ): Promise<T[]> => {
      if (!(await flush())) {
        throw new Error(t('flushNotRun'));
      }
      const created = await call();
      const fresh = await getImageDocument(projectId, documentId);
      const next = docFromDto(fresh);
      const local = arrange ? arrange(next, created) : next;
      adopt(fresh);
      const now = Date.now();
      setModel((prev) => {
        const history = prev?.history ?? emptyHistory;
        const step =
          replaced.length > 0
            ? layersReplaced(replaced, created.map((l) => l.id), label)
            : layersAdded(created.map((l) => l.id), label);
        const result = execute(history, shownWithout(local, prev), step, now);
        return { doc: result.state, server: next, history: result.history };
      });
      return created;
    },
    [adopt, documentId, flush, projectId, t],
  );
  const addLayers = useCallback(
    <T extends { id: string }>(label: string, call: () => Promise<T[]>, arrange?: (doc: EditorDocState, created: T[]) => EditorDocState) =>
      addOrReplace(label, [], call, arrange),
    [addOrReplace],
  );
  const replaceLayers = useCallback(
    <T extends { id: string }>(label: string, replaced: readonly string[], call: () => Promise<T[]>, arrange?: (doc: EditorDocState, created: T[]) => EditorDocState) =>
      addOrReplace(label, replaced, call, arrange),
    [addOrReplace],
  );

  const recoverLayers = useCallback(
    async (label: string, known: readonly string[]): Promise<string[]> => {
      const fresh = await getImageDocument(projectId, documentId);
      const found = newLayerIds(known, fresh.layers);
      if (found.length === 0) return [];
      const outcome = await syncFromServerRef.current?.({ kind: 'layersAdded', layerIds: found, label });
      return outcome?.present ?? [];
    },
    [documentId, projectId],
  );

  const removeLayers = useCallback((ids: readonly string[], label: string) => {
    const now = Date.now();
    setModel((prev) => {
      if (!prev || !ids.some((id) => prev.doc.layers.some((l) => l.id === id))) return prev;
      const result = execute(prev.history, prev.doc, layersRemoved(ids, label), now);
      return { ...prev, doc: result.state, history: result.history };
    });
  }, []);

  const holdHidden = useCallback((ids: readonly string[]) => {
    setModel((prev) => {
      if (!prev || !prev.doc.layers.some((l) => ids.includes(l.id) && l.visible)) return prev;
      return { ...prev, doc: { ...prev.doc, layers: prev.doc.layers.map((l) => (ids.includes(l.id) ? { ...l, visible: false } : l)) } };
    });
  }, []);

  const deleteLayer = useCallback(
    async (layerId: string) => {
      if (!(await flush())) {
        throw new Error(t('flushNotRemoved'));
      }
      await deleteImageLayer(projectId, documentId, layerId);
      const fresh = await getImageDocument(projectId, documentId);
      const next = docFromDto(fresh);
      adopt(fresh);
      setModel((prev) => ({ doc: shownWithout(next, prev), server: next, history: clearHistory() }));
    },
    [adopt, documentId, flush, projectId, t],
  );

  const syncFromServer = useCallback(
    async (change: ServerChange): Promise<ServerSyncResult> => {
      if (!(await flush())) return { synced: false, present: [] };
      const fresh = await getImageDocument(projectId, documentId);
      const next = docFromDto(fresh);
      const present = change.layerIds.filter((id) => next.layers.some((l) => l.id === id));
      adopt(fresh);
      const now = Date.now();
      setModel((prev) => {
        if (change.kind === 'layersAdded') {
          const history = prev?.history ?? emptyHistory;
          if (present.length === 0) return { doc: shownWithout(next, prev), server: next, history };
          const result = execute(history, shownWithout(next, prev), layersAdded(present, change.label), now);
          return { doc: result.state, server: next, history: result.history };
        }
        return { doc: shownWithout(next, prev), server: next, history: clearHistory() };
      });
      return { synced: true, present };
    },
    [adopt, documentId, flush, projectId],
  );

  useEffect(() => {
    syncFromServerRef.current = syncFromServer;
  }, [syncFromServer]);
  const writeMatte = useCallback(
    async (
      layerId: string,
      call: (baseRevision: number | undefined) => Promise<{ matteUrl: string | null; revision: number | null }>,
      beforeAdopt?: (matteUrl: string | null) => void,
    ): Promise<string | null> => {
      if (!(await flush())) throw new Error(t('flush'));
      let result: { matteUrl: string | null; revision: number | null };
      try {
        result = await call(revisionRef.current ?? undefined);
      } catch (cause: unknown) {
        if (isRevisionConflict(cause)) {
          setNotice(t('changedElsewhereMask'));
          await reload();
        }
        throw cause;
      }
      let revision = result.revision;
      if (revision === null) {
        const fresh = await getImageDocument(projectId, documentId);
        adopt(fresh);
        revision = fresh.revision;
      } else {
        revisionRef.current = revision;
        setRevision(revision);
      }
      const url = result.matteUrl ? versionedMatteUrl(result.matteUrl, revision) : null;
      beforeAdopt?.(url);
      const apply = (state: EditorDocState): EditorDocState => ({
        ...state,
        layers: state.layers.map((l) => (l.id === layerId ? { ...l, matteUrl: url } : l)),
      });
      setModel((prev) => (prev ? { ...prev, doc: apply(prev.doc), server: apply(prev.server) } : prev));
      return url;
    },
    [adopt, documentId, flush, projectId, reload, t],
  );

  const history = model?.history;
  const saveStatus: SaveStatus = saving ? 'saving' : saveError ? 'error' : dirty ? 'unsaved' : 'saved';

  return {
    loading: model === null && loadError === null,
    loadError,
    doc: model?.doc ?? null,
    name,
    revision,
    saveStatus,
    saveError,
    notice,
    dismissNotice: () => setNotice(null),
    canUndo: history ? canUndo(history) : false,
    canRedo: history ? canRedo(history) : false,
    undoLabel: history ? undoLabel(history) : null,
    redoLabel: history ? redoLabel(history) : null,
    undo: undoStep,
    redo: redoStep,
    commit,
    flush,
    addLayers,
    replaceLayers,
    recoverLayers,
    removeLayers,
    holdHidden,
    deleteLayer,
    syncFromServer,
    reload,
    writeMatte,
  };
}
