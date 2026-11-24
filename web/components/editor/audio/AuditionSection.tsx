'use client';

import React, { useEffect, useRef } from 'react';
import { Alert, Button, Group, Stack, Text } from '@mantine/core';
import { IconPlayerPlayFilled } from '@tabler/icons-react';
import type { EditTimeline } from '@/lib/editing/types';
import { auditionRange, auditionUrl } from '@/lib/editing/audio/audio-api';
import type { FlushResult } from '../deliver/DeliverWorkspace';
import { AudioJobStatus } from './AudioJobStatus';
import { defaultAudioJobApi, useAudioJob, type AudioJobApi } from './useAudioJob';
import { useT } from '@/i18n';
import classes from './audio.module.css';
import { CheckedAudio } from '@/components/outputs/CheckedVideo';

export interface AuditionSectionProps {
  doc: EditTimeline;
  projectId: string;
  revision: number;
  playheadFrame: number;
  flushSave?: () => Promise<FlushResult>;
  api?: AudioJobApi;
}

const timelineEnd = (doc: EditTimeline): number => {
  let end = 0;
  for (const track of doc.tracks) for (const item of track.items) if (item.recordOut > end) end = item.recordOut;
  return end;
};

/**
 * R08.6: hear the sound processing now. The server renders about ten seconds around the playhead
 * through the same audio mix the final render uses and the clip plays here; it is a throw-away
 * preview, never a file in the project.
 */
export function AuditionSection({ doc, projectId, revision, playheadFrame, flushSave, api = defaultAudioJobApi }: AuditionSectionProps) {
  const t = useT('editor-audio.audition');
  const job = useAudioJob('audition', { projectId, timelineId: doc.id, flushSave, revision }, api);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const fps = doc.settings.rate.den > 0 ? doc.settings.rate.num / doc.settings.rate.den : 30;
  const range = auditionRange(playheadFrame, timelineEnd(doc), fps);

  const src = job.state.kind === 'done' && job.state.job.stepResultId ? auditionUrl(projectId, job.state.job.stepResultId) : null;
  useEffect(() => {
    if (src) audioRef.current?.play?.()?.catch?.(() => undefined);
  }, [src]);

  const busy = job.state.kind === 'saving' || job.state.kind === 'running';
  return (
    <Stack gap={6} data-testid="audition-section">
      <Text size="xs" c="dimmed" data-testid="audio-processing-note">
        {t('note')}
      </Text>
      <Group gap={6}>
        <Button size="compact-xs" fz="xs" className={classes.touch} variant="light" leftSection={<IconPlayerPlayFilled size={12} />} loading={busy} disabled={!range} onClick={() => range && void job.run(range)} data-testid="audition-run">
          {t('run')}
        </Button>
      </Group>
      {job.state.kind === 'running' && <AudioJobStatus kind="audition" executionId={job.state.executionId} />}
      {job.state.kind === 'error' && (
        <Alert color="red" variant="light" data-testid="audition-error">
          <Text size="xs">{job.state.message}</Text>
        </Alert>
      )}
      {src && job.state.kind === 'done' && (
        <CheckedAudio ref={audioRef} src={src} controls preload="auto" style={{ width: '100%' }} data-testid="audition-player" aria-label={t('player')} />
      )}
    </Stack>
  );
}
