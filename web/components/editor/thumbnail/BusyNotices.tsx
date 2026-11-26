'use client';

import React from 'react';
import { Alert, Button, Group, Modal, Text } from '@mantine/core';
import { useT } from '@/i18n';
import { EDITOR_MODAL_Z } from '@/components/editor/ui/z-index';

export interface BusyGuardModalProps {
  opened: boolean;
  /** Offered only when the job runs as a stoppable run (the graphic read names its execution). */
  onStop?: () => void;
  onGoOn: () => void;
  onBack: () => void;
}

/** Asked before an edit that would change a layer a background job is still working on. */
export function BusyGuardModal({ opened, onStop, onGoOn, onBack }: BusyGuardModalProps) {
  const t = useT('editor-thumbnail-a.editor.busyGuard');
  return (
    <Modal zIndex={EDITOR_MODAL_Z} opened={opened} onClose={onBack} title={t('title')} centered size="sm">
      <Text size="sm" mb="lg">
        {t('message')}
      </Text>
      <Group justify="flex-end">
        {onStop && (
          <Button variant="subtle" color="red" onClick={onStop} data-testid="busy-guard-stop">
            {t('stop')}
          </Button>
        )}
        <Button variant="default" onClick={onBack} data-testid="busy-guard-back">
          {t('back')}
        </Button>
        <Button onClick={onGoOn} data-testid="busy-guard-go-on">
          {t('goOn')}
        </Button>
      </Group>
    </Modal>
  );
}

export interface ReviewBarProps {
  onShow: () => void;
  onDiscard: () => void;
  busy?: boolean;
}

/** A finished result whose layer was deleted meanwhile: kept hidden until the user decides. */
export function ReviewBar({ onShow, onDiscard, busy = false }: ReviewBarProps) {
  const t = useT('editor-thumbnail-a.editor.review');
  return (
    <Alert color="yellow" variant="light" role="status" aria-label={t('label')} data-testid="review-bar">
      <Group justify="space-between" gap="xs" wrap="wrap">
        <Text size="sm">{t('message')}</Text>
        <Group gap="xs">
          <Button size="xs" fz="xs" onClick={onShow} disabled={busy} data-testid="review-show">
            {t('show')}
          </Button>
          <Button size="xs" fz="xs" variant="default" onClick={onDiscard} disabled={busy} data-testid="review-discard">
            {t('discard')}
          </Button>
        </Group>
      </Group>
    </Alert>
  );
}
