'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Box, Button, Checkbox, Container, Group, Loader, Stack, Text, TextInput, Textarea, Title } from '@mantine/core';
import { BrandMark } from '@/components/shell/BrandMark';
import { framesToTimecode } from '@/lib/editing/time';
import { useFormat, useT } from '@/i18n';
import {
  GUEST_NAME_KEY,
  GuestLinkError,
  changesNeedsNote,
  fetchGuestReview,
  guestMediaUrl,
  guestThreads,
  postGuestComment,
  postGuestDecision,
  type GuestComment,
  type GuestFailureKind,
  type GuestDecisionKind,
  type GuestReview,
} from '@/lib/editing/review/guest-links';

/** Product name: a proper noun, identical in every language. */
const BRAND_NAME = 'ReelBolt';

/**
 * Comment frames are read and written at the film's own frame rate (`rate` in the response), 30 fps when
 * it is missing.
 */
const DEFAULT_RATE = { num: 30, den: 1 } as const;

export interface GuestReviewPageProps {
  token: string;
}

type Load = { state: 'loading' } | { state: 'gone'; kind: GuestFailureKind } | { state: 'ok'; review: GuestReview };

function readName(): string {
  try {
    return window.localStorage.getItem(GUEST_NAME_KEY) ?? '';
  } catch {
    return '';
  }
}

function saveName(name: string): void {
  try {
    window.localStorage.setItem(GUEST_NAME_KEY, name);
  } catch {
    // Private mode: the name is simply not remembered.
  }
}

const failureKind = (e: unknown): GuestFailureKind => (e instanceof GuestLinkError ? e.kind : 'server');

/**
 * The public page behind a guest review link. No account, no cookies: every request is made with
 * `credentials: 'omit'` through guest-links.ts, nothing is sent to analytics, and EVERY guest-supplied
 * string (names, notes, decisions) is rendered as plain React text, never as markup.
 */
export function GuestReviewPage({ token }: GuestReviewPageProps) {
  const t = useT('review.guest');
  const f = useFormat();
  const [load, setLoad] = useState<Load>({ state: 'loading' });
  const [name, setName] = useState(readName);
  const [video, setVideo] = useState<HTMLVideoElement | null>(null);
  const [time, setTime] = useState(0);
  const [mediaDuration, setMediaDuration] = useState(0);
  const [body, setBody] = useState('');
  const [cover, setCover] = useState(false);
  const [replyTo, setReplyTo] = useState<string | null>(null);
  const [reply, setReply] = useState('');
  const [note, setNote] = useState('');
  const [askingChanges, setAskingChanges] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [postedCount, setPostedCount] = useState(0);
  const videoRef = useRef<HTMLVideoElement | null>(null);

  const failureSentence = (kind: GuestFailureKind): string =>
    kind === 'network' ? t('failNetwork') : kind === 'server' ? t('failServer') : kind === 'rate' ? t('failRate') : kind === 'invalid' ? t('failInvalid') : t('failGone');

  const reload = useCallback(async () => {
    try {
      setLoad({ state: 'ok', review: await fetchGuestReview(token) });
    } catch (e) {
      setLoad({ state: 'gone', kind: failureKind(e) });
    }
  }, [token]);

  useEffect(() => {
    let live = true;
    fetchGuestReview(token).then(
      (review) => live && setLoad({ state: 'ok', review }),
      (e: unknown) => live && setLoad({ state: 'gone', kind: failureKind(e) }),
    );
    return () => {
      live = false;
    };
  }, [token]);

  const attachVideo = useCallback((node: HTMLVideoElement | null) => {
    videoRef.current = node;
    setVideo(node);
  }, []);

  useEffect(() => {
    if (!video) return undefined;
    const onTime = () => setTime(video.currentTime);
    const onMeta = () => setMediaDuration(Number.isFinite(video.duration) ? video.duration : 0);
    video.addEventListener('timeupdate', onTime);
    video.addEventListener('seeked', onTime);
    video.addEventListener('pause', onTime);
    video.addEventListener('loadedmetadata', onMeta);
    video.addEventListener('durationchange', onMeta);
    onMeta();
    return () => {
      video.removeEventListener('timeupdate', onTime);
      video.removeEventListener('seeked', onTime);
      video.removeEventListener('pause', onTime);
      video.removeEventListener('loadedmetadata', onMeta);
      video.removeEventListener('durationchange', onMeta);
    };
  }, [video]);

  const review = load.state === 'ok' ? load.review : null;
  const rate = review?.rate && review.rate.num > 0 && review.rate.den > 0 ? review.rate : DEFAULT_RATE;
  const fps = rate.num / rate.den;
  const duration = review?.durationSec && review.durationSec > 0 ? review.durationSec : mediaDuration;
  const totalFrames = Math.max(1, Math.round(duration * fps));
  const frame = Math.max(0, Math.round(time * fps));
  const threads = useMemo(() => guestThreads(review?.comments ?? []), [review]);
  const trimmedName = name.trim();
  const guestHasCommented = postedCount > 0 || (review?.comments ?? []).some((c) => c.guest && trimmedName !== '' && c.authorName === trimmedName);

  const seekFrame = useCallback((f: number) => {
    const v = videoRef.current;
    const clamped = Math.max(0, f);
    setTime(clamped / fps);
    if (v) {
      try {
        v.currentTime = clamped / fps;
      } catch {
        // metadata not ready yet
      }
    }
  }, []);

  const currentFrame = (): number => {
    const v = videoRef.current;
    return Math.max(0, Math.round((v ? v.currentTime : time) * fps));
  };

  const act = async (fn: () => Promise<void>) => {
    setProblem(null);
    setBusy(true);
    try {
      await fn();
      await reload();
    } catch (e) {
      if (e instanceof GuestLinkError && e.kind === 'gone') setLoad({ state: 'gone', kind: 'gone' });
      else setProblem(failureSentence(failureKind(e)));
    } finally {
      setBusy(false);
    }
  };

  const onVideoKey = (e: React.KeyboardEvent<HTMLVideoElement>) => {
    const v = videoRef.current;
    if (!v) return;
    if (e.key === ' ') {
      e.preventDefault();
      if (v.paused) void v.play()?.catch(() => undefined);
      else v.pause();
    } else if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
      e.preventDefault();
      v.pause();
      seekFrame(Math.min(totalFrames, currentFrame() + (e.key === 'ArrowRight' ? 1 : -1)));
    }
  };

  const sendComment = () =>
    act(async () => {
      const at = currentFrame();
      const span = cover ? Math.max(1, Math.min(Math.round(fps), totalFrames - at)) : null;
      await postGuestComment(token, { name: trimmedName, body: body.trim(), frame: at, durationFrames: span });
      setBody('');
      setCover(false);
      setPostedCount((n) => n + 1);
    });

  const sendReply = (root: GuestComment) =>
    act(async () => {
      await postGuestComment(token, { name: trimmedName, body: reply.trim(), frame: root.frame, parentId: root.id });
      setReply('');
      setReplyTo(null);
      setPostedCount((n) => n + 1);
    });

  const sendDecision = (decision: GuestDecisionKind) =>
    act(async () => {
      await postGuestDecision(token, { decision, name: trimmedName, ...(decision === 'changesRequested' && note.trim() ? { note: note.trim() } : {}) });
      setNote('');
      setAskingChanges(false);
    });

  if (load.state === 'loading') {
    return (
      <Container size="xs" py="xl" data-testid="guest-loading">
        <Group justify="center">
          <Loader size="sm" aria-label={t('loading')} />
        </Group>
      </Container>
    );
  }
  if (load.state === 'gone') {
    return (
      <Container size="xs" py="xl">
        <Stack align="center" gap="sm">
          <Text ta="center" size="md" data-testid="guest-failure" role="alert">
            {failureSentence(load.kind)}
          </Text>
          {load.kind === 'network' || load.kind === 'server' || load.kind === 'rate' ? (
            <Button
              variant="default"
              onClick={() => {
                setLoad({ state: 'loading' });
                void reload();
              }}
            >
              {t('retry')}
            </Button>
          ) : null}
        </Stack>
      </Container>
    );
  }

  const r = load.review;
  const nameMissing = trimmedName === '';
  const needsNote = askingChanges && changesNeedsNote(note, guestHasCommented);
  const expiry = f.date(new Date(r.expiresAt), 'short');
  const btn = { size: 'md', mih: 44 } as const;

  return (
    <Container size="sm" px="xs" py="sm" data-testid="guest-review">
      <Stack gap="sm">
        <Group gap="xs" aria-hidden="true">
          <BrandMark size={24} />
          <Text fw={700} ff="var(--mantine-font-family-headings)">{BRAND_NAME}</Text>
        </Group>
        <Title order={3} style={{ overflowWrap: 'anywhere' }} data-testid="guest-title">
          {r.title}
        </Title>
        {r.stale && (
          <Alert color="yellow" variant="light" title={t('staleTitle')} data-testid="guest-stale-banner">
            <Text size="sm">{t('staleBody')}</Text>
          </Alert>
        )}
        {r.decision && (
          <Alert color={r.decision.decision === 'approved' ? 'green' : 'orange'} variant="light" data-testid="guest-decision-banner">
            <Text size="sm">
              {r.decision.decision === 'approved' ? t('approvedBy', { name: r.decision.guestName }) : t('askedChanges', { name: r.decision.guestName })}
            </Text>
          </Alert>
        )}

        <video
          ref={attachVideo}
          src={guestMediaUrl(token)}
          controls
          playsInline
          preload="metadata"
          tabIndex={0}
          onKeyDown={onVideoKey}
          aria-label={t('film')}
          style={{ width: '100%', borderRadius: 'var(--mantine-radius-sm)', background: '#000', maxHeight: '60dvh' }}
          data-testid="guest-video"
        />

        <Group justify="space-between" wrap="nowrap" align="center">
          <Text size="sm" ff="monospace" px="xs" style={{ borderRadius: 'var(--mantine-radius-sm)', border: '1px solid var(--mantine-color-default-border)' }} data-testid="guest-timecode">
            {framesToTimecode(frame, rate)}
          </Text>
          <Text size="xs" c="dimmed">
            {t('keys')}
          </Text>
        </Group>

        <Box style={{ position: 'relative', height: 44 }} data-testid="guest-marker-bar">
          <input
            type="range"
            min={0}
            max={totalFrames}
            step={1}
            value={Math.min(frame, totalFrames)}
            onChange={(e) => seekFrame(Number(e.currentTarget.value))}
            aria-label={t('position')}
            style={{ position: 'absolute', inset: 0, width: '100%', height: 44, margin: 0 }}
            data-testid="guest-scrubber"
          />
          {threads.map(({ root }) => (
            <button
              key={root.id}
              type="button"
              aria-label={t('noteAtLabel', { time: framesToTimecode(root.frame, rate) })}
              onClick={() => seekFrame(root.frame)}
              style={{
                position: 'absolute',
                left: `calc(${Math.min(100, (root.frame / totalFrames) * 100)}% - 22px)`,
                top: 0,
                width: 44,
                height: 44,
                padding: 0,
                border: 0,
                background: 'transparent',
                cursor: 'var(--rb-cursor-pointer)',
              }}
              data-testid={`guest-marker-${root.id}`}
            >
              <span aria-hidden style={{ display: 'block', margin: '0 auto', width: 10, height: 10, borderRadius: 5, background: root.resolved ? 'var(--mantine-color-gray-6)' : 'var(--mantine-color-yellow-5)', border: '1px solid #000' }} />
            </button>
          ))}
        </Box>

        <TextInput
          size="md"
          label={t('yourName')}
          description={t('yourNameHint')}
          value={name}
          maxLength={80}
          onChange={(e) => {
            setName(e.currentTarget.value);
            saveName(e.currentTarget.value.trim());
          }}
          autoComplete="name"
          data-testid="guest-name"
        />

        {r.allowComments && (
          <Stack gap="xs">
            <Textarea
              size="md"
              label={t('noteAtLabel', { time: framesToTimecode(frame, rate) })}
              value={body}
              maxLength={2000}
              minRows={2}
              autosize
              onChange={(e) => setBody(e.currentTarget.value)}
              data-testid="guest-comment-body"
            />
            <Checkbox label={t('cover')} checked={cover} onChange={(e) => setCover(e.currentTarget.checked)} styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }} data-testid="guest-cover" />
            <Button {...btn} disabled={busy || nameMissing || body.trim() === ''} onClick={() => void sendComment()} data-testid="guest-comment-send">
              {t('addNote')}
            </Button>
          </Stack>
        )}
        {nameMissing && <Text size="xs" c="dimmed">{t('nameFirst')}</Text>}
        {problem && (
          <Text size="sm" c="red" role="alert" data-testid="guest-problem">
            {problem}
          </Text>
        )}

        <Stack gap="xs" data-testid="guest-decision">
          <Group gap="sm" wrap="wrap">
            <Button {...btn} color="green" disabled={busy || nameMissing} onClick={() => void sendDecision('approved')} data-testid="guest-approve">
              {t('approve')}
            </Button>
            <Button {...btn} variant="default" disabled={busy || nameMissing} onClick={() => setAskingChanges(true)} data-testid="guest-request-changes">
              {t('requestChanges')}
            </Button>
          </Group>
          {askingChanges && (
            <Stack gap="xs">
              <Textarea
                size="md"
                label={guestHasCommented ? t('whatChangeOptional') : t('whatChange')}
                value={note}
                maxLength={1000}
                minRows={2}
                autosize
                onChange={(e) => setNote(e.currentTarget.value)}
                data-testid="guest-changes-note"
              />
              <Group gap="sm">
                <Button {...btn} disabled={busy || nameMissing || needsNote} onClick={() => void sendDecision('changesRequested')} data-testid="guest-changes-send">
                  {t('send')}
                </Button>
                <Button {...btn} variant="subtle" onClick={() => setAskingChanges(false)}>
                  {t('cancel')}
                </Button>
              </Group>
            </Stack>
          )}
        </Stack>

        <Stack gap="xs" data-testid="guest-threads">
          <Text size="sm" fw={600}>
            {threads.length === 0 ? t('noNotes') : t('noteCount', { count: threads.length })}
          </Text>
          {threads.map(({ root, replies }) => (
            <Stack key={root.id} gap={4} p="xs" style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 'var(--mantine-radius-sm)', opacity: root.resolved ? 0.6 : 1 }} data-testid={`guest-thread-${root.id}`}>
              <Group justify="space-between" wrap="nowrap">
                <Text size="sm" ff="monospace">{framesToTimecode(root.frame, rate)}</Text>
                <Button size="md" mih={44} variant="subtle" onClick={() => seekFrame(root.frame)} data-testid={`guest-jump-${root.id}`}>
                  {t('jumpTo')}
                </Button>
              </Group>
              <Text size="sm" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{root.body}</Text>
              <Text size="xs" c="dimmed">
                {t('byline', { name: root.authorName, guest: root.guest ? 'yes' : 'no', resolved: root.resolved ? 'yes' : 'no' })}
              </Text>
              {replies.map((c) => (
                <Stack key={c.id} gap={0} pl="sm" style={{ borderLeft: '2px solid var(--mantine-color-default-border)' }}>
                  <Text size="sm" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{c.body}</Text>
                  <Text size="xs" c="dimmed">
                    {t('byline', { name: c.authorName, guest: c.guest ? 'yes' : 'no', resolved: 'no' })}
                  </Text>
                </Stack>
              ))}
              {r.allowComments &&
                (replyTo === root.id ? (
                  <Stack gap="xs">
                    <Textarea size="md" aria-label={t('reply')} value={reply} maxLength={2000} minRows={1} autosize onChange={(e) => setReply(e.currentTarget.value)} data-testid={`guest-reply-body-${root.id}`} />
                    <Group gap="sm">
                      <Button {...btn} disabled={busy || nameMissing || reply.trim() === ''} onClick={() => void sendReply(root)} data-testid={`guest-reply-send-${root.id}`}>
                        {t('reply')}
                      </Button>
                      <Button {...btn} variant="subtle" onClick={() => setReplyTo(null)}>
                        {t('cancel')}
                      </Button>
                    </Group>
                  </Stack>
                ) : (
                  <Button size="md" mih={44} variant="subtle" style={{ alignSelf: 'flex-start' }} onClick={() => setReplyTo(root.id)} data-testid={`guest-reply-${root.id}`}>
                    {t('reply')}
                  </Button>
                ))}
            </Stack>
          ))}
        </Stack>

        <Text size="xs" c="dimmed" data-testid="guest-expiry">
          {t('expiry', { date: expiry })}
        </Text>
      </Stack>
    </Container>
  );
}
