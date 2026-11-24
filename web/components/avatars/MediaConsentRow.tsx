'use client';

import { useState } from 'react';
import { Badge, Button, Card, Group, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { grantAvatarMediaConsent, withdrawAvatarMediaConsent } from '@/lib/api/avatars';
import { describeError } from '@/lib/api/describe-error';
import { useAvatarMediaConsent } from '@/lib/hooks/use-avatars';
import type { AvatarMediaConsent } from '@/lib/types/avatars';
import { useFormat, useT } from '@/i18n';

/**
 * The project's one-time permission to send the pictures and videos of its avatars and objects to AI services
 * from the editors and the assistant (docs/design/avatar-objects.md "Avatar REST contract with objects").
 * It is given and withdrawn only here, by a person: the assistant never grants it. A workflow step is not
 * covered by it and keeps its own switch.
 * A server that does not have the permission yet (no answer, and nothing in the requirements) shows nothing.
 */
export function MediaConsentRow({ projectId, initial }: {
  projectId: string;
  /** What the requirements said, shown until the permission's own route answers. */
  initial?: AvatarMediaConsent;
}) {
  const t = useT('avatars.mediaConsent');
  const tAll = useT();
  const f = useFormat();
  const { data, mutate } = useAvatarMediaConsent(projectId);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const consent = data ?? initial;
  if (!consent) return null;

  const change = async (allow: boolean) => {
    setBusy(true);
    setError(null);
    try {
      const next = await (allow ? grantAvatarMediaConsent(projectId) : withdrawAvatarMediaConsent(projectId));
      await mutate(next, { revalidate: false });
      notifications.show({ color: allow ? 'green' : 'yellow', message: allow ? t('allowed') : t('withdrawn') });
    } catch (e) {
      setError(describeError(tAll, e, t('failed')));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card withBorder radius="md" padding="md" data-testid="avatar-media-consent">
      <Group justify="space-between" align="flex-start" wrap="wrap" gap="sm">
        <Stack gap={4} style={{ flex: '1 1 320px', minWidth: 0 }}>
          <Group gap="xs">
            <Text fw={650}>{t('title')}</Text>
            <Badge color={consent.consented ? 'green' : 'gray'} variant="light" fz="xs" data-testid="avatar-media-consent-state">
              {consent.consented
                ? (consent.consentedAt ? t('onSince', { date: f.date(consent.consentedAt) }) : t('on'))
                : t('off')}
            </Badge>
          </Group>
          <Text size="sm" c="dimmed">{t('body')}</Text>
          <Text size="sm" c="dimmed">{t('stepsNote')}</Text>
          {error && <Text size="sm" c="red" role="alert" data-testid="avatar-media-consent-error">{error}</Text>}
        </Stack>
        {consent.consented ? (
          <Button variant="default" onClick={() => void change(false)} loading={busy} style={{ minHeight: 44 }}>{t('withdraw')}</Button>
        ) : (
          <Button variant="light" onClick={() => void change(true)} loading={busy} style={{ minHeight: 44 }}>{t('allow')}</Button>
        )}
      </Group>
    </Card>
  );
}
