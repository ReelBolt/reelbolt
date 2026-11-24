'use client';

import { describeError } from '@/lib/api/describe-error';
import { useState } from 'react';
import { Button, Group, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { mutate as mutateGlobal } from 'swr';
import { AUTH_SETTINGS_PATH, updateAuthSettings } from '@/lib/api/auth-settings';
import { notifyAuthChange } from '@/lib/hooks/use-auth';
import { useT } from '@/i18n';

/**
 * "Your name" (A02): the name the dashboard greets you by. The server trims it and refreshes the
 * readable session cookie on a successful save, so notifyAuthChange() is enough for the greeting to
 * update without signing in again. A refusal's message is shown inline, next to the field.
 */
export function DisplayNameForm({ currentName }: { currentName: string }) {
  const t = useT('settings.displayName');
  const tAll = useT();
  const [draft, setDraft] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const value = draft ?? currentName;
  const unchanged = value.trim() === currentName.trim();

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    if (saving || unchanged) return;
    setSaving(true);
    setError(null);
    try {
      const saved = await updateAuthSettings({ displayName: value });
      await mutateGlobal(AUTH_SETTINGS_PATH, saved, { revalidate: false });
      notifyAuthChange();
      setDraft(null);
      notifications.show({ color: 'green', message: t('saved') });
    } catch (err) {
      setError(describeError(tAll, err, t('saveFailed')));
    } finally {
      setSaving(false);
    }
  };

  return (
    <form onSubmit={(e) => void save(e)} data-testid="display-name-form">
      <Group align="flex-end" gap="sm" wrap="wrap">
        <TextInput
          label={t('label')}
          description={t('description')}
          value={value}
          onChange={(e) => setDraft(e.currentTarget.value)}
          maxLength={100}
          error={error}
          disabled={saving}
          style={{ flex: '1 1 220px' }}
          styles={{ input: { minHeight: 44 } }}
        />
        <Button type="submit" loading={saving} disabled={unchanged} style={{ minHeight: 44 }}>
          {t('save')}
        </Button>
      </Group>
    </form>
  );
}
