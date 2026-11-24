'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { Alert, Button, Card, Center, Loader, Stack, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconInfoCircle } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { EmptyState } from '@/components/shared/EmptyState';
import { CreateOrgModal } from '@/components/org/CreateOrgModal';
import { MembersTable } from '@/components/org/MembersTable';
import { InviteList } from '@/components/org/InviteList';
import { InviteForm } from '@/components/org/InviteForm';
import { createInvite, removeMember, revokeInvite, updateMember, type Org, type OrgInvite, type OrgMember } from '@/lib/api/orgs';
import { useOrgInvites, useOrgMembers, useOrgs, useSwitchOrg } from '@/lib/hooks/use-orgs';
import { useAuth } from '@/lib/hooks/use-auth';
import { canManageOrg, canViewMembers } from '@/lib/org-permissions';
import { orgDisplayLabel } from '@/lib/org-label';
import type { OrgRole } from '@/lib/types/auth';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { useRoleLabel } from '@/components/org/useRoleLabel';

/**
 * Workspace members and invitations (A16-UI), for the ACTIVE workspace.
 *
 * Access follows the API rather than a hidden page:
 *   - Owner/Admin: full management — role changes, removal, invitations, revocation.
 *   - Member: the same member list, read-only, plus leaving the workspace. The API allows a
 *     Member to list members and to remove themselves, and nothing else.
 *   - Billing: nothing. `GET /api/v1/orgs/{id}/members` is 403 for that role, so the page
 *     explains the role instead of firing a request it knows will fail.
 *   - Personal: hides the member and invite controls entirely — a personal workspace has one
 *     owner, cannot be invited into, and cannot be left.
 */
export default function OrgMembersPage() {
  const tr = useT();
  const t = useT('org.members');
  const roleLabel = useRoleLabel();
  const router = useRouter();
  const { user, activeOrgId } = useAuth();
  const { data: orgs, mutate: mutateOrgs } = useOrgs();
  const switchOrg = useSwitchOrg();

  const org = orgs?.find((candidate) => candidate.id === activeOrgId) ?? null;
  const role = org?.role ?? user?.orgRole ?? null;
  const personal = org?.kind === 'Personal';
  const manager = canManageOrg(role) && !personal;
  const mayView = canViewMembers(role) && !personal;

  const { data: members, mutate: mutateMembers } = useOrgMembers(mayView ? activeOrgId : null);
  const { data: invites, mutate: mutateInvites } = useOrgInvites(manager ? activeOrgId : null);

  const [busyUserId, setBusyUserId] = useState<string | null>(null);
  const [busyInviteId, setBusyInviteId] = useState<string | null>(null);
  const [resendingId, setResendingId] = useState<string | null>(null);
  const [removeTarget, setRemoveTarget] = useState<OrgMember | null>(null);
  const [revokeTarget, setRevokeTarget] = useState<OrgInvite | null>(null);
  const [createOpened, setCreateOpened] = useState(false);

  const isSelf = (member: OrgMember) =>
    !!user?.email && member.email.toLowerCase() === user.email.toLowerCase();

  async function handleRoleChange(member: OrgMember, nextRole: OrgRole) {
    if (!activeOrgId) return;
    setBusyUserId(member.userId);
    try {
      await updateMember(activeOrgId, member.userId, { role: nextRole });
      await mutateMembers();
      // Changing your own role makes the readable session cookie stale — the API only re-reads
      // the real role from the database — so re-issue the session to keep the nav honest.
      if (isSelf(member)) await switchOrg(activeOrgId);
      notifications.show({ title: t('savedTitle'), message: t('roleChanged', { email: member.email, role: roleLabel(nextRole) }), color: 'green' });
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tr, err, t('roleChangeFailed')),
        color: 'red',
      });
    } finally {
      setBusyUserId(null);
    }
  }

  async function handleRemove() {
    const member = removeTarget;
    if (!activeOrgId || !member) return;
    setBusyUserId(member.userId);
    try {
      await removeMember(activeOrgId, member.userId);
      setRemoveTarget(null);
      if (isSelf(member)) {
        // We just left the active workspace: move to the Personal one before the dashboard.
        const fallback =
          (orgs ?? []).find((candidate) => candidate.kind === 'Personal' && candidate.id !== activeOrgId) ??
          (orgs ?? []).find((candidate) => candidate.id !== activeOrgId);
        await mutateMembers();
        if (fallback) {
          try {
            await switchOrg(fallback.id);
          } catch {
            // Left either way; the next sign-in resolves a fresh active workspace.
          }
        }
        notifications.show({ title: t('leftTitle'), message: t('leftMessage', { name: org?.name ?? t('theWorkspace') }), color: 'green' });
        router.push('/dashboard');
        return;
      }
      await mutateMembers();
      notifications.show({ title: t('removedTitle'), message: t('removedMessage', { email: member.email }), color: 'green' });
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tr, err, t('removeFailed')),
        color: 'red',
      });
    } finally {
      setBusyUserId(null);
    }
  }

  // The API never returns an invite token, so there is no link to copy. Inviting the same address
  // again is the supported "resend": the server revokes the earlier pending invite in the same
  // transaction and emails a fresh link.
  async function handleResend(invite: OrgInvite) {
    if (!activeOrgId) return;
    setResendingId(invite.id);
    try {
      const sent = await createInvite(activeOrgId, { email: invite.email, role: invite.role });
      await mutateInvites();
      notifications.show(
        sent.emailSent === false
          ? { title: t('notSentTitle'), message: t('notSentMessage', { email: invite.email }), color: 'yellow' }
          : { title: t('sentTitle'), message: t('sentMessage', { email: invite.email }), color: 'green' },
      );
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: t('resendFailed', { reason: describeError(tr, err, t('resendFailedReason')) }),
        color: 'red',
      });
    } finally {
      setResendingId(null);
    }
  }

  async function handleCreateTeam(created: Org) {
    await mutateOrgs();
    notifications.show({ title: t('createdTitle'), message: t('createdMessage', { name: created.name }), color: 'green' });
    try {
      await switchOrg(created.id);
    } catch {
      // Created either way; the workspace menu can switch to it.
    }
    router.refresh();
  }

  async function handleRevoke() {
    const invite = revokeTarget;
    if (!activeOrgId || !invite) return;
    setBusyInviteId(invite.id);
    try {
      await revokeInvite(activeOrgId, invite.id);
      await mutateInvites();
      notifications.show({ title: t('revokedTitle'), message: t('revokedMessage', { email: invite.email }), color: 'green' });
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: describeError(tr, err, t('revokeFailed')),
        color: 'red',
      });
    } finally {
      setBusyInviteId(null);
      setRevokeTarget(null);
    }
  }

  return (
    <>
      <PageHeader
        title={t('title')}
        breadcrumbs={[
          // The active workspace, so a page that manages members says which members. The Personal
          // workspace is labelled "Personal" here too (lib/org-label.ts, decision D1).
          { label: org ? orgDisplayLabel(org, tr) : t('breadcrumbOrg'), href: '/org/settings' },
          { label: t('title') },
        ]}
      />

      {personal ? (
        <EmptyState
          title={t('personal.title')}
          description={t('personal.description')}
        >
          <Button onClick={() => setCreateOpened(true)} style={{ minHeight: 44 }}>
            {t('personal.create')}
          </Button>
          <Link href="/org/settings">{t('personal.settings')}</Link>
        </EmptyState>
      ) : !mayView ? (
        <Alert
          icon={<IconInfoCircle size={16} />}
          color="gray"
          variant="light"
          title={role === 'Billing' ? t('hidden.billingTitle') : t('hidden.unavailableTitle')}
        >
          {role === 'Billing'
            ? t('hidden.billingBody')
            : t('hidden.noRoleBody')}
        </Alert>
      ) : (
        <Stack gap="lg">
          <Card withBorder padding="lg">
            <Title order={4} mb="xs">
              {t('people.title')}
            </Title>
            {members === undefined ? (
              <Center h={120}>
                <Loader size="sm" />
              </Center>
            ) : members.length === 0 ? (
              <Text size="sm" c="dimmed">
                {t('people.empty')}
              </Text>
            ) : (
              <MembersTable
                members={members}
                callerRole={role}
                callerEmail={user?.email ?? null}
                busyUserId={busyUserId}
                onRoleChange={handleRoleChange}
                onRemove={setRemoveTarget}
              />
            )}
          </Card>

          {manager && (
            <>
              <Card withBorder padding="lg">
                <Title order={4} mb="xs">
                  {t('invite.title')}
                </Title>
                <InviteForm orgId={activeOrgId ?? ''} callerRole={role} onCreated={() => void mutateInvites()} />
              </Card>

              <Card withBorder padding="lg">
                <Title order={4} mb="xs">
                  {t('pending.title')}
                </Title>
                {invites === undefined ? (
                  <Center h={100}>
                    <Loader size="sm" />
                  </Center>
                ) : invites.length === 0 ? (
                  <Text size="sm" c="dimmed">
                    {t('pending.empty')}
                  </Text>
                ) : (
                  <InviteList invites={invites} busyId={busyInviteId} resendingId={resendingId} onRevoke={setRevokeTarget} onResend={handleResend} />
                )}
              </Card>
            </>
          )}

          {!manager && (
            <Alert icon={<IconInfoCircle size={16} />} color="gray" variant="light">
              {t('readOnly')}
            </Alert>
          )}
        </Stack>
      )}

      <CreateOrgModal opened={createOpened} onClose={() => setCreateOpened(false)} onCreated={handleCreateTeam} />

      <ConfirmModal
        opened={!!removeTarget}
        onClose={() => setRemoveTarget(null)}
        onConfirm={handleRemove}
        title={removeTarget && isSelf(removeTarget) ? t('removeDialog.leaveTitle') : t('removeDialog.removeTitle')}
        message={
          removeTarget && isSelf(removeTarget)
            ? t('removeDialog.leaveMessage', { name: org?.name ?? t('removeDialog.thisWorkspace') })
            : t('removeDialog.removeMessage', {
                email: removeTarget?.email ?? t('removeDialog.thisMember'),
                name: org?.name ?? t('removeDialog.thisWorkspace'),
              })
        }
        confirmLabel={removeTarget && isSelf(removeTarget) ? t('removeDialog.leave') : t('removeDialog.remove')}
        loading={busyUserId === removeTarget?.userId}
      />

      <ConfirmModal
        opened={!!revokeTarget}
        onClose={() => setRevokeTarget(null)}
        onConfirm={handleRevoke}
        title={t('revokeDialog.title')}
        message={t('revokeDialog.message', { email: revokeTarget?.email ?? t('revokeDialog.thisAddress') })}
        confirmLabel={t('revokeDialog.confirm')}
        loading={busyInviteId === revokeTarget?.id}
      />
    </>
  );
}
