'use client';

import { useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Center,
  Group,
  Loader,
  Select,
  Stack,
  Table,
  Text,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconRefresh, IconShieldLock } from '@tabler/icons-react';
import useSWR from 'swr';
import { PageHeader } from '@/components/shared/PageHeader';
import { EmptyState } from '@/components/shared/EmptyState';
import {
  adminScansPath,
  csamAdminMessage,
  csamRescanMessage,
  listAdminScans,
  rescanCsamFile,
  sameCsamStatus,
  type CsamScanItem,
  type CsamScanListResponse,
  type CsamStatusFilter,
} from '@/lib/api/csam';
import { useAuth } from '@/lib/hooks/use-auth';

const BREADCRUMBS = [{ label: 'Admin', href: '/admin' }, { label: 'Content safety' }];

const FILTER_OPTIONS: { value: CsamStatusFilter; label: string }[] = [
  { value: 'all', label: 'All scans' },
  { value: 'match', label: 'Matches' },
  { value: 'not-scanned', label: 'Not scanned' },
  { value: 'disabled', label: 'Disabled' },
  { value: 'clean', label: 'Clean' },
];

const STATUS_LABELS: Record<string, string> = {
  match: 'Match',
  clean: 'Clean',
  notscanned: 'Not scanned',
  disabled: 'Disabled',
  pending: 'Pending',
  scanning: 'Scanning',
  quarantined: 'Quarantined',
};

function statusLabel(status: string): string {
  const key = Object.keys(STATUS_LABELS).find((k) => sameCsamStatus(status, k));
  return key ? STATUS_LABELS[key] : status;
}

function statusColor(status: string): string {
  if (sameCsamStatus(status, 'match') || sameCsamStatus(status, 'quarantined')) return 'red';
  if (sameCsamStatus(status, 'clean')) return 'green';
  if (sameCsamStatus(status, 'pending') || sameCsamStatus(status, 'scanning')) return 'yellow';
  return 'gray';
}

function plain(value: string | null): string {
  return value && value.trim() ? value : '—';
}

function createdLabel(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

/**
 * Platform-admin view of CSAM scan results (csam-hash-matching). It lists the audit rows and offers
 * a re-scan per file; it deliberately shows no content, storage key or URL. Review and reporting
 * are manual, and the runbook is `docs/csam.md` in the repository.
 */
export default function AdminContentSafetyPage() {
  const { isPlatformAdmin } = useAuth();
  const [filter, setFilter] = useState<CsamStatusFilter>('all');
  const [busyId, setBusyId] = useState<string | null>(null);

  const scans = useSWR<CsamScanListResponse>(
    isPlatformAdmin ? adminScansPath(filter) : null,
    () => listAdminScans({ status: filter }),
  );

  async function rescan(item: CsamScanItem) {
    if (!item.projectFileId) {
      notifications.show({
        title: 'Re-scan unavailable',
        message: 'The file no longer exists; only its audit record remains.',
        color: 'yellow',
      });
      return;
    }
    setBusyId(item.projectFileId);
    try {
      const result = await rescanCsamFile(item.projectFileId);
      const color = result.quarantined || sameCsamStatus(result.status, 'match')
        ? 'red'
        : sameCsamStatus(result.status, 'clean')
          ? 'green'
          : 'yellow';
      notifications.show({ title: 'Re-scan finished', message: csamRescanMessage(result), color });
      await scans.mutate();
    } catch (error) {
      notifications.show({ title: 'Re-scan failed', message: csamAdminMessage(error), color: 'red' });
    } finally {
      setBusyId(null);
    }
  }

  if (!isPlatformAdmin) {
    return (
      <>
        <PageHeader title="Content safety" breadcrumbs={BREADCRUMBS} />
        <Alert color="red" variant="light" title="Platform administrators only">
          CSAM scan results are visible to platform administrators only. The API answers 403 for
          anyone else.
        </Alert>
      </>
    );
  }

  const disabled = scans.data !== undefined && !scans.data.enabled;
  const items = scans.data?.items ?? [];

  return (
    <>
      <PageHeader title="Content safety" breadcrumbs={BREADCRUMBS}>
        <Select
          aria-label="Filter by scan status"
          value={filter}
          onChange={(value) => setFilter((value as CsamStatusFilter | null) ?? 'all')}
          data={FILTER_OPTIONS}
          allowDeselect={false}
          w={200}
          data-testid="csam-status-filter"
        />
      </PageHeader>

      <Stack gap="md">
        <Text size="sm" c="dimmed">
          Files that match cannot be used: a quarantined file is never served, and the only
          user-facing detail is “This file cannot be used.” Review and reporting are manual and
          follow docs/csam.md. This page shows no file content, storage keys or URLs.
        </Text>

        {scans.error ? (
          <Alert color="red" variant="light" title="Could not load scan results">
            {csamAdminMessage(scans.error)}
          </Alert>
        ) : disabled ? (
          <Alert color="blue" variant="light" title="Scanning is not enabled on this deployment">
            <Group gap="xs">
              <IconShieldLock size={16} aria-hidden="true" />
              <Text size="sm">
                Uploads are accepted without a scan and no results are recorded here. Enabling the
                control is a deployment configuration change.
              </Text>
            </Group>
          </Alert>
        ) : !scans.data ? (
          <Center h={200}>
            <Loader />
          </Center>
        ) : items.length === 0 ? (
          <EmptyState
            title="No scan results"
            description="Nothing has been scanned with this status yet. Change the filter or re-scan a file from another view."
          />
        ) : (
          <Table.ScrollContainer minWidth={900}>
            <Table striped highlightOnHover verticalSpacing="sm" data-testid="csam-scans-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>File</Table.Th>
                  <Table.Th>Status</Table.Th>
                  <Table.Th>Classification</Table.Th>
                  <Table.Th>Match type</Table.Th>
                  <Table.Th>Quarantined</Table.Th>
                  <Table.Th>Created</Table.Th>
                  <Table.Th aria-label="Actions" />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {items.map((item) => (
                  <Table.Tr key={item.id}>
                    <Table.Td>
                      <Text size="sm" fw={500} style={{ wordBreak: 'break-word' }}>
                        {plain(item.fileName)}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Badge color={statusColor(item.status)} variant="light">
                        {statusLabel(item.status)}
                      </Badge>
                    </Table.Td>
                    <Table.Td>{plain(item.classification)}</Table.Td>
                    <Table.Td>{plain(item.matchType)}</Table.Td>
                    <Table.Td>
                      <Badge color={item.quarantined ? 'red' : 'gray'} variant="light">
                        {item.quarantined ? 'Yes' : 'No'}
                      </Badge>
                    </Table.Td>
                    <Table.Td>{createdLabel(item.createdAt)}</Table.Td>
                    <Table.Td>
                      <Button
                        size="xs"
                        variant="light"
                        leftSection={<IconRefresh size={14} />}
                        loading={busyId === item.projectFileId}
                        disabled={!item.projectFileId || (busyId !== null && busyId !== item.projectFileId)}
                        onClick={() => { void rescan(item); }}
                      >
                        Re-scan
                      </Button>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Stack>
    </>
  );
}
