'use client';

import { ActionIcon, Badge, Group, Table, Text, Tooltip } from '@mantine/core';
import { IconSend, IconX } from '@tabler/icons-react';
import type { OrgInvite } from '@/lib/api/orgs';
import { useT, useFormat } from '@/i18n';
import { useRoleLabel } from './useRoleLabel';

interface InviteListProps {
  invites: OrgInvite[];
  /** The invite being revoked, so only that row's revoke goes busy. */
  busyId?: string | null;
  /** The invite being resent; separate from busyId so the two actions never share a spinner. */
  resendingId?: string | null;
  onRevoke: (invite: OrgInvite) => void;
  /** Sends the invitation again (a fresh link; the old one stops working). */
  onResend?: (invite: OrgInvite) => void;
}

/** The workspace's pending invitations: who was invited, as what, and until when (A16-UI). */
export function InviteList({ invites, busyId, resendingId, onRevoke, onResend }: InviteListProps) {
  const t = useT('org.inviteList');
  const f = useFormat();
  const roleLabel = useRoleLabel();
  return (
    <Table.ScrollContainer minWidth={640}>
      <Table striped highlightOnHover verticalSpacing="sm">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('email')}</Table.Th>
            <Table.Th>{t('role')}</Table.Th>
            <Table.Th>{t('expires')}</Table.Th>
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {invites.map((invite) => (
            <Table.Tr key={invite.id}>
              <Table.Td>
                <Text size="sm">{invite.email}</Text>
                {invite.emailSent === false && (
                  <Text size="xs" c="var(--mantine-color-yellow-text)">
                    {t('notSent')}
                  </Text>
                )}
              </Table.Td>
              <Table.Td>
                <Badge color="gray" variant="light" size="sm">
                  {roleLabel(invite.role)}
                </Badge>
              </Table.Td>
              <Table.Td>
                <Text size="sm" c="dimmed">
                  {f.date(invite.expiresAt, 'dateTime')}
                </Text>
              </Table.Td>
              <Table.Td>
                <Group gap={4} wrap="nowrap" justify="flex-end">
                  {onResend && (
                    <Tooltip label={t('resend')}>
                      <ActionIcon
                        variant="subtle"
                        size={44}
                        aria-label={t('resendAria', { email: invite.email })}
                        loading={resendingId === invite.id}
                        onClick={() => onResend(invite)}
                      >
                        <IconSend size={16} />
                      </ActionIcon>
                    </Tooltip>
                  )}
                  <Tooltip label={t('revoke')}>
                    <ActionIcon
                      color="red"
                      variant="subtle"
                      size={44}
                      aria-label={t('revokeAria', { email: invite.email })}
                      loading={busyId === invite.id}
                      onClick={() => onRevoke(invite)}
                    >
                      <IconX size={16} />
                    </ActionIcon>
                  </Tooltip>
                </Group>
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}
