'use client';

import { useState } from 'react';
import { ActionIcon, Alert, Badge, Button, Card, Center, Group, Loader, Modal, Stack, Table, Text, Tooltip, VisuallyHidden } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconKey, IconListDetails, IconPencil, IconPlus, IconSend, IconTrash } from '@tabler/icons-react';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { EmptyState } from '@/components/shared/EmptyState';
import { ApiError } from '@/lib/api/client';
import { deleteWebhook, rotateWebhookSecret, sendWebhookTest } from '@/lib/api/webhooks';
import { useWebhooks } from '@/lib/hooks/use-webhooks';
import { useProjects } from '@/lib/hooks/use-projects';
import type { WebhookEndpoint } from '@/lib/types/webhook';
import { WebhookFormModal } from './WebhookFormModal';
import { WebhookDeliveriesDrawer } from './WebhookDeliveriesDrawer';
import { SecretOnceStep } from './SecretOnceStep';
import { useLocale, useT, useFormat } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { docsPageHref } from '@/lib/onboarding/guide-links';

function hostAndPath(url: string): string {
  try {
    const u = new URL(url);
    return `${u.host}${u.pathname === '/' ? '' : u.pathname}`;
  } catch {
    return url;
  }
}

/** The Webhooks tab of the API keys page: list, create/edit, test, rotate, delete, deliveries. */
export function WebhooksPanel({ orgId }: { orgId: string | null }) {
  const t = useT('org.apiKeys');
  const tAll = useT();
  const f = useFormat();
  const locale = useLocale();
  const phone = useMediaQuery('(max-width: 599px)');
  const { data, error, mutate } = useWebhooks(orgId);
  const { data: projects } = useProjects();
  const [formOpen, setFormOpen] = useState(false);
  // Bumped on every open so the form remounts and is prefilled from the endpoint each time.
  const [formOpenCount, setFormOpenCount] = useState(0);
  const [editing, setEditing] = useState<WebhookEndpoint | null>(null);
  const [deliveriesFor, setDeliveriesFor] = useState<WebhookEndpoint | null>(null);
  const [rotateTarget, setRotateTarget] = useState<WebhookEndpoint | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<WebhookEndpoint | null>(null);
  const [busy, setBusy] = useState(false);
  const [rotatedSecret, setRotatedSecret] = useState<string | null>(null);

  const items = data ?? [];
  const projectName = (id: string) => projects?.find((p) => p.id === id)?.name ?? id;

  function openCreate() {
    setEditing(null);
    setFormOpenCount((n) => n + 1);
    setFormOpen(true);
  }

  function failed(err: unknown) {
    notifications.show({
      message: describeError(tAll, err, t('webhooks.notify.actionFailed')),
      color: 'red',
    });
  }

  async function sendTest(endpoint: WebhookEndpoint) {
    try {
      await sendWebhookTest(endpoint.id);
      notifications.show({ message: t('webhooks.notify.testQueued'), color: 'green' });
    } catch (err: unknown) {
      failed(err);
    }
  }

  async function rotate() {
    const target = rotateTarget;
    if (!target) return;
    setBusy(true);
    try {
      const result = await rotateWebhookSecret(target.id);
      setRotatedSecret(result.secret);
    } catch (err: unknown) {
      failed(err);
    } finally {
      setBusy(false);
      setRotateTarget(null);
    }
  }

  async function remove() {
    const target = deleteTarget;
    if (!target) return;
    setBusy(true);
    try {
      await deleteWebhook(target.id);
      await mutate();
      notifications.show({ message: t('webhooks.notify.deleted'), color: 'green' });
    } catch (err: unknown) {
      failed(err);
    } finally {
      setBusy(false);
      setDeleteTarget(null);
    }
  }

  function projectsLabel(e: WebhookEndpoint): string {
    return e.projectIds && e.projectIds.length > 0 ? e.projectIds.map(projectName).join(', ') : t('webhooks.table.projectsAll');
  }

  function statusBadge(e: WebhookEndpoint) {
    if (!e.enabled) {
      const reason = e.disabledReason?.trim().toLowerCase() ?? '';
      const label =
        reason === 'api_key_revoked'
          ? t('webhooks.status.disabledApiKeyRevoked')
          : reason.startsWith('no successful delivery for')
            ? t('webhooks.status.disabledAuto')
            : t('webhooks.status.disabled');
      return (
        <Badge color="red" variant="light" title={label}>
          {label}
        </Badge>
      );
    }
    if (e.failingSince) {
      return (
        <Badge color="yellow" variant="light">
          {t('webhooks.status.failingSince', { date: f.date(e.failingSince, 'short') })}
        </Badge>
      );
    }
    return (
      <Badge color="green" variant="light">
        {t('webhooks.status.active')}
      </Badge>
    );
  }

  function actions(e: WebhookEndpoint) {
    return (
      <Group gap={4} wrap="nowrap">
        <Tooltip label={t('webhooks.actions.edit')}>
          <ActionIcon variant="subtle" aria-label={t('webhooks.actions.editAria', { url: e.url })} onClick={() => { setEditing(e); setFormOpenCount((n) => n + 1); setFormOpen(true); }}>
            <IconPencil size={16} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label={t('webhooks.actions.test')}>
          <ActionIcon variant="subtle" aria-label={t('webhooks.actions.testAria', { url: e.url })} onClick={() => void sendTest(e)}>
            <IconSend size={16} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label={t('webhooks.actions.rotate')}>
          <ActionIcon variant="subtle" aria-label={t('webhooks.actions.rotateAria', { url: e.url })} onClick={() => setRotateTarget(e)}>
            <IconKey size={16} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label={t('webhooks.actions.deliveries')}>
          <ActionIcon variant="subtle" aria-label={t('webhooks.actions.deliveriesAria', { url: e.url })} onClick={() => setDeliveriesFor(e)}>
            <IconListDetails size={16} />
          </ActionIcon>
        </Tooltip>
        <Tooltip label={t('webhooks.actions.delete')}>
          <ActionIcon variant="subtle" color="red" aria-label={t('webhooks.actions.deleteAria', { url: e.url })} onClick={() => setDeleteTarget(e)}>
            <IconTrash size={16} />
          </ActionIcon>
        </Tooltip>
      </Group>
    );
  }

  const eventBadges = (e: WebhookEndpoint) => (
    <Group gap={4}>
      {e.events.map((ev) => (
        <Badge key={ev} size="sm" fz="xs" variant="outline" tt="none">
          {ev}
        </Badge>
      ))}
    </Group>
  );

  return (
    <Stack gap="lg">
      <Group justify="space-between" align="flex-start" wrap="wrap">
        <Text size="sm" c="dimmed" maw={640}>
          {t('webhooks.description')} <a href={docsPageHref('api/webhooks', locale)}>{t('webhooks.docsLink')}</a>
        </Text>
        <Button leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={openCreate}>
          {t('webhooks.newEndpoint')}
        </Button>
      </Group>

      <Card withBorder padding="lg">
        {error ? (
          <Alert color={error instanceof ApiError && error.status === 404 ? 'gray' : 'red'} variant="light">
            {error instanceof ApiError && error.status === 404 ? t('notAvailableTitle') : t('loadFailedTitle')}
          </Alert>
        ) : data === undefined ? (
          <Center h={140}>
            <Loader size="sm" />
          </Center>
        ) : items.length === 0 ? (
          <EmptyState title={t('webhooks.emptyTitle')} description={t('webhooks.emptyDescription')}>
            <Button variant="light" leftSection={<IconPlus size={16} aria-hidden="true" />} onClick={openCreate}>
              {t('webhooks.newEndpoint')}
            </Button>
          </EmptyState>
        ) : phone ? (
          <Stack gap="sm" data-testid="webhook-cards">
            {items.map((e) => (
              <Card key={e.id} withBorder padding="sm">
                <Stack gap={6}>
                  <Text fw={600} ff="monospace" fz="sm" style={{ wordBreak: 'break-all' }}>
                    {hostAndPath(e.url)}
                  </Text>
                  {statusBadge(e)}
                  {eventBadges(e)}
                  <Text size="xs" c="dimmed">
                    {t('webhooks.table.projects')}: {projectsLabel(e)}
                  </Text>
                  {actions(e)}
                </Stack>
              </Card>
            ))}
          </Stack>
        ) : (
          <Table.ScrollContainer minWidth={640}>
            <Table verticalSpacing="sm">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('webhooks.table.url')}</Table.Th>
                  <Table.Th>{t('webhooks.table.events')}</Table.Th>
                  <Table.Th>{t('webhooks.table.projects')}</Table.Th>
                  <Table.Th>{t('webhooks.table.status')}</Table.Th>
                  <Table.Th><VisuallyHidden>{t('webhooks.table.actions')}</VisuallyHidden></Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {items.map((e) => (
                  <Table.Tr key={e.id}>
                    <Table.Td>
                      <Text ff="monospace" fz="sm" title={e.url} style={{ wordBreak: 'break-all' }}>
                        {hostAndPath(e.url)}
                      </Text>
                      {e.description && (
                        <Text size="xs" c="dimmed">
                          {e.description}
                        </Text>
                      )}
                    </Table.Td>
                    <Table.Td>{eventBadges(e)}</Table.Td>
                    <Table.Td>
                      <Text size="sm">{projectsLabel(e)}</Text>
                    </Table.Td>
                    <Table.Td>{statusBadge(e)}</Table.Td>
                    <Table.Td>{actions(e)}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}
      </Card>

      <WebhookFormModal
        key={`${editing?.id ?? 'new'}-${formOpenCount}`}
        opened={formOpen}
        endpoint={editing}
        onClose={() => setFormOpen(false)}
        onSaved={async () => {
          await mutate();
        }}
      />
      <WebhookDeliveriesDrawer endpoint={deliveriesFor} opened={!!deliveriesFor} onClose={() => setDeliveriesFor(null)} />
      <ConfirmModal
        opened={!!rotateTarget}
        onClose={() => setRotateTarget(null)}
        onConfirm={() => void rotate()}
        title={t('webhooks.rotate.title')}
        message={t('webhooks.rotate.message', { url: rotateTarget ? hostAndPath(rotateTarget.url) : '' })}
        confirmLabel={t('webhooks.rotate.confirm')}
        loading={busy}
      />
      <ConfirmModal
        opened={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        onConfirm={() => void remove()}
        title={t('webhooks.delete.title')}
        message={t('webhooks.delete.message', { url: deleteTarget ? hostAndPath(deleteTarget.url) : '' })}
        confirmLabel={t('webhooks.delete.confirm')}
        loading={busy}
      />
      <Modal opened={rotatedSecret !== null} onClose={() => setRotatedSecret(null)} title={t('webhooks.secret.rotatedTitle')} centered size="lg" closeOnClickOutside={false} closeOnEscape={false} withCloseButton={false}>
        <SecretOnceStep body={t('webhooks.secret.rotatedBody')} secret={rotatedSecret ?? ''} onDone={() => setRotatedSecret(null)} />
      </Modal>
    </Stack>
  );
}
