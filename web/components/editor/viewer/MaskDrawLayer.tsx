'use client';

import React, { useEffect, useRef, useState } from 'react';
import type { MediaClip, ShapeMask } from '@/lib/editing/types';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';
import { commitFrameOps, drawBoxFromDrag, frameDraftFor, maskFromBox, maskOps, type ShapeBox } from '@/lib/editing/viewer-transform';
import { DEFAULT_SHAPE_MASK } from '../panels/MaskReframeFields';
import { useT } from '@/i18n';

/** A press that moves less than this (CSS px) is a click, not a drag: it writes nothing. */
const DRAG_THRESHOLD_PX = 4;

export interface MaskDrawLayerProps {
  store: EditorStore;
  clip: MediaClip;
  /** What a drawn box becomes: the chosen shape for a new mask, or the existing mask (feather, invert kept) for a redraw. */
  base: ShapeMask;
  /** True when this replaces an existing mask. */
  replace: boolean;
  /** Called after a mask is written (the tool leaves redraw mode). */
  onDone: () => void;
}

/**
 * T-012: the draw layer of the mask tool. It owns only the press-drag-release gesture on the empty picture;
 * the handles of an existing mask are a separate layer (`FrameHandlesOverlay`'s targets), so the corner pin
 * tool (its own layer) stays apart from this. A drag drafts the box live through the frame
 * draft store (the sliders follow) and writes ONE `setProperty mask` on release. A click writes nothing;
 * Escape during a drag cancels with no write; Enter (no mask yet) writes the default mask.
 */
export function MaskDrawLayer({ store, clip, base, replace, onDone }: MaskDrawLayerProps) {
  const tx = useT('editor.viewer.maskTool');
  const ref = useRef<HTMLDivElement | null>(null);
  const gesture = useRef<{ start: { x: number; y: number }; client: { x: number; y: number }; moved: boolean } | null>(null);
  const [box, setBox] = useState<ShapeBox | null>(null);
  const width = useEditorStore(store, (s) => s.doc.settings.width);
  const height = useEditorStore(store, (s) => s.doc.settings.height);
  const canvas = { width, height };
  const draftStore = frameDraftFor(store);

  useEffect(() => {
    ref.current?.focus({ preventScroll: true });
  }, []);

  const cancel = () => {
    if (!gesture.current) return;
    gesture.current = null;
    setBox(null);
    draftStore.set(null);
  };

  useEffect(() => {
    // Escape during a drag cancels the drag and nothing else: stopping the event here keeps the editor's global
    // handler (leave the tool, then deselect) from also running on the same press.
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && gesture.current) {
        e.preventDefault();
        e.stopPropagation();
        cancel();
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- cancel only touches refs, state setters and the stable draft store
  }, []);

  // A drag that outlives its layer (another chord, the playhead leaving the clip, a page change) must not leave
  // its draft behind: the fields would show a value nothing committed, and the next field edit would write it.
  useEffect(
    () => () => {
      if (gesture.current) {
        gesture.current = null;
        draftStore.set(null);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the draft store is stable per editor store
    [],
  );

  const toCanvas = (e: React.PointerEvent) => {
    const r = ref.current!.getBoundingClientRect();
    return { x: ((e.clientX - r.left) / r.width) * canvas.width, y: ((e.clientY - r.top) / r.height) * canvas.height };
  };
  const boxFor = (e: React.PointerEvent): ShapeBox => drawBoxFromDrag(gesture.current!.start, toCanvas(e), canvas, { keepAspect: e.shiftKey, fromCentre: e.altKey });

  const onPointerDown = (e: React.PointerEvent) => {
    if (e.button !== 0) return;
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    gesture.current = { start: toCanvas(e), client: { x: e.clientX, y: e.clientY }, moved: false };
    ref.current?.focus({ preventScroll: true });
  };
  const onPointerMove = (e: React.PointerEvent) => {
    const g = gesture.current;
    if (!g) return;
    if (!g.moved && Math.hypot(e.clientX - g.client.x, e.clientY - g.client.y) < DRAG_THRESHOLD_PX) return;
    g.moved = true;
    const next = boxFor(e);
    setBox(next);
    draftStore.set({ kind: 'mask', itemId: clip.id, shape: maskFromBox(next, base) });
  };
  const onPointerUp = (e: React.PointerEvent) => {
    const g = gesture.current;
    if (!g) return;
    const moved = g.moved;
    const next = moved ? boxFor(e) : null;
    gesture.current = null;
    setBox(null);
    draftStore.set(null);
    if (!next) return;
    if (commitFrameOps(store, maskOps(clip, maskFromBox(next, base)), replace ? tx('labelRedraw') : tx('labelDraw'))) onDone();
  };
  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key !== 'Enter' || replace || e.repeat) return;
    e.preventDefault();
    if (commitFrameOps(store, maskOps(clip, { ...DEFAULT_SHAPE_MASK, kind: base.kind }), tx('labelDraw'))) onDone();
  };

  return (
    <div
      ref={ref}
      role="application"
      tabIndex={0}
      aria-label={tx('drawSurface')}
      data-testid="viewer-mask-draw"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={cancel}
      onKeyDown={onKeyDown}
      style={{ position: 'absolute', inset: 0, zIndex: 5, cursor: 'var(--rb-cursor-mask-draw)', touchAction: 'none', outlineOffset: -2 }}
    >
      {box && (
        <div
          data-testid="viewer-mask-draw-box"
          style={{
            position: 'absolute',
            left: `${(box.x - box.w / 2) * 100}%`,
            top: `${(box.y - box.h / 2) * 100}%`,
            width: `${box.w * 100}%`,
            height: `${box.h * 100}%`,
            border: '2px dashed var(--mantine-color-yellow-4)',
            borderRadius: base.kind === 'ellipse' ? '50%' : 0,
            pointerEvents: 'none',
          }}
        />
      )}
    </div>
  );
}
