'use client';

import { describeError, readableServerMessage } from '@/lib/api/describe-error';
import { useEffect, useRef, useState } from 'react';
import { Alert, Anchor, Button, Checkbox, Group, Loader, Modal, Stack, Switch, Text, Textarea, TextInput } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconAlertCircle, IconWriting } from '@tabler/icons-react';
import { useFormat, useT } from '@/i18n';
import {
  estimatePostText, isPreparing, refusalOf, savePostText, startTranscript, writePostText,
  type PostTextDraft, type PostTextEstimate, type PostTextRequest, type PostTextResult, type SavedPostText,
} from '@/lib/api/post-text';
import { useSavedPostTexts } from '@/lib/hooks/use-post-texts';
import { PostTextCard, type PostTextCardData } from './PostTextCard';

const PLATFORM_IDS = ['tiktok', 'instagram_reels', 'youtube_shorts', 'linkedin', 'x'] as const;
const MAX_PREPARE_ROUNDS = 12;
const isHttpUrl = (u: string): boolean => {
  try { return ['http:', 'https:'].includes(new URL(u).protocol); } catch { return false; }
};
const wait = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

type PlatformMeta = { hashtagsInBody: boolean };

/** "Write post text" button plus its dialog, for one finished output (a step result or a saved render file). */
export function PostTextButton({ projectId, outputId, size = 'sm' }: { projectId: string; outputId: string; size?: 'xs' | 'sm' }) {
  const t = useT('postText');
  const [opened, setOpened] = useState(false);
  return (
    <>
      <Button
        variant="default" size={size} leftSection={<IconWriting size={16} />} style={{ minHeight: 44 }}
        onClick={() => setOpened(true)} data-testid="post-text-open"
      >
        {t('open')}
      </Button>
      {opened && <PostTextDialog projectId={projectId} outputId={outputId} onClose={() => setOpened(false)} />}
    </>
  );
}

export function PostTextDialog({ projectId, outputId, onClose }: { projectId: string; outputId: string; onClose: () => void }) {
  const t = useT('postText');
  const tAll = useT();
  const f = useFormat();
  const isPhone = useMediaQuery('(max-width: 48em)');
  const saved = useSavedPostTexts(projectId, outputId);
  const [platforms, setPlatforms] = useState<string[]>(['tiktok', 'instagram_reels']);
  const [tone, setTone] = useState('');
  const [language, setLanguage] = useState('');
  const [about, setAbout] = useState('');
  const [readVideo, setReadVideo] = useState(false);
  const [webHashtags, setWebHashtags] = useState(false);
  const [answer, setAnswer] = useState<{ key: string; estimate: PostTextEstimate | null } | null>(null);
  const [phase, setPhase] = useState<'idle' | 'preparing' | 'writing'>('idle');
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<PostTextResult | null>(null);
  const [regenerating, setRegenerating] = useState<string | null>(null);
  const alive = useRef(true);
  useEffect(() => () => { alive.current = false; }, []);

  const request = (ids: string[]): PostTextRequest => ({
    platforms: ids, tone: tone.trim() || null, language: language.trim() || null, about: about.trim() || null,
    readVideo, hashtagSource: webHashtags ? 'web' : 'model',
  });

  // The price follows the choices: asked again whenever one that changes it changes.
  const estimateKey = JSON.stringify([platforms, language.trim(), readVideo, webHashtags]);
  useEffect(() => {
    if (platforms.length === 0) return;
    let live = true;
    estimatePostText(projectId, outputId, {
      platforms, tone: null, language: language.trim() || null, about: null, readVideo, hashtagSource: webHashtags ? 'web' : 'model',
    })
      .then((e) => { if (live) setAnswer({ key: estimateKey, estimate: e }); })
      .catch(() => { if (live) setAnswer({ key: estimateKey, estimate: null }); });
    return () => { live = false; };
  // estimateKey encodes every other dependency.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, outputId, estimateKey]);
  const current = platforms.length > 0 && answer?.key === estimateKey ? answer : null;
  const estimate = current?.estimate ?? null;
  const estimating = platforms.length > 0 && current === null;
  const estimateFailed = current !== null && current.estimate === null;

  const explain = (e: unknown): string => {
    const kind = refusalOf(e);
    if (kind === 'credits') return t('creditsRefused');
    if (kind === 'held' && readableServerMessage(e) === null) return t('heldRefused');
    return describeError(tAll, e, t('failedTitle'));
  };

  /** Writes; while the video is being prepared, starts the transcript once and asks again after the pause the server named. */
  const run = async (ids: string[]): Promise<PostTextResult | null> => {
    let started = false;
    for (let round = 0; round < MAX_PREPARE_ROUNDS; round++) {
      const answer = await writePostText(projectId, outputId, request(ids));
      if (!isPreparing(answer)) return answer;
      if (!alive.current) return null;
      setPhase('preparing');
      const start = answer.video?.startTranscript;
      if (start && !started) {
        started = true;
        await startTranscript(start.path, start.kinds);
      }
      await wait(Math.max(2, answer.retryAfterSeconds) * 1000);
      if (!alive.current) return null;
    }
    throw new Error(t('preparingFailed'));
  };

  const write = async () => {
    setError(null);
    setPhase('writing');
    try {
      const r = await run(platforms);
      if (r && alive.current) setResult(r);
    } catch (e) {
      if (alive.current) setError(explain(e));
    } finally {
      if (alive.current) setPhase('idle');
    }
  };

  const regenerate = async (id: string) => {
    setError(null);
    setRegenerating(id);
    try {
      const r = await run([id]);
      if (r && alive.current) {
        setResult((prev) => prev ? { ...prev, ...r, drafts: prev.drafts.map((d) => (d.platform === id ? r.drafts.find((n) => n.platform === id) ?? d : d)) } : r);
      }
    } catch (e) {
      if (alive.current) setError(explain(e));
    } finally {
      if (alive.current) { setRegenerating(null); setPhase('idle'); }
    }
  };

  const meta = (id: string): PlatformMeta => ({
    hashtagsInBody: estimate?.platforms.items.find((p) => p.id === id)?.hashtagsInBody ?? (id === 'x' || id === 'linkedin'),
  });
  const labelOf = (id: string): string => {
    switch (id) {
      case 'tiktok': return t('platform.tiktok');
      case 'instagram_reels': return t('platform.instagram_reels');
      case 'youtube_shorts': return t('platform.youtube_shorts');
      case 'linkedin': return t('platform.linkedin');
      case 'x': return t('platform.x');
      default: return id;
    }
  };
  const kindLabel = (kind: string): string => {
    switch (kind) {
      case 'text': return t('lineKind.text');
      case 'transcript': return t('lineKind.transcript');
      case 'frames': return t('lineKind.frames');
      case 'webSearch': return t('lineKind.webSearch');
      default: return t('lineKindOther');
    }
  };
  /** Known server sentences get the reader's language; any other note is shown as the server sent it. */
  const noteLabel = (note: string): string => {
    switch (note) {
      case "The video has no speech to read.": return t('note.noSpeech');
      case "Only video and audio can be transcribed.": return t('note.onlyAv');
      case "The video's length is not known yet, so a transcript cannot be priced.": return t('note.noLength');
      case "Transcription isn't set up for this workspace.": return t('note.noTranscription');
      case "Frames are read from videos only.": return t('note.framesVideoOnly');
      case "No vision service is set up, so the frames are skipped.": return t('note.noVision');
      case "Too many frame sets were asked for recently, so the frames are skipped.": return t('note.tooManyFrames');
      case "A frame could not be read, so the frames are skipped.": return t('note.frameUnreadable');
      case "No frames were made.": return t('note.noFrames');
      case "Web search is not set up, so the writer's own tags are used.": return t('note.noWebSearch');
      case "This search provider is not usable here, so the writer's own tags are used.": return t('note.searchUnusable');
      default: return note;
    }
  };
  const stateLabel = (state: string): string | null => {
    switch (state) {
      case 'included': return t('lineState.included');
      case 'ready': return t('lineState.ready');
      case 'pending': return t('lineState.pending');
      case 'missing': return t('lineState.missing');
      case 'skipped': return t('lineState.skipped');
      default: return null;
    }
  };

  const fromDraft = (d: PostTextDraft): PostTextCardData => ({
    platform: d.platform, label: d.label || labelOf(d.platform), title: d.title, body: d.body, hashtags: d.hashtags,
    limit: d.limit, titleLimit: d.titleLimit, hashtagsInBody: meta(d.platform).hashtagsInBody, trimmed: d.trimmed, saved: false,
  });
  const fromSaved = (s: SavedPostText): PostTextCardData => ({
    platform: s.platform, label: labelOf(s.platform), title: s.title, body: s.body, hashtags: s.hashtags,
    limit: s.limit, titleLimit: s.titleLimit, hashtagsInBody: meta(s.platform).hashtagsInBody, trimmed: false, saved: true,
  });

  const savedItems = saved.data?.items ?? [];
  const fresh = new Map((result?.drafts ?? []).map((d) => [d.platform, d]));
  const cards: Array<{ key: string; data: PostTextCardData; language: string; tone: string | null }> = [];
  for (const d of result?.drafts ?? []) cards.push({ key: `new-${d.platform}-${d.text.length}-${d.body}`, data: fromDraft(d), language: result?.language ?? language, tone: result?.tone ?? null });
  for (const s of savedItems) {
    if (fresh.has(s.platform) && s.language === result?.language) continue;
    cards.push({ key: `saved-${s.id}-${s.updatedAt}`, data: fromSaved(s), language: s.language, tone: s.tone });
  }

  const save = async (c: { data: PostTextCardData; language: string; tone: string | null }, edit: { title: string | null; body: string; hashtags: string[]; edited: boolean }) => {
    await savePostText(projectId, outputId, c.data.platform, {
      language: c.language, title: edit.title, body: edit.body, hashtags: edit.hashtags, tone: c.tone, edited: edit.edited,
    });
    await saved.mutate();
  };

  const busy = phase !== 'idle' || regenerating !== null;
  const total = estimate?.totalCredits ?? estimate?.credits ?? null;
  const basis = result?.hashtagBasis;

  return (
    <Modal opened onClose={onClose} title={t('title')} size="lg" centered fullScreen={!!isPhone} closeButtonProps={{ size: 44, 'aria-label': t('close') }}>
      <Stack gap="md" data-testid="post-text-dialog">
        <Checkbox.Group label={t('platformsLabel')} value={platforms} onChange={setPlatforms}>
          <Group mt="xs" gap="md">
            {PLATFORM_IDS.map((id) => <Checkbox key={id} value={id} label={labelOf(id)} />)}
          </Group>
        </Checkbox.Group>
        <Group grow align="flex-start">
          <TextInput label={t('toneLabel')} placeholder={t('tonePlaceholder')} value={tone} maxLength={60} onChange={(e) => setTone(e.currentTarget.value)} />
          <TextInput label={t('languageLabel')} placeholder={t('languagePlaceholder')} value={language} maxLength={32} onChange={(e) => setLanguage(e.currentTarget.value)} />
        </Group>
        <Textarea label={t('aboutLabel')} placeholder={t('aboutPlaceholder')} value={about} autosize minRows={2} maxRows={6} onChange={(e) => setAbout(e.currentTarget.value)} />
        <Switch label={t('readVideo')} description={t('readVideoHint')} checked={readVideo} onChange={(e) => setReadVideo(e.currentTarget.checked)} data-testid="post-text-read-video" />
        <Switch label={t('webHashtags')} description={t('webHashtagsHint')} checked={webHashtags} onChange={(e) => setWebHashtags(e.currentTarget.checked)} data-testid="post-text-web" />

        <Text size="xs" c="dimmed" data-testid="post-text-data-handling">
          {t('dataHandling')}{readVideo ? ` ${t('dataHandlingVideo')}` : ''}
        </Text>
        {estimate?.disclosure && <Text size="xs" c="dimmed" data-testid="post-text-disclosure">{estimate.disclosure}</Text>}

        <Stack gap={4} data-testid="post-text-estimate" aria-live="polite">
          {estimating && <Group gap="xs"><Loader size="xs" /><Text size="sm">{t('estimate.checking')}</Text></Group>}
          {!estimating && estimateFailed && <Text size="sm" c="dimmed">{t('estimate.failed')}</Text>}
          {!estimating && estimate && (
            <>
              {estimate.billingEnabled && total !== null && total > 0
                ? <Text size="sm" fw={600}>{estimate.lineItems.length > 1 ? t('estimate.aboutTotal', { credits: f.credits(total * 1000) }) : t('estimate.about', { credits: f.credits(total * 1000) })}</Text>
                : <Text size="sm">{t('estimate.free')}</Text>}
              {estimate.lineItems.length > 0 && (
                <Stack gap={2}>
                  <Text size="xs" c="dimmed">{t('estimate.parts')}</Text>
                  {estimate.lineItems.map((li) => (
                    <Text size="xs" key={li.kind}>
                      {kindLabel(li.kind)}
                      {li.credits !== null && li.credits > 0 ? `: ${t('estimate.about', { credits: f.credits(li.credits * 1000) })}` : ''}
                      {stateLabel(li.state) ? ` (${stateLabel(li.state)}${li.heldSeparately ? `, ${t('estimate.separately')}` : ''})` : ''}
                      {li.note ? ` ${noteLabel(li.note)}` : ''}
                    </Text>
                  ))}
                </Stack>
              )}
              {!estimate.affordable && <Alert color="yellow" icon={<IconAlertCircle size={16} />}>{t('estimate.unaffordable')}</Alert>}
            </>
          )}
        </Stack>

        {platforms.length === 0 && <Text size="sm" c="red">{t('pickPlatform')}</Text>}
        {phase === 'preparing' && <Group gap="xs" role="status"><Loader size="xs" /><Text size="sm">{t('preparing')}</Text></Group>}
        {error && <Alert color="red" icon={<IconAlertCircle size={16} />} title={t('failedTitle')} data-testid="post-text-error">{error}</Alert>}
        <Group>
          <Button
            style={{ minHeight: 44 }} leftSection={<IconWriting size={16} />} loading={phase !== 'idle'}
            disabled={platforms.length === 0 || busy || estimate?.affordable === false} onClick={() => void write()} data-testid="post-text-write"
          >
            {phase === 'idle' ? t('write') : t('writing')}
          </Button>
        </Group>

        {basis && (
          <Stack gap={2} data-testid="post-text-basis">
            <Text size="xs" c="dimmed">{t('basis', { label: basis.label })}</Text>
            {basis.sources && basis.sources.length > 0 && (
              <>
                <Text size="xs" c="dimmed">{t('sourcesLabel')}</Text>
                {basis.sources.map((s) => (
                  isHttpUrl(s.url)
                    ? <Anchor key={s.url} href={s.url} target="_blank" rel="noopener noreferrer nofollow" size="xs">{s.title || s.host} ({s.host})</Anchor>
                    : <Text key={s.url} size="xs">{s.host}</Text>
                ))}
              </>
            )}
          </Stack>
        )}
        {result?.creditsUsed != null && result.creditsUsed > 0 && <Text size="xs" c="dimmed">{t('usedCredits', { credits: f.credits(result.creditsUsed * 1000) })}</Text>}

        {cards.length > 0 && (
          <Stack gap="sm">
            <Text fw={600} size="sm">{t('savedHeading')}</Text>
            {cards.map((c) => (
              <PostTextCard
                key={c.key} card={c.data} busy={busy}
                onSave={(edit) => save(c, edit)} onRegenerate={() => void regenerate(c.data.platform)}
              />
            ))}
          </Stack>
        )}
      </Stack>
    </Modal>
  );
}
