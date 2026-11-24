'use client';

import React, { useEffect, useRef, useState } from 'react';
import { Group, Text } from '@mantine/core';
import { IconRestore } from '@tabler/icons-react';
import type { GradeWheel } from '@/lib/editing/types';
import type { WheelKey } from '@/lib/editing/color/grade';
import { describeWheel, nudgeWheel, pointerToDisc, resetWheelColour, wheelFromPuck, wheelPuck } from '@/lib/editing/color/wheel';
import { useFormat, useT } from '@/i18n';
import { LiveSlider } from '../shell/LiveSlider';
import { EditorIcon } from '../ui';
import { useCoarsePointer } from '../thumbnail/useCoarsePointer';

/**
 * Round colour wheel (lift / gamma / gain / offset). The disc shows the zero-sum part of (r, g, b)
 * exactly where `lib/editing/color/wheel.ts` puts it: x = Cb, y = Cr up, the vectorscope's angles.
 * A drag updates the puck and readout live and calls `onChange` ONCE on release, so one drag is one
 * undo step. `onPreview` carries the would-be wheel to the viewer during the drag (at most once per
 * animation frame, T-008) and gets `null` on release, cancel and unmount, so a drag shows its colour
 * without ever writing it; keyboard, reset and the exact-value fields still write once each.
 */

const RAD = Math.PI / 180;
const RING_SAMPLES = 72;
const RING_INNER = 40;
const RING_OUTER = 49;

/** Screen (vectorscope) angle of a pure cast hue, read from the same maths as the puck. */
function hueAngle(hue: number): number {
  return wheelPuck({
    r: Math.cos(hue * RAD),
    g: Math.cos((hue - 120) * RAD),
    b: Math.cos((hue + 120) * RAD),
  }).angle;
}

const hueMid = (a: number, b: number): number => a + ((((b - a + 540) % 360) - 180) / 2);

const HUE_ANGLES = Array.from({ length: RING_SAMPLES }, (_, i) => hueAngle((i * 360) / RING_SAMPLES));

const RING_SLICES = HUE_ANGLES.map((angle, i) => {
  const start = hueMid(HUE_ANGLES[(i - 1 + RING_SAMPLES) % RING_SAMPLES], angle);
  const end = hueMid(angle, HUE_ANGLES[(i + 1) % RING_SAMPLES]);
  const hue = (i * 360) / RING_SAMPLES;
  const pt = (r: number, a: number) => `${(50 + Math.cos(a * RAD) * r).toFixed(2)} ${(50 - Math.sin(a * RAD) * r).toFixed(2)}`;
  return { color: `hsl(${hue}, 82%, 52%)`, points: `${pt(RING_OUTER, start)} ${pt(RING_OUTER, end)} ${pt(RING_INNER, end)} ${pt(RING_INNER, start)}` };
});

const sameWheel = (a: GradeWheel, b: GradeWheel): boolean =>
  (a.r ?? 0) === (b.r ?? 0) && (a.g ?? 0) === (b.g ?? 0) && (a.b ?? 0) === (b.b ?? 0) && (a.master ?? 0) === (b.master ?? 0);

const colourIsZero = (w: GradeWheel): boolean => (w.r ?? 0) === 0 && (w.g ?? 0) === 0 && (w.b ?? 0) === 0;

export interface ColorWheelProps {
  wheelKey: WheelKey;
  /** The localized wheel name, e.g. "Gain (highlights)". */
  label: string;
  value: GradeWheel;
  readOnly?: boolean;
  /** The whole new wheel, called once per drag / key press / reset. */
  onChange: (wheel: GradeWheel) => void;
  /**
   * T-008: the wheel a drag would write, at most once per animation frame; `null` on release (after
   * the commit), cancel and unmount, so the viewer drops the transient grade.
   */
  onPreview?: (wheel: GradeWheel | null) => void;
}

export function ColorWheel({ wheelKey, label, value, readOnly = false, onChange, onPreview }: ColorWheelProps) {
  const t = useT('editor-color.grade');
  const f = useFormat();
  const coarse = useCoarsePointer();
  const size = coarse ? 156 : 132;
  const puckR = coarse ? 5.5 : 4.5;
  const hitR = (coarse ? 22 : 14) / size * 100;

  const [draft, setDraft] = useState<GradeWheel | null>(null);
  const draftRef = useRef<GradeWheel | null>(null);
  const dragId = useRef<number | null>(null);
  const [focused, setFocused] = useState(false);
  const svgRef = useRef<SVGSVGElement | null>(null);

  // T-008: the drag preview. Coalesced to one call per animation frame, with the latest wheel.
  const onPreviewRef = useRef(onPreview);
  useEffect(() => {
    onPreviewRef.current = onPreview;
  }, [onPreview]);
  const previewFrame = useRef<number | null>(null);
  const previewWheel = useRef<GradeWheel | null>(null);
  const schedulePreview = (wheel: GradeWheel) => {
    previewWheel.current = wheel;
    if (!onPreviewRef.current || previewFrame.current !== null) return;
    previewFrame.current = requestAnimationFrame(() => {
      previewFrame.current = null;
      const next = previewWheel.current;
      if (next) onPreviewRef.current?.(next);
    });
  };
  const clearPreview = () => {
    if (previewFrame.current !== null) cancelAnimationFrame(previewFrame.current);
    previewFrame.current = null;
    previewWheel.current = null;
    onPreviewRef.current?.(null);
  };
  useEffect(() => () => {
    if (previewFrame.current !== null) cancelAnimationFrame(previewFrame.current);
    previewFrame.current = null;
    previewWheel.current = null;
    onPreviewRef.current?.(null);
  }, []);

  const shown = draft ?? value;
  const desc = describeWheel(shown);
  const puck = wheelPuck(shown);
  const px = 50 + puck.x * 50;
  const py = 50 - puck.y * 50;

  const base = t('readout', { hue: t(`hues.${desc.hue}`), strength: desc.strength });
  const readout = desc.residual === 0
    ? base
    : t('readoutShift', { base, amount: f.number(desc.residual, { minimumFractionDigits: 2, maximumFractionDigits: 2, signDisplay: 'always' }) });

  const setDraftWheel = (w: GradeWheel) => {
    draftRef.current = w;
    setDraft(w);
  };
  const clearDraft = () => {
    draftRef.current = null;
    setDraft(null);
  };

  const discPoint = (e: { clientX: number; clientY: number }) => {
    const rect = svgRef.current?.getBoundingClientRect();
    return rect ? pointerToDisc(e.clientX, e.clientY, rect) : null;
  };

  const onPointerDown = (e: React.PointerEvent<SVGSVGElement>) => {
    if (readOnly || e.button !== 0) return;
    const p = discPoint(e);
    if (!p) return;
    e.currentTarget.setPointerCapture?.(e.pointerId);
    dragId.current = e.pointerId;
    const next = wheelFromPuck(p.x, p.y, value, { snapAngle: e.shiftKey });
    setDraftWheel(next);
    schedulePreview(next);
  };

  const onPointerMove = (e: React.PointerEvent<SVGSVGElement>) => {
    if (readOnly || dragId.current !== e.pointerId) return;
    const p = discPoint(e);
    if (!p) return;
    const next = wheelFromPuck(p.x, p.y, draftRef.current ?? value, { snapAngle: e.shiftKey });
    setDraftWheel(next);
    schedulePreview(next);
  };

  const onPointerUp = (e: React.PointerEvent<SVGSVGElement>) => {
    if (dragId.current !== e.pointerId) return;
    e.currentTarget.releasePointerCapture?.(e.pointerId);
    dragId.current = null;
    const next = draftRef.current;
    clearDraft();
    // Defence in depth: a read-only flip mid-drag must not commit the draft on release.
    if (next && !readOnly && !sameWheel(next, value)) onChange(next);
    clearPreview();
  };

  const onPointerCancel = (e: React.PointerEvent<SVGSVGElement>) => {
    if (dragId.current !== e.pointerId) return;
    dragId.current = null;
    clearDraft();
    clearPreview();
  };

  const onKeyDown = (e: React.KeyboardEvent<SVGSVGElement>) => {
    if (readOnly) return;
    const step = e.shiftKey ? 0.1 : 0.02;
    let next: GradeWheel;
    switch (e.key) {
      case 'ArrowLeft': next = nudgeWheel(value, -1, 0, step, { snapAngle: e.shiftKey }); break;
      case 'ArrowRight': next = nudgeWheel(value, 1, 0, step, { snapAngle: e.shiftKey }); break;
      case 'ArrowUp': next = nudgeWheel(value, 0, 1, step, { snapAngle: e.shiftKey }); break;
      case 'ArrowDown': next = nudgeWheel(value, 0, -1, step, { snapAngle: e.shiftKey }); break;
      case 'Home':
      case 'Delete':
        next = resetWheelColour(value);
        break;
      default:
        return;
    }
    e.preventDefault();
    if (!sameWheel(next, value)) onChange(next);
  };

  const reset = () => {
    const next = resetWheelColour(value);
    if (!sameWheel(next, value)) onChange(next);
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4, alignItems: 'stretch' }} data-testid={`grade-wheel-${wheelKey}`}>
      <Group justify="space-between" gap={4} wrap="nowrap">
        <Text size="xs" c="dimmed" style={{ flex: 1, minWidth: 0 }}>
          {label}
        </Text>
        <EditorIcon
          icon={IconRestore}
          size="sm"
          tooltip={false}
          label={t('resetWheel', { wheel: label })}
          disabled={readOnly || colourIsZero(value)}
          onClick={reset}
          data-testid={`grade-wheel-${wheelKey}-reset`}
        />
      </Group>
      <div style={{ display: 'flex', justifyContent: 'center' }}>
        <svg
          ref={svgRef}
          width={size}
          height={size}
          viewBox="0 0 100 100"
          role="slider"
          tabIndex={readOnly ? -1 : 0}
          aria-label={t('wheelDisc', { wheel: label })}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={desc.strength}
          aria-valuetext={readout}
          aria-disabled={readOnly || undefined}
          data-testid={`grade-wheel-${wheelKey}-disc`}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerCancel}
          onDoubleClick={() => { if (!readOnly) reset(); }}
          onKeyDown={onKeyDown}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
          style={{
            display: 'block',
            borderRadius: '50%',
            touchAction: 'none',
            cursor: readOnly ? 'var(--rb-cursor-default)' : 'var(--rb-cursor-crosshair)',
            boxShadow: focused ? '0 0 0 2px var(--mantine-color-blue-5)' : undefined,
          }}
        >
          <circle cx={50} cy={50} r={50} fill="#1f1f1f" />
          {RING_SLICES.map((s, i) => (
            <polygon key={i} points={s.points} fill={s.color} />
          ))}
          <circle cx={50} cy={50} r={RING_INNER} fill="#262626" stroke="rgba(255,255,255,0.35)" strokeWidth={0.8} />
          <line x1={50 - RING_INNER} y1={50} x2={50 + RING_INNER} y2={50} stroke="rgba(255,255,255,0.28)" strokeWidth={0.8} />
          <line x1={50} y1={50 - RING_INNER} x2={50} y2={50 + RING_INNER} stroke="rgba(255,255,255,0.28)" strokeWidth={0.8} />
          <circle cx={px} cy={py} r={hitR} fill="transparent" data-testid={`grade-wheel-${wheelKey}-puck-hit`} />
          <circle cx={px} cy={py} r={puckR} fill="#ffffff" stroke="#000000" strokeWidth={2} pointerEvents="none" data-testid={`grade-wheel-${wheelKey}-puck`} />
        </svg>
      </div>
      <LiveSlider
        size="xs"
        min={-1}
        max={1}
        step={0.01}
        value={value.master ?? 0}
        disabled={readOnly}
        aria-label={t('masterLabel', { wheel: label })}
        label={(v) => f.number(v, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
        onChange={(v) => schedulePreview({ ...shown, master: v })}
        onChangeEnd={(v) => {
          onChange({ ...value, master: v });
          clearPreview();
        }}
        data-testid={`grade-wheel-${wheelKey}-master`}
      />
      <Text size="xs" c="dimmed" data-testid={`grade-wheel-${wheelKey}-readout`}>
        {readout}
      </Text>
    </div>
  );
}
