'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ApiError } from '@/lib/api/client';
import { useT, type T } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { maskSaveMsg } from '@/lib/editor/labels-msg';
import { deleteImageLayerMatte, errorCodeOf, putImageLayerMatte } from '@/lib/api/image-documents';
import { matteAsGray, MaskSurface, maskFromMatte, maskToPng } from '@/lib/editor/canvas-dom';
import type { Bitmap, BitmapMap } from '@/lib/editor/compose';
import { LayerMaskBuffer, saveWithRetry, type MaskSaveState } from '@/lib/editor/layer-mask';
import type { EditorLayer } from '@/lib/editor/model';
import type { EditorDocumentApi } from './useEditorDocument';

export interface MaskBrushState {
  /** Brush diameter in CANVAS pixels (what the user sees). */
  size: number;
  /** 0 soft .. 1 hard. */
  hardness: number;
  /** True paints Hide (black); false paints Show (white). */
  hide: boolean;
}

export interface MaskSession {
  layerId: string;
  buffer: LayerMaskBuffer;
  surface: MaskSurface;
}

export interface UseLayerMaskEditingArgs {
  projectId: string;
  documentId: string;
  layers: readonly EditorLayer[];
  bitmaps: BitmapMap;
  writeMatte: EditorDocumentApi['writeMatte'];
  seed(url: string, bitmap: Bitmap): void;
  /** A plain sentence for the user (an error, or a confirmation). */
  onMessage(tone: 'error' | 'info', text: string): void;
  /** The paint tool should be armed (true) or put away (false). */
  onActive(active: boolean): void;
}

const statusOf = (cause: unknown): number | null => (cause instanceof ApiError ? cause.status : null);
const failureText = (t: T, cause: unknown, fallback: string) =>
  t.msg(maskSaveMsg(statusOf(cause), errorCodeOf(cause), describeError(t, cause, fallback)));

/** The pixel size a layer's mask must have: the server's, else the decoded picture's. */
export function maskSizeOf(layer: EditorLayer, bitmaps: BitmapMap): { w: number; h: number } | null {
  const image = bitmaps.get(layer.id)?.image;
  const w = layer.width ?? image?.width ?? 0;
  const h = layer.height ?? image?.height ?? 0;
  return w > 0 && h > 0 ? { w, h } : null;
}

/**
 * Everything about layer masks in the picture editor: the paint session (an offscreen buffer at the
 * layer's pixel size, in-session undo), saving it (one PUT, retried, never discarded until the server
 * answered), and the whole-mask actions that need no painting (add, invert, delete, show mask only).
 */
export function useLayerMaskEditing(args: UseLayerMaskEditingArgs) {
  const { projectId, documentId, layers, bitmaps, writeMatte, seed } = args;
  const t = useT('editor-thumbnail-a.maskEdit');
  const tAll = useT();
  const saveFallback = t('saveFailed');
  const layerLoading = t('layerLoading');
  const maskLoading = t('maskLoading');
  // The callbacks are replaced on every render by the caller; the latest pair is read through a ref so
  // the actions below keep a stable identity.
  const callbacks = useRef({ onMessage: args.onMessage, onActive: args.onActive });
  useEffect(() => {
    callbacks.current = { onMessage: args.onMessage, onActive: args.onActive };
  });
  const onMessage = useCallback((tone: 'error' | 'info', text: string) => callbacks.current.onMessage(tone, text), []);
  const onActive = useCallback((active: boolean) => callbacks.current.onActive(active), []);
  const [session, setSession] = useState<MaskSession | null>(null);
  const [tick, setTick] = useState(0);
  const [brush, setBrush] = useState<MaskBrushState>({ size: 60, hardness: 0.8, hide: true });
  const [saveState, setSaveState] = useState<MaskSaveState>({ phase: 'idle' });
  const [busyId, setBusyId] = useState<string | null>(null);
  const [maskOnlyId, setMaskOnlyId] = useState<string | null>(null);
  const sessionRef = useRef<MaskSession | null>(null);

  const open = useCallback(
    (layerId: string, buffer: LayerMaskBuffer) => {
      const next = { layerId, buffer, surface: new MaskSurface(buffer) };
      sessionRef.current = next;
      setSession(next);
      setSaveState({ phase: 'idle' });
      setTick((t) => t + 1);
      onActive(true);
    },
    [onActive],
  );

  const close = useCallback(() => {
    sessionRef.current = null;
    setSession(null);
    setSaveState({ phase: 'idle' });
    onActive(false);
  }, [onActive]);

  // A layer that disappears (deleted, undone) ends its session.
  useEffect(() => {
    const current = sessionRef.current;
    if (current && !layers.some((l) => l.id === current.layerId)) close();
  }, [layers, close]);

  /** Writes a buffer as the layer's mask. Rejects with the raw error (after setting a plain state). */
  const upload = useCallback(
    async (layerId: string, buffer: LayerMaskBuffer, surface: MaskSurface) => {
      const png = await maskToPng(buffer);
      return saveWithRetry({
        send: () =>
          writeMatte(
            layerId,
            async (base) => {
              const result = await putImageLayerMatte(projectId, documentId, layerId, png, base);
              return { matteUrl: result.layer?.matteUrl ?? null, revision: result.revision };
            },
            (url) => {
              if (url) seed(url, surface.alpha as unknown as Bitmap);
            },
          ),
        onState: setSaveState,
        statusOf,
        messageOf: (cause) => failureText(tAll, cause, saveFallback),
      });
    },
    [documentId, projectId, saveFallback, seed, tAll, writeMatte],
  );

  const needSize = useCallback(
    (layer: EditorLayer) => {
      const size = maskSizeOf(layer, bitmaps);
      if (!size) onMessage('error', layerLoading);
      return size;
    },
    [bitmaps, layerLoading, onMessage],
  );

  const addMask = useCallback(
    async (layer: EditorLayer) => {
      const size = needSize(layer);
      if (!size) return;
      const buffer = new LayerMaskBuffer(size.w, size.h);
      setBusyId(layer.id);
      try {
        const surface = new MaskSurface(buffer);
        await upload(layer.id, buffer, surface);
        buffer.markSaved();
        open(layer.id, buffer);
      } catch (cause) {
        onMessage('error', failureText(tAll, cause, saveFallback));
      } finally {
        setBusyId(null);
      }
    },
    [needSize, onMessage, open, saveFallback, tAll, upload],
  );

  const editMask = useCallback(
    (layer: EditorLayer) => {
      const matte = bitmaps.get(layer.id)?.matte;
      const buffer = matte ? maskFromMatte(matte) : null;
      if (!buffer) {
        onMessage('error', maskLoading);
        return;
      }
      open(layer.id, buffer);
    },
    [bitmaps, maskLoading, onMessage, open],
  );

  const invertMask = useCallback(
    async (layer: EditorLayer) => {
      const matte = bitmaps.get(layer.id)?.matte;
      const buffer = matte ? maskFromMatte(matte) : null;
      if (!buffer) {
        onMessage('error', maskLoading);
        return;
      }
      buffer.invert();
      setBusyId(layer.id);
      try {
        await upload(layer.id, buffer, new MaskSurface(buffer));
      } catch (cause) {
        onMessage('error', failureText(tAll, cause, saveFallback));
      } finally {
        setBusyId(null);
      }
    },
    [bitmaps, maskLoading, onMessage, saveFallback, tAll, upload],
  );

  const deleteMask = useCallback(
    async (layer: EditorLayer) => {
      setBusyId(layer.id);
      try {
        await writeMatte(layer.id, async (base) => {
          const result = await deleteImageLayerMatte(projectId, documentId, layer.id, base);
          return { matteUrl: null, revision: result.revision };
        });
        if (sessionRef.current?.layerId === layer.id) close();
        setMaskOnlyId((cur) => (cur === layer.id ? null : cur));
      } catch (cause) {
        onMessage('error', failureText(tAll, cause, saveFallback));
      } finally {
        setBusyId(null);
      }
    },
    [close, documentId, onMessage, projectId, saveFallback, tAll, writeMatte],
  );

  /** "Done": saves what was painted (one PUT) and closes. On failure the painting stays open. */
  const done = useCallback(async () => {
    const current = sessionRef.current;
    if (!current) return;
    if (!current.buffer.changed) {
      close();
      return;
    }
    try {
      await upload(current.layerId, current.buffer, current.surface);
      current.buffer.markSaved();
      close();
    } catch (cause) {
      onMessage('error', failureText(tAll, cause, saveFallback));
    }
  }, [close, onMessage, saveFallback, tAll, upload]);

  /** Closes without saving; the mask on the server is untouched. */
  const discard = useCallback(() => close(), [close]);

  const undo = useCallback(() => {
    const current = sessionRef.current;
    if (current?.buffer.undo()) {
      current.surface.sync();
      setTick((t) => t + 1);
    }
  }, []);

  const redo = useCallback(() => {
    const current = sessionRef.current;
    if (current?.buffer.redo()) {
      current.surface.sync();
      setTick((t) => t + 1);
    }
  }, []);

  /** Called by the canvas after every stroke. */
  const changed = useCallback(() => {
    sessionRef.current?.surface.sync();
    setTick((t) => t + 1);
  }, []);

  const toggleMaskOnly = useCallback((layerId: string) => setMaskOnlyId((cur) => (cur === layerId ? null : layerId)), []);

  const maskOnlyMatte = maskOnlyId && !session ? bitmaps.get(maskOnlyId)?.matte : undefined;
  const maskOnlyGray = useMemo(() => (maskOnlyMatte ? matteAsGray(maskOnlyMatte) : null), [maskOnlyMatte]);

  /** The bitmaps the canvas draws: the edited layer through its live mask, or a mask shown on its own. */
  const liveBitmaps = useMemo<BitmapMap>(() => {
    const showOnly = maskOnlyId;
    if (!session && !maskOnlyGray) return bitmaps;
    const map = new Map(bitmaps);
    if (session) {
      const base = bitmaps.get(session.layerId);
      if (base?.image) {
        map.set(
          session.layerId,
          showOnly === session.layerId
            ? { image: session.surface.gray as unknown as Bitmap }
            : { image: base.image, matte: session.surface.alpha as unknown as Bitmap },
        );
      }
    } else if (maskOnlyId && maskOnlyGray) {
      map.set(maskOnlyId, { image: maskOnlyGray });
    }
    return map;
  }, [bitmaps, maskOnlyGray, maskOnlyId, session]);

  return {
    session,
    tick,
    brush,
    setBrush,
    saveState,
    busyId,
    maskOnlyId,
    /** Unsaved painting: the page must not be closed on it. */
    unsaved: session !== null && session.buffer.changed,
    liveBitmaps,
    addMask,
    editMask,
    invertMask,
    deleteMask,
    toggleMaskOnly,
    done,
    discard,
    undo,
    redo,
    changed,
    canUndo: session?.buffer.canUndo ?? false,
    canRedo: session?.buffer.canRedo ?? false,
  };
}

// `tick` is read by callers so a stroke re-renders them; keep the export typed.
export type LayerMaskEditing = ReturnType<typeof useLayerMaskEditing>;
