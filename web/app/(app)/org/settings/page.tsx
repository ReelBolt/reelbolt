'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import {
  Alert,
  Badge,
  Button,
  Card,
  Center,
  Group,
  Loader,
  Radio,
  Stack,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconBuildingPlus, IconInfoCircle } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { CreateOrgModal } from '@/components/org/CreateOrgModal';
import { deleteOrg, updateOrg, type Org } from '@/lib/api/orgs';
import { useOrgs, useSwitchOrg } from '@/lib/hooks/use-orgs';
import { useRunners } from '@/lib/hooks/use-runners';
import { useAuth } from '@/lib/hooks/use-auth';
import { canManageOrg } from '@/lib/org-permissions';
import { runnerPresence } from '@/lib/utils/runners';
import {
  computePreferenceOptions,
  computePreferenceOrDefault,
  computePreferenceLabel,
  runnerAvailabilityNote,
} from '@/lib/utils/compute-preference';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { useRoleLabel } from '@/components/org/useRoleLabel';

/**
 * Workspace settings (A16-UI): rename and delete the active organization, and create a new Team
 * one. Everything here targets the ACTIVE workspace — the caller's authority in it comes from the
 * session cookie for display and from the database on every call, so a role change elsewhere
 * takes effect on the next request regardless of what this page believes.
 *
 * Role rules (mirrored from api/handlers/orgs/orgs.go, see lib/org-permissions):
 *   - Owner and Admin may rename. Everyone else sees the workspace read-only, because
 *     `PATCH /api/v1/orgs/{id}` answers 403 for a Member or Billing caller.
 *   - Only an Owner may delete, and only a Team workspace: the API refuses a Personal one
 *     ("a personal workspace cannot be deleted") and a non-Owner ("only an owner can delete").
 *   - A Personal workspace is never renamed from here. It is always displayed as "Personal"
 *     (lib/org-label.ts, decision D1), so a rename field would be a control with no visible
 *     effect. The API permits it; the UI deliberately does not offer it.
 */
export default function OrgSettingsPage() {
  const t = useT('org.settings');
  const tr = useT();
  const roleLabel = useRoleLabel();
  const router = useRouter();
  const { user, activeOrgId, isOrgAdmin } = useAuth();
  const { data: orgs, mutate: mutateOrgs } = useOrgs();
  const switchOrg = useSwitchOrg();

  // The name field is derived from the loaded org unless the caller has typed into it. A draft is
  // tagged with the org it belongs to, so switching workspaces discards it rather than carrying a
  // stale name across.
  const [draft, setDraft] = useState<{ orgId: string; name: string } | null>(null);
  const [savingName, setSavingName] = useState(false);
  const [createOpened, setCreateOpened] = useState(false);
  const [deleteOpened, setDeleteOpened] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [savingPreference, setSavingPreference] = useState(false);

  // The workspace's paired machines, so the compute choice is made with the
  // number of machines it would actually use in sight. Readable by every member
  // (GET /api/v1/runners gates on having a workspace, not on a role); when it
  // cannot be read, the line is simply absent rather than claiming zero.
  const { data: devices } = useRunners();

  const org = orgs?.find((candidate) => candidate.id === activeOrgId) ?? null;
  const role = org?.role ?? user?.orgRole ?? null;
  const manager = canManageOrg(role);
  const personal = org?.kind === 'Personal';
  const name = draft && draft.orgId === org?.id ? draft.name : org?.name ?? '';
  const preference = computePreferenceOrDefault(org?.computePreference);

  async function handleCreate(created: Org) {
    await mutateOrgs();
    notifications.show({ title: t('createdTitle'), message: t('createdMessage', { name: created.name }), color: 'green' });
    try {
      // Creating a workspace you do not enter is a dead end: switch into it, like the switcher.
      await switchOrg(created.id);
      router.push('/dashboard');
    } catch (err: unknown) {
      notifications.show({
        title: t('createdTitle'),
        message: err instanceof Error
          ? t('createdSwitchFailed', { name: created.name, reason: describeError(tr, err) })
          : t('createdSwitchManual', { name: created.name }),
        color: 'yellow',
      });
    }
  }

  async function handleRename() {
    if (!org || !manager || personal) return;
    const next = name.trim();
    if (!next || next === org.name) return;
    setSavingName(true);
    try {
      await updateOrg(org.id, { name: next });
      await mutateOrgs();
      setDraft(null);
      notifications.show({ title: t('savedTitle'), message: t('renamed'), color: 'green' });
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tr, err, t('renameFailed')),
        color: 'red',
      });
    } finally {
      setSavingName(false);
    }
  }

  /**
   * Save the workspace's compute preference (E8-UI).
   *
   * The value goes to `PATCH /api/v1/orgs/{id}`, which an Owner or Admin may
   * call and which refuses a value the engine does not know — a 400 here means
   * the choice never reached the database, so the radio snaps back to the stored
   * value and the notification carries the server's message.
   */
  async function handlePreferenceChange(next: string) {
    if (!org || !manager || next === preference) return;
    setSavingPreference(true);
    try {
      await updateOrg(org.id, { computePreference: next });
      await mutateOrgs();
      notifications.show({
        title: t('savedTitle'),
        message: t('preferenceSaved', { name: org.name, label: computePreferenceLabel(next, tr) }),
        color: 'green',
      });
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tr, err, t('preferenceFailed')),
        color: 'red',
      });
    } finally {
      setSavingPreference(false);
    }
  }

  async function handleDelete() {
    if (!org) return;
    setDeleting(true);
    try {
      await deleteOrg(org.id);
      const remaining = (orgs ?? []).filter((candidate) => candidate.id !== org.id);
      // The session still points at a workspace that no longer exists, so move to the Personal
      // one before returning to the dashboard (the list orders Personal first, decision D1).
      const fallback = remaining.find((candidate) => candidate.kind === 'Personal') ?? remaining[0];
      await mutateOrgs();
      if (fallback) {
        try {
          await switchOrg(fallback.id);
        } catch {
          // The workspace is gone either way; the next sign-in resolves a fresh active org.
        }
      }
      notifications.show({ title: t('deletedTitle'), message: t('deletedMessage', { name: org.name }), color: 'green' });
      router.push('/dashboard');
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tr, err, t('deleteFailed')),
        color: 'red',
      });
    } finally {
      setDeleting(false);
      setDeleteOpened(false);
    }
  }

  if (orgs === undefined) {
    return (
      <Center h={300}>
        <Loader />
      </Center>
    );
  }

  return (
    <>
      <PageHeader
        title={t('title')}
        breadcrumbs={[{ label: t('breadcrumbOrg') }, { label: t('breadcrumbSettings') }]}
      >
        <Button
          leftSection={<IconBuildingPlus size={16} />}
          variant="default"
          onClick={() => setCreateOpened(true)}
        >
          {t('newTeam')}
        </Button>
      </PageHeader>

      <Stack gap="lg">
        <Card withBorder padding="lg">
          <Group justify="space-between" align="center" wrap="wrap" gap="sm" mb="xs">
            <Title order={4}>{org ? org.name : t('workspaceFallback')}</Title>
            {org && (
              <Badge color={org.kind === 'Personal' ? 'gray' : 'violet'} variant="light">
                {org.kind === 'Personal' ? t('kind.Personal') : org.kind === 'Team' ? t('kind.Team') : t('kind.Platform')}
              </Badge>
            )}
          </Group>

          {!org ? (
            <Text size="sm" c="dimmed">
              {t('loading')}
            </Text>
          ) : personal ? (
            <Text size="sm" c="dimmed">
              {t('personalNote')}
            </Text>
          ) : manager ? (
            <Stack gap="sm">
              <TextInput
                label={t('nameLabel')}
                description={t('nameDescription')}
                value={name}
                maxLength={100}
                onChange={(event) => setDraft({ orgId: org.id, name: event.currentTarget.value })}
              />
              <Group>
                <Button
                  onClick={handleRename}
                  loading={savingName}
                  disabled={!name.trim() || name.trim() === org.name}
                >
                  {t('save')}
                </Button>
              </Group>
            </Stack>
          ) : (
            <Text size="sm" c="dimmed">
              {t('ownerOrAdminRename')}
            </Text>
          )}

          {org && !personal && (
            <Text size="sm" mt="md">
              <Link href="/org/members">{t('manageMembers')}</Link>
            </Text>
          )}
        </Card>

        {org && (
          <Card withBorder padding="lg">
            <Title order={4} mb="xs">
              {t('compute.title')}
            </Title>

            <Text size="sm" c="dimmed" mb="sm">
              {tr('libLabels.compute.scopeNote')}
            </Text>
            {/* Only when the runner list actually loaded: an unreadable list is
                not the same fact as "no machines are paired", and saying zero
                would be a claim about a workspace we could not read. */}
            {devices && (
              <Text size="sm" mb="md">
                {runnerAvailabilityNote({
                  paired: devices.length,
                  online: devices.filter((device) => runnerPresence(device.lastSeenAt) === 'online').length,
                }, tr)}{' '}
                <Link href="/runners">{t('compute.manageRunners')}</Link>
              </Text>
            )}
            {!devices && (
              <Text size="sm" mb="md">
                <Link href="/runners">{t('compute.seeMachines')}</Link>
              </Text>
            )}

            <Alert icon={<IconInfoCircle size={16} />} color="gray" variant="light" mb="sm">
              {tr('libLabels.compute.routingNote')}
            </Alert>

            {manager ? (
              <Radio.Group
                name="computePreference"
                value={preference}
                onChange={handlePreferenceChange}
                disabled={savingPreference}
              >
                <Stack gap="sm">
                  {computePreferenceOptions(tr).map((option) => (
                    <div key={option.value}>
                      <Radio value={option.value} label={option.label} description={option.description} />
                      {option.warning && (
                        <Text size="xs" c="var(--mantine-color-orange-text)" mt={4} ml={26}>
                          {option.warning}
                        </Text>
                      )}
                    </div>
                  ))}
                </Stack>
              </Radio.Group>
            ) : (
              <Text size="sm">
                {t('compute.readOnly', { label: computePreferenceLabel(preference, tr) })}
              </Text>
            )}
          </Card>
        )}

        {org && !personal && (
          <Card withBorder padding="lg">
            <Title order={4} mb="xs">
              {t('delete.title')}
            </Title>
            {role === 'Owner' ? (
              <>
                <Text size="sm" c="dimmed" mb="md">
                  {t('delete.body')}
                </Text>
                <Button color="red" variant="light" onClick={() => setDeleteOpened(true)}>
                  {t('delete.button')}
                </Button>
              </>
            ) : (
              <Text size="sm" c="dimmed">
                {t('delete.ownerOnly')}
              </Text>
            )}
          </Card>
        )}

        {!manager && !personal && (
          <Alert icon={<IconInfoCircle size={16} />} color="gray" variant="light">
            {isOrgAdmin
              ? t('readOnlyAdmin')
              : t('readOnlyRole', { role: role ? roleLabel(role) : t('roleFallback') })}
          </Alert>
        )}
      </Stack>

      <CreateOrgModal opened={createOpened} onClose={() => setCreateOpened(false)} onCreated={handleCreate} />

      <ConfirmModal
        opened={deleteOpened}
        onClose={() => setDeleteOpened(false)}
        onConfirm={handleDelete}
        title={t('deleteDialog.title')}
        message={t('deleteDialog.message', { name: org?.name ?? t('deleteDialog.thisWorkspace') })}
        confirmLabel={t('deleteDialog.confirm')}
        loading={deleting}
      />
    </>
  );
}
