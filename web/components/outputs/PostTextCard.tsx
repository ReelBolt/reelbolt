'use client';

import { describeError } from '@/lib/api/describe-error';
import { useState } from 'react';
import { Badge, Button, Card, Group, Stack, Text, Textarea, TextInput } from '@mantine/core';
import { IconCheck, IconCopy, IconDeviceFloppy, IconPencil, IconRefresh } from '@tabler/icons-react';
import { useT } from '@/i18n';

export interface PostTextCardData {
  platform: string;
  label: string;
  title: string | null;
  body: string;
  hashtags: string[];
  limit: number;
  titleLimit: number | null;
  hashtagsInBody: boolean;
  trimmed: boolean;
  saved: boolean;
}

export function parseHashtags(raw: string): string[] {
  return raw.split(/\s+/).map((h) => h.replace(/^#+/, '').trim()).filter(Boolean);
}

const tagsText = (tags: string[]): string => tags.map((h) => `#${h}`).join(' ');

/** What the platform counts: the text, plus the hashtags when they are typed into the same text. */
export function countCharacters(body: string, tags: string[], inBody: boolean): number {
  return body.length + (inBody && tags.length > 0 ? 2 + tagsText(tags).length : 0);
}

export function PostTextCard({
  card, busy, onSave, onRegenerate,
}: {
  card: PostTextCardData;
  busy: boolean;
  onSave: (edit: { title: string | null; body: string; hashtags: string[]; edited: boolean }) => Promise<void>;
  onRegenerate: () => void;
}) {
  const t = useT('postText.card');
  const tAll = useT();
  const [editing, setEditing] = useState(false);
  const [title, setTitle] = useState(card.title ?? '');
  const [body, setBody] = useState(card.body);
  const [tags, setTags] = useState(tagsText(card.hashtags));
  const [edited, setEdited] = useState(false);
  const [note, setNote] = useState<{ text: string; error: boolean } | null>(null);
  const [saving, setSaving] = useState(false);

  const parsed = parseHashtags(tags);
  const count = countCharacters(body, parsed, card.hashtagsInBody);
  const over = count > card.limit || (card.titleLimit !== null && title.length > card.titleLimit);
  const fullText = [card.titleLimit !== null && title ? title : null, [body, tagsText(parsed)].filter(Boolean).join('\n\n')]
    .filter(Boolean).join('\n\n');

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(fullText);
      setNote({ text: t('copied'), error: false });
    } catch {
      setNote({ text: t('copyFailed'), error: true });
    }
  };

  const save = async () => {
    setSaving(true);
    try {
      await onSave({ title: card.titleLimit !== null ? title || null : null, body, hashtags: parsed, edited });
      setNote({ text: t('saved'), error: false });
    } catch (e) {
      setNote({ text: describeError(tAll, e, t('saveFailed')), error: true });
    } finally {
      setSaving(false);
    }
  };

  return (
    <Card withBorder radius="md" padding="md" data-testid={`post-text-card-${card.platform}`}>
      <Stack gap="xs">
        <Group justify="space-between" gap="xs">
          <Text fw={600}>{card.label}</Text>
          {card.saved && <Badge variant="light" size="md" leftSection={<IconCheck size={12} />}>{t('savedBadge')}</Badge>}
        </Group>
        {editing ? (
          <Stack gap="xs">
            {card.titleLimit !== null && (
              <TextInput
                label={t('titleLabel')} value={title} onChange={(e) => { setTitle(e.currentTarget.value); setEdited(true); }}
                description={t('titleCounter', { count: title.length, limit: card.titleLimit })}
              />
            )}
            <Textarea
              label={t('bodyLabel')} value={body} autosize minRows={3} maxRows={12}
              onChange={(e) => { setBody(e.currentTarget.value); setEdited(true); }}
            />
            <TextInput
              label={t('hashtagsLabel')} description={t('hashtagsHint')} value={tags}
              onChange={(e) => { setTags(e.currentTarget.value); setEdited(true); }}
            />
          </Stack>
        ) : (
          <Stack gap={4}>
            {card.titleLimit !== null && title && <Text fw={600} size="sm">{title}</Text>}
            <Text size="sm" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{body}</Text>
            {parsed.length > 0 && <Text size="sm" c="dimmed" style={{ overflowWrap: 'anywhere' }}>{tagsText(parsed)}</Text>}
          </Stack>
        )}
        <Text size="xs" c={over ? 'red' : 'dimmed'} role={over ? 'alert' : undefined} data-testid={`post-text-count-${card.platform}`}>
          {t('counter', { count, limit: card.limit })}
          {over ? ` ${t('over', { platform: card.label })}` : ''}
        </Text>
        {card.trimmed && !edited && <Text size="xs" c="dimmed">{t('trimmed')}</Text>}
        <Group gap="xs">
          <Button size="xs" fz="xs" variant="light" style={{ minHeight: 44 }} leftSection={<IconCopy size={14} />} onClick={() => void copy()}>{t('copy')}</Button>
          <Button size="xs" fz="xs" variant="light" style={{ minHeight: 44 }} leftSection={<IconPencil size={14} />} onClick={() => setEditing((v) => !v)}>
            {editing ? t('doneEditing') : t('edit')}
          </Button>
          <Button size="xs" fz="xs" variant="light" style={{ minHeight: 44 }} leftSection={<IconRefresh size={14} />} disabled={busy} onClick={onRegenerate}>{t('regenerate')}</Button>
          <Button size="xs" fz="xs" style={{ minHeight: 44 }} leftSection={<IconDeviceFloppy size={14} />} loading={saving} disabled={busy || body.trim() === ''} onClick={() => void save()}>{t('save')}</Button>
        </Group>
        {note && <Text size="xs" c={note.error ? 'red' : 'dimmed'} role="status">{note.text}</Text>}
      </Stack>
    </Card>
  );
}
