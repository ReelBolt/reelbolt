'use client';

import { useState } from 'react';
import { Alert, Button, Group, Modal, Stack, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { publishToAvatarLibrary } from '@/lib/api/avatar-library';
import { isObjectAvatar } from '@/lib/avatars/model';
import { libraryLimits } from '@/lib/avatars/library';
import type { AvatarLibraryRequirements, AvatarRequirements, ProjectAvatar } from '@/lib/types/avatars';
import { useT } from '@/i18n';
import { useLibraryErrorMessage } from './library-errors';
import { useWorkspaceConfirmation, WorkspaceConfirmationFields } from './WorkspaceConfirmation';

interface Props {
  projectId: string;
  library: AvatarLibraryRequirements;
  requirements: Pick<AvatarRequirements, 'limits' | 'locales'>;
  /** The project avatar to save; null keeps the dialog closed. */
  avatar: ProjectAvatar | null;
  onClose: () => void;
  onSaved: () => void | Promise<void>;
}

/**
 * Saving a project avatar to the workspace library (an Owner or Admin). A consent given for one project never
 * becomes workspace-wide by itself: the person confirms the WORKSPACE statement of the avatar's kind, served by
 * the server and sent back unchanged, and for a person the 18-or-older tick and the keep-for days.
 * Afterwards every project of the workspace can import the item, and this project's avatar is linked to it.
 */
export function SaveToLibraryModal(props: Props) {
  // A fresh form every time it opens, so a ticked statement is never carried to the next avatar.
  if (!props.avatar) return null;
  return <SaveForm key={props.avatar.id} {...props} avatar={props.avatar} />;
}

function SaveForm({ projectId, library, requirements, avatar, onClose, onSaved }: Props & { avatar: ProjectAvatar }) {
  const t = useT('avatars.library.save');
  const tForm = useT('avatars.form');
  const phone = Boolean(useMediaQuery('(max-width: 600px)'));
  const isObject = isObjectAvatar(avatar);
  const limits = libraryLimits(requirements.limits);
  const confirmation = useWorkspaceConfirmation({
    kind: isObject ? 'Object' : 'Person', library, limits, locales: requirements.locales,
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const describe = useLibraryErrorMessage();

  const submit = async () => {
    if (!confirmation.ready || saving) return;
    setSaving(true);
    setError(null);
    try {
      await publishToAvatarLibrary({ projectId, avatarId: avatar.id, ...confirmation.body });
      notifications.show({ color: 'green', message: t('saved', { name: avatar.name }) });
      await onSaved();
    } catch (e) {
      // A statement refusal that carries the server's text shows that text for a fresh tick instead of an error.
      setError(confirmation.acceptRefusal(e) ? null : describe(e, t('failed')));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title', { name: avatar.name })} size="lg" fullScreen={phone} radius="md">
      <Stack gap="sm" data-testid="library-save-form">
        <Text size="sm" data-testid="library-save-explain">{isObject ? t('explainObject', { name: avatar.name }) : t('explainPerson', { name: avatar.name })}</Text>
        <WorkspaceConfirmationFields
          confirmation={confirmation} limits={limits}
          about={isObject ? undefined : <Text size="sm" c="dimmed">{tForm('statementAbout', { name: avatar.consent.subjectName || avatar.name })}</Text>}
        />
        {error && <Alert color="red" variant="light" data-testid="library-save-error">{error}</Alert>}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
          <Button onClick={() => void submit()} disabled={!confirmation.ready} loading={saving} style={{ minHeight: 44 }} data-testid="library-save-submit">
            {t('confirm')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
