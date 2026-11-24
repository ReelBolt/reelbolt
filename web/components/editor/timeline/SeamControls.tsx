'use client';

import React, { useRef, useState } from 'react';
import { Box, Popover, Text } from '@mantine/core';
import { IconPlus, IconTransitionRight } from '@tabler/icons-react';
import type { EditOp } from '@/lib/editing/engine';
import { findSeams, findTransitions, formatSeconds, framesToSeconds, transitionLabel } from '@/lib/editing/clip-depth';
import type { EditTimeline, MediaClip, Rational } from '@/lib/editing/types';
import { TransitionEditor, TransitionPicker } from '../panels/clip-depth/TransitionPicker';
import { frameToX, type Viewport } from './paint';
import { useT } from '@/i18n';
import { transitionLabelMsg } from '@/lib/editing/labels-msg';

export interface SeamControlsProps {
  doc: EditTimeline;
  viewport: Viewport;
  /** The track rows (`y`, `height`) as painted. */
  rows: readonly { track: { id: string }; y: number; height: number }[];
  /** The selected item, when exactly one: its seams hide (its trim handles own them) and its fade handles show. */
  selectedId: string | null;
  rate: Rational;
  coarse: boolean;
  readOnly: boolean;
  newId: () => string;
  /** Applies ops as ONE undo step. */
  onApply: (ops: EditOp[], label: string) => void;
}

const TARGET = { fine: 22, coarse: 44 } as const;
// Coarse: the fade handle clears the 44 px trim handle that is centred on the same edge (a finger on the trim handle must not grab the fade).
const FADE_INSET = { fine: 14, coarse: 46 } as const;

/**
 * R07.3/R07.4: DOM controls laid over the timeline canvas. A "+" on every cut between two media clips opens the
 * transition picker; an existing transition gets a glyph that opens its editor; the selected clip shows draggable
 * fade corners (a slider too: arrow keys move a frame). Nothing here mutates the document: it applies ops.
 */
export function SeamControls({ doc, viewport, rows, selectedId, rate, coarse, readOnly, newId, onApply }: SeamControlsProps) {
  const t = useT('editor-timeline.seam');
  const [openKey, setOpenKey] = useState<string | null>(null);
  const size = coarse ? TARGET.coarse : TARGET.fine;
  const rowOf = (trackId: string) => rows.find((r) => r.track.id === trackId);
  /** The glyphs sit on the lower edge of the row so a click on the middle of a clip next to a cut still reaches the clip. */
  const visible = (x: number) => x > -size && x < viewport.width + size;
  const containerRef = useRef<HTMLDivElement | null>(null);

  const seams = findSeams(doc);
  const transitions = findTransitions(doc);
  const selected = selectedId ? doc.tracks.flatMap((tr) => tr.items.map((i) => ({ item: i, trackId: tr.id }))).find((e) => e.item.id === selectedId) : undefined;
  const selectedClip = selected?.item.type === 'mediaClip' ? (selected.item as MediaClip) : null;

  return (
    <Box ref={containerRef} style={{ position: 'absolute', inset: 0, pointerEvents: 'none' }} data-testid="seam-controls">
      {seams.map((seam) => {
        const row = rowOf(seam.trackId);
        if (!row || row.height < 30) return null;
        if (seam.outgoing.id === selectedId || seam.incoming.id === selectedId) return null;
        const x = frameToX(seam.outgoing.recordOut, viewport);
        if (!visible(x)) return null;
        const key = `seam-${seam.outgoing.id}`;
        return (
          <Popover key={key} opened={openKey === key} onChange={(o) => setOpenKey(o ? key : null)} position="bottom" width={340} withArrow shadow="md" trapFocus returnFocus>
            <Popover.Target>
              <button
                type="button"
                aria-label={t('addAt', { time: formatSeconds(framesToSeconds(seam.outgoing.recordOut, rate)) })}
                title={t('add')}
                data-testid="seam-add"
                data-cut-frame={seam.outgoing.recordOut}
                disabled={readOnly}
                onClick={() => setOpenKey(openKey === key ? null : key)}
                onPointerDown={(e) => e.stopPropagation()}
                style={{
                  position: 'absolute',
                  left: x - size / 2,
                  top: row.y + row.height - size - 2,
                  width: size,
                  height: size,
                  padding: 0,
                  borderRadius: '50%',
                  border: '1px solid var(--mantine-color-blue-5)',
                  background: 'rgba(20, 20, 24, 0.82)',
                  color: 'var(--mantine-color-blue-3)',
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  cursor: 'var(--rb-cursor-pointer)',
                  pointerEvents: 'auto',
                  touchAction: 'manipulation',
                }}
              >
                <IconPlus size={coarse ? 20 : 14} aria-hidden />
              </button>
            </Popover.Target>
            <Popover.Dropdown data-testid="seam-picker">
              <Text size="sm" fw={600} mb={6}>
                {t('add')}
              </Text>
              <TransitionPicker doc={doc} seam={seam} rate={rate} readOnly={readOnly} newId={newId} onApply={onApply} onDone={() => setOpenKey(null)} />
            </Popover.Dropdown>
          </Popover>
        );
      })}

      {transitions.map(({ trackId, transition }) => {
        const row = rowOf(trackId);
        if (!row || row.height < 30) return null;
        const mid = Math.floor((transition.recordIn + transition.recordOut) / 2);
        const x = frameToX(mid, viewport);
        if (!visible(x)) return null;
        const key = `transition-${transition.id}`;
        return (
          <Popover key={key} opened={openKey === key} onChange={(o) => setOpenKey(o ? key : null)} position="bottom" width={340} withArrow shadow="md" trapFocus returnFocus>
            <Popover.Target>
              <button
                type="button"
                aria-label={t('edit', { name: t.msg(transitionLabelMsg(transition.transitionType)) })}
                title={t('editTitle', { name: t.msg(transitionLabelMsg(transition.transitionType)) })}
                data-testid="seam-edit"
                data-transition-id={transition.id}
                onClick={() => setOpenKey(openKey === key ? null : key)}
                onPointerDown={(e) => e.stopPropagation()}
                style={{
                  position: 'absolute',
                  left: x - size / 2,
                  top: row.y + row.height - size - 2,
                  width: size,
                  height: size,
                  padding: 0,
                  borderRadius: 6,
                  border: '1px solid var(--mantine-color-yellow-5)',
                  background: 'rgba(20, 20, 24, 0.82)',
                  color: 'var(--mantine-color-yellow-4)',
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  cursor: 'var(--rb-cursor-pointer)',
                  pointerEvents: 'auto',
                  touchAction: 'manipulation',
                }}
              >
                <IconTransitionRight size={coarse ? 22 : 14} aria-hidden />
              </button>
            </Popover.Target>
            <Popover.Dropdown data-testid="seam-editor">
              <Text size="sm" fw={600} mb={6}>
                {t.msg(transitionLabelMsg(transition.transitionType))}
              </Text>
              <TransitionEditor transition={transition} cutFrame={mid} readOnly={readOnly} onApply={onApply} onDone={() => setOpenKey(null)} />
            </Popover.Dropdown>
          </Popover>
        );
      })}

      {selectedClip && selected && !readOnly && (() => {
        const row = rowOf(selected.trackId);
        if (!row) return null;
        return (
          <>
            <FadeHandle which="in" clip={selectedClip} rowY={row.y} viewport={viewport} rate={rate} coarse={coarse} containerRef={containerRef} onApply={onApply} />
            <FadeHandle which="out" clip={selectedClip} rowY={row.y} viewport={viewport} rate={rate} coarse={coarse} containerRef={containerRef} onApply={onApply} />
          </>
        );
      })()}
    </Box>
  );
}

function FadeHandle({
  which,
  clip,
  rowY,
  viewport,
  rate,
  coarse,
  containerRef,
  onApply,
}: {
  which: 'in' | 'out';
  clip: MediaClip;
  rowY: number;
  viewport: Viewport;
  rate: Rational;
  coarse: boolean;
  containerRef: React.RefObject<HTMLDivElement | null>;
  onApply: (ops: EditOp[], label: string) => void;
}) {
  const t = useT('editor-timeline.seam');
  const [dragFrames, setDragFrames] = useState<number | null>(null);
  const duration = clip.recordOut - clip.recordIn;
  const own = which === 'in' ? (clip.fadeInFrames ?? 0) : (clip.fadeOutFrames ?? 0);
  const other = which === 'in' ? (clip.fadeOutFrames ?? 0) : (clip.fadeInFrames ?? 0);
  const room = Math.max(0, duration - other);
  const frames = dragFrames ?? own;
  const size = coarse ? TARGET.coarse : 18;
  const inset = coarse ? FADE_INSET.coarse : FADE_INSET.fine;
  const edgeX = frameToX(which === 'in' ? clip.recordIn : clip.recordOut, viewport);
  const offset = Math.max(frames * viewport.pixelsPerFrame, inset);
  const centreX = which === 'in' ? edgeX + offset : edgeX - offset;
  const key = which === 'in' ? 'fadeInFrames' : 'fadeOutFrames';
  const commit = (value: number) => {
    const next = Math.max(0, Math.min(room, Math.round(value)));
    if (next === own) return;
    onApply([{ type: 'setProperty', itemId: clip.id, key, value: next > 0 ? next : undefined }], which === 'in' ? t('fadeIn') : t('fadeOut'));
  };
  const frameAt = (clientX: number): number => {
    const left = containerRef.current?.getBoundingClientRect().left ?? 0;
    const x = clientX - left;
    return which === 'in' ? (x - edgeX) / viewport.pixelsPerFrame : (edgeX - x) / viewport.pixelsPerFrame;
  };
  const onDown = (event: React.PointerEvent<HTMLDivElement>) => {
    event.stopPropagation();
    event.preventDefault();
    try {
      event.currentTarget.setPointerCapture(event.pointerId);
    } catch {
      // The pointer is already gone; the drag ends on its own.
    }
    setDragFrames(own);
  };
  const onMove = (event: React.PointerEvent<HTMLDivElement>) => {
    if (dragFrames === null) return;
    setDragFrames(Math.max(0, Math.min(room, Math.round(frameAt(event.clientX)))));
  };
  const onUp = (event: React.PointerEvent<HTMLDivElement>) => {
    if (dragFrames === null) return;
    const value = Math.max(0, Math.min(room, Math.round(frameAt(event.clientX))));
    setDragFrames(null);
    commit(value);
  };
  const label = which === 'in' ? t('fadeIn') : t('fadeOut');
  return (
    <>
      {frames > 0 && (
        <svg
          aria-hidden
          style={{ position: 'absolute', left: which === 'in' ? edgeX : edgeX - frames * viewport.pixelsPerFrame, top: rowY + 2, width: frames * viewport.pixelsPerFrame, height: 22, pointerEvents: 'none' }}
          viewBox="0 0 100 100"
          preserveAspectRatio="none"
        >
          <polygon points={which === 'in' ? '0,0 100,0 0,100' : '0,0 100,0 100,100'} fill="rgba(255,255,255,0.35)" />
        </svg>
      )}
      <div
        role="slider"
        tabIndex={0}
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={room}
        aria-valuenow={frames}
        aria-valuetext={`${formatSeconds(framesToSeconds(frames, rate))}`}
        data-testid={`fade-handle-${which}`}
        data-touch-action="none"
        title={t('fadeTitle', { label, time: formatSeconds(framesToSeconds(frames, rate)) })}
        onPointerDown={onDown}
        onPointerMove={onMove}
        onPointerUp={onUp}
        onPointerCancel={() => setDragFrames(null)}
        onKeyDown={(e) => {
          if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
          e.preventDefault();
          e.stopPropagation();
          const dir = (e.key === 'ArrowRight' ? 1 : -1) * (which === 'in' ? 1 : -1);
          commit(own + dir);
        }}
        style={{
          position: 'absolute',
          left: centreX - size / 2,
          top: rowY + 2,
          width: size,
          height: size,
          display: 'flex',
          alignItems: 'flex-start',
          justifyContent: 'center',
          cursor: 'ew-resize',
          pointerEvents: 'auto',
          touchAction: 'none',
        }}
      >
        <div style={{ width: 12, height: 12, marginTop: coarse ? 2 : 0, borderRadius: '50%', background: 'var(--mantine-color-white)', border: '2px solid var(--mantine-color-blue-6)', pointerEvents: 'none' }} />
      </div>
    </>
  );
}
