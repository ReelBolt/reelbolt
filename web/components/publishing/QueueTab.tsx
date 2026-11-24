'use client';

import { useState } from 'react';
import {
  Alert, Anchor, Badge, Box, Button, Card, Checkbox, Group, Loader, Modal, SegmentedControl, Select, Stack, Text, Textarea, TextInput,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconChevronLeft, IconChevronRight } from '@tabler/icons-react';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { EmptyState } from '@/components/shared/EmptyState';
import { useFormat, useT } from '@/i18n';
import { useSocialPosts } from '@/lib/hooks/use-social';
import { cancelSocialPost, retrySocialPost, socialErrorMessage, updateSocialPost } from '@/lib/api/social';
import type { SocialPost, SocialPostStatus } from '@/lib/types/social';
import {
  browserTimeZone, groupByDay, hasTitle, hashtagsText, localDateTime, monthGrid, parseHashtags, splitLocal, statusColor, timeZoneChoices,
} from './social-utils';

const MONTH_TITLE: Intl.DateTimeFormatOptions = { month: 'long', year: 'numeric' };

export function useStatusLabel() {
  const t = useT('publishing.queue.status');
  return (s: SocialPostStatus): string => {
    switch (s) {
      case 'draft': return t('draft');
      case 'scheduled': return t('scheduled');
      case 'publishing': return t('publishing');
      case 'published': return t('published');
      case 'failed': return t('failed');
      case 'cancelled': return t('cancelled');
      default: return t('unknown');
    }
  };
}

/** The queue as a list or a month view, with edit (time and text), cancel and retry. */
export function QueueTab() {
  const t = useT('publishing.queue');
  const f = useFormat();
  const statusLabel = useStatusLabel();
  const { data, error, isLoading, mutate } = useSocialPosts();
  const [view, setView] = useState<'list' | 'month'>('list');
  const [editing, setEditing] = useState<SocialPost | null>(null);
  const [cancelling, setCancelling] = useState<SocialPost | null>(null);
  const [retrying, setRetrying] = useState<SocialPost | null>(null);
  const [confirmedAbsent, setConfirmedAbsent] = useState(false);
  const [busy, setBusy] = useState(false);

  const run = async (action: () => Promise<unknown>, okMessage: string, failMessage: string) => {
    setBusy(true);
    try {
      await action();
      notifications.show({ color: 'green', message: okMessage });
    } catch (e) {
      notifications.show({ color: 'red', message: socialErrorMessage(e) ?? failMessage });
    } finally {
      setBusy(false);
      setCancelling(null);
      setRetrying(null);
      setConfirmedAbsent(false);
      await mutate();
    }
  };

  if (isLoading) return <Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">{t('loading')}</Text></Group>;
  if (error) return <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert">{t('loadFailed')}</Alert>;
  const posts = data?.items ?? [];
  if (posts.length === 0) return <EmptyState title={t('emptyTitle')} description={t('empty')} />;

  const when = (p: SocialPost): string => {
    const iso = p.publishedAt ?? p.scheduledAt;
    return iso ? f.date(iso, 'dateTime') : t('noTime');
  };

  return (
    <Stack gap="md" data-testid="publishing-queue">
      <SegmentedControl
        value={view} onChange={(v) => setView(v === 'month' ? 'month' : 'list')}
        data={[{ value: 'list', label: t('viewList') }, { value: 'month', label: t('viewMonth') }]}
        style={{ alignSelf: 'flex-start' }}
      />

      {view === 'month' && <MonthView posts={posts} statusLabel={statusLabel} />}

      {view === 'list' && posts.map((p) => (
        <Card key={p.id} withBorder padding="sm" data-testid={`post-${p.id}`}>
          <Stack gap="xs">
            <Group justify="space-between" wrap="wrap">
              <Group gap="xs">
                <Badge variant="outline">{p.platform}</Badge>
                <Badge color={statusColor(p.status)} variant="light">{statusLabel(p.status)}</Badge>
                <Text size="sm" c="dimmed">{when(p)}</Text>
              </Group>
              <Group gap="xs">
                {p.canEdit && <Button size="sm" variant="default" style={{ minHeight: 44 }} onClick={() => setEditing(p)}>{t('edit')}</Button>}
                {p.canCancel && <Button size="sm" variant="default" color="red" style={{ minHeight: 44 }} onClick={() => setCancelling(p)}>{t('cancel')}</Button>}
                {p.canRetry && <Button size="sm" style={{ minHeight: 44 }} onClick={() => setRetrying(p)}>{t('retry')}</Button>}
              </Group>
            </Group>
            <Text size="sm" lineClamp={2}>{p.title || p.body || t('noText')}</Text>
            {p.status === 'failed' && p.failureMessage && <Text size="sm" c="red">{p.failureMessage}</Text>}
            {p.status === 'unknown' && (
              <Alert color="orange" icon={<IconAlertTriangle size={16} />} data-testid="unknown-prompt">
                {t('unknownPrompt')}
                {p.remotePostUrl && <> <Anchor href={p.remotePostUrl} target="_blank" rel="noopener noreferrer">{t('openChannel')}</Anchor></>}
              </Alert>
            )}
            {p.status === 'published' && p.remotePostUrl && (
              <Anchor size="sm" href={p.remotePostUrl} target="_blank" rel="noopener noreferrer">{t('openPost')}</Anchor>
            )}
          </Stack>
        </Card>
      ))}

      {editing && <EditPostModal post={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); void mutate(); }} />}

      <ConfirmModal
        opened={cancelling !== null} onClose={() => setCancelling(null)} loading={busy}
        title={t('cancelTitle')} message={t('cancelMessage')} confirmLabel={t('cancel')}
        onConfirm={() => cancelling && void run(() => cancelSocialPost(cancelling.id), t('cancelled'), t('cancelFailed'))}
      />

      <Modal opened={retrying !== null} onClose={() => { setRetrying(null); setConfirmedAbsent(false); }} title={t('retryTitle')} centered size="sm">
        <Stack gap="md">
          <Text size="sm">{retrying?.status === 'unknown' ? t('retryUnknown') : t('retryMessage')}</Text>
          {retrying?.status === 'unknown' && (
            <Checkbox checked={confirmedAbsent} onChange={(e) => setConfirmedAbsent(e.currentTarget.checked)} label={t('retryConfirm')} />
          )}
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRetrying(null)}>{t('close')}</Button>
            <Button
              loading={busy} disabled={retrying?.status === 'unknown' && !confirmedAbsent}
              onClick={() => retrying && void run(() => retrySocialPost(retrying.id, confirmedAbsent), t('retried'), t('retryFailed'))}
            >
              {t('retry')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

function MonthView({ posts, statusLabel }: { posts: SocialPost[]; statusLabel: (s: SocialPostStatus) => string }) {
  const t = useT('publishing.queue');
  const f = useFormat();
  const now = new Date();
  const [cursor, setCursor] = useState({ year: now.getFullYear(), month: now.getMonth() });
  const byDay = groupByDay(posts);
  const weeks = monthGrid(cursor.year, cursor.month);
  const shift = (delta: number) => setCursor((c) => {
    const d = new Date(c.year, c.month + delta, 1);
    return { year: d.getFullYear(), month: d.getMonth() };
  });
  // 2024-01-01 is a Monday: weekday names without hard-coding any language.
  const weekdays = Array.from({ length: 7 }, (_, i) => new Intl.DateTimeFormat(f.locale, { weekday: 'short' }).format(new Date(2024, 0, 1 + i)));
  return (
    <Stack gap="xs" data-testid="publishing-month">
      <Group justify="space-between">
        <Button variant="default" size="sm" aria-label={t('prevMonth')} style={{ minHeight: 44 }} onClick={() => shift(-1)}><IconChevronLeft size={16} /></Button>
        <Text fw={600}>{new Intl.DateTimeFormat(f.locale, MONTH_TITLE).format(new Date(cursor.year, cursor.month, 1))}</Text>
        <Button variant="default" size="sm" aria-label={t('nextMonth')} style={{ minHeight: 44 }} onClick={() => shift(1)}><IconChevronRight size={16} /></Button>
      </Group>
      <Box style={{ overflowX: 'auto' }}>
        <Box style={{ display: 'grid', gridTemplateColumns: 'repeat(7, minmax(88px, 1fr))', gap: 4, minWidth: 640 }}>
          {weekdays.map((w) => <Text key={w} size="xs" c="dimmed" ta="center">{w}</Text>)}
          {weeks.flat().map((cell) => {
            const items = byDay.get(cell.key) ?? [];
            return (
              <Box
                key={cell.key} data-testid={`day-${cell.key}`}
                style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 4, minHeight: 72, padding: 4, opacity: cell.inMonth ? 1 : 0.4 }}
              >
                <Text size="xs" c="dimmed">{cell.day}</Text>
                {items.slice(0, 3).map((p) => (
                  <Badge key={p.id} size="xs" variant="light" color={statusColor(p.status)} fullWidth mt={2} title={statusLabel(p.status)} style={{ textTransform: 'none' }}>
                    {p.title || p.body || p.platform}
                  </Badge>
                ))}
                {items.length > 3 && <Text size="xs" c="dimmed">{t('more', { count: items.length - 3 })}</Text>}
              </Box>
            );
          })}
        </Box>
      </Box>
    </Stack>
  );
}

function EditPostModal({ post, onClose, onSaved }: { post: SocialPost; onClose: () => void; onSaved: () => void }) {
  const t = useT('publishing.queue.editDialog');
  const initial = splitLocal(post.scheduledLocal);
  const [title, setTitle] = useState(post.title ?? '');
  const [body, setBody] = useState(post.body ?? '');
  const [tags, setTags] = useState(hashtagsText(post.hashtags));
  const [date, setDate] = useState(initial.date);
  const [time, setTime] = useState(initial.time);
  const [zone, setZone] = useState(post.timeZone ?? browserTimeZone());
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const local = localDateTime(date, time);

  const save = async () => {
    if (!local) { setError(t('badTime')); return; }
    setSaving(true);
    setError(null);
    try {
      await updateSocialPost(post.id, {
        title: hasTitle(post.platform) ? title.trim() || null : null,
        body, hashtags: parseHashtags(tags), scheduledLocal: local, timeZone: zone,
      });
      notifications.show({ color: 'green', message: t('saved') });
      onSaved();
    } catch (e) {
      setError(socialErrorMessage(e) ?? t('failed'));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title')} centered fullScreen={false} size="lg">
      <Stack gap="sm">
        {error && <Alert color="red" role="alert">{error}</Alert>}
        {hasTitle(post.platform) && <TextInput label={t('titleLabel')} value={title} onChange={(e) => setTitle(e.currentTarget.value)} />}
        <Textarea label={t('bodyLabel')} value={body} onChange={(e) => setBody(e.currentTarget.value)} autosize minRows={4} />
        <TextInput label={t('hashtagsLabel')} value={tags} onChange={(e) => setTags(e.currentTarget.value)} />
        <Group grow align="flex-end">
          <TextInput type="date" label={t('dateLabel')} value={date} onChange={(e) => setDate(e.currentTarget.value)} />
          <TextInput type="time" label={t('timeLabel')} value={time} onChange={(e) => setTime(e.currentTarget.value)} />
        </Group>
        <Select label={t('zoneLabel')} data={timeZoneChoices(zone)} value={zone} onChange={(v) => v && setZone(v)} searchable allowDeselect={false} />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>{t('close')}</Button>
          <Button loading={saving} onClick={() => void save()}>{t('save')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
