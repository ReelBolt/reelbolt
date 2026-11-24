'use client';

import { useCallback, useEffect, useState } from 'react';
import { Alert, Badge, Button, Center, Drawer, Group, Loader, Stack, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { listWebhookDeliveries, redeliverWebhook } from '@/lib/api/webhooks';
import type { WebhookDelivery, WebhookDeliveryStatus, WebhookEndpoint } from '@/lib/types/webhook';
import { useT, useFormat } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

interface WebhookDeliveriesDrawerProps {
  endpoint: WebhookEndpoint | null;
  opened: boolean;
  onClose: () => void;
}

const STATUS_COLOR: Record<WebhookDeliveryStatus, string> = {
  pending: 'gray',
  delivered: 'green',
  failed: 'yellow',
  abandoned: 'red',
};

/** One cursor page at a time, with a Redeliver action per delivery. */
export function WebhookDeliveriesDrawer({ endpoint, opened, onClose }: WebhookDeliveriesDrawerProps) {
  const t = useT('org.apiKeys');
  const tAll = useT();
  const f = useFormat();
  const [rows, setRows] = useState<WebhookDelivery[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(false);
  const endpointId = endpoint?.id ?? null;

  const load = useCallback(
    async (next: string | null) => {
      if (!endpointId) return;
      setLoading(true);
      setError(false);
      try {
        const page = await listWebhookDeliveries(endpointId, { limit: 25, cursor: next });
        setRows((prev) => (next ? [...prev, ...page.data] : page.data));
        setCursor(page.nextCursor);
      } catch {
        setError(true);
      } finally {
        setLoading(false);
      }
    },
    [endpointId],
  );

  useEffect(() => {
    if (!opened || !endpointId) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- reset to the first page when an endpoint opens
    setRows([]);
    setCursor(null);
    void load(null);
  }, [opened, endpointId, load]);

  function statusLabel(s: WebhookDeliveryStatus) {
    switch (s) {
      case 'pending': return t('webhooks.deliveries.pending');
      case 'delivered': return t('webhooks.deliveries.delivered');
      case 'failed': return t('webhooks.deliveries.failed');
      case 'abandoned': return t('webhooks.deliveries.abandoned');
    }
  }

  async function redeliver(row: WebhookDelivery) {
    if (!endpointId) return;
    try {
      await redeliverWebhook(endpointId, row.id);
      notifications.show({ message: t('webhooks.notify.redelivered'), color: 'green' });
      await load(null);
    } catch (err: unknown) {
      notifications.show({
        message: describeError(tAll, err, t('webhooks.notify.actionFailed')),
        color: 'red',
      });
    }
  }

  return (
    <Drawer
      opened={opened}
      onClose={onClose}
      title={endpoint ? t('webhooks.deliveries.title', { url: endpoint.url }) : t('webhooks.actions.deliveries')}
      position="right"
      size="xl"
      data-testid="webhook-deliveries-drawer"
    >
      <Stack gap="sm">
        {error && (
          <Alert color="red" variant="light">
            {t('webhooks.deliveries.loadFailed')}
          </Alert>
        )}
        {rows.length === 0 && loading ? (
          <Center h={100}>
            <Loader size="sm" />
          </Center>
        ) : rows.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t('webhooks.deliveries.empty')}
          </Text>
        ) : (
          <Table.ScrollContainer minWidth={560}>
            <Table striped verticalSpacing="xs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('webhooks.deliveries.event')}</Table.Th>
                  <Table.Th>{t('webhooks.deliveries.status')}</Table.Th>
                  <Table.Th>{t('webhooks.deliveries.attempts')}</Table.Th>
                  <Table.Th>{t('webhooks.deliveries.nextAttempt')}</Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {rows.map((row) => (
                  <Table.Tr key={row.id}>
                    <Table.Td>
                      <Text size="xs" ff="monospace">
                        {row.eventType}
                      </Text>
                      <Text size="xs" c="dimmed">
                        {f.date(row.createdAt, 'dateTime')}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4} wrap="nowrap">
                        <Badge size="xs" fz="xs" color={STATUS_COLOR[row.status]} variant="light">
                          {statusLabel(row.status)}
                        </Badge>
                        {row.lastStatusCode !== null && (
                          <Text size="xs" c="dimmed">
                            {row.lastStatusCode}
                          </Text>
                        )}
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs">{row.attempts}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs">{row.nextAttemptAt ? f.date(row.nextAttemptAt, 'dateTime') : '-'}</Text>
                    </Table.Td>
                    <Table.Td>
                      <Button size="compact-xs" variant="light" onClick={() => void redeliver(row)}>
                        {t('webhooks.deliveries.redeliver')}
                      </Button>
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
              {t('webhooks.deliveries.loadMore')}
            </Button>
          </Group>
        )}
      </Stack>
    </Drawer>
  );
}
