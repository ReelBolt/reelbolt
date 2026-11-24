'use client';

import React, { useRef, useState } from 'react';
import { Button, Group, SegmentedControl, Stack, Text } from '@mantine/core';
import type { CurvePoint, GradeCurves } from '@/lib/editing/types';
import { MAX_CURVE_POINTS } from '@/lib/editing/engine';
import { useT } from '@/i18n';

export interface CurveEditorProps {
  curves: GradeCurves | null | undefined;
  readOnly: boolean;
  onChange: (curves: GradeCurves | null) => void;
}

type Channel = 'y' | 'r' | 'g' | 'b';
const CHANNEL_COLORS: Record<Channel, string> = { y: '#e9ecef', r: '#ff6b6b', g: '#51cf66', b: '#339af0' };

/** Points sorted, clamped to [0, 1] and with strictly increasing x (a dragged point cannot pass its neighbours). */
export function normalizeCurve(points: readonly CurvePoint[]): CurvePoint[] {
  const sorted = points.map((p) => ({ x: clamp01(p.x), y: clamp01(p.y) })).sort((a, b) => a.x - b.x);
  const out: CurvePoint[] = [];
  for (const p of sorted) {
    const last = out[out.length - 1];
    if (last && p.x <= last.x) out.push({ x: Math.min(1, last.x + 1e-4), y: p.y });
    else out.push(p);
  }
  return out.slice(0, MAX_CURVE_POINTS);
}

const clamp01 = (v: number) => Math.min(1, Math.max(0, v));

/** The curve's y at x by linear interpolation between points, with (0,0)/(1,1) implied. */
export function curveValue(points: readonly CurvePoint[] | null | undefined, x: number): number {
  const pts = [{ x: 0, y: 0 }, ...(points ?? []), { x: 1, y: 1 }].sort((a, b) => a.x - b.x);
  for (let i = 1; i < pts.length; i++) {
    if (x <= pts[i].x) {
      const a = pts[i - 1];
      const b = pts[i];
      const t = b.x === a.x ? 0 : (x - a.x) / (b.x - a.x);
      return a.y + (b.y - a.y) * t;
    }
  }
  return 1;
}

/** A point being dragged: the channel's points as they will be written, and which one moves. */
interface Drag {
  channel: Channel;
  points: CurvePoint[];
  index: number;
  pointerId: number;
}

/**
 * Custom curves (P7): one SVG per channel. Press adds a point (or grabs a nearby one), drag moves
 * it, double-click removes it. W0.6 (UI2-04): the drag runs on pointer events with pointer capture,
 * so mouse, pen and touch all work, and it is a local draft until release — one drag is ONE
 * `setProperty grade` (one undo step), and a cancelled pointer writes nothing. The end points are
 * implied, so an empty channel is the identity.
 */
export function CurveEditor({ curves, readOnly, onChange }: CurveEditorProps) {
  const t = useT('editor-color.curves');
  const [channel, setChannel] = useState<Channel>('y');
  const [drag, setDrag] = useState<Drag | null>(null);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const size = 180;
  const stored = curves?.[channel] ?? [];
  const points = drag && drag.channel === channel ? drag.points : stored;

  const write = (next: CurvePoint[]) => {
    const merged: GradeCurves = { ...(curves ?? {}), [channel]: next.length === 0 ? null : normalizeCurve(next) };
    const empty = (['y', 'r', 'g', 'b'] as Channel[]).every((c) => !merged[c] || merged[c]!.length === 0);
    onChange(empty ? null : merged);
  };

  const toCurve = (e: { clientX: number; clientY: number }): CurvePoint | null => {
    const svg = svgRef.current;
    if (!svg) return null;
    const rect = svg.getBoundingClientRect();
    return { x: clamp01((e.clientX - rect.left) / rect.width), y: clamp01(1 - (e.clientY - rect.top) / rect.height) };
  };

  const path = () => {
    const samples: string[] = [];
    for (let i = 0; i <= 32; i++) {
      const x = i / 32;
      samples.push(`${i === 0 ? 'M' : 'L'}${(x * size).toFixed(1)},${((1 - curveValue(points, x)) * size).toFixed(1)}`);
    }
    return samples.join(' ');
  };

  return (
    <Stack gap={4} data-testid="curve-editor">
      <Group justify="space-between">
        <SegmentedControl size="xs" value={channel} onChange={(v) => setChannel(v as Channel)} data={[{ value: 'y', label: 'Y' }, { value: 'r', label: 'R' }, { value: 'g', label: 'G' }, { value: 'b', label: 'B' }]} data-testid="curve-channel" />
        <Button size="compact-xs" fz="xs" variant="subtle" disabled={readOnly || points.length === 0} onClick={() => write([])} data-testid="curve-reset">
          {t('reset', { channel: channel.toUpperCase() })}
        </Button>
      </Group>
      <svg
        ref={svgRef}
        width={size}
        height={size}
        viewBox={`0 0 ${size} ${size}`}
        style={{ background: 'var(--mantine-color-dark-8)', border: '1px solid var(--mantine-color-default-border)', borderRadius: 4, cursor: readOnly ? 'var(--rb-cursor-default)' : 'var(--rb-cursor-crosshair)', touchAction: 'none' }}
        data-testid="curve-svg"
        onPointerDown={(e) => {
          if (readOnly || drag) return;
          const p = toCurve(e);
          if (!p) return;
          e.currentTarget.setPointerCapture?.(e.pointerId);
          // Grab a nearby point, else add one; either way nothing is written until release.
          const near = stored.findIndex((q) => Math.abs(q.x - p.x) * size < 8 && Math.abs(q.y - p.y) * size < 8);
          if (near >= 0) {
            setDrag({ channel, points: [...stored], index: near, pointerId: e.pointerId });
            return;
          }
          if (stored.length >= MAX_CURVE_POINTS) return;
          const next = normalizeCurve([...stored, p]);
          setDrag({ channel, points: next, index: next.findIndex((q) => Math.abs(q.x - p.x) < 1e-6), pointerId: e.pointerId });
        }}
        onPointerMove={(e) => {
          if (!drag || drag.pointerId !== e.pointerId) return;
          const p = toCurve(e);
          if (!p) return;
          // A dragged point stays between its neighbours, so the order (and the index) never changes.
          const prev = drag.points[drag.index - 1];
          const next = drag.points[drag.index + 1];
          const x = Math.min(next ? next.x - 1e-4 : 1, Math.max(prev ? prev.x + 1e-4 : 0, p.x));
          setDrag({ ...drag, points: drag.points.map((q, i) => (i === drag.index ? { x, y: p.y } : q)) });
        }}
        onPointerUp={(e) => {
          if (!drag || drag.pointerId !== e.pointerId) return;
          e.currentTarget.releasePointerCapture?.(e.pointerId);
          setDrag(null);
          const changed = drag.points.length !== stored.length || drag.points.some((q, i) => q.x !== stored[i].x || q.y !== stored[i].y);
          if (changed) write(drag.points);
        }}
        onPointerCancel={() => setDrag(null)}
        onDoubleClick={(e) => {
          if (readOnly) return;
          const p = toCurve(e);
          if (!p) return;
          const near = points.findIndex((q) => Math.abs(q.x - p.x) * size < 8 && Math.abs(q.y - p.y) * size < 8);
          if (near >= 0) write(points.filter((_, i) => i !== near));
        }}
      >
        <line x1={0} y1={size} x2={size} y2={0} stroke="#495057" strokeDasharray="4 4" />
        {[0.25, 0.5, 0.75].map((g) => (
          <React.Fragment key={g}>
            <line x1={g * size} y1={0} x2={g * size} y2={size} stroke="#343a40" />
            <line x1={0} y1={g * size} x2={size} y2={g * size} stroke="#343a40" />
          </React.Fragment>
        ))}
        <path d={path()} fill="none" stroke={CHANNEL_COLORS[channel]} strokeWidth={2} />
        {points.map((p, i) => (
          <circle key={i} cx={p.x * size} cy={(1 - p.y) * size} r={4} fill={CHANNEL_COLORS[channel]} stroke="#000" data-testid={`curve-point-${i}`} />
        ))}
      </svg>
      <Text size="xs" c="dimmed">
        {t('hint')}
      </Text>
    </Stack>
  );
}
