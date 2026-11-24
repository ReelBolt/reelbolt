'use client';

import { ActionIcon, Badge, Group, Menu, Table, Text, Tooltip } from '@mantine/core';
import { IconDotsVertical, IconLogout, IconTrash } from '@tabler/icons-react';
import { assignableRoles, canAssignRole, canManageOrg, isLastOwner } from '@/lib/org-permissions';
import type { OrgMember } from '@/lib/api/orgs';
import type { OrgRole } from '@/lib/types/auth';
import { useT, useFormat } from '@/i18n';
import { useRoleLabel } from './useRoleLabel';

interface MembersTableProps {
  members: OrgMember[];
  /** The caller's role in the org the members belong to. */
  callerRole: string | null;
  /** The caller's own address — there is no user id in the session cookie, emails are unique. */
  callerEmail: string | null;
  /** Pending action for one member, so only that row's controls go busy. */
  busyUserId?: string | null;
  onRoleChange: (member: OrgMember, role: OrgRole) => void;
  onRemove: (member: OrgMember) => void;
}

/**
 * The workspace's members (A16-UI).
 *
 * What each row offers follows the API's own rules, mirrored in `lib/org-permissions`:
 * an Admin can neither grant Owner nor change an existing Owner, and nobody can demote or remove
 * the last Owner (`the last owner cannot be removed or demoted`, 409) — that row says so inline
 * instead of offering a control that would fail. A caller who is not an Owner or Admin sees the
 * same list read-only, plus a way to leave the workspace (the API allows self-removal for any
 * role, which is the only member action a Member has).
 */
export function MembersTable({
  members,
  callerRole,
  callerEmail,
  busyUserId,
  onRoleChange,
  onRemove,
}: MembersTableProps) {
  const t = useT('org.membersTable');
  const f = useFormat();
  const roleLabel = useRoleLabel();
  const manager = canManageOrg(callerRole);
  const self = (member: OrgMember) =>
    !!callerEmail && member.email.toLowerCase() === callerEmail.toLowerCase();

  return (
    <Table.ScrollContainer minWidth={640}>
      <Table striped highlightOnHover verticalSpacing="sm">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('member')}</Table.Th>
            <Table.Th>{t('role')}</Table.Th>
            <Table.Th visibleFrom="sm">{t('joined')}</Table.Th>
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {members.map((member) => {
            const isSelf = self(member);
            const lastOwner = isLastOwner(member, members);
            // An Admin may not touch an Owner at all and nobody may touch the last Owner — both are
            // rules the server enforces (403 and 409), so the row offers no control for either.
            const touchable = manager && canAssignRole(callerRole, member.role, member.role) && !lastOwner;
            const changeable = touchable;
            const removable = touchable && !isSelf;
            // Leaving is the one member action available to every role, and to a manager as well;
            // the API refuses it only for the last Owner.
            const leavable = isSelf && !lastOwner;
            const options = assignableRoles(callerRole).filter(
              (role) => role === member.role || canAssignRole(callerRole, member.role, role),
            );
            return (
              <Table.Tr key={member.userId}>
                <Table.Td>
                  <Group gap="xs" wrap="nowrap">
                    <Text size="sm" fw={500}>
                      {member.displayName || member.email}
                    </Text>
                    {isSelf && (
                      <Badge color="gray" variant="light" size="xs">
                        {t('you')}
                      </Badge>
                    )}
                  </Group>
                  {member.displayName && (
                    <Text size="xs" c="dimmed">
                      {member.email}
                    </Text>
                  )}
                </Table.Td>
                <Table.Td>
                  <Group gap="xs" wrap="nowrap">
                    <Badge color={member.role === 'Owner' ? 'violet' : 'gray'} variant="light" size="sm">
                      {roleLabel(member.role)}
                    </Badge>
                    {manager && lastOwner && (
                      <Text size="xs" c="dimmed">
                        {t('lastOwner')}
                      </Text>
                    )}
                  </Group>
                </Table.Td>
                <Table.Td visibleFrom="sm">
                  <Text size="sm" c="dimmed">
                    {f.date(member.joinedAt, 'dateTime')}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Group justify="flex-end" gap="xs" wrap="nowrap">
                    {changeable && (
                      <Menu width={180} position="bottom-end">
                        <Menu.Target>
                          <ActionIcon
                            variant="subtle"
                            aria-label={t('changeRoleAria', { email: member.email })}
                            loading={busyUserId === member.userId}
                          >
                            <IconDotsVertical size={16} />
                          </ActionIcon>
                        </Menu.Target>
                        <Menu.Dropdown>
                          <Menu.Label>{t('changeRole')}</Menu.Label>
                          {options.map((role) => (
                            <Menu.Item
                              key={role}
                              disabled={role === member.role}
                              onClick={() => onRoleChange(member, role)}
                            >
                              {roleLabel(role)}
                            </Menu.Item>
                          ))}
                        </Menu.Dropdown>
                      </Menu>
                    )}
                    {removable && (
                      <Tooltip label={t('remove')}>
                        <ActionIcon
                          color="red"
                          variant="subtle"
                          aria-label={t('removeAria', { email: member.email })}
                          loading={busyUserId === member.userId}
                          onClick={() => onRemove(member)}
                        >
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Tooltip>
                    )}
                    {leavable && (
                      <Tooltip label={t('leave')}>
                        <ActionIcon
                          color="red"
                          variant="subtle"
                          aria-label={t('leaveAria')}
                          loading={busyUserId === member.userId}
                          onClick={() => onRemove(member)}
                        >
                          <IconLogout size={16} />
                        </ActionIcon>
                      </Tooltip>
                    )}
                  </Group>
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
