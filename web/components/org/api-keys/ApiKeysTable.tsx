'use client';

import { useState } from 'react';
import {
  ActionIcon,
  Badge,
  Card,
  Group,
  Progress,
  Stack,
  Table,
  Text,
  Tooltip,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconActivity, IconPencil, IconTrash } from '@tabler/icons-react';
import type { ApiKeyItem, ApiKeyStatus, ApiKeyUsageWindow } from '@/lib/types/api-key';
import { useT, useFormat } from '@/i18n';
import { ScopeBadges } from './CreateApiKeyModal';

const DAY_MS = 24 * 60 * 60 * 1000;
const THIRTY_DAYS_MS = 30 * DAY_MS;

function statusColor(status: ApiKeyStatus): string {
  switch (status) {
    case 'active':
      return 'green';
    case 'revoked':
      return 'red';
    case 'expired':
      return 'gray';
    default:
      return 'yellow';
  }
}

function notUsedFor30Days(item: ApiKeyItem, now: number): boolean {
  if (item.status !== 'active') return false;
  const reference = item.lastUsedAt ?? item.createdAt;
  const at = Date.parse(reference);
  return Number.isFinite(at) && now - at > THIRTY_DAYS_MS;
}

function expiresInDays(item: ApiKeyItem, now: number): number | null {
  if (!item.expiresAt || item.neverExpires) return null;
  const at = Date.parse(item.expiresAt);
  if (!Number.isFinite(at)) return null;
  const ms = at - now;
  if (ms <= 0) return null;
  const days = Math.ceil(ms / DAY_MS);
  return days <= 7 ? days : null;
}

interface ActionsProps {
  item: ApiKeyItem;
  phone: boolean;
  busyId?: string | null;
  onEdit: (item: ApiKeyItem) => void;
  onViewAudit: (item: ApiKeyItem) => void;
  onRevoke: (item: ApiKeyItem) => void;
}

function Actions({ item, phone, busyId, onEdit, onViewAudit, onRevoke }: ActionsProps) {
  const t = useT('org.apiKeys');
  return (
    <Group gap={4} wrap="nowrap" justify={phone ? 'flex-start' : 'flex-end'}>
      <Tooltip label={t('actions.edit')}>
        <ActionIcon variant="subtle" aria-label={t('actions.editAria', { name: item.name })} onClick={() => onEdit(item)}>
          <IconPencil size={16} />
        </ActionIcon>
      </Tooltip>
      <Tooltip label={t('actions.activity')}>
        <ActionIcon
          variant="subtle"
          aria-label={t('actions.activityAria', { name: item.name })}
          onClick={() => onViewAudit(item)}
        >
          <IconActivity size={16} />
        </ActionIcon>
      </Tooltip>
      <Tooltip label={t('actions.revoke')}>
        <ActionIcon
          color="red"
          variant="subtle"
          aria-label={t('actions.revokeAria', { name: item.name })}
          loading={busyId === item.id}
          onClick={() => onRevoke(item)}
        >
          <IconTrash size={16} />
        </ActionIcon>
      </Tooltip>
    </Group>
  );
}

/** Daily limit status: warns on a key with no limit that nobody chose, notes one that was confirmed. */
function DailyCapStatus({ item }: { item: ApiKeyItem }) {
  const t = useT('org.apiKeys');
  const status = item.dailyCap?.status;
  if (status === 'legacy_uncapped') {
    return (
      <Badge size="xs" fz="xs" color="yellow" variant="light" data-testid="api-key-legacy-badge">
        {t('capGuard.legacyBadge')}
      </Badge>
    );
  }
  if (status === 'waived') {
    return (
      <Text size="xs" fz="xs" c="dimmed" data-testid="api-key-waived-badge">
        {t('capGuard.waivedNote')}
      </Text>
    );
  }
  return null;
}

function Caps({ item }: { item: ApiKeyItem }) {
  const t = useT('org.apiKeys');
  const f = useFormat();
  const windows: { key: string; label: string; cap: number | null; usage: ApiKeyUsageWindow | null }[] = [
    { key: 'daily', label: t('usage.daily'), cap: item.caps.dailyCredits, usage: item.usage?.daily ?? null },
    { key: 'weekly', label: t('usage.weekly'), cap: item.caps.weeklyCredits, usage: item.usage?.weekly ?? null },
    { key: 'monthly', label: t('usage.monthly'), cap: item.caps.monthlyCredits, usage: item.usage?.monthly ?? null },
  ];
  const visible = windows.filter((window) => window.cap !== null || window.usage);
  if (visible.length === 0) {
    return (
      <Stack gap={4}>
        <DailyCapStatus item={item} />
        {item.dailyCap?.status ? null : (
          <Text size="xs" fz="xs" c="dimmed">
            {t('values.noCaps')}
          </Text>
        )}
      </Stack>
    );
  }
  return (
    <Stack gap={6}>
      {(item.dailyCap?.status === 'legacy_uncapped' || item.dailyCap?.status === 'waived') && (
        <DailyCapStatus item={item} />
      )}
      {visible.map((window) => {
        const cap = window.cap ?? window.usage?.capCredits ?? null;
        const used = window.usage?.usedCredits ?? 0;
        const percent = cap ? Math.min(100, (used / cap) * 100) : 0;
        return (
          <Stack key={window.key} gap={2}>
            <Text size="xs" fz="xs">
              {window.label} · {cap !== null ? t('usage.ofCap', { used, cap }) : t('usage.used', { used })}
            </Text>
            {cap !== null && <Progress value={percent} size="xs" aria-label={window.label} />}
            {window.usage?.resetsAt && (
              <Text size="xs" fz="xs" c="dimmed" title={f.date(window.usage.resetsAt, 'dateTime')}>
                {t('usage.resetsAt', { date: f.date(window.usage.resetsAt, 'short') })}
              </Text>
            )}
          </Stack>
        );
      })}
    </Stack>
  );
}

function StatusBadge({ status }: { status: ApiKeyStatus }) {
  const t = useT('org.apiKeys');
  const label =
    status === 'active'
      ? t('status.active')
      : status === 'revoked'
        ? t('status.revoked')
        : status === 'expired'
          ? t('status.expired')
          : t('status.inactive');
  return (
    <Badge size="xs" fz="xs" color={statusColor(status)} variant="light">
      {label}
    </Badge>
  );
}

/** The non-status badges: never expires, expires soon, and not used for 30 days. */
function Badges({ item, now }: { item: ApiKeyItem; now: number }) {
  const t = useT('org.apiKeys');
  const days = expiresInDays(item, now);
  return (
    <Group gap={4} wrap="wrap">
      {item.neverExpires && (
        <Badge size="xs" fz="xs" color="yellow" variant="light">
          {t('values.noExpiry')}
        </Badge>
      )}
      {days !== null && (
        <Badge size="xs" fz="xs" color="orange" variant="light">
          {t('values.expiresInDays', { days })}
        </Badge>
      )}
      {notUsedFor30Days(item, now) && (
        <Badge size="xs" fz="xs" color="yellow" variant="outline">
          {t('values.notUsedFor30Days')}
        </Badge>
      )}
    </Group>
  );
}

interface ApiKeysTableProps {
  items: ApiKeyItem[];
  capsEnforced: boolean | null;
  onEdit: (item: ApiKeyItem) => void;
  onViewAudit: (item: ApiKeyItem) => void;
  onRevoke: (item: ApiKeyItem) => void;
  busyId?: string | null;
}

/**
 * The API-key list. On a phone (under 600 px) each key becomes a card; on wider screens it is a
 * table whose columns follow the ticket: name/prefix, scopes, projects, caps with usage, expiry,
 * last use, author and status, plus the three row actions.
 */
export function ApiKeysTable({ items, capsEnforced, onEdit, onViewAudit, onRevoke, busyId }: ApiKeysTableProps) {
  const t = useT('org.apiKeys');
  const f = useFormat();
  const phone = useMediaQuery('(max-width: 599px)');
  const [now] = useState(() => Date.now());

  const keyLabel = (item: ApiKeyItem) => `${item.keyPrefix}…${item.lastFour}`;
  const projectsLabel = (item: ApiKeyItem) => {
    const names = (item.projects ?? []).map((project) => project.name).filter((name): name is string => !!name);
    const ids = item.projectIds ?? [];
    if (ids.length === 0) return t('values.allProjects');
    return names.length > 0 ? names.join(', ') : ids.join(', ');
  };
  const createdBy = (item: ApiKeyItem) => item.createdBy.name || t('values.unknownCreator');
  const lastUsed = (item: ApiKeyItem) => (item.lastUsedAt ? f.date(item.lastUsedAt, 'dateTime') : t('values.never'));

  if (phone) {
    return (
      <Stack gap="sm" data-testid="api-keys-cards">
        {items.map((item) => (
          <Card key={item.id} withBorder padding="md">
            <Stack gap="xs">
              <Group justify="space-between" align="flex-start" wrap="nowrap">
                <Stack gap={2} style={{ minWidth: 0 }}>
                  <Text size="sm" fw={600} style={{ wordBreak: 'break-word' }}>
                    {item.name}
                  </Text>
                  <Text size="xs" fz="xs" ff="monospace" c="dimmed">
                    {keyLabel(item)}
                  </Text>
                </Stack>
                <Stack gap={4} align="flex-end">
                  <StatusBadge status={item.status} />
                  <Badges item={item} now={now} />
                </Stack>
              </Group>
              <ScopeBadges scopes={item.scopes} />
              <Text size="xs" fz="xs" c="dimmed">
                {t('table.projects')}: {projectsLabel(item)}
              </Text>
              <Caps item={item} />
              <Text size="xs" fz="xs" c="dimmed">
                {t('table.expiry')}: {item.expiresAt ? f.date(item.expiresAt, 'short') : t('values.noExpiry')}
              </Text>
              <Text size="xs" fz="xs" c="dimmed">
                {t('table.lastUsed')}: {lastUsed(item)}
              </Text>
              <Text size="xs" fz="xs" c="dimmed">
                {t('table.createdBy')}: {createdBy(item)}
              </Text>
              <Actions
                item={item}
                phone={phone}
                busyId={busyId}
                onEdit={onEdit}
                onViewAudit={onViewAudit}
                onRevoke={onRevoke}
              />
            </Stack>
          </Card>
        ))}
        {capsEnforced === false && (
          <Text size="xs" fz="xs" c="dimmed">
            {t('values.capsNotEnforced')}
          </Text>
        )}
      </Stack>
    );
  }

  return (
    <Stack gap="xs">
      <Table.ScrollContainer minWidth={960}>
        <Table striped highlightOnHover verticalSpacing="sm">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('table.name')}</Table.Th>
              <Table.Th>{t('table.scopes')}</Table.Th>
              <Table.Th>{t('table.projects')}</Table.Th>
              <Table.Th>{t('table.caps')}</Table.Th>
              <Table.Th>{t('table.expiry')}</Table.Th>
              <Table.Th>{t('table.lastUsed')}</Table.Th>
              <Table.Th>{t('table.createdBy')}</Table.Th>
              <Table.Th>{t('table.status')}</Table.Th>
              <Table.Th>{t('table.actions')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {items.map((item) => (
              <Table.Tr key={item.id}>
                <Table.Td>
                  <Stack gap={2}>
                    <Text size="sm" fw={500}>
                      {item.name}
                    </Text>
                    <Text size="xs" fz="xs" ff="monospace" c="dimmed">
                      {keyLabel(item)}
                    </Text>
                    <Badges item={item} now={now} />
                  </Stack>
                </Table.Td>
                <Table.Td>
                  <ScopeBadges scopes={item.scopes} />
                </Table.Td>
                <Table.Td>
                  <Text size="sm">{projectsLabel(item)}</Text>
                </Table.Td>
                <Table.Td>
                  <Caps item={item} />
                </Table.Td>
                <Table.Td>
                  <Text size="sm">
                    {item.expiresAt ? f.date(item.expiresAt, 'short') : t('values.noExpiry')}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Text size="sm">{lastUsed(item)}</Text>
                </Table.Td>
                <Table.Td>
                  <Text size="sm">{createdBy(item)}</Text>
                </Table.Td>
                <Table.Td>
                  <StatusBadge status={item.status} />
                </Table.Td>
                <Table.Td>
                  <Actions
                    item={item}
                    phone={phone}
                    busyId={busyId}
                    onEdit={onEdit}
                    onViewAudit={onViewAudit}
                    onRevoke={onRevoke}
                  />
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
      {capsEnforced === false && (
        <Text size="xs" fz="xs" c="dimmed">
          {t('values.capsNotEnforced')}
        </Text>
      )}
    </Stack>
  );
}
