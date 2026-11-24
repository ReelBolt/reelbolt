'use client';

import { useRef, useState } from 'react';
import { useDownloadGuard, useOutputCheck } from '@/lib/outputs/output-check';
import Link from 'next/link';
import { Badge, Button, Group, Loader, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconDownload, IconExternalLink, IconShare2 } from '@tabler/icons-react';
import { getOutputPreviewUrl, getOutputVideoUrl } from '@/lib/api/outputs';
import { outputMediaKind } from '@/lib/outputs/media-kind';
import { useAuth } from '@/lib/hooks/use-auth';
import { emitFunnelEvent, userKeyFromEmail } from '@/lib/telemetry';
import { timeAgo } from '@/lib/files/model';
import {
  formatFrameSize, formatVideoLength, renderDownloadName, renderStage, renderTitle, runName,
  type RunWarning,
} from '@/lib/utils/run-summary';
import { RunWarningsAlert } from './RunWarningsAlert';
import { useT, type T } from '@/i18n';
import classes from './outputs.module.css';

export interface PlayableOutput {
  stepResultId: string;
  projectId: string;
  fileName: string;
  producedAt: string;
  /** Link to the run that produced it, when known. */
  runHref?: string;
  projectName?: string;
  // Optional run context (see OutputVideo); absent on surfaces that do not have it.
  workflowName?: string | null;
  runNumber?: number | null;
  runStartedAt?: string | null;
  executionStatus?: string | null;
  isFinal?: boolean;
  durationSec?: number | null;
  width?: number | null;
  height?: number | null;
  warnings?: RunWarning[] | null;
}

// Copy lives in the `outputs` catalogue. `share.scopeNote` says who a render link opens for (the render
// endpoints are owner-scoped, there is no public link). Why a render failed to load is read by
// `useOutputCheck` (lib/outputs/output-check.ts): one probe, then a quiet re-check only while it is being prepared.

/** Copies text with the async Clipboard API, falling back to the legacy selection copy. */
async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Permission denied or insecure context: try the legacy path below.
  }
  try {
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    const ok = typeof document.execCommand === 'function' && document.execCommand('copy');
    area.remove();
    return !!ok;
  } catch {
    return false;
  }
}

/**
 * Share a render link: the native share sheet when the browser has one, otherwise (or when it
 * fails for a reason other than the user closing it) the clipboard — and always visible feedback,
 * since a share button that silently does nothing reads as broken.
 */
export async function shareRenderLink(title: string, url: string, t: T): Promise<'shared' | 'copied' | 'cancelled' | 'failed'> {
  if (typeof navigator !== 'undefined' && typeof navigator.share === 'function') {
    try {
      await navigator.share({ title, url });
      return 'shared';
    } catch (e) {
      // Closing the share sheet rejects with AbortError: that is the user's choice, not a failure.
      if (e instanceof Error && e.name === 'AbortError') return 'cancelled';
    }
  }
  if (await copyText(url)) {
    notifications.show({ title: t('share.copiedTitle'), message: t('share.scopeNote'), color: 'green' });
    return 'copied';
  }
  notifications.show({
    title: t('share.failedTitle'),
    message: t('share.copyYourself', { url }),
    color: 'red',
    autoClose: false,
  });
  return 'failed';
}

/**
 * Native-controls player for one render. The <video> is `playsInline` (iOS would otherwise force
 * fullscreen on play) and the native fullscreen button stays available. Actions sit under the
 * video in a 3-up grid of 44px targets on phones, i.e. within thumb reach.
 */
export function OutputPlayer({ output, autoPlay = false }: { output: PlayableOutput; autoPlay?: boolean }) {
  const t = useT('outputs');
  const { user } = useAuth();
  const src = getOutputVideoUrl(output.projectId, output.stepResultId);
  // Silent while the video is healthy; once it errors, the reason is probed before any copy is shown —
  // a video still being prepared is not a missing file, and it loads by itself when ready.
  const check = useOutputCheck(src);
  const guardDownload = useDownloadGuard();
  // What the browser measured, for renders whose run did not record length/size.
  const [measured, setMeasured] = useState<{ duration: number; width: number; height: number } | null>(null);
  const started = useRef(false);
  const kind = outputMediaKind(output.fileName);

  const stage = kind === 'image' ? null : renderStage(output);
  const title = renderTitle(output);
  const length = formatVideoLength(output.durationSec ?? measured?.duration);
  const size = formatFrameSize(output.width ?? measured?.width, output.height ?? measured?.height);
  const run = output.workflowName ? runName(output.runNumber, output.runStartedAt) : null;
  const facts = [run, length, size].filter(Boolean).join(' · ');
  // A portrait (9:16) render must not play in a 960px-wide black box: size the stage to the video's own ratio.
  const frameW = output.width ?? measured?.width;
  const frameH = output.height ?? measured?.height;
  const portrait = !!frameW && !!frameH && frameH > frameW;
  const stageStyle = portrait
    ? { aspectRatio: `${frameW} / ${frameH}`, width: 'min(100%, 360px)', maxHeight: '70dvh', marginInline: 'auto' }
    : undefined;

  const share = async () => {
    await shareRenderLink(title, new URL(src, window.location.origin).toString(), t);
  };

  return (
    <Stack gap="sm">
      {check.message ? (
        <Stack className={classes.stage} align="center" justify="center" gap={4} p="md" role="status">
          {check.status === 'checking' || check.status === 'pending' ? (
            <Group gap="xs" wrap="nowrap">
              <Loader size="sm" color="gray" />
              <Text c="gray.4" size="sm" ta="center">{check.message}</Text>
            </Group>
          ) : (
            <Text c="gray.4" size="sm" ta="center">{check.message}</Text>
          )}
        </Stack>
      ) : kind === 'image' ? (
        // A picture: shown as one, and a click opens it full size in its own tab.
        <a href={src} target="_blank" rel="noopener noreferrer" style={{ display: 'contents' }}>
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            key={`${output.stepResultId}:${check.remountKey}`}
            className={classes.stage}
            style={{ ...stageStyle, objectFit: 'contain' }}
            src={getOutputPreviewUrl(output.projectId, output.stepResultId, 1920)}
            alt={title}
            data-testid="output-image"
            onError={check.onError}
          />
        </a>
      ) : kind === 'audio' ? (
        <audio
          key={`${output.stepResultId}:${check.remountKey}`}
          src={src}
          controls
          preload="metadata"
          style={{ width: '100%' }}
          onError={check.onError}
          aria-label={t('player.ariaLabel', { title })}
        />
      ) : (
        <video
          key={`${output.stepResultId}:${check.remountKey}`}
          className={classes.stage}
          style={stageStyle}
          src={src}
          controls
          playsInline
          autoPlay={autoPlay}
          preload="metadata"
          onLoadedMetadata={(e) => {
            const v = e.currentTarget;
            if (Number.isFinite(v.duration) && v.duration > 0) {
              setMeasured({ duration: v.duration, width: v.videoWidth, height: v.videoHeight });
            }
            // Renders fade in from black: park on a later frame so the paused player is not a black box.
            if (!autoPlay && Number.isFinite(v.duration) && v.duration > 0) v.currentTime = Math.min(1.5, v.duration * 0.25);
          }}
          onPlay={(e) => {
            if (!started.current) {
              started.current = true;
              if (!autoPlay) e.currentTarget.currentTime = 0;
            }
          }}
          onError={check.onError}
          aria-label={t('player.ariaLabel', { title })}
        />
      )}
      <Group justify="space-between" gap="xs" wrap="nowrap" align="flex-start">
        <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
          {stage && (
            <Badge size="sm" variant={stage === 'final' ? 'filled' : 'light'} color={stage === 'final' ? 'green' : 'gray'} style={{ flexShrink: 0 }}>
              {stage === 'final' ? t('stage.final') : stage === 'draft' ? t('stage.draft') : t('stage.inProgress')}
            </Badge>
          )}
          <Text fw={600} size="sm" truncate>{title}</Text>
        </Group>
        <Text size="xs" c="dimmed" style={{ whiteSpace: 'nowrap' }}>{timeAgo(output.producedAt)}</Text>
      </Group>
      {facts && <Text size="xs" c="dimmed">{facts}</Text>}
      {output.warnings && output.warnings.length > 0 && <RunWarningsAlert warnings={output.warnings} compact />}
      <div className={classes.actions}>
        <Button
          component="a"
          href={src}
          download={renderDownloadName(output, stage)}
          variant="default"
          disabled={check.status === 'pending'}
          leftSection={<IconDownload size={16} />}
          // alpha-launch T-018: the export funnel step. A bare `<a download>` gives the page no
          // success signal, so this is the closest real transition available and the event is
          // labelled "initiated" in docs/observability.md — it must not be read as a completed
          // export. Once per user and consent-gated in the library; nothing here is blocked on it.
          onClick={(e) => {
            emitFunnelEvent('alpha.first_export_initiated', userKeyFromEmail(user?.email));
            void guardDownload(src, renderDownloadName(output, stage))(e);
          }}
        >
          {t('player.download')}
        </Button>
        <Button variant="default" onClick={share} leftSection={<IconShare2 size={16} />}>
          {t('player.share')}
        </Button>
        {output.runHref ? (
          <Button
            component={Link} href={output.runHref} variant="default" leftSection={<IconExternalLink size={16} />}
          >
            {t('player.openRun')}
          </Button>
        ) : null}
      </div>
      <Text size="xs" c="dimmed">{t('share.scopeNote')}</Text>
    </Stack>
  );
}
