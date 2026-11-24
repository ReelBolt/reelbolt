'use client';

import { useState } from 'react';
import Link from 'next/link';
import {
  Alert, Anchor, Badge, Button, Checkbox, Group, Loader, Modal, Radio, Select, Stack, Switch, Text, Textarea, TextInput,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconSend } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { useSavedPostTexts } from '@/lib/hooks/use-post-texts';
import { useSocialAccounts, useSocialProviders } from '@/lib/hooks/use-social';
import { createSocialPost, isPlanGated, preflightSocialPost, socialErrorMessage } from '@/lib/api/social';
import type { SocialAccount, SocialOutputKind, SocialPostContent, SocialPreflight, SocialPrivacy } from '@/lib/types/social';
import {
  browserTimeZone, hasTitle, hashtagsText, localDateTime, parseHashtags, postTextPlatform, privacyChoices, timeZoneChoices,
} from './social-utils';

interface Props { projectId: string; outputId: string; outputKind: SocialOutputKind; size?: 'xs' | 'sm' }

/** "Publish" button plus its dialog, for one finished output. Sits next to "Write post text". */
export function PublishButton({ projectId, outputId, outputKind, size = 'sm' }: Props) {
  const t = useT('publishing.dialog');
  const [opened, setOpened] = useState(false);
  return (
    <>
      <Button
        variant="default" size={size} leftSection={<IconSend size={16} />} style={{ minHeight: 44 }}
        onClick={() => setOpened(true)} data-testid="publish-open"
      >
        {t('open')}
      </Button>
      {opened && <PublishDialog projectId={projectId} outputId={outputId} outputKind={outputKind} onClose={() => setOpened(false)} />}
    </>
  );
}

type Step = 'content' | 'when' | 'review' | 'done';
type Edit = Partial<{ title: string; body: string; tags: string; privacy: SocialPrivacy; aiGenerated: boolean }>;
type Result = { accountId: string; ok: boolean; message: string | null };

export function PublishDialog({ projectId, outputId, outputKind, onClose }: Props & { onClose: () => void }) {
  const t = useT('publishing.dialog');
  const isPhone = useMediaQuery('(max-width: 48em)');
  const providers = useSocialProviders();
  const accounts = useSocialAccounts();
  const saved = useSavedPostTexts(projectId, outputId);
  const [step, setStep] = useState<Step>('content');
  const [selected, setSelected] = useState<string[]>([]);
  const [edits, setEdits] = useState<Record<string, Edit>>({});
  const [mode, setMode] = useState<'now' | 'later'>('now');
  const [date, setDate] = useState('');
  const [time, setTime] = useState('09:00');
  const [zone, setZone] = useState(browserTimeZone());
  const [checks, setChecks] = useState<Record<string, SocialPreflight | string> | null>(null);
  const [checking, setChecking] = useState(false);
  const [sending, setSending] = useState(false);
  const [gated, setGated] = useState(false);
  const [results, setResults] = useState<Result[]>([]);

  const usable = (accounts.data ?? []).filter((a) => a.status === 'active');
  const stale = (accounts.data ?? []).filter((a) => a.status !== 'active');
  const chosen = usable.filter((a) => selected.includes(a.id));
  const local = mode === 'later' ? localDateTime(date, time) : null;

  const contentOf = (a: SocialAccount): SocialPostContent & { postTextId: string | null } => {
    const text = (saved.data?.items ?? []).find((s) => s.platform === postTextPlatform(a.provider));
    const e = edits[a.id] ?? {};
    return {
      title: hasTitle(a.provider) ? (e.title ?? text?.title ?? '').trim() || null : null,
      body: e.body ?? text?.body ?? '',
      hashtags: e.tags !== undefined ? parseHashtags(e.tags) : (text?.hashtags ?? []),
      options: { privacy: e.privacy ?? 'public', aiGenerated: e.aiGenerated ?? true, commentsEnabled: true },
      postTextId: e.body === undefined && e.title === undefined && e.tags === undefined ? (text?.id ?? null) : null,
    };
  };
  const patch = (id: string, e: Edit) => setEdits((prev) => ({ ...prev, [id]: { ...prev[id], ...e } }));

  const review = async () => {
    setStep('review');
    setChecking(true);
    setChecks(null);
    const out: Record<string, SocialPreflight | string> = {};
    await Promise.all(chosen.map(async (a) => {
      const c = contentOf(a);
      try {
        out[a.id] = await preflightSocialPost({ projectId, outputKind, outputId, accountId: a.id, title: c.title, body: c.body, hashtags: c.hashtags, options: c.options });
      } catch (e) {
        if (isPlanGated(e)) setGated(true);
        out[a.id] = socialErrorMessage(e) ?? t('preflightFailed');
      }
    }));
    setChecks(out);
    setChecking(false);
  };

  const confirm = async () => {
    setSending(true);
    const out: Result[] = [];
    for (const a of chosen) {
      const c = contentOf(a);
      try {
        await createSocialPost({
          projectId, outputKind, outputId, accountId: a.id, title: c.title, body: c.body, hashtags: c.hashtags, options: c.options,
          postTextId: c.postTextId, publishNow: mode === 'now', scheduledLocal: local, timeZone: mode === 'later' ? zone : null,
        });
        out.push({ accountId: a.id, ok: true, message: null });
      } catch (e) {
        if (isPlanGated(e)) setGated(true);
        out.push({ accountId: a.id, ok: false, message: socialErrorMessage(e) ?? t('sendFailed') });
      }
    }
    setResults(out);
    setSending(false);
    setStep('done');
  };

  const allOk = checks !== null && chosen.every((a) => typeof checks[a.id] === 'object' && (checks[a.id] as SocialPreflight).ok);
  const loading = providers.isLoading || accounts.isLoading;
  const nameOf = (a: SocialAccount) => a.displayName ?? providers.data?.find((p) => p.provider === a.provider)?.displayName ?? a.provider;
  const platformName = (id: string) => providers.data?.find((p) => p.provider === id)?.displayName ?? id;

  return (
    <Modal opened onClose={onClose} title={t('title')} size="lg" centered={!isPhone} fullScreen={!!isPhone}>
      <Stack gap="md" data-testid="publish-dialog">
        {gated && (
          <Alert color="orange" role="alert" data-testid="publish-plan-gated">
            {t('planGated')} <Anchor component={Link} href="/settings/billing">{t('seePlans')}</Anchor>
          </Alert>
        )}

        {loading && <Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">{t('loading')}</Text></Group>}

        {!loading && (providers.data ?? []).length === 0 && usable.length === 0 && (
          <Text size="sm" data-testid="publish-not-available">{t('notAvailable')}</Text>
        )}

        {!loading && (providers.data?.length ?? 0) + usable.length > 0 && usable.length === 0 && step === 'content' && (
          <Text size="sm">{t('noAccounts')} <Anchor component={Link} href="/publishing">{t('connectAccount')}</Anchor></Text>
        )}

        {step === 'content' && usable.length > 0 && (
          <>
            <Text fw={500}>{t('accountsLabel')}</Text>
            {usable.map((a) => (
              <Checkbox
                key={a.id} label={`${nameOf(a)} (${platformName(a.provider)})`} checked={selected.includes(a.id)}
                onChange={(e) => setSelected((s) => (e.currentTarget.checked ? [...s, a.id] : s.filter((x) => x !== a.id)))}
                styles={{ root: { minHeight: 36 } }}
              />
            ))}
            {stale.length > 0 && <Text size="xs" c="dimmed">{t('needReconnect', { count: stale.length })}</Text>}

            {chosen.map((a) => {
              const c = contentOf(a);
              const e = edits[a.id] ?? {};
              return (
                <Stack key={a.id} gap="xs" p="sm" style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8 }} data-testid={`draft-${a.id}`}>
                  <Group gap="xs"><Text fw={600}>{platformName(a.provider)}</Text><Badge variant="light">{nameOf(a)}</Badge></Group>
                  {hasTitle(a.provider) && (
                    <TextInput label={t('titleLabel')} value={e.title ?? c.title ?? ''} onChange={(ev) => patch(a.id, { title: ev.currentTarget.value })} />
                  )}
                  <Textarea label={t('bodyLabel')} value={c.body ?? ''} onChange={(ev) => patch(a.id, { body: ev.currentTarget.value })} autosize minRows={3} />
                  <TextInput label={t('hashtagsLabel')} value={e.tags ?? hashtagsText(c.hashtags)} onChange={(ev) => patch(a.id, { tags: ev.currentTarget.value })} />
                  <Select
                    label={t('privacyLabel')} allowDeselect={false} value={c.options.privacy ?? 'public'}
                    data={privacyChoices(a.provider).map((p) => ({ value: p, label: privacyLabel(t, p) }))}
                    onChange={(v) => v && patch(a.id, { privacy: v as SocialPrivacy })}
                  />
                  {a.provider === 'youtube' && <Text size="xs" c="dimmed">{t('youtubePrivate')}</Text>}
                  <Switch label={t('aiLabel')} description={t('aiHint')} checked={c.options.aiGenerated ?? true} onChange={(ev) => patch(a.id, { aiGenerated: ev.currentTarget.checked })} />
                </Stack>
              );
            })}
            <Group justify="flex-end">
              <Button variant="default" onClick={onClose}>{t('close')}</Button>
              <Button disabled={chosen.length === 0} onClick={() => setStep('when')} style={{ minHeight: 44 }}>{t('next')}</Button>
            </Group>
          </>
        )}

        {step === 'when' && (
          <>
            <Radio.Group value={mode} onChange={(v) => setMode(v === 'later' ? 'later' : 'now')} label={t('whenLabel')}>
              <Stack gap="xs" mt="xs">
                <Radio value="now" label={t('whenNow')} />
                <Radio value="later" label={t('whenLater')} />
              </Stack>
            </Radio.Group>
            {mode === 'later' && (
              <Stack gap="xs">
                <Group grow align="flex-end">
                  <TextInput type="date" label={t('dateLabel')} value={date} onChange={(e) => setDate(e.currentTarget.value)} />
                  <TextInput type="time" label={t('timeLabel')} value={time} onChange={(e) => setTime(e.currentTarget.value)} />
                </Group>
                <Select label={t('zoneLabel')} data={timeZoneChoices(zone)} value={zone} onChange={(v) => v && setZone(v)} searchable allowDeselect={false} />
              </Stack>
            )}
            <Group justify="space-between">
              <Button variant="default" onClick={() => setStep('content')}>{t('back')}</Button>
              <Button disabled={mode === 'later' && !local} onClick={() => void review()} style={{ minHeight: 44 }}>{t('next')}</Button>
            </Group>
          </>
        )}

        {step === 'review' && (
          <>
            <Text fw={500}>{t('reviewTitle')}</Text>
            <Text size="sm">{mode === 'now' ? t('reviewNow') : t('reviewLater', { date, time, zone })}</Text>
            {checking && <Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">{t('checking')}</Text></Group>}
            {checks && chosen.map((a) => {
              const c = checks[a.id];
              return (
                <Stack key={a.id} gap={4} data-testid={`check-${a.id}`}>
                  <Group gap="xs">
                    <Text fw={600}>{platformName(a.provider)}</Text>
                    <Badge color={typeof c === 'object' && c.ok ? 'green' : 'red'} variant="light">
                      {typeof c === 'object' && c.ok ? t('checkOk') : t('checkProblem')}
                    </Badge>
                  </Group>
                  {typeof c === 'string' && <Text size="sm" c="red">{c}</Text>}
                  {typeof c === 'object' && c.issues.map((i) => (
                    <Text key={`${i.code}-${i.field}`} size="sm" c={i.severity === 'error' ? 'red' : 'orange'}>
                      {i.message}
                      {i.rerenderVertical && <> <Anchor component={Link} href="/editor" data-testid="rerender-vertical">{t('rerenderVertical')}</Anchor></>}
                    </Text>
                  ))}
                </Stack>
              );
            })}
            <Text size="xs" c="dimmed">{t('aiNote')}</Text>
            <Group justify="space-between">
              <Button variant="default" onClick={() => setStep('when')}>{t('back')}</Button>
              <Button loading={sending} disabled={checking || !allOk} onClick={() => void confirm()} style={{ minHeight: 44 }} data-testid="publish-confirm">
                {mode === 'now' ? t('confirmNow') : t('confirmLater')}
              </Button>
            </Group>
          </>
        )}

        {step === 'done' && (
          <>
            {results.map((r) => {
              const a = (accounts.data ?? []).find((x) => x.id === r.accountId);
              return (
                <Alert key={r.accountId} color={r.ok ? 'green' : 'red'} role={r.ok ? 'status' : 'alert'}>
                  {a ? nameOf(a) : r.accountId}: {r.ok ? (mode === 'now' ? t('sentNow') : t('sentLater')) : r.message}
                </Alert>
              );
            })}
            <Group justify="flex-end">
              <Anchor component={Link} href="/publishing" size="sm">{t('openQueue')}</Anchor>
              <Button onClick={onClose}>{t('close')}</Button>
            </Group>
          </>
        )}
      </Stack>
    </Modal>
  );
}

function privacyLabel(t: ReturnType<typeof useT>, p: SocialPrivacy): string {
  switch (p) {
    case 'public': return t('privacyPublic');
    case 'unlisted': return t('privacyUnlisted');
    case 'private': return t('privacyPrivate');
    default: return t('privacyConnections');
  }
}
