'use client';

import type { ReactNode } from 'react';
import Link from 'next/link';
import { ActionIcon, Anchor, Badge, Button, Card, Group, Menu, Stack, Text } from '@mantine/core';
import {
  IconBooks, IconBox, IconDotsVertical, IconEdit, IconFileDescription, IconFiles, IconMicrophone, IconShieldOff, IconTrash,
  IconUnlink, IconUserCircle,
} from '@tabler/icons-react';
import { CheckedImage } from '@/components/outputs/CheckedVideo';
import { projectFileUrl } from '@/lib/api/files';
import { FACT_FIELDS, hasFacts } from '@/lib/avatars/facts';
import { canSaveToLibrary, isLinkedAvatar } from '@/lib/avatars/library';
import { avatarStatus, isObjectAvatar, needsPictureConsent, primaryPictureFileId, statusColor } from '@/lib/avatars/model';
import type { AvatarLibraryRequirements, ProjectAvatar } from '@/lib/types/avatars';
import { useFormat, useT } from '@/i18n';
import { useAvatarName } from './avatar-names';
import { useAvatarWords } from './avatar-words';

/** Where the workspace's avatar library is managed. */
export const AVATAR_LIBRARY_HREF = '/settings/avatar-library';

/** What a card can open. `voice` and `remove` are an import's own actions; `publish` saves to the workspace library. */
export type CardAction = 'replace' | 'edit' | 'revoke' | 'delete' | 'details' | 'voice' | 'remove' | 'publish';

/**
 * One avatar of the project, a person or an object, with whether it can be used and, when not, why.
 * An import from the workspace library follows its library item: the card says so, and offers only its
 * details, the voice link (a person) and removing it from the project.
 * What belongs under the card (its variants as chips, "Add a variant") is passed as `children`.
 */
export function AvatarCard({ projectId, avatar, library, onOpen, children }: {
  projectId: string;
  avatar: ProjectAvatar;
  /** The workspace library, when the server has one; null hides everything about it. */
  library: AvatarLibraryRequirements | null;
  onOpen: (kind: CardAction) => void;
  children?: ReactNode;
}) {
  const t = useT('avatars');
  const f = useFormat();
  const words = useAvatarWords();
  // A variant shown as a card of its own (its original is not listed) still reads "Car · Red" where it can.
  const name = useAvatarName()(avatar);
  const variantDescription = (avatar.variantDescription ?? '').trim();
  const status = avatarStatus(avatar);
  const pictureId = primaryPictureFileId(avatar);
  const isObject = isObjectAvatar(avatar);
  const linked = isLinkedAvatar(avatar);
  // An object has no consent of a person: nothing to withdraw, so nothing is ever "revoked" on it.
  const revoked = !isObject && Boolean(avatar.consent.revokedAt);
  const counts = { images: 0, videos: 0, audio: 0 };
  for (const r of avatar.references) {
    if (r.kind === 'Image') counts.images += 1;
    else if (r.kind === 'Video') counts.videos += 1;
    else counts.audio += 1;
  }
  // An import is fixed in the workspace library, never here.
  const fixing = !linked && !revoked
    && (status === 'referenceMissing' || status === 'expired' || status === 'expiresSoon' || (isObject && status === 'consentMissing'));
  const notForPictures = !linked && !revoked && needsPictureConsent(avatar);
  const linkedReason = linked && avatar.unselectableReason && t.has(`library.linkedReasons.${avatar.unselectableReason}`)
    ? t(`library.linkedReasons.${avatar.unselectableReason}`)
    : null;

  return (
    <Card withBorder radius="md" padding="md" data-testid={`avatar-card-${avatar.id}`} data-kind={isObject ? 'object' : 'person'}>
      <Group align="flex-start" wrap="nowrap" gap="sm">
        <div style={{ width: 72, height: 72, flex: '0 0 72px', borderRadius: 8, overflow: 'hidden', background: 'var(--mantine-color-default-hover)', display: 'grid', placeItems: 'center' }}>
          {pictureId
            ? <CheckedImage src={projectFileUrl(projectId, pictureId)} alt={t('card.pictureAlt', { name })} loading="lazy" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
            : isObject ? <IconBox size={40} stroke={1.3} aria-hidden /> : <IconUserCircle size={40} stroke={1.3} aria-hidden />}
        </div>
        <Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
          <Group justify="space-between" wrap="nowrap" gap="xs" align="flex-start">
            <Text fw={650} truncate>{name}</Text>
            <Menu position="bottom-end" withinPortal>
              <Menu.Target>
                <ActionIcon variant="subtle" color="gray" size={44} aria-label={t('card.actions', { name })}>
                  <IconDotsVertical size={16} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item leftSection={<IconFileDescription size={15} />} onClick={() => onOpen('details')}>{t('card.details')}</Menu.Item>
                {linked
                  ? <LinkedMenuItems isObject={isObject} onOpen={onOpen} />
                  : <OwnMenuItems isObject={isObject} revoked={revoked} canPublish={canSaveToLibrary(avatar, library)} onOpen={onOpen} />}
              </Menu.Dropdown>
            </Menu>
          </Group>
          <Group gap={6}>
            <Badge color={isObject ? 'teal' : 'grape'} variant="outline" fz="xs" data-testid="avatar-kind">
              {isObject ? t('card.kindObject') : t('card.kindPerson')}
            </Badge>
            <Badge color={statusColor(status)} variant="light" fz="xs" data-testid="avatar-status">
              {words.statusLabel(avatar, status)}
            </Badge>
            {linked && <Badge color="blue" variant="light" fz="xs" data-testid="avatar-from-library">{t('library.card.badge')}</Badge>}
          </Group>
          {!avatar.selectable && avatar.unselectableReason && (
            <Text size="sm" c="dimmed">{linkedReason ?? words.reasonOf(avatar)}</Text>
          )}
        </Stack>
      </Group>
      {avatar.description && <Text size="sm" mt="xs" lineClamp={2}>{avatar.description}</Text>}
      {variantDescription && <Text size="sm" mt="xs" lineClamp={2} data-testid="avatar-variant-description">{t('card.variantDescription', { description: variantDescription })}</Text>}
      {isObject ? (
        <Stack gap={2} mt="xs">
          <Text size="sm" c="dimmed">{t('card.objectFiles', { images: counts.images, videos: counts.videos })}</Text>
          {hasFacts(avatar.identityFacts) && (
            <Text size="sm" c="dimmed" lineClamp={2} data-testid="avatar-facts">
              {FACT_FIELDS.filter((k) => avatar.identityFacts?.[k]).map((k) => `${t(`facts.fields.${k}`)}: ${avatar.identityFacts?.[k]}`).join(' · ')}
            </Text>
          )}
        </Stack>
      ) : (
        <Stack gap={2} mt="xs">
          <Text size="sm">
            {t('card.forSubject', { name: avatar.consent.subjectName })}
            {' · '}
            {avatar.consent.basis === 'Self' ? t('card.basisSelf') : t('card.basisWritten')}
          </Text>
          <Text size="sm" c="dimmed">{t('card.files', counts)}</Text>
          <Text size="sm" c="dimmed">
            {avatar.projectVoiceName
              ? (avatar.projectVoiceSelectable === false
                ? t('card.voiceUnavailable', { name: avatar.projectVoiceName })
                : t('card.voice', { name: avatar.projectVoiceName }))
              : t('card.noVoice')}
          </Text>
          {(revoked || avatar.retentionUntil) && (
            <Text size="sm" c="dimmed">
              {revoked
                ? t('card.revokedOn', { date: f.date(avatar.consent.revokedAt!) })
                : t('card.keepUntil', { date: f.date(avatar.retentionUntil!) })}
            </Text>
          )}
          {notForPictures && <Text size="sm" c="dimmed" data-testid="avatar-not-for-pictures">{t('card.notForPictures')}</Text>}
        </Stack>
      )}
      {linked && (
        <Text size="sm" c="dimmed" mt="xs" data-testid="avatar-library-managed">
          {t('library.card.managed')}
          {library?.canManage && (
            <>
              {' '}
              <Anchor component={Link} href={AVATAR_LIBRARY_HREF} size="sm">{t('library.card.openLibrary')}</Anchor>
            </>
          )}
        </Text>
      )}
      {(fixing || notForPictures) && (
        <Button mt="sm" variant="light" onClick={() => onOpen('replace')} style={{ minHeight: 44 }}>
          {status === 'referenceMissing' ? t('card.fixFiles') : isObject ? t('card.confirmObject') : t('card.renew')}
        </Button>
      )}
      {children}
    </Card>
  );
}

/** The menu of an avatar made in this project: edit it, change its files, save it to the library, withdraw, delete. */
export function OwnMenuItems({ isObject, revoked, canPublish, onOpen }: {
  isObject: boolean;
  revoked: boolean;
  /** An Owner or Admin may save a usable avatar to the workspace library. */
  canPublish: boolean;
  onOpen: (kind: CardAction) => void;
}) {
  const t = useT('avatars');
  return (
    <>
      {!revoked && <Menu.Item leftSection={<IconEdit size={15} />} onClick={() => onOpen('edit')}>{t('card.edit')}</Menu.Item>}
      {!revoked && <Menu.Item leftSection={<IconFiles size={15} />} onClick={() => onOpen('replace')}>{t('card.changeFiles')}</Menu.Item>}
      {canPublish && <Menu.Item leftSection={<IconBooks size={15} />} onClick={() => onOpen('publish')}>{t('library.card.save')}</Menu.Item>}
      {!isObject && !revoked && <Menu.Item color="orange" leftSection={<IconShieldOff size={15} />} onClick={() => onOpen('revoke')}>{t('card.revoke')}</Menu.Item>}
      <Menu.Item color="red" leftSection={<IconTrash size={15} />} onClick={() => onOpen('delete')}>{t('card.delete')}</Menu.Item>
    </>
  );
}

/** The menu of an import: its voice link (a person) and removing it from this project. Nothing else is set here. */
export function LinkedMenuItems({ isObject, onOpen }: { isObject: boolean; onOpen: (kind: CardAction) => void }) {
  const t = useT('avatars.library.card');
  return (
    <>
      {!isObject && <Menu.Item leftSection={<IconMicrophone size={15} />} onClick={() => onOpen('voice')}>{t('voice')}</Menu.Item>}
      <Menu.Item color="red" leftSection={<IconUnlink size={15} />} onClick={() => onOpen('remove')}>{t('remove')}</Menu.Item>
    </>
  );
}
