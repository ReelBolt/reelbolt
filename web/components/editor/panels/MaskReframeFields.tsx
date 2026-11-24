'use client';

import React from 'react';
import { Button, Group, Select, Stack, Switch, Text } from '@mantine/core';
import type { MediaClip, ShapeMask } from '@/lib/editing/types';
import type { PropertyKey, PropertyValue } from '@/lib/editing/engine';
import { shapeMaskOf } from '@/lib/editing/viewer-transform/frame-handles';
import { LiveSlider } from '../shell/LiveSlider';
import { useFormat, useT } from '@/i18n';
import type { LiveChord } from '../prefs/live-chords';

/** §7.2: the default a new shape mask writes (the value Compose has always used). */
export const DEFAULT_SHAPE_MASK: ShapeMask = { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.05, invert: false };
/** §7.2: the default a new reframe point writes. */
export const DEFAULT_REFRAME_POINT = { x: 0.5, y: 0.5 } as const;

/** The two picture tools a mask/reframe entry point activates. */
export type MaskReframeTool = 'mask' | 'reframe';

export interface MaskReframeFieldsProps {
  /** The single selected media clip. */
  clip: MediaClip;
  readOnly: boolean;
  /** Every change is ONE `setProperty` (one undo step); the host owns the store. */
  onSetProperty: (itemId: string, key: PropertyKey, value: PropertyValue, label?: string) => void;
  /** Entering a shape tool pauses playback and focuses the picture (T-004). */
  onActivateViewerTool?: (tool: MaskReframeTool) => void;
  /** For a segmentation mask: the tracked objects as the analysis names them (Compose knows the pool). */
  segmentationLabel?: string;
  /** Which half to show: Compose's Mask and Reframe entries show one each; the Inspector shows both (default). */
  part?: 'mask' | 'reframe';
  /** Compose's Mask entry leads with "Draw on the picture"; the Add buttons are then quieter alternatives. */
  quietAdd?: boolean;
  /** The remove-entry chord, when it would remove this one (Compose only); its tooltip and `aria-keyshortcuts`. */
  removeChord?: LiveChord;
}

/**
 * §7.2/§7.3: the ONE mask and reframe field set, rendered by both the Inspector and Compose so their
 * labels and test ids cannot drift. It only ever writes the existing `mask` / `reframe` document fields
 * through `setProperty` — one batch and one undo step per change — and never mutates the document.
 *
 * A segmentation (object) mask is read-only here: shape controls would silently replace it, so the
 * section only says which objects it follows and offers Remove. Compose is desktop-only; the phone
 * renders neither this component nor an Add entry point.
 */
export function MaskReframeFields({ clip, readOnly, onSetProperty, onActivateViewerTool, segmentationLabel, part, quietAdd = false, removeChord }: MaskReframeFieldsProps) {
  const t = useT('editor.panels.maskReframeFields');
  const nf = useFormat();
  const dec2 = (v: number) => nf.number(v, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const shape = shapeMaskOf(clip.mask);
  const objectMask = clip.mask && clip.mask.kind === 'segmentation' ? clip.mask : null;
  const reframe = clip.reframe ?? null;
  const set = (key: PropertyKey, value: PropertyValue, label: string) => onSetProperty(clip.id, key, value, label);

  const addMask = (kind: 'rect' | 'ellipse') => {
    set('mask', { ...DEFAULT_SHAPE_MASK, kind }, kind === 'ellipse' ? t('addEllipseMask') : t('addMask'));
    onActivateViewerTool?.('mask');
  };
  const addReframe = () => {
    set('reframe', { ...DEFAULT_REFRAME_POINT }, t('addReframe'));
    onActivateViewerTool?.('reframe');
  };

  const slider = (testId: string, label: string, value: number, min: number, max: number, onEnd: (v: number) => void) => (
    <Group gap={6} wrap="nowrap" align="center" data-testid={testId}>
      <Text size="xs" w={70} c="dimmed">
        {label}
      </Text>
      <LiveSlider size="xs" style={{ flex: 1 }} thumbLabel={label} min={min} max={max} step={0.01} value={value} disabled={readOnly} onChangeEnd={onEnd} label={dec2} />
    </Group>
  );

  return (
    <Stack gap={4} data-testid="mask-reframe-fields">
      {part === 'reframe' ? null : objectMask ? (
        <Group gap={6} wrap="nowrap" align="center" data-testid="mask-reframe-object">
          <Text size="xs" style={{ flex: 1 }}>
            {t('objectMask', { labels: segmentationLabel || t('objectMaskFallback') })}
          </Text>
          <Button size="compact-xs" fz="xs" variant="light" color="red" disabled={readOnly} onClick={() => set('mask', null, t('removeMask'))} data-testid="mask-reframe-object-remove">
            {t('removeMask')}
          </Button>
        </Group>
      ) : shape ? (
        <>
          <Group gap={6} wrap="nowrap" align="flex-end">
            <Select
              size="xs"
              label={t('shape')}
              data={[
                { value: 'rect', label: t('rectangle') },
                { value: 'ellipse', label: t('ellipse') },
              ]}
              value={shape.kind ?? 'rect'}
              onChange={(v) => v && set('mask', { ...shape, kind: v as ShapeMask['kind'] }, t('maskShape'))}
              allowDeselect={false}
              w={120}
              disabled={readOnly}
              data-testid="mask-reframe-kind"
            />
            <Switch size="xs" label={t('invert')} checked={shape.invert === true} disabled={readOnly} onChange={(e) => set('mask', { ...shape, invert: e.currentTarget.checked }, t('maskInvert'))} data-testid="mask-reframe-invert" />
          </Group>
          {slider('mask-reframe-x', t('centreX'), shape.x ?? 0.5, 0, 1, (v) => set('mask', { ...shape, x: v }, t('maskLabel')))}
          {slider('mask-reframe-y', t('centreY'), shape.y ?? 0.5, 0, 1, (v) => set('mask', { ...shape, y: v }, t('maskLabel')))}
          {slider('mask-reframe-w', t('width'), shape.w ?? 0.5, 0, 1, (v) => set('mask', { ...shape, w: v }, t('maskLabel')))}
          {slider('mask-reframe-h', t('height'), shape.h ?? 0.5, 0, 1, (v) => set('mask', { ...shape, h: v }, t('maskLabel')))}
          {slider('mask-reframe-feather', t('feather'), shape.feather ?? 0, 0, 0.5, (v) => set('mask', { ...shape, feather: v }, t('maskFeather')))}
          {!readOnly && (
            <Group justify="flex-end">
              <Button size="compact-xs" fz="xs" variant="subtle" color="red" aria-keyshortcuts={removeChord?.aria} title={removeChord?.display ? removeChord.tip(t('removeMask')) : undefined} onClick={() => set('mask', null, t('removeMask'))} data-testid="mask-reframe-remove">
                {t('removeMask')}
              </Button>
            </Group>
          )}
        </>
      ) : !readOnly ? (
        <Group gap={6}>
          <Button size="compact-xs" fz="xs" variant={quietAdd ? 'subtle' : 'light'} onClick={() => addMask('rect')} data-testid="mask-reframe-add-mask">
            {t('addMask')}
          </Button>
          <Button size="compact-xs" fz="xs" variant={quietAdd ? 'subtle' : 'light'} onClick={() => addMask('ellipse')} data-testid="mask-reframe-add-ellipse">
            {t('addEllipseMask')}
          </Button>
        </Group>
      ) : null}

      {part === 'mask' ? null : reframe ? (
        <>
          <Text size="xs" c="dimmed" data-testid="mask-reframe-reframe-help">
            {t('reframeHelp')}
          </Text>
          {slider('mask-reframe-point-x', t('subjectX'), reframe.x ?? 0.5, 0, 1, (v) => set('reframe', { ...reframe, x: v }, t('reframeLabel')))}
          {slider('mask-reframe-point-y', t('subjectY'), reframe.y ?? 0.5, 0, 1, (v) => set('reframe', { ...reframe, y: v }, t('reframeLabel')))}
          {!readOnly && (
            <Group justify="flex-end">
              <Button size="compact-xs" fz="xs" variant="subtle" color="red" aria-keyshortcuts={removeChord?.aria} title={removeChord?.display ? removeChord.tip(t('removeReframe')) : undefined} onClick={() => set('reframe', null, t('removeReframe'))} data-testid="mask-reframe-remove-reframe">
                {t('removeReframe')}
              </Button>
            </Group>
          )}
        </>
      ) : !readOnly ? (
        <Button size="compact-xs" fz="xs" variant="light" onClick={addReframe} data-testid="mask-reframe-add-reframe" w="fit-content">
          {t('addReframe')}
        </Button>
      ) : null}

      {part === undefined && (
        <Text size="12px" c="dimmed" data-testid="mask-reframe-note">
          {t('previewNote')}
        </Text>
      )}
    </Stack>
  );
}
