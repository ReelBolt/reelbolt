'use client';

import { Alert, Button, Group, Modal, Text } from '@mantine/core';
import { useT } from '@/i18n';

/** A destructive confirm that says what is lost; localised buttons (the shared one is English-only). */
export function HistoryConfirm({
  opened, onClose, onConfirm, title, message, confirmLabel, loading, error,
}: {
  opened: boolean;
  onClose: () => void;
  onConfirm: () => void;
  title: string;
  message: string;
  confirmLabel: string;
  loading?: boolean;
  error?: string | null;
}) {
  const t = useT('projectHistory');
  return (
    <Modal opened={opened} onClose={onClose} title={title} centered size="sm">
      <Text size="sm" mb="md">{message}</Text>
      {error && <Alert color="red" mb="md" role="alert">{error}</Alert>}
      <Group justify="flex-end">
        <Button variant="default" onClick={onClose}>{t('restore.cancel')}</Button>
        <Button color="red" onClick={onConfirm} loading={loading}>{confirmLabel}</Button>
      </Group>
    </Modal>
  );
}
