'use client';

import { useState } from 'react';
import { Alert, Badge, Button, Group, Loader, Modal, SegmentedControl, Stack, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { importLibraryAvatar } from '@/lib/api/avatar-library';
import { enabledKinds, kindAvailability } from '@/lib/avatars/model';
import {
  groupLibraryItems, importBlock, libraryDisplayName, libraryFileCounts, libraryItemStatus, libraryItemsOfKind, libraryStatusColor,
  type ImportBlock,
} from '@/lib/avatars/library';
import { useAvatarLibrary } from '@/lib/hooks/use-avatar-library';
import type { AvatarRequirements, AvatarSubjectKind } from '@/lib/types/avatars';
import type { AvatarLibraryItem } from '@/lib/types/avatar-library';
import { useT } from '@/i18n';
import { LibraryPicture } from './LibraryPicture';
import { useLibraryErrorMessage } from './library-errors';
import { useLibraryWords } from './library-words';

interface Props {
  projectId: string;
  requirements: AvatarRequirements;
  opened: boolean;
  /** How many avatars of each kind the project holds already: an import needs room, like a new one. */
  projectCounts: Record<AvatarSubjectKind, number>;
  /** The caller is an Owner or Admin: the empty state then says how to add an item. */
  canManage: boolean;
  onClose: () => void;
  /** Called after each import, so the tab and the Files tab show the new avatar and its copied files. */
  onImported: () => void | Promise<void>;
}

/**
 * The workspace library, to import from (docs/design/avatar-objects.md "The workspace avatar library"): the
 * people and objects the workspace keeps for every project, each with its picture, its status and one Import
 * button. An item that cannot be used is listed with the reason; one already in the project says so.
 * An import is a linked project avatar: it follows the library item. Variants are listed under their original
 * (`Car · Red`); one imported while its original is in the project lands under it there.
 */
export function LibraryPickerModal(props: Props) {
  // Nothing is asked while the picker is closed.
  if (!props.opened) return null;
  return <Picker {...props} />;
}

function Picker({ projectId, requirements, projectCounts, canManage, onClose, onImported }: Props) {
  const t = useT('avatars.library.picker');
  const tTab = useT('avatars.tab');
  const phone = Boolean(useMediaQuery('(max-width: 600px)'));
  const { items, error, isLoading, mutate } = useAvatarLibrary(projectId);
  const describe = useLibraryErrorMessage();
  const kinds = kindAvailability(requirements);
  const shownKinds = enabledKinds(requirements);
  const max = requirements.limits.maxAvatarsPerProject;
  const projectFull = { Person: projectCounts.Person >= max, Object: projectCounts.Object >= max };
  const people = libraryItemsOfKind(items ?? [], 'Person');
  const objects = libraryItemsOfKind(items ?? [], 'Object');
  const [picked, setPicked] = useState<AvatarSubjectKind | null>(null);
  // Until the person picks a side, the picker opens on people, or on objects when the library has only those.
  const automatic: AvatarSubjectKind = shownKinds.length === 1
    ? shownKinds[0]
    : (people.length === 0 && objects.length > 0 ? 'Object' : 'Person');
  const shown: AvatarSubjectKind = picked && shownKinds.includes(picked) ? picked : automatic;
  const list = shown === 'Object' ? objects : people;
  const offered = shownKinds.reduce((n, k) => n + (k === 'Object' ? objects.length : people.length), 0);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [failure, setFailure] = useState<{ itemId: string; message: string } | null>(null);

  const importItem = async (item: AvatarLibraryItem) => {
    if (busyId) return;
    const name = libraryDisplayName(item, items);
    setBusyId(item.id);
    setFailure(null);
    try {
      await importLibraryAvatar(projectId, item.id);
      notifications.show({ color: 'green', message: t('done', { name }) });
      await Promise.all([mutate(), onImported()]);
    } catch (e) {
      setFailure({ itemId: item.id, message: describe(e, t('failed', { name })) });
      // A refusal can mean the list is out of date (already imported, no longer usable): read it again.
      void mutate();
    } finally {
      setBusyId(null);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title')} size="lg" fullScreen={phone} radius="md">
      <Stack gap="sm" data-testid="library-picker">
        <Text size="sm" c="dimmed">{t('intro')}</Text>
        {shownKinds.length > 1 && offered > 0 && (
          <SegmentedControl
            value={shown} onChange={(v) => setPicked(v as AvatarSubjectKind)} aria-label={tTab('filterLabel')}
            data-testid="library-kind-filter" style={{ alignSelf: 'flex-start' }}
            data={[
              { value: 'Person', label: tTab('filterPeople', { count: people.length }) },
              { value: 'Object', label: tTab('filterObjects', { count: objects.length }) },
            ]}
          />
        )}
        {error && !items ? (
          <Alert color="red" variant="light">{t('loadFailed')}</Alert>
        ) : isLoading && !items ? (
          <Group justify="center" py="lg"><Loader size="sm" /></Group>
        ) : offered === 0 ? (
          <Stack gap={4} data-testid="library-picker-empty">
            <Text fw={650}>{t('emptyTitle')}</Text>
            <Text size="sm" c="dimmed">{canManage ? t('emptyBodyManager') : t('emptyBodyMember')}</Text>
          </Stack>
        ) : list.length === 0 ? (
          <Text size="sm" c="dimmed" data-testid="library-picker-empty-kind">{shown === 'Object' ? t('noObjects') : t('noPeople')}</Text>
        ) : (
          <Stack gap="xs">
            {groupLibraryItems(list).map(({ item, variants }) => (
              <Stack key={item.id} gap={6}>
                <PickerRow
                  item={item} max={max} block={importBlock(item, kinds, projectFull)}
                  busy={busyId === item.id} locked={busyId !== null}
                  error={failure?.itemId === item.id ? failure.message : null}
                  onImport={() => void importItem(item)}
                />
                {variants.length > 0 && (
                  <Stack
                    gap={6} pl="lg" role="list" aria-label={t('variantsOf', { name: item.name })}
                    data-testid={`library-variants-${item.id}`}
                  >
                    {variants.map((variant) => (
                      <div role="listitem" key={variant.id}>
                        <PickerRow
                          item={variant} name={libraryDisplayName(variant, list)} max={max}
                          block={importBlock(variant, kinds, projectFull, item.imported)}
                          busy={busyId === variant.id} locked={busyId !== null}
                          error={failure?.itemId === variant.id ? failure.message : null}
                          onImport={() => void importItem(variant)}
                        />
                      </div>
                    ))}
                  </Stack>
                )}
              </Stack>
            ))}
          </Stack>
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{t('close')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

function PickerRow({ item, name = item.name, block, max, busy, locked, error, onImport }: {
  item: AvatarLibraryItem;
  /** The name to show: `Car · Red` for a variant listed under its original. */
  name?: string;
  block: ImportBlock | null;
  max: number;
  busy: boolean;
  /** Another import is running: one at a time. */
  locked: boolean;
  error: string | null;
  onImport: () => void;
}) {
  const t = useT('avatars.library.picker');
  const tCard = useT('avatars.card');
  const words = useLibraryWords();
  const status = libraryItemStatus(item);
  const isObject = item.subjectKind === 'Object';
  const counts = libraryFileCounts(item);
  const why = block === 'unusable' ? words.reasonOf(item)
    : block === 'notInPlan' ? (isObject ? t('blocked.notInPlanObject') : t('blocked.notInPlanPerson'))
      : block === 'projectFull' ? (isObject ? t('blocked.projectFullObject', { max }) : t('blocked.projectFullPerson', { max }))
        : null;
  return (
    <Group
      wrap="nowrap" align="flex-start" gap="sm" p="xs" data-testid={`library-row-${item.id}`} data-kind={isObject ? 'object' : 'person'}
      style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 8 }}
    >
      <LibraryPicture item={{ ...item, name }} size={64} />
      <Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
        <Text fw={650} truncate>{name}</Text>
        <Group gap={6}>
          <Badge color={isObject ? 'teal' : 'grape'} variant="outline" fz="xs">{isObject ? tCard('kindObject') : tCard('kindPerson')}</Badge>
          <Badge color={libraryStatusColor(status)} variant="light" fz="xs" data-testid="library-status">{words.statusLabel(item, status)}</Badge>
          {block === 'imported' && <Badge color="blue" variant="light" fz="xs" data-testid="library-imported">{t('imported')}</Badge>}
        </Group>
        {item.variantDescription && <Text size="sm" lineClamp={2} data-testid="library-variant-description">{item.variantDescription}</Text>}
        {item.description && <Text size="sm" lineClamp={2}>{item.description}</Text>}
        <Text size="sm" c="dimmed">
          {isObject
            ? tCard('objectFiles', { images: counts.images, videos: counts.videos })
            : tCard('files', counts)}
        </Text>
        {why && <Text size="sm" c="dimmed" data-testid="library-blocked">{why}</Text>}
        {error && <Text size="sm" c="red" role="alert" data-testid="library-import-error">{error}</Text>}
      </Stack>
      {block !== 'imported' && (
        <Button
          variant="light" onClick={onImport} disabled={block !== null || (locked && !busy)} loading={busy}
          aria-label={t('importNamed', { name })} style={{ minHeight: 44, flex: '0 0 auto' }}
        >
          {t('import')}
        </Button>
      )}
    </Group>
  );
}
