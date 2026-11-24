'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { createLayerFromFile } from '@/lib/api/image-documents';
import { uploadFileWithProgress } from '@/lib/api/files';
import {
  alignLayers,
  distributeLayers,
  setField,
  unionBox,
  type AlignMode,
  type DistributeAxis,
  type Move,
  type SizeField,
} from '@/lib/editor/arrange';
import { domTextMeasurer, renderMergedLayers } from '@/lib/editor/canvas-dom';
import { placeLayers, type BitmapMap, type PlacedLayer } from '@/lib/editor/compose';
import { croppedTransform, naturalSize, rectToCrop, type CropRect } from '@/lib/editor/crop';
import { patchLayer, patchLayers } from '@/lib/editor/history';
import type { EditorDocState, EditorLayer } from '@/lib/editor/model';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useT } from '@/i18n';
import type { AlignTarget } from './ArrangePanel';
import type { EditorDocumentApi } from './useEditorDocument';

const MAX_SIDE = 4096;
const NO_IDS: readonly string[] = [];

export interface ArrangeMessage {
  tone: 'info' | 'error' | 'warning';
  text: string;
}

interface Options {
  projectId: string;
  documentId: string;
  editor: EditorDocumentApi;
  layers: readonly EditorLayer[];
  bitmaps: BitmapMap;
  doc: EditorDocState | null;
  fontVersion: number;
  selectedId: string | null;
  onSelect(id: string | null): void;
  say(message: ArrangeMessage | null): void;
  /** Called after a file was added to the project (so the media list refreshes). */
  onFileAdded(): void;
  /** Asks before an edit that changes a layer a background job works on; runs `run` when it may go on. */
  guard?(layerId: string, run: () => void): void;
}

/** Everything the "Position and size / Line up / Crop and merge" section and the Delete key do. */
export function useArrange({ projectId, documentId, editor, layers, bitmaps, doc, fontVersion, selectedId, onSelect, say, onFileAdded, guard }: Options) {
  const t = useT('editor-thumbnail-b.arrange.messages');
  const [picking, setPicking] = useState(false);
  const [checked, setChecked] = useState<ReadonlySet<string>>(new Set());
  const [cropping, setCropping] = useState(false);
  const [busy, setBusy] = useState<'merge' | null>(null);
  const { commit, removeLayers } = editor;

  const placed = useMemo(
    () => (typeof document === 'undefined' || layers.length === 0 ? [] : placeLayers(layers, bitmaps, domTextMeasurer())),
    // The measurer reads fonts that load after first paint.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [layers, bitmaps, fontVersion],
  );

  // Ticked layers that no longer exist (deleted, undone) drop out.
  const liveChecked = useMemo(() => {
    const live = new Set(layers.map((l) => l.id));
    return new Set([...checked].filter((id) => live.has(id)));
  }, [checked, layers]);

  const targets = useMemo(() => {
    const ids: readonly string[] = picking && liveChecked.size > 0 ? [...liveChecked] : selectedId ? [selectedId] : NO_IDS;
    return placed.filter((l) => ids.includes(l.id));
  }, [placed, picking, liveChecked, selectedId]);
  const selectedPlaced: PlacedLayer | null = placed.find((l) => l.id === selectedId) ?? null;

  const toggleChecked = useCallback((id: string) => {
    setChecked((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);
  const setPickingOn = useCallback(
    (on: boolean) => {
      setPicking(on);
      // Turning it on starts from the selected layer, so "choose several" is one tick away from two.
      setChecked(on && selectedId ? new Set([selectedId]) : new Set());
    },
    [selectedId],
  );

  const applyMoves = (moves: Move[], label: string) => {
    if (moves.length === 0) {
      say({ tone: 'info', text: t('alreadyThere') });
      return;
    }
    commit((d) => patchLayers(d, moves.map((m) => ({ id: m.id, patch: { transform: m.transform } })), label));
  };

  const onField = (field: SizeField, value: number) => {
    if (!selectedPlaced) return;
    const transform = setField(selectedPlaced, field, value);
    if (!transform) return;
    const label = field === 'x' ? t('setLeft') : field === 'y' ? t('setTop') : field === 'width' ? t('setWidth') : t('setHeight');
    commit((d) => patchLayer(d, selectedPlaced.id, { transform }, label, `field-${selectedPlaced.id}-${field}`));
  };

  const onAlign = (mode: AlignMode, target: AlignTarget) => {
    if (!doc || targets.length === 0) return;
    const frame = target === 'canvas' ? { x: 0, y: 0, w: doc.canvasWidth, h: doc.canvasHeight } : unionBox(targets);
    if (!frame) return;
    const names: Record<AlignMode, string> = { left: t('alignLeft'), center: t('alignCentre'), right: t('alignRight'), top: t('alignTop'), middle: t('alignMiddle'), bottom: t('alignBottom') };
    applyMoves(alignLayers(targets, mode, frame), names[mode]);
  };

  const onDistribute = (axis: DistributeAxis) => {
    applyMoves(distributeLayers(targets, axis), axis === 'horizontal' ? t('spreadSideways') : t('spreadUpDown'));
  };

  /** A rendered picture becomes a project file, then a layer: the way an upload does. */
  const makeLayer = async (blob: Blob, name: string, label: string) => {
    // Working copies go in their own folder, named for what made them, so they are not mistaken for footage.
    const uploaded = await uploadFileWithProgress(projectId, new File([blob], name, { type: 'image/png' }), () => {}, {
      relativePath: `Picture working copies/${name}`,
    });
    onFileAdded();
    return createLayerFromFile(projectId, documentId, { projectFileId: uploaded.id, role: 'overlay', label });
  };

  /**
   * Crops in place: the layer keeps its stored picture and gets a crop window, with its position moved so
   * the kept part does not shift. One undo step; opening Crop again can widen it (or remove it).
   */
  const crop = (rect: CropRect) => {
    const layer = selectedPlaced;
    if (!layer || layer.kind === 'text') return;
    const next = rectToCrop(rect, naturalSize(layer));
    const transform = croppedTransform(layer, rect);
    commit((d) => patchLayer(d, layer.id, { crop: next, transform }, next ? t('crop') : t('removeCrop')));
    setCropping(false);
    say({
      tone: 'info',
      text: next ? t('cropped') : t('cropRemoved'),
    });
  };

  const runMerge = async () => {
    const chosen = targets.filter((l) => l.visible);
    const box = unionBox(chosen);
    if (chosen.length < 2 || !box) {
      say({ tone: 'warning', text: t('chooseTwo') });
      return;
    }
    setBusy('merge');
    say(null);
    try {
      const x = Math.floor(box.x);
      const y = Math.floor(box.y);
      const region = { x, y, w: Math.ceil(box.x + box.w) - x, h: Math.ceil(box.y + box.h) - y };
      const factor = Math.min(1, MAX_SIDE / Math.max(region.w, region.h));
      const ordered = [...chosen].sort((a, b) => a.zOrder - b.zOrder);
      const topId = ordered[ordered.length - 1].id;
      const blob = await renderMergedLayers(ordered, bitmaps, region, factor);
      const created = await editor.replaceLayers(
        t('mergeLayers'),
        chosen.map((l) => l.id),
        async () => [await makeLayer(blob, 'Picture working copy - merged layers.png', 'Merged layer')],
        (next, made) => {
          const id = made[0]?.id;
          const at = next.layers.findIndex((l) => l.id === topId);
          const fresh = next.layers.find((l) => l.id === id);
          if (!fresh || at < 0) return next;
          const rest = next.layers.filter((l) => l.id !== id);
          const merged: EditorLayer = {
            ...fresh,
            transform: { x: region.x, y: region.y, scale: 1 / factor, rotation: 0, flipX: false, flipY: false },
            opacity: 1,
            blendMode: 'normal',
            visible: true,
          };
          return { ...next, layers: [...rest.slice(0, at + 1), merged, ...rest.slice(at + 1)].map((l, i) => ({ ...l, zOrder: i + 1 })) };
        },
      );
      setChecked(new Set());
      setPicking(false);
      onSelect(created[0]?.id ?? null);
      say({ tone: 'info', text: t('merged') });
    } catch (cause: unknown) {
      say({ tone: 'error', text: humanizeErrorT(t, cause, t('mergeFailed')) });
    } finally {
      setBusy(null);
    }
  };

  // The Delete key removes the selected layer (or the ticked ones) as an undo step.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Delete' && e.key !== 'Backspace') return;
      if (e.ctrlKey || e.metaKey || e.altKey) return;
      const el = e.target as HTMLElement | null;
      if (el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName))) return;
      if (typeof document !== 'undefined' && document.querySelector('[role="dialog"]')) return;
      const ids = (picking && liveChecked.size > 0 ? [...liveChecked] : selectedId ? [selectedId] : []).filter((id) => {
        const l = layers.find((x) => x.id === id);
        return l && !l.locked;
      });
      if (ids.length === 0) return;
      e.preventDefault();
      const remove = () => {
        removeLayers(ids, ids.length > 1 ? t('deleteLayers') : t('deleteLayer'));
        setChecked(new Set());
        if (selectedId && ids.includes(selectedId)) onSelect(null);
        say({ tone: 'info', text: ids.length > 1 ? t('layersDeleted') : t('layerDeleted') });
      };
      // A layer a background job is working on asks first (one question per such layer).
      (guard ? ids.reduce<() => void>((next, id) => () => guard(id, next), remove) : remove)();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [guard, layers, liveChecked, onSelect, picking, removeLayers, say, selectedId, t]);

  /** Merging replaces the layers: one that a background job works on asks first (one question per such layer). */
  const merge = async () => {
    const busyFirst = targets.filter((l) => l.visible);
    if (!guard || busyFirst.length < 2) return runMerge();
    busyFirst.reduce<() => void>((next, l) => () => guard(l.id, next), () => void runMerge())();
  };

  return {
    picking,
    setPicking: setPickingOn,
    checked: liveChecked,
    toggleChecked,
    count: targets.length,
    selectedPlaced,
    cropping,
    openCrop: () => setCropping(true),
    closeCrop: () => setCropping(false),
    busy,
    onField,
    onAlign,
    onDistribute,
    crop,
    merge,
  };
}
