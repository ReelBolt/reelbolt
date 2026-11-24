'use client';

import { ActionIcon, Group, Table, Text, Tooltip } from '@mantine/core';
import { IconBan } from '@tabler/icons-react';
import { RunnerCapabilityChips, RunnerPresenceDot } from './RunnerCapabilityChips';
import { formatRelative, runnerPlatformLabel, shortId } from '@/lib/utils/runners';
import type { RunnerDevice } from '@/lib/types/runner';
import { useT, useFormat } from '@/i18n';

function dash(value?: string | null) {
  return value && value.trim() !== '' ? value : '—';
}

/**
 * Last seen, as a relative time with the exact instant on hover. The exact value is the honest
 * primary fact — the relative form is what a person reads — and it is the only thing that tells a
 * user whether a machine is currently usable.
 */
function LastSeen({ device }: { device: RunnerDevice }) {
  const t = useT('runners.table');
  const tr = useT();
  const f = useFormat();
  if (!device.lastSeenAt) {
    return <Text size="sm" c="dimmed">{t('never')}</Text>;
  }
  return (
    <Tooltip label={new Date(device.lastSeenAt).toLocaleString(f.locale)}>
      <Text size="sm">{formatRelative(device.lastSeenAt, undefined, tr)}</Text>
    </Tooltip>
  );
}

interface RunnerTableProps {
  devices: RunnerDevice[];
  onRevoke: (device: RunnerDevice) => void;
  /**
   * Display names or addresses for the account ids in `pairedByUserId`, when the caller may read
   * the workspace's members. The API returns only an id here, so without this map the cell shows
   * the id's short form with the full id on hover — never a silently wrong name.
   */
  pairerNames?: Record<string, string>;
}

/**
 * The workspace's paired machines (E7-UI).
 *
 * Revoke is offered on every row rather than only to an Owner or Admin: the server also allows the
 * user who PAIRED the device, and this side cannot tell which account that was — the session cookie
 * carries no user id. So the control stays visible and the server answers; a refusal comes back as
 * a sentence (describeRevokeError) rather than a button that was never there.
 */
export function RunnerTable({ devices, onRevoke, pairerNames }: RunnerTableProps) {
  const t = useT('runners.table');
  const tr = useT();
  return (
    <Table.ScrollContainer minWidth={760}>
      <Table striped highlightOnHover>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>{t('machine')}</Table.Th>
            <Table.Th>{t('platform')}</Table.Th>
            <Table.Th visibleFrom="sm">{t('runner')}</Table.Th>
            <Table.Th>{t('lastSeen')}</Table.Th>
            <Table.Th visibleFrom="md">{t('canRun')}</Table.Th>
            <Table.Th visibleFrom="sm">{t('pairedBy')}</Table.Th>
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {devices.map((device) => {
            const name = device.pairedByUserId ? pairerNames?.[device.pairedByUserId] : undefined;
            return (
              <Table.Tr key={device.id}>
                <Table.Td>
                  <Group gap={8} wrap="nowrap">
                    <RunnerPresenceDot lastSeenAt={device.lastSeenAt} />
                    <Text size="sm" fw={500} style={{ wordBreak: 'break-word' }}>
                      {device.name}
                    </Text>
                  </Group>
                </Table.Td>
                <Table.Td>
                  <Text size="sm">{runnerPlatformLabel(device.os, device.arch, tr)}</Text>
                </Table.Td>
                <Table.Td visibleFrom="sm">
                  <Text size="sm">{dash(device.runnerVersion)}</Text>
                </Table.Td>
                <Table.Td>
                  <LastSeen device={device} />
                </Table.Td>
                <Table.Td visibleFrom="md">
                  <RunnerCapabilityChips device={device} />
                </Table.Td>
                <Table.Td visibleFrom="sm">
                  {device.pairedByUserId ? (
                    name ? (
                      <Text size="sm">{name}</Text>
                    ) : (
                      <Tooltip label={device.pairedByUserId}>
                        <Text size="sm" c="dimmed">{shortId(device.pairedByUserId)}</Text>
                      </Tooltip>
                    )
                  ) : (
                    <Text size="sm" c="dimmed">—</Text>
                  )}
                </Table.Td>
                <Table.Td>
                  <Group justify="flex-end">
                    <ActionIcon
                      color="red"
                      variant="subtle"
                      onClick={() => onRevoke(device)}
                      aria-label={t('revokeAria', { name: device.name })}
                    >
                      <IconBan size={16} />
                    </ActionIcon>
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
