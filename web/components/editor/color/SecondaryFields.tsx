'use client';

import React, { useState } from 'react';
import { Alert, Button, Checkbox, Group, NumberInput, Select, Stack, Text } from '@mantine/core';
import { LiveRangeSlider, LiveSlider } from '../shell/LiveSlider';
import { IconFocus2 } from '@tabler/icons-react';
import { LargerScreenOnly } from '../shell/LargerScreen';
import type { GradeNode, GradeQualifier, GradeWindow, ShapeMask } from '@/lib/editing/types';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useT } from '@/i18n';

export interface SecondaryFieldsProps {
  node: GradeNode;
  readOnly: boolean;
  onChange: (node: GradeNode, label: string) => void;
  /** Tracks the node's window through its clip; absent when the stack is not a clip's (e.g. a colour group). */
  onTrackWindow?: (node: GradeNode) => Promise<GradeWindow | null>;
  /** §8.2: enters the window viewer tool so the box can be dragged on the picture (pauses and focuses it). */
  onEditWindow?: () => void;
}

const DEFAULT_QUALIFIER: GradeQualifier = { hueCenter: 25, hueWidth: 25, satMin: 0.15, satMax: 1, lumMin: 0.1, lumMax: 0.95, softness: 0.1, invert: false };
const DEFAULT_WINDOW: GradeWindow = { shape: { kind: 'ellipse', x: 0.5, y: 0.45, w: 0.35, h: 0.5, feather: 0.08, invert: false } };

/**
 * P9 secondaries: an HSL qualifier (hue centre/width, saturation and lightness bands, softness,
 * invert) and a power window (shape, feather, invert, and — for a clip — "Track" to follow a feature
 * through the clip, writing the window's offset keyframes). Both confine THIS node's correction; the
 * viewer's grade pass applies them as the render does.
 */
export function SecondaryFields({ node, readOnly, onChange, onTrackWindow, onEditWindow }: SecondaryFieldsProps) {
  const t = useT('editor-color.secondary');
  const [tracking, setTracking] = useState(false);
  const [trackError, setTrackError] = useState<string | null>(null);
  const q = node.qualifier ?? null;
  const w = node.window ?? null;
  const setQ = (patch: Partial<GradeQualifier>, label: string) => onChange({ ...node, qualifier: { ...(q ?? DEFAULT_QUALIFIER), ...patch } }, label);
  const setShape = (patch: Partial<ShapeMask>, label: string) => onChange({ ...node, window: { ...(w ?? DEFAULT_WINDOW), shape: { ...(w ?? DEFAULT_WINDOW).shape, ...patch } } }, label);
  const keyCount = (w?.offsetX?.keys?.length ?? 0) + (w?.offsetY?.keys?.length ?? 0);

  return (
    <Stack gap={6} data-testid="grade-secondary">
      <Checkbox size="xs" label={t('qualifier')} checked={!!q} disabled={readOnly} onChange={(e) => onChange({ ...node, qualifier: e.currentTarget.checked ? DEFAULT_QUALIFIER : null }, e.currentTarget.checked ? t('addQualifier') : t('removeQualifier'))} data-testid="grade-qualifier-toggle" />
      {q && (
        <>
          <Group gap={6} align="center">
            <Text size="12px" w={70}>{t('hue', { deg: Math.round(q.hueCenter ?? 0) })}</Text>
            <LiveSlider size="xs" style={{ flex: 1 }} min={0} max={359} step={1} value={q.hueCenter ?? 0} disabled={readOnly} onChangeEnd={(v) => setQ({ hueCenter: v }, t('qualifierHue'))} data-testid="grade-qualifier-hue" />
          </Group>
          <Group gap={6} align="center">
            <Text size="12px" w={70}>{t('width', { deg: Math.round(q.hueWidth ?? 180) })}</Text>
            <LiveSlider size="xs" style={{ flex: 1 }} thumbLabel={t('hueWidth')} min={1} max={180} step={1} value={q.hueWidth ?? 180} disabled={readOnly} onChangeEnd={(v) => setQ({ hueWidth: v }, t('qualifierHueWidth'))} />
          </Group>
          <Group gap={6} align="center">
            <Text size="12px" w={70}>{t('saturation')}</Text>
            <LiveRangeSlider size="xs" style={{ flex: 1 }} min={0} max={1} step={0.01} value={[q.satMin ?? 0, q.satMax ?? 1]} disabled={readOnly} onChangeEnd={([a, b]) => setQ({ satMin: a, satMax: b }, t('qualifierSaturation'))} />
          </Group>
          <Group gap={6} align="center">
            <Text size="12px" w={70}>{t('lightness')}</Text>
            <LiveRangeSlider size="xs" style={{ flex: 1 }} min={0} max={1} step={0.01} value={[q.lumMin ?? 0, q.lumMax ?? 1]} disabled={readOnly} onChangeEnd={([a, b]) => setQ({ lumMin: a, lumMax: b }, t('qualifierLightness'))} />
          </Group>
          <Group gap={6} align="end">
            <NumberInput size="xs" label={t('softness')} w={90} min={0} max={0.5} step={0.01} decimalScale={2} value={q.softness ?? 0.1} disabled={readOnly} onChange={(v) => typeof v === 'number' && setQ({ softness: Math.min(0.5, Math.max(0, v)) }, t('qualifierSoftness'))} />
            <Checkbox size="xs" label={t('invert')} checked={q.invert === true} disabled={readOnly} onChange={(e) => setQ({ invert: e.currentTarget.checked }, t('qualifierInvert'))} />
          </Group>
        </>
      )}
      <Checkbox size="xs" label={t('window')} checked={!!w} disabled={readOnly} onChange={(e) => onChange({ ...node, window: e.currentTarget.checked ? DEFAULT_WINDOW : null }, e.currentTarget.checked ? t('addWindow') : t('removeWindow'))} data-testid="grade-window-toggle" />
      {w && (
        <>
          <Group gap={4} align="end" wrap="wrap">
            <Select size="xs" label={t('shape')} w={90} data={[{ value: 'ellipse', label: t('ellipse') }, { value: 'rect', label: t('rectangle') }]} value={w.shape.kind ?? 'ellipse'} allowDeselect={false} disabled={readOnly} onChange={(v) => v && setShape({ kind: v as ShapeMask['kind'] }, t('windowShape'))} />
            {/* eslint-disable-next-line i18next/no-literal-string -- ids, not text */}
            {(['x', 'y', 'w', 'h'] as const).map((k) => (
              <NumberInput key={k} size="xs" label={k.toUpperCase()} w={70} min={0} max={1} step={0.01} decimalScale={2} value={w.shape[k] ?? 0.5} disabled={readOnly} onChange={(v) => typeof v === 'number' && setShape({ [k]: Math.min(1, Math.max(0, v)) }, t('windowDim', { dim: k }))} data-testid={`grade-window-${k}`} />
            ))}
            <NumberInput size="xs" label={t('feather')} w={80} min={0} max={0.5} step={0.01} decimalScale={2} value={w.shape.feather ?? 0} disabled={readOnly} onChange={(v) => typeof v === 'number' && setShape({ feather: Math.min(0.5, Math.max(0, v)) }, t('windowFeather'))} />
            <Checkbox size="xs" label={t('outside')} checked={w.shape.invert === true} disabled={readOnly} onChange={(e) => setShape({ invert: e.currentTarget.checked }, t('windowInvert'))} />
          </Group>
          <Group gap={6}>
            {onEditWindow && (
              <Button size="compact-xs" fz="xs" variant="light" leftSection={<IconFocus2 size={12} />} onClick={onEditWindow} data-testid="color-edit-window">
                {t('editWindow')}
              </Button>
            )}
            {onTrackWindow && (
              <LargerScreenOnly feature="window-tracking" what={t('trackingWhat')}>
                <Button
                  size="compact-xs" fz="xs"
                  variant="light"
                  leftSection={<IconFocus2 size={12} />}
                  loading={tracking}
                  disabled={readOnly}
                  onClick={async () => {
                    setTracking(true);
                    setTrackError(null);
                    try {
                      const tracked = await onTrackWindow(node);
                      if (tracked) onChange({ ...node, window: tracked }, t('trackWindow'));
                    } catch (error) {
                      setTrackError(humanizeErrorT(t, error, t('trackFailed')));
                    } finally {
                      setTracking(false);
                    }
                  }}
                  data-testid="grade-window-track"
                >
                  {t('track')}
                </Button>
              </LargerScreenOnly>
            )}
            {keyCount > 0 && (
              <Button size="compact-xs" fz="xs" variant="subtle" disabled={readOnly} onClick={() => onChange({ ...node, window: { shape: w.shape } }, t('clearTrack'))}>
                {t('clearTrackKeys', { count: keyCount })}
              </Button>
            )}
          </Group>
          {trackError && <Alert color="red" variant="light">{trackError}</Alert>}
        </>
      )}
      {(q || w) && (
        <Text size="12px" c="dimmed">
          {t('note')}
        </Text>
      )}
    </Stack>
  );
}
