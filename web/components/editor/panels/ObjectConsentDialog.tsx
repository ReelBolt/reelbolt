'use client';

import React, { useState } from 'react';
import { Button, Checkbox, Group, Modal, Stack, Text } from '@mantine/core';
import { useT } from '@/i18n';

export interface ObjectConsentDialogProps {
  opened: boolean;
  busy: boolean;
  /** Plain-words reason the permission could not be saved, when it could not. */
  error: string | null;
  onCancel: () => void;
  /** The explicit confirm; the host writes the consent and then continues the run. */
  onConfirm: () => void;
}

/** Sending a video to an outside AI service needs an explicit yes. Cancel writes nothing. */
export function ObjectConsentDialog(props: ObjectConsentDialogProps) {
  // Mounted only while open, so the checkbox always starts unticked.
  return props.opened ? <OpenDialog {...props} /> : null;
}

function OpenDialog({ opened, busy, error, onCancel, onConfirm }: ObjectConsentDialogProps) {
  const t = useT('editor.panels.objectEffects');
  const [agreed, setAgreed] = useState(false);
  return (
    <Modal opened={opened} onClose={onCancel} title={t('consentTitle')} centered trapFocus returnFocus data-testid="object-consent-dialog">
      <Stack gap="sm">
        <Text size="sm">{t('consentBody')}</Text>
        <Checkbox size="sm" label={t('consentCheck')} checked={agreed} onChange={(e) => setAgreed(e.currentTarget.checked)} data-autofocus data-testid="object-consent-check" />
        {error && (
          <Text size="xs" c="red" role="alert" data-testid="object-consent-error">
            {error}
          </Text>
        )}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" mih={44} onClick={onCancel} data-testid="object-consent-cancel">
            {t('consentCancel')}
          </Button>
          <Button mih={44} disabled={!agreed} loading={busy} onClick={onConfirm} data-testid="object-consent-confirm">
            {t('consentConfirm')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
