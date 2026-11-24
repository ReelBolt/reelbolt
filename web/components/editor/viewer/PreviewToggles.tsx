'use client';

import React from 'react';
import { Group, SegmentedControl, Stack, Switch, Text } from '@mantine/core';
import { PREVIEW_QUALITY_OPTIONS, type PreviewQuality } from '@/lib/editing/preview/device-budget';
import { useT } from '@/i18n';

export interface PreviewTogglesProps {
  playbackResolution: PreviewQuality;
  onPlaybackResolution: (value: PreviewQuality) => void;
  /** The Simple preview is on (chosen by the user, or by Auto on a low-budget device). */
  degraded: boolean;
  onDegraded: (value: boolean) => void;
  degradedAvailable: boolean;
  /** Why Auto started on the simple preview, when it did. */
  autoReason?: string | null;
}

/**
 * The preview controls, in plain words: "Preview quality" (Auto / Full / Half / Quarter - the size the
 * live preview decodes at; Auto picks by what the device can carry) and the "Simple preview" switch
 * (one clip at a time from its small copy; your edits are still exact in the final render).
 */
export function PreviewToggles({ playbackResolution, onPlaybackResolution, degraded, onDegraded, degradedAvailable, autoReason = null }: PreviewTogglesProps) {
  const t = useT('editor.viewer.previewToggles');
  return (
    <Stack gap="xs" data-testid="preview-toggles">
      <Group gap="sm" wrap="nowrap">
        <Text size="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }}>
          {t('previewQuality')}
        </Text>
        <SegmentedControl
          size="xs"
          value={playbackResolution}
          onChange={(v) => onPlaybackResolution(v as PreviewQuality)}
          data={PREVIEW_QUALITY_OPTIONS}
          aria-label={t('previewQuality2')}
          data-testid="preview-quality"
        />
      </Group>
      <Switch
        size="xs"
        label={t('simplePreview')}
        description={t('oneClipAtA')}
        checked={degraded}
        disabled={!degradedAvailable}
        onChange={(e) => onDegraded(e.currentTarget.checked)}
        data-testid="degraded-toggle"
      />
      {autoReason && (
        <Text size="xs" c="dimmed" data-testid="auto-preview-reason">
          {autoReason}
        </Text>
      )}
    </Stack>
  );
}
