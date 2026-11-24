'use client';

import React from 'react';
import { Button, Group, SegmentedControl, Slider, Stack, Text } from '@mantine/core';
import { IconEraser } from '@tabler/icons-react';
import { BRUSH_MAX } from '@/lib/editor/mask';
import { useT } from '@/i18n';
import type { BrushState } from './EditorCanvas';

export interface BrushPanelProps {
  brush: BrushState;
  onBrush(next: BrushState): void;
  /** The layer the mask belongs to, by name; null when no picture layer is selected. */
  targetLabel: string | null;
  hasMask: boolean;
  onClear(): void;
}

/**
 * The brush mask tool: paint over the part of the selected layer you want to change, then choose
 * Erase or Fill below it. The mask is sized to the layer's own pixels, so a stroke lands where
 * you see it however the layer is moved or scaled.
 */
export function BrushPanel({ brush, onBrush, targetLabel, hasMask, onClear }: BrushPanelProps) {
  const t = useT('editor-thumbnail-a.brush');
  const tc = useT('editor-thumbnail-a.common');
  return (
    <Stack gap="sm">
      <Text size="xs" c="dimmed">
        {targetLabel
          ? t('hintTarget', { layer: targetLabel })
          : t('hintNone')}
      </Text>

      <SegmentedControl
        size="xs"
        fullWidth
        aria-label={t('modeLabel')}
        data={[
          { value: 'paint', label: t('paint') },
          { value: 'erase', label: t('erase') },
        ]}
        value={brush.erase ? 'erase' : 'paint'}
        onChange={(v) => onBrush({ ...brush, erase: v === 'erase' })}
      />

      <div>
        <Group justify="space-between">
          <Text size="xs" id="brush-size-label">
            {t('size')}
          </Text>
          <Text size="xs" c="dimmed">
            {tc('px', { n: brush.size }) /* eslint-disable-line i18next/no-literal-string -- tc is a useT() translator, not copy */}
          </Text>
        </Group>
        <Slider
          size="sm"
          min={4}
          max={Math.min(BRUSH_MAX, 400)}
          value={brush.size}
          onChange={(size) => onBrush({ ...brush, size })}
          label={null}
          aria-labelledby="brush-size-label"
          thumbLabel={t('size')}
        />
      </div>

      <Group justify="space-between">
        <Text size="xs" c={hasMask ? 'green' : 'dimmed'}>
          {hasMask ? t('maskReady') : t('nothingPainted')}
        </Text>
        <Button size="compact-xs" fz="xs" variant="subtle" color="gray" leftSection={<IconEraser size={12} />} disabled={!hasMask} onClick={onClear}>
          {t('clear')}
        </Button>
      </Group>
    </Stack>
  );
}
