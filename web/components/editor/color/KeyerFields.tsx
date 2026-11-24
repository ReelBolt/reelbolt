'use client';

import React from 'react';
import { ColorInput, Group, NumberInput, Stack } from '@mantine/core';
import type { ChromaKeyer } from '@/lib/editing/types';
import { useT } from '@/i18n';

export interface KeyerFieldsProps {
  value: ChromaKeyer;
  readOnly: boolean;
  onChange: (next: ChromaKeyer) => void;
  testId?: string;
}

/** The keyer/matte fields (P7): key colour, similarity, blend, spill — shared by the clip keyer and the insert matte. */
export function KeyerFields({ value, readOnly, onChange, testId = 'keyer' }: KeyerFieldsProps) {
  const t = useT('editor-color.keyer');
  return (
    <Stack gap={4} data-testid={testId}>
      <ColorInput size="xs" label={t('keyColour')} format="hex" value={value.color ?? '#00FF00'} disabled={readOnly} onChangeEnd={(c) => /^#[0-9a-fA-F]{6}$/.test(c) && onChange({ ...value, color: c })} data-testid={`${testId}-color`} />
      <Group grow>
        <NumberInput size="xs" label={t('similarity')} min={0.01} max={1} step={0.01} decimalScale={2} value={value.similarity ?? 0.3} disabled={readOnly} onChange={(v) => typeof v === 'number' && v > 0 && v <= 1 && onChange({ ...value, similarity: v })} data-testid={`${testId}-similarity`} />
        <NumberInput size="xs" label={t('blend')} min={0} max={1} step={0.01} decimalScale={2} value={value.blend ?? 0} disabled={readOnly} onChange={(v) => typeof v === 'number' && onChange({ ...value, blend: Math.min(1, Math.max(0, v)) })} data-testid={`${testId}-blend`} />
        <NumberInput size="xs" label={t('spill')} min={0} max={1} step={0.05} decimalScale={2} value={value.spill ?? 0} disabled={readOnly} onChange={(v) => typeof v === 'number' && onChange({ ...value, spill: Math.min(1, Math.max(0, v)) })} data-testid={`${testId}-spill`} />
      </Group>
    </Stack>
  );
}
