'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Box, Button, Container, Group, Loader, Select, Stack, Text, Title } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconCopy, IconShare } from '@tabler/icons-react';
import Link from 'next/link';
import useSWR from 'swr';
import { editTimelineKey, getEditTimeline, type EditTimelineResponse } from '@/lib/editing/sync/api';
import { useEditTimelineRenders } from '@/lib/hooks/use-edit-timeline-renders';
import { useReviewComments } from '@/lib/hooks/use-editor-p9';
import { getOutputVideoUrl } from '@/lib/api/outputs';
import { framesToTimecode } from '@/lib/editing/time';
import { timelineEndFrame } from '@/lib/editing/transport';
import { pickReviewRender } from '@/lib/editing/review/pick-render';
import { REVIEW_SIDE_BY_SIDE_QUERY, isRenderStale, markerPercent, pickerRenders, pinnedFrame, renderLabel } from '@/lib/editing/review/review-state';
import { ReviewPanel } from './ReviewPanel';
import { GuestDecisionBanners, ShareForReview } from './ShareForReview';
import { useReviewLinks } from '@/lib/hooks/use-review-links';
import { EarlyPreviewLabel } from '../shell/EarlyPreviewLabel';
import { useKeyboardInset } from '../phone/use-keyboard-inset';
import { useT } from '@/i18n';
import { CheckedVideo } from '@/components/outputs/CheckedVideo';

export interface MobileReviewProps {
  projectId: string;
  timelineId: string;
}

/**
 * R11.5 / VF16: the ONE review page, for a phone and for a desktop. It plays the newest finished render
 * of the whole timeline WITH its picture in the browser's own player, shows the comment markers on a bar
 * under it, and keeps the review thread beside the video (width >= md) or under it (a phone). A comment
 * pins to the frame the video is paused on. A render older than the latest edit says so and links to
 * the editor. Nothing here edits the document.
 */
export function MobileReview({ projectId, timelineId }: MobileReviewProps) {
  const t = useT('editor.review.mobileReview');
  const { data: timeline, error } = useSWR<EditTimelineResponse>(editTimelineKey(projectId, timelineId), () => getEditTimeline(projectId, timelineId));
  const { data: renders } = useEditTimelineRenders(projectId, timelineId);
  const { data: comments } = useReviewComments(projectId, timelineId);
  const { data: reviewLinks } = useReviewLinks(projectId, timelineId);
  const sideBySide = useMediaQuery(REVIEW_SIDE_BY_SIDE_QUERY, false, { getInitialValueInEffect: false }) ?? false;
  const videoRef = useRef<HTMLVideoElement | null>(null);
  // UI2-01: the <video> mounts only once the timeline AND a render are known, which can be after the first
  // effect ran; a ref callback puts the element in state so the listeners attach whenever it appears.
  const [video, setVideo] = useState<HTMLVideoElement | null>(null);
  const attachVideo = useCallback((node: HTMLVideoElement | null) => {
    videoRef.current = node;
    setVideo(node);
  }, []);
  const [frame, setFrame] = useState(0);
  const [duration, setDuration] = useState(0);
  const [picked, setPicked] = useState<string | null>(null);
  const [linkNote, setLinkNote] = useState<string | null>(null);
  const keyboardInset = useKeyboardInset(true);
  const rate = timeline?.document.settings.rate ?? { num: 30, den: 1 };
  const fps = rate.den > 0 ? rate.num / rate.den : 30;
  const endFrame = useMemo(() => (timeline ? timelineEndFrame(timeline.document) : 0), [timeline]);
  const revision = timeline?.revision ?? -1;
  const choice = pickReviewRender(renders, endFrame, revision);
  const options = useMemo(() => pickerRenders(renders), [renders]);
  const chosen = picked ? options.find((r) => r.executionId === picked) ?? null : null;
  const render = chosen ?? choice?.render ?? null;
  const partial = chosen ? false : (choice?.partial ?? false);
  // The share button only renders once the timeline loaded (client side), so reading navigator here cannot mismatch hydration.
  const canShare = typeof navigator !== 'undefined' && typeof navigator.share === 'function';
  const stale = render ? isRenderStale(render.revision, revision) : false;

  useEffect(() => {
    if (!video) return;
    const onTime = () => setFrame(pinnedFrame(video.currentTime, fps));
    const onMeta = () => {
      setDuration(Number.isFinite(video.duration) ? video.duration : 0);
      onTime();
    };
    onMeta();
    video.addEventListener('timeupdate', onTime);
    video.addEventListener('seeked', onTime);
    video.addEventListener('pause', onTime);
    video.addEventListener('loadedmetadata', onMeta);
    video.addEventListener('durationchange', onMeta);
    return () => {
      video.removeEventListener('timeupdate', onTime);
      video.removeEventListener('seeked', onTime);
      video.removeEventListener('pause', onTime);
      video.removeEventListener('loadedmetadata', onMeta);
      video.removeEventListener('durationchange', onMeta);
    };
  }, [video, fps]);

  // Keyboard-safe comment box (iOS): when the soft keyboard covers the bottom, keep the focused field in view.
  useEffect(() => {
    const active = document.activeElement;
    if (keyboardInset > 0 && active instanceof HTMLTextAreaElement) active.scrollIntoView({ block: 'center' });
  }, [keyboardInset]);

  const seekTo = useCallback(
    (f: number) => {
      setFrame(f);
      if (videoRef.current) videoRef.current.currentTime = f / fps;
    },
    [fps],
  );

  const reviewUrl = () => (typeof window !== 'undefined' ? window.location.href : '');
  const copyLink = async () => {
    try {
      await navigator.clipboard.writeText(reviewUrl());
      setLinkNote(t('linkCopied'));
    } catch {
      setLinkNote(t('couldNotCopyThe'));
    }
  };
  const shareLink = async () => {
    try {
      await navigator.share({ title: timeline?.name ?? 'Review', url: reviewUrl() });
    } catch (e) {
      // Closing the share sheet is not a failure.
      if (!(e instanceof DOMException && e.name === 'AbortError')) setLinkNote(t('couldNotShareThe'));
    }
  };

  if (error) {
    return (
      <Container size="xs" py="md">
        <Alert color="red" title={t('thisTimelineCouldNot')}>{t('youMayNotHave')}</Alert>
      </Container>
    );
  }
  if (!timeline) {
    return (
      <Container size="xs" py="md">
        <Group justify="center"><Loader size="sm" /></Group>
      </Container>
    );
  }

  const editorHref = `/editor/${projectId}/timeline/${timelineId}`;
  const totalFrames = endFrame > 0 ? endFrame : Math.round(duration * fps);
  const markers = (comments ?? []).filter((c) => c.parentId === null && !c.resolved);

  return (
    <Container size={sideBySide ? 'xl' : 'sm'} px="xs" py="sm" data-testid="mobile-review" data-layout={sideBySide ? 'side-by-side' : 'single-column'} style={{ paddingBottom: keyboardInset > 0 ? keyboardInset + 16 : undefined }}>
      <Stack gap="sm">
        <Group justify="space-between" wrap="wrap" gap="xs">
          <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
            <Title order={4} style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>{timeline.name}</Title>
            <EarlyPreviewLabel />
          </Group>
          <Group gap={8} wrap="wrap">
            <Button size="md" mih={44} variant="default" leftSection={<IconCopy size={16} />} onClick={() => void copyLink()} data-testid="review-copy-link">
              {t('copyReviewLink')}
            </Button>
            {canShare && (
              <Button size="md" mih={44} variant="default" leftSection={<IconShare size={16} />} onClick={() => void shareLink()} data-testid="review-share-link">
                {t('share')}
              </Button>
            )}
            <ShareForReview projectId={projectId} timelineId={timelineId} defaultExecutionId={render?.executionId ?? null} />
            <Button component={Link} href={editorHref} size="md" mih={44} variant="subtle" data-testid="review-open-editor">
              {t('openInTheEditor')}
            </Button>
          </Group>
        </Group>
        <Text size="xs" c="dimmed" data-testid="review-access-line">
          {t('anyoneInThisWorkspace')}{linkNote ? ` ${linkNote}.` : ''}
        </Text>
        <GuestDecisionBanners links={reviewLinks} />
        {stale && render && (
          <Alert color="yellow" variant="light" title={t('newerEditsExist')} data-testid="review-stale-banner">
            <Group justify="space-between" gap="xs">
              <Text size="sm">{t('thisFilmShowsAn')}</Text>
              <Button component={Link} href={editorHref} size="md" mih={44} variant="default" data-testid="review-stale-open-editor">
                {t('openInTheEditor2')}
              </Button>
            </Group>
          </Alert>
        )}
        <Box style={{ display: 'grid', gridTemplateColumns: sideBySide ? 'minmax(0, 1fr) minmax(320px, 400px)' : 'minmax(0, 1fr)', gap: 'var(--mantine-spacing-md)', alignItems: 'start' }}>
          <Stack gap="xs" style={{ minWidth: 0 }}>
            {render ? (
              <>
                <CheckedVideo
                  key={render.stepResultId as string}
                  ref={attachVideo}
                  src={getOutputVideoUrl(projectId, render.stepResultId as string)}
                  controls
                  playsInline
                  preload="metadata"
                  style={{ width: '100%', borderRadius: 6, background: '#000' }}
                  data-testid="mobile-review-video"
                />
                <Box style={{ position: 'relative', height: 44 }} data-testid="review-marker-bar">
                  <Box style={{ position: 'absolute', left: 0, right: 0, top: 20, height: 4, borderRadius: 2, background: 'var(--mantine-color-default-border)' }} />
                  <Box style={{ position: 'absolute', left: 0, top: 20, height: 4, borderRadius: 2, background: 'var(--mantine-primary-color-filled)', width: `${markerPercent(frame, totalFrames)}%` }} />
                  {markers.map((c) => (
                    <button
                      key={c.id}
                      type="button"
                      aria-label={t('commentAt', { v1: framesToTimecode(c.frame, rate) })}
                      onClick={() => seekTo(c.frame)}
                      style={{ position: 'absolute', left: `calc(${markerPercent(c.frame, totalFrames)}% - 22px)`, top: 0, width: 44, height: 44, border: 0, background: 'transparent', cursor: 'pointer', padding: 0 }}
                      data-testid={`review-marker-${c.id}`}
                    >
                      <span style={{ display: 'block', margin: '0 auto', width: 12, height: 12, borderRadius: 6, background: 'var(--mantine-color-orange-6)', border: '2px solid var(--mantine-color-body)' }} />
                    </button>
                  ))}
                </Box>
                <Group justify="space-between" gap="xs">
                  <Text size="sm" ff="monospace" data-testid="mobile-review-timecode">{framesToTimecode(frame, rate)}</Text>
                  <Text size="xs" c="dimmed">{render.revision !== timeline.revision ? t('thisFilmShowsAn2') : t('version', { revision: render.revision })}</Text>
                </Group>
                {options.length > 1 && (
                  <Select
                    size="md"
                    label={t('versionToWatch')}
                    allowDeselect={false}
                    data={options.map((r) => ({ value: r.executionId, label: renderLabel(r, revision) }))}
                    value={render.executionId}
                    onChange={(v) => setPicked(v)}
                    data-testid="review-render-picker"
                  />
                )}
                {partial && (
                  <Text size="xs" c="dimmed" data-testid="mobile-review-partial">
                    {t('thisFilmCoversOnly')}
                  </Text>
                )}
              </>
            ) : (
              <Alert color="gray" variant="light" data-testid="mobile-review-no-render">
                {t('thereIsNoFinished')}
              </Alert>
            )}
          </Stack>
          <ReviewPanel
            compact
            projectId={projectId}
            timelineId={timelineId}
            rate={rate}
            playheadFrame={frame}
            readPlayheadFrame={() => (videoRef.current ? pinnedFrame(videoRef.current.currentTime, fps) : frame)}
            onSeek={seekTo}
          />
        </Box>
      </Stack>
    </Container>
  );
}
