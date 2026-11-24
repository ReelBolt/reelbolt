'use client';

import { useEffect } from 'react';
import { Button, Group, Modal, Text } from '@mantine/core';
import { useT } from '@/i18n';
import { enT } from '@/lib/i18n-default';
import { holdAssistantDialog } from '@/lib/assistant/dialog-guard';

/** The warning, word for word, shared by every way into auto mode. */
export const AUTO_MODE_WARNING = enT('assistant.autoMode.warning');

interface AutoModeConfirmModalProps {
  opened: boolean;
  /** The person read the warning and chose auto mode. */
  onConfirm: () => void;
  /** Closing the dialog any other way — the safe button, Escape, the backdrop — keeps asking. */
  onCancel: () => void;
  loading?: boolean;
}

/**
 * The one place auto mode is switched on from. Both the switch in the drawer's header and a request's
 * "Always allow" go through it, so the person reads the same plain words whichever way they got
 * here: this mode includes deletions; only a cost above the limit still asks.
 *
 * Opens above the assistant's own drawer (which sits at Mantine's default modal layer), and puts the
 * focus on the safe answer rather than the one that removes the checks.
 */
export function AutoModeConfirmModal({ opened, onConfirm, onCancel, loading = false }: AutoModeConfirmModalProps) {
  const t = useT();
  // While this is up, Escape belongs to it alone — the drawer underneath must not close with it.
  useEffect(() => (opened ? holdAssistantDialog() : undefined), [opened]);

  return (
    <Modal
      opened={opened}
      onClose={onCancel}
      title={t('assistant.autoMode.title')}
      centered
      size="sm"
      zIndex={300}
      closeButtonProps={{ 'aria-label': t('assistant.autoMode.closeLabel') }}
    >
      <Text size="sm" mb="lg">
        {t('assistant.autoMode.warning')}
      </Text>
      <Group justify="flex-end">
        <Button variant="default" onClick={onCancel} data-autofocus>
          {t('assistant.autoMode.keepAsking')}
        </Button>
        <Button onClick={onConfirm} loading={loading}>
          {t('assistant.autoMode.turnOn')}
        </Button>
      </Group>
    </Modal>
  );
}
