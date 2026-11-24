'use client';

import React, { useState } from 'react';
import { Button, Group, SegmentedControl, Stack, Switch, Text, TextInput, Tooltip } from '@mantine/core';
import {
  IconArrowDown,
  IconArrowLeft,
  IconArrowMerge,
  IconArrowRight,
  IconArrowUp,
  IconCrop,
  IconLayoutAlignBottom,
  IconLayoutAlignCenter,
  IconLayoutAlignLeft,
  IconLayoutAlignMiddle,
  IconLayoutAlignRight,
  IconLayoutAlignTop,
  IconLayoutDistributeHorizontal,
  IconLayoutDistributeVertical,
} from '@tabler/icons-react';
import type { NudgeKey } from '@/lib/editor/transform-tools';
import { nudgeStepPx } from './layer-actions';
import { fieldValues, type AlignMode, type DistributeAxis, type SizeField } from '@/lib/editor/arrange';
import type { PlacedLayer } from '@/lib/editor/compose';
import { EditorIcon, type EditorIconComponent } from '../ui';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';

export type AlignTarget = 'canvas' | 'selection';

export interface ArrangePanelProps {
  /** The layer being edited (its numbers are shown), or null. */
  layer: PlacedLayer | null;
  /** How many layers the buttons below act on: the ticked ones, else the selected one. */
  count: number;
  picking: boolean;
  onPicking(on: boolean): void;
  onField(field: SizeField, value: number): void;
  onAlign(mode: AlignMode, target: AlignTarget): void;
  /** One press of the nudge pad: move the selected layer a little (1 or 10 canvas pixels). */
  onNudge?(key: NudgeKey, big: boolean): void;
  onDistribute(axis: DistributeAxis): void;
  /** Opens the crop window for the selected picture layer. */
  onCrop(): void;
  canCrop: boolean;
  onMerge(): void;
  merging: boolean;
  coarse?: boolean;
}

const alignItems = (t: T): { mode: AlignMode; label: string; icon: EditorIconComponent }[] => [
  { mode: 'left', label: t('alignLeft'), icon: IconLayoutAlignLeft },
  { mode: 'center', label: t('alignCentre'), icon: IconLayoutAlignCenter },
  { mode: 'right', label: t('alignRight'), icon: IconLayoutAlignRight },
  { mode: 'top', label: t('alignTop'), icon: IconLayoutAlignTop },
  { mode: 'middle', label: t('alignMiddle'), icon: IconLayoutAlignMiddle },
  { mode: 'bottom', label: t('alignBottom'), icon: IconLayoutAlignBottom },
];

const nudgeItems = (t: T): { key: NudgeKey; label: string; icon: EditorIconComponent }[] => [
  { key: 'ArrowLeft', label: t('nudgeLeft'), icon: IconArrowLeft },
  { key: 'ArrowUp', label: t('nudgeUp'), icon: IconArrowUp },
  { key: 'ArrowDown', label: t('nudgeDown'), icon: IconArrowDown },
  { key: 'ArrowRight', label: t('nudgeRight'), icon: IconArrowRight },
];

const fieldItems = (t: T): { field: SizeField; label: string; testKey: string }[] => [
  { field: 'x', label: t('left'), testKey: 'left' },
  { field: 'y', label: t('top'), testKey: 'top' },
  { field: 'width', label: t('width'), testKey: 'width' },
  { field: 'height', label: t('height'), testKey: 'height' },
];

/** One number box: typing is a draft, and the picture changes when you leave the box or press Enter. */
function NumberField({ label, testKey, value, onCommit, disabled, coarse }: { label: string; testKey: string; value: number; onCommit(v: number): void; disabled: boolean; coarse: boolean }) {
  const t = useT('editor-thumbnail-b.arrange');
  const [draft, setDraft] = useState<string | null>(null);
  const commit = () => {
    if (draft === null) return;
    const n = Number(draft.trim());
    setDraft(null);
    if (draft.trim() !== '' && Number.isFinite(n)) onCommit(n);
  };
  return (
    <TextInput
      size="xs"
      label={label}
      inputMode="numeric"
      value={draft ?? String(value)}
      disabled={disabled}
      onChange={(e) => setDraft(e.currentTarget.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') commit();
        if (e.key === 'Escape') setDraft(null);
      }}
      onFocus={(e) => e.currentTarget.select()}
      styles={{ input: { minHeight: coarse ? 44 : undefined, fontSize: 12 }, label: { fontSize: 12 } }}
      aria-label={t('inPixels', { label })}
      data-testid={`arrange-${testKey}`}
      rightSection={<Text size="xs" c="dimmed">{t('px')}</Text>}
    />
  );
}

/**
 * Position and size in numbers, align, spread out, crop and merge for the layer(s) being edited.
 * Everything here is one undo step; "Choose several layers" shows tick boxes in the list.
 */
export function ArrangePanel({ layer, count, picking, onPicking, onField, onAlign, onNudge, onDistribute, onCrop, canCrop, onMerge, merging, coarse = false }: ArrangePanelProps) {
  const t = useT('editor-thumbnail-b.arrange');
  const [target, setTarget] = useState<AlignTarget>('canvas');
  const size = coarse ? 44 : 'md';
  const several = count >= 2;
  const useTarget: AlignTarget = several ? target : 'canvas';
  const values = layer ? fieldValues(layer) : null;
  const locked = layer?.locked ?? false;
  const [bigStep, setBigStep] = useState(false);
  return (
    <Stack gap="xs" data-testid="arrange-panel">
      <Text size="xs" fw={600}>
        {t('positionSize')}
      </Text>
      <Group grow gap={6} align="flex-end" wrap="wrap">
        {fieldItems(t).map(({ field, label, testKey }) => (
          <NumberField
            testKey={testKey}
            key={`${field}-${layer?.id ?? ''}-${values?.[field] ?? ''}`}
            label={label}
            value={values?.[field] ?? 0}
            disabled={!layer || locked}
            coarse={coarse}
            onCommit={(v) => onField(field, v)}
          />
        ))}
      </Group>
      <Text size="12px" c="dimmed">
        {layer ? (locked ? t('locked') : t('proportion')) : t('chooseLayer')}
      </Text>

      {onNudge && (
        <Stack gap={6} data-testid="nudge-pad">
          <Group justify="space-between" gap="xs" wrap="wrap">
            <Text size="xs" fw={600}>
              {t('nudge')}
            </Text>
            <SegmentedControl
              size="xs"
              value={bigStep ? '10' : '1'}
              onChange={(v) => setBigStep(v === '10')}
              data={[
                { value: '1', label: t('step1') },
                { value: '10', label: t('step10') },
              ]}
              aria-label={t('nudgeStep')}
              styles={coarse ? { root: { minHeight: 44 }, label: { minHeight: 36, display: 'flex', alignItems: 'center', justifyContent: 'center' } } : undefined}
            />
          </Group>
          <Group gap={8} wrap="nowrap">
            {nudgeItems(t).map(({ key, label, icon }) => (
              <EditorIcon
                key={key}
                icon={icon}
                variant="light"
                tooltip={false}
                disabled={!layer || locked}
                label={t('nudgeBy', { label, px: nudgeStepPx(bigStep) })}
                data-testid={`nudge-${key}`}
                onClick={() => onNudge(key, bigStep)}
              />
            ))}
          </Group>
        </Stack>
      )}

      <Text size="xs" fw={600}>
        {t('lineUp')}
      </Text>
      <Switch
        size="xs"
        label={t('chooseSeveral')}
        checked={picking}
        onChange={(e) => onPicking(e.currentTarget.checked)}
        styles={{ label: { fontSize: 12 }, root: coarse ? { minHeight: 44, display: 'flex', alignItems: 'center' } : undefined }}
        data-testid="arrange-pick-switch"
      />
      {several && (
        <SegmentedControl
          size="xs"
          fullWidth
          value={target}
          onChange={(v) => setTarget(v as AlignTarget)}
          data={[
            { value: 'canvas', label: t('toPicture') },
            { value: 'selection', label: t('toEachOther') },
          ]}
          aria-label={t('lineUpAgainst')}
        />
      )}
      <Group gap={4} wrap="wrap">
        {alignItems(t).map(({ mode, label, icon }) => (
          <Tooltip key={mode} label={label} openDelay={400}>
            <EditorIcon icon={icon} tooltip={false} variant="light"
              disabled={count === 0 || (useTarget === 'selection' && !several)}
              label={label}
              data-testid={`align-${mode}`}
              onClick={() => onAlign(mode, useTarget)}
            />
          </Tooltip>
        ))}
        <Tooltip label={t('spreadSidewaysTip')} openDelay={400}>
          <EditorIcon tooltip={false} icon={IconLayoutDistributeHorizontal} variant="light" disabled={count < 3} label={t('spreadSideways')} data-testid="distribute-horizontal" onClick={() => onDistribute('horizontal')} />
        </Tooltip>
        <Tooltip label={t('spreadUpDownTip')} openDelay={400}>
          <EditorIcon tooltip={false} icon={IconLayoutDistributeVertical} variant="light" disabled={count < 3} label={t('spreadUpDown')} data-testid="distribute-vertical" onClick={() => onDistribute('vertical')} />
        </Tooltip>
      </Group>

      <Text size="xs" fw={600}>
        {t('cropMerge')}
      </Text>
      <Group gap={6} wrap="wrap">
        <Button size="xs" fz="xs" variant="light" leftSection={<IconCrop size={14} />} h={coarse ? 44 : undefined} disabled={!canCrop} onClick={onCrop} data-testid="crop-open">
          {t('crop')}
        </Button>
        <Button
          size="xs"
          fz="xs"
          variant="light"
          leftSection={<IconArrowMerge size={14} />}
          h={coarse ? 44 : undefined}
          disabled={count < 2}
          loading={merging}
          onClick={onMerge}
          data-testid="merge-layers"
        >
          {t('merge')}
        </Button>
      </Group>
      <Text size="12px" c="dimmed">
        {t('cropMergeHint')}
      </Text>
    </Stack>
  );
}
