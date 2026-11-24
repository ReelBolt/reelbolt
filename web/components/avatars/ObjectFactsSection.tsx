'use client';

import { useState } from 'react';
import { Alert, Button, Checkbox, Group, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';
import { deriveAvatarIdentityFacts, updateAvatar } from '@/lib/api/avatars';
import { FACT_FIELDS, FACT_MAX, draftOf, factsPatch, hasFacts, type FactDraft } from '@/lib/avatars/facts';
import { isLinkedAvatar } from '@/lib/avatars/library';
import type { ProjectAvatar } from '@/lib/types/avatars';
import { useT } from '@/i18n';
import { useAvatarErrorMessage } from './avatar-errors';

/**
 * What an object looks like in plain words (colour, make and model, marks, size): the AI is told these so it
 * cannot change them. Typed by the owner or read from the pictures. An import from the library is read-only.
 */
export function ObjectFactsSection({ projectId, avatar, onSaved }: {
  projectId: string;
  avatar: ProjectAvatar;
  onSaved: () => void | Promise<void>;
}) {
  const t = useT('avatars.facts');
  const readOnly = isLinkedAvatar(avatar);
  const [facts, setFacts] = useState(avatar.identityFacts ?? null);
  const [draft, setDraft] = useState<FactDraft>(() => draftOf(avatar.identityFacts));
  const [overwrite, setOverwrite] = useState(false);
  const [busy, setBusy] = useState<'save' | 'derive' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const describe = useAvatarErrorMessage(() => null, 'Object');
  const patch = factsPatch(draft, facts);

  const fail = (e: unknown, fallback: string) => {
    if (e instanceof ApiError && e.code && t.has(`errors.${e.code}`)) setError(t(`errors.${e.code}`));
    else setError(describe(e, fallback));
  };

  const adopt = (saved: ProjectAvatar) => {
    setFacts(saved.identityFacts ?? null);
    setDraft(draftOf(saved.identityFacts));
  };

  const save = async () => {
    if (!patch) return;
    setBusy('save');
    setError(null);
    try {
      adopt(await updateAvatar(projectId, avatar.id, { identityFacts: patch }));
      notifications.show({ color: 'green', message: t('saved') });
      await onSaved();
    } catch (e) {
      fail(e, t('saveFailed'));
    } finally {
      setBusy(null);
    }
  };

  const derive = async () => {
    setBusy('derive');
    setError(null);
    try {
      adopt(await deriveAvatarIdentityFacts(projectId, avatar.id, overwrite));
      notifications.show({ color: 'green', message: t('derived') });
      await onSaved();
    } catch (e) {
      fail(e, t('deriveFailed'));
    } finally {
      setBusy(null);
    }
  };

  return (
    <Stack gap="xs" data-testid="object-facts">
      <Text fw={600} size="sm">{t('title')}</Text>
      <Text size="sm" c="dimmed">{readOnly ? t('readOnly') : t('hint')}</Text>
      {readOnly && !hasFacts(facts) && <Text size="sm" c="dimmed">{t('none')}</Text>}
      {(!readOnly || hasFacts(facts)) && FACT_FIELDS.map((field) => (
        <TextInput
          key={field} label={t(`fields.${field}`)} placeholder={t(`placeholders.${field}`)} maxLength={FACT_MAX}
          value={draft[field]} readOnly={readOnly} onChange={(e) => { const v = e.currentTarget.value; setDraft((d) => ({ ...d, [field]: v })); }}
          data-testid={`object-fact-${field}`}
        />
      ))}
      {error && <Alert color="red" variant="light" data-testid="object-facts-error">{error}</Alert>}
      {!readOnly && (
        <>
          <Checkbox label={t('overwrite')} description={t('overwriteHint')} checked={overwrite} onChange={(e) => setOverwrite(e.currentTarget.checked)} />
          <Group gap="xs">
            <Button variant="default" onClick={() => void derive()} loading={busy === 'derive'} disabled={busy !== null} style={{ minHeight: 44 }} data-testid="object-facts-derive">
              {t('derive')}
            </Button>
            <Button onClick={() => void save()} loading={busy === 'save'} disabled={!patch || busy !== null} style={{ minHeight: 44 }} data-testid="object-facts-save">
              {t('save')}
            </Button>
          </Group>
        </>
      )}
    </Stack>
  );
}
