'use client';

import { JobProgress } from '@/components/activity/JobProgress';
import { localJob } from '@/lib/editing/background/activity-job';
import React, { useMemo, useSyncExternalStore } from 'react';
import { Alert, Box, Button, Group, Popover, Stack, Text, UnstyledButton } from '@mantine/core';
import { IconAlertTriangle, IconInfoCircle, IconLoader2 } from '@tabler/icons-react';
import type { EditTimeline, MediaClip } from '@/lib/editing/types';
import { useIsTouchSurface } from '@/lib/editing/use-device-class';
import {
  readinessRows,
  readinessWords,
  summarizeReadiness,
  viewerReason,
  type MissingLayer,
  type ProxyStatus,
  type ReadinessStore,
  type ViewerReason,
} from '@/lib/editing/preview';
import type { ContextLoss } from '@/lib/editing/preview';
import { useT } from '@/i18n';
import { previewText } from './preview-text';
import { previewSessionReasonMsg, readinessWordsMsg } from '@/lib/editing/preview/labels-msg';

/** The 12 px floor of the editor: nothing in the viewer's status area is smaller. */
const STATUS_TEXT = 12;

export interface ReadinessChipProps {
  doc: EditTimeline;
  readiness: ReadinessStore;
  proxyStatusFor: (clip: MediaClip) => ProxyStatus;
  /** Bumped when a proxy lookup answers (the rows are recomputed). */
  proxyVersion: number;
  onRetry?: (mediaRefs: string[]) => void;
  onPrepareAll?: () => void;
  onJumpToClip?: (itemId: string) => void;
}

/**
 * The project-level preview line: "Preparing preview 42%", "Preview failed for 2 clips", or nothing when
 * every picture source is ready. It opens the per-clip list with "Try again" and "Prepare all".
 */
export function ReadinessChip({ doc, readiness, proxyStatusFor, proxyVersion, onRetry, onPrepareAll, onJumpToClip }: ReadinessChipProps) {
  const t = useT('editor.viewer.viewerStatus');
  const touchH = useIsTouchSurface() ? 44 : undefined;
  const version = useSyncExternalStore(readiness.subscribe, readiness.getVersion, readiness.getVersion);
  const rows = useMemo(
    () => readinessRows(doc, readiness, proxyStatusFor),
    // version / proxyVersion are the signals that the store or a lookup changed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [doc, readiness, proxyStatusFor, version, proxyVersion],
  );
  const summary = useMemo(() => summarizeReadiness(rows), [rows]);
  const notReady = summary.preparing + summary.failed + summary.offline;
  if (summary.total === 0 || notReady === 0) return null;

  const failed = rows.filter((r) => r.readiness.state === 'failed');
  const label =
    summary.failed > 0
      ? t('previewFailedFor', { n: summary.failed })
      : summary.preparing > 0
        ? summary.percent !== null
          ? t('preparingPreview4', { percent: summary.percent })
          : t('preparingPreview3')
        : t('sourcesOffline', { n: summary.offline });
  const color = summary.failed > 0 ? 'var(--mantine-color-red-4)' : summary.preparing > 0 ? 'var(--mantine-color-blue-3)' : 'var(--mantine-color-orange-4)';

  return (
    <Popover width={340} position="bottom-end" withArrow shadow="md" trapFocus>
      <Popover.Target>
        <UnstyledButton
          data-testid="readiness-chip"
          data-state={summary.failed > 0 ? 'failed' : summary.preparing > 0 ? 'preparing' : 'offline'}
          aria-label={t('showTheClips', { label })}
          style={{ display: 'inline-flex', alignItems: 'center', gap: 6, minHeight: touchH ?? 32, minWidth: touchH, padding: '0 6px', color, fontSize: STATUS_TEXT }}
        >
          {summary.failed > 0 ? <IconAlertTriangle size={14} /> : <IconLoader2 size={14} />}
          <span data-testid="readiness-label">{label}</span>
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown p="sm" data-testid="readiness-list">
        <Stack gap="xs">
          <Text fw={600} size="sm">
            {t('previewOfThisTimeline')}
          </Text>
          <Stack gap={8} mah={260} style={{ overflowY: 'auto' }}>
            {rows.map((row) => (
              <Stack key={row.mediaRef} gap={2} data-testid="readiness-row" data-state={row.readiness.state}>
                <Group justify="space-between" wrap="nowrap" gap="xs">
                  <UnstyledButton onClick={() => onJumpToClip?.(row.itemId)} style={{ minHeight: touchH ?? 28, minWidth: 0, flex: 1, textAlign: 'left' }}>
                    <Text size="sm" truncate="end">
                      {row.name || t('clip')}
                    </Text>
                  </UnstyledButton>
                  {row.readiness.state === 'failed' && (
                    <Button size="compact-sm" fz="xs" mih={touchH} variant="light" color="red" onClick={() => onRetry?.([row.mediaRef])} data-testid="readiness-retry">
                      {t('tryAgain')}
                    </Button>
                  )}
                </Group>
                <Text size="sm" c="dimmed" style={{ fontSize: STATUS_TEXT }}>
                  {t.msg(readinessWordsMsg(row.readiness))}
                </Text>
                {row.readiness.state === 'preparing' && row.readiness.percent !== null && <JobProgress size="xs" job={localJob({ state: 'running', type: 'mediaPrep', progress: row.readiness.percent / 100 })} />}
              </Stack>
            ))}
          </Stack>
          <Group gap="xs" justify="space-between">
            <Text size="sm" c="dimmed" style={{ fontSize: STATUS_TEXT }}>
              {t('ofReady', { ready: summary.ready, total: summary.total })}
            </Text>
            <Group gap="xs">
              {failed.length > 1 && (
                <Button size="compact-sm" fz="xs" mih={touchH} variant="default" onClick={() => onRetry?.(failed.map((r) => r.mediaRef))}>
                  {t('tryAllAgain')}
                </Button>
              )}
              <Button size="compact-sm" fz="xs" mih={touchH} onClick={() => onPrepareAll?.()} data-testid="prepare-all">
                {t('prepareAll')}
              </Button>
            </Group>
          </Group>
        </Stack>
      </Popover.Dropdown>
    </Popover>
  );
}

export interface ViewerReasonOverlayProps {
  doc: EditTimeline;
  missing: readonly MissingLayer[];
  readiness: ReadinessStore | null;
  proxyStatusFor: (clip: MediaClip) => ProxyStatus;
  proxyVersion: number;
  onRetry?: (mediaRefs: string[]) => void;
}

/**
 * The line over a picture with a hole in it: why there is no picture (preparing with its percent, source
 * offline, failed with Try again). The viewer is never black without one of these.
 */
export function ViewerReasonOverlay({ doc, missing, readiness, proxyStatusFor, proxyVersion, onRetry }: ViewerReasonOverlayProps) {
  const t = useT('editor.viewer.viewerStatus');
  const touchH = useIsTouchSurface() ? 44 : undefined;
  const version = useSyncExternalStore(
    readiness ? readiness.subscribe : subscribeNothing,
    readiness ? readiness.getVersion : zeroVersion,
    readiness ? readiness.getVersion : zeroVersion,
  );
  const reason: ViewerReason | null = useMemo(
    () => viewerReason(doc, missing, proxyStatusFor, readiness),
    // version / proxyVersion are the signals that the store or a lookup changed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [doc, missing, proxyStatusFor, readiness, version, proxyVersion],
  );
  if (!reason) return null;
  const failed = reason.kind === 'failed';
  return (
    <Box style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16, pointerEvents: 'none', zIndex: 3 }}>
      <Stack gap={8} align="center" maw={420} data-testid="preview-reason" data-kind={reason.kind} style={{ pointerEvents: 'auto' }}>
        <Text size="sm" c={failed ? 'red.3' : 'gray.3'} ta="center" data-testid="preview-notice" role="status">
          {reason.text}
        </Text>
        {reason.kind === 'preparing' && reason.percent !== null && (
          <div style={{ width: 220 }} data-testid="preview-progress">
            <JobProgress job={localJob({ state: 'running', type: 'mediaPrep', progress: reason.percent / 100 })} />
          </div>
        )}
        {reason.retryRef && (
          <Button size="compact-md" mih={touchH} variant="light" color={failed ? 'red' : 'gray'} onClick={() => onRetry?.([reason.retryRef as string])} data-testid="preview-retry">
            {t('tryAgain2')}
          </Button>
        )}
      </Stack>
    </Box>
  );
}

const subscribeNothing = () => () => {};
const zeroVersion = () => 0;

/** The "Simple preview" label: always visible while the viewer runs the single-clip proxy video. */
export function SimplePreviewLabel({ reason }: { reason: string | null }) {
  const t = useT('editor.viewer.viewerStatus');
  return (
    <Group gap={4} wrap="nowrap" data-testid="simple-preview-label" title={reason ? previewText(reason, t.msg) : undefined} style={{ fontSize: STATUS_TEXT, color: 'var(--mantine-color-orange-4)' }}>
      <IconInfoCircle size={14} />
      <span>{t('simplePreview')}</span>
    </Group>
  );
}

/** The dropped-frame warning: playback is dropping frames, so offer the simple preview. */
export function DroppedFramesChip({ onSwitch }: { onSwitch: () => void }) {
  const t = useT('editor.viewer.viewerStatus');
  const touchH = useIsTouchSurface() ? 44 : undefined;
  return (
    <Alert
      color="orange"
      variant="light"
      p={6}
      radius="sm"
      icon={<IconAlertTriangle size={14} />}
      data-testid="dropped-frames-chip"
      styles={{ message: { fontSize: STATUS_TEXT }, wrapper: { alignItems: 'center' } }}
    >
      <Group gap="xs" wrap="nowrap" justify="space-between">
        <span>{t('playbackIsDroppingFrames')}</span>
        <Button size="compact-sm" fz="xs" mih={touchH} variant="light" color="orange" onClick={onSwitch} data-testid="switch-simple-preview">
          {t('switchToSimplePreview')}
        </Button>
      </Group>
    </Alert>
  );
}

/** "Use live preview" / "Try live preview again": the way back from the Simple preview. */
export function LivePreviewAction({ mode, compact = false, onClick }: { mode: 'use' | 'retry'; compact?: boolean; onClick: () => void }) {
  const t = useT('editor.viewer.viewerStatus');
  const touchH = useIsTouchSurface() ? 44 : undefined;
  return (
    <Button
      size="compact-md"
      mih={touchH}
      variant={mode === 'use' ? 'light' : 'filled'}
      color={mode === 'use' ? 'gray' : 'orange'}
      onClick={onClick}
      aria-label={compact ? t('useLivePreview') : undefined}
      data-testid={mode === 'use' ? 'preview-use-live' : 'preview-live-retry'}
    >
      {compact ? t('useLivePreviewShort') : mode === 'use' ? t('useLivePreview') : t('tryLivePreviewAgain')}
    </Button>
  );
}

/** Over the frame while the graphics context is gone: what happened and, if it cannot come back by itself, the way to retry. */
export function ContextLossOverlay({ loss, onRetry }: { loss: ContextLoss; onRetry: () => void }) {
  const t = useT('editor.viewer.viewerStatus');
  return (
    <Box style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16, pointerEvents: 'none', background: 'rgba(15,17,21,0.55)' }}>
      <Stack gap={8} align="center" maw={420} data-testid="preview-context-lost" data-state={loss.state} style={{ pointerEvents: 'auto' }}>
        <Text size="sm" c={loss.state === 'failed' ? 'red.3' : 'gray.2'} ta="center" role="status">
          {t.msg(previewSessionReasonMsg(loss.message))}
        </Text>
        {loss.state === 'failed' && <LivePreviewAction mode="retry" onClick={onRetry} />}
      </Stack>
    </Box>
  );
}
