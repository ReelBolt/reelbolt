'use client';

import React, { useRef, useState } from 'react';
import { Group, Select, Text, Tooltip } from '@mantine/core';
import { IconTrash } from '@tabler/icons-react';
import type { EditOp, KeyframeParam, KeyframeTarget } from '@/lib/editing/engine';
import { INTERP_KINDS } from '@/lib/editing/engine';
import type { InterpKind, Keyframed } from '@/lib/editing/types';
import { evaluateKeyframed } from '@/lib/editing/preview';
import { EditorIcon } from '../ui';
import { useFormat, useT } from '@/i18n';

export interface KeyframeLaneProps {
  target: KeyframeTarget;
  param: KeyframeParam;
  channel: Keyframed<number> | null | undefined;
  /** The span the lane shows, timeline frames. */
  startFrame: number;
  endFrame: number;
  playheadFrame: number;
  readOnly: boolean;
  /** Dispatch one op as one undo step. */
  onOp: (op: EditOp, label?: string) => void;
  onSeek?: (frame: number) => void;
  height?: number;
}

/**
 * The curve editor (P5): one keyframed channel drawn as its evaluated curve over the clip's span,
 * each key a handle. Click a key to select it, drag it along the lane to move it in time
 * (`moveKeyframe` on release — one undo step, never a stream of ops while dragging), change its
 * interpolation with the select, delete it with the bin. The playhead is drawn so "add key at
 * playhead" in the inspector is visibly where the key lands. SVG, so it tests under jsdom.
 */
export function KeyframeLane({ target, param, channel, startFrame, endFrame, playheadFrame, readOnly, onOp, onSeek, height = 72 }: KeyframeLaneProps) {
  const t = useT('editor.panels.keyframeLane');
  const nf = useFormat();
  const [selected, setSelected] = useState<number | null>(null);
  const [dragFrame, setDragFrame] = useState<number | null>(null);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const width = 260;
  const span = Math.max(1, endFrame - startFrame);
  const keys = channel?.keys ?? [];
  const values = keys.map((k) => k.value);
  const base = typeof channel?.value === 'number' ? channel.value : 0;
  const lo = Math.min(base, ...values, 0);
  const hi = Math.max(base, ...values, 0);
  const range = hi - lo || 1;
  const x = (frame: number) => ((frame - startFrame) / span) * width;
  const y = (value: number) => height - 8 - ((value - lo) / range) * (height - 16);
  const frameAt = (clientX: number): number => {
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect || rect.width === 0) return startFrame;
    const u = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
    return Math.round(startFrame + u * span);
  };

  const points: string[] = [];
  const steps = 64;
  for (let i = 0; i <= steps; i++) {
    const frame = startFrame + (span * i) / steps;
    points.push(`${x(frame).toFixed(1)},${y(evaluateKeyframed(channel ?? null, frame, base)).toFixed(1)}`);
  }

  const selectedKey = selected === null ? undefined : keys.find((k) => k.frame === selected);

  return (
    <div data-testid={`keyframe-lane-${param}`}>
      <svg
        ref={svgRef}
        width="100%"
        viewBox={`0 0 ${width} ${height}`}
        style={{ display: 'block', background: 'var(--mantine-color-default-hover)', borderRadius: 4, touchAction: 'none' }}
        onPointerMove={(e) => {
          if (selected === null || readOnly || e.buttons === 0) return;
          setDragFrame(frameAt(e.clientX));
        }}
        onPointerUp={(e) => {
          if (selected === null || dragFrame === null) return;
          const to = frameAt(e.clientX);
          setDragFrame(null);
          if (to !== selected && !keys.some((k) => k.frame === to)) {
            onOp({ type: 'moveKeyframe', target, param, frame: selected, toFrame: to }, `Move ${param} key`);
            setSelected(to);
          }
        }}
        onDoubleClick={(e) => onSeek?.(frameAt(e.clientX))}
      >
        <polyline points={points.join(' ')} fill="none" stroke="var(--mantine-color-blue-5)" strokeWidth={1.5} />
        <line x1={x(playheadFrame)} x2={x(playheadFrame)} y1={0} y2={height} stroke="var(--mantine-color-red-6)" strokeWidth={1} data-testid="keyframe-lane-playhead" />
        {keys.map((k) => {
          const frame = dragFrame !== null && k.frame === selected ? dragFrame : k.frame;
          return (
            <circle
              key={k.frame}
              cx={x(frame)}
              cy={y(k.value)}
              r={selected === k.frame ? 5 : 3.5}
              fill={selected === k.frame ? 'var(--mantine-color-yellow-5)' : 'var(--mantine-color-blue-7)'}
              stroke="#fff"
              strokeWidth={1}
              style={{ cursor: readOnly ? 'var(--rb-cursor-default)' : 'var(--rb-cursor-grab)' }}
              onPointerDown={(e) => {
                e.stopPropagation();
                setSelected(k.frame);
                setDragFrame(null);
              }}
              data-testid={`keyframe-${param}-${k.frame}`}
            />
          );
        })}
      </svg>
      <Group justify="space-between" gap={4} mt={2}>
        <Text size="xs" c="dimmed">
          {keys.length === 0 ? t('noKeysTheValue') : selectedKey ? t('keyAt', { frame: selectedKey.frame, v2: nf.number(selectedKey.value, { maximumFractionDigits: 3 }) }) : t('otherClickOneTo', { length: keys.length })}
        </Text>
        {selectedKey && !readOnly && (
          <Group gap={4}>
            <Select
              size="xs"
              w={130}
              data={INTERP_KINDS.map((k) => ({ value: k, label: k }))}
              value={selectedKey.interp}
              allowDeselect={false}
              onChange={(v) => v && onOp({ type: 'setKeyframe', target, param, frame: selectedKey.frame, value: selectedKey.value, interp: v as InterpKind }, t('setKeyEasing', { param }))}
              aria-label={t('keyEasing')}
              data-testid="keyframe-interp"
            />
            <Tooltip label={t('deleteThisKey')}>
              <EditorIcon tooltip={false} icon={IconTrash} size="sm"
                color="red"
                label={t('deleteKeyframe')}
                onClick={() => {
                  onOp({ type: 'removeKeyframe', target, param, frame: selectedKey.frame }, `Remove ${param} key`);
                  setSelected(null);
                }}
                data-testid="keyframe-delete" />
            </Tooltip>
          </Group>
        )}
      </Group>
    </div>
  );
}
