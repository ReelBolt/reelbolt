'use client';

import { useId } from 'react';
import Link from 'next/link';
import { Anchor, Select, SegmentedControl, Stack, Text, Tooltip } from '@mantine/core';
import type { EditingStyle, RunOutputFormat, RunResolutionChoice, StylePreset } from '@/lib/types/styles';
import { useT } from '@/i18n';
import {
  BILLING_SETTINGS_PATH,
  NO_STYLE,
  RUN_FORMAT_CHOICES,
  RUN_RESOLUTION_CHOICES,
  configFor,
  describeStyle,
  fitsOutputCap,
  outputCapMsg,
  type StyleChoiceValue,
} from './style-options';

/** One independently renderable control of the picker. */
export type RunStyleControl = 'style' | 'shape' | 'resolution';

const ALL_CONTROLS: readonly RunStyleControl[] = ['style', 'shape', 'resolution'];

interface RunStylePickerProps {
  /**
   * Which controls to render; all three by default. The app view renders each as an independent
   * input, so a control that is not listed needs none of its props.
   */
  controls?: readonly RunStyleControl[];
  presets?: StylePreset[] | undefined;
  styles?: EditingStyle[] | undefined;
  style?: StyleChoiceValue;
  onStyleChange?: (value: StyleChoiceValue) => void;
  format?: RunOutputFormat;
  onFormatChange?: (format: RunOutputFormat) => void;
  /** An extra sentence under the shape hint (e.g. that the default follows the chosen clips). */
  formatNote?: string;
  resolution?: RunResolutionChoice;
  onResolutionChange?: (resolution: RunResolutionChoice) => void;
  /**
   * The plan's largest output short side in pixels; null when unlimited or unknown (billing off).
   * Resolutions above it are offered but disabled, with the reason in text.
   */
  maxOutputHeight?: number | null;
}

/**
 * The run-time choices beyond the brief: an editing style (a built-in preset or one of the user's
 * saved styles) and, in one click each, the shape and the resolution of the video. All apply to
 * this run only.
 */
export function RunStylePicker({
  controls = ALL_CONTROLS, presets, styles, style = NO_STYLE, onStyleChange, format = 'Original', onFormatChange, formatNote,
  resolution = 'Auto', onResolutionChange, maxOutputHeight = null,
}: RunStylePickerProps) {
  // Ids are per instance, so two pickers on one page (the app view renders each control on its own) never collide.
  const t = useT('styles.picker');
  const tAll = useT();
  const uid = useId();
  const formatLabelId = `${uid}-format-label`;
  const resolutionLabelId = `${uid}-resolution-label`;
  const resolutionHintId = `${uid}-resolution-hint`;
  const showStyle = controls.includes('style');
  const showShape = controls.includes('shape');
  const showResolution = controls.includes('resolution');
  const data = [
    { group: t('noStyleGroup'), items: [{ value: NO_STYLE, label: t('noStyle') }] },
    ...(presets && presets.length > 0
      ? [{ group: t('builtInGroup'), items: presets.map((p) => ({ value: `preset:${p.key}`, label: p.name })) }]
      : []),
    ...(styles && styles.length > 0
      ? [{ group: t('yourGroup'), items: styles.map((s) => ({ value: `style:${s.id}`, label: s.name })) }]
      : []),
  ];
  const formatHint = RUN_FORMAT_CHOICES.find((o) => o.value === format)?.hint;
  const resolutionHint = RUN_RESOLUTION_CHOICES.find((o) => o.value === resolution)?.hint;
  const summary = describeStyle(configFor(style, presets, styles), tAll);
  const presetDescription = style.startsWith('preset:')
    ? presets?.find((p) => `preset:${p.key}` === style)?.description
    : undefined;

  return (
    <Stack gap="sm">
      {showStyle && (
      <Select
        label={t('styleLabel')}
        description={t('styleHint')}
        data={data}
        value={style}
        onChange={(v) => onStyleChange?.(v ?? NO_STYLE)}
        allowDeselect={false}
        comboboxProps={{ withinPortal: true }}
      />
      )}
      {showStyle && (presetDescription || summary) && (
        <Text size="xs" c="dimmed" mt={-6}>
          {presetDescription ?? summary}
        </Text>
      )}
      {showShape && (
      <div>
        <Text size="sm" fw={500} mb={4} id={formatLabelId}>
          {t('shape')}
        </Text>
        <SegmentedControl
          aria-labelledby={formatLabelId}
          fullWidth
          value={format}
          onChange={(v) => onFormatChange?.(v as RunOutputFormat)}
          data={RUN_FORMAT_CHOICES.map((o) => ({
            value: o.value,
            label: (
              <Tooltip label={tAll.msg(o.hint)} withinPortal>
                <span>{tAll.msg(o.label)}</span>
              </Tooltip>
            ),
          }))}
        />
        <Text size="xs" c="dimmed" mt={4}>
          {formatHint ? tAll.msg(formatHint) : null}
          {formatNote ? ` ${formatNote}` : null}
        </Text>
      </div>
      )}
      {showResolution && (
      <div>
        <Text size="sm" fw={500} mb={4} id={resolutionLabelId}>
          {t('resolution')}
        </Text>
        <SegmentedControl
          aria-labelledby={resolutionLabelId}
          aria-describedby={resolutionHintId}
          fullWidth
          value={resolution}
          onChange={(v) => onResolutionChange?.(v as RunResolutionChoice)}
          data={RUN_RESOLUTION_CHOICES.map((o) => {
            const allowed = fitsOutputCap(o.shortSide, maxOutputHeight);
            return {
              value: o.value,
              disabled: !allowed,
              label: (
                <Tooltip label={tAll.msg(allowed || maxOutputHeight === null ? o.hint : outputCapMsg(maxOutputHeight))} withinPortal>
                  <span>{tAll.msg(o.label)}</span>
                </Tooltip>
              ),
            };
          })}
        />
        <Text size="xs" c="dimmed" mt={4} id={resolutionHintId}>
          {resolutionHint ? tAll.msg(resolutionHint) : null}
        </Text>
        {/* The reason an option is unavailable, in text and not only in a tooltip. */}
        {maxOutputHeight !== null && RUN_RESOLUTION_CHOICES.some((o) => !fitsOutputCap(o.shortSide, maxOutputHeight)) && (
          <Text size="xs" c="dimmed" mt={2} data-testid="run-resolution-cap">
            {tAll.msg(outputCapMsg(maxOutputHeight))}{' '}
            <Anchor component={Link} href={BILLING_SETTINGS_PATH} size="xs">
              {t('seePlans')}
            </Anchor>
          </Text>
        )}
      </div>
      )}
    </Stack>
  );
}
