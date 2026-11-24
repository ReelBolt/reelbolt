'use client';

import { useState } from 'react';
import { ActionIcon, Alert, Button, Checkbox, Group, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconCheck, IconPencil, IconTrash, IconX } from '@tabler/icons-react';
import { Panel, QuietText } from '@/components/dashboard/Panel';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { ApiError } from '@/lib/api/client';
import { createPronunciation, deletePronunciation, updatePronunciation } from '@/lib/api/pronunciation';
import { useProjectPronunciation } from '@/lib/hooks/use-pronunciation';
import { DEFAULT_GLOSSARY_MAX, type PronunciationEntry } from '@/lib/types/pronunciation';
import { useT } from '@/i18n';
import { describeError, readableServerMessage } from '@/lib/api/describe-error';
import type { T } from '@/i18n/translator';

function describe(t: T, tAll: T, e: unknown, fallback: string): string {
  if (e instanceof ApiError) {
    if (e.status === 409) return readableServerMessage(e) ?? t('duplicate');
    if (e.status === 403) return t('readOnly');
    if (e.errors.length > 0) return e.errors.join(' ');
  }
  return describeError(tAll, e, fallback);
}

const CONTROL_HEIGHT = { input: { minHeight: 44 } } as const;

/**
 * Project pronunciation glossary: tells the narrator how to say a word. Applies to spoken narration
 * only (never subtitles or on-screen text). Writes the API refuses (viewers) surface as a message.
 */
export function ProjectPronunciationTab({ projectId }: { projectId: string }) {
  const t = useT('projects.pronunciation');
  const tAll = useT();
  const { data, mutate, isLoading, error: loadError } = useProjectPronunciation(projectId);
  const entries = data?.entries ?? [];
  const max = data?.max ?? DEFAULT_GLOSSARY_MAX;

  const [term, setTerm] = useState('');
  const [spelling, setSpelling] = useState('');
  const [caseSensitive, setCaseSensitive] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [editingId, setEditingId] = useState<string | null>(null);
  const [editTerm, setEditTerm] = useState('');
  const [editSpelling, setEditSpelling] = useState('');
  const [editCase, setEditCase] = useState(false);
  const [editError, setEditError] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<PronunciationEntry | null>(null);

  async function onAdd() {
    setFormError(null);
    setBusy(true);
    try {
      await createPronunciation(projectId, { term: term.trim(), spelling: spelling.trim(), caseSensitive });
      setTerm('');
      setSpelling('');
      setCaseSensitive(false);
      await mutate();
    } catch (e) {
      setFormError(describe(t, tAll, e, t('addFailed')));
    } finally {
      setBusy(false);
    }
  }

  function startEdit(entry: PronunciationEntry) {
    setEditingId(entry.id);
    setEditTerm(entry.term);
    setEditSpelling(entry.spelling);
    setEditCase(entry.caseSensitive);
    setEditError(null);
  }

  async function onSave() {
    if (!editingId) return;
    setEditError(null);
    setBusy(true);
    try {
      await updatePronunciation(projectId, editingId, { term: editTerm.trim(), spelling: editSpelling.trim(), caseSensitive: editCase });
      setEditingId(null);
      await mutate();
    } catch (e) {
      setEditError(describe(t, tAll, e, t('saveFailed')));
    } finally {
      setBusy(false);
    }
  }

  async function onDelete() {
    if (!pendingDelete) return;
    setBusy(true);
    try {
      await deletePronunciation(projectId, pendingDelete.id);
      setPendingDelete(null);
      await mutate();
    } catch (e) {
      notifications.show({ color: 'red', message: describe(t, tAll, e, t('deleteFailed')) });
    } finally {
      setBusy(false);
    }
  }

  const full = entries.length >= max;

  return (
    <Stack gap="md">
      <Panel title={t('title')}>
        <QuietText>
          {t('intro')}
        </QuietText>
      </Panel>

      <Panel title={t('addTitle')}>
        <Stack gap="sm">
          <TextInput label={t('word')} value={term} onChange={(e) => setTerm(e.currentTarget.value)} maxLength={60} styles={CONTROL_HEIGHT} />
          <TextInput label={t('howToSay')} description={t('howToSayHint')} value={spelling} onChange={(e) => setSpelling(e.currentTarget.value)} maxLength={120} styles={CONTROL_HEIGHT} />
          <Checkbox label={t('matchCase')} checked={caseSensitive} onChange={(e) => setCaseSensitive(e.currentTarget.checked)} styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }} />
          {formError && <Alert color="red" role="alert">{formError}</Alert>}
          <Button onClick={onAdd} loading={busy} disabled={!term.trim() || !spelling.trim() || full} style={{ minHeight: 44 }} fullWidth>
            {t('addWord')}
          </Button>
          {full && <QuietText>{t('full')}</QuietText>}
        </Stack>
      </Panel>

      <Panel title={t('yourWords', { count: entries.length, max })}>
        {loadError && <Alert color="red" role="alert">{describe(t, tAll, loadError, t('loadFailed'))}</Alert>}
        {!loadError && !isLoading && entries.length === 0 && (
          <Stack gap="xs"><Text fw={650}>{t('noWords')}</Text><QuietText>{t('noWordsHint')}</QuietText></Stack>
        )}
        <Stack gap="sm">
          {entries.map((entry) =>
            editingId === entry.id ? (
              <Stack key={entry.id} gap="xs" data-testid="pronunciation-edit-row">
                <TextInput label={t('word')} value={editTerm} onChange={(e) => setEditTerm(e.currentTarget.value)} maxLength={60} styles={CONTROL_HEIGHT} />
                <TextInput label={t('howToSay')} value={editSpelling} onChange={(e) => setEditSpelling(e.currentTarget.value)} maxLength={120} styles={CONTROL_HEIGHT} />
                <Checkbox label={t('matchCase')} checked={editCase} onChange={(e) => setEditCase(e.currentTarget.checked)} styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }} />
                {editError && <Alert color="red" role="alert">{editError}</Alert>}
                <Group gap="xs">
                  <Button leftSection={<IconCheck size={16} />} onClick={onSave} loading={busy} style={{ minHeight: 44 }}>{t('save')}</Button>
                  <Button variant="default" leftSection={<IconX size={16} />} onClick={() => setEditingId(null)} style={{ minHeight: 44 }}>{t('cancel')}</Button>
                </Group>
              </Stack>
            ) : (
              <Group key={entry.id} justify="space-between" wrap="nowrap" align="center">
                <div style={{ minWidth: 0, overflowWrap: 'anywhere' }}>
                  <Text fw={600}>{entry.term} -&gt; {entry.spelling}</Text>
                  {entry.caseSensitive && <QuietText>{t('caseMustMatch')}</QuietText>}
                </div>
                <Group gap={4} wrap="nowrap">
                  <ActionIcon variant="subtle" size={44} aria-label={t('edit', { term: entry.term })} onClick={() => startEdit(entry)}><IconPencil size={18} /></ActionIcon>
                  <ActionIcon variant="subtle" color="red" size={44} aria-label={t('delete', { term: entry.term })} onClick={() => setPendingDelete(entry)}><IconTrash size={18} /></ActionIcon>
                </Group>
              </Group>
            ),
          )}
        </Stack>
      </Panel>

      <ConfirmModal
        opened={pendingDelete !== null}
        onClose={() => setPendingDelete(null)}
        onConfirm={onDelete}
        title={t('deleteTitle')}
        message={t('deleteMessage', { term: pendingDelete?.term ?? '' })}
        loading={busy}
      />
    </Stack>
  );
}
