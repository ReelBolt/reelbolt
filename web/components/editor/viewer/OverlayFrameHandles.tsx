'use client';

import React, { useEffect, useRef, useState } from 'react';
import {
  BOX_HANDLES,
  NUDGE_FRACTION,
  NUDGE_FRACTION_BIG,
  featherFromPointer,
  featherHandlePoint,
  featherInsetPx,
  moveFrameBy,
  nudgeFrame,
  resizeFrame,
  type Frame,
  type HandleSpec,
  type Vec,
} from '@/lib/editing/viewer-transform';
import { HANDLE_CORE, HANDLE_HALO, HANDLE_HIT_COARSE, HANDLE_HIT_FINE } from './handle-style';
import { useT } from '@/i18n';
import { msg } from '@/i18n/msg';

/** The value a gesture drafts and commits: the frame in canvas pixels, plus the feather for shapes that have one. */
export type FrameValue = { frame: Frame; feather: number | null };

export interface OverlayFrameHandlesProps {
  /** Test id prefix, e.g. `viewer-mask`. */
  idPrefix: string;
  /** What the user is moving, for aria labels ("Mask", "Power window", "Reframe point"). */
  label: string;
  color: string;
  kind: 'rect' | 'ellipse' | 'point';
  /** The shape in canvas pixels (for a point only cx, cy matter). */
  value: FrameValue;
  canvas: { width: number; height: number };
  /** Screen pixels per canvas pixel. */
  k: number;
  /** The overlay's box in screen pixels (handles are kept inside it). */
  size: { w: number; h: number };
  coarse: boolean;
  readOnly: boolean;
  /** The overlay's root element, for pointer to canvas conversion. */
  rootRef: React.RefObject<HTMLElement | null>;
  /** While true the shape has a dimmed surround (the reframe crop preview): the frame to keep lit. */
  dimOutside?: Frame | null;
  onDraft: (next: FrameValue) => void;
  /** `null` = the gesture was cancelled. */
  onCommit: (next: FrameValue | null, label: string) => void;
  onActiveChange?: (active: boolean) => void;
}

type Gesture =
  | { kind: 'move'; start: FrameValue; startPointer: Vec; moved: boolean }
  | { kind: 'resize'; handle: HandleSpec; start: FrameValue; moved: boolean }
  | { kind: 'feather'; start: FrameValue; moved: boolean }
  | { kind: 'point'; start: FrameValue; moved: boolean };

type GestureInit = { kind: 'move'; startPointer: Vec } | { kind: 'resize'; handle: HandleSpec } | { kind: 'feather' } | { kind: 'point' };

const HANDLE_NAMES: Record<string, string> = { tl: 'top left corner', tr: 'top right corner', br: 'bottom right corner', bl: 'bottom left corner', t: 'top edge', r: 'right edge', b: 'bottom edge', l: 'left edge' };
const CURSORS: Record<string, string> = { tl: 'nwse-resize', br: 'nwse-resize', tr: 'nesw-resize', bl: 'nesw-resize', t: 'ns-resize', b: 'ns-resize', l: 'ew-resize', r: 'ew-resize' };

const ARROWS: Record<string, Vec> = { ArrowLeft: { x: -1, y: 0 }, ArrowRight: { x: 1, y: 0 }, ArrowUp: { x: 0, y: -1 }, ArrowDown: { x: 0, y: 1 } };

/** Keeps a handle's hit box inside the overlay so a handle on the picture's edge never covers the controls beside the viewer. */
const clampBox = (start: number, extent: number, hit: number) => Math.max(0, Math.min(start, Math.max(0, extent - hit)));

type DotProps = {
  id: string;
  at: Vec;
  aria: string;
  cursor: string;
  round?: boolean;
  onDown: (e: React.PointerEvent) => void;
  onKey: (e: React.KeyboardEvent) => void;
  box: { hit: number; size: { w: number; h: number }; color: string; idPrefix: string; onMove: (e: React.PointerEvent) => void; onUp: (e: React.PointerEvent) => void; onCancel: (e: React.PointerEvent) => void };
};

function Dot({ id, at, aria, cursor, round = false, onDown, onKey, box }: DotProps) {
  const tm = useT();
  const { hit, size, color, idPrefix } = box;
  return (
    <div
      data-testid={`${idPrefix}-handle-${id}`}
      role="button"
      tabIndex={0}
      aria-roledescription={tm.msg(msg('editor-helpers-2.fmt.dragHandle'))}
      aria-label={aria}
      onPointerDown={onDown}
      onPointerMove={box.onMove}
      onPointerUp={box.onUp}
      onPointerCancel={box.onCancel}
      onKeyDown={onKey}
      style={{ position: 'absolute', left: clampBox(at.x - hit / 2, size.w, hit), top: clampBox(at.y - hit / 2, size.h, hit), width: hit, height: hit, display: 'flex', alignItems: 'center', justifyContent: 'center', cursor, pointerEvents: 'auto', touchAction: 'none' }}
    >
      <span style={{ width: 10, height: 10, background: HANDLE_CORE, border: `2px solid ${color}`, borderRadius: round ? '50%' : 2, boxShadow: `0 0 0 1.5px ${HANDLE_HALO}` }} />
    </div>
  );
}

/**
 * R10.3: the generic on-canvas handle layer, shared by the mask, the reframe point and the power window.
 * A rectangle / ellipse gets a draggable body, eight handles and (with a feather) a dashed inner outline with
 * one handle; a point gets one crosshair. It owns only the pointer math: the host drafts the value into the
 * preview and writes ONE batch on release (`onCommit`). Pointer events (mouse and touch), `touch-action: none`,
 * 44 px handles on coarse pointers, arrow keys nudge 1% (10% with Shift).
 */
export function OverlayFrameHandles(p: OverlayFrameHandlesProps) {
  const tx = useT('editor.viewer.overlayFrameHandles');
  const { idPrefix, label, color, kind, value, canvas, k, size, coarse, readOnly, rootRef } = p;
  const gesture = useRef<Gesture | null>(null);
  const [active, setActive] = useState(false);
  const [focused, setFocused] = useState(false);
  const hit = coarse ? HANDLE_HIT_COARSE : HANDLE_HIT_FINE;
  const f = value.frame;

  // The latest props, for the window listener and the unmount cleanup (both live longer than one render).
  const latest = useRef(p);
  useEffect(() => {
    latest.current = p;
  });

  const cancelGesture = () => {
    if (!gesture.current) return;
    gesture.current = null;
    setActive(false);
    latest.current.onActiveChange?.(false);
    latest.current.onCommit(null, '');
  };

  // Escape during a drag cancels the drag and nothing else: stopping the event keeps the editor's global handler
  // (leave the tool, then deselect) from also running on the same press. The release afterwards writes nothing.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && gesture.current) {
        e.preventDefault();
        e.stopPropagation();
        cancelGesture();
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, []);

  // A drag that outlives its handles (another chord, the playhead leaving the clip) must not leave its draft: the
  // fields would show a value nothing committed and the next field edit would write it.
  useEffect(
    () => () => {
      if (gesture.current) {
        gesture.current = null;
        latest.current.onActiveChange?.(false);
        latest.current.onCommit(null, '');
      }
    },
    [],
  );

  const toCanvas = (e: React.PointerEvent): Vec => {
    const r = rootRef.current!.getBoundingClientRect();
    return { x: ((e.clientX - r.left) / r.width) * canvas.width, y: ((e.clientY - r.top) / r.height) * canvas.height };
  };

  const begin = (e: React.PointerEvent, g: GestureInit) => {
    if (readOnly || k === 0) return;
    e.stopPropagation();
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    gesture.current = { ...g, start: value, moved: false };
    setActive(true);
    p.onActiveChange?.(true);
  };

  const onMove = (e: React.PointerEvent) => {
    const g = gesture.current;
    if (!g) return;
    const pt = toCanvas(e);
    let next: FrameValue = g.start;
    if (g.kind === 'move') next = { ...g.start, frame: moveFrameBy(g.start.frame, pt.x - g.startPointer.x, pt.y - g.startPointer.y, canvas) };
    else if (g.kind === 'resize') next = { ...g.start, frame: resizeFrame(g.start.frame, g.handle, pt, canvas, { keepAspect: e.shiftKey, fromCentre: e.altKey }) };
    else if (g.kind === 'feather') next = { ...g.start, feather: featherFromPointer(g.start.frame, kind === 'ellipse' ? 'ellipse' : 'rect', canvas, pt) };
    else next = { ...g.start, frame: { ...g.start.frame, cx: Math.min(canvas.width, Math.max(0, pt.x)), cy: Math.min(canvas.height, Math.max(0, pt.y)) } };
    g.moved = true;
    p.onDraft(next);
  };

  const end = (e: React.PointerEvent, cancel: boolean) => {
    const g = gesture.current;
    if (!g) return;
    gesture.current = null;
    setActive(false);
    p.onActiveChange?.(false);
    if (cancel || !g.moved) {
      p.onCommit(null, '');
      return;
    }
    // The last drafted value is the one on screen; the host re-reads it from its draft store on commit.
    const pt = toCanvas(e);
    let next: FrameValue = g.start;
    if (g.kind === 'move') next = { ...g.start, frame: moveFrameBy(g.start.frame, pt.x - g.startPointer.x, pt.y - g.startPointer.y, canvas) };
    else if (g.kind === 'resize') next = { ...g.start, frame: resizeFrame(g.start.frame, g.handle, pt, canvas, { keepAspect: e.shiftKey, fromCentre: e.altKey }) };
    else if (g.kind === 'feather') next = { ...g.start, feather: featherFromPointer(g.start.frame, kind === 'ellipse' ? 'ellipse' : 'rect', canvas, pt) };
    else next = { ...g.start, frame: { ...g.start.frame, cx: Math.min(canvas.width, Math.max(0, pt.x)), cy: Math.min(canvas.height, Math.max(0, pt.y)) } };
    const what = g.kind === 'move' ? `Move ${label.toLowerCase()}` : g.kind === 'resize' ? `Resize ${label.toLowerCase()}` : g.kind === 'feather' ? `Soften ${label.toLowerCase()}` : `Move ${label.toLowerCase()}`;
    p.onCommit(next, what);
  };

  const nudgeBody = (e: React.KeyboardEvent) => {
    if (readOnly || gesture.current) return;
    const d = ARROWS[e.key];
    if (!d) return;
    e.preventDefault();
    e.stopPropagation();
    const step = e.shiftKey ? NUDGE_FRACTION_BIG : NUDGE_FRACTION;
    p.onCommit({ ...value, frame: nudgeFrame(f, d.x * step, d.y * step, canvas) }, `Nudge ${label.toLowerCase()}`);
  };

  const nudgeHandle = (e: React.KeyboardEvent, handle: HandleSpec) => {
    if (readOnly || gesture.current) return;
    const d = ARROWS[e.key];
    if (!d) return;
    e.preventDefault();
    e.stopPropagation();
    const step = e.shiftKey ? NUDGE_FRACTION_BIG : NUDGE_FRACTION;
    const at: Vec = { x: f.cx + handle.local.x * f.w + d.x * step * canvas.width, y: f.cy + handle.local.y * f.h + d.y * step * canvas.height };
    p.onCommit({ ...value, frame: resizeFrame(f, handle, at, canvas) }, `Resize ${label.toLowerCase()}`);
  };

  const screen = (v: Vec): Vec => ({ x: v.x * k, y: v.y * k });
  const hitBox = { hit, size, color, idPrefix, onMove, onUp: (e: React.PointerEvent) => end(e, false), onCancel: (e: React.PointerEvent) => end(e, true) };
  const showDim = !!p.dimOutside && (active || focused);
  const inset = value.feather != null && kind !== 'point' ? featherInsetPx(f, canvas, value.feather) : 0;
  const rounded = kind === 'ellipse' ? '50%' : 0;

  return (
    <div
      data-testid={`${idPrefix}-layer`}
      role="group"
      tabIndex={0}
      aria-label={readOnly ? tx('readOnly', { label }) : tx('handlesArrowKeysNudge', { label })}
      onKeyDown={nudgeBody}
      onFocus={() => setFocused(true)}
      onBlur={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setFocused(false);
      }}
      style={{ position: 'absolute', inset: 0, outline: 'none', pointerEvents: 'none', touchAction: 'none' }}
    >
      {showDim && p.dimOutside && (
        <div data-testid={`${idPrefix}-dim`} style={{ position: 'absolute', inset: 0, overflow: 'hidden', pointerEvents: 'none' }}>
          <div style={{ position: 'absolute', left: (p.dimOutside.cx - p.dimOutside.w / 2) * k, top: (p.dimOutside.cy - p.dimOutside.h / 2) * k, width: p.dimOutside.w * k, height: p.dimOutside.h * k, boxShadow: '0 0 0 9999px rgba(0,0,0,0.55)', border: `1px dashed ${color}`, boxSizing: 'border-box' }} />
        </div>
      )}
      {kind !== 'point' && (
        <>
          <div
            data-testid={`${idPrefix}-body`}
            onPointerDown={(e) => begin(e, { kind: 'move', startPointer: toCanvas(e) })}
            onPointerMove={onMove}
            onPointerUp={(e) => end(e, false)}
            onPointerCancel={(e) => end(e, true)}
            style={{ position: 'absolute', left: (f.cx - f.w / 2) * k, top: (f.cy - f.h / 2) * k, width: f.w * k, height: f.h * k, border: `1.5px solid ${color}`, borderRadius: rounded, boxShadow: `0 0 0 1px ${HANDLE_HALO}`, cursor: readOnly ? 'var(--rb-cursor-default)' : 'var(--rb-cursor-move)', pointerEvents: 'auto', touchAction: 'none', boxSizing: 'border-box' }}
          />
          {value.feather != null && inset > 0 && (
            <div
              data-testid={`${idPrefix}-feather-outline`}
              style={{ position: 'absolute', left: (f.cx - f.w / 2 + inset) * k, top: (f.cy - f.h / 2 + inset) * k, width: Math.max(0, f.w - 2 * inset) * k, height: Math.max(0, f.h - 2 * inset) * k, border: `1px dashed ${color}`, borderRadius: rounded, pointerEvents: 'none', boxSizing: 'border-box' }}
            />
          )}
          {!readOnly && BOX_HANDLES.map((h) => (
            <Dot key={h.id} id={h.id} box={hitBox} at={screen({ x: f.cx + h.local.x * f.w, y: f.cy + h.local.y * f.h })} aria={tx('resizeFromThe', { v1: label.toLowerCase(), v2: HANDLE_NAMES[h.id] })} cursor={CURSORS[h.id]} onDown={(e) => begin(e, { kind: 'resize', handle: h })} onKey={(e) => nudgeHandle(e, h)} />
          ))}
          {!readOnly && value.feather != null && (
            <Dot
              id="feather"
              box={hitBox}
              round
              at={screen(featherHandlePoint(f, kind === 'ellipse' ? 'ellipse' : 'rect', canvas, value.feather))}
              aria={tx('softenTheEdgeOf', { v1: label.toLowerCase() })}
              cursor="ew-resize"
              onDown={(e) => begin(e, { kind: 'feather' })}
              onKey={(e) => {
                const d = ARROWS[e.key];
                if (!d || d.x === 0) return;
                e.preventDefault();
                e.stopPropagation();
                const step = e.shiftKey ? 0.05 : 0.01;
                p.onCommit({ ...value, feather: Math.min(0.5, Math.max(0, Math.round(((value.feather ?? 0) - d.x * step) * 10000) / 10000)) }, `Soften ${label.toLowerCase()}`);
              }}
            />
          )}
        </>
      )}
      {kind === 'point' && (
        <>
          <div aria-hidden style={{ position: 'absolute', left: f.cx * k, top: 0, bottom: 0, width: 0, borderLeft: `1px dashed ${color}`, pointerEvents: 'none', opacity: 0.8 }} />
          <div aria-hidden style={{ position: 'absolute', top: f.cy * k, left: 0, right: 0, height: 0, borderTop: `1px dashed ${color}`, pointerEvents: 'none', opacity: 0.8 }} />
          <Dot
            id="point"
            box={hitBox}
            round
            at={screen({ x: f.cx, y: f.cy })}
            aria={readOnly ? label : tx('dragToSetWhere', { v1: label.toLowerCase() })}
            cursor="crosshair"
            onDown={(e) => begin(e, { kind: 'point' })}
            onKey={(e) => {
              if (readOnly) return;
              const d = ARROWS[e.key];
              if (!d) return;
              e.preventDefault();
              e.stopPropagation();
              const step = e.shiftKey ? NUDGE_FRACTION_BIG : NUDGE_FRACTION;
              p.onCommit({ ...value, frame: { ...f, cx: Math.min(canvas.width, Math.max(0, f.cx + d.x * step * canvas.width)), cy: Math.min(canvas.height, Math.max(0, f.cy + d.y * step * canvas.height)) } }, `Nudge ${label.toLowerCase()}`);
            }}
          />
        </>
      )}
    </div>
  );
}
