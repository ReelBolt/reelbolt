'use client';

export { findProxyRow } from './use-source-playback';

/**
 * The source viewer — one clip, played, marked, and (optionally) cut into a subclip descriptor.
 *
 * ## Playback is a plain `<video>`; WebCodecs is deliberately deferred to P3
 *
 * **This is a documented P3 hand-off, not an oversight.** P2's plan permits a plain `<video>`
 * element over the proxy so long as the choice is written down (P2-F1, and acceptance 1 of this
 * ticket). The reason is that a `<video>` decodes exactly one track and gives no frame-accurate
 * access to the decoded picture, which is fine for *viewing* a source and useless for a compositor.
 * P3 owns the WebCodecs/demuxer path — P0-F1's spike measures the demuxer options separately — and
 * when it lands, the element below is what it replaces. **Nothing here builds a WebCodecs path**,
 * and nothing here should be read as one.
 *
 * ## Everything this component knows comes in as a prop
 *
 * It reads no route, no global state and no store: the clip, the project id and an optional
 * callback are its whole input. That is what lets P3 mount it beside the timeline viewer, and it is
 * asserted by `__tests__/SourceViewer.test.tsx`.
 *
 * ## Marks are view state; marking issues no request
 *
 * The in/out marks live in `useState` here and are computed by the pure reducer in
 * `lib/editing/media/in-out.ts`. Changing one produces **no network request and no timeline
 * revision** — the fetch below is keyed by the clip alone and never re-runs on a mark, which
 * `__tests__/SourceViewer.test.tsx` asserts by call count around five mark changes.
 *
 * ## Creating a subclip creates nothing but a value
 *
 * `createSubclip` returns a descriptor to `onCreateSubclip` and to this component's own preview.
 * **It is not persisted** — P4's ops and the P1 engine are what will place a subclip on a timeline,
 * and writing one from a media screen would be the hidden write this initiative forbids. The
 * preview says so in words.
 *
 * ## No `<video>` without a URL that exists
 *
 * Both source kinds have an original route (`file:` through the project-files media route,
 * `step_result:` through the outputs download route), and a proxy row may simply not exist yet
 * (the normal case until the derivatives consumer catches up). When neither position has a usable
 * URL (a token of an unknown kind), this component renders a stated reason instead of a player, and it never claims a proxy
 * "failed" — the API cannot distinguish that from "not yet".
 */

import React, { useCallback, useMemo, useState } from 'react';
import { Alert, Box, Button, Group, Image, Stack, Text } from '@mantine/core';

import { CheckedVideo } from '@/components/outputs/CheckedVideo';
import { useIsTouchSurface } from '@/lib/editing/use-device-class';
import { isMarkable, markSpanFrames, type Marks } from '@/lib/editing/media/in-out';
import { createSubclip, type SubclipDescriptor } from '@/lib/editing/media/subclip';
import { framesToTimecode } from '@/lib/editing/time';
import type { MediaPoolItem } from '@/lib/editing/media/types';
import { EditorPanel } from '../ui/EditorPanel';
import { useT } from '@/i18n';
import { useSourcePlayback } from './use-source-playback';

export interface SourceViewerProps {
  /** The owning project — needed only to build API paths. Never read from a route here. */
  projectId: string;
  /** The clip to view, or `null` when the selection is empty. */
  item: MediaPoolItem | null;
  /**
   * Called with a subclip descriptor when the user creates one.
   *
   * Optional, and nothing in this phase persists what it receives: P4's `insert` op and the P1
   * engine are what place a subclip on a timeline.
   */
  onCreateSubclip?: (descriptor: SubclipDescriptor) => void;
  /**
   * Bump to re-read this clip's derivative rows.
   *
   * A derivative is produced asynchronously — `POST …/derivatives/request` only publishes the work —
   * so a proxy that did not exist a moment ago can legitimately exist now. Without a way to ask
   * again, the OFF/ON toggle would keep reporting "not ready" forever after a request. Changing this
   * value is the ONLY thing that re-runs the fetch: marks, the toggle and every other piece of view
   * state still issue nothing, which is what `__tests__/SourceViewer.test.tsx` asserts by call count.
   */
  derivativesReloadKey?: number;
  /** Told the marks after every change, so the caller can place the marked range on a timeline. */
  onMarksChange?: (marks: Marks) => void;
  /**
   * What "Create subclip" does here, shown under the descriptor. Given by a caller that places the
   * range (the editor's Media workspace); without it the descriptor is shown as not saved.
   */
  subclipHint?: string;
}

export function SourceViewer({
  projectId,
  item,
  onCreateSubclip,
  derivativesReloadKey = 0,
  onMarksChange,
  subclipHint,
}: SourceViewerProps) {
  const t = useT('editor-media.viewer');
  const mediaRef = item ? item.mediaRef : null;
  // Finger-sized mark and subclip buttons on touch (44 px); the compact ones stay for a mouse.
  const touch = useIsTouchSurface();
  const buttonStyle = touch ? { minHeight: 44, minWidth: 44 } : undefined;
  const {
    rows, rowsError, marks, playheadFrame, domain, attachVideo, videoUrl, effectiveSource, originalUrl,
    refreshPlayhead, handleLoadedMetadata, applyMark, handleClearMarks,
  } = useSourcePlayback(projectId, item, derivativesReloadKey, onMarksChange);
  const [lastDescriptor, setLastDescriptor] = useState<SubclipDescriptor | null>(null);
  const descriptor = useMemo(() => {
    if (!item || marks.inFrame === null || marks.outFrame === null) return null;
    return createSubclip(
      { mediaRef: item.mediaRef, probe: item.probe },
      marks.inFrame,
      marks.outFrame,
      item.fileName,
    );
  }, [item, marks]);

  const handleCreateSubclip = useCallback(() => {
    if (!descriptor) return;
    setLastDescriptor(descriptor);
    onCreateSubclip?.(descriptor);
  }, [descriptor, onCreateSubclip]);

  // ───────────────────────────────────────────────────────────────────────────
  // Render
  // ───────────────────────────────────────────────────────────────────────────
  if (!item) {
    return (
      <EditorPanel title={t('sourceViewer')} testId="source-viewer">
        <Text size="sm" c="dimmed" data-testid="source-viewer-empty">
          {t('empty')}
        </Text>
      </EditorPanel>
    );
  }

  const timecodeOf = (frame: number | null): string => {
    if (frame === null) return t('notSet');
    return domain.kind === 'timebase'
      ? t('timecode', { timecode: framesToTimecode(frame, domain.fps), frame })
      : t('decodedFrame', { frame });
  };

  // Sound and stills never get a proxy and never go through a <video> element (audit d2 N3): a
  // sound plays in an <audio> element and a still is shown as a picture, both from the original.
  if (item.kind === 'audio' || item.kind === 'image') {
    const original = typeof originalUrl === 'string' && originalUrl.trim().length > 0 ? originalUrl : null;
    return (
      <EditorPanel title={item.fileName} testId="source-viewer">
      <Stack gap="xs" data-media-kind={item.kind}>
        {original === null ? (
          <Alert color="gray" variant="light" p="xs" data-testid="source-viewer-unplayable">
            <Text size="xs">{t('cannotOpen')}</Text>
          </Alert>
        ) : item.kind === 'audio' ? (
          <audio src={original} controls preload="metadata" data-testid="source-viewer-audio" style={{ width: '100%' }} />
        ) : (
          <Image src={original} alt={item.fileName} fit="contain" mah={320} data-testid="source-viewer-image" />
        )}
        <Text size="xs" c="dimmed" data-testid="source-viewer-no-proxy">
          {item.kind === 'audio' ? t('noProxyAudio') : t('noProxyImage')}
        </Text>
      </Stack>
      </EditorPanel>
    );
  }

  const markable = isMarkable(domain);
  const span = markSpanFrames(marks);
  // A URL is usable only if it is a non-empty string: this is the single gate in front of `<video>`.
  const playableUrl = typeof videoUrl === 'string' && videoUrl.trim().length > 0 ? videoUrl : null;

  return (
    <EditorPanel title={item.fileName} testId="source-viewer">
    <Stack gap="xs">
      {playableUrl ? (
        /*
         * A plain HTML video element, on purpose: P3 replaces exactly this with the WebCodecs
         * path (see this file's header). It is only ever rendered with a URL that exists.
         */
        <Box data-testid="source-viewer-player" data-playing-source={effectiveSource ?? undefined}>
          <CheckedVideo
            ref={attachVideo}
            src={playableUrl}
            controls
            preload="metadata"
            data-testid="source-viewer-video"
            onTimeUpdate={refreshPlayhead}
            onSeeked={refreshPlayhead}
            onLoadedMetadata={handleLoadedMetadata}
            style={{ width: '100%', maxHeight: 320, background: '#000' }}
          />
        </Box>
      ) : (
        <Alert color="gray" variant="light" p="xs" data-testid="source-viewer-unplayable">
          <Text size="xs" fw={600}>
            {rows === null && !rowsError
              ? t('gettingReady')
              : t('noPlayableUrl')}
          </Text>
          <Text size="xs" c="dimmed">
            {rows === null && !rowsError
              ? t('takesMoment')
              : t('cannotPlay')}
          </Text>
        </Alert>
      )}

      {effectiveSource === 'proxy' ? (
        <Text size="xs" c="dimmed" data-testid="source-viewer-preview-label">
          {t('simplePreview')}
        </Text>
      ) : null}

      <Stack gap={4} data-testid="mark-controls">
        <Group gap="xs">
          <Button
            size={touch ? 'md' : 'compact-xs'} fz="xs" style={buttonStyle}
            variant="light"
            onClick={() => applyMark('in')}
            disabled={!markable || playheadFrame === null}
            data-testid="mark-in"
          >
            {t('setIn')}
          </Button>
          <Button
            size={touch ? 'md' : 'compact-xs'} fz="xs" style={buttonStyle}
            variant="light"
            onClick={() => applyMark('out')}
            disabled={!markable || playheadFrame === null}
            data-testid="mark-out"
          >
            {t('setOut')}
          </Button>
          <Button
            size={touch ? 'md' : 'compact-xs'} fz="xs" style={buttonStyle}
            variant="default"
            onClick={handleClearMarks}
            disabled={marks.inFrame === null && marks.outFrame === null}
            data-testid="mark-clear"
          >
            {t('clearMarks')}
          </Button>
        </Group>

        {!markable ? (
          <Text size="xs" c="yellow.8" data-testid="mark-disabled-reason">
            {domain.kind === 'unavailable' ? domain.reason : ''}
          </Text>
        ) : playheadFrame === null ? (
          <Text size="xs" c="dimmed" data-testid="mark-playhead-reason">
            {domain.kind === 'decoded'
              ? t('noFrameRate')
              : t('movePlayhead')}
          </Text>
        ) : null}

        <Group gap="md">
          <Text size="xs" data-testid="mark-in-value">
            {t('inValue', { value: timecodeOf(marks.inFrame) })}
          </Text>
          <Text size="xs" data-testid="mark-out-value">
            {t('outValue', { value: timecodeOf(marks.outFrame) })}
          </Text>
          <Text size="xs" c="dimmed" data-testid="mark-span-value">
            {span === null ? t('noRange') : t('frames', { count: span })}
          </Text>
        </Group>

        <Group gap="xs" align="center">
          <Button
            size={touch ? 'md' : 'compact-xs'} fz="xs" style={buttonStyle}
            onClick={handleCreateSubclip}
            disabled={descriptor === null}
            data-testid="create-subclip"
          >
            {t('createSubclip')}
          </Button>
          <Text size="xs" c="dimmed">
            {t('marksNote')}
          </Text>
        </Group>

        {lastDescriptor ? (
          <Alert color="gray" variant="light" p="xs" data-testid="subclip-descriptor">
            <Text size="xs" fw={600} data-testid="subclip-name">
              {lastDescriptor.name}
            </Text>
            <Text size="xs" data-testid="subclip-range">
              {t('range', { from: lastDescriptor.sourceInFrames, to: lastDescriptor.sourceOutFrames, length: lastDescriptor.durationFrames })}
            </Text>
            <Text size="xs" c="dimmed" data-testid="subclip-hint">
              {subclipHint ?? t('notSaved')}
            </Text>
          </Alert>
        ) : null}
      </Stack>
    </Stack>
    </EditorPanel>
  );
}
