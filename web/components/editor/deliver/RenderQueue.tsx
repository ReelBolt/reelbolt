'use client';

import { lutReasonFromFailureMessage, lutReasonMsg } from '@/lib/editing/color/lut-reason';
import React from 'react';
import { Button, Group, Stack, Text } from '@mantine/core';
import { IconDownload, IconPlayerStop, IconRefresh, IconRepeat, IconX } from '@tabler/icons-react';
import { PageEmptyState } from '../ui/PageEmptyState';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';
import { formatBytes } from '@/lib/files/model';
import { humanizeError } from '@/lib/editing/humanize-error';
import { describeRenderNotesMsg } from './render-notes';
import { formatLength, formatNameMsg } from './deliver-model';
import { RenderNotes } from './RenderNotes';
import { useFormat, useT } from '@/i18n';
import classes from './Deliver.module.css';
import type { Msg } from '@/i18n/msg';
import { useDownloadGuard } from '@/lib/outputs/output-check';
import { useJobFor } from '@/lib/activity/ActivityProvider';
import type { ActivityJobState } from '@/lib/activity/types';
import { JobProgress } from '@/components/activity/JobProgress';
import { JobStatusBadge } from '@/components/activity/JobStatusBadge';
import { ReportProblem } from '@/components/feedback/ReportProblem';
import { localJob } from '@/lib/editing/background/activity-job';

export interface RenderProgress {
  /** The execution the progress belongs to. */
  executionId: string;
  /** The stage the renderer reported ("Rendering region 2/5", "Mixing audio", …). */
  stage: string;
  /** 0..100, or null when the stage carried no percentage. */
  percent: number | null;
}

export interface RenderQueueProps {
  renders: readonly EditTimelineRenderSummary[];
  /** Live progress for the render the workspace is following, if any. */
  progress: RenderProgress | null;
  /** The URL that saves a finished render, by step result id and the file name to save it as. */
  outputUrl: (stepResultId: string, fileName: string) => string;
  onCancel: (render: EditTimelineRenderSummary) => void;
  /** A failed row's "Try again": starts a render with the settings shown above. */
  onRetry?: (render: EditTimelineRenderSummary) => void;
  /** A failed row's "Remove": takes the row out of the list. */
  onRemove?: (render: EditTimelineRenderSummary) => void;
  loading: boolean;
  /** RD-5: item id → name in the current document, so a skipped item is named the way the user named it. */
  itemNames?: ReadonlyMap<string, string>;
  /**
   * Wave 5, "Render again": puts the settings a finished, stopped or failed render was started with back into the
   * form. Offered only for rows `canRenderAgain` answers true for: the row itself carries just the format, so a
   * render whose settings are not known is not offered the action rather than guessed at.
   */
  onRenderAgain?: (render: EditTimelineRenderSummary) => void;
  canRenderAgain?: (render: EditTimelineRenderSummary) => boolean;
  /** Wave 5: the URL that saves a caption file a render made, by its project-file id. Absent = the files are not listed here. */
  captionFileUrl?: (fileId: string) => string;
  /** Wave 5: the render shown in the film card above; its caption files are listed there, not twice. */
  featuredExecutionId?: string | null;
  /** Wave 5: the empty state's first action (move to the preset list). Absent = the sentence alone. */
  onFirstAction?: () => void;
}

const NO_NAMES: ReadonlyMap<string, string> = new Map();

/** A render row's status in the activity vocabulary (the same badge as the tray). */
const ACTIVITY_STATE: Record<string, ActivityJobState> = {
  Queued: 'waiting',
  Running: 'running',
  Passed: 'done',
  Failed: 'failed',
  Cancelled: 'cancelled',
};

/** The running row's progress: the queue's own live frame when it follows this render, else the activity feed's. */
function RowProgress({ executionId, live }: { executionId: string; live: RenderProgress | null }) {
  const t = useT('editor.deliver.renderQueue');
  const fed = useJobFor((j) => j.executionId === executionId);
  if (!live && !fed) return null;
  const base = fed ?? localJob({ state: 'running', type: 'render', executionId });
  const job = { ...base, state: 'running' as const, liveProgress: live && live.percent !== null ? live.percent / 100 : base.liveProgress, liveStage: null };
  return (
    <Stack gap={2}>
      {live && (
        <Text size="xs" data-testid="render-progress-stage">
          {live.stage}
        </Text>
      )}
      <div aria-label={t('renderProgress')}>
        <JobProgress job={job} />
      </div>
    </Stack>
  );
}

const ROW_BUTTON = { minHeight: 44 } as const;

/** The failure of a render in a sentence for a person: never a code, a GUID or an exception name. */
export function failureSentence(message: string | null, fallback = 'This render did not finish. Check that the media in your timeline is still in the project, then try again.'): string {
  // An engine code ("ASSET_UNRESOLVED: ...") is not a sentence for a person, whatever follows it.
  if (message && /\b[A-Z][A-Z0-9]*_[A-Z0-9_]+\b/.test(message)) return fallback;
  return humanizeError(message ? new Error(message) : null, fallback);
}

/**
 * The render queue: one row per `TimelineRender` execution of this timeline, newest first, each with its state
 * in a word (Waiting, Rendering, Finished, Did not finish, Stopped). A waiting or running row can be stopped; a
 * failed row says why in plain words and offers Try again and Remove. Progress is the `step.progress` stream's own
 * words: the row never invents a percentage it was not sent. A finished row also says what the render left out
 * and warned about (RD-5), when it says anything.
 */
export function RenderQueue({ renders, progress, outputUrl, onCancel, onRetry, onRemove, loading, itemNames = NO_NAMES, onRenderAgain, canRenderAgain, captionFileUrl, onFirstAction, featuredExecutionId = null }: RenderQueueProps) {
  const t = useT('editor.deliver.renderQueue');
  const fmt = useFormat();
  const guardDownload = useDownloadGuard();
  const tl = useT('editor-color.lutReason');
  /** A render the engine failed on a LUT says which rule the file broke (the message prefix is `LUT_INVALID:` / `LUT_UNSUPPORTED:`). */
  const lutFailure = (message: string | null): string | null => {
    const reason = lutReasonFromFailureMessage(message);
    return reason ? tl.msg(lutReasonMsg(reason)) : null;
  };
  const show = (m: Msg | string): string => (typeof m === 'string' ? m : t.msg(m));
  if (!loading && renders.length === 0) {
    return (
      <PageEmptyState page="deliver" hint={onFirstAction ? t('chooseAPresetThen') : t('chooseAFormatAbove')} action={onFirstAction ? { label: t('chooseAPreset'), onClick: onFirstAction, testId: 'render-queue-first-action' } : undefined} testId="render-queue-empty" compact />
    );
  }
  if (loading && renders.length === 0) {
    return (
      <Text size="sm" c="dimmed" role="status" data-testid="render-queue-loading">
        {t('loadingRenders')}
      </Text>
    );
  }
  return (
    <Stack gap={6} data-testid="render-queue">
      {renders.map((render) => {
        const active = render.status === 'Queued' || render.status === 'Running';
        const live = progress && progress.executionId === render.executionId ? progress : null;
        const notes = active ? null : describeRenderNotesMsg(render, itemNames);
        const failed = render.status === 'Failed';
        const again = !active && !!onRenderAgain && (canRenderAgain?.(render) ?? false);
        const when = render.completedAt
          ? t('finishedWhen', { when: fmt.relative(render.completedAt) })
          : render.startedAt || render.createdAt
            ? t('startedWhen', { when: fmt.relative((render.startedAt ?? render.createdAt) as string) })
            : null;
        const facts = [show(formatNameMsg(render.preset)), active ? null : formatLength(render.durationSec), !active && render.sizeBytes ? formatBytes(render.sizeBytes) : null, when].filter((part): part is string => !!part);
        const captionFiles = !active && captionFileUrl && render.executionId !== featuredExecutionId ? (render.captionFiles ?? []) : [];
        return (
          <Stack key={render.executionId} gap={4} p="xs" className={classes.queueRow} data-testid={`render-row-${render.executionId}`}>
            <Group justify="space-between" wrap="wrap" gap={6}>
              <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
                <span data-testid="render-status">
                  <JobStatusBadge state={ACTIVITY_STATE[render.status] ?? 'waiting'} />
                </span>
                <Text size="sm" fw={600} truncate title={render.outputFileName}>
                  {render.outputFileName}
                </Text>
              </Group>
              <Group gap={6} wrap="wrap">
                {active && (
                  <Button size="sm" fz="xs" variant="light" color="red" leftSection={<IconPlayerStop size={16} />} onClick={() => onCancel(render)} style={ROW_BUTTON} data-testid="render-cancel">
                    {t('stop')}
                  </Button>
                )}
                {render.status === 'Passed' && render.hasOutput && render.stepResultId && (
                  <Button size="sm" fz="xs" variant="light" component="a" href={outputUrl(render.stepResultId, render.outputFileName)} onClick={guardDownload(outputUrl(render.stepResultId, render.outputFileName), render.outputFileName)} download={render.outputFileName} leftSection={<IconDownload size={16} />} style={ROW_BUTTON} data-testid="render-download">
                    {t('download')}
                  </Button>
                )}
                {failed && onRetry && (
                  <Button size="sm" fz="xs" variant="light" leftSection={<IconRefresh size={16} />} onClick={() => onRetry(render)} style={ROW_BUTTON} data-testid="render-retry">
                    {t('tryAgain')}
                  </Button>
                )}
                {again && (
                  <Button size="sm" fz="xs" variant="light" leftSection={<IconRepeat size={16} />} onClick={() => onRenderAgain?.(render)} style={ROW_BUTTON} data-testid="render-again">
                    {t('renderAgain')}
                  </Button>
                )}
                {failed && onRemove && (
                  <Button size="sm" fz="xs" variant="subtle" color="gray" leftSection={<IconX size={16} />} onClick={() => onRemove(render)} style={ROW_BUTTON} data-testid="render-remove">
                    {t('remove')}
                  </Button>
                )}
              </Group>
            </Group>
            <Text size="xs" c="dimmed" data-testid="render-facts">
              {facts.join(' · ')}
            </Text>
            {render.status === 'Running' && <RowProgress executionId={render.executionId} live={live} />}
            {failed && (
              <Text size="xs" c="red" data-testid="render-error">
                {lutFailure(render.errorMessage) ?? failureSentence(render.errorMessage, t('failureFallback'))}
              </Text>
            )}
            {failed && <ReportProblem run={{ status: 'Failed' }} executionId={render.executionId} showOnUs={false} />}
            {render.status === 'Cancelled' && (
              <Text size="xs" c="dimmed" data-testid="render-stopped">
                {t('stoppedNote')}
              </Text>
            )}
            {captionFiles.length > 0 && captionFileUrl && (
              <Group gap="xs" wrap="wrap" data-testid="render-caption-files">
                <Text size="xs" fw={600}>
                  {t('captionFiles')}
                </Text>
                {captionFiles.map((file) => (
                  <Button key={file.fileId} component="a" size="sm" fz="xs" variant="subtle" href={captionFileUrl(file.fileId)} download={file.fileName} leftSection={<IconDownload size={14} />} className={classes.touchTarget} data-testid={`render-caption-${file.format}`}>
                    {file.fileName}
                  </Button>
                ))}
              </Group>
            )}
            {notes && <RenderNotes notes={notes} />}
          </Stack>
        );
      })}
    </Stack>
  );
}
