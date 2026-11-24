'use client';

import React, { useEffect, useRef, useState } from 'react';
import { Alert, Badge, Button, Group, Loader, Paper, Stack, Text } from '@mantine/core';
import { useIsTouchSurface } from '@/lib/editing/use-device-class';
import { IconWaveSine } from '@tabler/icons-react';
import type { EditTimeline } from '@/lib/editing/types';
import { useMediaPool } from '@/lib/hooks/use-media';
import {
  ANALYSIS_KINDS,
  NO_KIND,
  describeAnalysisError,
  describeKind,
  documentFileIds,
  estimateMediaAnalysis,
  fileMediaRef,
  isWorking,
  refreshAnalysis,
  startMediaAnalysis,
  useMediaAnalysisStatus,
  type AnalysisEstimate,
  type AnalysisKind,
} from '@/lib/editing/api/media-analysis';
import { useT } from '@/i18n';
import { useJobFor } from '@/lib/activity/ActivityProvider';
import type { ActivityJobState } from '@/lib/activity/types';
import { JobCost } from '@/components/activity/JobCost';
import { JobStatusBadge } from '@/components/activity/JobStatusBadge';
import { describeAnalysisErrorMsg } from '@/lib/editing/api/labels-msg';
import { analysisKindLabelMsg, describeKindMsg } from '@/lib/editing/api/labels-msg';

const FREE_KINDS: readonly AnalysisKind[] = ['silence', 'shots', 'motion', 'events'];
const TOUCH_TARGET = 44;

export interface AnalysePanelProps {
  doc: EditTimeline;
  projectId: string;
  readOnly: boolean;
  /** The file of the selected clip, to put first. */
  focusFileId?: string | null;
}

/** A kind's state in the activity vocabulary; `none` has no job and shows no badge. */
const KIND_ACTIVITY_STATE: Record<string, ActivityJobState | undefined> = { queued: 'waiting', running: 'running', ready: 'done', failed: 'failed' };

/** What the transcript will cost, in plain words, from the estimate. */
export function describeEstimate(estimate: AnalysisEstimate): string {
  if (estimate.unavailableReason) return estimate.unavailableReason;
  if (!estimate.billingEnabled) return 'The transcript is free on this account.';
  if (estimate.cached) return 'This file was already transcribed, so it costs nothing.';
  const credits = estimate.credits ?? 0;
  const seconds = estimate.seconds === null ? '' : ` for ${Math.round(estimate.seconds)} s of audio`;
  const balance = estimate.balanceCredits === null ? '' : ` You have ${estimate.balanceCredits} credits.`;
  return `The transcript costs about ${credits} credit${credits === 1 ? '' : 's'}${seconds}.${balance}`;
}

function FileRow({ projectId, fileId, name, readOnly, touch, focused }: { projectId: string; fileId: string; name: string; readOnly: boolean; touch: boolean; focused: boolean }) {
  const t = useT('editor.panels.analysePanel');
  const { data: status, mutate, error } = useMediaAnalysisStatus(projectId, fileId);
  const transcriptJob = useJobFor((j) => j.origin === 'server' && j.type === 'mediaAnalysis' && j.state !== 'done' && j.state !== 'failed' && j.state !== 'cancelled' && j.subject.mediaRef === fileMediaRef(fileId) && j.subject.analysisKind === 'transcript');
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [estimate, setEstimate] = useState<AnalysisEstimate | null>(null);
  const working = isWorking(status);
  const size = touch ? 'md' : 'compact-xs';
  const style = touch ? { minHeight: TOUCH_TARGET } : undefined;

  // When a run finishes, tell every panel reading analysis to look again.
  const wasWorking = useRef(false);
  useEffect(() => {
    if (wasWorking.current && !working) void refreshAnalysis();
    wasWorking.current = working;
  }, [working]);

  const start = async (kinds: readonly AnalysisKind[]) => {
    setBusy(true);
    setMessage(null);
    try {
      await startMediaAnalysis(projectId, fileMediaRef(fileId), kinds);
      setEstimate(null);
      await mutate();
    } catch (e) {
      setMessage(t.msg(describeAnalysisErrorMsg(e)));
    } finally {
      setBusy(false);
    }
  };

  const askTranscript = async () => {
    setBusy(true);
    setMessage(null);
    try {
      const result = await estimateMediaAnalysis(projectId, fileMediaRef(fileId), ['transcript']);
      setEstimate(result);
    } catch (e) {
      setMessage(t.msg(describeAnalysisErrorMsg(e)));
    } finally {
      setBusy(false);
    }
  };

  const freeDone = FREE_KINDS.every((k) => status?.kinds[k]?.state === 'ready');
  const transcriptState = status?.kinds.transcript?.state ?? 'none';
  const canConfirm = estimate !== null && estimate.unavailableReason === null && estimate.affordable;

  return (
    <Paper withBorder p="xs" radius="sm" data-testid={`analyse-file-${fileId}`} data-focused={focused} style={focused ? { borderColor: 'var(--mantine-color-blue-5)' } : undefined}>
      <Stack gap={6}>
        <Group justify="space-between" wrap="nowrap" gap="xs">
          <Text size="sm" fw={600} truncate>
            {name}
          </Text>
          {working && (
            <Group gap={4} wrap="nowrap" role="status" data-testid={`analyse-working-${fileId}`}>
              <Loader size={12} />
              <Text size="xs" c="dimmed">{t('workingOnIt')}</Text>
            </Group>
          )}
        </Group>
        <Stack gap={2}>
          {ANALYSIS_KINDS.map((kind) => {
            const k = status?.kinds[kind] ?? NO_KIND;
            return (
              <Group key={kind} gap="xs" wrap="nowrap" align="center" data-testid={`analyse-kind-${fileId}-${kind}`} data-state={k.state}>
                <Badge size="sm" fz="xs" variant="light" color="gray" style={{ minWidth: 84 }}>
                  {t.msg(analysisKindLabelMsg(kind))}
                </Badge>
                {KIND_ACTIVITY_STATE[k.state] && <JobStatusBadge state={KIND_ACTIVITY_STATE[k.state] as ActivityJobState} />}
                <Text size="xs" c={k.state === 'failed' ? 'red' : 'dimmed'}>
                  {t.msg(describeKindMsg(kind, k))}
                </Text>
              </Group>
            );
          })}
        </Stack>
        {transcriptJob && <JobCost job={transcriptJob} />}
        {error && <Alert color="red" p="xs">{t('theAnalysisStateCould')}</Alert>}
        <Group gap="xs">
          <Button size={size} fz="xs" style={style} leftSection={<IconWaveSine size={14} />} disabled={readOnly || busy || working} loading={busy && estimate === null} onClick={() => void start(FREE_KINDS)} data-testid={`analyse-run-${fileId}`}>
            {freeDone ? t('analyseAgain') : t('analyse')}
          </Button>
          <Button size={size} fz="xs" style={style} variant="light" disabled={readOnly || busy || working || transcriptState === 'ready'} onClick={() => void askTranscript()} data-testid={`analyse-transcribe-${fileId}`}>
            {transcriptState === 'ready' ? t('transcribed') : t('transcribeSpeech')}
          </Button>
        </Group>
        {estimate && (
          <Alert color={canConfirm ? 'blue' : 'orange'} p="xs" data-testid={`analyse-estimate-${fileId}`}>
            <Stack gap={6}>
              <Text size="xs">{describeEstimate(estimate)}</Text>
              {!estimate.affordable && estimate.unavailableReason === null && <Text size="xs">{t('thatIsMoreThan')}</Text>}
              <Group gap="xs">
                <Button size={size} fz="xs" style={style} disabled={!canConfirm || busy} loading={busy} onClick={() => void start(['transcript'])} data-testid={`analyse-transcribe-confirm-${fileId}`}>
                  {estimate.cached || !estimate.billingEnabled || (estimate.credits ?? 0) === 0 ? t('transcribe') : t('transcribeForCredits', { credits: estimate.credits })}
                </Button>
                <Button size={size} fz="xs" style={style} variant="subtle" onClick={() => setEstimate(null)} data-testid={`analyse-transcribe-cancel-${fileId}`}>
                  {t('notNow')}
                </Button>
              </Group>
            </Stack>
          </Alert>
        )}
        {message && (
          <Text size="xs" c="red" role="alert" data-testid={`analyse-message-${fileId}`}>
            {message}
          </Text>
        )}
      </Stack>
    </Paper>
  );
}

/**
 * Analysis on demand (R09.1-R09.2): every media file the timeline shows, with what has been found
 * in it (quiet stretches, shots, motion, speech) and a button to find it. Quiet stretches, shots and
 * motion are free and start at once; the transcript shows its credit estimate first and waits for a
 * confirmation. The Transcript, Subtitles, Remove silence, Cut and Takes tools read what is found here.
 */
export function AnalysePanel({ doc, projectId, readOnly, focusFileId = null }: AnalysePanelProps) {
  const t = useT('editor.panels.analysePanel');
  const { data: pool } = useMediaPool(projectId);
  const touch = useIsTouchSurface();
  const ids = documentFileIds(doc);
  const ordered = focusFileId && ids.includes(focusFileId) ? [focusFileId, ...ids.filter((i) => i !== focusFileId)] : ids;
  const nameOf = (id: string): string => (pool ?? []).find((p) => p.mediaRef === fileMediaRef(id))?.fileName ?? t('mediaFile');
  return (
    <Stack gap="xs" data-testid="analyse-panel">
      <Text size="sm" fw={600}>
        {t('analyse2')}
      </Text>
      <Text size="xs" c="dimmed">
        {t('findTheQuietStretches')}
      </Text>
      {ordered.length === 0 ? (
        <Text size="xs" c="dimmed" data-testid="analyse-none">{t('putAVideoOr')}</Text>
      ) : (
        ordered.map((id) => <FileRow key={id} projectId={projectId} fileId={id} name={nameOf(id)} readOnly={readOnly} touch={touch} focused={id === focusFileId} />)
      )}
    </Stack>
  );
}
