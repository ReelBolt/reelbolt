'use client';

import type { ReactNode } from 'react';
import { Button, Group, Modal, Stack, Text } from '@mantine/core';

/**
 * The confirm step in front of a billing change. Both buttons carry their own label (never
 * "OK"/"Cancel"), because on this page "cancel" is itself one of the actions.
 */
export function BillingConfirmModal({
  opened,
  title,
  children,
  confirmLabel,
  confirmColor,
  dismissLabel,
  testId,
  onConfirm,
  onClose,
}: {
  opened: boolean;
  title: string;
  children: ReactNode;
  confirmLabel: string;
  confirmColor?: string;
  dismissLabel: string;
  /** Root of the modal's `data-testid`s: `{testId}`, `{testId}-confirm`, `{testId}-dismiss`. */
  testId: string;
  onConfirm: () => void;
  onClose: () => void;
}) {
  return (
    <Modal opened={opened} onClose={onClose} title={title} centered size="md" data-testid={testId}>
      <Stack gap="lg">
        <Text size="sm">{children}</Text>
        <Group justify="flex-end" gap="sm">
          <Button variant="default" onClick={onClose} data-testid={`${testId}-dismiss`}>
            {dismissLabel}
          </Button>
          <Button color={confirmColor} onClick={onConfirm} data-testid={`${testId}-confirm`}>
            {confirmLabel}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
