'use client';

import { useCallback, useEffect, useState } from 'react';
import { Alert, Badge, Button, Center, Drawer, Group, Loader, Stack, Table, Text } from '@mantine/core';
import { getApiKeyAudit } from '@/lib/api/api-keys';
import type { ApiKeyAuditEntry, ApiKeyItem } from '@/lib/types/api-key';
import { useT, useFormat } from '@/i18n';

interface ApiKeyAuditDrawerProps {
  item: ApiKeyItem | null;
  opened: boolean;
  onClose: () => void;
}

function statusColor(status: number): string {
  if (status >= 500) return 'red';
  if (status >= 400) return 'yellow';
  return 'green';
}

/** One cursor page at a time, appended as "Load more" is pressed. */
export function ApiKeyAuditDrawer({ item, opened, onClose }: ApiKeyAuditDrawerProps) {
  const t = useT('org.apiKeys');
  const f = useFormat();
  const [entries, setEntries] = useState<ApiKeyAuditEntry[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(false);

  const keyId = item?.id ?? null;

  const load = useCallback(
    async (nextCursor: string | null) => {
      if (!keyId) return;
      setLoading(true);
      setError(false);
      try {
        const page = await getApiKeyAudit(keyId, { limit: 25, cursor: nextCursor });
        setEntries((prev) => (nextCursor ? [...prev, ...page.data] : page.data));
        setCursor(page.nextCursor);
      } catch {
        setError(true);
      } finally {
        setLoading(false);
      }
    },
    [keyId],
  );

  // Opening the drawer for a key starts from its first page; closing does not keep stale rows.
  useEffect(() => {
    if (!opened || !keyId) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset to the first page when a key opens
    setEntries([]);
    setCursor(null);
    void load(null);
  }, [opened, keyId, load]);

  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      title={item ? t('audit.title', { name: item.name }) : t('actions.activity')}
      position="right"
      size="lg"
      data-testid="api-key-audit-drawer"
    >
      <Stack gap="sm">
        {error && (
          <Alert color="red" variant="light">
            {t('audit.loadFailed')}
          </Alert>
        )}
        {entries.length === 0 && loading ? (
          <Center h={100}>
            <Loader size="sm" />
          </Center>
        ) : entries.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t('audit.empty')}
          </Text>
        ) : (
          <Table.ScrollContainer minWidth={480}>
            <Table striped verticalSpacing="xs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('audit.method')}</Table.Th>
                  <Table.Th>{t('audit.route')}</Table.Th>
                  <Table.Th>{t('audit.status')}</Table.Th>
                  <Table.Th>{t('audit.time')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {entries.map((entry) => (
                  <Table.Tr key={entry.requestId}>
                    <Table.Td>
                      <Badge size="xs" fz="xs" variant="light">
                        {entry.method}
                      </Badge>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" fz="xs" ff="monospace" style={{ wordBreak: 'break-all' }}>
                        {entry.route}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Badge size="xs" fz="xs" color={statusColor(entry.statusCode)} variant="light">
                        {entry.statusCode}
                      </Badge>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" fz="xs">
                        {f.date(entry.occurredAt, 'dateTime')}
                      </Text>
                      <Text size="xs" fz="xs" c="dimmed">
                        {t('audit.duration', { ms: entry.durationMs })}
                      </Text>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
        {cursor && (
          <Group justify="center">
            <Button variant="light" loading={loading} onClick={() => void load(cursor)}>
              {t('audit.loadMore')}
            </Button>
          </Group>
        )}
      </Stack>
    </Drawer>
  );
}
