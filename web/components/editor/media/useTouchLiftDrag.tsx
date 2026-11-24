'use client';

/**
 * Wires {@link TouchLiftGesture} to one tile button (R05.4): long-press lifts the tile, a floating
 * chip follows the finger, and the timeline (listening on the window event) shows where it would
 * land and places it on release. Mouse and pen keep using native drag and drop.
 */

import React, { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import {
  TouchLiftGesture,
  emitPoolTouchDrag,
  subscribePoolTouchDrag,
  type PoolTouchDragDetail,
} from '@/lib/editing/media/touch-drag';
import type { MediaKind } from '@/lib/editing/media/types';

export interface TouchLiftDragOptions {
  mediaRef: string;
  kind: MediaKind;
}

const dragKind = (kind: MediaKind): PoolTouchDragDetail['kind'] => (kind === 'audio' || kind === 'image' ? kind : 'video');

export function useTouchLiftDrag(target: React.RefObject<HTMLButtonElement | null>, { mediaRef, kind }: TouchLiftDragOptions) {
  const liftedRef = useRef(false);
  const suppressClick = useRef(false);
  const lastPointerType = useRef<string>('mouse');
  const latest = useRef({ mediaRef, kind });
  useEffect(() => {
    latest.current = { mediaRef, kind };
  }, [mediaRef, kind]);

  const gestureRef = useRef<TouchLiftGesture | null>(null);
  useEffect(() => {
    const emit = (phase: PoolTouchDragDetail['phase'], clientX: number, clientY: number) =>
      emitPoolTouchDrag({ phase, mediaRef: latest.current.mediaRef, kind: dragKind(latest.current.kind), clientX, clientY });
    gestureRef.current = new TouchLiftGesture({
      onLift: (x, y) => {
        liftedRef.current = true;
        suppressClick.current = true;
        if (typeof navigator !== 'undefined' && 'vibrate' in navigator) navigator.vibrate(10);
        emit('lift', x, y);
      },
      onMove: (x, y) => {
        emit('move', x, y);
      },
      onDrop: (x, y) => {
        liftedRef.current = false;
        emit('drop', x, y);
      },
      onCancel: () => {
        liftedRef.current = false;
        emit('cancel', 0, 0);
      },
    });
    return () => gestureRef.current?.cancel();
  }, []);

  // The element is wired with native listeners, so the tile's own React props stay untouched. The
  // touchmove one must be non-passive to stop the page scrolling under a lifted finger.
  useEffect(() => {
    const el = target.current;
    if (!el) return;
    const gesture = () => gestureRef.current;
    const onPointerDown = (event: PointerEvent) => {
      lastPointerType.current = event.pointerType;
      if (event.pointerType !== 'touch') return;
      suppressClick.current = false;
      gesture()?.down(event.clientX, event.clientY);
    };
    const onPointerMove = (event: PointerEvent) => {
      if (event.pointerType === 'touch') gesture()?.move(event.clientX, event.clientY);
    };
    const onPointerUp = (event: PointerEvent) => {
      if (event.pointerType === 'touch') gesture()?.up(event.clientX, event.clientY);
    };
    const onPointerCancel = () => gesture()?.cancel();
    const onTouchMove = (event: TouchEvent) => {
      if (liftedRef.current && event.cancelable) event.preventDefault();
    };
    const onContextMenu = (event: Event) => {
      if (liftedRef.current || gesture()?.state === 'pressing') event.preventDefault();
    };
    // The click that follows a lift must not also select the tile.
    const onClickCapture = (event: Event) => {
      if (!suppressClick.current) return;
      suppressClick.current = false;
      event.stopPropagation();
      event.preventDefault();
    };
    // A finger's long press is the lift gesture; the browser's own drag must not start as well.
    const onDragStart = (event: Event) => {
      if (lastPointerType.current === 'touch') event.preventDefault();
    };
    el.addEventListener('pointerdown', onPointerDown);
    el.addEventListener('pointermove', onPointerMove);
    el.addEventListener('pointerup', onPointerUp);
    el.addEventListener('pointercancel', onPointerCancel);
    el.addEventListener('touchmove', onTouchMove, { passive: false });
    el.addEventListener('contextmenu', onContextMenu);
    el.addEventListener('click', onClickCapture, true);
    el.addEventListener('dragstart', onDragStart, true);
    return () => {
      el.removeEventListener('pointerdown', onPointerDown);
      el.removeEventListener('pointermove', onPointerMove);
      el.removeEventListener('pointerup', onPointerUp);
      el.removeEventListener('pointercancel', onPointerCancel);
      el.removeEventListener('touchmove', onTouchMove);
      el.removeEventListener('contextmenu', onContextMenu);
      el.removeEventListener('click', onClickCapture, true);
      el.removeEventListener('dragstart', onDragStart, true);
    };
  }, [target]);

  // Lets the tile's own gestures (the filmstrip scrub) stand down while a tile is lifted.
  return { isLifted: () => liftedRef.current };
}

/** The floating chip that follows the finger while a tile is lifted. */
export function TouchDragChip({ mediaRef, label }: { mediaRef: string; label: string }) {
  const [chip, setChip] = useState<{ x: number; y: number } | null>(null);
  useEffect(
    () =>
      subscribePoolTouchDrag((detail) => {
        if (detail.mediaRef !== mediaRef) return;
        setChip(detail.phase === 'lift' || detail.phase === 'move' ? { x: detail.clientX, y: detail.clientY } : null);
      }),
    [mediaRef],
  );
  if (!chip || typeof document === 'undefined') return null;
  return createPortal(
    <div
      data-testid="pool-touch-chip"
      aria-hidden
      style={{
        position: 'fixed',
        left: chip.x,
        top: chip.y,
        transform: 'translate(-50%, -120%)',
        pointerEvents: 'none',
        zIndex: 10000,
        maxWidth: 220,
        padding: '6px 10px',
        borderRadius: 8,
        fontSize: 12,
        background: 'var(--mantine-color-violet-6)',
        color: 'white',
        whiteSpace: 'nowrap',
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        boxShadow: '0 4px 14px rgba(0,0,0,0.35)',
      }}
    >
      {label}
    </div>,
    document.body,
  );
}
