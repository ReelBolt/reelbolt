'use client';

import { useCallback, useState } from 'react';
import { ActionIcon, Alert, Badge, Card, Group, Loader, Menu, SimpleGrid, Stack, Text } from '@mantine/core';
import { IconDotsVertical, IconEdit, IconShieldCheck, IconShieldOff, IconTrash } from '@tabler/icons-react';
import {
  groupLibraryItems, libraryDisplayName, libraryFileCounts, libraryItemStatus, libraryLimits, libraryOf, libraryStatusColor,
} from '@/lib/avatars/library';
import { useAuth } from '@/lib/hooks/use-auth';
import { useAvatarLibrary } from '@/lib/hooks/use-avatar-library';
import { useAvatarRequirements } from '@/lib/hooks/use-avatars';
import { useProjects } from '@/lib/hooks/use-projects';
import type { AvatarLibraryRequirements } from '@/lib/types/avatars';
import type { AvatarLibraryItem } from '@/lib/types/avatar-library';
import { useFormat, useT } from '@/i18n';
import { LibraryDeleteModal, LibraryReconfirmModal, LibraryRenameModal, LibraryRevokeModal } from './LibraryItemDialogs';
import { LibraryPicture } from './LibraryPicture';
import { useLibraryWords } from './library-words';

type ItemAction = 'rename' | 'reconfirm' | 'revoke' | 'delete';

/**
 * What the manager's dialogs need besides the items: the workspace statements, the limits and the statement
 * languages. The list answer carries them when the server sends an envelope; otherwise they are read from the
 * requirements of one of the workspace's projects (the same for every project of a workspace). Asked only when
 * the caller manages something (`wanted`).
 */
function useLibrarySource(listed: AvatarLibraryRequirements | null, wanted: boolean) {
  const ask = wanted && !listed;
  const { data: projects } = useProjects(ask);
  const projectId = ask ? projects?.[0]?.id ?? null : null;
  const { data: requirements } = useAvatarRequirements(projectId);
  return {
    library: listed ?? libraryOf(requirements),
    limits: libraryLimits(requirements?.limits),
    locales: requirements?.locales,
  };
}

/**
 * The workspace's avatar library (docs/design/avatar-objects.md "The workspace avatar library"): every person and
 * object the workspace keeps for all its projects, with its status and how many projects use it. Every member
 * may read it; an Owner or Admin may rename an item, confirm it again, withdraw a person's consent and delete it.
 * The server re-checks the role on every call, so the menus only avoid actions that could not work.
 */
export function LibraryManager() {
  const t = useT('avatars.library.page');
  const { isOrgAdmin } = useAuth();
  const { items, library: listed, error, isLoading, mutate, off } = useAvatarLibrary();
  const manages = (items ?? []).some((i) => i.canManage === true);
  const source = useLibrarySource(listed, manages);
  // Who the page talks to: a manager is told how to add and change items, a member where they come from.
  const manager = manages || listed?.canManage === true || isOrgAdmin;
  const [dialog, setDialog] = useState<{ kind: ItemAction; item: AvatarLibraryItem } | null>(null);
  const close = useCallback(() => setDialog(null), []);
  const refresh = useCallback(async () => { await mutate(); }, [mutate]);
  const itemFor = (kind: ItemAction): AvatarLibraryItem | null => (dialog?.kind === kind ? dialog.item : null);

  if (off) {
    return (
      <Card withBorder radius="md" padding="lg" data-testid="avatar-library-off">
        <Stack gap={4}>
          <Text fw={650}>{t('offTitle')}</Text>
          <Text size="sm" c="dimmed">{t('offBody')}</Text>
        </Stack>
      </Card>
    );
  }

  return (
    <Stack gap="md" data-testid="avatar-library">
      <Text size="sm" c="dimmed" data-testid="avatar-library-intro">{manager ? t('introManager') : t('introMember')}</Text>
      {error && !items ? (
        <Alert color="red" variant="light">{t('loadFailed')}</Alert>
      ) : isLoading && !items ? (
        <Group justify="center" py="lg"><Loader size="sm" /></Group>
      ) : !items || items.length === 0 ? (
        <Card withBorder radius="md" padding="lg" data-testid="avatar-library-empty">
          <Stack gap={4}>
            <Text fw={650}>{t('emptyTitle')}</Text>
            <Text size="sm" c="dimmed">{manager ? t('emptyBodyManager') : t('emptyBodyMember')}</Text>
          </Stack>
        </Card>
      ) : (
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="md">
          {groupLibraryItems(items).map((group) => (
            <LibraryItemCard
              key={group.item.id} item={group.item} variants={group.variants}
              onOpen={(kind, target) => setDialog({ kind, item: target })}
            />
          ))}
        </SimpleGrid>
      )}

      <LibraryRenameModal item={itemFor('rename')} limits={source.limits} onClose={close} onDone={refresh} />
      <LibraryReconfirmModal
        item={itemFor('reconfirm')} library={source.library} limits={source.limits} locales={source.locales}
        onClose={close} onDone={refresh}
      />
      <LibraryRevokeModal item={itemFor('revoke')} graceDays={source.limits.revokedGraceDays} onClose={close} onDone={refresh} />
      <LibraryDeleteModal item={itemFor('delete')} onClose={close} onDone={refresh} />
    </Stack>
  );
}

/**
 * One entry of the library: an item and, under it, its variants (docs/design/avatar-objects.md "Avatar variants"),
 * each a labelled row named `Car · Red` with its own status and the same actions as any item.
 */
function LibraryItemCard({ item, variants, onOpen }: {
  item: AvatarLibraryItem;
  variants: readonly AvatarLibraryItem[];
  onOpen: (kind: ItemAction, target: AvatarLibraryItem) => void;
}) {
  const t = useT('avatars.library.page');
  const tCard = useT('avatars.card');
  const f = useFormat();
  const words = useLibraryWords();
  const status = libraryItemStatus(item);
  const isObject = item.subjectKind === 'Object';
  const counts = libraryFileCounts(item);
  const used = typeof item.importCount === 'number' && item.importCount > 0 ? item.importCount : 0;
  return (
    <Card withBorder radius="md" padding="md" data-testid={`library-item-${item.id}`} data-kind={isObject ? 'object' : 'person'}>
      <Group align="flex-start" wrap="nowrap" gap="sm">
        <LibraryPicture item={item} />
        <Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
          <Group justify="space-between" wrap="nowrap" gap="xs" align="flex-start">
            <Text fw={650} truncate>{item.name}</Text>
            {item.canManage === true && <ItemMenu item={item} withdrawn={status === 'withdrawn'} onOpen={(kind) => onOpen(kind, item)} />}
          </Group>
          <Group gap={6}>
            <Badge color={isObject ? 'teal' : 'grape'} variant="outline" fz="xs" data-testid="library-kind">
              {isObject ? tCard('kindObject') : tCard('kindPerson')}
            </Badge>
            <Badge color={libraryStatusColor(status)} variant="light" fz="xs" data-testid="library-status">
              {words.statusLabel(item, status)}
            </Badge>
          </Group>
          {!item.usable && <Text size="sm" c="dimmed">{words.reasonOf(item)}</Text>}
        </Stack>
      </Group>
      {item.description && <Text size="sm" mt="xs" lineClamp={2}>{item.description}</Text>}
      <Stack gap={2} mt="xs">
        <Text size="sm" c="dimmed">
          {isObject ? tCard('objectFiles', { images: counts.images, videos: counts.videos }) : tCard('files', counts)}
        </Text>
        {!isObject && item.retentionUntil && status !== 'withdrawn' && (
          <Text size="sm" c="dimmed">{tCard('keepUntil', { date: f.date(item.retentionUntil) })}</Text>
        )}
        <Text size="sm" c="dimmed" data-testid="library-used">{used > 0 ? t('usedIn', { count: used }) : t('notUsed')}</Text>
      </Stack>
      <LibraryVariantRows original={item} variants={variants} onOpen={onOpen} />
    </Card>
  );
}

/** The variants of one library item, as labelled rows under its card. Nothing when it has none. */
function LibraryVariantRows({ original, variants, onOpen }: {
  original: AvatarLibraryItem;
  variants: readonly AvatarLibraryItem[];
  onOpen: (kind: ItemAction, target: AvatarLibraryItem) => void;
}) {
  const t = useT('avatars.library.page');
  const tVar = useT('avatars.library.variants');
  const words = useLibraryWords();
  if (variants.length === 0) return null;
  const all = [original, ...variants];
  return (
    <Stack gap={6} mt="sm" data-testid={`library-variants-${original.id}`}>
      <Text size="sm" c="dimmed" id={`library-variants-of-${original.id}`}>{tVar('title', { count: variants.length })}</Text>
      <Stack gap={6} role="list" aria-labelledby={`library-variants-of-${original.id}`}>
        {variants.map((variant) => {
          const status = libraryItemStatus(variant);
          const used = typeof variant.importCount === 'number' && variant.importCount > 0 ? variant.importCount : 0;
          const name = libraryDisplayName(variant, all);
          const description = (variant.variantDescription ?? '').trim();
          return (
            <Group
              key={variant.id} role="listitem" wrap="nowrap" align="flex-start" gap="sm" p="xs"
              data-testid={`library-variant-${variant.id}`}
              style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8 }}
            >
              <LibraryPicture item={{ ...variant, name }} size={48} />
              <Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
                <Group justify="space-between" wrap="nowrap" gap="xs" align="flex-start">
                  <Text fw={600} size="sm" truncate>{name}</Text>
                  {variant.canManage === true && (
                    <ItemMenu item={{ ...variant, name }} withdrawn={status === 'withdrawn'} onOpen={(kind) => onOpen(kind, variant)} />
                  )}
                </Group>
                <Group gap={6}>
                  <Badge color={libraryStatusColor(status)} variant="light" fz="xs" data-testid="library-status">
                    {words.statusLabel(variant, status)}
                  </Badge>
                </Group>
                {!variant.usable && <Text size="sm" c="dimmed">{words.reasonOf(variant)}</Text>}
                {description && <Text size="sm" lineClamp={2}>{description}</Text>}
                <Text size="sm" c="dimmed">{used > 0 ? t('usedIn', { count: used }) : t('notUsed')}</Text>
              </Stack>
            </Group>
          );
        })}
      </Stack>
    </Stack>
  );
}

/** What an Owner or Admin can do with an item. A withdrawn consent cannot be confirmed again or withdrawn twice. */
function ItemMenu({ item, withdrawn, onOpen }: { item: AvatarLibraryItem; withdrawn: boolean; onOpen: (kind: ItemAction) => void }) {
  const t = useT('avatars.library.page.menu');
  const tCard = useT('avatars.card');
  const isObject = item.subjectKind === 'Object';
  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <ActionIcon variant="subtle" color="gray" size={44} aria-label={tCard('actions', { name: item.name })}>
          <IconDotsVertical size={16} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconEdit size={15} />} onClick={() => onOpen('rename')}>{t('rename')}</Menu.Item>
        {!withdrawn && <Menu.Item leftSection={<IconShieldCheck size={15} />} onClick={() => onOpen('reconfirm')}>{t('reconfirm')}</Menu.Item>}
        {!isObject && !withdrawn && <Menu.Item color="orange" leftSection={<IconShieldOff size={15} />} onClick={() => onOpen('revoke')}>{t('revoke')}</Menu.Item>}
        <Menu.Item color="red" leftSection={<IconTrash size={15} />} onClick={() => onOpen('delete')}>{t('delete')}</Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}
