'use client';

import { useState } from 'react';
import { Button, Checkbox, Group, List, Modal, Stack, Text } from '@mantine/core';
import { useT } from '@/i18n';

export interface MediaConsentDialogProps {
  opened: boolean;
  /** The consent is being saved. */
  busy: boolean;
  /** Saving it failed: say so, and let the person try again. */
  failed: boolean;
  onCancel: () => void;
  /** The explicit yes. The caller records it (`PUT …/avatars/media-consent`) and then repeats what it was doing. */
  onConfirm: () => void;
}

/**
 * The one-time question before an avatar's or an object's files leave the project for an AI service from an editor
 * (docs/design/avatar-objects.md "Avatar REST contract with objects", media consent). It is asked once per project,
 * only by a person in the app, and can be withdrawn in the Avatars tab. Shared by the timeline's "Generate clip"
 * dialog, the picture editor and "Make a variant with AI".
 */
export function MediaConsentDialog({ opened, busy, failed, onCancel, onConfirm }: MediaConsentDialogProps) {
  return opened ? <OpenConsent busy={busy} failed={failed} onCancel={onCancel} onConfirm={onConfirm} /> : null;
}

function OpenConsent({ busy, failed, onCancel, onConfirm }: Omit<MediaConsentDialogProps, 'opened'>) {
  const t = useT('avatars.mediaConsent.dialog');
  // Mounted only while open, so the box always starts unticked.
  const [agreed, setAgreed] = useState(false);
  return (
    <Modal opened onClose={onCancel} title={t('title')} centered trapFocus returnFocus data-testid="media-consent-dialog">
      <Stack gap="sm">
        <Text size="sm">{t('body')}</Text>
        <List size="sm" spacing={2}>
          <List.Item>{t('whatPictures')}</List.Item>
          <List.Item>{t('whatVideos')}</List.Item>
          <List.Item>{t('whatVoice')}</List.Item>
        </List>
        <Text size="sm" c="dimmed">{t('once')}</Text>
        <Checkbox size="sm" label={t('check')} checked={agreed} onChange={(e) => setAgreed(e.currentTarget.checked)} data-autofocus data-testid="media-consent-check" />
        {failed && (
          <Text size="sm" c="red" role="alert" data-testid="media-consent-error">{t('failed')}</Text>
        )}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" mih={44} onClick={onCancel} data-testid="media-consent-cancel">{t('cancel')}</Button>
          <Button mih={44} disabled={!agreed} loading={busy} onClick={onConfirm} data-testid="media-consent-confirm">{t('confirm')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
