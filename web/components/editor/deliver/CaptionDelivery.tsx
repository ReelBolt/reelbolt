'use client';

import React from 'react';
import { Button, Group, SegmentedControl, Stack, Text } from '@mantine/core';
import { editTimelineExportUrl } from '@/lib/editing/sync/api';
import { type CaptionDelivery as Choice } from '@/lib/editing/subtitles/delivery';
import { useT } from '@/i18n';

export interface CaptionDeliveryProps {
  projectId: string;
  timelineId: string;
  value: Choice;
  readOnly: boolean;
  /** Called with the choice; the caller switches the caption tracks on or off. */
  onChoose: (choice: Choice) => void;
}


/** R09.7: burn the captions in, hand them over as separate files, or both. */
export function CaptionDelivery({ projectId, timelineId, value, readOnly, onChoose }: CaptionDeliveryProps) {
  const t = useT('editor.deliver.captionDelivery');
  const choice = value;
  const files = choice === 'sidecar' || choice === 'both';
  return (
    <Stack gap={4} data-testid="deliver-captions">
      <Text size="sm" fw={600}>
        {t('captions')}
      </Text>
      <SegmentedControl
        size="xs"
        fullWidth
        disabled={readOnly}
        value={choice}
        onChange={(v) => onChoose(v as Choice)}
        data={[
          { value: 'burn', label: t('burnIntoTheVideo') },
          { value: 'sidecar', label: t('separateFilesOnly') },
          { value: 'both', label: t('both') },
        ]}
        styles={{ label: { minHeight: 44, display: 'flex', alignItems: 'center', justifyContent: 'center' } }}
        data-testid="deliver-captions-choice"
      />
      <Text size="xs" c="dimmed" data-testid="deliver-captions-note">
        {t(`description.${choice}`)}
      </Text>
      {files && (
        <Group gap="xs" data-testid="deliver-captions-files">
          <Button component="a" size="xs" fz="xs" variant="light" mih={44} href={editTimelineExportUrl(projectId, timelineId, 'srt')} download data-testid="deliver-captions-srt">
            {t('downloadSrt')}
          </Button>
          <Button component="a" size="xs" fz="xs" variant="light" mih={44} href={editTimelineExportUrl(projectId, timelineId, 'vtt')} download data-testid="deliver-captions-vtt">
            {t('downloadWebvtt')}
          </Button>
        </Group>
      )}
    </Stack>
  );
}
