'use client';

import React, { useState } from 'react';
import { Button, Group, SegmentedControl, Slider, Stack, Text } from '@mantine/core';
import { IconArrowBackUp, IconArrowForwardUp } from '@tabler/icons-react';
import type { MaskSaveState } from '@/lib/editor/layer-mask';
import { BRUSH_MAX } from '@/lib/editor/mask';
import { useT } from '@/i18n';
import type { MaskBrushState } from './useLayerMaskEditing';

export interface LayerMaskBarProps {
  layerLabel: string;
  brush: MaskBrushState;
  onBrush(next: MaskBrushState): void;
  canUndo: boolean;
  canRedo: boolean;
  onUndo(): void;
  onRedo(): void;
  /** True when there is painting that was not saved yet. */
  changed: boolean;
  saveState: MaskSaveState;
  onDone(): void;
  onDiscard(): void;
  showMaskOnly: boolean;
  onToggleMaskOnly(): void;
  /** A touch screen: every control is a 44 px target. */
  coarse?: boolean;
}

/**
 * The strip shown while a layer's mask is being painted: Hide or Show, brush size and softness,
 * undo and redo of strokes, "Show mask only", and Done (which saves the mask).
 */
export function LayerMaskBar(props: LayerMaskBarProps) {
  const { brush, onBrush, saveState, coarse = false, changed } = props;
  const t = useT('editor-thumbnail-a.maskBar');
  const tc = useT('editor-thumbnail-a.common');
  const [confirming, setConfirming] = useState(false);
  const h = coarse ? 44 : undefined;
  const saving = saveState.phase === 'saving';
  return (
    <Stack gap="xs" p="xs" data-testid="layer-mask-bar" role="group" aria-label={t('group', { layer: props.layerLabel })}
      style={{ border: '1px solid var(--rb-ed-border, var(--mantine-color-dark-4))', borderRadius: 8, background: 'var(--rb-ed-surface, var(--mantine-color-body))' }}>
      <Text size="xs" fw={600}>{t('painting', { layer: props.layerLabel })}</Text>
      <SegmentedControl
        size="xs"
        fullWidth
        aria-label={t('brushLabel')}
        data={[
          { value: 'hide', label: t('hide') },
          { value: 'show', label: t('show') },
        ]}
        value={brush.hide ? 'hide' : 'show'}
        onChange={(v) => onBrush({ ...brush, hide: v === 'hide' })}
        styles={coarse ? { root: { minHeight: 44 }, label: { minHeight: 44, display: 'flex', alignItems: 'center', justifyContent: 'center' } } : undefined}
        data-testid="layer-mask-mode"
      />
      <Text size="xs" c="dimmed">
        {t('hint')}
      </Text>
      <div>
        <Group justify="space-between">
          <Text size="xs" id="mask-size-label">{t('size')}</Text>
          <Text size="xs" c="dimmed">{tc('px', { n: brush.size }) /* eslint-disable-line i18next/no-literal-string -- tc is a useT() translator, not copy */}</Text>
        </Group>
        <Slider size="sm" min={4} max={Math.min(BRUSH_MAX, 400)} value={brush.size} onChange={(size) => onBrush({ ...brush, size })}
          label={null} aria-labelledby="mask-size-label" thumbLabel={t('size')} />
      </div>
      <div>
        <Group justify="space-between">
          <Text size="xs" id="mask-hardness-label">{t('softness')}</Text>
          <Text size="xs" c="dimmed">{tc('percent', { n: Math.round((1 - brush.hardness) * 100) }) /* eslint-disable-line i18next/no-literal-string -- tc is a useT() translator, not copy */}</Text>
        </Group>
        <Slider size="sm" min={0} max={100} value={Math.round((1 - brush.hardness) * 100)} onChange={(soft) => onBrush({ ...brush, hardness: 1 - soft / 100 })}
          label={null} aria-labelledby="mask-hardness-label" thumbLabel={t('softness')} />
      </div>
      <Group gap="xs" grow>
        <Button size="xs" fz="xs" h={h} variant="default" leftSection={<IconArrowBackUp size={14} />} disabled={!props.canUndo || saving} onClick={props.onUndo} data-testid="layer-mask-undo">
          {t('undoStroke')}
        </Button>
        <Button size="xs" fz="xs" h={h} variant="default" leftSection={<IconArrowForwardUp size={14} />} disabled={!props.canRedo || saving} onClick={props.onRedo} data-testid="layer-mask-redo">
          {t('redoStroke')}
        </Button>
      </Group>
      <Button size="xs" fz="xs" h={h} variant={props.showMaskOnly ? 'filled' : 'default'} aria-pressed={props.showMaskOnly} onClick={props.onToggleMaskOnly} data-testid="layer-mask-only">
        {t('maskOnly')}
      </Button>
      {saveState.phase === 'failed' && (
        <Text size="xs" c="red" role="alert" data-testid="layer-mask-error">{saveState.message}</Text>
      )}
      {saveState.phase === 'saving' && <Text size="xs" c="dimmed" role="status">{t('saving')}</Text>}
      {confirming ? (
        <Stack gap="xs">
          <Text size="xs">{t('discardAsk')}</Text>
          <Group gap="xs" grow>
            <Button size="xs" fz="xs" h={h} variant="default" onClick={() => setConfirming(false)}>{t('keepPainting')}</Button>
            <Button size="xs" fz="xs" h={h} color="red" variant="light" onClick={props.onDiscard} data-testid="layer-mask-discard-confirm">{t('throwAway')}</Button>
          </Group>
        </Stack>
      ) : (
        <Group gap="xs" grow>
          <Button size="xs" fz="xs" h={h} variant="default" disabled={saving} onClick={() => (changed ? setConfirming(true) : props.onDiscard())} data-testid="layer-mask-cancel">
            {t('cancel')}
          </Button>
          <Button size="xs" fz="xs" h={h} loading={saving} onClick={props.onDone} data-testid="layer-mask-done">
            {saveState.phase === 'failed' ? t('tryAgain') : t('done')}
          </Button>
        </Group>
      )}
    </Stack>
  );
}
