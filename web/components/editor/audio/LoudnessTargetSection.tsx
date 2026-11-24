'use client';

import React, { useState } from 'react';
import { RenderChecks } from './RenderChecks';
import type { RenderCheck } from '@/lib/editing/audio/render-checks';
import { Alert, Button, Group, SegmentedControl, Stack, Text } from '@mantine/core';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';
import { LOUDNESS_TARGETS, compileMatchTarget, judgeLoudness, targetById, verdictMsg } from '@/lib/editing/audio/loudness-target';
import { useFormat, useT } from '@/i18n';
import type { FlushResult } from '../deliver/DeliverWorkspace';
import { AudioJobStatus } from './AudioJobStatus';
import { defaultAudioJobApi, useAudioJob, type AudioJobApi } from './useAudioJob';
import classes from './audio.module.css';

const TARGET_KEY = 'rb.editor.loudnessTarget';

export interface LoudnessTargetSectionProps {
  doc: EditTimeline;
  readOnly: boolean;
  projectId: string;
  revision: number;
  flushSave?: () => Promise<FlushResult>;
  onApply: (ops: readonly EditOp[], label: string) => void;
  api?: AudioJobApi;
}

/**
 * R08.4: the loudness of the whole timeline against where it will be heard. The number comes from
 * the real mix (T-011), not the live preview meter; "Match target" moves the track faders by the
 * difference in one undo step and measures again so the result is on screen.
 */
export function LoudnessTargetSection({ doc, readOnly, projectId, revision, flushSave, onApply, api = defaultAudioJobApi }: LoudnessTargetSectionProps) {
  const t = useT('editor-audio.loudness');
  const f = useFormat();
  const oneDecimal = (v: number) => f.number(v, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const [targetId, setTargetId] = useState<string>(() => {
    try {
      return targetById(typeof window === 'undefined' ? null : window.localStorage.getItem(TARGET_KEY)).id;
    } catch {
      return 'social';
    }
  });
  const rawTarget = targetById(targetId);
  const target = { ...rawTarget, label: t(`targets.${rawTarget.id}.label`), hint: t(`targets.${rawTarget.id}.hint`) };
  const job = useAudioJob('loudness', { projectId, timelineId: doc.id, flushSave, revision }, api);
  const [measured, setMeasured] = useState<number | null>(null);
  const [checks, setChecks] = useState<RenderCheck[] | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const hasAudio = doc.tracks.some((t) => t.kind === 'audio' && !t.mute && t.items.length > 0) || doc.tracks.some((t) => t.items.some((i) => i.type === 'mediaClip'));

  const measure = async () => {
    setNote(null);
    const result = await job.run();
    const lufs = result?.loudness?.integratedLufs;
    setMeasured(typeof lufs === 'number' ? lufs : null);
    setChecks(result?.loudness?.checks ?? null);
    return typeof lufs === 'number' ? lufs : null;
  };

  const verdict = judgeLoudness(measured, target.lufs);
  const match = async () => {
    if (verdict.kind === 'unmeasurable' || verdict.kind === 'on') return;
    const ops = compileMatchTarget(doc, verdict.delta);
    if (ops.length === 0) {
      setNote(t('noTrack'));
      return;
    }
    onApply(ops, t('matchLabel', { target: target.label, delta: `${verdict.delta > 0 ? '+' : ''}${oneDecimal(verdict.delta)}` }));
    setNote(t('moved', { direction: verdict.delta > 0 ? 'up' : 'down', db: oneDecimal(Math.abs(verdict.delta)) }));
    setMeasured(null);
    await new Promise((r) => setTimeout(r, 0));
    await measure();
  };

  const busy = job.state.kind === 'saving' || job.state.kind === 'running';
  return (
    <Stack gap={6} data-testid="loudness-target">
      <Text size="xs" c="dimmed">
        {t('where')}
      </Text>
      <SegmentedControl
        size="xs"
        fullWidth
        value={target.id}
        onChange={(v) => {
          setTargetId(v);
          try {
            window.localStorage.setItem(TARGET_KEY, v);
          } catch {
            /* private mode */
          }
        }}
        data={LOUDNESS_TARGETS.map((x) => ({ value: x.id, label: `${t(`targets.${x.id}.label`)} ${x.lufs}` }))}
        data-testid="loudness-target-chips"
      />
      <Text size="xs" c="dimmed">
        {t('summary', { label: target.label, lufs: target.lufs, hint: target.hint })}
      </Text>
      <Group gap={6}>
        <Button size="compact-xs" fz="xs" className={classes.touch} variant="light" loading={busy} disabled={!hasAudio} onClick={() => void measure()} data-testid="loudness-measure">
          {t('measureLoudness')}
        </Button>
        <Button size="compact-xs" fz="xs" className={classes.touch} disabled={readOnly || busy || verdict.kind === 'unmeasurable' || verdict.kind === 'on'} onClick={() => void match()} data-testid="loudness-match">
          {t('matchTarget')}
        </Button>
      </Group>
      {job.state.kind === 'running' && <AudioJobStatus kind="loudness" executionId={job.state.executionId} />}
      {job.state.kind === 'confirm' && (
        <Alert color="blue" variant="light" data-testid="loudness-confirm">
          <Text size="xs">{t('confirm', { credits: job.state.credits })}</Text>
          <Group gap={6} mt={4}>
            <Button size="compact-xs" fz="xs" className={classes.touch} onClick={() => void job.confirm()} data-testid="loudness-confirm-go">
              {t('measure')}
            </Button>
            <Button size="compact-xs" fz="xs" className={classes.touch} variant="subtle" onClick={job.cancel}>
              {t('notNow')}
            </Button>
          </Group>
        </Alert>
      )}
      {job.state.kind === 'error' && (
        <Alert color="red" variant="light" data-testid="loudness-error">
          <Text size="xs">{job.state.message}</Text>
        </Alert>
      )}
      {measured !== null && job.state.kind === 'done' && (
        <Text size="xs" data-testid="loudness-measured" data-lufs={measured.toFixed(2)} data-verdict={verdict.kind}>
          {t('measured', { lufs: oneDecimal(measured), verdict: t.msg(verdictMsg(verdict, target.label)) })}
        </Text>
      )}
      {measured !== null && job.state.kind === 'done' && <RenderChecks checks={checks} />}
      {job.state.kind === 'done' && measured === null && (
        <Text size="xs" c="dimmed" data-testid="loudness-measured" data-verdict="unmeasurable">
          {t('nothing')}
        </Text>
      )}
      {note && (
        <Text size="xs" c="dimmed" data-testid="loudness-note">
          {note}
        </Text>
      )}
    </Stack>
  );
}
