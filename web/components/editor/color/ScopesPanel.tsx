'use client';

import React, { useEffect, useRef, useState } from 'react';
import { ActionIcon, Group, NativeSelect, SegmentedControl, Stack, Text, Tooltip } from '@mantine/core';
import { IconChevronDown, IconChevronRight, IconLayoutColumns, IconLayoutGrid, IconMinus, IconPlus, IconSquare } from '@tabler/icons-react';
import { computeScopes, sampleCanvas, type ScopeData } from '@/lib/editing/color/scopes';
import { clampScopesHeight, SCOPE_KINDS, SCOPES_HEIGHT_MAX, SCOPES_HEIGHT_MIN, scopesShown, useScopesPrefs, writeScopesPrefs, type ScopeKind, type ScopesLayout } from '@/lib/editing/color/colour-page-prefs';
import { useCoarsePointer } from '../thumbnail/useCoarsePointer';
import { EditorIcon } from '../ui';
import { useFormat, useT } from '@/i18n';
import classes from './colour-depth.module.css';

export interface ScopesPanelProps {
  /** The program canvas (the composited preview), or null when the viewer has none. */
  getCanvas: () => HTMLCanvasElement | null;
  /** Called with every sample's statistics (shot match reads the latest). */
  onStats?: (data: ScopeData) => void;
  /** Sampling rate; default ~15 Hz. */
  intervalMs?: number;
  /** §8.2: the docked panel can be collapsed; statistics are still computed (and `onStats` still fires). */
  collapsible?: boolean;
  /** Start expanded (default true). */
  defaultOpen?: boolean;
}

type Scope = ScopeKind;

/** How far one arrow key moves the docked height, in CSS px. */
const RESIZE_STEP = 16;

/** Draws one scope's bins into a 2D canvas. Exported for the test. */
export function drawScope(ctx: CanvasRenderingContext2D, data: ScopeData, scope: Scope, width: number, height: number): void {
  ctx.fillStyle = '#111';
  ctx.fillRect(0, 0, width, height);
  const plane = (bins: Uint32Array, columns: number, levels: number, x0: number, w: number, color: string) => {
    let max = 1;
    for (let i = 0; i < bins.length; i++) if (bins[i] > max) max = bins[i];
    const cw = w / columns;
    const lh = height / levels;
    for (let c = 0; c < columns; c++) {
      for (let l = 0; l < levels; l++) {
        const v = bins[c * levels + l];
        if (v === 0) continue;
        ctx.fillStyle = color;
        ctx.globalAlpha = Math.min(1, 0.15 + (v / max) * 2);
        ctx.fillRect(x0 + c * cw, height - (l + 1) * lh, Math.max(1, cw), Math.max(1, lh));
      }
    }
    ctx.globalAlpha = 1;
  };
  switch (scope) {
    case 'waveform':
      plane(data.waveform, data.columns, data.levels, 0, width, '#8ce99a');
      break;
    case 'parade': {
      const third = width / 3;
      plane(data.parade[0], data.columns, data.levels, 0, third, '#ff6b6b');
      plane(data.parade[1], data.columns, data.levels, third, third, '#51cf66');
      plane(data.parade[2], data.columns, data.levels, third * 2, third, '#339af0');
      break;
    }
    case 'vectorscope': {
      const n = data.vectorSize;
      const cell = Math.min(width, height) / n;
      const ox = (width - cell * n) / 2;
      let max = 1;
      for (let i = 0; i < data.vectorscope.length; i++) if (data.vectorscope[i] > max) max = data.vectorscope[i];
      ctx.strokeStyle = '#444';
      ctx.beginPath();
      ctx.arc(width / 2, height / 2, (cell * n) / 2, 0, Math.PI * 2);
      ctx.stroke();
      for (let y = 0; y < n; y++) {
        for (let x = 0; x < n; x++) {
          const v = data.vectorscope[y * n + x];
          if (v === 0) continue;
          ctx.fillStyle = '#ffd43b';
          ctx.globalAlpha = Math.min(1, 0.2 + (v / max) * 2);
          ctx.fillRect(ox + x * cell, y * cell, Math.max(1, cell), Math.max(1, cell));
        }
      }
      ctx.globalAlpha = 1;
      break;
    }
    case 'histogram': {
      const series: [Uint32Array, string][] = [[data.histogram.r, '#ff6b6b'], [data.histogram.g, '#51cf66'], [data.histogram.b, '#339af0'], [data.histogram.y, '#dee2e6']];
      let max = 1;
      for (const [bins] of series) for (let i = 0; i < bins.length; i++) if (bins[i] > max) max = bins[i];
      const bw = width / data.levels;
      for (const [bins, color] of series) {
        ctx.fillStyle = color;
        ctx.globalAlpha = 0.6;
        for (let i = 0; i < bins.length; i++) {
          const h = (bins[i] / max) * (height - 2);
          ctx.fillRect(i * bw, height - h, Math.max(1, bw), h);
        }
      }
      ctx.globalAlpha = 1;
      break;
    }
  }
}

/**
 * GPU-fed scopes (P7): the program canvas is downsampled into a 2D buffer at ~15 Hz and binned by
 * `computeScopes` — so the scopes read the COMPOSITED frame, grade included (or bypassed), never
 * a source. Throttled so the compositor keeps its budget.
 *
 * Layouts (pro UI wave 4): one scope, two side by side, or all four. Whatever the layout, a tick is ONE
 * readback of the downscaled picture and ONE binning pass (which already yields every scope's bins); a
 * layout only adds the drawing of the scopes it shows. The layout, the scopes it shows and the docked
 * height are remembered per device.
 */
export function ScopesPanel({ getCanvas, onStats, intervalMs = 66, collapsible = true, defaultOpen = true }: ScopesPanelProps) {
  const t = useT('editor-color.scopes');
  const f = useFormat();
  const coarse = useCoarsePointer();
  const prefs = useScopesPrefs();
  const [open, setOpen] = useState(defaultOpen);
  const shown = scopesShown(prefs);
  const shownKey = `${prefs.layout}:${shown.join(',')}`;
  // One canvas per slot (two slots may show the same scope).
  const canvases = useRef(new Map<number, { kind: Scope; el: HTMLCanvasElement }>());
  const scratchRef = useRef<HTMLCanvasElement | null>(null);
  const [status, setStatus] = useState<{ luma: number; black: number; white: number } | null>(null);
  const [dragHeight, setDragHeight] = useState<number | null>(null);
  const height = dragHeight ?? prefs.height;

  useEffect(() => {
    let cancelled = false;
    const tick = () => {
      if (cancelled) return;
      const source = getCanvas();
      if (!source) return;
      scratchRef.current ??= document.createElement('canvas');
      const buffer = sampleCanvas(source, 192, scratchRef.current);
      if (!buffer) return;
      const data = computeScopes(buffer, { columns: 96, levels: 96, vectorSize: 64 });
      // The statistics are always computed — collapsed or not — so "Match to reference" works from any
      // tab; only the drawing needs a canvas on screen. Every visible scope draws from this one sample.
      for (const { kind, el } of canvases.current.values()) {
        const ctx = el.getContext('2d');
        if (ctx) drawScope(ctx, data, kind, el.width, el.height);
      }
      onStats?.(data);
      setStatus({ luma: data.stats.meanLuma * 100, black: data.clipped.black * 100, white: data.clipped.white * 100 });
    };
    const handle = window.setInterval(tick, intervalMs);
    tick();
    return () => {
      cancelled = true;
      window.clearInterval(handle);
    };
    // `shownKey` and `open`: a new layout or a re-opened panel draws at once instead of waiting a tick.
  }, [getCanvas, onStats, shownKey, open, intervalMs]);

  const kindLabel = (kind: Scope): string => (kind === 'waveform' ? t('kinds.waveform') : kind === 'parade' ? t('kinds.parade') : kind === 'vectorscope' ? t('kinds.vectorscope') : t('kinds.histogram'));
  const kindName = (kind: Scope): string => (kind === 'waveform' ? t('names.waveform') : kind === 'parade' ? t('names.parade') : kind === 'vectorscope' ? t('names.vectorscope') : t('names.histogram'));
  const layouts: { value: ScopesLayout; label: string; icon: typeof IconSquare }[] = [
    { value: 'single', label: t('layouts.single'), icon: IconSquare },
    { value: 'two', label: t('layouts.two'), icon: IconLayoutColumns },
    { value: 'four', label: t('layouts.four'), icon: IconLayoutGrid },
  ];

  // The docked height: drag the top edge, or use the arrow keys on it. Stored when the drag ends.
  const drag = useRef<{ startY: number; startHeight: number } | null>(null);
  const onResizeDown = (e: React.PointerEvent<HTMLDivElement>) => {
    drag.current = { startY: e.clientY, startHeight: height };
    e.currentTarget.setPointerCapture?.(e.pointerId);
  };
  const onResizeMove = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!drag.current) return;
    setDragHeight(clampScopesHeight(drag.current.startHeight + (drag.current.startY - e.clientY)));
  };
  const onResizeUp = () => {
    if (!drag.current) return;
    drag.current = null;
    if (dragHeight !== null) writeScopesPrefs({ height: dragHeight });
    setDragHeight(null);
  };
  const onResizeKey = (e: React.KeyboardEvent) => {
    const next = e.key === 'ArrowUp' ? height + RESIZE_STEP : e.key === 'ArrowDown' ? height - RESIZE_STEP : e.key === 'Home' ? SCOPES_HEIGHT_MIN : e.key === 'End' ? SCOPES_HEIGHT_MAX : null;
    if (next === null) return;
    e.preventDefault();
    writeScopesPrefs({ height: clampScopesHeight(next) });
  };

  return (
    <Stack gap={4} data-testid="scopes-panel" data-open={open} data-layout={prefs.layout} style={{ flex: '0 0 auto', padding: '0 8px 6px', minWidth: 0 }}>
      {open && !coarse && (
        <div
          role="separator"
          aria-orientation="horizontal"
          aria-label={t('resize')}
          aria-valuemin={SCOPES_HEIGHT_MIN}
          aria-valuemax={SCOPES_HEIGHT_MAX}
          aria-valuenow={height}
          tabIndex={0}
          className={classes.scopesResize}
          onPointerDown={onResizeDown}
          onPointerMove={onResizeMove}
          onPointerUp={onResizeUp}
          onPointerCancel={onResizeUp}
          onKeyDown={onResizeKey}
          data-testid="scopes-resize"
        />
      )}
      <Group justify="space-between" wrap="wrap" gap={6}>
        <Group gap={4} wrap="nowrap">
          <Text size="xs" fw={600}>
            {t('title')}
          </Text>
          {collapsible && (
            <Tooltip label={open ? t('collapse') : t('expand')}>
              <ActionIcon size="xs" variant="subtle" color="gray" aria-label={open ? t('collapse') : t('expand')} aria-expanded={open} data-testid="scopes-toggle" onClick={() => setOpen((v) => !v)}>
                {open ? <IconChevronDown size={12} /> : <IconChevronRight size={12} />}
              </ActionIcon>
            </Tooltip>
          )}
        </Group>
        {open && (
          <Group gap={6} wrap="wrap">
            {prefs.layout === 'single' && (
              <SegmentedControl size="xs" value={prefs.single} onChange={(v) => writeScopesPrefs({ single: v as Scope })} data={SCOPE_KINDS.map((kind) => ({ value: kind, label: kindLabel(kind) }))} data-testid="scopes-kind" />
            )}
            {prefs.layout === 'two' &&
              ([0, 1] as const).map((slot) => (
                <NativeSelect
                  key={slot}
                  size={coarse ? 'md' : 'xs'}
                  aria-label={slot === 0 ? t('leftScope') : t('rightScope')}
                  value={prefs.pair[slot]}
                  onChange={(e) => {
                    const kind = e.currentTarget.value as Scope;
                    writeScopesPrefs({ pair: slot === 0 ? [kind, prefs.pair[1]] : [prefs.pair[0], kind] });
                  }}
                  data={SCOPE_KINDS.map((kind) => ({ value: kind, label: kindName(kind) }))}
                  data-testid={slot === 0 ? 'scopes-kind-left' : 'scopes-kind-right'}
                />
              ))}
            {coarse && (
              <Group gap={2} wrap="nowrap">
                <EditorIcon icon={IconMinus} size="sm" label={t('shorter')} disabled={height <= SCOPES_HEIGHT_MIN} onClick={() => writeScopesPrefs({ height: clampScopesHeight(height - RESIZE_STEP * 2) })} data-testid="scopes-shorter" />
                <EditorIcon icon={IconPlus} size="sm" label={t('taller')} disabled={height >= SCOPES_HEIGHT_MAX} onClick={() => writeScopesPrefs({ height: clampScopesHeight(height + RESIZE_STEP * 2) })} data-testid="scopes-taller" />
              </Group>
            )}
            <Group gap={2} wrap="nowrap" role="radiogroup" aria-label={t('layout')} data-testid="scopes-layout">
              {layouts.map((l) => (
                <EditorIcon key={l.value} icon={l.icon} size="sm" dense role="radio" aria-checked={prefs.layout === l.value} variant={prefs.layout === l.value ? 'light' : 'subtle'} color={prefs.layout === l.value ? undefined : 'gray'} label={l.label} onClick={() => writeScopesPrefs({ layout: l.value })} data-testid={`scopes-layout-${l.value}`} />
              ))}
            </Group>
          </Group>
        )}
      </Group>
      {open && (
        <div className={classes.scopesGrid} style={{ gridTemplateColumns: `repeat(${shown.length}, minmax(0, 1fr))` }} data-testid="scopes-grid">
          {shown.map((kind, slot) => (
            <div key={`${slot}:${kind}`} className={classes.scopeCell}>
              <canvas
                ref={(el) => {
                  if (el) canvases.current.set(slot, { kind, el });
                  else canvases.current.delete(slot);
                }}
                width={256}
                height={128}
                className={classes.scopeCanvas}
                style={{ height }}
                role="img"
                aria-label={kindName(kind)}
                data-testid={shown.length === 1 ? 'scopes-canvas' : `scopes-canvas-${slot}`}
                data-scope={kind}
              />
              {shown.length > 1 && <span className={classes.scopeCaption}>{kindLabel(kind)}</span>}
            </div>
          ))}
        </div>
      )}
      {open && (
        <Text size="xs" c="dimmed" data-testid="scopes-status">
          {t('note', {
            status: status
              ? t('status', {
                  luma: f.number(status.luma, { maximumFractionDigits: 0 }),
                  black: f.number(status.black, { minimumFractionDigits: 1, maximumFractionDigits: 1 }),
                  white: f.number(status.white, { minimumFractionDigits: 1, maximumFractionDigits: 1 }),
                })
              : t('waiting'),
            hz: 15,
          })}
        </Text>
      )}
    </Stack>
  );
}
