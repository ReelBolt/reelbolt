'use client';

import React, { useEffect, useRef, useState } from 'react';
import type { MediaClip } from '@/lib/editing/types';
import type { EditorStore } from '@/lib/editing/store/editor-store';
import { useEditorStore } from '@/lib/editing/store/use-editor-store';
import { transformAt } from '@/lib/editing/viewer-transform';
import { canvasDragToSourceBox, canvasToSourceMapper } from '@/lib/editing/follow/map';
import { mediaRefOf, sourceTimeSec } from '@/lib/editing/preview';
import { canvasPointToSourcePoint, invertMapper, moveSeedTo, nudgeSeed, resizeSeedCorner, seedFromBox, seedFromTap, type Corner, type ObjectSeed } from '@/lib/editing/selection/seed';
import { Button } from '@mantine/core';
import { useCoarsePointer } from '../thumbnail/useCoarsePointer';
import { disarmFollowBox, getFollowHost, registerFollowStore, useFollowHost } from '@/lib/editing/follow/follow-host';
import { useT } from '@/i18n';

/** A press that moves less than this (CSS px) is a click, not a drag. A finger wobbles more than a mouse. */
const DRAG_THRESHOLD_PX = 4;
const TOUCH_DRAG_THRESHOLD_PX = 10;
const thresholdFor = (pointerType: string) => (pointerType === 'touch' || pointerType === 'pen' ? TOUCH_DRAG_THRESHOLD_PX : DRAG_THRESHOLD_PX);

type Point = { x: number; y: number };

/**
 * Follow subject: registers the viewer's store for the Follow section and, while the section has armed a box,
 * draws a one-shot rectangle drag over the picture. The drag writes nothing to the document: it hands the box
 * (fractions of the source frame, through the clip's fit and transform at the playhead) back to the section.
 */
export function FollowTool({ store, sourceSizeFor, readOnly }: { store: EditorStore; sourceSizeFor: (clip: MediaClip) => { width: number; height: number } | null; readOnly: boolean }) {
  useEffect(() => registerFollowStore(store), [store]);
  const { armed } = useFollowHost();
  if (!armed || readOnly) return null;
  return <FollowDrawLayer store={store} clipId={armed.clipId} sourceSizeFor={sourceSizeFor} picker={Boolean(armed.onSeed)} />;
}

function FollowDrawLayer({ store, clipId, sourceSizeFor, picker }: { store: EditorStore; clipId: string; sourceSizeFor: (clip: MediaClip) => { width: number; height: number } | null; picker: boolean }) {
  const t = useT('editor.viewer.followTool');
  const coarse = useCoarsePointer();
  const handle = coarse ? 44 : 24;
  const [draft, setDraft] = useState<{ seed: ObjectSeed; frame: number } | null>(null);
  const markerRef = useRef<HTMLButtonElement | null>(null);
  const lastCanvas = useRef<Point | null>(null);
  const [dragging, setDragging] = useState<{ kind: 'marker' } | { kind: 'corner'; corner: Corner } | null>(null);
  const ref = useRef<HTMLDivElement | null>(null);
  const gesture = useRef<{ start: Point; client: Point; moved: boolean; threshold: number } | null>(null);
  const [rect, setRect] = useState<{ a: Point; b: Point } | null>(null);
  const width = useEditorStore(store, (s) => s.doc.settings.width);
  const height = useEditorStore(store, (s) => s.doc.settings.height);

  useEffect(() => {
    // The drag works on a still picture.
    if (store.getState().ui.playbackSpeed !== 0) store.setUi({ playbackSpeed: 0 });
    ref.current?.focus({ preventScroll: true });
  }, [store]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      e.preventDefault();
      e.stopPropagation();
      disarmFollowBox();
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, []);

  const toCanvas = (e: React.PointerEvent): Point => {
    const r = ref.current!.getBoundingClientRect();
    return { x: ((e.clientX - r.left) / r.width) * width, y: ((e.clientY - r.top) / r.height) * height };
  };
  // A tap (or the keyboard's Enter / Space) at a canvas position: a marker and a default box. A miss is ignored; the help line stays.
  const placeTap = (at: Point) => {
    const state = store.getState();
    const frame = state.ui.playheadFrame;
    const clip = state.doc.tracks.flatMap((tr) => tr.items).find((i): i is MediaClip => i.id === clipId && i.type === 'mediaClip');
    const mediaRef = clip?.assetRef ? mediaRefOf(clip.assetRef) : null;
    if (!clip || !mediaRef) return;
    const point = canvasPointToSourcePoint(clip, transformAt(clip, frame), at, { width, height }, sourceSizeFor(clip), state.doc.settings.aspectPolicy);
    if (!point) return;
    lastCanvas.current = at;
    setDraft({ seed: seedFromTap(mediaRef, sourceTimeSec(clip, frame, state.doc.settings.rate), point), frame });
    window.setTimeout(() => markerRef.current?.focus({ preventScroll: true }), 0);
  };
  const onSurfaceKey = (e: React.KeyboardEvent) => {
    if (!picker || draft || e.target !== e.currentTarget || (e.key !== 'Enter' && e.key !== ' ')) return;
    e.preventDefault();
    e.stopPropagation();
    placeTap(lastCanvas.current ?? { x: width / 2, y: height / 2 });
  };
  const onPointerDown = (e: React.PointerEvent) => {
    if (e.button !== 0) return;
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    gesture.current = { start: toCanvas(e), client: { x: e.clientX, y: e.clientY }, moved: false, threshold: thresholdFor(e.pointerType) };
  };
  const onPointerMove = (e: React.PointerEvent) => {
    const g = gesture.current;
    if (!g) return;
    if (!g.moved && Math.hypot(e.clientX - g.client.x, e.clientY - g.client.y) < g.threshold) return;
    g.moved = true;
    setRect({ a: g.start, b: toCanvas(e) });
  };
  const onPointerUp = (e: React.PointerEvent) => {
    const g = gesture.current;
    gesture.current = null;
    setRect(null);
    if (!g) return;
    if (!picker && !g.moved) return;
    const state = store.getState();
    const frame = state.ui.playheadFrame;
    const clip = state.doc.tracks.flatMap((tr) => tr.items).find((i): i is MediaClip => i.id === clipId && i.type === 'mediaClip');
    if (picker) {
      const mediaRef = clip?.assetRef ? mediaRefOf(clip.assetRef) : null;
      if (!clip || !mediaRef) return;
      const at = toCanvas(e);
      const canvas = { width, height };
      const values = transformAt(clip, frame);
      const atSec = sourceTimeSec(clip, frame, state.doc.settings.rate);
      let seed: ObjectSeed | null = null;
      if (g.moved) {
        const box = canvasDragToSourceBox(clip, values, g.start, at, canvas, sourceSizeFor(clip), state.doc.settings.aspectPolicy);
        seed = box ? seedFromBox(mediaRef, atSec, box) : null;
      } else {
        placeTap(at);
        return;
      }
      if (seed) {
        setDraft({ seed, frame });
        window.setTimeout(() => markerRef.current?.focus({ preventScroll: true }), 0);
      }
      return;
    }
    if (!g.moved) return;
    // Read the arm at release: the section may have been re-armed or cancelled during the drag.
    const arm = getFollowHost().armed;
    if (!clip || !arm || arm.clipId !== clipId) return;
    const box = canvasDragToSourceBox(clip, transformAt(clip, frame), g.start, toCanvas(e), { width, height }, sourceSizeFor(clip), state.doc.settings.aspectPolicy);
    disarmFollowBox();
    if (box) arm.onBox?.(box, frame);
  };

  // Picker: where the draft sits on the canvas. The mapping is the clip's own, at the frame the tap was made on.
  const mapping = (() => {
    if (!picker || !draft) return null;
    const state = store.getState();
    const clip = state.doc.tracks.flatMap((tr) => tr.items).find((i): i is MediaClip => i.id === clipId && i.type === 'mediaClip');
    if (!clip) return null;
    const toSource = canvasToSourceMapper(clip, transformAt(clip, draft.frame), { width, height }, sourceSizeFor(clip), state.doc.settings.aspectPolicy);
    const toCanvasPt = toSource ? invertMapper(toSource) : null;
    return toSource && toCanvasPt ? { toSource, toCanvas: toCanvasPt } : null;
  })();
  const confirm = () => {
    if (!draft) return;
    const arm = getFollowHost().armed;
    disarmFollowBox();
    if (arm && arm.clipId === clipId) arm.onSeed?.(draft.seed, draft.frame);
  };
  const toLocal = (e: React.PointerEvent): Point => {
    const r = ref.current!.getBoundingClientRect();
    return { x: ((e.clientX - r.left) / r.width) * width, y: ((e.clientY - r.top) / r.height) * height };
  };
  const dragMove = (e: React.PointerEvent) => {
    const d = dragging;
    if (!d || !mapping || !draft) return;
    const p = mapping.toSource(toLocal(e));
    setDraft({ ...draft, seed: d.kind === 'marker' ? moveSeedTo(draft.seed, p) : resizeSeedCorner(draft.seed, d.corner, p) });
  };
  const startDrag = (kind: { kind: 'marker' } | { kind: 'corner'; corner: Corner }) => (e: React.PointerEvent) => {
    e.stopPropagation();
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    setDragging(kind);
  };
  const pct = (p: Point) => ({ left: `${(p.x / width) * 100}%`, top: `${(p.y / height) * 100}%` });
  const draftBox = (() => {
    if (!mapping || !draft?.seed.box) return null;
    const b = draft.seed.box;
    const pts = ([[b.x, b.y], [b.x + b.w, b.y], [b.x, b.y + b.h], [b.x + b.w, b.y + b.h]] as const).map(([x, y]) => mapping.toCanvas({ x, y }));
    const xs = pts.map((q) => q.x);
    const ys = pts.map((q) => q.y);
    return { x0: Math.min(...xs), x1: Math.max(...xs), y0: Math.min(...ys), y1: Math.max(...ys) };
  })();
  const markerAt = mapping && draft ? mapping.toCanvas(draft.seed.point) : null;
  const onMarkerKey = (e: React.KeyboardEvent) => {
    if (!draft) return;
    const step: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
    const s = step[e.key];
    if (s) {
      e.preventDefault();
      e.stopPropagation();
      const k = e.shiftKey ? 10 : 1;
      setDraft({ ...draft, seed: nudgeSeed(draft.seed, s[0] * k, s[1] * k) });
    } else if (e.key === 'Enter') {
      e.preventDefault();
      e.stopPropagation();
      confirm();
    }
  };

  return (
    <div
      ref={ref}
      role="application"
      tabIndex={0}
      aria-label={picker ? t('pickSurface') : t('drawSurface')}
      data-testid="viewer-follow-draw"
      data-picker={picker ? 'true' : undefined}
      onKeyDown={onSurfaceKey}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={() => {
        gesture.current = null;
        setRect(null);
      }}
      style={{ position: 'absolute', inset: 0, zIndex: 5, cursor: 'var(--rb-cursor-mask-draw)', touchAction: 'none', outlineOffset: -2 }}
    >
      {rect && (
        <div
          data-testid="viewer-follow-draw-box"
          style={{
            position: 'absolute',
            left: `${(Math.min(rect.a.x, rect.b.x) / width) * 100}%`,
            top: `${(Math.min(rect.a.y, rect.b.y) / height) * 100}%`,
            width: `${(Math.abs(rect.a.x - rect.b.x) / width) * 100}%`,
            height: `${(Math.abs(rect.a.y - rect.b.y) / height) * 100}%`,
            border: '2px dashed var(--mantine-color-yellow-4)',
            pointerEvents: 'none',
          }}
        />
      )}
      {picker && (
        <>
          <div
            data-testid="viewer-pick-help"
            style={{ position: 'absolute', top: 6, left: 0, right: 0, textAlign: 'center', pointerEvents: 'none' }}
          >
            <span style={{ background: 'rgba(0,0,0,0.65)', color: '#fff', fontSize: 13, padding: '2px 8px', borderRadius: 4 }}>{t('help')}</span>
          </div>
          {draftBox && (
            <div
              data-testid="viewer-pick-box"
              style={{ position: 'absolute', left: `${(draftBox.x0 / width) * 100}%`, top: `${(draftBox.y0 / height) * 100}%`, width: `${((draftBox.x1 - draftBox.x0) / width) * 100}%`, height: `${((draftBox.y1 - draftBox.y0) / height) * 100}%`, border: '2px solid var(--mantine-color-yellow-4)', pointerEvents: 'none' }}
            />
          )}
          {draftBox &&
            ([['nw', draftBox.x0, draftBox.y0], ['ne', draftBox.x1, draftBox.y0], ['sw', draftBox.x0, draftBox.y1], ['se', draftBox.x1, draftBox.y1]] as const).map(([corner, x, y]) => (
              <div
                key={corner}
                role="presentation"
                data-testid={`viewer-pick-corner-${corner}`}
                onPointerDown={startDrag({ kind: 'corner', corner })}
                onPointerMove={dragMove}
                onPointerUp={() => setDragging(null)}
                style={{ position: 'absolute', ...pct({ x, y }), width: handle, height: handle, marginLeft: -handle / 2, marginTop: -handle / 2, touchAction: 'none', zIndex: 6, display: 'grid', placeItems: 'center' }}
              >
                <span style={{ width: 12, height: 12, background: 'var(--mantine-color-yellow-4)', border: '1px solid #000', borderRadius: 2 }} />
              </div>
            ))}
          {markerAt && (
            <button
              ref={markerRef}
              type="button"
              aria-label={t('markerLabel')}
              data-testid="viewer-pick-marker"
              onPointerDown={startDrag({ kind: 'marker' })}
              onPointerMove={dragMove}
              onPointerUp={() => setDragging(null)}
              onKeyDown={onMarkerKey}
              style={{ position: 'absolute', ...pct(markerAt), width: handle, height: handle, marginLeft: -handle / 2, marginTop: -handle / 2, touchAction: 'none', zIndex: 7, padding: 0, background: 'transparent', border: 'none', cursor: 'var(--rb-cursor-move)', display: 'grid', placeItems: 'center' }}
            >
              <span aria-hidden style={{ width: 16, height: 16, borderRadius: '50%', background: 'var(--mantine-color-yellow-4)', border: '2px solid #000' }} />
            </button>
          )}
          <div style={{ position: 'absolute', bottom: 8, left: 0, right: 0, display: 'flex', justifyContent: 'center', gap: 8, zIndex: 8 }} onPointerDown={(e) => e.stopPropagation()}>
            {draft && (
              <Button size="xs" fz="xs" style={coarse ? { minHeight: 44 } : undefined} onClick={confirm} data-testid="viewer-pick-confirm">
                {t('confirm')}
              </Button>
            )}
            <Button size="xs" fz="xs" variant="default" style={coarse ? { minHeight: 44 } : undefined} onClick={() => disarmFollowBox()} data-testid="viewer-pick-cancel">
              {t('cancel')}
            </Button>
          </div>
        </>
      )}
    </div>
  );
}
