'use client';

import { useCallback, useMemo, useState } from 'react';
import Link from 'next/link';
import { Alert, Badge, Button, Card, Group, Loader, SegmentedControl, SimpleGrid, Stack, Text } from '@mantine/core';
import { IconBooks, IconPlus } from '@tabler/icons-react';
import { libraryOf } from '@/lib/avatars/library';
import { avatarsOfKind, enabledKinds, isObjectAvatar, kindAvailability } from '@/lib/avatars/model';
import { addVariantBlock, countedAvatars, groupByOriginal } from '@/lib/avatars/variants';
import { useAvatarBuilds } from '@/lib/hooks/use-avatar-builds';
import { useAvatarRequirements, useProjectAvatars } from '@/lib/hooks/use-avatars';
import { useProjectFiles } from '@/lib/hooks/use-files';
import type { AvatarRequirements, AvatarSubjectKind, ProjectAvatar } from '@/lib/types/avatars';
import { useT } from '@/i18n';
import { AvatarCard, type CardAction } from './AvatarCard';
import { AvatarFormModal } from './AvatarFormModal';
import { AvatarDeleteModal, AvatarDetailsModal, AvatarEditModal, AvatarRevokeModal } from './AvatarDialogs';
import { AvatarNamesProvider } from './avatar-names';
import { MediaConsentRow } from './MediaConsentRow';
import { ObjectFormModal } from './ObjectFormModal';
import { LibraryPickerModal } from './library/LibraryPickerModal';
import { LinkedVoiceModal, RemoveImportModal } from './library/LinkedAvatarDialogs';
import { SaveToLibraryModal } from './library/SaveToLibraryModal';
import { AddVariantMenu } from './variants/AddVariantMenu';
import { AvatarBuildsPanel } from './variants/AvatarBuildsPanel';
import { VariantAiModal } from './variants/VariantAiModal';
import { VariantChips } from './variants/VariantChips';
import { variantTargetOf } from './variants/variant-target';

/**
 * Whether the Avatars tab exists for this project: only when the API answers the requirements
 * route with a kind switched on, people or objects. While both operator switches (`AVATARS_ENABLED`,
 * `AVATAR_OBJECTS_ENABLED`) are off every avatar route is 404, and the tab is not shown at all.
 */
export function useAvatarsAvailable(projectId: string): { available: boolean; requirements: AvatarRequirements | undefined } {
  const { data, error } = useAvatarRequirements(projectId);
  const available = Boolean(data) && !error && enabledKinds(data).length > 0;
  return { available, requirements: available ? data : undefined };
}

type Dialog =
  | { kind: 'create'; subject: AvatarSubjectKind }
  | { kind: 'library' }
  // A variant of `original`: from other files (the create form of its kind), or made by the picture model.
  | { kind: 'variantFiles'; original: ProjectAvatar }
  | { kind: 'variantAi'; original: ProjectAvatar }
  | { kind: CardAction; avatar: ProjectAvatar };

/**
 * The project's avatars (docs/design/avatars.md "Web", docs/design/avatar-objects.md): people, each a
 * consented likeness of one person, and objects (a product, a vehicle, a building: anything that is not a
 * person). Every card shows whether it can be used and, when not, why in plain words. A person's consent is
 * given only here, by someone ticking the statement the server serves; an object needs one tick of its own
 * statement and no consent of a person.
 * Each kind has its own operator switch and its own plan feature, so the tab works with either or both.
 * With a workspace library (docs/design/avatar-objects.md "The workspace avatar library") the tab also imports
 * from it, and an Owner or Admin saves a project avatar to it; a server without one shows none of that.
 * An avatar can have variants (docs/design/avatar-objects.md "Avatar variants": the same car in another colour):
 * each is a complete avatar of its own, shown as a chip under its original, and "Add a variant" makes one from
 * other files or, for an object, with the picture model. Builds in progress are listed at the top. A server from
 * before variants shows none of that either.
 */
export function ProjectAvatarsTab({ projectId, requirements }: { projectId: string; requirements: AvatarRequirements }) {
  const t = useT('avatars.tab');
  const { data: avatars, error, isLoading, mutate } = useProjectAvatars(projectId);
  const { data: files, mutate: mutateFiles } = useProjectFiles(projectId);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const close = useCallback(() => setDialog(null), []);
  // Deleting or changing an avatar can delete reference files, so the Files tab is refreshed too.
  const refresh = useCallback(async () => { await Promise.all([mutate(), mutateFiles()]); }, [mutate, mutateFiles]);

  const kinds = kindAvailability(requirements);
  const library = libraryOf(requirements);
  const shownKinds = enabledKinds(requirements);
  const people = avatarsOfKind(avatars, 'Person');
  const objects = avatarsOfKind(avatars, 'Object');
  // A variant sits under its original and does not count toward the per-kind limit of the project.
  const counted = { Person: countedAvatars(people).length, Object: countedAvatars(objects).length };
  const builds = useAvatarBuilds(projectId);
  // Until the person picks a side, the tab opens on people, or on objects when the project has only those.
  const [picked, setPicked] = useState<AvatarSubjectKind | null>(null);
  const automatic: AvatarSubjectKind = shownKinds.length === 1
    ? shownKinds[0]
    : (people.length === 0 && objects.length > 0 ? 'Object' : 'Person');
  const shown: AvatarSubjectKind = picked && shownKinds.includes(picked) ? picked : automatic;
  const showingObjects = shown === 'Object';
  const cards = { Person: groupByOriginal(people), Object: groupByOriginal(objects) };
  const groups = cards[shown];
  const max = requirements.limits.maxAvatarsPerProject;
  // People and objects are counted apart: each kind has its own room.
  const atLimit = { Person: counted.Person >= max, Object: counted.Object >= max };
  const entitled = kinds[shown].entitled;
  const fileName = useMemo(() => {
    const names = new Map((files ?? []).map((f) => [f.id, f.originalFileName]));
    return (id: string) => names.get(id) ?? null;
  }, [files]);

  const kindOf = (a: ProjectAvatar): AvatarSubjectKind => (isObjectAvatar(a) ? 'Object' : 'Person');
  const formKind: AvatarSubjectKind | null = dialog?.kind === 'create'
    ? dialog.subject
    : dialog?.kind === 'replace' ? kindOf(dialog.avatar)
      : dialog?.kind === 'variantFiles' ? kindOf(dialog.original) : null;
  const formAvatar = dialog?.kind === 'replace' ? dialog.avatar : null;
  const formVariant = dialog?.kind === 'variantFiles' ? variantTargetOf(dialog.original, avatars) : null;
  const aiVariant = dialog?.kind === 'variantAi' ? variantTargetOf(dialog.original, avatars) : null;
  const avatarFor = (kind: CardAction): ProjectAvatar | null => (dialog?.kind === kind && 'avatar' in dialog ? dialog.avatar : null);
  const saved = async () => { close(); await refresh(); };
  const { refresh: refreshBuilds } = builds;
  const buildStarted = async () => { close(); await Promise.resolve(refreshBuilds()).catch(() => undefined); };

  return (
    <AvatarNamesProvider avatars={avatars}>
    <Stack gap="md" data-testid="avatars-tab">
      <Group justify="space-between" align="flex-start" wrap="wrap" gap="sm">
        <div style={{ flex: '1 1 280px', minWidth: 0 }}>
          <Group gap="xs" wrap="nowrap">
            <Text fw={650}>{t('title')}</Text>
            {!entitled && (showingObjects
              ? <Badge variant="light" color="violet" fz="xs" data-testid="avatars-objects-plan-badge">{t('objectsPlanBadge')}</Badge>
              : <Badge variant="light" color="violet" fz="xs" data-testid="avatars-pro-badge">{t('proBadge')}</Badge>)}
          </Group>
          <Text size="sm" c="dimmed" data-testid="avatars-intro">
            {shownKinds.length > 1 ? t('introBoth') : showingObjects ? t('introObjects') : t('intro')}
          </Text>
        </div>
        <Group gap="xs" wrap="wrap">
          {kinds.Person.enabled && kinds.Person.entitled && (
            <Button
              leftSection={<IconPlus size={16} />} onClick={() => setDialog({ kind: 'create', subject: 'Person' })}
              disabled={atLimit.Person} style={{ minHeight: 44 }}
            >
              {t('create')}
            </Button>
          )}
          {kinds.Object.enabled && kinds.Object.entitled && (
            <Button
              leftSection={<IconPlus size={16} />} onClick={() => setDialog({ kind: 'create', subject: 'Object' })}
              disabled={atLimit.Object} style={{ minHeight: 44 }} variant={kinds.Person.enabled && kinds.Person.entitled ? 'light' : 'filled'}
            >
              {t('createObject')}
            </Button>
          )}
          {library && (
            <Button
              leftSection={<IconBooks size={16} />} onClick={() => setDialog({ kind: 'library' })} variant="default"
              style={{ minHeight: 44 }} data-testid="avatars-from-library"
            >
              {t('fromLibrary')}
            </Button>
          )}
        </Group>
      </Group>

      {shownKinds.length > 1 && (
        <SegmentedControl
          value={shown} onChange={(v) => setPicked(v as AvatarSubjectKind)} aria-label={t('filterLabel')}
          data-testid="avatars-kind-filter" style={{ alignSelf: 'flex-start' }}
          data={[
            { value: 'Person', label: t('filterPeople', { count: cards.Person.length }) },
            { value: 'Object', label: t('filterObjects', { count: cards.Object.length }) },
          ]}
        />
      )}

      {!entitled && (showingObjects ? (
        <Alert color="violet" variant="light" title={t('objectsUpgradeTitle')} data-testid="avatars-objects-upgrade">
          <Stack gap="xs" align="flex-start">
            <Text size="sm">{t('objectsUpgradeBody')}</Text>
            <Button component={Link} href="/settings/billing" variant="light" style={{ minHeight: 44 }}>{t('upgradeLink')}</Button>
          </Stack>
        </Alert>
      ) : (
        <Alert color="violet" variant="light" title={t('upgradeTitle')} data-testid="avatars-upgrade">
          <Stack gap="xs" align="flex-start">
            <Text size="sm">{t('upgradeBody')}</Text>
            <Button component={Link} href="/settings/billing" variant="light" style={{ minHeight: 44 }}>{t('upgradeLink')}</Button>
          </Stack>
        </Alert>
      ))}
      {entitled && atLimit[shown] && (
        <Text size="sm" c="dimmed" data-testid="avatars-limit">
          {showingObjects ? t('objectLimitReached', { max }) : t('limitReached', { max })}
        </Text>
      )}

      <AvatarBuildsPanel projectId={projectId} state={builds} onReady={refresh} />

      {error ? (
        <Alert color="red" variant="light">{t('loadFailed')}</Alert>
      ) : isLoading && !avatars ? (
        <Group justify="center" py="lg"><Loader size="sm" /></Group>
      ) : groups.length === 0 ? (
        <Card withBorder radius="md" padding="lg" data-testid="avatars-empty">
          <Stack gap={4}>
            <Text fw={650}>{showingObjects ? t('objectsEmptyTitle') : t('emptyTitle')}</Text>
            <Text size="sm" c="dimmed">{showingObjects ? t('objectsEmptyBody') : t('emptyBody')}</Text>
          </Stack>
        </Card>
      ) : (
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="md">
          {groups.map(({ avatar: a, variants }) => (
            <AvatarCard
              key={a.id} projectId={projectId} avatar={a} library={library}
              onOpen={(kind) => setDialog({ kind, avatar: a })}
            >
              <VariantChips original={a} variants={variants} library={library} onOpen={(variant, kind) => setDialog({ kind, avatar: variant })} />
              <AddVariantMenu
                avatar={a} block={addVariantBlock(a, avatars, kinds)}
                onAdd={(mode) => setDialog({ kind: mode === 'ai' ? 'variantAi' : 'variantFiles', original: a })}
              />
            </AvatarCard>
          ))}
        </SimpleGrid>
      )}

      <AvatarFormModal
        projectId={projectId} requirements={requirements}
        opened={formKind === 'Person'} avatar={formKind === 'Person' ? formAvatar : null}
        variant={formKind === 'Person' ? formVariant : null}
        onClose={close} onSaved={saved}
      />
      <ObjectFormModal
        projectId={projectId} requirements={requirements}
        opened={formKind === 'Object'} avatar={formKind === 'Object' ? formAvatar : null}
        variant={formKind === 'Object' ? formVariant : null}
        onClose={close} onSaved={saved}
      />
      <AvatarEditModal
        projectId={projectId} requirements={requirements} avatar={avatarFor('edit')}
        onClose={close} onSaved={saved}
      />
      <AvatarRevokeModal
        projectId={projectId} requirements={requirements} avatar={avatarFor('revoke')}
        onClose={close} onDone={refresh} fileName={fileName}
      />
      <AvatarDeleteModal
        projectId={projectId} avatar={avatarFor('delete')}
        onClose={close} onDone={refresh} fileName={fileName}
      />
      <AvatarDetailsModal projectId={projectId} avatar={avatarFor('details')} onClose={close} />
      <VariantAiModal projectId={projectId} requirements={requirements} target={aiVariant} onClose={close} onStarted={buildStarted} />
      <LinkedVoiceModal projectId={projectId} avatar={avatarFor('voice')} onClose={close} onSaved={saved} />
      <RemoveImportModal projectId={projectId} avatar={avatarFor('remove')} onClose={close} onDone={refresh} />
      {library && (
        <>
          <LibraryPickerModal
            projectId={projectId} requirements={requirements} opened={dialog?.kind === 'library'}
            projectCounts={counted} canManage={library.canManage}
            onClose={close} onImported={refresh}
          />
          <SaveToLibraryModal
            projectId={projectId} library={library} requirements={requirements} avatar={avatarFor('publish')}
            onClose={close} onSaved={saved}
          />
        </>
      )}

      <MediaConsentRow projectId={projectId} initial={requirements.mediaConsent} />
    </Stack>
    </AvatarNamesProvider>
  );
}
