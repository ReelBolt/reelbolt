'use client';

import { Alert, Button, Code, CopyButton, Group, Stack, Text, VisuallyHidden } from '@mantine/core';
import { IconAlertTriangle, IconCheck, IconCopy } from '@tabler/icons-react';
import { useT } from '@/i18n';

interface SecretOnceStepProps {
  /** The explanatory sentence under the title; says what the secret is for. */
  body: string;
  secret: string;
  onDone: () => void;
}

/**
 * The shown-once secret step, for a webhook signing secret. The owner keeps the secret in its own
 * state and clears it on close; this component holds nothing.
 */
export function SecretOnceStep({ body, secret, onDone }: SecretOnceStepProps) {
  const t = useT('org.apiKeys');
  return (
    <Stack gap="md" data-testid="webhook-secret-step">
      <Text size="sm">{body}</Text>
      <Stack gap={4}>
        <Text size="sm" fw={600}>
          {t('secret.label')}
        </Text>
        <Code block p="md" data-testid="webhook-secret">
          {secret}
        </Code>
      </Stack>
      <Alert icon={<IconAlertTriangle size={16} />} color="yellow" variant="light">
        {t('webhooks.secret.warning')}
      </Alert>
      <CopyButton value={secret}>
        {({ copied, copy }) => (
          <>
            <Button
              leftSection={copied ? <IconCheck size={16} /> : <IconCopy size={16} />}
              color={copied ? 'green' : undefined}
              onClick={copy}
              fullWidth
            >
              {copied ? t('secret.copied') : t('secret.copy')}
            </Button>
            <VisuallyHidden role="status" aria-live="polite">
              {copied ? t('secret.copied') : ''}
            </VisuallyHidden>
          </>
        )}
      </CopyButton>
      <Group justify="flex-end">
        <Button onClick={onDone}>{t('secret.done')}</Button>
      </Group>
    </Stack>
  );
}
